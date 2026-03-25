using System.Drawing;
using System.Collections.ObjectModel;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SqlManager;

internal sealed class TerminalGuiRunner
{
    private const string ManualEntryChoice = "<Enter manually>";

    private readonly SqlManagerService _service;
    private IApplication? _app;
    private CancellationToken _cancellationToken;
    private string _configPath = string.Empty;
    private SqlManagerConfig _config = new();
    private string? _activeServer;
    private Label? _activeServerLabel;
    private Button? _initialMainButton;
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

        var summary = WaitForTaskCompletion(
            _service.LoadConfigSummaryAsync(configPath, cancellationToken),
            "Loading Config",
            "Loading configuration...");
        if (!summary.Succeeded || summary.Value is null)
        {
            MessageBox.ErrorQuery(app, "SQL Manager", summary.Message, "OK");
            return summary.ExitCode;
        }

        _config = summary.Value;
        _activeServer = ResolveActiveServerName(_config);

        var window = new Window
        {
            Title = $"SQL Manager v{AppVersion.DisplayVersion}",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            TabStop = TabBehavior.TabGroup
        };

        BuildMainWindow(window);
        RefreshViews();
        _initialMainButton?.SetFocus();
        app.Run(window);
        return _exitCode;
    }

    private void BuildMainWindow(Window window)
    {
        _activeServerLabel = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Text = string.Empty
        };

        var instructionsLabel = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Text = "Tab moves across actions. Enter runs the selected action."
        };

        var serverActions = new (string Title, Action Action)[]
        {
            ("Select Active Server", (Action)ShowSelectServerDialog),
            ("Add Server", (Action)ShowAddServerDialog),
            ("Sync Server", (Action)ShowSyncServerDialog),
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

        var configurationActions = new (string Title, Action Action)[]
        {
            ("View Config", (Action)ShowConfigDialog),
            ("Initialize Config", (Action)ShowInitializeConfigDialog),
            ("Refresh", (Action)ReloadAndRefresh),
            ("Exit", (Action)(() => RequireApp().RequestStop()))
        };

        AddMenuColumn(window, "Server Management", 1, 3, Dim.Percent(33) - 2, serverActions);
        AddMenuColumn(window, "User Management", Pos.Percent(33) + 1, 3, Dim.Percent(33) - 2, userActions);
        AddMenuColumn(window, "Configuration", Pos.Percent(66) + 1, 3, Dim.Fill(2), configurationActions);

        window.Add(_activeServerLabel, instructionsLabel);
    }

    private void AddMenuColumn(Window window, string title, Pos x, int topY, Dim width, IReadOnlyList<(string Title, Action Action)> actions)
    {
        var headingText = title.ToUpperInvariant();
        var headingUnderline = new string('=', Math.Max(headingText.Length + 6, 20));

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
            if (_initialMainButton is null)
            {
                _initialMainButton = button;
            }

            window.Add(button);
            previous = button;
        }
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
            _service.LoadConfigSummaryAsync(_configPath, _cancellationToken),
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

    private void ShowConfigDialog()
    {
        var lines = new List<string>
        {
            $"Config Path: {_configPath}",
            $"Selected Server: {(string.IsNullOrWhiteSpace(_config.SelectedServerName) ? "<none>" : _config.SelectedServerName)}",
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
                    lines.Add($"    User: {user.Username} | Roles: {string.Join(", ", user.Roles)}");
                }
            }
        }

        ShowTextDialog("Config", string.Join(Environment.NewLine, lines));
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
                ServerName = GetText(serverField),
                AdminUsername = GetText(adminUserField),
                AdminPassword = GetText(adminPasswordField)
            }, _cancellationToken), GetText(serverField)));

        if (result?.Result is not null)
        {
            ShowResult(result.Result);
        }
    }

    private void ShowAddServerDialog()
    {
        var activeConfig = GetActiveServerConfig();
        var serverField = CreateTextField(_activeServer);
        var adminUserField = CreateTextField(activeConfig?.AdminUsername);
        var adminPasswordField = CreateSecretField(activeConfig?.AdminPassword);

        var result = ShowFormDialog(
            "Add Server",
            72,
            14,
            dialog =>
            {
                AddField(dialog, 1, "Server Name:", serverField);
                AddField(dialog, 3, "Admin User:", adminUserField);
                AddField(dialog, 5, "Admin Password:", adminPasswordField);
            },
            () =>
            {
                var serverName = GetRequiredText(serverField, "Server name is required.");
                var adminUser = GetRequiredText(adminUserField, "Admin user is required.");
                return ExecuteOperation(_service.AddServerAsync(new CommandOptions
                {
                    Command = CommandKind.AddServer,
                    ConfigPath = _configPath,
                    ServerName = serverName,
                    AdminUsername = adminUser,
                    AdminPassword = GetText(adminPasswordField)
                }, _cancellationToken), serverName, selectServerAfterSuccess: true);
            });

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
            "Sync Server",
            server,
            12,
            dialog =>
            {
                AddAdminCredentialSection(dialog, 1, server, adminUserField, adminPasswordField);
            },
            () => ExecuteOperation(
                _service.SyncServerAsync(new CommandOptions
                {
                    Command = CommandKind.SyncServer,
                    ConfigPath = _configPath,
                    ServerName = server.ServerName,
                    AdminUsername = ResolveAdminUsername(server, adminUserField),
                    AdminPassword = ResolveAdminPassword(server, adminPasswordField)
                }, _cancellationToken),
                "Sync Server",
                "Syncing databases and users from SQL Server. Press Ctrl+C to cancel."));

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
            dialog =>
            {
                var nextY = AddAdminCredentialSection(dialog, 1, server, adminUserField, adminPasswordField);
                AddField(dialog, nextY, "Database Name:", databaseField);
            },
            () => ExecuteOperation(_service.CreateDatabaseAsync(new CommandOptions
            {
                Command = CommandKind.CreateDatabase,
                ConfigPath = _configPath,
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
            ShowTextDialog(
                $"Databases on {server.ServerName}",
                string.Join(Environment.NewLine, result.Value));
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
            () => ExecuteOperation(_service.RemoveDatabaseAsync(new CommandOptions
            {
                Command = CommandKind.RemoveDatabase,
                ConfigPath = _configPath,
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
            dialog =>
            {
                dialog.Add(new Label { X = 1, Y = 1, Text = $"Database: {databaseName}" });
                AddAdminCredentialSection(dialog, 3, server, adminUserField, adminPasswordField);
            },
            () => ExecuteUserLookup(_service.ShowUsersAsync(new CommandOptions
            {
                Command = CommandKind.ShowUsers,
                ConfigPath = _configPath,
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
                ShowTextDialog(
                    $"Users in {databaseName}",
                    string.Join(Environment.NewLine, result.UserResult.Value.Select(row => $"{row.UserName} | {row.LoginName} | {row.Roles}")));
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

                return ExecuteOperation(_service.RemoveUserAsync(new CommandOptions
                {
                    Command = CommandKind.RemoveUser,
                    ConfigPath = _configPath,
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
            dialog =>
            {
                dialog.Add(new Label { X = 1, Y = 1, Text = $"User: {userName}" });
                var nextY = AddAdminCredentialSection(dialog, 3, server, adminUserField, adminPasswordField);
                AddField(dialog, nextY, "New Password (optional):", newPasswordField);
            },
            () => ExecuteOperation(_service.UpdatePasswordAsync(new CommandOptions
            {
                Command = CommandKind.UpdatePassword,
                ConfigPath = _configPath,
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
                    AddField(dialog, currentY, "User Password (optional):", passwordField);
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

                return ExecuteOperation(_service.SetUserAccessAsync(new CommandOptions
                {
                    Command = CommandKind.CreateUser,
                    ConfigPath = _configPath,
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

    private OperationExecutionResult? ShowFormDialog(string title, int width, int height, Action<Dialog> buildBody, Func<OperationExecutionResult> submit)
    {
        var runButton = new Button { Text = "Run" };
        var backButton = new Button { Text = "Back" };
        var cancelButton = new Button { Text = "Cancel" };
        var dialog = new Dialog
        {
            Title = title,
            Width = width,
            Height = height,
            TabStop = TabBehavior.TabGroup
        };
        buildBody(dialog);
        var initialFocus = FindPreferredInitialFocus(dialog);
        dialog.Buttons = [runButton, backButton, cancelButton];

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
        cancelButton.Accepting += (_, args) =>
        {
            args.Handled = true;
            result = null;
            dialog.RequestStop();
        };

        initialFocus?.SetFocus();
        RequireApp().Run(dialog);
        dialog.Dispose();
        return result;
    }

    private OperationExecutionResult? ShowServerDialog(string title, ServerConfig server, int height, Action<Dialog> buildBody, Func<OperationExecutionResult> submit)
        => ShowFormDialog(title, 92, height, dialog =>
        {
            dialog.Add(new Label { X = 1, Y = 0, Text = $"Server: {server.ServerName}" });
            buildBody(dialog);
        }, submit);

    private int ShowListDialog(string title, View content, string runButtonText)
    {
        var primary = new Button { Text = runButtonText };
        var back = new Button { Text = "Back" };
        var cancel = new Button { Text = "Cancel" };
        var dialog = new Dialog
        {
            Title = title,
            Width = 76,
            Height = 18,
            TabStop = TabBehavior.TabGroup
        };
        dialog.Add(content);
        dialog.Buttons = [primary, back, cancel];

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
        cancel.Accepting += (_, args) =>
        {
            args.Handled = true;
            result = 2;
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
            return ShowInputDialog(title, "Database Name:");
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
            ? ShowInputDialog(title, "Database Name:")
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
            return ShowInputDialog(title, "User Name:");
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
            ? ShowInputDialog(title, "User Name:")
            : selected;
    }

    private string? ShowInputDialog(string title, string label)
    {
        var field = CreateTextField();
        string? value = null;
        var primary = new Button { Text = "Select" };
        var back = new Button { Text = "Back" };
        var cancel = new Button { Text = "Cancel" };
        var dialog = new Dialog
        {
            Title = title,
            Width = 72,
            Height = 10,
            TabStop = TabBehavior.TabGroup
        };
        AddField(dialog, 1, label, field);
        dialog.Buttons = [primary, back, cancel];

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
        cancel.Accepting += (_, args) =>
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

    private IApplication RequireApp()
        => _app ?? throw new InvalidOperationException("Terminal.Gui application is not initialized.");

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
        var passwordState = string.IsNullOrWhiteSpace(server.AdminPassword) ? "missing" : "saved";
        var userCount = server.Databases.Sum(database => database.Users.Count);
        return $"{server.ServerName} | provider: {SqlProviders.GetDisplayName(server.Provider)} | admin: {(string.IsNullOrWhiteSpace(server.AdminUsername) ? "<none>" : server.AdminUsername)} | password: {passwordState} | dbs: {server.Databases.Count} | users: {userCount}";
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
