using System.Drawing;
using System.Collections.ObjectModel;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using GuiScheme = Terminal.Gui.Drawing.Scheme;

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
    private Window? _mainWindow;
    private MenuBar? _mainMenuBar;
    private Label? _activeServerLabel;
    private Button? _initialMainButton;
    private readonly List<IReadOnlyList<Button>> _mainMenuColumns = [];
    private readonly Dictionary<Button, (int ColumnIndex, int RowIndex)> _mainMenuButtonPositions = [];
    private readonly Dictionary<TerminalThemeSurface, GuiScheme> _themeSchemes = [];
    private bool _exitConfirmationApproved;
    private bool _exitPromptOpen;
    private bool _exitPromptQueued;
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
        _exitConfirmationApproved = false;
        _exitPromptOpen = false;
        _exitPromptQueued = false;

        IApplication app = Application.Create();
        try
        {
            app.Init();
            _app = app;
            ConfigureTheme(app, null);

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

            var summary = WaitForTaskCompletion(
                _service.LoadConfigSummaryAsync(configPath, GetConfigEncryptionPassword(), cancellationToken),
                "Loading Config",
                "Loading configuration...");
            if (!summary.Succeeded || summary.Value is null)
            {
                ShowErrorDialog("SQL Manager", summary.Message);
                return summary.ExitCode;
            }

            _config = summary.Value;
            _activeServer = ResolveActiveServerName(_config);
            ConfigureTheme(app, _config.ThemeName);

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
            _mainWindow = window;
            ApplyTheme(window, TerminalThemeSurface.Runnable);
            window.IsRunningChanging += OnMainWindowIsRunningChanging;
            window.KeyDown += OnMainWindowKeyDown;

            BuildMainWindow(window);
            RefreshViews();
            _initialMainButton?.SetFocus();
            app.Run(window);
            return _exitCode;
        }
        finally
        {
            _mainWindow = null;
            _mainMenuBar = null;
            _app = null;
            app.Dispose();
            _configEncryptionPassword.Dispose();
            TryClearTerminal();
        }
    }

    private void BuildMainWindow(Window window)
    {
        var menuBar = BuildMenuBar();
        _mainMenuBar = menuBar;
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
            ("Test User Login", (Action)ShowTestUserLoginDialog),
            ("Remove User", (Action)ShowRemoveUserDialog),
            ("Update Password", (Action)ShowUpdatePasswordDialog)
        };

        _mainMenuColumns.Add(AddMenuColumn(window, 0, "Server Management", 1, 4, Dim.Percent(50) - 2, serverActions));
        _mainMenuColumns.Add(AddMenuColumn(window, 1, "User Management", Pos.Percent(50) + 1, 4, Dim.Fill(2), userActions));

        window.Add(menuBar, _activeServerLabel, instructionsLabel);
    }

    private MenuBar BuildMenuBar()
    {
        var menuBar = new MenuBar([
            new MenuBarItem("_File", new PopoverMenu([
                new MenuItem("_Save", "Ctrl+S", SaveCurrentConfig, Key.S.WithCtrl),
                new MenuItem("E_xit", "Ctrl+Q", TryRequestApplicationExit, Key.Q.WithCtrl)
            ])),
            new MenuBarItem("_Configuration", new PopoverMenu([
                new MenuItem("_View Config", string.Empty, ShowConfigDialog, default),
                new MenuItem("_Initialize Config", string.Empty, ShowInitializeConfigDialog, default),
                new MenuItem("_Theme", string.Empty, ShowThemeSelectionDialog, default),
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

        ApplyTheme(menuBar, TerminalThemeSurface.Menu);
        return menuBar;
    }

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
        if (key.NoCtrl.NoAlt.NoShift == Key.Esc)
        {
            key.Handled = true;
            TryRequestApplicationExit();
            return;
        }

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

        _activeServerLabel.Text = $"Active server: {GetActiveServerLabel()}";
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
            || ServerConnections.FindBySelectionKey(_config.Servers, _activeServer) is null)
        {
            _activeServer = ResolveActiveServerName(_config);
        }

        ConfigureTheme(RequireApp(), _config.ThemeName);
        RefreshMainTheme();
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
            $"Selected Server: {GetSelectedServerLabel(_config)}",
            $"Theme: {_config.ThemeName}",
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

    private void ShowThemeSelectionDialog()
    {
        var themeNames = TerminalThemeCatalog.GetThemeNames();
        var selectedIndex = Math.Max(0, themeNames.IndexOf(_config.ThemeName));
        var listView = CreateListView(themeNames, selectedIndex);
        var selection = ShowListDialog("Theme", listView, "Apply");
        if (selection != 0)
        {
            return;
        }

        var themeName = themeNames[Math.Clamp(listView.SelectedItem ?? selectedIndex, 0, themeNames.Count - 1)];
        if (string.Equals(themeName, _config.ThemeName, StringComparison.OrdinalIgnoreCase))
        {
            ShowInfoDialog("Theme", $"Theme is already set to '{themeName}'.");
            return;
        }

        var result = WaitForTaskCompletion(
            _service.UpdateThemePreferenceAsync(_configPath, GetConfigEncryptionPassword(), themeName, _cancellationToken),
            "Theme",
            "Saving theme preference...");
        if (result.Succeeded)
        {
            _config.ThemeName = TerminalThemeCatalog.NormalizeThemeName(themeName);
            ConfigureTheme(RequireApp(), _config.ThemeName);
            RefreshMainTheme();
        }

        ShowResult(result);
    }

    private void ShowAboutDialog()
    {
        var repoUrlField = new TextField
        {
            X = 1,
            Y = 8,
            Width = Dim.Fill(1),
            Text = AppVersion.RepositoryUrl
        };
        ConfigureSelectableReadOnlyField(repoUrlField);
        var copyButton = new Button { Text = "Copy URL (Ctrl+C)" };
        var closeButton = new Button { Text = "Close (Esc)" };
        var dialog = new Dialog
        {
            Title = "About SQL Manager",
            Width = 96,
            Height = 24,
            TabStop = TabBehavior.TabGroup
        };
        ApplyTheme(dialog, TerminalThemeSurface.Dialog);
        var textView = CreateSelectableReadOnlyTextView(BuildAboutSummaryText(), wordWrap: true);
        textView.X = 1;
        textView.Y = 0;
        textView.Width = Dim.Fill(1);
        textView.Height = 14;
        var repoUrlLabel = new Label
        {
            X = 1,
            Y = 14,
            Text = "GitHub Repository (selectable, Ctrl+C to copy):"
        };

        dialog.Add(textView, repoUrlLabel, repoUrlField);
        dialog.Buttons = [copyButton, closeButton];

        void CopyRepoUrl()
        {
            var clipboard = RequireApp().Clipboard;
            if (clipboard is not null && clipboard.TrySetClipboardData(AppVersion.RepositoryUrl))
            {
                ShowInfoDialog("About SQL Manager", "Repository URL copied to clipboard.");
                return;
            }

            ShowErrorDialog("About SQL Manager", "Clipboard is not available. Select the URL field and copy it manually.");
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
        ConfigureEscapeToClose(dialog, dialog.RequestStop);
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
        ApplyTheme(dialog, TerminalThemeSurface.Dialog);

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
        ApplyTheme(dialog, TerminalThemeSurface.Dialog);

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
        ApplyTheme(dialog, TerminalThemeSurface.Dialog);

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
            var action = ShowChoiceDialog(
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
                ShowInfoDialog("Encrypt Passwords", "Stored passwords are locked again for this session.");
            }

            return;
        }

        var lockedAction = ShowChoiceDialog(
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
            ShowErrorDialog("Select Active Server", "No servers are configured.");
            return;
        }

        var items = _config.Servers.Select(BuildServerLine).ToList();
        var listView = CreateListView(items, Math.Max(0, _config.Servers.FindIndex(server => ServerConnections.GetIdentifier(server).Equals(_activeServer, StringComparison.OrdinalIgnoreCase))));

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
                ServerIdentifier = ServerConnections.GetIdentifier(selectedServer)
            }, _cancellationToken),
            "Select Active Server",
            "Saving selected server...");

        if (result.Succeeded)
        {
            _activeServer = ServerConnections.GetIdentifier(selectedServer);
            ReloadAndRefresh();
        }

        ShowResult(result);
    }

    private void ShowInitializeConfigDialog()
    {
        var activeServerConfig = GetActiveServerConfig();
        var serverField = CreateTextField(activeServerConfig?.ServerName);
        var adminUserField = CreateTextField(activeServerConfig?.AdminUsername);
        var adminPasswordField = CreateSecretField(activeServerConfig?.AdminPassword);

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
            ShowErrorDialog("Edit Server", "No servers are configured.");
            return;
        }

        var items = _config.Servers.Select(BuildServerLine).ToList();
        var listView = CreateListView(items, Math.Max(0, _config.Servers.FindIndex(server => ServerConnections.GetIdentifier(server).Equals(_activeServer, StringComparison.OrdinalIgnoreCase))));
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
        const int serverEditorFieldX = 41;
        const int serverEditorFieldWidth = 55;
        const int serverEditorValueWidth = 28;
        const int serverEditorChooserX = 73;

        var isEdit = existingServer is not null;
        var selectedProvider = SqlProviders.Normalize(existingServer?.Provider);
        var identifierField = CreateTextField(existingServer is null
            ? ServerConnections.GetNextIdentifier(_config.Servers)
            : ServerConnections.GetIdentifier(existingServer));
        var displayNameField = CreateTextField(existingServer is null ? string.Empty : ServerConnections.GetDisplayName(existingServer));
        var serverField = CreateTextField(existingServer?.ServerName);
        var portField = CreateTextField(existingServer?.Port?.ToString());
        var adminDatabaseField = CreateTextField(string.IsNullOrWhiteSpace(existingServer?.AdminDatabase)
            ? SqlProviders.GetDefaultAdminDatabase(selectedProvider)
            : existingServer!.AdminDatabase);
        var adminUserField = CreateTextField(existingServer?.AdminUsername);
        var adminPasswordField = CreateSecretField(existingServer?.AdminPassword);
        var connectionTimeoutField = CreateTextField(
            existingServer?.ConnectionTimeoutSeconds?.ToString()
            ?? SqlTimeoutConfig.DefaultConnectionTimeoutSeconds.ToString());
        var commandTimeoutField = CreateTextField(
            existingServer?.CommandTimeoutSeconds?.ToString()
            ?? SqlTimeoutConfig.DefaultCommandTimeoutSeconds.ToString());
        var poolingCheckBox = new CheckBox
        {
            X = 24,
            Y = 19,
            Text = "Enable connection pooling",
            Value = ServerConnectionOptions.GetEffectivePostgreSqlPooling(existingServer?.PostgreSqlPooling)
                ? CheckState.Checked
                : CheckState.UnChecked,
            TabStop = TabBehavior.TabStop
        };
        var selectedPostgreSqlSslMode = isEdit
            ? PostgreSqlSslModes.GetEffective(existingServer?.PostgreSqlSslMode)
            : PostgreSqlSslModes.GetDefaultForNewServers();
        var selectedSqlServerTrustMode = isEdit
            ? SqlServerTrustModes.GetEffective(existingServer?.SqlServerTrustMode)
            : SqlServerTrustModes.GetDefaultForNewServers();

        var identifierLabel = new Label { X = 1, Y = 1, Text = "Connection Identifier:" };
        var displayNameLabel = new Label { X = 1, Y = 3, Text = "Display Name:" };
        var serverLabel = new Label { X = 1, Y = 5 };
        var providerLabel = new Label { X = 1, Y = 7, Text = "Server Type:" };
        var providerValueLabel = new Label { X = 24, Y = 7, Width = 20 };
        var changeProviderButton = new Button
        {
            X = 46,
            Y = 7,
            Width = 11,
            Text = "Choose",
            TabStop = TabBehavior.TabStop
        };
        var portLabel = new Label { X = 1, Y = 9 };
        var adminDatabaseLabel = new Label { X = 1, Y = 11 };
        var adminUserLabel = new Label { X = 1, Y = 13 };
        var securityModeLabel = new Label { X = 1, Y = 17 };
        var securityModeValueLabel = new Label { X = 24, Y = 17, Width = 20 };
        var changeSecurityModeButton = new Button
        {
            X = 46,
            Y = 17,
            Width = 11,
            Text = "Choose",
            TabStop = TabBehavior.TabStop
        };
        var connectionTimeoutLabel = new Label { X = 1, Y = 19 };
        var commandTimeoutLabel = new Label { X = 1, Y = 21 };
        var providerHintLabel = new Label
        {
            X = 1,
            Y = 25,
            Width = Dim.Fill(2),
            Height = 2
        };

        var saveButton = new Button { Text = isEdit ? "Save" : "Add" };
        var testConnectionButton = new Button { Text = "Test Connection" };
        var showPasswordButton = new Button { Text = "Show Password" };
        var versionHistoryButton = new Button { Text = "See Version History" };
        var backButton = new Button { Text = "Back" };
        var dialog = new Dialog
        {
            Title = isEdit ? $"Edit Server: {ServerConnections.GetSelectionDisplay(existingServer!)}" : "Add Server",
            Width = 104,
            Height = 32,
            TabStop = TabBehavior.TabGroup
        };
        ApplyTheme(dialog, TerminalThemeSurface.Dialog);

        identifierField.X = serverEditorFieldX;
        identifierField.Width = serverEditorFieldWidth;
        identifierField.Y = 1;
        ConfigureSelectableReadOnlyField(identifierField);
        displayNameField.X = serverEditorFieldX;
        displayNameField.Width = serverEditorFieldWidth;
        displayNameField.Y = 3;
        serverField.X = serverEditorFieldX;
        serverField.Width = serverEditorFieldWidth;
        serverField.Y = 5;
        providerValueLabel.X = serverEditorFieldX;
        providerValueLabel.Width = serverEditorValueWidth;
        changeProviderButton.X = serverEditorChooserX;
        portField.X = serverEditorFieldX;
        portField.Width = serverEditorFieldWidth;
        portField.Y = 9;
        adminDatabaseField.X = serverEditorFieldX;
        adminDatabaseField.Width = serverEditorFieldWidth;
        adminDatabaseField.Y = 11;
        adminUserField.X = serverEditorFieldX;
        adminUserField.Width = serverEditorFieldWidth;
        adminUserField.Y = 13;
        adminPasswordField.X = serverEditorFieldX;
        adminPasswordField.Width = serverEditorFieldWidth;
        adminPasswordField.Y = 15;
        securityModeValueLabel.X = serverEditorFieldX;
        securityModeValueLabel.Width = serverEditorValueWidth;
        changeSecurityModeButton.X = serverEditorChooserX;
        connectionTimeoutField.X = serverEditorFieldX;
        connectionTimeoutField.Width = serverEditorFieldWidth;
        connectionTimeoutField.Y = 19;
        commandTimeoutField.X = serverEditorFieldX;
        commandTimeoutField.Width = serverEditorFieldWidth;
        commandTimeoutField.Y = 21;
        poolingCheckBox.X = serverEditorFieldX;
        poolingCheckBox.Y = 23;

        dialog.Add(
            identifierLabel,
            identifierField,
            displayNameLabel,
            displayNameField,
            serverLabel,
            serverField,
            providerLabel,
            providerValueLabel,
            changeProviderButton,
            portLabel,
            portField,
            adminDatabaseLabel,
            adminDatabaseField,
            adminUserLabel,
            adminUserField,
            securityModeLabel,
            securityModeValueLabel,
            changeSecurityModeButton,
            connectionTimeoutLabel,
            connectionTimeoutField,
            commandTimeoutLabel,
            commandTimeoutField,
            poolingCheckBox);
        AddField(dialog, 15, "Admin Password:", adminPasswordField);
        dialog.Add(providerHintLabel);
        dialog.Buttons = isEdit
            ? [saveButton, testConnectionButton, showPasswordButton, versionHistoryButton, backButton]
            : [saveButton, testConnectionButton, backButton];

        CommandOptions BuildDraftServerOptions(bool requirePassword)
        {
            var adminUser = GetRequiredText(adminUserField, "Admin user is required.");
            var provider = selectedProvider;
            var command = new CommandOptions
            {
                Command = CommandKind.AddServer,
                ConfigPath = _configPath,
                EncryptionPassword = GetConfigEncryptionPassword(),
                ServerIdentifier = GetRequiredText(identifierField, "Connection identifier is required."),
                DisplayName = GetRequiredText(displayNameField, "Display name is required."),
                ServerName = GetRequiredText(serverField, "Server name is required."),
                Provider = provider,
                Port = ParseOptionalPort(portField),
                AdminDatabase = GetText(adminDatabaseField),
                AdminUsername = adminUser,
                AdminPassword = requirePassword
                    ? GetRequiredText(adminPasswordField, "Admin password is required to test the connection.")
                    : GetText(adminPasswordField)
            };

            if (provider == SqlProviders.PostgreSql)
            {
                command.PostgreSqlSslMode = selectedPostgreSqlSslMode;
                command.PostgreSqlPooling = poolingCheckBox.Value == CheckState.Checked;
                command.ConnectionTimeoutSeconds = ParseOptionalPositiveInteger(connectionTimeoutField, "Connection timeout must be a positive integer.");
                command.CommandTimeoutSeconds = ParseOptionalPositiveInteger(commandTimeoutField, "Command timeout must be a positive integer.");
            }
            else
            {
                command.SqlServerTrustMode = selectedSqlServerTrustMode;
            }

            return command;
        }

        void RefreshProviderFields()
        {
            serverLabel.Text = BuildServerEditorServerLabel(selectedProvider);
            providerValueLabel.Text = SqlProviders.GetDisplayName(selectedProvider);
            portLabel.Text = BuildServerEditorPortLabel(selectedProvider);
            adminDatabaseLabel.Text = BuildServerEditorAdminDatabaseLabel(selectedProvider);
            adminUserLabel.Text = BuildServerEditorAdminUserLabel(selectedProvider);
            securityModeLabel.Text = selectedProvider == SqlProviders.PostgreSql
                ? "SSL Mode:"
                : "Encrypt / Trust Mode:";
            securityModeValueLabel.Text = selectedProvider == SqlProviders.PostgreSql
                ? PostgreSqlSslModes.GetPickerDisplayName(selectedPostgreSqlSslMode)
                : SqlServerTrustModes.GetPickerDisplayName(selectedSqlServerTrustMode);
            var showPostgreSqlOptions = selectedProvider == SqlProviders.PostgreSql;
            connectionTimeoutLabel.Text = "Timeout:";
            commandTimeoutLabel.Text = "Command Timeout:";
            connectionTimeoutLabel.Visible = showPostgreSqlOptions;
            connectionTimeoutField.Visible = showPostgreSqlOptions;
            commandTimeoutLabel.Visible = showPostgreSqlOptions;
            commandTimeoutField.Visible = showPostgreSqlOptions;
            poolingCheckBox.Visible = showPostgreSqlOptions;
            providerHintLabel.Text = BuildServerEditorProviderHint(selectedProvider);
        }

        RefreshProviderFields();

        OperationExecutionResult? result = null;
        changeProviderButton.Accepting += (_, args) =>
        {
            args.Handled = true;

            var providerItems = new[] { "SQL Server", "PostgreSQL" };
            var providerListView = CreateListView(providerItems, selectedProvider == SqlProviders.PostgreSql ? 1 : 0);
            var selection = ShowListDialog("Server Type", providerListView, "Select");
            if (selection != 0)
            {
                return;
            }

            var previousProvider = selectedProvider;
            selectedProvider = (providerListView.SelectedItem ?? 0) == 1
                ? SqlProviders.PostgreSql
                : SqlProviders.SqlServer;

            if (!string.Equals(previousProvider, selectedProvider, StringComparison.OrdinalIgnoreCase))
            {
                var oldDefaultDatabase = SqlProviders.GetDefaultAdminDatabase(previousProvider);
                if (string.IsNullOrWhiteSpace(GetText(adminDatabaseField))
                    || GetText(adminDatabaseField).Equals(oldDefaultDatabase, StringComparison.OrdinalIgnoreCase))
                {
                    adminDatabaseField.Text = SqlProviders.GetDefaultAdminDatabase(selectedProvider);
                }
            }

            RefreshProviderFields();
        };
        changeSecurityModeButton.Accepting += (_, args) =>
        {
            args.Handled = true;

            if (selectedProvider == SqlProviders.PostgreSql)
            {
                var items = PostgreSqlSslModes.Choices
                    .Select(PostgreSqlSslModes.GetPickerDisplayName)
                    .ToList();
                var sslModeListView = CreateListView(items, Math.Max(0, PostgreSqlSslModes.Choices.IndexOf(selectedPostgreSqlSslMode)));
                var selection = ShowListDialog("PostgreSQL SSL Mode", sslModeListView, "Select");
                if (selection != 0)
                {
                    return;
                }

                selectedPostgreSqlSslMode = PostgreSqlSslModes.Choices[Math.Clamp(sslModeListView.SelectedItem ?? 0, 0, PostgreSqlSslModes.Choices.Count - 1)];
            }
            else
            {
                var items = SqlServerTrustModes.Choices
                    .Select(SqlServerTrustModes.GetPickerDisplayName)
                    .ToList();
                var trustModeListView = CreateListView(items, Math.Max(0, SqlServerTrustModes.Choices.IndexOf(selectedSqlServerTrustMode)));
                var selection = ShowListDialog("SQL Server Encrypt / Trust Mode", trustModeListView, "Select");
                if (selection != 0)
                {
                    return;
                }

                selectedSqlServerTrustMode = SqlServerTrustModes.Choices[Math.Clamp(trustModeListView.SelectedItem ?? 0, 0, SqlServerTrustModes.Choices.Count - 1)];
            }

            RefreshProviderFields();
        };
        saveButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            RunGuardedUiAction(dialog.Title?.ToString() ?? "Server", () =>
            {
                var draft = BuildDraftServerOptions(requirePassword: false);
                var serverIdentifier = draft.ServerIdentifier!;

                result = isEdit
                    ? ExecuteOperation(
                        _service.UpdateServerAsync(
                            _configPath,
                            ServerConnections.GetIdentifier(existingServer!),
                            draft.DisplayName!,
                            draft.ServerName!,
                            draft.Provider,
                            draft.Port,
                            draft.AdminDatabase,
                            draft.AdminUsername!,
                            draft.AdminPassword,
                            draft.PostgreSqlSslMode,
                            draft.PostgreSqlPooling,
                            draft.SqlServerTrustMode,
                            draft.ConnectionTimeoutSeconds,
                            draft.CommandTimeoutSeconds,
                            GetConfigEncryptionPassword(),
                            _cancellationToken),
                        title: "Edit Server",
                        newActiveServer: serverIdentifier,
                        selectServerAfterSuccess: true)
                    : ExecuteOperation(
                        _service.AddServerAsync(new CommandOptions
                        {
                            Command = CommandKind.AddServer,
                            ConfigPath = draft.ConfigPath,
                            EncryptionPassword = draft.EncryptionPassword,
                            ServerIdentifier = draft.ServerIdentifier,
                            DisplayName = draft.DisplayName,
                            ServerName = draft.ServerName,
                            Provider = draft.Provider,
                            Port = draft.Port,
                            AdminDatabase = draft.AdminDatabase,
                            AdminUsername = draft.AdminUsername,
                            AdminPassword = draft.AdminPassword,
                            PostgreSqlSslMode = draft.PostgreSqlSslMode,
                            PostgreSqlPooling = draft.PostgreSqlPooling,
                            SqlServerTrustMode = draft.SqlServerTrustMode,
                            ConnectionTimeoutSeconds = draft.ConnectionTimeoutSeconds,
                            CommandTimeoutSeconds = draft.CommandTimeoutSeconds
                        }, _cancellationToken),
                        title: "Add Server",
                        newActiveServer: serverIdentifier,
                        selectServerAfterSuccess: true);

                if (result is not null && !result.Succeeded && result.Result is not null)
                {
                    ShowResult(result.Result);
                    return;
                }

                dialog.RequestStop();
            });
        };
        testConnectionButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            RunGuardedUiAction("Test Connection", () =>
            {
                var draft = BuildDraftServerOptions(requirePassword: true);
                var operation = WaitForTaskCompletion(
                    _service.TestServerConnectionAsync(draft, _cancellationToken),
                    "Test Connection",
                    "Testing the server connection. Press Ctrl+C to cancel.");
                if (operation.ExitCode == 130)
                {
                    _exitCode = 130;
                }

                ShowResult(operation);
            });
        };
        showPasswordButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            var password = GetText(adminPasswordField);
            if (string.IsNullOrWhiteSpace(password))
            {
                ShowErrorDialog("Show Password", "Password is not available in the current config view.");
                return;
            }

            ShowTextDialog($"Password: {GetText(displayNameField)}", password);
        };
        versionHistoryButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            if (existingServer is not null)
            {
                ShowVersionHistoryDialog($"Server History: {ServerConnections.GetSelectionDisplay(existingServer)}", existingServer.VersionHistory);
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
                    ServerIdentifier = ServerConnections.GetIdentifier(server),
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
                ServerIdentifier = ServerConnections.GetIdentifier(server),
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
                ServerIdentifier = ServerConnections.GetIdentifier(server),
                ServerName = server.ServerName,
                AdminUsername = ResolveAdminUsername(server, adminUserField),
                AdminPassword = ResolveAdminPassword(server, adminPasswordField)
            }, _cancellationToken),
            "Show Databases",
            $"Loading databases from {SqlProviders.GetDisplayName(server.Provider)}. Press Ctrl+C to cancel.");

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
                ServerIdentifier = ServerConnections.GetIdentifier(server),
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
            ShowErrorDialog("Create User", "No configured databases were found. Sync the server or create a database first.");
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
            ShowErrorDialog("Manage Roles", "No configured databases were found. Sync the server or create a database first.");
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
                ServerIdentifier = ServerConnections.GetIdentifier(server),
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

    private void OnMainWindowKeyDown(object? sender, Key key)
    {
        if (key.NoCtrl.NoAlt.NoShift != Key.Esc)
        {
            return;
        }

        key.Handled = true;
        TryRequestApplicationExit();
    }

    private void ShowTestUserLoginDialog()
    {
        var server = RequireActiveServer();
        if (server is null)
        {
            return;
        }

        var databaseName = PromptDatabaseName(server, "Test User Login");
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return;
        }

        var userName = PromptKnownUserName(server, "Test User Login", databaseName);
        if (string.IsNullOrWhiteSpace(userName))
        {
            return;
        }

        var passwordField = CreateSecretField(GetConfiguredUserPassword(server, databaseName, userName));
        var result = ShowServerDialog(
            "Test User Login",
            server,
            14,
            "Test Login",
            dialog =>
            {
                dialog.Add(new Label { X = 1, Y = 1, Text = $"Database: {databaseName}" });
                dialog.Add(new Label { X = 1, Y = 3, Text = $"User: {userName}" });
                AddField(dialog, 5, "Password:", passwordField);
            },
            () => ExecuteDatabaseSaveOperation(
                _service.TestUserLoginAsync(new CommandOptions
                {
                    Command = CommandKind.TestUserLogin,
                    ConfigPath = _configPath,
                    EncryptionPassword = GetConfigEncryptionPassword(),
                    ServerIdentifier = ServerConnections.GetIdentifier(server),
                    ServerName = server.ServerName,
                    DatabaseName = databaseName,
                    UserName = userName,
                    NewUserPassword = GetText(passwordField)
                }, _cancellationToken),
                title: "Test User Login",
                message: "Testing user login..."));

        if (result?.Result is not null)
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
                    ServerIdentifier = ServerConnections.GetIdentifier(server),
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
                ServerIdentifier = ServerConnections.GetIdentifier(server),
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
        var supportsReadWriteRoles = SqlProviders.Normalize(server.Provider) == SqlProviders.SqlServer;

        var matrixViewportHeight = Math.Min(10, Math.Max(5, server.Databases.Count + 1));
        var dialogHeight = Math.Min((includePasswordField ? 19 : 18) + matrixViewportHeight, 30);
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
                    currentY += 3;
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
                if (supportsReadWriteRoles)
                {
                    matrixViewport.Add(new Label { X = 42, Y = 0, Text = "Reader" });
                    matrixViewport.Add(new Label { X = 55, Y = 0, Text = "Writer" });
                }

                var rowY = 1;
                foreach (var database in server.Databases.OrderBy(database => database.DatabaseName, StringComparer.OrdinalIgnoreCase))
                {
                    var roles = existingRoleLookup.TryGetValue(database.DatabaseName, out var storedRoles)
                        ? storedRoles
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var row = new DatabaseRoleRow(
                        database.DatabaseName,
                        new CheckBox { X = 30, Y = rowY, Width = 3, Height = 1, Text = string.Empty, Value = roles.Contains("db_owner") ? CheckState.Checked : CheckState.UnChecked, TabStop = TabBehavior.TabStop },
                        supportsReadWriteRoles ? new CheckBox { X = 42, Y = rowY, Width = 3, Height = 1, Text = string.Empty, Value = roles.Contains("db_datareader") ? CheckState.Checked : CheckState.UnChecked, TabStop = TabBehavior.TabStop } : null,
                        supportsReadWriteRoles ? new CheckBox { X = 55, Y = rowY, Width = 3, Height = 1, Text = string.Empty, Value = roles.Contains("db_datawriter") ? CheckState.Checked : CheckState.UnChecked, TabStop = TabBehavior.TabStop } : null);
                    matrixViewport.Add(new Label { X = 1, Y = rowY, Width = 27, Text = database.DatabaseName });
                    matrixViewport.Add(row.OwnerCheckBox);
                    if (row.ReaderCheckBox is not null)
                    {
                        matrixViewport.Add(row.ReaderCheckBox);
                    }

                    if (row.WriterCheckBox is not null)
                    {
                        matrixViewport.Add(row.WriterCheckBox);
                    }

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
                    ServerIdentifier = ServerConnections.GetIdentifier(server),
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
        ApplyTheme(dialog, TerminalThemeSurface.Dialog);
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
        ConfigureEscapeToClose(dialog, () =>
        {
            result = null;
            dialog.RequestStop();
        });

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
            dialog.Add(new Label { X = 1, Y = 0, Text = $"Connection: {ServerConnections.GetSelectionDisplay(server)} | Host: {server.ServerName}" });
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
        ApplyTheme(dialog, TerminalThemeSurface.Dialog);
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
        ConfigureEscapeToClose(dialog, () =>
        {
            result = 1;
            dialog.RequestStop();
        });

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
        ApplyTheme(dialog, TerminalThemeSurface.Dialog);
        var textView = CreateSelectableReadOnlyTextView(text);
        textView.X = 0;
        textView.Y = 0;
        textView.Width = Dim.Fill();
        textView.Height = Dim.Fill(1);
        dialog.Add(textView);
        dialog.Buttons = [closeButton];
        closeButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            dialog.RequestStop();
        };
        ConfigureEscapeToClose(dialog, dialog.RequestStop);
        textView.SetFocus();
        RequireApp().Run(dialog);
        dialog.Dispose();
    }

    private int ShowChoiceDialog(string title, string text, params string[] buttons)
        => ShowChoiceDialog(title, text, TerminalThemeSurface.Dialog, buttons);

    private int ShowChoiceDialog(string title, string text, TerminalThemeSurface surface, params string[] buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        if (buttons.Length == 0)
        {
            throw new ArgumentException("At least one button is required.", nameof(buttons));
        }

        var size = CalculateMessageDialogSize(text);
        var dialog = new Dialog
        {
            Title = title,
            Width = size.Width,
            Height = size.Height,
            TabStop = TabBehavior.TabGroup
        };
        ApplyTheme(dialog, surface);
        var textView = CreateSelectableReadOnlyTextView(text, wordWrap: true);
        textView.X = 0;
        textView.Y = 0;
        textView.Width = Dim.Fill();
        textView.Height = Dim.Fill(1);
        dialog.Add(textView);

        var buttonViews = buttons
            .Select((label, index) => new Button
            {
                Text = label,
                IsDefault = index == 0
            })
            .ToArray();
        dialog.Buttons = buttonViews;
        dialog.DefaultAcceptView = buttonViews[0];

        var result = -1;
        for (var index = 0; index < buttonViews.Length; index++)
        {
            var capturedIndex = index;
            buttonViews[index].Accepting += (_, args) =>
            {
                args.Handled = true;
                result = capturedIndex;
                dialog.RequestStop();
            };
        }

        ConfigureEscapeToClose(dialog, () =>
        {
            result = -1;
            dialog.RequestStop();
        });

        textView.DefaultAcceptView = buttonViews[0];
        textView.SetFocus();
        RequireApp().Run(dialog);
        dialog.Dispose();
        return result;
    }

    private void ShowInfoDialog(string title, string message)
        => ShowChoiceDialog(title, message, TerminalThemeSurface.Dialog, "OK");

    private void ShowErrorDialog(string title, string message)
        => ShowChoiceDialog(title, message, TerminalThemeSurface.Error, "OK");

    private static Size CalculateMessageDialogSize(string text)
    {
        var normalized = string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var longestLineLength = lines.Length == 0 ? 0 : lines.Max(line => line.Length);
        var width = Math.Clamp(longestLineLength + 8, 48, 96);
        var height = Math.Clamp(lines.Length + 7, 8, 24);
        return new Size(width, height);
    }

    private static TextView CreateSelectableReadOnlyTextView(string text, bool wordWrap = false)
        => new()
        {
            ReadOnly = true,
            WordWrap = wordWrap,
            Text = text,
            TabStop = TabBehavior.TabStop,
            CanFocus = true,
            MousePositionTracking = true,
            SelectWordOnlyOnDoubleClick = false
        };

    private static void ConfigureSelectableReadOnlyField(TextField field)
    {
        field.ReadOnly = true;
        field.CanFocus = true;
        field.TabStop = TabBehavior.TabStop;
        field.MousePositionTracking = true;
        field.SelectWordOnlyOnDoubleClick = false;
    }

    private static void ConfigureEscapeToClose(Dialog dialog, Action onEscape)
        => dialog.KeyDown += (_, key) =>
        {
            if (key.NoCtrl.NoAlt.NoShift != Key.Esc)
            {
                return;
            }

            key.Handled = true;
            onEscape();
        };

    private static string BuildAboutSummaryText()
        => string.Join(Environment.NewLine,
        [
            "SQL Manager",
            AppVersion.Description,
            string.Empty,
            $"Version: {AppVersion.DisplayVersion}",
            $"Built: {AppVersion.BuildDate}",
            AppVersion.Copyright,
            string.Empty,
            "Open source projects used in this app:",
            "Terminal.Gui: terminal user interface framework",
            "Spectre.Console: console rendering and formatting",
            "Microsoft.Data.SqlClient: SQL Server connectivity",
            "Npgsql: PostgreSQL connectivity",
            "Konscious.Security.Cryptography.Argon2: password-based encryption support"
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
        ApplyTheme(dialog, TerminalThemeSurface.Dialog);

        var textView = CreateSelectableReadOnlyTextView(details);
        textView.X = 0;
        textView.Y = 0;
        textView.Width = Dim.Fill();
        textView.Height = Dim.Fill(1);
        dialog.Add(textView);
        dialog.Buttons = [showPasswordButton, versionHistoryButton, closeButton];

        showPasswordButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            if (trackedUser is null || string.IsNullOrWhiteSpace(trackedUser.Password))
            {
                ShowErrorDialog("Show Password", "Password is not available in the current config view.");
                return;
            }

            ShowTextDialog($"Password: {row.UserName}", trackedUser.Password);
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

        ConfigureEscapeToClose(dialog, dialog.RequestStop);
        textView.SetFocus();
        RequireApp().Run(dialog);
        dialog.Dispose();
    }

    private static string BuildConnectionStringPreview(ServerConfig server, string databaseName, string userName)
    {
        return SqlProviders.Normalize(server.Provider) == SqlProviders.SqlServer
            ? ServerConnectionOptions.BuildSqlServerUserConnectionString(server.ServerName, server.Port, databaseName, userName, null, server.SqlServerTrustMode)
            : ServerConnectionOptions.BuildPostgreSqlUserConnectionString(server.ServerName, server.Port, databaseName, userName, null, server.PostgreSqlSslMode, server.PostgreSqlPooling, server.ConnectionTimeoutSeconds, server.CommandTimeoutSeconds);
    }

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
        ApplyTheme(dialog, TerminalThemeSurface.Dialog);

        var textView = CreateSelectableReadOnlyTextView(details);
        textView.X = 0;
        textView.Y = 0;
        textView.Width = Dim.Fill();
        textView.Height = Dim.Fill(1);
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

        ConfigureEscapeToClose(dialog, dialog.RequestStop);
        textView.SetFocus();
        RequireApp().Run(dialog);
        dialog.Dispose();
    }

    private void ShowTrashBinDialog()
    {
        if (_config.Trash.Count == 0)
        {
            ShowInfoDialog("Trash Bin", "Trash is empty.");
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
        ApplyTheme(dialog, TerminalThemeSurface.Dialog);

        var textView = CreateSelectableReadOnlyTextView(details);
        textView.X = 0;
        textView.Y = 0;
        textView.Width = Dim.Fill();
        textView.Height = Dim.Fill(1);
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

        ConfigureEscapeToClose(dialog, dialog.RequestStop);
        textView.SetFocus();
        RequireApp().Run(dialog);
        dialog.Dispose();
    }

    private void ShowVersionHistoryDialog(string title, IReadOnlyList<EntryVersion> versionHistory)
    {
        if (versionHistory.Count == 0)
        {
            ShowInfoDialog(title, "No version history is available for this entry.");
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

    private string? PromptKnownUserName(ServerConfig server, string title, string? databaseName = null)
    {
        var userNames = server.Databases
            .Where(database => string.IsNullOrWhiteSpace(databaseName) || database.DatabaseName.Equals(databaseName, StringComparison.OrdinalIgnoreCase))
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

    private static string GetConfiguredUserPassword(ServerConfig server, string databaseName, string userName)
        => server.Databases
            .FirstOrDefault(database => database.DatabaseName.Equals(databaseName, StringComparison.OrdinalIgnoreCase))?
            .Users
            .FirstOrDefault(user => user.Username.Equals(userName, StringComparison.OrdinalIgnoreCase))?
            .Password
            ?? string.Empty;

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
        ApplyTheme(dialog, TerminalThemeSurface.Dialog);
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
        ConfigureEscapeToClose(dialog, () =>
        {
            value = null;
            dialog.RequestStop();
        });

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
                            ServerIdentifier = newActiveServer
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
            ShowInfoDialog("SQL Manager", message);
        }
        else
        {
            ShowErrorDialog("SQL Manager", message);
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
            ShowErrorDialog(title, exception.Message);
        }
        catch (OperationCanceledException)
        {
            _exitCode = 130;
            ShowErrorDialog(title, "Operation cancelled.");
            RequireApp().RequestStop();
        }
        catch (Exception exception)
        {
            ErrorLogger.LogException($"{title} UI action failed", exception);
            ShowErrorDialog(title, $"Unexpected error: {exception.Message}");
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
        ApplyTheme(dialog, TerminalThemeSurface.Dialog);
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
            ShowErrorDialog("SQL Manager", "Select an active server first.");
        }

        return server;
    }

    private ServerConfig? GetActiveServerConfig()
        => ServerConnections.FindBySelectionKey(_config.Servers, _activeServer);

    private string GetActiveServerLabel()
    {
        var server = GetActiveServerConfig();
        return server is null ? "<none>" : ServerConnections.GetSelectionDisplay(server);
    }

    private static string GetSelectedServerLabel(SqlManagerConfig config)
    {
        var server = ServerConnections.FindBySelectionKey(config.Servers, config.SelectedServerName);
        return server is null ? "<none>" : ServerConnections.GetSelectionDisplay(server);
    }

    private string? GetConfigEncryptionPassword()
        => _configEncryptionPassword.Reveal();

    private IApplication RequireApp()
        => _app ?? throw new InvalidOperationException("Terminal.Gui application is not initialized.");

    private void ConfigureTheme(IApplication app, string? preferredThemeName)
    {
        _themeSchemes.Clear();
        var driver = app.Driver;
        var capability = driver?.ColorCapabilities is { } colorCapabilities
            ? colorCapabilities.Capability
            : ColorCapabilityLevel.NoColor;
        if (!TerminalThemeCatalog.TryCreateSchemes(capability, preferredThemeName, out var schemes))
        {
            return;
        }

        foreach (var pair in schemes)
        {
            _themeSchemes[pair.Key] = pair.Value;
        }
    }

    private void RefreshMainTheme()
    {
        if (_mainWindow is not null)
        {
            ApplyTheme(_mainWindow, TerminalThemeSurface.Runnable);
            _mainWindow.SetNeedsDraw();
        }

        if (_mainMenuBar is not null)
        {
            ApplyTheme(_mainMenuBar, TerminalThemeSurface.Menu);
            _mainMenuBar.SetNeedsDraw();
        }
    }

    private void ApplyTheme(View view, TerminalThemeSurface surface)
    {
        if (_themeSchemes.TryGetValue(surface, out var scheme))
        {
            view.SetScheme(scheme);
        }
    }

    private void TryRequestApplicationExit()
    {
        _exitPromptQueued = false;
        if (_exitPromptOpen)
        {
            return;
        }

        _exitPromptOpen = true;
        try
        {
            if (!ConfirmExitApplication())
            {
                return;
            }

            _exitConfirmationApproved = true;
            RequireApp().RequestStop();
        }
        finally
        {
            _exitPromptOpen = false;
        }
    }

    private bool ConfirmExitApplication()
        => ShowChoiceDialog(
            "Exit SQL Manager",
            "Are you sure you want to exit?",
            "Stay",
            "Exit") == 1;

    private void OnMainWindowIsRunningChanging(object? sender, Terminal.Gui.App.CancelEventArgs<bool> args)
    {
        if (!ShouldCancelMainWindowStop(args.NewValue, _exitConfirmationApproved, _cancellationToken.IsCancellationRequested))
        {
            if (_exitConfirmationApproved)
            {
                _exitConfirmationApproved = false;
            }

            return;
        }

        // Only explicit exit flows should stop the main window. Prompting from inside
        // IsRunningChanging can leave Terminal.Gui stuck if the confirmation is cancelled.
        args.Cancel = true;
        QueueExitConfirmation();
    }

    private void QueueExitConfirmation()
    {
        if (_exitPromptQueued || _exitPromptOpen)
        {
            return;
        }

        _exitPromptQueued = true;
        RequireApp().AddTimeout(TimeSpan.Zero, () =>
        {
            _exitPromptQueued = false;
            if (!_exitConfirmationApproved && !_cancellationToken.IsCancellationRequested)
            {
                TryRequestApplicationExit();
            }

            return false;
        });
    }

    private static bool ShouldCancelMainWindowStop(bool isStarting, bool exitApproved, bool cancellationRequested)
        => !isStarting && !exitApproved && !cancellationRequested;

    private static void TryClearTerminal()
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        try
        {
            Console.Write("\u001b[2J\u001b[3J\u001b[H");
            Console.Out.Flush();
        }
        catch
        {
            try
            {
                Console.Clear();
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
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

        return config.Servers.Count == 1 ? ServerConnections.GetIdentifier(config.Servers[0]) : string.Empty;
    }

    private static string BuildServerLine(ServerConfig server)
    {
        var passwordState = BuildPasswordState(server.AdminPassword, server.Encrypted);
        var userCount = server.Databases.Sum(database => database.Users.Count);
        return $"{ServerConnections.GetSelectionDisplay(server)} | host: {server.ServerName} | provider: {SqlProviders.GetDisplayName(server.Provider)} | admin: {(string.IsNullOrWhiteSpace(server.AdminUsername) ? "<none>" : server.AdminUsername)} | password: {passwordState} | dbs: {server.Databases.Count} | users: {userCount}";
    }

    internal static string BuildServerEditorServerLabel(string provider)
        => SqlProviders.Normalize(provider) == SqlProviders.PostgreSql
            ? "Host:"
            : "Server / Instance:";

    internal static string BuildServerEditorPortLabel(string provider)
        => $"Port (default {SqlProviders.GetDefaultPort(provider)}):";

    internal static string BuildServerEditorAdminDatabaseLabel(string provider)
        => $"Admin Database (default {SqlProviders.GetDefaultAdminDatabase(provider)}):";

    internal static string BuildServerEditorAdminUserLabel(string provider)
        => SqlProviders.Normalize(provider) == SqlProviders.PostgreSql
            ? "Admin User:"
            : "Admin Login:";

    internal static string BuildServerEditorProviderHint(string provider)
        => SqlProviders.Normalize(provider) == SqlProviders.PostgreSql
            ? "PostgreSQL: use the host name, connect through the postgres admin database, and choose SSL mode, timeouts, and pooling settings for the connection."
            : "SQL Server: use a host or instance name, connect through the master admin database, and choose whether the client uses Encrypt=False, Encrypt=True with TrustServerCertificate=True, or Encrypt=Strict.";

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
        const int buttonWidth = 14;
        var fieldX = Math.Max(24, label.Length + 4);

        dialog.Add(new Label { X = 1, Y = y, Text = label });
        field.X = fieldX;
        field.Y = y;
        field.Width = Dim.Fill(buttonWidth + 5);
        dialog.Add(field);

        var generateButton = new Button
        {
            X = Pos.AnchorEnd(buttonWidth + 2),
            Y = y,
            Width = buttonWidth,
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
                ShowTextDialog(title, $"Generated password:{Environment.NewLine}{Environment.NewLine}{generatedPassword}");
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

    private static string BuildSqlServerConnectionTarget(string server, int? port)
        => ServerConnectionOptions.BuildSqlServerDataSource(server, port);

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

    private static int? ParseOptionalPositiveInteger(TextField field, string error)
    {
        var text = GetText(field);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!int.TryParse(text, out var value) || value <= 0)
        {
            throw new UserInputException(error);
        }

        return value;
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

    private sealed record DatabaseRoleRow(string DatabaseName, CheckBox OwnerCheckBox, CheckBox? ReaderCheckBox, CheckBox? WriterCheckBox)
    {
        public DatabaseRoleAssignment ToAssignment()
        {
            var roles = new List<string>();
            if (OwnerCheckBox.Value == CheckState.Checked)
            {
                roles.Add("db_owner");
            }

            if (ReaderCheckBox?.Value == CheckState.Checked)
            {
                roles.Add("db_datareader");
            }

            if (WriterCheckBox?.Value == CheckState.Checked)
            {
                roles.Add("db_datawriter");
            }

            return new DatabaseRoleAssignment(DatabaseName, roles);
        }
    }

    private sealed record DatabaseSelectionRow(string DatabaseName, CheckBox CheckBox);
}
