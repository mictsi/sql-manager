using System.Drawing;
using System.Collections.ObjectModel;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SqlManager;

internal sealed class TerminalGuiRunner
{
    private const string ManualEntryChoice = "<Enter manually>";

    private readonly SqlManagerService _service;
    private readonly PasswordGenerator _passwordGenerator = new();
    private readonly ProtectedSessionSecret _configEncryptionPassword = new();
    private readonly Dictionary<string, int> _gameHighScores = new(StringComparer.OrdinalIgnoreCase);
    private IApplication? _app;
    private CancellationToken _cancellationToken;
    private string _configPath = string.Empty;
    private SqlManagerConfig _config = new();
    private string? _activeServer;
    private Label? _activeServerLabel;
    private Button? _initialMainButton;
    private readonly List<IReadOnlyList<Button>> _mainMenuColumns = [];
    private readonly Dictionary<Button, (int ColumnIndex, int RowIndex)> _mainMenuButtonPositions = [];
    private bool _exitConfirmationApproved;
    private int _exitCode;

    public TerminalGuiRunner(SqlManagerService service)
    {
        _service = service;
    }

    public int Run(string configPath, CancellationToken cancellationToken)
    {
        _configPath = configPath;
        _cancellationToken = cancellationToken;
        _exitCode = 0;

        using IApplication app = Application.Create();
        app.Init();
        _app = app;

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                app.Invoke(() => app.RequestStop());
            }
            catch
            {
                // Cancellation should not throw on the UI thread.
            }
        });

        try
        {
            var summary = WaitForTaskCompletion(
                _service.LoadConfigSummaryAsync(configPath, GetConfigEncryptionPassword(), cancellationToken),
                "Loading Config",
                "Loading configuration...");
            if (!summary.Succeeded || summary.Value is null)
            {
                MessageBox.ErrorQuery(app, "SQL Manager", summary.Message, "OK");
                return summary.ExitCode;
            }

            _config = summary.Value;
            _activeServer = ResolveActiveServerName(_config);

            if (_config.EncryptPasswords && !PromptForStartupEncryptionPassword())
            {
                return _exitCode == 0 ? 1 : _exitCode;
            }

            var window = new Window
            {
                Title = $"SQL Manager v{AppVersion.DisplayVersion}",
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                TabStop = TabBehavior.TabGroup
            };
            window.IsRunningChanging += OnMainWindowIsRunningChanging;

            BuildMainWindow(window);
            RefreshViews();
            _initialMainButton?.SetFocus();
            app.Run(window);
            return _exitCode;
        }
        finally
        {
            _configEncryptionPassword.Dispose();
        }
    }

    private void BuildMainWindow(Window window)
    {
        var menuBar = BuildMenuBar();
        _activeServerLabel = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Text = string.Empty
        };

        var instructionsLabel = new Label
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Text = "Use Up/Down within a column, Left/Right across columns, Tab to cycle, and Enter to run the selected action."
        };

        _initialMainButton = null;
        _mainMenuColumns.Clear();
        _mainMenuButtonPositions.Clear();

        var serverActions = new (string Title, Action Action)[]
        {
            ("Select Active Server", (Action)ShowSelectServerDialog),
            ("Add Server", (Action)ShowAddServerDialog),
            ("Edit Server", (Action)ShowEditServerDialog),
            ("Sync Server Configuration", (Action)ShowSyncServerDialog),
            ("Show Databases", (Action)ShowDatabasesDialog),
            ("Create Database", (Action)ShowCreateDatabaseDialog),
            ("Remove Database", (Action)ShowRemoveDatabaseDialog)
        };

        var userActions = new (string Title, Action Action)[]
        {
            ("Create User", (Action)ShowCreateUserDialog),
            ("Manage Roles", (Action)ShowManageRolesDialog),
            ("Show Users", (Action)ShowUsersDialog),
            ("Remove User", (Action)ShowRemoveUserDialog),
            ("Update Password", (Action)ShowUpdatePasswordDialog)
        };

        _mainMenuColumns.Add(AddMenuColumn(window, 0, "Server Management", 1, 4, Dim.Percent(50) - 2, serverActions));
        _mainMenuColumns.Add(AddMenuColumn(window, 1, "User Management", Pos.Percent(50) + 1, 4, Dim.Fill(2), userActions));

        window.Add(menuBar, _activeServerLabel, instructionsLabel);
    }

    private MenuBar BuildMenuBar()
        => new([
            new MenuBarItem("_File", new PopoverMenu([
                new MenuItem("_Save", "Ctrl+S", SaveCurrentConfig, Key.S.WithCtrl),
                new MenuItem("E_xit", "Ctrl+Q", TryRequestApplicationExit, Key.Q.WithCtrl)
            ])),
            new MenuBarItem("_Configuration", new PopoverMenu([
                new MenuItem("_View Config", string.Empty, ShowConfigDialog, default),
                new MenuItem("_Initialize Config", string.Empty, ShowInitializeConfigDialog, default),
                new MenuItem("_Toggle Encrypt Passwords", string.Empty, ShowPasswordEncryptionDialog, default),
                new MenuItem("_Trash Bin", string.Empty, ShowTrashBinDialog, default),
                new MenuItem("_Refresh", string.Empty, ReloadAndRefresh, default)
            ])),
            new MenuBarItem("_Games", new PopoverMenu([
                new MenuItem("_Snake", string.Empty, ShowSnakeGameDialog, default),
                new MenuItem("_Pong", string.Empty, ShowPongGameDialog, default),
                new MenuItem("_Tetris", string.Empty, ShowTetrisGameDialog, default)
            ])),
            new MenuBarItem("_Help", new PopoverMenu([
                new MenuItem("_Version", string.Empty, ShowVersionDialog, default),
                new MenuItem("_Help Overview", string.Empty, ShowHelpOverviewDialog, default),
                new MenuItem("_Command Reference", string.Empty, ShowCommandReferenceDialog, default)
            ])),
            new MenuBarItem("_About", new PopoverMenu([
                new MenuItem("_About SQL Manager", string.Empty, ShowAboutDialog, default)
            ]))
        ])
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1
        };

    private List<Button> AddMenuColumn(Window window, int columnIndex, string title, Pos x, int topY, Dim width, IReadOnlyList<(string Title, Action Action)> actions)
    {
        var headingText = title.ToUpperInvariant();
        var headingUnderline = new string('=', Math.Max(headingText.Length + 6, 20));
        var buttons = new List<Button>(actions.Count);

        window.Add(new Label
        {
            X = x,
            Y = topY,
            Width = width,
            TextAlignment = Alignment.Center,
            Text = headingText
        });

        window.Add(new Label
        {
            X = x,
            Y = topY + 1,
            Width = width,
            TextAlignment = Alignment.Center,
            Text = headingUnderline
        });

        View? previous = null;
        var rowIndex = 0;
        foreach (var (actionTitle, action) in actions)
        {
            var button = new Button
            {
                X = x,
                Y = previous is null ? topY + 3 : Pos.Bottom(previous) + 1,
                Width = width,
                Text = actionTitle,
                CanFocus = true,
                TabStop = TabBehavior.TabStop
            };
            button.Accepting += (_, args) =>
            {
                args.Handled = true;
                RunGuardedUiAction(actionTitle, action);
            };
            button.KeyDown += (_, key) => HandleMainMenuKeyDown(button, key);
            if (_initialMainButton is null)
            {
                _initialMainButton = button;
            }

            window.Add(button);
            previous = button;
            buttons.Add(button);
            _mainMenuButtonPositions[button] = (columnIndex, rowIndex++);
        }

        return buttons;
    }

    private void HandleMainMenuKeyDown(Button button, Key key)
    {
        if (!_mainMenuButtonPositions.TryGetValue(button, out var position))
        {
            return;
        }

        if (key.KeyCode == KeyCode.CursorLeft)
        {
            MoveMainMenuFocus(position.ColumnIndex - 1, position.RowIndex);
            key.Handled = true;
            return;
        }

        if (key.KeyCode == KeyCode.CursorRight)
        {
            MoveMainMenuFocus(position.ColumnIndex + 1, position.RowIndex);
            key.Handled = true;
        }
    }

    private void MoveMainMenuFocus(int targetColumnIndex, int preferredRowIndex)
    {
        if (targetColumnIndex < 0 || targetColumnIndex >= _mainMenuColumns.Count)
        {
            return;
        }

        var column = _mainMenuColumns[targetColumnIndex];
        if (column.Count == 0)
        {
            return;
        }

        var targetRowIndex = Math.Clamp(preferredRowIndex, 0, column.Count - 1);
        column[targetRowIndex].SetFocus();
    }

    private void RefreshViews()
    {
        if (_activeServerLabel is null)
        {
            return;
        }

        _activeServerLabel.Text = $"Active server: {(string.IsNullOrWhiteSpace(_activeServer) ? "<none>" : _activeServer)}";
    }

    private void ReloadAndRefresh()
    {
        var summary = WaitForTaskCompletion(
            _service.LoadConfigSummaryAsync(_configPath, GetConfigEncryptionPassword(), _cancellationToken),
            "Refreshing",
            "Reloading configuration...");
        if (!summary.Succeeded || summary.Value is null)
        {
            ShowResult(summary);
            _exitCode = summary.ExitCode;
            return;
        }

        _config = summary.Value;
        if (string.IsNullOrWhiteSpace(_activeServer)
            || _config.Servers.All(server => !server.ServerName.Equals(_activeServer, StringComparison.OrdinalIgnoreCase)))
        {
            _activeServer = ResolveActiveServerName(_config);
        }

        RefreshViews();
    }

    private void SaveCurrentConfig()
    {
        var result = WaitForTaskCompletion(
            _service.SaveConfigAsync(_configPath, GetConfigEncryptionPassword(), _cancellationToken),
            "Save Configuration",
            "Saving configuration...");

        if (result.Succeeded)
        {
            ReloadAndRefresh();
        }

        ShowResult(result);
    }

    private void ShowConfigDialog()
    {
        var lines = new List<string>
        {
            $"Config Path: {_configPath}",
            $"Selected Server: {(string.IsNullOrWhiteSpace(_config.SelectedServerName) ? "<none>" : _config.SelectedServerName)}",
            $"Password Encryption: {(_config.EncryptPasswords ? $"enabled ({(_configEncryptionPassword.HasValue ? "unlocked" : "locked")})" : "disabled")}",
            $"Connection Timeout: {_config.Timeouts.ConnectionTimeoutSeconds}s",
            $"Command Timeout: {_config.Timeouts.CommandTimeoutSeconds}s",
            $"Configured Servers: {_config.Servers.Count}",
            $"Tracked Databases: {_config.Servers.Sum(server => server.Databases.Count)}",
            $"Tracked Users: {_config.Servers.Sum(server => server.Databases.Sum(database => database.Users.Count))}",
            string.Empty
        };

        foreach (var server in _config.Servers)
        {
            lines.Add(BuildServerLine(server));
            foreach (var database in server.Databases.OrderBy(database => database.DatabaseName, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add($"  Database: {database.DatabaseName}");
                foreach (var user in database.Users.OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add($"    User: {user.Username} | Password: {BuildPasswordState(user.Password, user.Encrypted)} | Roles: {string.Join(", ", user.Roles)}");
                }
            }
        }

        ShowTextDialog("Config", string.Join(Environment.NewLine, lines));
    }

    private void ShowAboutDialog()
    {
        var repoUrlField = new TextField
        {
            X = 1,
            Y = 8,
            Width = Dim.Fill(1),
            Text = AppVersion.RepositoryUrl,
            ReadOnly = true,
            TabStop = TabBehavior.TabStop
        };
        var copyButton = new Button { Text = "Copy URL (Ctrl+C)" };
        var closeButton = new Button { Text = "Close (Esc)" };
        var dialog = new Dialog
        {
            Title = "About SQL Manager",
            Width = 96,
            Height = 17,
            TabStop = TabBehavior.TabGroup
        };
        var textView = new TextView
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Height = 7,
            ReadOnly = true,
            WordWrap = true,
            Text = BuildAboutSummaryText(),
            TabStop = TabBehavior.NoStop
        };
        var repoUrlLabel = new Label
        {
            X = 1,
            Y = 7,
            Text = "GitHub Repository (selectable, Ctrl+C to copy):"
        };

        dialog.Add(textView, repoUrlLabel, repoUrlField);
        dialog.Buttons = [copyButton, closeButton];

        void CopyRepoUrl()
        {
            var clipboard = RequireApp().Clipboard;
            if (clipboard is not null && clipboard.TrySetClipboardData(AppVersion.RepositoryUrl))
            {
                MessageBox.Query(RequireApp(), "About SQL Manager", "Repository URL copied to clipboard.", "OK");
                return;
            }

            MessageBox.ErrorQuery(RequireApp(), "About SQL Manager", "Clipboard is not available. Select the URL field and copy it manually.", "OK");
        }

        copyButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            CopyRepoUrl();
        };
        closeButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            dialog.RequestStop();
        };
        dialog.KeyDown += (_, key) =>
        {
            if (key == Key.Esc)
            {
                key.Handled = true;
                dialog.RequestStop();
            }
        };
        repoUrlField.KeyDown += (_, key) =>
        {
            if (key == Key.C.WithCtrl)
            {
                key.Handled = true;
                CopyRepoUrl();
            }
        };

        repoUrlField.SelectAll();
        repoUrlField.SetFocus();
        RequireApp().Run(dialog);
        dialog.Dispose();
    }

    private void ShowVersionDialog()
        => ShowTextDialog("Version", HelpContent.BuildVersionText());

    private void ShowSnakeGameDialog()
    {
        var game = new SnakeGame();
        var scoreLabel = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Text = string.Empty
        };
        var controlsLabel = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Text = "Use Arrow keys or WASD to move. Press R to restart and Esc to close."
        };
        var boardView = new TextView
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Height = Dim.Fill(3),
            ReadOnly = true,
            WordWrap = false,
            Text = string.Empty,
            TabStop = TabBehavior.TabStop
        };
        var restartButton = new Button { Text = "Restart (R)" };
        var closeButton = new Button { Text = "Close (Esc)" };
        var dialog = new Dialog
        {
            Title = "Snake",
            Width = 38,
            Height = 23,
            TabStop = TabBehavior.TabGroup
        };

        dialog.Add(scoreLabel, controlsLabel, boardView);
        dialog.Buttons = [restartButton, closeButton];

        var app = RequireApp();
        var isRunning = true;

        void RefreshSnakeView()
        {
            var highScore = RecordGameHighScore("Snake", game.Score);
            scoreLabel.Text = game.IsGameOver
                ? $"Score: {game.Score} | High: {highScore} | Game over"
                : $"Score: {game.Score} | High: {highScore} | Length: {game.Segments.Count}";
            boardView.Text = game.RenderBoard();
        }

        void RestartGame()
        {
            game.Reset();
            RefreshSnakeView();
            boardView.SetFocus();
        }

        bool HandleSnakeKey(Key key)
        {
            var normalized = key.NoCtrl.NoAlt.NoShift;
            if (normalized == Key.Esc)
            {
                isRunning = false;
                dialog.RequestStop();
                return true;
            }

            if (normalized == Key.R)
            {
                RestartGame();
                return true;
            }

            if (normalized == Key.CursorUp || normalized == Key.W)
            {
                game.ChangeDirection(SnakeDirection.Up);
                return true;
            }

            if (normalized == Key.CursorDown || normalized == Key.S)
            {
                game.ChangeDirection(SnakeDirection.Down);
                return true;
            }

            if (normalized == Key.CursorLeft || normalized == Key.A)
            {
                game.ChangeDirection(SnakeDirection.Left);
                return true;
            }

            if (normalized == Key.CursorRight || normalized == Key.D)
            {
                game.ChangeDirection(SnakeDirection.Right);
                return true;
            }

            return false;
        }

        void OnSnakeKeyDown(object? _, Key key)
        {
            if (HandleSnakeKey(key))
            {
                key.Handled = true;
            }
        }

        var timerToken = app.AddTimeout(TimeSpan.FromMilliseconds(140), () =>
        {
            if (!isRunning)
            {
                return false;
            }

            game.Tick();
            RefreshSnakeView();
            return true;
        });

        restartButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            RestartGame();
        };
        closeButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            isRunning = false;
            dialog.RequestStop();
        };
        dialog.KeyDown += OnSnakeKeyDown;
        boardView.KeyDown += OnSnakeKeyDown;
        restartButton.KeyDown += OnSnakeKeyDown;
        closeButton.KeyDown += OnSnakeKeyDown;

        RefreshSnakeView();
        boardView.SetFocus();
        app.Run(dialog);
        isRunning = false;
        if (timerToken is not null)
        {
            app.RemoveTimeout(timerToken);
        }
        dialog.Dispose();
    }

    private void ShowPongGameDialog()
    {
        var game = new PongGame();
        var scoreLabel = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Text = string.Empty
        };
        var controlsLabel = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Text = "Use Up/Down or W/S. Press R to restart and Esc to close."
        };
        var boardView = new TextView
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Height = Dim.Fill(3),
            ReadOnly = true,
            WordWrap = false,
            Text = string.Empty,
            TabStop = TabBehavior.TabStop
        };
        var restartButton = new Button { Text = "Restart (R)" };
        var closeButton = new Button { Text = "Close (Esc)" };
        var dialog = new Dialog
        {
            Title = "Pong",
            Width = 42,
            Height = 23,
            TabStop = TabBehavior.TabGroup
        };

        dialog.Add(scoreLabel, controlsLabel, boardView);
        dialog.Buttons = [restartButton, closeButton];

        var app = RequireApp();
        var isRunning = true;

        void RefreshPongView()
        {
            var highScore = RecordGameHighScore("Pong", game.Score);
            scoreLabel.Text = game.IsGameOver
                ? $"Score: {game.Score} | High: {highScore} | Game over"
                : $"Score: {game.Score} | High: {highScore}";
            boardView.Text = game.RenderBoard();
        }

        void RestartGame()
        {
            game.Reset();
            RefreshPongView();
            boardView.SetFocus();
        }

        bool HandlePongKey(Key key)
        {
            var normalized = key.NoCtrl.NoAlt.NoShift;
            if (normalized == Key.Esc)
            {
                isRunning = false;
                dialog.RequestStop();
                return true;
            }

            if (normalized == Key.R)
            {
                RestartGame();
                return true;
            }

            if (normalized == Key.CursorUp || normalized == Key.W)
            {
                game.MovePlayerUp();
                RefreshPongView();
                return true;
            }

            if (normalized == Key.CursorDown || normalized == Key.S)
            {
                game.MovePlayerDown();
                RefreshPongView();
                return true;
            }

            return false;
        }

        void OnPongKeyDown(object? _, Key key)
        {
            if (HandlePongKey(key))
            {
                key.Handled = true;
            }
        }

        var timerToken = app.AddTimeout(TimeSpan.FromMilliseconds(110), () =>
        {
            if (!isRunning)
            {
                return false;
            }

            game.Tick();
            RefreshPongView();
            return true;
        });

        restartButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            RestartGame();
        };
        closeButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            isRunning = false;
            dialog.RequestStop();
        };
        dialog.KeyDown += OnPongKeyDown;
        boardView.KeyDown += OnPongKeyDown;
        restartButton.KeyDown += OnPongKeyDown;
        closeButton.KeyDown += OnPongKeyDown;

        RefreshPongView();
        boardView.SetFocus();
        app.Run(dialog);
        isRunning = false;
        if (timerToken is not null)
        {
            app.RemoveTimeout(timerToken);
        }
        dialog.Dispose();
    }

    private void ShowTetrisGameDialog()
    {
        var game = new TetrisGame();
        var scoreLabel = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Text = string.Empty
        };
        var controlsLabel = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Text = "Use Arrow keys or WASD. Up/W rotate, Space hard drop, R restart, Esc close."
        };
        var boardView = new TextView
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Height = Dim.Fill(3),
            ReadOnly = true,
            WordWrap = false,
            Text = string.Empty,
            TabStop = TabBehavior.TabStop
        };
        var restartButton = new Button { Text = "Restart (R)" };
        var closeButton = new Button { Text = "Close (Esc)" };
        var dialog = new Dialog
        {
            Title = "Tetris",
            Width = 32,
            Height = 28,
            TabStop = TabBehavior.TabGroup
        };

        dialog.Add(scoreLabel, controlsLabel, boardView);
        dialog.Buttons = [restartButton, closeButton];

        var app = RequireApp();
        var isRunning = true;

        void RefreshTetrisView()
        {
            var highScore = RecordGameHighScore("Tetris", game.Score);
            scoreLabel.Text = game.IsGameOver
                ? $"Score: {game.Score} | High: {highScore} | Lines: {game.LinesCleared} | Game over"
                : $"Score: {game.Score} | High: {highScore} | Lines: {game.LinesCleared}";
            boardView.Text = game.RenderBoard();
        }

        void RestartGame()
        {
            game.Reset();
            RefreshTetrisView();
            boardView.SetFocus();
        }

        bool HandleTetrisKey(Key key)
        {
            var normalized = key.NoCtrl.NoAlt.NoShift;
            if (normalized == Key.Esc)
            {
                isRunning = false;
                dialog.RequestStop();
                return true;
            }

            if (normalized == Key.R)
            {
                RestartGame();
                return true;
            }

            if (normalized == Key.CursorLeft || normalized == Key.A)
            {
                game.MoveLeft();
                RefreshTetrisView();
                return true;
            }

            if (normalized == Key.CursorRight || normalized == Key.D)
            {
                game.MoveRight();
                RefreshTetrisView();
                return true;
            }

            if (normalized == Key.CursorUp || normalized == Key.W)
            {
                game.RotateClockwise();
                RefreshTetrisView();
                return true;
            }

            if (normalized == Key.CursorDown || normalized == Key.S)
            {
                game.SoftDrop();
                RefreshTetrisView();
                return true;
            }

            if (normalized == Key.Space)
            {
                game.HardDrop();
                RefreshTetrisView();
                return true;
            }

            return false;
        }

        void OnTetrisKeyDown(object? _, Key key)
        {
            if (HandleTetrisKey(key))
            {
                key.Handled = true;
            }
        }

        var timerToken = app.AddTimeout(TimeSpan.FromMilliseconds(320), () =>
        {
            if (!isRunning)
            {
                return false;
            }

            game.Tick();
            RefreshTetrisView();
            return true;
        });

        restartButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            RestartGame();
        };
        closeButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            isRunning = false;
            dialog.RequestStop();
        };
        dialog.KeyDown += OnTetrisKeyDown;
        boardView.KeyDown += OnTetrisKeyDown;
        restartButton.KeyDown += OnTetrisKeyDown;
        closeButton.KeyDown += OnTetrisKeyDown;

        RefreshTetrisView();
        boardView.SetFocus();
        app.Run(dialog);
        isRunning = false;
        if (timerToken is not null)
        {
            app.RemoveTimeout(timerToken);
        }
        dialog.Dispose();
    }

    private void ShowHelpOverviewDialog()
        => ShowTextDialog("Help", HelpContent.BuildHelpOverviewText(_configPath));

    private void ShowCommandReferenceDialog()
        => ShowTextDialog("Command Reference", HelpContent.BuildCommandReferenceText(_configPath));

    private void ShowPasswordEncryptionDialog()
    {
        if (!_config.EncryptPasswords)
        {
            ShowEnablePasswordEncryptionDialog();
            return;
        }

        if (_configEncryptionPassword.HasValue)
        {
            var action = MessageBox.Query(
                RequireApp(),
                "Encrypt Passwords",
                "encryptPasswords is enabled and currently unlocked.",
                "Turn Off",
                "Relock",
                "Close");

            if (action == 0)
            {
                ShowDisablePasswordEncryptionDialog(promptForPassword: false);
                return;
            }

            if (action == 1)
            {
                _configEncryptionPassword.Clear();
                ReloadAndRefresh();
                MessageBox.Query(RequireApp(), "Encrypt Passwords", "Stored passwords are locked again for this session.", "OK");
            }

            return;
        }

        var lockedAction = MessageBox.Query(
            RequireApp(),
            "Encrypt Passwords",
            "encryptPasswords is enabled and currently locked.",
            "Unlock",
            "Turn Off",
            "Close");

        if (lockedAction == 0)
        {
            ShowUnlockPasswordEncryptionDialog();
            return;
        }

        if (lockedAction == 1)
        {
            ShowDisablePasswordEncryptionDialog(promptForPassword: true);
        }
    }

    private void ShowEnablePasswordEncryptionDialog()
    {
        var passwordField = CreateSecretField();
        var confirmField = CreateSecretField();
        var result = ShowFormDialog(
            "Enable Password Encryption",
            78,
            14,
            "Enable",
            dialog =>
            {
                dialog.Add(new Label
                {
                    X = 1,
                    Y = 1,
                    Width = Dim.Fill(2),
                    Text = "The unlock password must be at least 10 characters and include upper, lower, number, and symbol."
                });
                AddField(dialog, 4, "Unlock Password:", passwordField);
                AddField(dialog, 6, "Confirm Password:", confirmField);
            },
            () =>
            {
                var password = GetRequiredText(passwordField, "Unlock password is required.");
                var confirmation = GetRequiredText(confirmField, "Password confirmation is required.");
                if (!string.Equals(password, confirmation, StringComparison.Ordinal))
                {
                    throw new UserInputException("The unlock passwords do not match.");
                }

                var operation = WaitForTaskCompletion(
                    _service.ConfigurePasswordEncryptionAsync(_configPath, true, password, _cancellationToken),
                    "Enable Password Encryption",
                    "Encrypting stored passwords...");
                if (operation.Succeeded)
                {
                    _configEncryptionPassword.Set(password);
                    ReloadAndRefresh();
                }

                return OperationExecutionResult.FromResult(operation);
            });

        if (result?.Result is not null)
        {
            ShowResult(result.Result);
        }
    }

    private void ShowUnlockPasswordEncryptionDialog()
    {
        var passwordField = CreateSecretField();
        var result = ShowFormDialog(
            "Unlock Passwords",
            72,
            10,
            "Unlock",
            dialog => AddField(dialog, 1, "Encryption Password:", passwordField),
            () =>
            {
                var password = GetRequiredText(passwordField, "Encryption password is required.");
                var summary = WaitForTaskCompletion(
                    _service.LoadConfigSummaryAsync(_configPath, password, _cancellationToken),
                    "Unlock Passwords",
                    "Validating encryption password...");
                if (!summary.Succeeded || summary.Value is null)
                {
                    return OperationExecutionResult.Failure(OperationResult.Failure(summary.Message, summary.ExitCode, summary.Details.ToArray()));
                }

                _configEncryptionPassword.Set(password);
                _config = summary.Value;
                if (string.IsNullOrWhiteSpace(_activeServer)
                    || _config.Servers.All(server => !server.ServerName.Equals(_activeServer, StringComparison.OrdinalIgnoreCase)))
                {
                    _activeServer = ResolveActiveServerName(_config);
                }

                RefreshViews();
                return OperationExecutionResult.FromResult(OperationResult.Success("Password encryption unlocked."));
            });

        if (result?.Result is not null)
        {
            ShowResult(result.Result);
        }
    }

    private bool PromptForStartupEncryptionPassword()
    {
        while (true)
        {
            var passwordField = CreateSecretField();
            var result = ShowFormDialog(
                "Unlock Encrypted Config",
                76,
                12,
                "OK",
                dialog =>
                {
                    dialog.Add(new Label
                    {
                        X = 1,
                        Y = 1,
                        Width = Dim.Fill(2),
                        Text = "This config has encryptPasswords enabled. Enter the decryption password to start the UI."
                    });
                    AddField(dialog, 4, "Encryption Password:", passwordField);
                },
                () =>
                {
                    var password = GetRequiredText(passwordField, "Encryption password is required.");
                    var summary = WaitForTaskCompletion(
                        _service.LoadConfigSummaryAsync(_configPath, password, _cancellationToken),
                        "Unlock Encrypted Config",
                        "Validating encryption password...");
                    if (!summary.Succeeded || summary.Value is null)
                    {
                        return OperationExecutionResult.Failure(OperationResult.Failure(summary.Message, summary.ExitCode, summary.Details.ToArray()));
                    }

                    _configEncryptionPassword.Set(password);
                    _config = summary.Value;
                    _activeServer = ResolveActiveServerName(_config);
                    return OperationExecutionResult.FromResult(OperationResult.Success("Configuration unlocked."));
                },
                backButtonText: "Exit");

            if (result is not null)
            {
                return true;
            }

            if (ConfirmExitApplication())
            {
                _exitCode = 1;
                return false;
            }
        }
    }

    private void ShowDisablePasswordEncryptionDialog(bool promptForPassword)
    {
        if (promptForPassword)
        {
            var passwordField = CreateSecretField();
            var result = ShowFormDialog(
                "Turn Off Encrypt Passwords",
                72,
                10,
                "Turn Off",
                dialog => AddField(dialog, 1, "Encryption Password:", passwordField),
                () => DisablePasswordEncryption(GetRequiredText(passwordField, "Encryption password is required.")));

            if (result?.Result is not null)
            {
                ShowResult(result.Result);
            }

            return;
        }

        var password = GetConfigEncryptionPassword();
        if (string.IsNullOrWhiteSpace(password))
        {
            ShowDisablePasswordEncryptionDialog(promptForPassword: true);
            return;
        }

        ShowResult(DisablePasswordEncryption(password).Result!);
    }

    private OperationExecutionResult DisablePasswordEncryption(string password)
    {
        var result = WaitForTaskCompletion(
            _service.ConfigurePasswordEncryptionAsync(_configPath, false, password, _cancellationToken),
            "Turn Off Encrypt Passwords",
            "Writing plaintext passwords back to the config...");
        if (result.Succeeded)
        {
            _configEncryptionPassword.Clear();
            ReloadAndRefresh();
        }

        return OperationExecutionResult.FromResult(result);
    }

    private void ShowSelectServerDialog()
    {
        if (_config.Servers.Count == 0)
        {
            MessageBox.ErrorQuery(RequireApp(), "Select Active Server", "No servers are configured.", "OK");
            return;
        }

        var items = _config.Servers.Select(BuildServerLine).ToList();
        var listView = CreateListView(items, Math.Max(0, _config.Servers.FindIndex(server => server.ServerName.Equals(_activeServer, StringComparison.OrdinalIgnoreCase))));

        var selection = ShowListDialog("Select Active Server", listView, "Select");
        if (selection != 0)
        {
            return;
        }

        var selectedServer = _config.Servers[Math.Clamp(listView.SelectedItem ?? 0, 0, _config.Servers.Count - 1)];
        var result = WaitForTaskCompletion(
            _service.SelectServerAsync(new CommandOptions
            {
                Command = CommandKind.SelectServer,
                ConfigPath = _configPath,
                EncryptionPassword = GetConfigEncryptionPassword(),
                ServerName = selectedServer.ServerName
            }, _cancellationToken),
            "Select Active Server",
            "Saving selected server...");

        if (result.Succeeded)
        {
            _activeServer = selectedServer.ServerName;
            ReloadAndRefresh();
        }

        ShowResult(result);
    }

    private void ShowInitializeConfigDialog()
    {
        var serverField = CreateTextField(_activeServer);
        var adminUserField = CreateTextField(GetActiveServerConfig()?.AdminUsername);
        var adminPasswordField = CreateSecretField(GetActiveServerConfig()?.AdminPassword);

        var result = ShowFormDialog(
            "Initialize Config",
            72,
            14,
            "Save",
            dialog =>
            {
                AddField(dialog, 1, "Server Name (optional):", serverField);
                AddField(dialog, 3, "Admin User (optional):", adminUserField);
                AddField(dialog, 5, "Admin Password (optional):", adminPasswordField);
            },
            () => ExecuteOperation(_service.InitializeConfigAsync(new CommandOptions
            {
                Command = CommandKind.InitConfig,
                ConfigPath = _configPath,
                EncryptionPassword = GetConfigEncryptionPassword(),
                ServerName = GetText(serverField),
                AdminUsername = GetText(adminUserField),
                AdminPassword = GetText(adminPasswordField)
            }, _cancellationToken),
            title: "Initialize Config",
            newActiveServer: GetText(serverField)));

        if (result?.Result is not null)
        {
            ShowResult(result.Result);
        }
    }

    private void ShowAddServerDialog()
    {
        ShowServerEditorDialog(null);
    }

    private void ShowEditServerDialog()
    {
        if (_config.Servers.Count == 0)
        {
            MessageBox.ErrorQuery(RequireApp(), "Edit Server", "No servers are configured.", "OK");
            return;
        }

        var items = _config.Servers.Select(BuildServerLine).ToList();
        var listView = CreateListView(items, Math.Max(0, _config.Servers.FindIndex(server => server.ServerName.Equals(_activeServer, StringComparison.OrdinalIgnoreCase))));
        var selection = ShowListDialog("Edit Server", listView, "Edit");
        if (selection != 0)
        {
            return;
        }

        var server = _config.Servers[Math.Clamp(listView.SelectedItem ?? 0, 0, _config.Servers.Count - 1)];
        ShowServerEditorDialog(server);
    }

    private void ShowServerEditorDialog(ServerConfig? existingServer)
    {
        var isEdit = existingServer is not null;
        var activeConfig = existingServer ?? GetActiveServerConfig();
        var serverField = CreateTextField(existingServer?.ServerName ?? _activeServer);
        var providerField = CreateTextField(existingServer?.Provider ?? activeConfig?.Provider ?? SqlProviders.SqlServer);
        var portField = CreateTextField(existingServer?.Port?.ToString() ?? activeConfig?.Port?.ToString());
        var adminDatabaseField = CreateTextField(existingServer?.AdminDatabase ?? activeConfig?.AdminDatabase ?? string.Empty);
        var adminUserField = CreateTextField(existingServer?.AdminUsername ?? activeConfig?.AdminUsername);
        var adminPasswordField = CreateSecretField(existingServer?.AdminPassword ?? activeConfig?.AdminPassword);

        var saveButton = new Button { Text = isEdit ? "Save" : "Add" };
        var showPasswordButton = new Button { Text = "Show Password" };
        var versionHistoryButton = new Button { Text = "See Version History" };
        var backButton = new Button { Text = "Back" };
        var dialog = new Dialog
        {
            Title = isEdit ? $"Edit Server: {existingServer!.ServerName}" : "Add Server",
            Width = 88,
            Height = 20,
            TabStop = TabBehavior.TabGroup
        };

        AddField(dialog, 1, "Server Name:", serverField);
        AddField(dialog, 3, "Provider:", providerField);
        AddField(dialog, 5, "Port:", portField);
        AddField(dialog, 7, "Admin Database:", adminDatabaseField);
        AddField(dialog, 9, "Admin User:", adminUserField);
        AddField(dialog, 11, "Admin Password:", adminPasswordField);
        dialog.Buttons = isEdit
            ? [saveButton, showPasswordButton, versionHistoryButton, backButton]
            : [saveButton, backButton];

        OperationExecutionResult? result = null;
        saveButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            RunGuardedUiAction(dialog.Title?.ToString() ?? "Server", () =>
            {
                var serverName = GetRequiredText(serverField, "Server name is required.");
                var adminUser = GetRequiredText(adminUserField, "Admin user is required.");
                var provider = GetText(providerField);
                var adminDatabase = GetText(adminDatabaseField);
                var port = ParseOptionalPort(portField);

                result = isEdit
                    ? ExecuteOperation(
                        _service.UpdateServerAsync(
                            _configPath,
                            existingServer!.ServerName,
                            serverName,
                            provider,
                            port,
                            adminDatabase,
                            adminUser,
                            GetText(adminPasswordField),
                            GetConfigEncryptionPassword(),
                            _cancellationToken),
                        title: "Edit Server",
                        newActiveServer: serverName,
                        selectServerAfterSuccess: true)
                    : ExecuteOperation(
                        _service.AddServerAsync(new CommandOptions
                        {
                            Command = CommandKind.AddServer,
                            ConfigPath = _configPath,
                            EncryptionPassword = GetConfigEncryptionPassword(),
                            ServerName = serverName,
                            Provider = provider,
                            Port = port,
                            AdminDatabase = adminDatabase,
                            AdminUsername = adminUser,
                            AdminPassword = GetText(adminPasswordField)
                        }, _cancellationToken),
                        title: "Add Server",
                        newActiveServer: serverName,
                        selectServerAfterSuccess: true);

                if (result is not null && !result.Succeeded && result.Result is not null)
                {
                    ShowResult(result.Result);
                    return;
                }

                dialog.RequestStop();
            });
        };
        showPasswordButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            var password = GetText(adminPasswordField);
            if (string.IsNullOrWhiteSpace(password))
            {
                MessageBox.ErrorQuery(RequireApp(), "Show Password", "Password is not available in the current config view.", "OK");
                return;
            }

            MessageBox.Query(RequireApp(), $"Password: {GetText(serverField)}", password, "OK");
        };
        versionHistoryButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            if (existingServer is not null)
            {
                ShowVersionHistoryDialog($"Server History: {existingServer.ServerName}", existingServer.VersionHistory);
            }
        };
        backButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            dialog.RequestStop();
        };

        serverField.SetFocus();
        RequireApp().Run(dialog);
        dialog.Dispose();

        if (result?.Result is not null)
        {
            ShowResult(result.Result);
        }
    }

    private void ShowSyncServerDialog()
    {
        var server = RequireActiveServer();
        if (server is null)
        {
            return;
        }

        var adminUserField = CreateTextField(server.AdminUsername);
        var adminPasswordField = CreateSecretField(server.AdminPassword);
        var result = ShowServerDialog(
            "Sync Server Configuration",
            server,
            12,
            "Sync",
            dialog =>
            {
                AddAdminCredentialSection(dialog, 1, server, adminUserField, adminPasswordField);
            },
            () => ExecuteOperation(
                _service.SyncServerAsync(new CommandOptions
                {
                    Command = CommandKind.SyncServer,
                    ConfigPath = _configPath,
                    EncryptionPassword = GetConfigEncryptionPassword(),
                    ServerName = server.ServerName,
                    AdminUsername = ResolveAdminUsername(server, adminUserField),
                    AdminPassword = ResolveAdminPassword(server, adminPasswordField)
                }, _cancellationToken),
                "Sync Server Configuration",
                "Syncing tracked databases and users from the selected server. Press Ctrl+C to cancel."));

        if (result?.Result is not null)
        {
            ShowResult(result.Result);
        }
    }

    private void ShowCreateDatabaseDialog()
    {
        var server = RequireActiveServer();
        if (server is null)
        {
            return;
        }

        var adminUserField = CreateTextField(server.AdminUsername);
        var adminPasswordField = CreateSecretField(server.AdminPassword);
        var databaseField = CreateTextField();
        var result = ShowServerDialog(
            "Create Database",
            server,
            14,
            "Create",
            dialog =>
            {
                var nextY = AddAdminCredentialSection(dialog, 1, server, adminUserField, adminPasswordField);
                AddField(dialog, nextY, "Database Name:", databaseField);
            },
            () => ExecuteDatabaseSaveOperation(_service.CreateDatabaseAsync(new CommandOptions
            {
                Command = CommandKind.CreateDatabase,
                ConfigPath = _configPath,
                EncryptionPassword = GetConfigEncryptionPassword(),
                ServerName = server.ServerName,
                AdminUsername = ResolveAdminUsername(server, adminUserField),
                AdminPassword = ResolveAdminPassword(server, adminPasswordField),
                DatabaseName = GetRequiredText(databaseField, "Database name is required.")
            }, _cancellationToken)));

        if (result?.Result is not null)
        {
            ShowResult(result.Result);
        }
    }

    private void ShowDatabasesDialog()
    {
        var server = RequireActiveServer();
        if (server is null)
        {
            return;
        }

        var adminUserField = CreateTextField(server.AdminUsername);
        var adminPasswordField = CreateSecretField(server.AdminPassword);
        var result = WaitForTaskCompletion(
            _service.ShowDatabasesAsync(new CommandOptions
            {
                Command = CommandKind.ShowDatabases,
                ConfigPath = _configPath,
                EncryptionPassword = GetConfigEncryptionPassword(),
                ServerName = server.ServerName,
                AdminUsername = ResolveAdminUsername(server, adminUserField),
                AdminPassword = ResolveAdminPassword(server, adminPasswordField)
            }, _cancellationToken),
            "Show Databases",
            "Loading databases from SQL Server. Press Ctrl+C to cancel.");

        if (result.ExitCode == 130)
        {
            _exitCode = 130;
        }

        ShowResult(result);
        if (result.Succeeded && result.Value is { Count: > 0 })
        {
            ShowDatabaseEntriesDialog(server, result.Value);
        }
    }

    private void ShowRemoveDatabaseDialog()
    {
        var server = RequireActiveServer();
        if (server is null)
        {
            return;
        }

        var databaseName = PromptDatabaseName(server, "Remove Database");
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return;
        }

        var adminUserField = CreateTextField(server.AdminUsername);
        var adminPasswordField = CreateSecretField(server.AdminPassword);
        var result = ShowServerDialog(
            "Remove Database",
            server,
            16,
            "Remove",
            dialog =>
            {
                dialog.Add(new Label { X = 1, Y = 1, Text = $"Database: {databaseName}" });
                var nextY = AddAdminCredentialSection(dialog, 3, server, adminUserField, adminPasswordField);
                dialog.Add(new Label
                {
                    X = 1,
                    Y = nextY,
                    Width = Dim.Fill(2),
                    Text = "This drops the database from SQL Server and removes it from the config."
                });
            },
            () => ExecuteDatabaseSaveOperation(_service.RemoveDatabaseAsync(new CommandOptions
            {
                Command = CommandKind.RemoveDatabase,
                ConfigPath = _configPath,
                EncryptionPassword = GetConfigEncryptionPassword(),
                ServerName = server.ServerName,
                AdminUsername = ResolveAdminUsername(server, adminUserField),
                AdminPassword = ResolveAdminPassword(server, adminPasswordField),
                DatabaseName = databaseName
            }, _cancellationToken)));

        if (result?.Result is not null)
        {
            ShowResult(result.Result);
        }
    }

    private void ShowCreateUserDialog()
    {
        var server = RequireActiveServer();
        if (server is null)
        {
            return;
        }

        if (server.Databases.Count == 0)
        {
            MessageBox.ErrorQuery(RequireApp(), "Create User", "No configured databases were found. Sync the server or create a database first.", "OK");
            return;
        }

        ShowUserAccessDialog(server, "Create User", null, includePasswordField: true);
    }

    private void ShowManageRolesDialog()
    {
        var server = RequireActiveServer();
        if (server is null)
        {
            return;
        }

        if (server.Databases.Count == 0)
        {
            MessageBox.ErrorQuery(RequireApp(), "Manage Roles", "No configured databases were found. Sync the server or create a database first.", "OK");
            return;
        }

        var userName = PromptKnownUserName(server, "Manage Roles");
        if (string.IsNullOrWhiteSpace(userName))
        {
            return;
        }

        ShowUserAccessDialog(server, "Manage Roles", userName, includePasswordField: false);
    }

    private void ShowUsersDialog()
    {
        var server = RequireActiveServer();
        if (server is null)
        {
            return;
        }

        var databaseName = PromptDatabaseName(server, "Show Users");
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return;
        }

        var adminUserField = CreateTextField(server.AdminUsername);
        var adminPasswordField = CreateSecretField(server.AdminPassword);
        var result = ShowServerDialog(
            "Show Users",
            server,
            14,
            "Show",
            dialog =>
            {
                dialog.Add(new Label { X = 1, Y = 1, Text = $"Database: {databaseName}" });
                AddAdminCredentialSection(dialog, 3, server, adminUserField, adminPasswordField);
            },
            () => ExecuteUserLookup(_service.ShowUsersAsync(new CommandOptions
            {
                Command = CommandKind.ShowUsers,
                ConfigPath = _configPath,
                EncryptionPassword = GetConfigEncryptionPassword(),
                ServerName = server.ServerName,
                AdminUsername = ResolveAdminUsername(server, adminUserField),
                AdminPassword = ResolveAdminPassword(server, adminPasswordField),
                DatabaseName = databaseName
            }, _cancellationToken)));

        if (result?.UserResult is not null)
        {
            ShowResult(result.UserResult);
            if (result.UserResult.Value is { Count: > 0 })
            {
                ShowUserEntriesDialog(server, databaseName, result.UserResult.Value);
            }
        }
        else if (result?.Result is not null)
        {
            ShowResult(result.Result);
        }
    }

    private void ShowRemoveUserDialog()
    {
        var server = RequireActiveServer();
        if (server is null)
        {
            return;
        }

        var userName = PromptKnownUserName(server, "Remove User");
        if (string.IsNullOrWhiteSpace(userName))
        {
            return;
        }

        var existingDatabases = server.Databases
            .Where(database => database.Users.Any(user => user.Username.Equals(userName, StringComparison.OrdinalIgnoreCase)))
            .Select(database => database.DatabaseName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var adminUserField = CreateTextField(server.AdminUsername);
        var adminPasswordField = CreateSecretField(server.AdminPassword);
        var removeServerCheckBox = new CheckBox
        {
            X = 1,
            Y = 7,
            Width = Dim.Fill(2),
            Text = "Remove server login as well (drops database users from all databases)",
            Value = CheckState.UnChecked,
            TabStop = TabBehavior.TabStop
        };

        var selectionRows = new List<DatabaseSelectionRow>();
        var databaseViewportHeight = Math.Min(10, Math.Max(4, server.Databases.Count + 1));
        var dialogHeight = Math.Min(19 + databaseViewportHeight, 28);
        var result = ShowServerDialog(
            "Remove User",
            server,
            dialogHeight,
            "Remove",
            dialog =>
            {
                dialog.Add(new Label { X = 1, Y = 1, Text = $"User: {userName}" });
                var nextY = AddAdminCredentialSection(dialog, 3, server, adminUserField, adminPasswordField);
                removeServerCheckBox.Y = nextY;
                dialog.Add(removeServerCheckBox);
                dialog.Add(new Label { X = 1, Y = nextY + 2, Text = "Remove database access from:" });

                var databaseFrame = new FrameView
                {
                    Title = "Databases",
                    X = 1,
                    Y = nextY + 3,
                    Width = Dim.Fill(2),
                    Height = databaseViewportHeight + 2,
                    TabStop = TabBehavior.TabGroup
                };
                var databaseViewport = CreateScrollableViewport(databaseViewportHeight, server.Databases.Count + 1);
                databaseFrame.Add(databaseViewport);
                dialog.Add(databaseFrame);

                var currentY = 0;
                foreach (var database in server.Databases.OrderBy(database => database.DatabaseName, StringComparer.OrdinalIgnoreCase))
                {
                    var checkBox = new CheckBox
                    {
                        X = 3,
                        Y = currentY,
                        Width = Dim.Fill(4),
                        Text = database.DatabaseName,
                        Value = existingDatabases.Contains(database.DatabaseName) ? CheckState.Checked : CheckState.UnChecked,
                        TabStop = TabBehavior.TabStop
                    };
                    databaseViewport.Add(checkBox);
                    selectionRows.Add(new DatabaseSelectionRow(database.DatabaseName, checkBox));
                    currentY++;
                }
            },
            () =>
            {
                var selectedDatabaseNames = selectionRows
                    .Where(row => row.CheckBox.Value == CheckState.Checked)
                    .Select(row => row.DatabaseName)
                    .ToList();
                var removeServerLogin = removeServerCheckBox.Value == CheckState.Checked;
                if (!removeServerLogin && selectedDatabaseNames.Count == 0)
                {
                    throw new UserInputException("Select at least one database, or choose to remove the server login.");
                }

                return ExecuteDatabaseSaveOperation(_service.RemoveUserAsync(new CommandOptions
                {
                    Command = CommandKind.RemoveUser,
                    ConfigPath = _configPath,
                    EncryptionPassword = GetConfigEncryptionPassword(),
                    ServerName = server.ServerName,
                    AdminUsername = ResolveAdminUsername(server, adminUserField),
                    AdminPassword = ResolveAdminPassword(server, adminPasswordField),
                    UserName = userName,
                    DatabaseNames = selectedDatabaseNames,
                    DatabaseName = selectedDatabaseNames.Count == 1 ? selectedDatabaseNames[0] : null,
                    RemovalScope = removeServerLogin ? RemovalScope.Both : RemovalScope.Database,
                    RemoveServerLogin = removeServerLogin
                }, _cancellationToken));
            });

        if (result?.Result is not null)
        {
            ShowResult(result.Result);
        }
    }

    private void ShowUpdatePasswordDialog()
    {
        var server = RequireActiveServer();
        if (server is null)
        {
            return;
        }

        var userName = PromptKnownUserName(server, "Update Password");
        if (string.IsNullOrWhiteSpace(userName))
        {
            return;
        }

        var adminUserField = CreateTextField(server.AdminUsername);
        var adminPasswordField = CreateSecretField(server.AdminPassword);
        var newPasswordField = CreateSecretField();

        var result = ShowServerDialog(
            "Update Password",
            server,
            16,
            "Update",
            dialog =>
            {
                dialog.Add(new Label { X = 1, Y = 1, Text = $"User: {userName}" });
                var nextY = AddAdminCredentialSection(dialog, 3, server, adminUserField, adminPasswordField);
                AddPasswordFieldWithGenerateButton(dialog, nextY, "New Password (optional):", newPasswordField, "Update Password");
            },
            () => ExecuteDatabaseSaveOperation(_service.UpdatePasswordAsync(new CommandOptions
            {
                Command = CommandKind.UpdatePassword,
                ConfigPath = _configPath,
                EncryptionPassword = GetConfigEncryptionPassword(),
                ServerName = server.ServerName,
                AdminUsername = ResolveAdminUsername(server, adminUserField),
                AdminPassword = ResolveAdminPassword(server, adminPasswordField),
                UserName = userName,
                NewUserPassword = GetText(newPasswordField)
            }, _cancellationToken)));

        if (result?.Result is not null)
        {
            ShowResult(result.Result);
        }
    }

    private void ShowUserAccessDialog(ServerConfig server, string title, string? fixedUserName, bool includePasswordField)
    {
        var adminUserField = CreateTextField(server.AdminUsername);
        var adminPasswordField = CreateSecretField(server.AdminPassword);
        var userField = fixedUserName is null ? CreateTextField() : null;
        var passwordField = includePasswordField ? CreateSecretField() : null;
        var existingRoleLookup = BuildRoleLookup(server, fixedUserName);
        var roleRows = new List<DatabaseRoleRow>();

        var matrixViewportHeight = Math.Min(10, Math.Max(5, server.Databases.Count + 1));
        var dialogHeight = Math.Min(18 + matrixViewportHeight, 30);
        var result = ShowServerDialog(
            title,
            server,
            dialogHeight,
            title == "Create User" ? "Create" : "Save",
            dialog =>
            {
                var currentY = 1;
                if (!string.IsNullOrWhiteSpace(fixedUserName))
                {
                    dialog.Add(new Label { X = 1, Y = currentY, Text = $"User: {fixedUserName}" });
                    currentY += 2;
                }
                else
                {
                    AddField(dialog, currentY, "User Name:", userField!);
                    currentY += 2;
                }

                currentY = AddAdminCredentialSection(dialog, currentY, server, adminUserField, adminPasswordField);

                if (includePasswordField && passwordField is not null)
                {
                    AddPasswordFieldWithGenerateButton(dialog, currentY, "User Password (optional):", passwordField, title);
                    currentY += 2;
                }

                dialog.Add(new Label
                {
                    X = 1,
                    Y = currentY,
                    Width = Dim.Fill(2),
                    Text = "Select roles per database. Leave all roles unchecked to remove database access."
                });
                currentY += 2;

                var matrixFrame = new FrameView
                {
                    Title = "Database Access",
                    X = 1,
                    Y = currentY,
                    Width = Dim.Fill(2),
                    Height = matrixViewportHeight + 2,
                    TabStop = TabBehavior.TabGroup
                };
                var matrixViewport = CreateScrollableViewport(matrixViewportHeight, server.Databases.Count + 2);
                matrixViewport.Add(new Label { X = 1, Y = 0, Text = "Database" });
                matrixViewport.Add(new Label { X = 30, Y = 0, Text = "Owner" });
                matrixViewport.Add(new Label { X = 42, Y = 0, Text = "Reader" });
                matrixViewport.Add(new Label { X = 55, Y = 0, Text = "Writer" });

                var rowY = 1;
                foreach (var database in server.Databases.OrderBy(database => database.DatabaseName, StringComparer.OrdinalIgnoreCase))
                {
                    var roles = existingRoleLookup.TryGetValue(database.DatabaseName, out var storedRoles)
                        ? storedRoles
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var row = new DatabaseRoleRow(
                        database.DatabaseName,
                        new CheckBox { X = 30, Y = rowY, Width = 3, Height = 1, Text = string.Empty, Value = roles.Contains("db_owner") ? CheckState.Checked : CheckState.UnChecked, TabStop = TabBehavior.TabStop },
                        new CheckBox { X = 42, Y = rowY, Width = 3, Height = 1, Text = string.Empty, Value = roles.Contains("db_datareader") ? CheckState.Checked : CheckState.UnChecked, TabStop = TabBehavior.TabStop },
                        new CheckBox { X = 55, Y = rowY, Width = 3, Height = 1, Text = string.Empty, Value = roles.Contains("db_datawriter") ? CheckState.Checked : CheckState.UnChecked, TabStop = TabBehavior.TabStop });
                    matrixViewport.Add(new Label { X = 1, Y = rowY, Width = 27, Text = database.DatabaseName });
                    matrixViewport.Add(row.OwnerCheckBox, row.ReaderCheckBox, row.WriterCheckBox);
                    roleRows.Add(row);
                    rowY++;
                }

                matrixFrame.Add(matrixViewport);
                dialog.Add(matrixFrame);
            },
            () =>
            {
                var userName = fixedUserName ?? GetRequiredText(userField!, "User name is required.");
                var assignments = roleRows.Select(row => row.ToAssignment()).ToList();
                if (assignments.All(assignment => assignment.Roles.Count == 0))
                {
                    throw new UserInputException("Select at least one database role, or use Remove User to remove the login entirely.");
                }

                return ExecuteDatabaseSaveOperation(_service.SetUserAccessAsync(new CommandOptions
                {
                    Command = CommandKind.CreateUser,
                    ConfigPath = _configPath,
                    EncryptionPassword = GetConfigEncryptionPassword(),
                    ServerName = server.ServerName,
                    AdminUsername = ResolveAdminUsername(server, adminUserField),
                    AdminPassword = ResolveAdminPassword(server, adminPasswordField),
                    UserName = userName,
                    NewUserPassword = passwordField is null ? null : GetText(passwordField),
                    DatabaseRoleAssignments = assignments,
                    DatabaseNames = assignments.Select(assignment => assignment.DatabaseName).ToList()
                }, _cancellationToken));
            });

        if (result?.Result is not null)
        {
            ShowResult(result.Result);
        }
    }

    private OperationExecutionResult? ShowFormDialog(string title, int width, int height, string primaryButtonText, Action<Dialog> buildBody, Func<OperationExecutionResult> submit, string backButtonText = "Back")
    {
        var runButton = new Button { Text = primaryButtonText, IsDefault = true };
        var backButton = new Button { Text = backButtonText };
        var dialog = new Dialog
        {
            Title = title,
            Width = width,
            Height = height,
            TabStop = TabBehavior.TabGroup
        };
        buildBody(dialog);
        dialog.DefaultAcceptView = runButton;
        var initialFocus = FindPreferredInitialFocus(dialog);
        dialog.Buttons = [runButton, backButton];

        OperationExecutionResult? result = null;
        runButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            RunGuardedUiAction(title, () =>
            {
                result = submit();
                if (result is not null && !result.Succeeded && result.Result is not null)
                {
                    ShowResult(result.Result);
                    if (result.Result.ExitCode == 130)
                    {
                        _exitCode = 130;
                        RequireApp().RequestStop();
                    }

                    return;
                }

                dialog.RequestStop();
            });
        };
        backButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            result = null;
            dialog.RequestStop();
        };

        if (initialFocus is not null)
        {
            initialFocus.DefaultAcceptView = runButton;
            initialFocus.SetFocus();
        }
        else
        {
            runButton.SetFocus();
        }
        RequireApp().Run(dialog);
        dialog.Dispose();
        return result;
    }

    private OperationExecutionResult? ShowServerDialog(string title, ServerConfig server, int height, string primaryButtonText, Action<Dialog> buildBody, Func<OperationExecutionResult> submit)
        => ShowFormDialog(title, 92, height, primaryButtonText, dialog =>
        {
            dialog.Add(new Label { X = 1, Y = 0, Text = $"Server: {server.ServerName}" });
            buildBody(dialog);
        }, submit);

    private int ShowListDialog(string title, View content, string runButtonText, string backButtonText = "Back")
    {
        var primary = new Button { Text = runButtonText };
        var back = new Button { Text = backButtonText };
        var dialog = new Dialog
        {
            Title = title,
            Width = 76,
            Height = 18,
            TabStop = TabBehavior.TabGroup
        };
        dialog.Add(content);
        dialog.Buttons = [primary, back];

        var result = 2;
        primary.Accepting += (_, args) =>
        {
            args.Handled = true;
            result = 0;
            dialog.RequestStop();
        };
        back.Accepting += (_, args) =>
        {
            args.Handled = true;
            result = 1;
            dialog.RequestStop();
        };

        content.SetFocus();
        RequireApp().Run(dialog);
        dialog.Dispose();
        return result;
    }

    private void ShowTextDialog(string title, string text)
    {
        var closeButton = new Button { Text = "Close" };
        var dialog = new Dialog
        {
            Title = title,
            Width = 96,
            Height = 24,
            TabStop = TabBehavior.TabGroup
        };
        var textView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            ReadOnly = true,
            WordWrap = false,
            Text = text,
            TabStop = TabBehavior.TabStop
        };
        dialog.Add(textView);
        dialog.Buttons = [closeButton];
        closeButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            dialog.RequestStop();
        };
        textView.SetFocus();
        RequireApp().Run(dialog);
        dialog.Dispose();
    }

    private static string BuildAboutSummaryText()
        => string.Join(Environment.NewLine,
        [
            "SQL Manager",
            AppVersion.Description,
            string.Empty,
            $"Version: {AppVersion.DisplayVersion}",
            $"Built: {AppVersion.BuildDate}",
            AppVersion.Copyright
        ]);

    private void ShowUserEntriesDialog(ServerConfig server, string databaseName, IReadOnlyList<DatabaseUserRow> rows)
    {
        var items = rows
            .Select(row => $"{row.UserName} | {row.LoginName} | {row.Roles}")
            .ToList();
        var listView = CreateListView(items, 0);
        var selection = ShowListDialog($"Users in {databaseName}", listView, "Open");
        if (selection != 0)
        {
            return;
        }

        var row = rows[Math.Clamp(listView.SelectedItem ?? 0, 0, rows.Count - 1)];
        ShowUserEntryDialog(server, databaseName, row);
    }

    private void ShowUserEntryDialog(ServerConfig server, string databaseName, DatabaseUserRow row)
    {
        var trackedUser = server.Databases
            .FirstOrDefault(database => database.DatabaseName.Equals(databaseName, StringComparison.OrdinalIgnoreCase))?
            .Users
            .FirstOrDefault(user => user.Username.Equals(row.UserName, StringComparison.OrdinalIgnoreCase));
        var details = string.Join(Environment.NewLine,
        [
            $"Server: {server.ServerName}",
            $"Database: {databaseName}",
            $"User: {row.UserName}",
            $"Login: {row.LoginName}",
            $"Roles: {row.Roles}",
            $"Password State: {BuildPasswordState(trackedUser?.Password, trackedUser?.Encrypted ?? false)}",
            $"Connection String: {trackedUser?.ConnectionString ?? BuildConnectionStringPreview(server, databaseName, row.UserName)}"
        ]);

        var showPasswordButton = new Button { Text = "Show Password" };
        var versionHistoryButton = new Button { Text = "See Version History" };
        var closeButton = new Button { Text = "Close" };
        var dialog = new Dialog
        {
            Title = $"User Entry: {row.UserName}",
            Width = 96,
            Height = 18,
            TabStop = TabBehavior.TabGroup
        };

        var textView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            ReadOnly = true,
            WordWrap = false,
            Text = details,
            TabStop = TabBehavior.TabStop
        };
        dialog.Add(textView);
        dialog.Buttons = [showPasswordButton, versionHistoryButton, closeButton];

        showPasswordButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            if (trackedUser is null || string.IsNullOrWhiteSpace(trackedUser.Password))
            {
                MessageBox.ErrorQuery(RequireApp(), "Show Password", "Password is not available in the current config view.", "OK");
                return;
            }

            MessageBox.Query(RequireApp(), $"Password: {row.UserName}", trackedUser.Password, "OK");
        };
        versionHistoryButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            ShowVersionHistoryDialog($"User History: {row.UserName}", trackedUser?.VersionHistory ?? []);
        };
        closeButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            dialog.RequestStop();
        };

        textView.SetFocus();
        RequireApp().Run(dialog);
        dialog.Dispose();
    }

    private static string BuildConnectionStringPreview(ServerConfig server, string databaseName, string userName)
        => $"{(SqlProviders.Normalize(server.Provider) == SqlProviders.SqlServer ? "Server" : "Host")}={server.ServerName};Database={databaseName};User={userName};Password=<PASSWORD_REQUIRED>;";

    private void ShowDatabaseEntriesDialog(ServerConfig server, IReadOnlyList<string> databaseNames)
    {
        var orderedDatabaseNames = databaseNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        var items = orderedDatabaseNames
            .Select(name =>
            {
                var database = server.Databases.FirstOrDefault(candidate => candidate.DatabaseName.Equals(name, StringComparison.OrdinalIgnoreCase));
                return database is null
                    ? $"{name} | users: <not tracked>"
                    : $"{name} | users: {database.Users.Count}";
            })
            .ToList();
        var listView = CreateListView(items, 0);
        var selection = ShowListDialog($"Databases on {server.ServerName}", listView, "Open");
        if (selection != 0)
        {
            return;
        }

        var selectedDatabaseName = orderedDatabaseNames[Math.Clamp(listView.SelectedItem ?? 0, 0, orderedDatabaseNames.Count - 1)];
        ShowDatabaseEntryDialog(server, selectedDatabaseName);
    }

    private void ShowDatabaseEntryDialog(ServerConfig server, string databaseName)
    {
        var database = server.Databases.FirstOrDefault(candidate => candidate.DatabaseName.Equals(databaseName, StringComparison.OrdinalIgnoreCase));
        var details = string.Join(Environment.NewLine,
        [
            $"Server: {server.ServerName}",
            $"Database: {databaseName}",
            $"Tracked Users: {database?.Users.Count ?? 0}",
            $"Users: {(database is null || database.Users.Count == 0 ? "<none>" : string.Join(", ", database.Users.Select(user => user.Username).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)))}"
        ]);

        var versionHistoryButton = new Button { Text = "See Version History" };
        var closeButton = new Button { Text = "Close" };
        var dialog = new Dialog
        {
            Title = $"Database Entry: {databaseName}",
            Width = 96,
            Height = 16,
            TabStop = TabBehavior.TabGroup
        };

        var textView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            ReadOnly = true,
            WordWrap = false,
            Text = details,
            TabStop = TabBehavior.TabStop
        };
        dialog.Add(textView);
        dialog.Buttons = [versionHistoryButton, closeButton];

        versionHistoryButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            ShowVersionHistoryDialog($"Database History: {databaseName}", database?.VersionHistory ?? []);
        };
        closeButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            dialog.RequestStop();
        };

        textView.SetFocus();
        RequireApp().Run(dialog);
        dialog.Dispose();
    }

    private void ShowTrashBinDialog()
    {
        if (_config.Trash.Count == 0)
        {
            MessageBox.Query(RequireApp(), "Trash Bin", "Trash is empty.", "OK");
            return;
        }

        var orderedTrash = _config.Trash.OrderByDescending(entry => entry.DeletedAtUtc, StringComparer.OrdinalIgnoreCase).ToList();
        var items = orderedTrash.Select(entry => $"{entry.EntryType} | {entry.DisplayName} | deleted {entry.DeletedAtUtc}").ToList();
        var listView = CreateListView(items, 0);
        var selection = ShowListDialog("Trash Bin", listView, "Open");
        if (selection != 0)
        {
            return;
        }

        var entry = orderedTrash[Math.Clamp(listView.SelectedItem ?? 0, 0, orderedTrash.Count - 1)];
        ShowTrashEntryDialog(entry);
    }

    private void ShowTrashEntryDialog(TrashEntry entry)
    {
        var details = string.Join(Environment.NewLine,
        [
            $"Type: {entry.EntryType}",
            $"Name: {entry.DisplayName}",
            $"Deleted: {entry.DeletedAtUtc}",
            $"Server: {(string.IsNullOrWhiteSpace(entry.ParentServerName) ? "<none>" : entry.ParentServerName)}",
            $"Database: {(string.IsNullOrWhiteSpace(entry.ParentDatabaseName) ? "<none>" : entry.ParentDatabaseName)}",
            string.Empty,
            entry.Details
        ]);

        var recoverButton = new Button { Text = "Recover" };
        var versionHistoryButton = new Button { Text = "See Version History" };
        var closeButton = new Button { Text = "Close" };
        var dialog = new Dialog
        {
            Title = $"Trash Entry: {entry.DisplayName}",
            Width = 96,
            Height = 20,
            TabStop = TabBehavior.TabGroup
        };

        var textView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            ReadOnly = true,
            WordWrap = false,
            Text = details,
            TabStop = TabBehavior.TabStop
        };
        dialog.Add(textView);
        dialog.Buttons = [recoverButton, versionHistoryButton, closeButton];

        recoverButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            var result = WaitForTaskCompletion(
                _service.RestoreTrashItemAsync(_configPath, entry.TrashId, GetConfigEncryptionPassword(), _cancellationToken),
                "Recover Trash Entry",
                "Restoring entry from trash...");
            ShowResult(result);
            if (result.Succeeded)
            {
                dialog.RequestStop();
                ReloadAndRefresh();
            }
        };
        versionHistoryButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            ShowVersionHistoryDialog($"Trash History: {entry.DisplayName}", entry.VersionHistory);
        };
        closeButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            dialog.RequestStop();
        };

        textView.SetFocus();
        RequireApp().Run(dialog);
        dialog.Dispose();
    }

    private void ShowVersionHistoryDialog(string title, IReadOnlyList<EntryVersion> versionHistory)
    {
        if (versionHistory.Count == 0)
        {
            MessageBox.Query(RequireApp(), title, "No version history is available for this entry.", "OK");
            return;
        }

        var orderedVersions = versionHistory.OrderByDescending(version => version.VersionNumber).ToList();
        var items = orderedVersions.Select(version => $"v{version.VersionNumber} | {version.ChangedAtUtc} | {version.Summary}").ToList();
        var listView = CreateListView(items, 0);
        var selection = ShowListDialog(title, listView, "Open Version");
        if (selection != 0)
        {
            return;
        }

        var version = orderedVersions[Math.Clamp(listView.SelectedItem ?? 0, 0, orderedVersions.Count - 1)];
        ShowTextDialog($"{title}: v{version.VersionNumber}", string.Join(Environment.NewLine,
        [
            $"Version: {version.VersionNumber}",
            $"Changed: {version.ChangedAtUtc}",
            $"Summary: {version.Summary}",
            string.Empty,
            version.Details
        ]));
    }

    private string? PromptDatabaseName(ServerConfig server, string title)
    {
        var databaseNames = server.Databases
            .Select(database => database.DatabaseName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (databaseNames.Count == 0)
        {
            return ShowInputDialog(title, "Database Name:", "OK");
        }

        var items = databaseNames.Append(ManualEntryChoice).ToList();
        var listView = CreateListView(items, 0);
        var selection = ShowListDialog(title, listView, "Select");
        if (selection != 0)
        {
            return null;
        }

        var selected = items[Math.Clamp(listView.SelectedItem ?? 0, 0, items.Count - 1)];
        return selected == ManualEntryChoice
            ? ShowInputDialog(title, "Database Name:", "OK")
            : selected;
    }

    private string? PromptKnownUserName(ServerConfig server, string title)
    {
        var userNames = server.Databases
            .SelectMany(database => database.Users.Select(user => user.Username))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (userNames.Count == 0)
        {
            return ShowInputDialog(title, "User Name:", "OK");
        }

        var items = userNames.Append(ManualEntryChoice).ToList();
        var listView = CreateListView(items, 0);
        var selection = ShowListDialog(title, listView, "Select");
        if (selection != 0)
        {
            return null;
        }

        var selected = items[Math.Clamp(listView.SelectedItem ?? 0, 0, items.Count - 1)];
        return selected == ManualEntryChoice
            ? ShowInputDialog(title, "User Name:", "OK")
            : selected;
    }

    private string? ShowInputDialog(string title, string label, string primaryButtonText, string backButtonText = "Back")
    {
        var field = CreateTextField();
        string? value = null;
        var primary = new Button { Text = primaryButtonText };
        var back = new Button { Text = backButtonText };
        var dialog = new Dialog
        {
            Title = title,
            Width = 72,
            Height = 10,
            TabStop = TabBehavior.TabGroup
        };
        AddField(dialog, 1, label, field);
        dialog.Buttons = [primary, back];

        primary.Accepting += (_, args) =>
        {
            args.Handled = true;
            RunGuardedUiAction(title, () =>
            {
                value = GetRequiredText(field, $"{label.TrimEnd(':')} is required.");
                dialog.RequestStop();
            });
        };
        back.Accepting += (_, args) =>
        {
            args.Handled = true;
            value = null;
            dialog.RequestStop();
        };

        field.SetFocus();
        RequireApp().Run(dialog);
        dialog.Dispose();
        return value;
    }

    private OperationExecutionResult ExecuteOperation(
        Task<OperationResult> task,
        string title = "Working",
        string message = "Running operation. Press Ctrl+C to cancel.",
        string? newActiveServer = null,
        bool selectServerAfterSuccess = false)
    {
        var result = WaitForTaskCompletion(task, title, message);
        if (result.ExitCode == 130)
        {
            _exitCode = 130;
        }

        if (result.Succeeded)
        {
            if (!string.IsNullOrWhiteSpace(newActiveServer))
            {
                _activeServer = newActiveServer;
                if (selectServerAfterSuccess)
                {
                    var selectResult = WaitForTaskCompletion(
                        _service.SelectServerAsync(new CommandOptions
                        {
                            Command = CommandKind.SelectServer,
                            ConfigPath = _configPath,
                            EncryptionPassword = GetConfigEncryptionPassword(),
                            ServerName = newActiveServer
                        }, _cancellationToken),
                        "Select Server",
                        "Updating selected server...");
                    if (!selectResult.Succeeded)
                    {
                        return OperationExecutionResult.Failure(selectResult);
                    }
                }
            }

            ReloadAndRefresh();
        }

        return OperationExecutionResult.FromResult(result);
    }

    private OperationExecutionResult ExecuteDatabaseSaveOperation(
        Task<OperationResult> task,
        string title = "Saving",
        string message = "Saving changes to the database. Press Ctrl+C to cancel.",
        string? newActiveServer = null,
        bool selectServerAfterSuccess = false)
        => ExecuteOperation(task, title, message, newActiveServer, selectServerAfterSuccess);

    private OperationExecutionResult ExecuteUserLookup(Task<OperationResult<IReadOnlyList<DatabaseUserRow>>> task)
    {
        var result = WaitForTaskCompletion(task, "Show Users", "Loading users from SQL Server. Press Ctrl+C to cancel.");
        if (result.ExitCode == 130)
        {
            _exitCode = 130;
        }

        if (result.Succeeded)
        {
            ReloadAndRefresh();
        }

        return OperationExecutionResult.FromUserResult(result);
    }

    private void ShowResult(OperationResult result)
    {
        var message = result.Details.Count == 0
            ? result.Message
            : result.Message + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, result.Details);
        if (result.Succeeded)
        {
            MessageBox.Query(RequireApp(), "SQL Manager", message, "OK");
        }
        else
        {
            MessageBox.ErrorQuery(RequireApp(), "SQL Manager", message, "OK");
        }
    }

    private void RunGuardedUiAction(string title, Action action)
    {
        try
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _exitCode = 130;
                RequireApp().RequestStop();
                return;
            }

            action();
        }
        catch (UserInputException exception)
        {
            MessageBox.ErrorQuery(RequireApp(), title, exception.Message, "OK");
        }
        catch (OperationCanceledException)
        {
            _exitCode = 130;
            MessageBox.ErrorQuery(RequireApp(), title, "Operation cancelled.", "OK");
            RequireApp().RequestStop();
        }
        catch (Exception exception)
        {
            ErrorLogger.LogException($"{title} UI action failed", exception);
            MessageBox.ErrorQuery(RequireApp(), title, $"Unexpected error: {exception.Message}", "OK");
        }
    }

    private T WaitForTaskCompletion<T>(Task<T> task, string title, string message)
    {
        if (task.IsCompleted)
        {
            return task.GetAwaiter().GetResult();
        }

        var dialog = new Dialog
        {
            Title = title,
            Width = 72,
            Height = 8,
            TabStop = TabBehavior.TabGroup
        };
        var messageLabel = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(2),
            Text = message
        };
        var statusLabel = new Label
        {
            X = 1,
            Y = 3,
            Width = Dim.Fill(2),
            Text = "Working |"
        };

        dialog.Add(messageLabel, statusLabel);

        Exception? failure = null;
        var result = default(T)!;
        var completed = false;
        var frames = new[] { '|', '/', '-', '\\' };
        var frameIndex = 0;

        RequireApp().AddTimeout(TimeSpan.FromMilliseconds(125), () =>
        {
            if (task.IsCompleted)
            {
                try
                {
                    result = task.GetAwaiter().GetResult();
                    completed = true;
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                dialog.RequestStop();
                return false;
            }

            statusLabel.Text = $"Working {frames[frameIndex++ % frames.Length]}";
            return true;
        });

        RequireApp().Run(dialog);
        dialog.Dispose();

        if (failure is not null)
        {
            throw failure;
        }

        if (!completed)
        {
            return task.GetAwaiter().GetResult();
        }

        return result;
    }

    private ServerConfig? RequireActiveServer()
    {
        var server = GetActiveServerConfig();
        if (server is null)
        {
            MessageBox.ErrorQuery(RequireApp(), "SQL Manager", "Select an active server first.", "OK");
        }

        return server;
    }

    private ServerConfig? GetActiveServerConfig()
        => _config.Servers.FirstOrDefault(server => server.ServerName.Equals(_activeServer, StringComparison.OrdinalIgnoreCase));

    private string? GetConfigEncryptionPassword()
        => _configEncryptionPassword.Reveal();

    private IApplication RequireApp()
        => _app ?? throw new InvalidOperationException("Terminal.Gui application is not initialized.");

    private void TryRequestApplicationExit()
    {
        if (!ConfirmExitApplication())
        {
            return;
        }

        _exitConfirmationApproved = true;
        RequireApp().RequestStop();
    }

    private bool ConfirmExitApplication()
        => MessageBox.Query(
            RequireApp(),
            "Exit SQL Manager",
            "Are you sure you want to exit?",
            "Stay",
            "Exit") == 1;

    private void OnMainWindowIsRunningChanging(object? sender, Terminal.Gui.App.CancelEventArgs<bool> args)
    {
        if (args.NewValue)
        {
            return;
        }

        if (_exitConfirmationApproved)
        {
            _exitConfirmationApproved = false;
            return;
        }

        args.Cancel = !ConfirmExitApplication();
    }

    private int RecordGameHighScore(string gameName, int score)
    {
        if (_gameHighScores.TryGetValue(gameName, out var highScore))
        {
            if (score <= highScore)
            {
                return highScore;
            }
        }

        _gameHighScores[gameName] = score;
        return score;
    }

    private static string ResolveActiveServerName(SqlManagerConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.SelectedServerName))
        {
            return config.SelectedServerName;
        }

        return config.Servers.Count == 1 ? config.Servers[0].ServerName : string.Empty;
    }

    private static string BuildServerLine(ServerConfig server)
    {
        var passwordState = BuildPasswordState(server.AdminPassword, server.Encrypted);
        var userCount = server.Databases.Sum(database => database.Users.Count);
        return $"{server.ServerName} | provider: {SqlProviders.GetDisplayName(server.Provider)} | admin: {(string.IsNullOrWhiteSpace(server.AdminUsername) ? "<none>" : server.AdminUsername)} | password: {passwordState} | dbs: {server.Databases.Count} | users: {userCount}";
    }

    private static string BuildPasswordState(string? password, bool encrypted)
    {
        if (encrypted)
        {
            return "encrypted";
        }

        return string.IsNullOrWhiteSpace(password) ? "missing" : "saved";
    }

    private static ListView CreateListView(IReadOnlyList<string> items, int selectedIndex)
    {
        var listView = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            TabStop = TabBehavior.TabStop
        };
        listView.SetSource(new ObservableCollection<string>(items.ToList()));
        listView.SelectedItem = Math.Clamp(selectedIndex, 0, Math.Max(0, items.Count - 1));
        return listView;
    }

    private static TextField CreateTextField(string? value = null)
        => new TextField
        {
            Text = value ?? string.Empty,
            X = 24,
            Width = 54,
            TabStop = TabBehavior.TabStop
        };

    private static TextField CreateSecretField(string? value = null)
        => new TextField
        {
            Text = value ?? string.Empty,
            X = 24,
            Width = 54,
            Secret = true,
            TabStop = TabBehavior.TabStop
        };

    private static void AddField(Dialog dialog, int y, string label, View field)
    {
        dialog.Add(new Label { X = 1, Y = y, Text = label });
        field.Y = y;
        dialog.Add(field);
    }

    private void AddPasswordFieldWithGenerateButton(Dialog dialog, int y, string label, TextField field, string title)
    {
        AddField(dialog, y, label, field);
        field.Width = 42;

        var generateButton = new Button
        {
            X = Pos.Right(field) + 1,
            Y = y,
            Width = 12,
            Text = "Generate",
            TabStop = TabBehavior.TabStop
        };

        generateButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            RunGuardedUiAction(title, () =>
            {
                var generatedPassword = _passwordGenerator.Generate();
                field.Text = generatedPassword;
                field.SetFocus();
                MessageBox.Query(
                    RequireApp(),
                    title,
                    $"Generated password:{Environment.NewLine}{Environment.NewLine}{generatedPassword}",
                    "OK");
            });
        };

        dialog.Add(generateButton);
    }

    private static int AddAdminCredentialSection(Dialog dialog, int y, ServerConfig server, TextField adminUserField, TextField adminPasswordField)
    {
        if (HasStoredAdminCredentials(server))
        {
            dialog.Add(new Label
            {
                X = 1,
                Y = y,
                Width = Dim.Fill(2),
                Text = $"Using admin credentials from config ({server.AdminUsername})."
            });
            return y + 2;
        }

        AddField(dialog, y, "Admin User:", adminUserField);
        AddField(dialog, y + 2, "Admin Password:", adminPasswordField);
        return y + 4;
    }

    private static View? FindPreferredInitialFocus(View root)
    {
        foreach (var subview in root.SubViews)
        {
            var candidate = FindPreferredInitialFocusInSubview(subview);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static View? FindPreferredInitialFocusInSubview(View view)
    {
        foreach (var subview in view.SubViews)
        {
            var candidate = FindPreferredInitialFocusInSubview(subview);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return IsKeyboardFocusable(view) ? view : null;
    }

    private static bool IsKeyboardFocusable(View view)
        => view.Visible
            && view.Enabled
            && view.CanFocus
            && view.TabStop is not null and not TabBehavior.NoStop;

    private static View CreateScrollableViewport(int viewportHeight, int contentHeight)
    {
        var viewport = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true,
            TabStop = TabBehavior.TabGroup
        };
        viewport.ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        viewport.SetContentSize(new Size(64, Math.Max(viewportHeight, contentHeight)));
        return viewport;
    }

    private static string GetText(TextField field)
        => field.Text?.ToString()?.Trim() ?? string.Empty;

    private static string GetRequiredText(TextField field, string error)
    {
        var value = GetText(field);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UserInputException(error);
        }

        return value;
    }

    private static int? ParseOptionalPort(TextField field)
    {
        var text = GetText(field);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!int.TryParse(text, out var port) || port <= 0)
        {
            throw new UserInputException("Port must be a positive integer.");
        }

        return port;
    }

    private static bool HasStoredAdminCredentials(ServerConfig server)
        => !string.IsNullOrWhiteSpace(server.AdminUsername)
            && !string.IsNullOrWhiteSpace(server.AdminPassword);

    private static string? ResolveAdminUsername(ServerConfig server, TextField adminUserField)
        => HasStoredAdminCredentials(server)
            ? null
            : GetRequiredText(adminUserField, "Admin user is required.");

    private static string? ResolveAdminPassword(ServerConfig server, TextField adminPasswordField)
        => HasStoredAdminCredentials(server)
            ? null
            : GetText(adminPasswordField);

    private static Dictionary<string, HashSet<string>> BuildRoleLookup(ServerConfig server, string? userName)
    {
        var lookup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(userName))
        {
            return lookup;
        }

        foreach (var database in server.Databases)
        {
            var user = database.Users.FirstOrDefault(candidate => candidate.Username.Equals(userName, StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                continue;
            }

            lookup[database.DatabaseName] = user.Roles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return lookup;
    }

    private sealed class OperationExecutionResult
    {
        public bool Succeeded { get; init; }
        public OperationResult? Result { get; init; }
        public OperationResult<IReadOnlyList<DatabaseUserRow>>? UserResult { get; init; }

        public static OperationExecutionResult FromResult(OperationResult result)
            => new() { Succeeded = result.Succeeded, Result = result };

        public static OperationExecutionResult FromUserResult(OperationResult<IReadOnlyList<DatabaseUserRow>> result)
            => new() { Succeeded = result.Succeeded, UserResult = result };

        public static OperationExecutionResult Failure(OperationResult result)
            => new() { Succeeded = false, Result = result };
    }

    private sealed record DatabaseRoleRow(string DatabaseName, CheckBox OwnerCheckBox, CheckBox ReaderCheckBox, CheckBox WriterCheckBox)
    {
        public DatabaseRoleAssignment ToAssignment()
        {
            var roles = new List<string>();
            if (OwnerCheckBox.Value == CheckState.Checked)
            {
                roles.Add("db_owner");
            }

            if (ReaderCheckBox.Value == CheckState.Checked)
            {
                roles.Add("db_datareader");
            }

            if (WriterCheckBox.Value == CheckState.Checked)
            {
                roles.Add("db_datawriter");
            }

            return new DatabaseRoleAssignment(DatabaseName, roles);
        }
    }

    private sealed record DatabaseSelectionRow(string DatabaseName, CheckBox CheckBox);
}
