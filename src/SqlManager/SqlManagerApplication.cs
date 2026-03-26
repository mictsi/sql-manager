namespace SqlManager;

internal sealed class SqlManagerApplication
{
    private const string ManualEntryChoice = "<Enter manually>";
    private readonly TerminalUi _ui;
    private readonly SqlManagerService _service;

    public SqlManagerApplication(TerminalUi ui, SqlManagerService service)
    {
        _ui = ui;
        _service = service;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var defaultConfigPath = Path.Combine(AppContext.BaseDirectory, "sql-config.json");
        var parseResult = CommandLineParser.Parse(args, defaultConfigPath);
        if (!parseResult.Succeeded || parseResult.Value is null)
        {
            _ui.RenderResult(parseResult);
            _ui.WriteHelp(defaultConfigPath);
            return parseResult.ExitCode;
        }

        var parsed = parseResult.Value;
        if (parsed.Command == CommandKind.Help)
        {
            _ui.WriteHelp(defaultConfigPath);
            return 0;
        }

        if (parsed.Command == CommandKind.Version)
        {
            _ui.WriteVersion();
            return 0;
        }

        if (parsed.Command == CommandKind.Tui)
        {
            if (!_ui.CanPromptInteractively)
            {
                _ui.WriteError("Interactive TUI mode requires an attached console.");
                return 2;
            }

            return new TerminalGuiRunner(_service).Run(parsed.Options.ConfigPath, cancellationToken);
        }

        return await RunCommandAsync(parsed.Options, cancellationToken);
    }

    private async Task<int> RunTuiAsync(string configPath, CancellationToken cancellationToken)
    {
        string? activeServer = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var summaryResult = await _service.LoadConfigSummaryAsync(configPath, cancellationToken);
            if (!summaryResult.Succeeded || summaryResult.Value is null)
            {
                _ui.RenderResult(summaryResult);
                return summaryResult.ExitCode;
            }

            var config = summaryResult.Value;
            if (string.IsNullOrWhiteSpace(activeServer)
                || config.Servers.All(server => !server.ServerName.Equals(activeServer, StringComparison.OrdinalIgnoreCase)))
            {
                activeServer = ResolveInteractiveServerName(config);
            }

            Console.Clear();
            _ui.RenderConfigSummary(config, configPath);
            _ui.RenderConfiguredServers(config, activeServer);

            var action = _ui.PromptSelection("Choose an operation", new[]
            {
                "View config",
                "Select active server",
                "Initialize config",
                "Add server",
                "Sync server",
                "Create database",
                "Create user",
                "Add role",
                "Show users",
                "Remove user",
                "Update password",
                "Exit application"
            });

            if (action == "Exit application")
            {
                return 0;
            }

            if (action == "Select active server")
            {
                var selectedServer = await PromptActiveServerAsync(configPath, config, activeServer, cancellationToken);
                if (!string.IsNullOrWhiteSpace(selectedServer))
                {
                    activeServer = selectedServer;
                    _ui.Pause();
                }

                continue;
            }

            var options = await PromptForOptionsAsync(action, configPath, config, activeServer, cancellationToken);
            if (options is null)
            {
                continue;
            }

            var exitCode = await RunCommandAsync(options, cancellationToken);
            if (exitCode == 130)
            {
                return exitCode;
            }

            _ui.Pause();
        }

        return 130;
    }

    private async Task<int> RunCommandAsync(CommandOptions options, CancellationToken cancellationToken)
    {
        await TryHydrateStoredAdminCredentialsAsync(options, cancellationToken);
        await EnsurePromptedSecretsAsync(options, cancellationToken);

        switch (options.Command)
        {
            case CommandKind.ViewConfig:
            {
                var summary = await _service.LoadConfigSummaryAsync(options.ConfigPath, options.EncryptionPassword, cancellationToken);
                if (summary.Succeeded && summary.Value is not null)
                {
                    _ui.RenderConfigSummary(summary.Value, options.ConfigPath);
                }
                else
                {
                    _ui.RenderResult(summary);
                }

                return summary.ExitCode;
            }
            case CommandKind.InitConfig:
                return await RenderSimpleResultAsync(_service.InitializeConfigAsync(options, cancellationToken));
            case CommandKind.AddServer:
                return await RenderSimpleResultAsync(_service.AddServerAsync(options, cancellationToken));
            case CommandKind.SelectServer:
                return await RenderSimpleResultAsync(_service.SelectServerAsync(options, cancellationToken));
            case CommandKind.SyncServer:
                return await RenderSimpleResultAsync(_service.SyncServerAsync(options, cancellationToken));
            case CommandKind.ShowDatabases:
            {
                var result = await _service.ShowDatabasesAsync(options, cancellationToken);
                _ui.RenderResult(result);
                if (result.Succeeded && result.Value is not null)
                {
                    _ui.RenderDatabases(result.Value);
                }

                return result.ExitCode;
            }
            case CommandKind.CreateDatabase:
                return await RenderSimpleResultAsync(_service.CreateDatabaseAsync(options, cancellationToken));
            case CommandKind.RemoveDatabase:
                return await RenderSimpleResultAsync(_service.RemoveDatabaseAsync(options, cancellationToken));
            case CommandKind.CreateUser:
                return await RenderSimpleResultAsync(_service.CreateUserAsync(options, cancellationToken));
            case CommandKind.AddRole:
                return await RenderSimpleResultAsync(_service.AddRoleAsync(options, cancellationToken));
            case CommandKind.RemoveRole:
                return await RenderSimpleResultAsync(_service.RemoveRoleAsync(options, cancellationToken));
            case CommandKind.ShowUsers:
            {
                var result = await _service.ShowUsersAsync(options, cancellationToken);
                _ui.RenderResult(result);
                if (result.Succeeded && result.Value is not null)
                {
                    _ui.RenderUsers(result.Value);
                }

                return result.ExitCode;
            }
            case CommandKind.RemoveUser:
                return await RenderSimpleResultAsync(_service.RemoveUserAsync(options, cancellationToken));
            case CommandKind.UpdatePassword:
                return await RenderSimpleResultAsync(_service.UpdatePasswordAsync(options, cancellationToken));
            case CommandKind.EnableConfigEncryption:
                return await RenderSimpleResultAsync(_service.ConfigurePasswordEncryptionAsync(options.ConfigPath, true, RequireEncryptionPassword(options), cancellationToken));
            case CommandKind.DisableConfigEncryption:
                return await RenderSimpleResultAsync(_service.ConfigurePasswordEncryptionAsync(options.ConfigPath, false, RequireEncryptionPassword(options), cancellationToken));
            default:
                _ui.WriteError("Unsupported command.");
                return 2;
        }
    }

    private async Task<int> RenderSimpleResultAsync(Task<OperationResult> operation)
    {
        var result = await operation;
        _ui.RenderResult(result);
        return result.ExitCode;
    }

    private async Task EnsurePromptedSecretsAsync(CommandOptions options, CancellationToken cancellationToken)
    {
        if (options.NonInteractive || !_ui.CanPromptInteractively)
        {
            return;
        }

        var summary = await _service.LoadConfigSummaryAsync(options.ConfigPath, cancellationToken);
        var needsEncryptionPassword = summary.Succeeded
            && summary.Value is not null
            && summary.Value.EncryptPasswords
            && string.IsNullOrWhiteSpace(options.EncryptionPassword)
            && options.Command is not CommandKind.Help and not CommandKind.Version and not CommandKind.Tui and not CommandKind.ViewConfig and not CommandKind.ShowDatabases and not CommandKind.ShowUsers and not CommandKind.EnableConfigEncryption;

        if (needsEncryptionPassword)
        {
            options.EncryptionPassword = _ui.PromptSecret("Config encryption password:");
        }

        if (options.Command == CommandKind.EnableConfigEncryption && string.IsNullOrWhiteSpace(options.EncryptionPassword))
        {
            options.EncryptionPassword = _ui.PromptSecret("New config encryption password:");
        }

        var needsAdminPassword = options.Command is CommandKind.SyncServer
            or CommandKind.CreateDatabase
            or CommandKind.CreateUser
            or CommandKind.AddRole
            or CommandKind.RemoveRole
            or CommandKind.ShowUsers
            or CommandKind.RemoveUser
            or CommandKind.UpdatePassword;

        if (needsAdminPassword && string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            options.AdminPassword = _ui.PromptSecret("SQL admin password:");
        }

        if (options.Command is CommandKind.CreateUser or CommandKind.UpdatePassword)
        {
            if (string.IsNullOrWhiteSpace(options.NewUserPassword) && _ui.Confirm("Supply a specific user password?", false))
            {
                options.NewUserPassword = _ui.PromptSecret("User password:");
            }
        }

        await Task.CompletedTask;
    }

    private async Task TryHydrateStoredAdminCredentialsAsync(CommandOptions options, CancellationToken cancellationToken)
    {
        if (options.Command is CommandKind.Help or CommandKind.Tui or CommandKind.ViewConfig or CommandKind.SelectServer or CommandKind.EnableConfigEncryption or CommandKind.DisableConfigEncryption)
        {
            return;
        }

        var summary = await _service.LoadConfigSummaryAsync(options.ConfigPath, options.EncryptionPassword, cancellationToken);
        if (!summary.Succeeded || summary.Value is null)
        {
            return;
        }

        var config = summary.Value;
        var serverName = options.ServerName;
        if (string.IsNullOrWhiteSpace(serverName))
        {
            if (!string.IsNullOrWhiteSpace(config.SelectedServerName))
            {
                serverName = config.SelectedServerName;
            }
            else if (config.Servers.Count == 1)
            {
                serverName = config.Servers[0].ServerName;
            }
        }

        if (string.IsNullOrWhiteSpace(serverName))
        {
            return;
        }

        var server = config.Servers.FirstOrDefault(candidate => candidate.ServerName.Equals(serverName, StringComparison.OrdinalIgnoreCase));
        if (server is null)
        {
            return;
        }

        options.ServerName ??= server.ServerName;
        options.Provider ??= server.Provider;
        options.Port ??= server.Port;
        options.AdminDatabase ??= string.IsNullOrWhiteSpace(server.AdminDatabase) ? null : server.AdminDatabase;
        if (string.IsNullOrWhiteSpace(options.AdminUsername))
        {
            options.AdminUsername = server.AdminUsername;
        }

        if (string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            options.AdminPassword = server.AdminPassword;
        }
    }

    private static string RequireEncryptionPassword(CommandOptions options)
        => string.IsNullOrWhiteSpace(options.EncryptionPassword)
            ? throw new UserInputException("EncryptionPassword is required for this command.")
            : options.EncryptionPassword!;

    private async Task<CommandOptions?> PromptForOptionsAsync(string action, string configPath, SqlManagerConfig config, string? activeServer, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case "View config":
                return new CommandOptions { Command = CommandKind.ViewConfig, ConfigPath = configPath };
            case "Initialize config":
                return await PromptInitializeConfigAsync(configPath, activeServer);
            case "Add server":
                return await PromptAddServerAsync(configPath, config, activeServer);
            case "Sync server":
                return await PromptSyncServerAsync(configPath, config, activeServer);
            case "Create database":
                return await PromptCreateDatabaseAsync(configPath, config, activeServer);
            case "Create user":
                return await PromptCreateUserAsync(configPath, config, activeServer);
            case "Add role":
                return await PromptAddRoleAsync(configPath, config, activeServer);
            case "Show users":
                return await PromptShowUsersAsync(configPath, config, activeServer);
            case "Remove user":
                return await PromptRemoveUserAsync(configPath, config, activeServer);
            case "Update password":
                return await PromptUpdatePasswordAsync(configPath, config, activeServer);
            default:
                _ui.WriteWarning("Unknown action.");
                return null;
        }
    }

    private string ResolveInteractiveServerName(SqlManagerConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.SelectedServerName))
        {
            return config.SelectedServerName;
        }

        return config.Servers.Count == 1 ? config.Servers[0].ServerName : string.Empty;
    }

    private async Task<string?> PromptActiveServerAsync(string configPath, SqlManagerConfig config, string? activeServer, CancellationToken cancellationToken)
    {
        if (config.Servers.Count == 0)
        {
            _ui.WriteWarning("No servers are configured. Add a server first.");
            return null;
        }

        var labelMap = config.Servers.ToDictionary(
            server => BuildServerChoiceLabel(server, activeServer),
            server => server.ServerName,
            StringComparer.Ordinal);
        var response = _ui.PromptSelectionWithNavigation("Select active server", labelMap.Keys);
        if (response.Navigation != PromptNavigation.Submit || response.Value is null)
        {
            return null;
        }

        var serverName = labelMap[response.Value];
        var result = await _service.SelectServerAsync(
            new CommandOptions
            {
                Command = CommandKind.SelectServer,
                ConfigPath = configPath,
                ServerName = serverName
            },
            cancellationToken);
        _ui.RenderResult(result);
        return result.Succeeded ? serverName : null;
    }

    private async Task<CommandOptions?> PromptInitializeConfigAsync(string configPath, string? activeServer)
    {
        var options = new CommandOptions
        {
            Command = CommandKind.InitConfig,
            ConfigPath = configPath
        };

        var step = 0;
        while (true)
        {
            switch (step)
            {
                case 0:
                {
                    var response = _ui.PromptTextWithNavigation("Initial server name (leave blank for empty config):", activeServer, true);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.ServerName = string.IsNullOrWhiteSpace(response.Value) ? null : response.Value;
                    if (string.IsNullOrWhiteSpace(options.ServerName))
                    {
                        return options;
                    }

                    step++;
                    continue;
                }
                case 1:
                {
                    var response = PromptProviderWithNavigation(options.Provider);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.Provider = response.Value;
                    step++;
                    continue;
                }
                case 2:
                {
                    var response = _ui.PromptTextWithNavigation("Admin username (leave blank to skip):", null, true);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminUsername = string.IsNullOrWhiteSpace(response.Value) ? null : response.Value;
                    step++;
                    continue;
                }
                case 3:
                {
                    var response = PromptPortWithNavigation(null);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.Port = response.Value;
                    step++;
                    continue;
                }
                case 4:
                {
                    var defaultAdminDatabase = options.Provider == SqlProviders.PostgreSql ? SqlProviders.GetDefaultAdminDatabase(options.Provider) : null;
                    var response = _ui.PromptTextWithNavigation("Admin database (leave blank for provider default):", defaultAdminDatabase, true);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminDatabase = string.IsNullOrWhiteSpace(response.Value) ? null : response.Value;
                    return options;
                }
                default:
                    return options;
            }
        }
    }

    private async Task<CommandOptions?> PromptAddServerAsync(string configPath, SqlManagerConfig config, string? activeServer)
    {
        var existingServer = config.Servers.FirstOrDefault(server => server.ServerName.Equals(activeServer, StringComparison.OrdinalIgnoreCase));
        var options = new CommandOptions
        {
            Command = CommandKind.AddServer,
            ConfigPath = configPath
        };

        var step = 0;
        while (true)
        {
            switch (step)
            {
                case 0:
                {
                    var response = _ui.PromptTextWithNavigation("Server name:", activeServer, false);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.ServerName = response.Value;
                    step++;
                    continue;
                }
                case 1:
                {
                    var response = PromptProviderWithNavigation(existingServer?.Provider);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.Provider = response.Value;
                    step++;
                    continue;
                }
                case 2:
                {
                    var response = _ui.PromptTextWithNavigation("Admin username (leave blank to skip):", existingServer?.AdminUsername, true);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminUsername = string.IsNullOrWhiteSpace(response.Value) ? null : response.Value;
                    step++;
                    continue;
                }
                case 3:
                {
                    var response = PromptPortWithNavigation(existingServer?.Port);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.Port = response.Value;
                    step++;
                    continue;
                }
                case 4:
                {
                    var defaultAdminDatabase = existingServer?.AdminDatabase;
                    if (string.IsNullOrWhiteSpace(defaultAdminDatabase) && options.Provider == SqlProviders.PostgreSql)
                    {
                        defaultAdminDatabase = SqlProviders.GetDefaultAdminDatabase(options.Provider);
                    }

                    var response = _ui.PromptTextWithNavigation("Admin database (leave blank for provider default):", defaultAdminDatabase, true);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminDatabase = string.IsNullOrWhiteSpace(response.Value) ? null : response.Value;
                    return options;
                }
                default:
                    return options;
            }
        }
    }

    private async Task<CommandOptions?> PromptSyncServerAsync(string configPath, SqlManagerConfig config, string? activeServer)
    {
        if (!TryGetActiveServer(config, activeServer, out var serverConfig))
        {
            return null;
        }

        var options = CreateServerScopedOptions(CommandKind.SyncServer, configPath, serverConfig.ServerName);
        var step = 0;
        while (true)
        {
            switch (step)
            {
                case 0:
                {
                    var response = _ui.PromptTextWithNavigation("Admin username:", serverConfig.AdminUsername, false);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminUsername = response.Value;
                    step++;
                    continue;
                }
                case 1:
                {
                    var response = _ui.PromptSecretWithNavigation("SQL admin password:");
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminPassword = response.Value;
                    return options;
                }
                default:
                    return options;
            }
        }
    }

    private async Task<CommandOptions?> PromptCreateDatabaseAsync(string configPath, SqlManagerConfig config, string? activeServer)
    {
        if (!TryGetActiveServer(config, activeServer, out var serverConfig))
        {
            return null;
        }

        var options = CreateServerScopedOptions(CommandKind.CreateDatabase, configPath, serverConfig.ServerName);
        var step = 0;
        while (true)
        {
            switch (step)
            {
                case 0:
                {
                    var response = _ui.PromptTextWithNavigation("Admin username:", serverConfig.AdminUsername, false);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminUsername = response.Value;
                    step++;
                    continue;
                }
                case 1:
                {
                    var response = _ui.PromptSecretWithNavigation("SQL admin password:");
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminPassword = response.Value;
                    step++;
                    continue;
                }
                case 2:
                {
                    var response = _ui.PromptTextWithNavigation("Database name:");
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.DatabaseName = response.Value;
                    return options;
                }
                default:
                    return options;
            }
        }
    }

    private async Task<CommandOptions?> PromptCreateUserAsync(string configPath, SqlManagerConfig config, string? activeServer)
    {
        if (!TryGetActiveServer(config, activeServer, out var serverConfig))
        {
            return null;
        }

        var options = CreateServerScopedOptions(CommandKind.CreateUser, configPath, serverConfig.ServerName);
        var step = 0;
        while (true)
        {
            switch (step)
            {
                case 0:
                {
                    var response = _ui.PromptTextWithNavigation("Admin username:", serverConfig.AdminUsername, false);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminUsername = response.Value;
                    step++;
                    continue;
                }
                case 1:
                {
                    var response = _ui.PromptSecretWithNavigation("SQL admin password:");
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminPassword = response.Value;
                    step++;
                    continue;
                }
                case 2:
                {
                    var response = PromptDatabaseWithNavigation(serverConfig);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.DatabaseName = response.Value;
                    step++;
                    continue;
                }
                case 3:
                {
                    var response = _ui.PromptTextWithNavigation("SQL user name:");
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.UserName = response.Value;
                    step++;
                    continue;
                }
                case 4:
                {
                    var response = PromptRolesWithNavigation();
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.Roles = response.Value ?? Array.Empty<string>();
                    step++;
                    continue;
                }
                case 5:
                {
                    var response = _ui.PromptConfirmWithNavigation("Specify user password manually?", false);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    if (!response.Value)
                    {
                        return options;
                    }

                    step++;
                    continue;
                }
                case 6:
                {
                    var response = _ui.PromptSecretWithNavigation("User password:");
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.NewUserPassword = response.Value;
                    return options;
                }
                default:
                    return options;
            }
        }
    }

    private async Task<CommandOptions?> PromptAddRoleAsync(string configPath, SqlManagerConfig config, string? activeServer)
    {
        if (!TryGetActiveServer(config, activeServer, out var serverConfig))
        {
            return null;
        }

        var options = CreateServerScopedOptions(CommandKind.AddRole, configPath, serverConfig.ServerName);
        var step = 0;
        while (true)
        {
            switch (step)
            {
                case 0:
                {
                    var response = _ui.PromptTextWithNavigation("Admin username:", serverConfig.AdminUsername, false);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminUsername = response.Value;
                    step++;
                    continue;
                }
                case 1:
                {
                    var response = _ui.PromptSecretWithNavigation("SQL admin password:");
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminPassword = response.Value;
                    step++;
                    continue;
                }
                case 2:
                {
                    var response = PromptDatabaseWithNavigation(serverConfig);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.DatabaseName = response.Value;
                    step++;
                    continue;
                }
                case 3:
                {
                    var response = _ui.PromptTextWithNavigation("Existing SQL user name:");
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.UserName = response.Value;
                    step++;
                    continue;
                }
                case 4:
                {
                    var response = PromptRolesWithNavigation();
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.Roles = response.Value ?? Array.Empty<string>();
                    return options;
                }
                default:
                    return options;
            }
        }
    }

    private async Task<CommandOptions?> PromptShowUsersAsync(string configPath, SqlManagerConfig config, string? activeServer)
    {
        if (!TryGetActiveServer(config, activeServer, out var serverConfig))
        {
            return null;
        }

        var options = CreateServerScopedOptions(CommandKind.ShowUsers, configPath, serverConfig.ServerName);
        var step = 0;
        while (true)
        {
            switch (step)
            {
                case 0:
                {
                    var response = _ui.PromptTextWithNavigation("Admin username:", serverConfig.AdminUsername, false);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminUsername = response.Value;
                    step++;
                    continue;
                }
                case 1:
                {
                    var response = _ui.PromptSecretWithNavigation("SQL admin password:");
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminPassword = response.Value;
                    step++;
                    continue;
                }
                case 2:
                {
                    var response = PromptDatabaseWithNavigation(serverConfig);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.DatabaseName = response.Value;
                    return options;
                }
                default:
                    return options;
            }
        }
    }

    private async Task<CommandOptions?> PromptRemoveUserAsync(string configPath, SqlManagerConfig config, string? activeServer)
    {
        if (!TryGetActiveServer(config, activeServer, out var serverConfig))
        {
            return null;
        }

        var options = CreateServerScopedOptions(CommandKind.RemoveUser, configPath, serverConfig.ServerName);
        var step = 0;
        while (true)
        {
            switch (step)
            {
                case 0:
                {
                    var response = _ui.PromptTextWithNavigation("Admin username:", serverConfig.AdminUsername, false);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminUsername = response.Value;
                    step++;
                    continue;
                }
                case 1:
                {
                    var response = _ui.PromptSecretWithNavigation("SQL admin password:");
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminPassword = response.Value;
                    step++;
                    continue;
                }
                case 2:
                {
                    var response = PromptRemovalScopeWithNavigation();
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.RemovalScope = response.Value;
                    if (options.RemovalScope == RemovalScope.Database)
                    {
                        step++;
                    }
                    else
                    {
                        step = 4;
                    }

                    continue;
                }
                case 3:
                {
                    var response = PromptDatabaseWithNavigation(serverConfig);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.DatabaseName = response.Value;
                    step++;
                    continue;
                }
                case 4:
                {
                    var response = _ui.PromptTextWithNavigation("SQL user name:");
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.UserName = response.Value;
                    return options;
                }
                default:
                    return options;
            }
        }
    }

    private async Task<CommandOptions?> PromptUpdatePasswordAsync(string configPath, SqlManagerConfig config, string? activeServer)
    {
        if (!TryGetActiveServer(config, activeServer, out var serverConfig))
        {
            return null;
        }

        var options = CreateServerScopedOptions(CommandKind.UpdatePassword, configPath, serverConfig.ServerName);
        var step = 0;
        while (true)
        {
            switch (step)
            {
                case 0:
                {
                    var response = _ui.PromptTextWithNavigation("Admin username:", serverConfig.AdminUsername, false);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminUsername = response.Value;
                    step++;
                    continue;
                }
                case 1:
                {
                    var response = _ui.PromptSecretWithNavigation("SQL admin password:");
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.AdminPassword = response.Value;
                    step++;
                    continue;
                }
                case 2:
                {
                    var response = _ui.PromptTextWithNavigation("SQL user name:");
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.UserName = response.Value;
                    step++;
                    continue;
                }
                case 3:
                {
                    var response = _ui.PromptConfirmWithNavigation("Specify new password manually?", false);
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    if (!response.Value)
                    {
                        return options;
                    }

                    step++;
                    continue;
                }
                case 4:
                {
                    var response = _ui.PromptSecretWithNavigation("New user password:");
                                        if (HandlePromptNavigation(response, ref step, out var exitWizard))
                    {
                        if (exitWizard)
                        {
                            return null;
                        }

                        continue;
                    }

                    options.NewUserPassword = response.Value;
                    return options;
                }
                default:
                    return options;
            }
        }
    }

    private PromptResponse<string> PromptDatabaseWithNavigation(ServerConfig serverConfig)
    {
        var databaseNames = serverConfig.Databases
            .Select(database => database.DatabaseName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (databaseNames.Count == 0)
        {
            return _ui.PromptTextWithNavigation("Database name:");
        }

        databaseNames.Add(ManualEntryChoice);
        var response = _ui.PromptSelectionWithNavigation("Database", databaseNames);
        if (response.Navigation != PromptNavigation.Submit)
        {
            return response;
        }

        if (string.Equals(response.Value, ManualEntryChoice, StringComparison.Ordinal))
        {
            return _ui.PromptTextWithNavigation("Database name:");
        }

        return response;
    }

    private PromptResponse<IReadOnlyList<string>> PromptRolesWithNavigation()
    {
        var response = _ui.PromptTextWithNavigation("Roles (comma separated):", "db_owner", false);
        return response.Navigation switch
        {
            PromptNavigation.Back => PromptResponse<IReadOnlyList<string>>.Back(),
            PromptNavigation.Cancel => PromptResponse<IReadOnlyList<string>>.Cancel(),
            _ => PromptResponse<IReadOnlyList<string>>.Submitted(
                response.Value?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    ?? Array.Empty<string>())
        };
    }

    private PromptResponse<string> PromptProviderWithNavigation(string? currentProvider)
    {
        var preferredProvider = SqlProviders.Normalize(currentProvider);
        var choices = preferredProvider == SqlProviders.PostgreSql
            ? new[] { "PostgreSQL", "SQL Server" }
            : new[] { "SQL Server", "PostgreSQL" };
        var response = _ui.PromptSelectionWithNavigation("Provider", choices);
        return response.Navigation switch
        {
            PromptNavigation.Back => PromptResponse<string>.Back(),
            PromptNavigation.Cancel => PromptResponse<string>.Cancel(),
            _ => PromptResponse<string>.Submitted(response.Value == "PostgreSQL" ? SqlProviders.PostgreSql : SqlProviders.SqlServer)
        };
    }

    private PromptResponse<int?> PromptPortWithNavigation(int? currentPort)
    {
        while (true)
        {
            var response = _ui.PromptTextWithNavigation("Port (leave blank for provider default):", currentPort?.ToString(), true);
            switch (response.Navigation)
            {
                case PromptNavigation.Back:
                    return PromptResponse<int?>.Back();
                case PromptNavigation.Cancel:
                    return PromptResponse<int?>.Cancel();
            }

            if (string.IsNullOrWhiteSpace(response.Value))
            {
                return PromptResponse<int?>.Submitted(null);
            }

            if (int.TryParse(response.Value, out var port) && port > 0)
            {
                return PromptResponse<int?>.Submitted(port);
            }

            _ui.WriteWarning("Port must be a positive integer.");
        }
    }

    private PromptResponse<RemovalScope> PromptRemovalScopeWithNavigation()
    {
        var response = _ui.PromptSelectionWithNavigation("Removal scope", Enum.GetNames<RemovalScope>());
        return response.Navigation switch
        {
            PromptNavigation.Back => PromptResponse<RemovalScope>.Back(),
            PromptNavigation.Cancel => PromptResponse<RemovalScope>.Cancel(),
            _ => PromptResponse<RemovalScope>.Submitted(ParseScope(response.Value ?? nameof(RemovalScope.Both)))
        };
    }

    private static bool HandlePromptNavigation<T>(PromptResponse<T> response, ref int step, out bool exitWizard)
    {
        exitWizard = false;

        switch (response.Navigation)
        {
            case PromptNavigation.Submit:
                return false;
            case PromptNavigation.Back:
                if (step == 0)
                {
                    exitWizard = true;
                    return true;
                }

                step--;
                return true;
            case PromptNavigation.Cancel:
                exitWizard = true;
                return true;
            default:
                exitWizard = true;
                return true;
        }
    }

    private bool TryGetActiveServer(SqlManagerConfig config, string? activeServer, out ServerConfig serverConfig)
    {
        serverConfig = config.Servers.FirstOrDefault(server => server.ServerName.Equals(activeServer, StringComparison.OrdinalIgnoreCase))!;
        if (serverConfig is not null)
        {
            return true;
        }

        _ui.WriteWarning("Select an active server from the main menu first.");
        return false;
    }

    private static CommandOptions CreateServerScopedOptions(CommandKind command, string configPath, string serverName)
        => new()
        {
            Command = command,
            ConfigPath = configPath,
            ServerName = serverName
        };

    private static string BuildServerChoiceLabel(ServerConfig server, string? activeServer)
    {
        var activeMarker = !string.IsNullOrWhiteSpace(activeServer)
            && server.ServerName.Equals(activeServer, StringComparison.OrdinalIgnoreCase)
            ? "* "
            : string.Empty;
        var adminUser = string.IsNullOrWhiteSpace(server.AdminUsername) ? "<none>" : server.AdminUsername;
        return $"{activeMarker}{server.ServerName} | provider: {SqlProviders.GetDisplayName(server.Provider)} | admin: {adminUser}";
    }

    private static RemovalScope ParseScope(string value)
        => Enum.TryParse<RemovalScope>(value, true, out var scope) ? scope : RemovalScope.Both;
}

