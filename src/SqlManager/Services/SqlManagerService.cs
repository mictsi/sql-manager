using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace SqlManager;

internal sealed class SqlManagerService
{
    private static readonly string[] SupportedSqlServerRoles = ["db_owner", "db_datareader", "db_datawriter"];
    private static readonly string[] SupportedPostgreSqlRoles = ["db_owner"];
    private static readonly string[] SupportedMySqlRoles = ["db_owner"];
    private const string TrashEntryTypeServer = "server";
    private const string TrashEntryTypeDatabase = "database";
    private const string TrashEntryTypeUser = "user";
    private readonly ConfigStore _configStore;
    private readonly ConfigPasswordProtector _configPasswordProtector;
    private readonly PasswordGenerator _passwordGenerator;
    private readonly SqlServerGateway _sqlServerGateway;
    private readonly PostgreSqlGateway _postgreSqlGateway;
    private readonly MySqlGateway _mySqlGateway;

    public SqlManagerService(ConfigStore configStore, ConfigPasswordProtector configPasswordProtector, PasswordGenerator passwordGenerator, SqlServerGateway sqlServerGateway, PostgreSqlGateway postgreSqlGateway, MySqlGateway mySqlGateway)
    {
        _configStore = configStore;
        _configPasswordProtector = configPasswordProtector;
        _passwordGenerator = passwordGenerator;
        _sqlServerGateway = sqlServerGateway;
        _postgreSqlGateway = postgreSqlGateway;
        _mySqlGateway = mySqlGateway;
    }

    public Task<OperationResult<SqlManagerConfig>> LoadConfigSummaryAsync(string configPath, CancellationToken cancellationToken)
        => LoadConfigSummaryAsync(configPath, null, cancellationToken);

    public Task<OperationResult<SqlManagerConfig>> LoadConfigSummaryAsync(string configPath, string? encryptionPassword, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var config = await LoadDisplayConfigAsync(configPath, encryptionPassword, cancellationToken);
            return OperationResult<SqlManagerConfig>.Success(config, "Config loaded.");
        });

    public Task<OperationResult> ConfigurePasswordEncryptionAsync(string configPath, bool encryptPasswords, string encryptionPassword, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var storedConfig = await _configStore.LoadAsync(configPath, cancellationToken);
            if (encryptPasswords)
            {
                _configPasswordProtector.ValidateUnlockPassword(encryptionPassword);
                if (storedConfig.EncryptPasswords)
                {
                    if (HasFullConfigEnvelope(storedConfig))
                    {
                        throw new UserInputException("Password encryption is already enabled for this config.");
                    }

                    var migratedConfig = await LoadEditableConfigAsync(configPath, encryptionPassword, cancellationToken);
                    migratedConfig.EncryptPasswords = true;
                    migratedConfig.EncryptionKey = storedConfig.EncryptionKey;
                    await SaveEditableConfigAsync(configPath, migratedConfig, encryptionPassword, cancellationToken);
                    return OperationResult.Success("Password encryption updated.", "The complete config file is now encrypted at rest.");
                }

                var config = CloneConfig(storedConfig);
                config.EncryptPasswords = true;
                config.EncryptionKey = _configPasswordProtector.CreateEncryptionKey(encryptionPassword);
                await SaveEditableConfigAsync(configPath, config, encryptionPassword, cancellationToken);
                return OperationResult.Success("Password encryption enabled.", "The complete config file is now encrypted at rest.");
            }

            if (!storedConfig.EncryptPasswords)
            {
                return OperationResult.Success("Password encryption is already disabled.");
            }

            var decryptedConfig = await LoadEditableConfigAsync(configPath, encryptionPassword, cancellationToken);
            decryptedConfig.EncryptPasswords = false;
            decryptedConfig.EncryptionKey = string.Empty;
            await SaveEditableConfigAsync(configPath, decryptedConfig, null, cancellationToken);
            return OperationResult.Success("Password encryption disabled.", "Stored passwords are now written in plaintext again.");
        });

    public Task<OperationResult> MigrateEncryptedConfigFormatAsync(string configPath, string encryptionPassword, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var storedConfig = await _configStore.LoadAsync(configPath, cancellationToken);
            if (!storedConfig.EncryptPasswords)
            {
                throw new UserInputException("Config encryption is not enabled for this file.");
            }

            if (HasFullConfigEnvelope(storedConfig))
            {
                return OperationResult.Success("Encrypted config format is already up to date.");
            }

            var migratedConfig = await LoadEditableConfigAsync(configPath, encryptionPassword, cancellationToken);
            migratedConfig.EncryptPasswords = true;
            migratedConfig.EncryptionKey = storedConfig.EncryptionKey;
            await SaveEditableConfigAsync(configPath, migratedConfig, encryptionPassword, cancellationToken);
            return OperationResult.Success("Encrypted config format migrated.", "The config now uses full-file encryption at rest.");
        });

    public Task<OperationResult> RestoreTrashItemAsync(string configPath, string trashId, string? encryptionPassword, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var requestedTrashId = Require(trashId, "RestoreTrashItem requires a trash item id.");
            var config = await LoadEditableConfigAsync(configPath, encryptionPassword, cancellationToken);
            var trashEntry = config.Trash.FirstOrDefault(entry => entry.TrashId.Equals(requestedTrashId, StringComparison.OrdinalIgnoreCase))
                ?? throw new UserInputException("Trash item was not found.");

            switch (trashEntry.EntryType)
            {
                case TrashEntryTypeServer:
                {
                    var server = DeserializeTrashPayload<ServerConfig>(trashEntry);
                    if (FindServer(config, ServerConnections.GetIdentifier(server)) is not null)
                    {
                        throw new UserInputException($"Connection '{ServerConnections.GetIdentifier(server)}' already exists in the config.");
                    }

                    AddVersion(server.VersionHistory, "Recovered from trash.", BuildServerVersionDetails(server));
                    config.Servers.Add(server);
                    if (string.IsNullOrWhiteSpace(config.SelectedServerName))
                    {
                        config.SelectedServerName = ServerConnections.GetIdentifier(server);
                    }

                    break;
                }
                case TrashEntryTypeDatabase:
                {
                    var database = DeserializeTrashPayload<DatabaseConfig>(trashEntry);
                    var server = FindServer(config, trashEntry.ParentServerName)
                        ?? throw new UserInputException($"Server '{trashEntry.ParentServerName}' must exist before this database can be restored.");
                    if (server.Databases.Any(candidate => candidate.DatabaseName.Equals(database.DatabaseName, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new UserInputException($"Database '{database.DatabaseName}' already exists in the config.");
                    }

                    AddVersion(database.VersionHistory, "Recovered from trash.", BuildDatabaseVersionDetails(server, database));
                    server.Databases.Add(database);
                    break;
                }
                case TrashEntryTypeUser:
                {
                    var user = DeserializeTrashPayload<UserConfig>(trashEntry);
                    var server = FindServer(config, trashEntry.ParentServerName)
                        ?? throw new UserInputException($"Server '{trashEntry.ParentServerName}' must exist before this user can be restored.");
                    var database = server.Databases.FirstOrDefault(candidate => candidate.DatabaseName.Equals(trashEntry.ParentDatabaseName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new UserInputException($"Database '{trashEntry.ParentDatabaseName}' must exist before this user can be restored.");
                    if (database.Users.Any(candidate => candidate.Username.Equals(user.Username, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new UserInputException($"User '{user.Username}' already exists in database '{database.DatabaseName}'.");
                    }

                    AddVersion(user.VersionHistory, "Recovered from trash.", BuildUserVersionDetails(server, database, user));
                    database.Users.Add(user);
                    break;
                }
                default:
                    throw new UserInputException($"Unsupported trash entry type '{trashEntry.EntryType}'.");
            }

            config.Trash = config.Trash.Where(entry => !entry.TrashId.Equals(requestedTrashId, StringComparison.OrdinalIgnoreCase)).ToList();
            await SaveEditableConfigAsync(configPath, config, encryptionPassword, cancellationToken);

            var restoreMessage = trashEntry.EntryType is TrashEntryTypeDatabase or TrashEntryTypeUser
                ? "Entry restored to config. Sync or recreate the live SQL object if needed."
                : "Entry restored from trash.";
            return OperationResult.Success($"Restored '{trashEntry.DisplayName}' from trash.", restoreMessage);
        });

    public Task<OperationResult> InitializeConfigAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var config = await LoadEditableConfigAsync(options.ConfigPath, options.EncryptionPassword, cancellationToken);
            if (!string.IsNullOrWhiteSpace(options.ServerName))
            {
                var server = GetOrCreateServer(
                    config,
                    options.ServerIdentifier,
                    options.ServerName!,
                    options.DisplayName,
                    options.Provider,
                    options.Port,
                    options.AdminDatabase,
                    options.AdminUsername,
                    options.AdminPassword,
                    options.PostgreSqlSslMode,
                    options.PostgreSqlPooling,
                    options.MySqlSslMode,
                    options.MySqlPooling,
                    options.MySqlAllowPublicKeyRetrieval,
                    options.SqlServerTrustMode,
                    options.ConnectionTimeoutSeconds,
                    options.CommandTimeoutSeconds);
                server.AdminUsername = options.AdminUsername ?? server.AdminUsername;
                server.AdminPassword = options.AdminPassword ?? server.AdminPassword;
                config.SelectedServerName = ServerConnections.GetIdentifier(server);
            }

            await SaveEditableConfigAsync(options.ConfigPath, config, options.EncryptionPassword, cancellationToken);
            return OperationResult.Success($"Config file is ready at '{options.ConfigPath}'.");
        });

    public Task<OperationResult> AddServerAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var serverName = Require(options.ServerName, "AddServer requires ServerName.");
            var config = await LoadEditableConfigAsync(options.ConfigPath, options.EncryptionPassword, cancellationToken);
            var serverIdentifier = ServerConnections.GetNextIdentifier(config.Servers);

            var server = GetOrCreateServer(
                config,
                serverIdentifier,
                serverName,
                options.DisplayName,
                options.Provider,
                options.Port,
                options.AdminDatabase,
                options.AdminUsername,
                options.AdminPassword,
                options.PostgreSqlSslMode,
                options.PostgreSqlPooling,
                options.MySqlSslMode,
                options.MySqlPooling,
                options.MySqlAllowPublicKeyRetrieval,
                options.SqlServerTrustMode,
                options.ConnectionTimeoutSeconds,
                options.CommandTimeoutSeconds);
            AddVersion(server.VersionHistory, "Server added.", BuildServerVersionDetails(server));
            if (string.IsNullOrWhiteSpace(config.SelectedServerName))
            {
                config.SelectedServerName = ServerConnections.GetIdentifier(server);
            }

            await SaveEditableConfigAsync(options.ConfigPath, config, options.EncryptionPassword, cancellationToken);
            return OperationResult.Success($"Connection '{ServerConnections.GetSelectionDisplay(server)}' has been added to the config.");
        });

    public Task<OperationResult> UpdateServerAsync(
        string configPath,
        string existingServerIdentifier,
        string displayName,
        string serverName,
        string? provider,
        int? port,
        string? adminDatabase,
        string adminUsername,
        string? adminPassword,
        string? postgreSqlSslMode,
        bool? postgreSqlPooling,
        string? mySqlSslMode,
        bool? mySqlPooling,
        bool? mySqlAllowPublicKeyRetrieval,
        string? sqlServerTrustMode,
        int? connectionTimeoutSeconds,
        int? commandTimeoutSeconds,
        string? encryptionPassword,
        CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var currentName = Require(existingServerIdentifier, "UpdateServer requires an existing server identifier.");
            var newServerName = Require(serverName, "UpdateServer requires ServerName.");
            var newAdminUsername = Require(adminUsername, "UpdateServer requires AdminUsername.");
            var config = await LoadEditableConfigAsync(configPath, encryptionPassword, cancellationToken);
            var server = FindServer(config, currentName)
                ?? throw new UserInputException($"Connection '{currentName}' was not found in the config.");

            var normalizedProvider = SqlProviders.Normalize(string.IsNullOrWhiteSpace(provider) ? server.Provider : provider);
            var normalizedAdminDatabase = string.IsNullOrWhiteSpace(adminDatabase)
                ? SqlProviders.GetDefaultAdminDatabase(normalizedProvider)
                : adminDatabase!;

            server.ServerIdentifier = currentName;
            server.DisplayName = string.IsNullOrWhiteSpace(displayName) ? newServerName : displayName.Trim();
            server.ServerName = newServerName;
            server.Provider = normalizedProvider;
            server.Port = port;
            server.AdminDatabase = normalizedAdminDatabase;
            server.AdminUsername = newAdminUsername;
            server.AdminPassword = adminPassword ?? string.Empty;
            server.PostgreSqlSslMode = string.IsNullOrWhiteSpace(postgreSqlSslMode)
                ? normalizedProvider == SqlProviders.PostgreSql
                    ? PostgreSqlSslModes.GetDefaultForNewServers()
                    : string.Empty
                : PostgreSqlSslModes.Normalize(postgreSqlSslMode);
            server.PostgreSqlPooling = normalizedProvider == SqlProviders.PostgreSql
                ? postgreSqlPooling ?? true
                : null;
            server.MySqlSslMode = string.IsNullOrWhiteSpace(mySqlSslMode)
                ? normalizedProvider == SqlProviders.MySql
                    ? MySqlSslModes.GetDefaultForNewServers()
                    : string.Empty
                : MySqlSslModes.Normalize(mySqlSslMode);
            server.MySqlPooling = normalizedProvider == SqlProviders.MySql
                ? mySqlPooling ?? true
                : null;
            server.MySqlAllowPublicKeyRetrieval = normalizedProvider == SqlProviders.MySql
                ? mySqlAllowPublicKeyRetrieval ?? false
                : null;
            server.SqlServerTrustMode = string.IsNullOrWhiteSpace(sqlServerTrustMode)
                ? normalizedProvider == SqlProviders.SqlServer
                    ? SqlServerTrustModes.GetDefaultForNewServers()
                    : string.Empty
                : SqlServerTrustModes.Normalize(sqlServerTrustMode);
            server.ConnectionTimeoutSeconds = connectionTimeoutSeconds is > 0 ? connectionTimeoutSeconds : null;
            server.CommandTimeoutSeconds = commandTimeoutSeconds is > 0 ? commandTimeoutSeconds : null;
            RefreshConnectionStrings(server);
            AddVersion(server.VersionHistory, "Server updated.", BuildServerVersionDetails(server));

            if (config.SelectedServerName.Equals(currentName, StringComparison.OrdinalIgnoreCase))
            {
                config.SelectedServerName = ServerConnections.GetIdentifier(server);
            }

            await SaveEditableConfigAsync(configPath, config, encryptionPassword, cancellationToken);
            return OperationResult.Success($"Connection '{currentName}' was updated.");
        });

    public Task<OperationResult> SelectServerAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var serverName = Require(options.ServerIdentifier ?? options.ServerName, "SelectServer requires a server identifier.");
            var config = await LoadEditableConfigAsync(options.ConfigPath, options.EncryptionPassword, cancellationToken);
            var server = FindServer(config, serverName);
            if (server is null)
            {
                throw new UserInputException($"Connection '{serverName}' was not found in the config.");
            }

            config.SelectedServerName = ServerConnections.GetIdentifier(server);
            await SaveEditableConfigAsync(options.ConfigPath, config, options.EncryptionPassword, cancellationToken);
            return OperationResult.Success($"Selected connection '{ServerConnections.GetSelectionDisplay(server)}'.");
        });

    public Task<OperationResult> SaveConfigAsync(string configPath, string? encryptionPassword, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var config = await LoadEditableConfigAsync(configPath, encryptionPassword, cancellationToken);
            await SaveEditableConfigAsync(configPath, config, encryptionPassword, cancellationToken);
            return OperationResult.Success($"Configuration saved to '{configPath}'.");
        });

    public Task<OperationResult> UpdateThemePreferenceAsync(string configPath, string? encryptionPassword, string themeName, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var config = await LoadEditableConfigAsync(configPath, encryptionPassword, cancellationToken);
            config.ThemeName = TerminalThemeCatalog.NormalizeThemeName(themeName);
            await SaveEditableConfigAsync(configPath, config, encryptionPassword, cancellationToken);
            return OperationResult.Success($"Theme set to '{config.ThemeName}'.");
        });

    public Task<OperationResult> TestServerConnectionAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, persistSelection: false, cancellationToken);
            var adminPassword = Require(context.AdminPassword, "TestConnection requires AdminPassword.");
            await ExecuteScalarIntAsync(
                context,
                adminPassword,
                BuildServerConnectionTestQuery(context.Provider),
                context.AdminDatabase,
                cancellationToken);

            return OperationResult.Success(
                $"Connected to '{context.ServerName}'.",
                $"Connection '{context.ServerDisplayName}' responded successfully using {SqlProviders.GetDisplayName(context.Provider)}.");
        });

    public Task<OperationResult> SyncServerAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, true, cancellationToken);
            var adminPassword = Require(context.AdminPassword, "SyncServer requires AdminPassword.");
            var databaseNames = await LoadServerDatabaseNamesAsync(context, adminPassword, cancellationToken);

            var synchronizedDatabases = new List<DatabaseConfig>();
            var userCount = 0;

            foreach (var databaseName in databaseNames)
            {
                var existingDatabase = context.ServerConfig?.Databases.FirstOrDefault(database => database.DatabaseName.Equals(databaseName, StringComparison.OrdinalIgnoreCase));
                var databaseConfig = new DatabaseConfig { DatabaseName = databaseName };
                var userRows = await QueryDatabaseUsersAsync(context, adminPassword, databaseName, cancellationToken);

                foreach (var userRow in userRows)
                {
                    var existingUser = existingDatabase?.Users.FirstOrDefault(user => user.Username.Equals(userRow.UserName, StringComparison.OrdinalIgnoreCase));
                    var password = existingUser?.Password ?? string.Empty;
                    databaseConfig.Users.Add(new UserConfig
                    {
                        Username = userRow.UserName,
                        Password = password,
                        Roles = SplitRoleList(userRow.Roles),
                    ConnectionString = BuildConnectionString(context, databaseName, userRow.UserName, password)
                    });
                    userCount++;
                }

                synchronizedDatabases.Add(databaseConfig);
            }

            var serverConfig = GetOrCreateServer(context);
            serverConfig.Databases = synchronizedDatabases;
            serverConfig.AdminUsername = context.AdminUsername;
            serverConfig.AdminPassword = context.AdminPassword;
            context.Config.SelectedServerName = context.ServerIdentifier;
            await SaveEditableConfigAsync(options.ConfigPath, context.Config, options.EncryptionPassword, cancellationToken);

            return OperationResult.Success(
                $"Synchronized {databaseNames.Count} database(s) and {userCount} user record(s) from '{context.ServerName}'.");
        });

    public Task<OperationResult<IReadOnlyList<string>>> ShowDatabasesAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, true, cancellationToken);
            var adminPassword = Require(context.AdminPassword, "ShowDatabases requires AdminPassword.");
            var databaseNames = await LoadServerDatabaseNamesAsync(context, adminPassword, cancellationToken);
            var message = databaseNames.Count == 0
                ? $"No user databases were found on '{context.ServerName}'."
                : $"Retrieved {databaseNames.Count} database(s) from '{context.ServerName}'.";
            return OperationResult<IReadOnlyList<string>>.Success(databaseNames, message);
        });

    public Task<OperationResult> CreateDatabaseAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, true, cancellationToken);
            var adminPassword = Require(context.AdminPassword, "CreateDatabase requires AdminPassword.");
            var databaseName = Require(options.DatabaseName, "CreateDatabase requires DatabaseName.");
            if (IsSqlServer(context.Provider))
            {
                var quotedDatabase = QuoteSqlServerIdentifier(databaseName);
                var query = $"""
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = {QuoteSqlLiteral(databaseName)})
BEGIN
    EXEC('CREATE DATABASE {quotedDatabase}');
END
""";

                await ExecuteNonQueryAsync(context, adminPassword, query, context.AdminDatabase, cancellationToken);
            }
            else if (IsPostgreSql(context.Provider))
            {
                var databaseExists = await ExecuteScalarIntAsync(
                    context,
                    adminPassword,
                    $"SELECT COUNT(1) FROM pg_database WHERE datname = {QuotePostgreSqlLiteral(databaseName)};",
                    context.AdminDatabase,
                    cancellationToken) > 0;

                if (!databaseExists)
                {
                    await ExecuteNonQueryAsync(
                        context,
                        adminPassword,
                        $"CREATE DATABASE {QuotePostgreSqlIdentifier(databaseName)};",
                        context.AdminDatabase,
                        cancellationToken);
                }
            }
            else
            {
                var databaseExists = await ExecuteScalarIntAsync(
                    context,
                    adminPassword,
                    $"SELECT COUNT(1) FROM information_schema.schemata WHERE schema_name = {QuoteMySqlLiteral(databaseName)};",
                    context.AdminDatabase,
                    cancellationToken) > 0;

                if (!databaseExists)
                {
                    await ExecuteNonQueryAsync(
                        context,
                        adminPassword,
                        $"CREATE DATABASE {QuoteMySqlIdentifier(databaseName)};",
                        context.AdminDatabase,
                        cancellationToken);
                }
            }

            var serverConfig = GetOrCreateServer(context);
            var databaseConfig = GetOrCreateDatabase(serverConfig, databaseName);
            AddVersion(databaseConfig.VersionHistory, "Database added to config.", BuildDatabaseVersionDetails(serverConfig, databaseConfig));
            context.Config.SelectedServerName = context.ServerIdentifier;
            await SaveEditableConfigAsync(options.ConfigPath, context.Config, options.EncryptionPassword, cancellationToken);

            return OperationResult.Success($"Database '{databaseName}' is ready on '{context.ServerName}'.");
        });

    public Task<OperationResult> RemoveDatabaseAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, true, cancellationToken);
            var adminPassword = Require(context.AdminPassword, "RemoveDatabase requires AdminPassword.");
            var databaseName = Require(options.DatabaseName, "RemoveDatabase requires DatabaseName.");
            var databaseExistsQuery = IsSqlServer(context.Provider)
                ? $"SELECT COUNT(1) FROM sys.databases WHERE name = {QuoteSqlLiteral(databaseName)};"
                : IsPostgreSql(context.Provider)
                    ? $"SELECT COUNT(1) FROM pg_database WHERE datname = {QuotePostgreSqlLiteral(databaseName)};"
                    : $"SELECT COUNT(1) FROM information_schema.schemata WHERE schema_name = {QuoteMySqlLiteral(databaseName)};";
            var databaseExists = await ExecuteScalarIntAsync(
                context,
                adminPassword,
                databaseExistsQuery,
                context.AdminDatabase,
                cancellationToken) > 0;

            if (!databaseExists)
            {
                throw new UserInputException($"Database '{databaseName}' does not exist on '{context.ServerName}'.");
            }

            if (IsSqlServer(context.Provider))
            {
                var quotedDatabase = QuoteSqlServerIdentifier(databaseName);
                var query = $"ALTER DATABASE {quotedDatabase} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quotedDatabase};";
                await ExecuteNonQueryAsync(context, adminPassword, query, context.AdminDatabase, cancellationToken);
            }
            else if (IsPostgreSql(context.Provider))
            {
                var quotedDatabase = QuotePostgreSqlIdentifier(databaseName);
                var query = $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = {QuotePostgreSqlLiteral(databaseName)} AND pid <> pg_backend_pid(); DROP DATABASE {quotedDatabase};";
                await ExecuteNonQueryAsync(context, adminPassword, query, context.AdminDatabase, cancellationToken);
            }
            else
            {
                var quotedDatabase = QuoteMySqlIdentifier(databaseName);
                var query = $"DROP DATABASE {quotedDatabase};";
                await ExecuteNonQueryAsync(context, adminPassword, query, context.AdminDatabase, cancellationToken);
            }

            var serverConfig = GetOrCreateServer(context);
            SoftDeleteDatabase(context.Config, serverConfig, databaseName, options.EncryptionPassword);
            context.Config.SelectedServerName = context.ServerIdentifier;
            await SaveEditableConfigAsync(options.ConfigPath, context.Config, options.EncryptionPassword, cancellationToken);

            return OperationResult.Success($"Removed database '{databaseName}' from '{context.ServerName}'.");
        });

    public Task<OperationResult> CreateUserAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, true, cancellationToken);
            var adminPassword = Require(context.AdminPassword, "CreateUser requires AdminPassword.");
            var databaseName = Require(options.DatabaseName, "CreateUser requires DatabaseName.");
            var userName = Require(options.UserName, "CreateUser requires UserName.");
            var roles = NormalizeRoles(options.Roles, context.Provider);
            if (roles.Count == 0)
            {
                throw new UserInputException("CreateUser requires Roles.");
            }

            var loginExistsQuery = IsSqlServer(context.Provider)
                ? $"SELECT COUNT(1) FROM sys.server_principals WHERE name = {QuoteSqlLiteral(userName)};"
                : IsPostgreSql(context.Provider)
                    ? $"SELECT COUNT(1) FROM pg_roles WHERE rolname = {QuotePostgreSqlLiteral(userName)};"
                    : $"SELECT COUNT(1) FROM mysql.user WHERE User = {QuoteMySqlLiteral(userName)} AND Host = '%';";
            var loginExists = await ExecuteScalarIntAsync(
                context,
                adminPassword,
                loginExistsQuery,
                context.AdminDatabase,
                cancellationToken) > 0;

            var resolvedPassword = ResolveRequestedPassword(options.NewUserPassword, loginExists, context.ServerConfig, userName);
            await EnsureServerLoginAsync(context, adminPassword, databaseName, userName, resolvedPassword.Password, loginExists, !string.IsNullOrWhiteSpace(options.NewUserPassword), cancellationToken);
            var membershipQuery = BuildRoleMembershipSyncQuery(context, databaseName, userName, roles, failIfUserMissing: false);
            await ExecuteNonQueryAsync(context, adminPassword, membershipQuery, GetAdministrativeExecutionDatabase(context, databaseName), cancellationToken);

            var serverConfig = GetOrCreateServer(context);
            var databaseConfig = GetOrCreateDatabase(serverConfig, databaseName);
            var userConfig = GetOrCreateUser(databaseConfig, userName);
            userConfig.Password = resolvedPassword.Password;
            userConfig.Roles = roles.ToList();
            userConfig.ConnectionString = BuildConnectionString(context, databaseName, userName, resolvedPassword.Password);
            AddVersion(userConfig.VersionHistory, "User created or updated.", BuildUserVersionDetails(serverConfig, databaseConfig, userConfig));
            context.Config.SelectedServerName = context.ServerIdentifier;
            await SaveEditableConfigAsync(options.ConfigPath, context.Config, options.EncryptionPassword, cancellationToken);

            return OperationResult.Success(
                $"SQL user '{userName}' is ready for database '{databaseName}'.",
                resolvedPassword.SourceMessage,
                $"Connection string: {userConfig.ConnectionString}");
        });

    public Task<OperationResult> SetUserAccessAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, true, cancellationToken);
            var adminPassword = Require(context.AdminPassword, "SetUserAccess requires AdminPassword.");
            var userName = Require(options.UserName, "SetUserAccess requires UserName.");
            var assignments = NormalizeDatabaseRoleAssignments(options.DatabaseRoleAssignments, context.Provider);
            if (assignments.Count == 0)
            {
                throw new UserInputException("SetUserAccess requires at least one database role selection.");
            }

            var loginExistsQuery = IsSqlServer(context.Provider)
                ? $"SELECT COUNT(1) FROM sys.server_principals WHERE name = {QuoteSqlLiteral(userName)};"
                : IsPostgreSql(context.Provider)
                    ? $"SELECT COUNT(1) FROM pg_roles WHERE rolname = {QuotePostgreSqlLiteral(userName)};"
                    : $"SELECT COUNT(1) FROM mysql.user WHERE User = {QuoteMySqlLiteral(userName)} AND Host = '%';";
            var loginExists = await ExecuteScalarIntAsync(
                context,
                adminPassword,
                loginExistsQuery,
                context.AdminDatabase,
                cancellationToken) > 0;

            var serverConfig = GetOrCreateServer(context);
            var knownPassword = GetStoredPasswordForUser(serverConfig, userName);
            var passwordForConfig = knownPassword;
            string passwordMessage;

            if (!loginExists)
            {
                var firstAssignedDatabase = assignments.FirstOrDefault(assignment => assignment.Roles.Count > 0)?.DatabaseName;
                if (string.IsNullOrWhiteSpace(firstAssignedDatabase))
                {
                    throw new UserInputException("At least one database role must be selected when creating a new SQL login.");
                }

                passwordForConfig = string.IsNullOrWhiteSpace(options.NewUserPassword)
                    ? _passwordGenerator.Generate()
                    : options.NewUserPassword!;
                passwordMessage = string.IsNullOrWhiteSpace(options.NewUserPassword)
                    ? "Password was generated automatically."
                    : "Password was set from the supplied NewUserPassword value.";

                await EnsureServerLoginAsync(context, adminPassword, firstAssignedDatabase, userName, passwordForConfig, false, false, cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(options.NewUserPassword))
            {
                passwordForConfig = options.NewUserPassword!;
                passwordMessage = "Password was set from the supplied NewUserPassword value.";

                await EnsureServerLoginAsync(context, adminPassword, assignments[0].DatabaseName, userName, passwordForConfig, true, true, cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(knownPassword))
            {
                passwordMessage = "Password was reused from the config.";
            }
            else
            {
                passwordMessage = "Existing login password is not stored in config. Saved connection strings will use a password placeholder.";
            }

            foreach (var assignment in assignments)
            {
                if (assignment.Roles.Count == 0)
                {
                    var dropUserQuery = BuildRemoveDatabaseUserQuery(context, assignment.DatabaseName, userName);
                    await ExecuteNonQueryAsync(context, adminPassword, dropUserQuery, GetAdministrativeExecutionDatabase(context, assignment.DatabaseName), cancellationToken);
                    SoftDeleteUsersFromConfigDatabases(context.Config, serverConfig, userName, [assignment.DatabaseName], options.EncryptionPassword);
                    continue;
                }

                var syncQuery = BuildRoleMembershipSyncQuery(context, assignment.DatabaseName, userName, assignment.Roles, failIfUserMissing: false);
                await ExecuteNonQueryAsync(context, adminPassword, syncQuery, GetAdministrativeExecutionDatabase(context, assignment.DatabaseName), cancellationToken);

                var databaseConfig = GetOrCreateDatabase(serverConfig, assignment.DatabaseName);
                var userConfig = GetOrCreateUser(databaseConfig, userName);
                userConfig.Password = passwordForConfig;
                userConfig.Roles = assignment.Roles.ToList();
                    userConfig.ConnectionString = BuildConnectionString(context, assignment.DatabaseName, userName, passwordForConfig);
                AddVersion(userConfig.VersionHistory, "User access updated.", BuildUserVersionDetails(serverConfig, databaseConfig, userConfig));
            }

            context.Config.SelectedServerName = context.ServerIdentifier;
            await SaveEditableConfigAsync(options.ConfigPath, context.Config, options.EncryptionPassword, cancellationToken);

            var assignmentSummary = string.Join(", ",
                assignments.Select(assignment => $"{assignment.DatabaseName}=[{(assignment.Roles.Count == 0 ? "no access" : string.Join('/', assignment.Roles))}]")
            );

            return OperationResult.Success(
                $"Updated database access for '{userName}' on '{context.ServerName}'.",
                passwordMessage,
                $"Applied: {assignmentSummary}");
        });

    public Task<OperationResult> AddRoleAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, true, cancellationToken);
            var adminPassword = Require(context.AdminPassword, "AddRole requires AdminPassword.");
            var databaseName = Require(options.DatabaseName, "AddRole requires DatabaseName.");
            var userName = Require(options.UserName, "AddRole requires UserName.");
            var roles = NormalizeRoles(options.Roles, context.Provider);
            if (roles.Count == 0)
            {
                throw new UserInputException("AddRole requires Roles.");
            }

            var query = BuildAddRolesQuery(context, databaseName, userName, roles);
            await ExecuteNonQueryAsync(context, adminPassword, query, GetAdministrativeExecutionDatabase(context, databaseName), cancellationToken);

            var serverConfig = GetOrCreateServer(context);
            var databaseConfig = GetOrCreateDatabase(serverConfig, databaseName);
            var userConfig = GetOrCreateUser(databaseConfig, userName);
            userConfig.Roles = userConfig.Roles.Concat(roles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            userConfig.ConnectionString = BuildConnectionString(context, databaseName, userName, userConfig.Password);
            AddVersion(userConfig.VersionHistory, $"Roles added: {string.Join(", ", roles)}.", BuildUserVersionDetails(serverConfig, databaseConfig, userConfig));
            context.Config.SelectedServerName = context.ServerIdentifier;
            await SaveEditableConfigAsync(options.ConfigPath, context.Config, options.EncryptionPassword, cancellationToken);

            return OperationResult.Success($"Added role(s) '{string.Join(", ", roles)}' to '{userName}' on '{databaseName}'.");
        });

    public Task<OperationResult> RemoveRoleAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, true, cancellationToken);
            var adminPassword = Require(context.AdminPassword, "RemoveRole requires AdminPassword.");
            var databaseName = Require(options.DatabaseName, "RemoveRole requires DatabaseName.");
            var userName = Require(options.UserName, "RemoveRole requires UserName.");
            var roles = NormalizeRoles(options.Roles, context.Provider);
            if (roles.Count == 0)
            {
                throw new UserInputException("RemoveRole requires Roles.");
            }

            var query = BuildRemoveRolesQuery(context, databaseName, userName, roles);
            await ExecuteNonQueryAsync(context, adminPassword, query, GetAdministrativeExecutionDatabase(context, databaseName), cancellationToken);

            var serverConfig = GetOrCreateServer(context);
            var databaseConfig = GetOrCreateDatabase(serverConfig, databaseName);
            var userConfig = GetOrCreateUser(databaseConfig, userName);
            userConfig.Roles = userConfig.Roles.Except(roles, StringComparer.OrdinalIgnoreCase).ToList();
            userConfig.ConnectionString = BuildConnectionString(context, databaseName, userName, userConfig.Password);
            AddVersion(userConfig.VersionHistory, $"Roles removed: {string.Join(", ", roles)}.", BuildUserVersionDetails(serverConfig, databaseConfig, userConfig));
            context.Config.SelectedServerName = context.ServerIdentifier;
            await SaveEditableConfigAsync(options.ConfigPath, context.Config, options.EncryptionPassword, cancellationToken);

            return OperationResult.Success($"Removed role(s) '{string.Join(", ", roles)}' from '{userName}' on '{databaseName}'.");
        });

    public Task<OperationResult<IReadOnlyList<DatabaseUserRow>>> ShowUsersAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, true, cancellationToken);
            var adminPassword = Require(context.AdminPassword, "ShowUsers requires AdminPassword.");
            var databaseName = Require(options.DatabaseName, "ShowUsers requires DatabaseName.");
            var rows = await QueryDatabaseUsersAsync(context, adminPassword, databaseName, cancellationToken);
            var message = rows.Count == 0
                ? $"No matching users were found in database '{databaseName}'."
                : $"Retrieved {rows.Count} user(s) from '{databaseName}'.";
            return OperationResult<IReadOnlyList<DatabaseUserRow>>.Success(rows, message);
        });

    public Task<OperationResult> TestUserLoginAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, persistSelection: false, cancellationToken, requireAdminUsername: false);
            var databaseName = Require(options.DatabaseName, "TestUserLogin requires DatabaseName.");
            var userName = Require(options.UserName, "TestUserLogin requires UserName.");
            var password = ResolveUserLoginPassword(options.NewUserPassword, context.ServerConfig, databaseName, userName);
            var testValue = await ExecuteScalarIntAsync(
                context,
                userName,
                password,
                "SELECT 1;",
                databaseName,
                cancellationToken);

            if (testValue != 1)
            {
                throw new UserInputException($"User login test returned an unexpected result for '{userName}' on '{databaseName}'.");
            }

            return OperationResult.Success(
                $"Validated login for '{userName}' on database '{databaseName}'.",
                $"Provider: {SqlProviders.GetDisplayName(context.Provider)}",
                $"Connection string: {BuildConnectionString(context, databaseName, userName, password)}");
        });

    public Task<OperationResult> RemoveUserAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, true, cancellationToken);
            var adminPassword = Require(context.AdminPassword, "RemoveUser requires AdminPassword.");
            var userName = Require(options.UserName, "RemoveUser requires UserName.");

            var removeServerLogin = options.RemoveServerLogin || options.RemovalScope is RemovalScope.Server or RemovalScope.Both;
            var selectedDatabases = removeServerLogin
                ? await LoadServerDatabaseNamesAsync(context, adminPassword, cancellationToken)
                : GetRequestedDatabaseNames(options, requireOne: options.RemovalScope == RemovalScope.Database, "DatabaseName is required when RemovalScope is Database.");

            foreach (var databaseName in selectedDatabases)
            {
                var dropUserQuery = BuildRemoveDatabaseUserQuery(context, databaseName, userName);
                await ExecuteNonQueryAsync(context, adminPassword, dropUserQuery, GetAdministrativeExecutionDatabase(context, databaseName), cancellationToken);
            }

            var serverConfig = GetOrCreateServer(context);
            if (selectedDatabases.Count > 0)
            {
                SoftDeleteUsersFromConfigDatabases(context.Config, serverConfig, userName, selectedDatabases, options.EncryptionPassword);
            }

            if (removeServerLogin)
            {
                var dropLoginQuery = BuildDropLoginQuery(context, userName);
                await ExecuteNonQueryAsync(context, adminPassword, dropLoginQuery, context.AdminDatabase, cancellationToken);
            }

            context.Config.SelectedServerName = context.ServerIdentifier;
            await SaveEditableConfigAsync(options.ConfigPath, context.Config, options.EncryptionPassword, cancellationToken);

            var scopeMessage = removeServerLogin
                ? $"Removed '{userName}' from selected databases and dropped the server login."
                : $"Removed '{userName}' from {selectedDatabases.Count} database(s).";
            return OperationResult.Success(scopeMessage);
        });

    public Task<OperationResult> UpdatePasswordAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, true, cancellationToken);
            var adminPassword = Require(context.AdminPassword, "UpdatePassword requires AdminPassword.");
            var userName = Require(options.UserName, "UpdatePassword requires UserName.");
            var loginExistsQuery = IsSqlServer(context.Provider)
                ? $"SELECT COUNT(1) FROM sys.server_principals WHERE name = {QuoteSqlLiteral(userName)};"
                : IsPostgreSql(context.Provider)
                    ? $"SELECT COUNT(1) FROM pg_roles WHERE rolname = {QuotePostgreSqlLiteral(userName)};"
                    : $"SELECT COUNT(1) FROM mysql.user WHERE User = {QuoteMySqlLiteral(userName)} AND Host = '%';";
            var loginExists = await ExecuteScalarIntAsync(
                context,
                adminPassword,
                loginExistsQuery,
                context.AdminDatabase,
                cancellationToken);

            if (loginExists == 0)
            {
                var subject = IsPostgreSql(context.Provider) ? "Role" : "Login";
                throw new UserInputException($"{subject} '{userName}' does not exist on '{context.ServerName}'.");
            }

            var newPassword = string.IsNullOrWhiteSpace(options.NewUserPassword)
                ? _passwordGenerator.Generate()
                : options.NewUserPassword!;
            await EnsureServerLoginAsync(context, adminPassword, context.AdminDatabase, userName, newPassword, true, true, cancellationToken);

            var serverConfig = GetOrCreateServer(context);
            UpdateUserPasswordInConfig(serverConfig, userName, newPassword);
            foreach (var database in serverConfig.Databases)
            {
                foreach (var user in database.Users.Where(candidate => candidate.Username.Equals(userName, StringComparison.OrdinalIgnoreCase)))
                {
                    AddVersion(user.VersionHistory, "Password updated.", BuildUserVersionDetails(serverConfig, database, user));
                }
            }
            context.Config.SelectedServerName = context.ServerIdentifier;
            await SaveEditableConfigAsync(options.ConfigPath, context.Config, options.EncryptionPassword, cancellationToken);

            return OperationResult.Success(
                $"Password updated for '{userName}'.",
                string.IsNullOrWhiteSpace(options.NewUserPassword)
                    ? "Password was generated automatically."
                    : "Password was set from the supplied value.");
        });

    private async Task<ResolvedServerContext> ResolveServerContextAsync(CommandOptions options, bool persistSelection, CancellationToken cancellationToken, bool requireAdminUsername = true)
    {
        var config = await LoadEditableConfigAsync(options.ConfigPath, options.EncryptionPassword, cancellationToken);
        var requestedServer = !string.IsNullOrWhiteSpace(options.ServerIdentifier)
            ? options.ServerIdentifier
            : options.ServerName;
        if (string.IsNullOrWhiteSpace(requestedServer))
        {
            requestedServer = SelectConfiguredServerName(config);
        }

        if (string.IsNullOrWhiteSpace(requestedServer))
        {
            throw new UserInputException("No server is configured. Run InitConfig or AddServer first, or pass ServerName explicitly.");
        }

        var serverConfig = FindServer(config, requestedServer);
        var serverName = string.IsNullOrWhiteSpace(options.ServerName)
            ? serverConfig?.ServerName ?? requestedServer
            : options.ServerName;
        var serverIdentifier = serverConfig is null
            ? (string.IsNullOrWhiteSpace(options.ServerIdentifier)
                ? ServerConnections.GetNextIdentifier(config.Servers)
                : options.ServerIdentifier!)
            : ServerConnections.GetIdentifier(serverConfig);
        var serverDisplayName = serverConfig is null
            ? (string.IsNullOrWhiteSpace(options.DisplayName) ? serverName : options.DisplayName!)
            : ServerConnections.GetDisplayName(serverConfig);
        var provider = SqlProviders.Normalize(options.Provider ?? serverConfig?.Provider);
        var port = options.Port ?? serverConfig?.Port;
        var adminDatabase = string.IsNullOrWhiteSpace(options.AdminDatabase)
            ? serverConfig?.AdminDatabase
            : options.AdminDatabase;
        adminDatabase = string.IsNullOrWhiteSpace(adminDatabase)
            ? SqlProviders.GetDefaultAdminDatabase(provider)
            : adminDatabase;

        var adminUsername = string.IsNullOrWhiteSpace(options.AdminUsername)
            ? serverConfig?.AdminUsername
            : options.AdminUsername;
        var adminPassword = string.IsNullOrWhiteSpace(options.AdminPassword)
            ? serverConfig?.AdminPassword
            : options.AdminPassword;
        var postgreSqlSslMode = !string.IsNullOrWhiteSpace(options.PostgreSqlSslMode)
            ? PostgreSqlSslModes.Normalize(options.PostgreSqlSslMode)
            : serverConfig is null
                ? PostgreSqlSslModes.GetDefaultForNewServers()
                : PostgreSqlSslModes.GetEffective(serverConfig.PostgreSqlSslMode);
        var postgreSqlPooling = options.PostgreSqlPooling
            ?? serverConfig?.PostgreSqlPooling
            ?? true;
        var mySqlSslMode = !string.IsNullOrWhiteSpace(options.MySqlSslMode)
            ? MySqlSslModes.Normalize(options.MySqlSslMode)
            : serverConfig is null
                ? MySqlSslModes.GetDefaultForNewServers()
                : MySqlSslModes.GetEffective(serverConfig.MySqlSslMode);
        var mySqlPooling = options.MySqlPooling
            ?? serverConfig?.MySqlPooling
            ?? true;
        var mySqlAllowPublicKeyRetrieval = options.MySqlAllowPublicKeyRetrieval
            ?? serverConfig?.MySqlAllowPublicKeyRetrieval
            ?? false;
        var sqlServerTrustMode = !string.IsNullOrWhiteSpace(options.SqlServerTrustMode)
            ? SqlServerTrustModes.Normalize(options.SqlServerTrustMode)
            : serverConfig is null
                ? SqlServerTrustModes.GetDefaultForNewServers()
                : SqlServerTrustModes.GetEffective(serverConfig.SqlServerTrustMode);
        var resolvedTimeouts = new SqlTimeoutConfig
        {
            ConnectionTimeoutSeconds = options.ConnectionTimeoutSeconds is > 0
                ? options.ConnectionTimeoutSeconds.Value
                : serverConfig?.ConnectionTimeoutSeconds is > 0
                    ? serverConfig.ConnectionTimeoutSeconds.Value
                    : config.Timeouts.ConnectionTimeoutSeconds,
            CommandTimeoutSeconds = options.CommandTimeoutSeconds is > 0
                ? options.CommandTimeoutSeconds.Value
                : serverConfig?.CommandTimeoutSeconds is > 0
                    ? serverConfig.CommandTimeoutSeconds.Value
                    : config.Timeouts.CommandTimeoutSeconds
        };

        if (requireAdminUsername && string.IsNullOrWhiteSpace(adminUsername))
        {
            throw new UserInputException("AdminUsername is required for this operation.");
        }

        if (persistSelection)
        {
            config.SelectedServerName = serverIdentifier;
            if (serverConfig is not null)
            {
                serverConfig.Provider = provider;
                serverConfig.Port = port;
                serverConfig.AdminDatabase = adminDatabase;
                if (!string.IsNullOrWhiteSpace(options.AdminUsername))
                {
                    serverConfig.AdminUsername = options.AdminUsername!;
                }

                if (!string.IsNullOrWhiteSpace(options.AdminPassword))
                {
                    serverConfig.AdminPassword = options.AdminPassword!;
                }

                if (!string.IsNullOrWhiteSpace(options.PostgreSqlSslMode))
                {
                    serverConfig.PostgreSqlSslMode = postgreSqlSslMode;
                }

                if (options.PostgreSqlPooling.HasValue)
                {
                    serverConfig.PostgreSqlPooling = options.PostgreSqlPooling.Value;
                }

                if (!string.IsNullOrWhiteSpace(options.MySqlSslMode))
                {
                    serverConfig.MySqlSslMode = mySqlSslMode;
                }

                if (options.MySqlPooling.HasValue)
                {
                    serverConfig.MySqlPooling = options.MySqlPooling.Value;
                }

                if (options.MySqlAllowPublicKeyRetrieval.HasValue)
                {
                    serverConfig.MySqlAllowPublicKeyRetrieval = options.MySqlAllowPublicKeyRetrieval.Value;
                }

                if (!string.IsNullOrWhiteSpace(options.SqlServerTrustMode))
                {
                    serverConfig.SqlServerTrustMode = sqlServerTrustMode;
                }

                if (options.ConnectionTimeoutSeconds is > 0)
                {
                    serverConfig.ConnectionTimeoutSeconds = options.ConnectionTimeoutSeconds.Value;
                }

                if (options.CommandTimeoutSeconds is > 0)
                {
                    serverConfig.CommandTimeoutSeconds = options.CommandTimeoutSeconds.Value;
                }
            }

            await SaveEditableConfigAsync(options.ConfigPath, config, options.EncryptionPassword, cancellationToken);
        }

        return new ResolvedServerContext(
            config,
            serverConfig,
            serverIdentifier,
            serverDisplayName,
            serverName,
            provider,
            port,
            adminDatabase!,
            adminUsername ?? string.Empty,
            adminPassword ?? string.Empty,
            resolvedTimeouts,
            postgreSqlSslMode,
            postgreSqlPooling,
            mySqlSslMode,
            mySqlPooling,
            mySqlAllowPublicKeyRetrieval,
            sqlServerTrustMode);
    }

    private async Task<SqlManagerConfig> LoadDisplayConfigAsync(string configPath, string? encryptionPassword, CancellationToken cancellationToken)
    {
        var storedConfig = await _configStore.LoadAsync(configPath, cancellationToken);
        return PrepareConfigForUse(storedConfig, encryptionPassword, requireUnlock: false);
    }

    private async Task<SqlManagerConfig> LoadEditableConfigAsync(string configPath, string? encryptionPassword, CancellationToken cancellationToken)
    {
        var storedConfig = await _configStore.LoadAsync(configPath, cancellationToken);
        return PrepareConfigForUse(storedConfig, encryptionPassword, requireUnlock: storedConfig.EncryptPasswords);
    }

    private async Task SaveEditableConfigAsync(string configPath, SqlManagerConfig config, string? encryptionPassword, CancellationToken cancellationToken)
    {
        var configToPersist = CloneConfig(config);
        if (!configToPersist.EncryptPasswords)
        {
            configToPersist.EncryptionKey = string.Empty;
            configToPersist.EncryptedPayload = string.Empty;
            MarkPlaintextSecrets(configToPersist);
            RefreshConnectionStrings(configToPersist);
            ClearDatabasePayloads(configToPersist);
            MarkPlaintextTrashPayloads(configToPersist);
            await _configStore.SaveAsync(configPath, configToPersist, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(encryptionPassword))
        {
            throw new UserInputException("Config passwords are encrypted. Supply EncryptionPassword or unlock password encryption from the Configuration menu.");
        }

        if (string.IsNullOrWhiteSpace(configToPersist.EncryptionKey))
        {
            configToPersist.EncryptionKey = _configPasswordProtector.CreateEncryptionKey(encryptionPassword!);
        }
        else if (!_configPasswordProtector.VerifyUnlockPassword(encryptionPassword!, configToPersist.EncryptionKey))
        {
            throw new UserInputException("Encryption password is invalid.");
        }

        ClearDatabasePayloads(configToPersist);
        MarkPlaintextTrashPayloads(configToPersist);
        MarkProtectedAtRest(configToPersist);
        RefreshConnectionStrings(configToPersist);
        configToPersist.EncryptedPayload = _configPasswordProtector.EncryptSecret(SerializeFullConfigPayload(configToPersist), encryptionPassword!);
        configToPersist.SelectedServerName = string.Empty;
        configToPersist.Timeouts = new SqlTimeoutConfig();
        configToPersist.Servers = [];
        configToPersist.Trash = [];
        await _configStore.SaveAsync(configPath, configToPersist, cancellationToken);
    }

    private SqlManagerConfig PrepareConfigForUse(SqlManagerConfig storedConfig, string? encryptionPassword, bool requireUnlock)
    {
        var workingConfig = CloneConfig(storedConfig);
        if (!workingConfig.EncryptPasswords)
        {
            MarkPlaintextSecrets(workingConfig);
            RefreshConnectionStrings(workingConfig);
            ClearDatabasePayloads(workingConfig);
            MarkPlaintextTrashPayloads(workingConfig);
            return workingConfig;
        }

        if (string.IsNullOrWhiteSpace(workingConfig.EncryptionKey))
        {
            throw new UserInputException("Config password encryption is enabled, but no encryption metadata was found.");
        }

        if (string.IsNullOrWhiteSpace(encryptionPassword))
        {
            if (requireUnlock)
            {
                throw new UserInputException("Config passwords are encrypted. Supply EncryptionPassword or unlock password encryption from the Configuration menu.");
            }

            return BuildLockedConfigView(workingConfig);
        }

        if (!_configPasswordProtector.VerifyUnlockPassword(encryptionPassword!, workingConfig.EncryptionKey))
        {
            throw new UserInputException("Encryption password is invalid.");
        }

        if (HasFullConfigEnvelope(workingConfig))
        {
            var decryptedConfig = DeserializeFullConfigPayload(_configPasswordProtector.DecryptSecret(workingConfig.EncryptedPayload, encryptionPassword!));
            decryptedConfig.EncryptPasswords = true;
            decryptedConfig.EncryptionKey = workingConfig.EncryptionKey;
            decryptedConfig.EncryptedPayload = string.Empty;
            ConfigStore.NormalizeLoadedConfig(decryptedConfig);
            RefreshConnectionStrings(decryptedConfig);
            return decryptedConfig;
        }

        DecryptDatabasePayloads(workingConfig, encryptionPassword!);
        DecryptSecrets(workingConfig, encryptionPassword!);
        RefreshConnectionStrings(workingConfig);
        DecryptTrashPayloads(workingConfig, encryptionPassword!);
        return workingConfig;
    }

    private static SqlManagerConfig CloneConfig(SqlManagerConfig source)
        => new()
        {
            SelectedServerName = source.SelectedServerName,
            ThemeName = source.ThemeName,
            EncryptPasswords = source.EncryptPasswords,
            EncryptionKey = source.EncryptionKey,
            EncryptedPayload = source.EncryptedPayload,
            Timeouts = new SqlTimeoutConfig
            {
                ConnectionTimeoutSeconds = source.Timeouts.ConnectionTimeoutSeconds,
                CommandTimeoutSeconds = source.Timeouts.CommandTimeoutSeconds
            },
            Servers = source.Servers.Select(server => new ServerConfig
            {
                EntryId = server.EntryId,
                ServerIdentifier = server.ServerIdentifier,
                DisplayName = server.DisplayName,
                ServerName = server.ServerName,
                Provider = server.Provider,
                Port = server.Port,
                AdminDatabase = server.AdminDatabase,
                AdminUsername = server.AdminUsername,
                AdminPassword = server.AdminPassword,
                PostgreSqlSslMode = server.PostgreSqlSslMode,
                PostgreSqlPooling = server.PostgreSqlPooling,
                MySqlSslMode = server.MySqlSslMode,
                MySqlPooling = server.MySqlPooling,
                MySqlAllowPublicKeyRetrieval = server.MySqlAllowPublicKeyRetrieval,
                SqlServerTrustMode = server.SqlServerTrustMode,
                ConnectionTimeoutSeconds = server.ConnectionTimeoutSeconds,
                CommandTimeoutSeconds = server.CommandTimeoutSeconds,
                DatabasesPayload = server.DatabasesPayload,
                Encrypted = server.Encrypted,
                VersionHistory = server.VersionHistory.Select(CloneVersion).ToList(),
                Databases = server.Databases.Select(database => new DatabaseConfig
                {
                    EntryId = database.EntryId,
                    DatabaseName = database.DatabaseName,
                    VersionHistory = database.VersionHistory.Select(CloneVersion).ToList(),
                    Users = database.Users.Select(user => new UserConfig
                    {
                        EntryId = user.EntryId,
                        Username = user.Username,
                        Password = user.Password,
                        Encrypted = user.Encrypted,
                        Roles = user.Roles.ToList(),
                        ConnectionString = user.ConnectionString,
                        VersionHistory = user.VersionHistory.Select(CloneVersion).ToList()
                    }).ToList()
                }).ToList()
            }).ToList(),
            Trash = source.Trash.Select(entry => new TrashEntry
            {
                TrashId = entry.TrashId,
                EntryType = entry.EntryType,
                EntryId = entry.EntryId,
                DisplayName = entry.DisplayName,
                ParentServerName = entry.ParentServerName,
                ParentDatabaseName = entry.ParentDatabaseName,
                DeletedAtUtc = entry.DeletedAtUtc,
                Details = entry.Details,
                PayloadJson = entry.PayloadJson,
                VersionHistory = entry.VersionHistory.Select(CloneVersion).ToList()
            }).ToList()
        };

    private SqlManagerConfig BuildLockedConfigView(SqlManagerConfig config)
    {
        if (HasFullConfigEnvelope(config))
        {
            config.SelectedServerName = string.Empty;
            config.Timeouts = new SqlTimeoutConfig();
            config.Servers = [];
            config.Trash = [];
            return config;
        }

        foreach (var server in config.Servers)
        {
            if (server.Encrypted)
            {
                server.AdminPassword = string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(server.DatabasesPayload))
            {
                server.Databases = [];
            }

            foreach (var user in server.Databases.SelectMany(database => database.Users))
            {
                if (user.Encrypted)
                {
                    user.Password = string.Empty;
                }
            }
        }

        foreach (var trashEntry in config.Trash)
        {
            trashEntry.PayloadJson = string.Empty;
        }

        return config;
    }

    private void DecryptSecrets(SqlManagerConfig config, string encryptionPassword)
    {
        foreach (var server in config.Servers)
        {
            if (server.Encrypted && !string.IsNullOrWhiteSpace(server.AdminPassword))
            {
                server.AdminPassword = _configPasswordProtector.DecryptSecret(server.AdminPassword, encryptionPassword);
            }

            foreach (var user in server.Databases.SelectMany(database => database.Users))
            {
                if (user.Encrypted && !string.IsNullOrWhiteSpace(user.Password))
                {
                    user.Password = _configPasswordProtector.DecryptSecret(user.Password, encryptionPassword);
                }
            }
        }
    }

    private void EncryptDatabasePayloads(SqlManagerConfig config, string encryptionPassword)
    {
        foreach (var server in config.Servers)
        {
            if (server.Databases.Count == 0)
            {
                server.DatabasesPayload = string.Empty;
                continue;
            }

            server.DatabasesPayload = _configPasswordProtector.EncryptSecret(SerializeDatabaseCollection(server.Databases), encryptionPassword);
            server.Databases = [];
        }
    }

    private void DecryptDatabasePayloads(SqlManagerConfig config, string encryptionPassword)
    {
        foreach (var server in config.Servers)
        {
            if (string.IsNullOrWhiteSpace(server.DatabasesPayload))
            {
                continue;
            }

            server.Databases = DeserializeDatabaseCollection(_configPasswordProtector.DecryptSecret(server.DatabasesPayload, encryptionPassword));
            server.DatabasesPayload = string.Empty;
        }
    }

    private static void ClearDatabasePayloads(SqlManagerConfig config)
    {
        foreach (var server in config.Servers)
        {
            server.DatabasesPayload = string.Empty;
        }
    }

    private void EncryptSecrets(SqlManagerConfig config, string encryptionPassword)
    {
        foreach (var server in config.Servers)
        {
            if (string.IsNullOrWhiteSpace(server.AdminPassword))
            {
                server.Encrypted = false;
            }
            else
            {
                server.AdminPassword = _configPasswordProtector.EncryptSecret(server.AdminPassword, encryptionPassword);
                server.Encrypted = true;
            }

            foreach (var user in server.Databases.SelectMany(database => database.Users))
            {
                if (string.IsNullOrWhiteSpace(user.Password))
                {
                    user.Encrypted = false;
                }
                else
                {
                    user.Password = _configPasswordProtector.EncryptSecret(user.Password, encryptionPassword);
                    user.Encrypted = true;
                }
            }
        }
    }

    private static void MarkPlaintextSecrets(SqlManagerConfig config)
    {
        foreach (var server in config.Servers)
        {
            server.Encrypted = false;
            foreach (var user in server.Databases.SelectMany(database => database.Users))
            {
                user.Encrypted = false;
            }
        }
    }

    private static void MarkProtectedAtRest(SqlManagerConfig config)
    {
        foreach (var server in config.Servers)
        {
            server.Encrypted = !string.IsNullOrWhiteSpace(server.AdminPassword);
            foreach (var user in server.Databases.SelectMany(database => database.Users))
            {
                user.Encrypted = !string.IsNullOrWhiteSpace(user.Password);
            }
        }
    }

    private void EncryptTrashPayloads(SqlManagerConfig config, string encryptionPassword)
    {
        foreach (var entry in config.Trash)
        {
            entry.PayloadJson = TransformTrashPayload(entry, encryptionPassword, encrypt: true);
        }
    }

    private void DecryptTrashPayloads(SqlManagerConfig config, string encryptionPassword)
    {
        foreach (var entry in config.Trash)
        {
            entry.PayloadJson = TransformTrashPayload(entry, encryptionPassword, encrypt: false);
        }
    }

    private static void MarkPlaintextTrashPayloads(SqlManagerConfig config)
    {
        foreach (var entry in config.Trash)
        {
            entry.PayloadJson ??= string.Empty;
        }
    }

    private static void RefreshConnectionStrings(SqlManagerConfig config)
    {
        foreach (var server in config.Servers)
        {
            RefreshConnectionStrings(server);
        }
    }

    private static void RefreshConnectionStrings(ServerConfig server)
    {
        foreach (var database in server.Databases)
        {
            foreach (var user in database.Users)
            {
                user.ConnectionString = BuildConnectionString(server, database.DatabaseName, user.Username, user.Password);
            }
        }
    }

    private string TransformTrashPayload(TrashEntry entry, string encryptionPassword, bool encrypt)
    {
        if (string.IsNullOrWhiteSpace(entry.PayloadJson))
        {
            return string.Empty;
        }

        return entry.EntryType switch
        {
            TrashEntryTypeServer => SerializeTrashPayload(TransformServerSnapshotSecrets(DeserializeTrashPayload<ServerConfig>(entry), encryptionPassword, encrypt)),
            TrashEntryTypeDatabase => SerializeTrashPayload(TransformDatabaseSnapshotSecrets(DeserializeTrashPayload<DatabaseConfig>(entry), encryptionPassword, encrypt)),
            TrashEntryTypeUser => SerializeTrashPayload(TransformUserSnapshotSecrets(DeserializeTrashPayload<UserConfig>(entry), encryptionPassword, encrypt)),
            _ => entry.PayloadJson
        };
    }

    private ServerConfig TransformServerSnapshotSecrets(ServerConfig server, string encryptionPassword, bool encrypt)
    {
        if (encrypt)
        {
            if (string.IsNullOrWhiteSpace(server.AdminPassword))
            {
                server.Encrypted = false;
            }
            else
            {
                server.AdminPassword = _configPasswordProtector.EncryptSecret(server.AdminPassword, encryptionPassword);
                server.Encrypted = true;
            }
        }
        else if (server.Encrypted && !string.IsNullOrWhiteSpace(server.AdminPassword))
        {
            server.AdminPassword = _configPasswordProtector.DecryptSecret(server.AdminPassword, encryptionPassword);
        }

        foreach (var database in server.Databases)
        {
            TransformDatabaseSnapshotSecrets(database, encryptionPassword, encrypt);
        }

        RefreshConnectionStrings(server);
        return server;
    }

    private DatabaseConfig TransformDatabaseSnapshotSecrets(DatabaseConfig database, string encryptionPassword, bool encrypt)
    {
        foreach (var user in database.Users)
        {
            TransformUserSnapshotSecrets(user, encryptionPassword, encrypt);
        }

        return database;
    }

    private UserConfig TransformUserSnapshotSecrets(UserConfig user, string encryptionPassword, bool encrypt)
    {
        if (encrypt)
        {
            if (string.IsNullOrWhiteSpace(user.Password))
            {
                user.Encrypted = false;
            }
            else
            {
                user.Password = _configPasswordProtector.EncryptSecret(user.Password, encryptionPassword);
                user.Encrypted = true;
            }
        }
        else if (user.Encrypted && !string.IsNullOrWhiteSpace(user.Password))
        {
            user.Password = _configPasswordProtector.DecryptSecret(user.Password, encryptionPassword);
        }

        return user;
    }

    private static string SerializeTrashPayload<T>(T value)
        => JsonSerializer.Serialize(value);

    private static string SerializeFullConfigPayload(SqlManagerConfig config)
        => JsonSerializer.Serialize(config);

    private static SqlManagerConfig DeserializeFullConfigPayload(string payload)
        => JsonSerializer.Deserialize<SqlManagerConfig>(payload)
            ?? throw new UserInputException("Stored config payload is not in the expected encrypted format.");

    private static string SerializeDatabaseCollection(IReadOnlyList<DatabaseConfig> databases)
        => JsonSerializer.Serialize(databases);

    private static List<DatabaseConfig> DeserializeDatabaseCollection(string payload)
        => JsonSerializer.Deserialize<List<DatabaseConfig>>(payload) ?? [];

    private static T DeserializeTrashPayload<T>(TrashEntry entry)
        => JsonSerializer.Deserialize<T>(entry.PayloadJson)
            ?? throw new UserInputException($"Trash entry '{entry.DisplayName}' is not in a valid format.");

    private static EntryVersion CloneVersion(EntryVersion version)
        => new()
        {
            VersionNumber = version.VersionNumber,
            ChangedAtUtc = version.ChangedAtUtc,
            Summary = version.Summary,
            Details = version.Details
        };

    private static bool HasFullConfigEnvelope(SqlManagerConfig config)
        => !string.IsNullOrWhiteSpace(config.EncryptedPayload);

    private static string SelectConfiguredServerName(SqlManagerConfig config)
    {
        if (config.Servers.Count == 0)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(config.SelectedServerName))
        {
            return config.SelectedServerName;
        }

        return config.Servers.Count == 1
            ? ServerConnections.GetIdentifier(config.Servers[0])
            : string.Empty;
    }

    private static ServerConfig? FindServer(SqlManagerConfig config, string selectionKey)
        => ServerConnections.FindBySelectionKey(config.Servers, selectionKey);

    private static void AddVersion(List<EntryVersion> versionHistory, string summary, string details)
    {
        versionHistory ??= [];
        versionHistory.Add(new EntryVersion
        {
            VersionNumber = versionHistory.Count == 0 ? 1 : versionHistory.Max(version => version.VersionNumber) + 1,
            ChangedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Summary = summary,
            Details = details
        });
    }

    private static string BuildServerVersionDetails(ServerConfig server)
        => string.Join(Environment.NewLine,
        [
            $"Connection Identifier: {ServerConnections.GetIdentifier(server)}",
            $"Display Name: {ServerConnections.GetDisplayName(server)}",
            $"Server: {server.ServerName}",
            $"Provider: {SqlProviders.GetDisplayName(server.Provider)}",
            $"Port: {(server.Port?.ToString() ?? "<default>")}",
            $"Admin Database: {server.AdminDatabase}",
            $"Admin User: {(string.IsNullOrWhiteSpace(server.AdminUsername) ? "<none>" : server.AdminUsername)}",
            $"PostgreSQL SSL Mode: {PostgreSqlSslModes.GetDisplayName(server.PostgreSqlSslMode)}",
            $"MySQL SSL Mode: {MySqlSslModes.GetDisplayName(server.MySqlSslMode)}",
            $"SQL Server TLS Mode: {SqlServerTrustModes.GetDisplayName(server.SqlServerTrustMode)}",
            $"Connection Timeout: {ServerConnectionOptions.GetEffectiveConnectionTimeoutSeconds(server.ConnectionTimeoutSeconds)}",
            $"Command Timeout: {ServerConnectionOptions.GetEffectiveCommandTimeoutSeconds(server.CommandTimeoutSeconds)}",
            $"PostgreSQL Pooling: {ServerConnectionOptions.GetEffectivePostgreSqlPooling(server.PostgreSqlPooling)}",
            $"MySQL Pooling: {ServerConnectionOptions.GetEffectiveMySqlPooling(server.MySqlPooling)}",
            $"MySQL Allow Public Key Retrieval: {ServerConnectionOptions.GetEffectiveMySqlAllowPublicKeyRetrieval(server.MySqlAllowPublicKeyRetrieval)}",
            $"Password State: {BuildPasswordState(server.AdminPassword, server.Encrypted)}",
            $"Tracked Databases: {server.Databases.Count}",
            $"Tracked Users: {server.Databases.Sum(database => database.Users.Count)}"
        ]);

    private static string BuildDatabaseVersionDetails(ServerConfig server, DatabaseConfig database)
        => string.Join(Environment.NewLine,
        [
            $"Server: {server.ServerName}",
            $"Database: {database.DatabaseName}",
            $"Tracked Users: {database.Users.Count}",
            $"Users: {(database.Users.Count == 0 ? "<none>" : string.Join(", ", database.Users.Select(user => user.Username).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)))}"
        ]);

    private static string BuildUserVersionDetails(ServerConfig server, DatabaseConfig database, UserConfig user)
        => string.Join(Environment.NewLine,
        [
            $"Server: {server.ServerName}",
            $"Database: {database.DatabaseName}",
            $"User: {user.Username}",
            $"Password State: {BuildPasswordState(user.Password, user.Encrypted)}",
            $"Roles: {(user.Roles.Count == 0 ? "<none>" : string.Join(", ", user.Roles))}",
            $"Connection String: {user.ConnectionString}"
        ]);

    private static string BuildPasswordState(string? password, bool encrypted)
        => encrypted
            ? "encrypted"
            : string.IsNullOrWhiteSpace(password)
                ? "missing"
                : "saved";

    private static ServerConfig GetOrCreateServer(
        SqlManagerConfig config,
        string? serverIdentifier,
        string serverName,
        string? displayName,
        string? provider,
        int? port,
        string? adminDatabase,
        string? adminUsername,
        string? adminPassword,
        string? postgreSqlSslMode = null,
        bool? postgreSqlPooling = null,
        string? mySqlSslMode = null,
        bool? mySqlPooling = null,
        bool? mySqlAllowPublicKeyRetrieval = null,
        string? sqlServerTrustMode = null,
        int? connectionTimeoutSeconds = null,
        int? commandTimeoutSeconds = null)
    {
        var normalizedProvider = SqlProviders.Normalize(provider);
        var normalizedAdminDatabase = string.IsNullOrWhiteSpace(adminDatabase)
            ? SqlProviders.GetDefaultAdminDatabase(normalizedProvider)
            : adminDatabase!;

        var server = string.IsNullOrWhiteSpace(serverIdentifier)
            ? null
            : FindServer(config, serverIdentifier);
        if (server is null)
        {
            var usedIdentifiers = new HashSet<int>(
                config.Servers
                    .Select(ServerConnections.GetIdentifier)
                    .Where(identifier => ServerConnections.TryParseIdentifier(identifier, out _))
                    .Select(identifier => int.Parse(identifier, System.Globalization.CultureInfo.InvariantCulture)));
            server = new ServerConfig
            {
                ServerIdentifier = ServerConnections.ReserveIdentifier(serverIdentifier, usedIdentifiers),
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? serverName : displayName.Trim(),
                ServerName = serverName,
                Provider = normalizedProvider,
                Port = port,
                AdminDatabase = normalizedAdminDatabase,
                AdminUsername = adminUsername ?? string.Empty,
                AdminPassword = adminPassword ?? string.Empty,
                PostgreSqlSslMode = normalizedProvider == SqlProviders.PostgreSql
                    ? string.IsNullOrWhiteSpace(postgreSqlSslMode)
                        ? PostgreSqlSslModes.GetDefaultForNewServers()
                        : PostgreSqlSslModes.Normalize(postgreSqlSslMode)
                    : string.Empty,
                PostgreSqlPooling = normalizedProvider == SqlProviders.PostgreSql
                    ? postgreSqlPooling ?? true
                    : null,
                MySqlSslMode = normalizedProvider == SqlProviders.MySql
                    ? string.IsNullOrWhiteSpace(mySqlSslMode)
                        ? MySqlSslModes.GetDefaultForNewServers()
                        : MySqlSslModes.Normalize(mySqlSslMode)
                    : string.Empty,
                MySqlPooling = normalizedProvider == SqlProviders.MySql
                    ? mySqlPooling ?? true
                    : null,
                MySqlAllowPublicKeyRetrieval = normalizedProvider == SqlProviders.MySql
                    ? mySqlAllowPublicKeyRetrieval ?? false
                    : null,
                SqlServerTrustMode = normalizedProvider == SqlProviders.SqlServer
                    ? string.IsNullOrWhiteSpace(sqlServerTrustMode)
                        ? SqlServerTrustModes.GetDefaultForNewServers()
                        : SqlServerTrustModes.Normalize(sqlServerTrustMode)
                    : string.Empty,
                ConnectionTimeoutSeconds = connectionTimeoutSeconds is > 0 ? connectionTimeoutSeconds : null,
                CommandTimeoutSeconds = commandTimeoutSeconds is > 0 ? commandTimeoutSeconds : null
            };
            config.Servers.Add(server);
        }
        else
        {
            server.ServerIdentifier = ServerConnections.GetIdentifier(server);
            server.DisplayName = string.IsNullOrWhiteSpace(displayName) ? ServerConnections.GetDisplayName(server) : displayName.Trim();
            server.Provider = normalizedProvider;
            server.ServerName = serverName;
            server.Port = port;
            server.AdminDatabase = normalizedAdminDatabase;
            if (!string.IsNullOrWhiteSpace(adminUsername))
            {
                server.AdminUsername = adminUsername!;
            }

            if (!string.IsNullOrWhiteSpace(postgreSqlSslMode))
            {
                server.PostgreSqlSslMode = PostgreSqlSslModes.Normalize(postgreSqlSslMode);
            }
            else if (normalizedProvider == SqlProviders.PostgreSql && string.IsNullOrWhiteSpace(server.PostgreSqlSslMode))
            {
                server.PostgreSqlSslMode = PostgreSqlSslModes.GetDefaultForNewServers();
            }

            if (postgreSqlPooling.HasValue)
            {
                server.PostgreSqlPooling = postgreSqlPooling.Value;
            }
            else if (normalizedProvider == SqlProviders.PostgreSql && !server.PostgreSqlPooling.HasValue)
            {
                server.PostgreSqlPooling = true;
            }

            if (!string.IsNullOrWhiteSpace(mySqlSslMode))
            {
                server.MySqlSslMode = MySqlSslModes.Normalize(mySqlSslMode);
            }
            else if (normalizedProvider == SqlProviders.MySql && string.IsNullOrWhiteSpace(server.MySqlSslMode))
            {
                server.MySqlSslMode = MySqlSslModes.GetDefaultForNewServers();
            }

            if (mySqlPooling.HasValue)
            {
                server.MySqlPooling = mySqlPooling.Value;
            }
            else if (normalizedProvider == SqlProviders.MySql && !server.MySqlPooling.HasValue)
            {
                server.MySqlPooling = true;
            }

            if (mySqlAllowPublicKeyRetrieval.HasValue)
            {
                server.MySqlAllowPublicKeyRetrieval = mySqlAllowPublicKeyRetrieval.Value;
            }
            else if (normalizedProvider == SqlProviders.MySql && !server.MySqlAllowPublicKeyRetrieval.HasValue)
            {
                server.MySqlAllowPublicKeyRetrieval = false;
            }

            if (!string.IsNullOrWhiteSpace(sqlServerTrustMode))
            {
                server.SqlServerTrustMode = SqlServerTrustModes.Normalize(sqlServerTrustMode);
            }
            else if (normalizedProvider == SqlProviders.SqlServer && string.IsNullOrWhiteSpace(server.SqlServerTrustMode))
            {
                server.SqlServerTrustMode = SqlServerTrustModes.GetDefaultForNewServers();
            }

            if (connectionTimeoutSeconds is > 0)
            {
                server.ConnectionTimeoutSeconds = connectionTimeoutSeconds.Value;
            }

            if (commandTimeoutSeconds is > 0)
            {
                server.CommandTimeoutSeconds = commandTimeoutSeconds.Value;
            }

            if (normalizedProvider == SqlProviders.PostgreSql)
            {
                server.MySqlSslMode = string.Empty;
                server.MySqlPooling = null;
                server.MySqlAllowPublicKeyRetrieval = null;
                server.SqlServerTrustMode = string.Empty;
            }
            else if (normalizedProvider == SqlProviders.MySql)
            {
                server.PostgreSqlSslMode = string.Empty;
                server.PostgreSqlPooling = null;
                server.SqlServerTrustMode = string.Empty;
            }
            else
            {
                server.PostgreSqlSslMode = string.Empty;
                server.PostgreSqlPooling = null;
                server.MySqlSslMode = string.Empty;
                server.MySqlPooling = null;
                server.MySqlAllowPublicKeyRetrieval = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(adminPassword))
        {
            server.AdminPassword = adminPassword!;
        }

        if (string.IsNullOrWhiteSpace(config.SelectedServerName))
        {
            config.SelectedServerName = ServerConnections.GetIdentifier(server);
        }

        return server;
    }

    private static ServerConfig GetOrCreateServer(ResolvedServerContext context)
        => GetOrCreateServer(
            context.Config,
            context.ServerIdentifier,
            context.ServerName,
            context.ServerDisplayName,
            context.Provider,
            context.Port,
            context.AdminDatabase,
            context.AdminUsername,
            context.AdminPassword,
            context.PostgreSqlSslMode,
            context.PostgreSqlPooling,
            context.MySqlSslMode,
            context.MySqlPooling,
            context.MySqlAllowPublicKeyRetrieval,
            context.SqlServerTrustMode,
            context.Timeouts.ConnectionTimeoutSeconds,
            context.Timeouts.CommandTimeoutSeconds);

    private static DatabaseConfig GetOrCreateDatabase(ServerConfig server, string databaseName)
    {
        var database = server.Databases.FirstOrDefault(item => item.DatabaseName.Equals(databaseName, StringComparison.OrdinalIgnoreCase));
        if (database is null)
        {
            database = new DatabaseConfig { DatabaseName = databaseName };
            server.Databases.Add(database);
        }

        return database;
    }

    private static UserConfig GetOrCreateUser(DatabaseConfig database, string userName)
    {
        var user = database.Users.FirstOrDefault(item => item.Username.Equals(userName, StringComparison.OrdinalIgnoreCase));
        if (user is null)
        {
            user = new UserConfig { Username = userName };
            database.Users.Add(user);
        }

        return user;
    }

    private static string GetStoredPasswordForUser(ServerConfig? serverConfig, string userName, string? databaseName = null)
    {
        if (serverConfig is null)
        {
            return string.Empty;
        }

        foreach (var database in serverConfig.Databases)
        {
            if (!string.IsNullOrWhiteSpace(databaseName)
                && !database.DatabaseName.Equals(databaseName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var password = database.Users.FirstOrDefault(user => user.Username.Equals(userName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(user.Password))?.Password;
            if (!string.IsNullOrWhiteSpace(password))
            {
                return password;
            }
        }

        return string.Empty;
    }

    private static void RemoveUserFromConfigDatabases(ServerConfig server, string userName, IReadOnlyCollection<string>? databaseNames)
    {
        foreach (var database in server.Databases)
        {
            if (databaseNames is not null
                && databaseNames.Count > 0
                && !databaseNames.Contains(database.DatabaseName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            database.Users = database.Users
                .Where(user => !user.Username.Equals(userName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private void SoftDeleteDatabase(SqlManagerConfig config, ServerConfig server, string databaseName, string? encryptionPassword)
    {
        var database = server.Databases.FirstOrDefault(candidate => candidate.DatabaseName.Equals(databaseName, StringComparison.OrdinalIgnoreCase));
        if (database is null)
        {
            return;
        }

        AddVersion(database.VersionHistory, "Moved to trash.", BuildDatabaseVersionDetails(server, database));
        config.Trash.Add(CreateTrashEntry(TrashEntryTypeDatabase, database.EntryId, database.DatabaseName, ServerConnections.GetIdentifier(server), string.Empty, BuildDatabaseVersionDetails(server, database), SerializeTrashPayload(database), database.VersionHistory));
        server.Databases = server.Databases.Where(candidate => !candidate.DatabaseName.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void SoftDeleteUsersFromConfigDatabases(SqlManagerConfig config, ServerConfig server, string userName, IReadOnlyCollection<string>? databaseNames, string? encryptionPassword)
    {
        foreach (var database in server.Databases)
        {
            if (databaseNames is not null
                && databaseNames.Count > 0
                && !databaseNames.Contains(database.DatabaseName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var removedUsers = database.Users.Where(user => user.Username.Equals(userName, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var user in removedUsers)
            {
                AddVersion(user.VersionHistory, "Moved to trash.", BuildUserVersionDetails(server, database, user));
        config.Trash.Add(CreateTrashEntry(TrashEntryTypeUser, user.EntryId, user.Username, ServerConnections.GetIdentifier(server), database.DatabaseName, BuildUserVersionDetails(server, database, user), SerializeTrashPayload(user), user.VersionHistory));
            }

            database.Users = database.Users.Where(user => !user.Username.Equals(userName, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    private static TrashEntry CreateTrashEntry(string entryType, string entryId, string displayName, string parentServerName, string parentDatabaseName, string details, string payloadJson, IReadOnlyList<EntryVersion> versionHistory)
        => new()
        {
            EntryType = entryType,
            EntryId = entryId,
            DisplayName = displayName,
            ParentServerName = parentServerName,
            ParentDatabaseName = parentDatabaseName,
            DeletedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Details = details,
            PayloadJson = payloadJson,
            VersionHistory = versionHistory.Select(CloneVersion).ToList()
        };

    private static void UpdateUserPasswordInConfig(ServerConfig server, string userName, string password)
    {
        foreach (var database in server.Databases)
        {
            foreach (var user in database.Users.Where(user => user.Username.Equals(userName, StringComparison.OrdinalIgnoreCase)))
            {
                user.Password = password;
                user.ConnectionString = BuildConnectionString(server, database.DatabaseName, userName, password);
            }
        }
    }

    private ResolvedPassword ResolveRequestedPassword(string? providedPassword, bool loginExists, ServerConfig? serverConfig, string userName)
    {
        if (!string.IsNullOrWhiteSpace(providedPassword))
        {
            return new ResolvedPassword(providedPassword!, "Password was set from the supplied NewUserPassword value.");
        }

        if (loginExists)
        {
            var storedPassword = GetStoredPasswordForUser(serverConfig, userName);
            if (!string.IsNullOrWhiteSpace(storedPassword))
            {
                return new ResolvedPassword(storedPassword, "Password was reused from the config.");
            }

            throw new UserInputException("The login already exists, but no password is available in config. Supply NewUserPassword so the utility can update the login password and generate a valid connection string.");
        }

        return new ResolvedPassword(_passwordGenerator.Generate(), "Password was generated automatically.");
    }

    private static string ResolveUserLoginPassword(string? providedPassword, ServerConfig? serverConfig, string databaseName, string userName)
    {
        if (!string.IsNullOrWhiteSpace(providedPassword))
        {
            return providedPassword!;
        }

        var storedPassword = GetStoredPasswordForUser(serverConfig, userName, databaseName);
        if (!string.IsNullOrWhiteSpace(storedPassword))
        {
            return storedPassword;
        }

        storedPassword = GetStoredPasswordForUser(serverConfig, userName);
        if (!string.IsNullOrWhiteSpace(storedPassword))
        {
            return storedPassword;
        }

        throw new UserInputException("No password is stored for this user. Supply --user-password to test the login.");
    }

    private async Task ExecuteNonQueryAsync(ResolvedServerContext context, string adminPassword, string query, string database, CancellationToken cancellationToken)
    {
        if (IsSqlServer(context.Provider))
        {
            await _sqlServerGateway.ExecuteNonQueryAsync(context.ServerName, context.Port, context.AdminUsername, adminPassword, query, database, context.Timeouts, context.SqlServerTrustMode, cancellationToken);
            return;
        }

        if (IsPostgreSql(context.Provider))
        {
            await _postgreSqlGateway.ExecuteNonQueryAsync(context.ServerName, context.Port, context.AdminUsername, adminPassword, query, database, context.Timeouts, context.PostgreSqlSslMode, context.PostgreSqlPooling, cancellationToken);
            return;
        }

        await _mySqlGateway.ExecuteNonQueryAsync(context.ServerName, context.Port, context.AdminUsername, adminPassword, query, database, context.Timeouts, context.MySqlSslMode, context.MySqlPooling, context.MySqlAllowPublicKeyRetrieval, cancellationToken);
    }

    private async Task<int> ExecuteScalarIntAsync(ResolvedServerContext context, string adminPassword, string query, string database, CancellationToken cancellationToken)
    {
        return await ExecuteScalarIntAsync(context, context.AdminUsername, adminPassword, query, database, cancellationToken);
    }

    private async Task<int> ExecuteScalarIntAsync(ResolvedServerContext context, string username, string password, string query, string database, CancellationToken cancellationToken)
    {
        if (IsSqlServer(context.Provider))
        {
            return await _sqlServerGateway.ExecuteScalarIntAsync(context.ServerName, context.Port, username, password, query, database, context.Timeouts, context.SqlServerTrustMode, cancellationToken);
        }

        if (IsPostgreSql(context.Provider))
        {
            return await _postgreSqlGateway.ExecuteScalarIntAsync(context.ServerName, context.Port, username, password, query, database, context.Timeouts, context.PostgreSqlSslMode, context.PostgreSqlPooling, cancellationToken);
        }

        return await _mySqlGateway.ExecuteScalarIntAsync(context.ServerName, context.Port, username, password, query, database, context.Timeouts, context.MySqlSslMode, context.MySqlPooling, context.MySqlAllowPublicKeyRetrieval, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> LoadServerDatabaseNamesAsync(ResolvedServerContext context, string adminPassword, CancellationToken cancellationToken)
    {
        if (IsSqlServer(context.Provider))
        {
            return await _sqlServerGateway.QueryNamesAsync(
                context.ServerName,
                context.Port,
                context.AdminUsername,
                adminPassword,
                "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 ORDER BY name;",
                context.AdminDatabase,
                context.Timeouts,
                context.SqlServerTrustMode,
                cancellationToken);
        }

        if (IsPostgreSql(context.Provider))
        {
            return await _postgreSqlGateway.QueryNamesAsync(
                context.ServerName,
                context.Port,
                context.AdminUsername,
                adminPassword,
                "SELECT datname FROM pg_database WHERE datistemplate = false AND datallowconn = true AND datname NOT IN ('postgres') ORDER BY datname;",
                context.AdminDatabase,
                context.Timeouts,
                context.PostgreSqlSslMode,
                context.PostgreSqlPooling,
                cancellationToken);
        }

        return await _mySqlGateway.QueryNamesAsync(
            context.ServerName,
            context.Port,
            context.AdminUsername,
            adminPassword,
            "SELECT schema_name FROM information_schema.schemata WHERE schema_name NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys') ORDER BY schema_name;",
            context.AdminDatabase,
            context.Timeouts,
            context.MySqlSslMode,
            context.MySqlPooling,
            context.MySqlAllowPublicKeyRetrieval,
            cancellationToken);
    }

    private async Task<IReadOnlyList<DatabaseUserRow>> QueryDatabaseUsersAsync(ResolvedServerContext context, string adminPassword, string databaseName, CancellationToken cancellationToken)
    {
        if (IsSqlServer(context.Provider))
        {
            return await _sqlServerGateway.QueryDatabaseUsersAsync(
                context.ServerName,
                context.Port,
                context.AdminUsername,
                adminPassword,
                databaseName,
                context.Timeouts,
                context.SqlServerTrustMode,
                cancellationToken);
        }

        if (IsPostgreSql(context.Provider))
        {
            return await _postgreSqlGateway.QueryDatabaseUsersAsync(
                context.ServerName,
                context.Port,
                context.AdminUsername,
                adminPassword,
                databaseName,
                BuildPostgreSqlShowUsersQuery(databaseName),
                context.Timeouts,
                context.PostgreSqlSslMode,
                context.PostgreSqlPooling,
                cancellationToken);
        }

        return await _mySqlGateway.QueryDatabaseUsersAsync(
            context.ServerName,
            context.Port,
            context.AdminUsername,
            adminPassword,
            context.AdminDatabase,
            BuildMySqlShowUsersQuery(databaseName),
            context.Timeouts,
            context.MySqlSslMode,
            context.MySqlPooling,
            context.MySqlAllowPublicKeyRetrieval,
            cancellationToken);
    }

    private async Task EnsureServerLoginAsync(ResolvedServerContext context, string adminPassword, string defaultDatabase, string userName, string password, bool loginExists, bool updatePassword, CancellationToken cancellationToken)
    {
        if (IsSqlServer(context.Provider))
        {
            var quotedUser = QuoteSqlServerIdentifier(userName);
            var quotedDatabase = QuoteSqlServerIdentifier(defaultDatabase);
            if (!loginExists)
            {
                var createLogin = $"CREATE LOGIN {quotedUser} WITH PASSWORD = {QuoteSqlLiteral(password)}, CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = {quotedDatabase}";
                await ExecuteNonQueryAsync(context, adminPassword, $"EXEC({QuoteSqlLiteral(createLogin)}); ALTER LOGIN {quotedUser} ENABLE;", context.AdminDatabase, cancellationToken);
            }
            else if (updatePassword)
            {
                var alterLogin = $"ALTER LOGIN {quotedUser} WITH PASSWORD = {QuoteSqlLiteral(password)}";
                await ExecuteNonQueryAsync(context, adminPassword, $"EXEC({QuoteSqlLiteral(alterLogin)}); ALTER LOGIN {quotedUser} ENABLE;", context.AdminDatabase, cancellationToken);
            }

            return;
        }

        if (IsPostgreSql(context.Provider))
        {
            if (!loginExists)
            {
                var query = $"CREATE ROLE {QuotePostgreSqlIdentifier(userName)} LOGIN PASSWORD {QuotePostgreSqlLiteral(password)};";
                await ExecuteNonQueryAsync(context, adminPassword, query, context.AdminDatabase, cancellationToken);
            }
            else if (updatePassword)
            {
                var query = $"ALTER ROLE {QuotePostgreSqlIdentifier(userName)} LOGIN PASSWORD {QuotePostgreSqlLiteral(password)};";
                await ExecuteNonQueryAsync(context, adminPassword, query, context.AdminDatabase, cancellationToken);
            }

            return;
        }

        var mySqlAccount = QuoteMySqlAccount(userName);
        if (!loginExists)
        {
            var query = $"CREATE USER {mySqlAccount} IDENTIFIED BY {QuoteMySqlLiteral(password)};";
            await ExecuteNonQueryAsync(context, adminPassword, query, context.AdminDatabase, cancellationToken);
        }
        else if (updatePassword)
        {
            var query = $"ALTER USER {mySqlAccount} IDENTIFIED BY {QuoteMySqlLiteral(password)};";
            await ExecuteNonQueryAsync(context, adminPassword, query, context.AdminDatabase, cancellationToken);
        }
    }

    private static List<string> GetRequestedDatabaseNames(CommandOptions options, bool requireOne, string errorMessage)
    {
        var databaseNames = options.DatabaseNames.Count > 0
            ? options.DatabaseNames
            : string.IsNullOrWhiteSpace(options.DatabaseName)
                ? Array.Empty<string>()
                : [options.DatabaseName!];

        var normalized = databaseNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requireOne && normalized.Count == 0)
        {
            throw new UserInputException(errorMessage);
        }

        return normalized;
    }

    private static List<DatabaseRoleAssignment> NormalizeDatabaseRoleAssignments(IReadOnlyList<DatabaseRoleAssignment> assignments, string provider)
    {
        var normalized = new List<DatabaseRoleAssignment>();
        foreach (var assignment in assignments)
        {
            var databaseName = Require(assignment.DatabaseName, "Database role assignments require DatabaseName.");
            var roles = assignment.Roles.Count == 0
                ? []
                : NormalizeRoles(assignment.Roles, provider);
            normalized.Add(new DatabaseRoleAssignment(databaseName, roles));
        }

        return normalized;
    }

    private static string BuildRoleMembershipSyncQuery(ResolvedServerContext context, string databaseName, string userName, IReadOnlyCollection<string> desiredRoles, bool failIfUserMissing)
        => IsSqlServer(context.Provider)
            ? BuildSqlServerRoleMembershipSyncQuery(userName, desiredRoles, failIfUserMissing)
            : IsPostgreSql(context.Provider)
                ? BuildPostgreSqlRoleMembershipSyncQuery(databaseName, userName, desiredRoles, failIfUserMissing)
                : BuildMySqlRoleMembershipSyncQuery(databaseName, userName, desiredRoles);

    private static string BuildSqlServerRoleMembershipSyncQuery(string userName, IReadOnlyCollection<string> desiredRoles, bool failIfUserMissing)
    {
        var quotedUser = QuoteSqlServerIdentifier(userName);
        var queryBuilder = new StringBuilder();
        if (failIfUserMissing)
        {
            queryBuilder.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = {QuoteSqlLiteral(userName)})");
            queryBuilder.AppendLine("    THROW 50000, 'Database user does not exist.', 1;");
        }
        else
        {
            queryBuilder.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = {QuoteSqlLiteral(userName)})");
            queryBuilder.AppendLine("BEGIN");
            queryBuilder.AppendLine($"    CREATE USER {quotedUser} FOR LOGIN {quotedUser};");
            queryBuilder.AppendLine("END");
        }

        foreach (var role in SupportedSqlServerRoles)
        {
            var quotedRole = QuoteSqlServerIdentifier(role);
            var wantsRole = desiredRoles.Contains(role, StringComparer.OrdinalIgnoreCase);
            queryBuilder.AppendLine(wantsRole ? "IF NOT EXISTS (" : "IF EXISTS (");
            queryBuilder.AppendLine("    SELECT 1");
            queryBuilder.AppendLine("    FROM sys.database_role_members drm");
            queryBuilder.AppendLine("    INNER JOIN sys.database_principals rp ON drm.role_principal_id = rp.principal_id");
            queryBuilder.AppendLine("    INNER JOIN sys.database_principals mp ON drm.member_principal_id = mp.principal_id");
            queryBuilder.AppendLine($"    WHERE rp.name = {QuoteSqlLiteral(role)} AND mp.name = {QuoteSqlLiteral(userName)})");
            queryBuilder.AppendLine(wantsRole
                ? $"    ALTER ROLE {quotedRole} ADD MEMBER {quotedUser};"
                : $"    ALTER ROLE {quotedRole} DROP MEMBER {quotedUser};");
        }

        return queryBuilder.ToString();
    }

    private static string BuildPostgreSqlRoleMembershipSyncQuery(string databaseName, string userName, IReadOnlyCollection<string> desiredRoles, bool failIfUserMissing)
    {
        var roleMap = GetPostgreSqlManagedRoleMap(databaseName);
        var quotedUser = QuotePostgreSqlIdentifier(userName);
        var queryBuilder = new StringBuilder();
        queryBuilder.AppendLine(BuildEnsurePostgreSqlManagedRolesQuery(databaseName));
        if (failIfUserMissing)
        {
            queryBuilder.AppendLine($"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = {QuotePostgreSqlLiteral(userName)}) THEN RAISE EXCEPTION 'Database user does not exist.'; END IF; END $$;");
        }

        foreach (var role in SupportedPostgreSqlRoles)
        {
            var managedRole = roleMap[role];
            queryBuilder.AppendLine(desiredRoles.Contains(role, StringComparer.OrdinalIgnoreCase)
                ? $"GRANT {QuotePostgreSqlIdentifier(managedRole)} TO {quotedUser};"
                : $"REVOKE {QuotePostgreSqlIdentifier(managedRole)} FROM {quotedUser};");
        }

        return queryBuilder.ToString();
    }

    private static string BuildMySqlRoleMembershipSyncQuery(string databaseName, string userName, IReadOnlyCollection<string> desiredRoles)
        => desiredRoles.Intersect(SupportedMySqlRoles, StringComparer.OrdinalIgnoreCase).Any()
            ? $"GRANT ALL PRIVILEGES ON {BuildMySqlDatabaseScope(databaseName)} TO {QuoteMySqlAccount(userName)};"
            : $"REVOKE ALL PRIVILEGES ON {BuildMySqlDatabaseScope(databaseName)} FROM {QuoteMySqlAccount(userName)};";

    private static string BuildAddRolesQuery(ResolvedServerContext context, string databaseName, string userName, IReadOnlyCollection<string> roles)
    {
        if (IsSqlServer(context.Provider))
        {
            var queryBuilder = new StringBuilder();
            queryBuilder.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = {QuoteSqlLiteral(userName)})");
            queryBuilder.AppendLine("    THROW 50000, 'Database user does not exist.', 1;");

            foreach (var role in roles)
            {
                queryBuilder.AppendLine("IF NOT EXISTS (");
                queryBuilder.AppendLine("    SELECT 1");
                queryBuilder.AppendLine("    FROM sys.database_role_members drm");
                queryBuilder.AppendLine("    INNER JOIN sys.database_principals rp ON drm.role_principal_id = rp.principal_id");
                queryBuilder.AppendLine("    INNER JOIN sys.database_principals mp ON drm.member_principal_id = mp.principal_id");
                queryBuilder.AppendLine($"    WHERE rp.name = {QuoteSqlLiteral(role)} AND mp.name = {QuoteSqlLiteral(userName)})");
                queryBuilder.AppendLine($"    ALTER ROLE {QuoteSqlServerIdentifier(role)} ADD MEMBER {QuoteSqlServerIdentifier(userName)};");
            }

            return queryBuilder.ToString();
        }

        if (IsPostgreSql(context.Provider))
        {
            var roleMap = GetPostgreSqlManagedRoleMap(databaseName);
            var query = new StringBuilder();
            query.AppendLine(BuildEnsurePostgreSqlManagedRolesQuery(databaseName));
            query.AppendLine($"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = {QuotePostgreSqlLiteral(userName)}) THEN RAISE EXCEPTION 'Database user does not exist.'; END IF; END $$;");
            foreach (var role in roles)
            {
                query.AppendLine($"GRANT {QuotePostgreSqlIdentifier(roleMap[role])} TO {QuotePostgreSqlIdentifier(userName)};");
            }

            return query.ToString();
        }

        return $"GRANT ALL PRIVILEGES ON {BuildMySqlDatabaseScope(databaseName)} TO {QuoteMySqlAccount(userName)};";
    }

    private static string BuildRemoveRolesQuery(ResolvedServerContext context, string databaseName, string userName, IReadOnlyCollection<string> roles)
    {
        if (IsSqlServer(context.Provider))
        {
            var queryBuilder = new StringBuilder();
            queryBuilder.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = {QuoteSqlLiteral(userName)})");
            queryBuilder.AppendLine("    THROW 50000, 'Database user does not exist.', 1;");

            foreach (var role in roles)
            {
                queryBuilder.AppendLine("IF EXISTS (");
                queryBuilder.AppendLine("    SELECT 1");
                queryBuilder.AppendLine("    FROM sys.database_role_members drm");
                queryBuilder.AppendLine("    INNER JOIN sys.database_principals rp ON drm.role_principal_id = rp.principal_id");
                queryBuilder.AppendLine("    INNER JOIN sys.database_principals mp ON drm.member_principal_id = mp.principal_id");
                queryBuilder.AppendLine($"    WHERE rp.name = {QuoteSqlLiteral(role)} AND mp.name = {QuoteSqlLiteral(userName)})");
                queryBuilder.AppendLine($"    ALTER ROLE {QuoteSqlServerIdentifier(role)} DROP MEMBER {QuoteSqlServerIdentifier(userName)};");
            }

            return queryBuilder.ToString();
        }

        if (IsPostgreSql(context.Provider))
        {
            var roleMap = GetPostgreSqlManagedRoleMap(databaseName);
            var query = new StringBuilder();
            query.AppendLine($"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = {QuotePostgreSqlLiteral(userName)}) THEN RAISE EXCEPTION 'Database user does not exist.'; END IF; END $$;");
            foreach (var role in roles)
            {
                query.AppendLine($"REVOKE {QuotePostgreSqlIdentifier(roleMap[role])} FROM {QuotePostgreSqlIdentifier(userName)};");
            }

            return query.ToString();
        }

        return $"REVOKE ALL PRIVILEGES ON {BuildMySqlDatabaseScope(databaseName)} FROM {QuoteMySqlAccount(userName)};";
    }

    private static string BuildRemoveDatabaseUserQuery(ResolvedServerContext context, string databaseName, string userName)
    {
        if (IsSqlServer(context.Provider))
        {
            return $"IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = {QuoteSqlLiteral(userName)}) DROP USER {QuoteSqlServerIdentifier(userName)};";
        }

        if (IsPostgreSql(context.Provider))
        {
            var roleMap = GetPostgreSqlManagedRoleMap(databaseName);
            var query = new StringBuilder();
            query.AppendLine($"DO $$ BEGIN IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = {QuotePostgreSqlLiteral(userName)}) THEN");
            foreach (var managedRole in roleMap.Values)
            {
                query.AppendLine($"    REVOKE {QuotePostgreSqlIdentifier(managedRole)} FROM {QuotePostgreSqlIdentifier(userName)};");
            }
            query.AppendLine("END IF; END $$;");
            return query.ToString();
        }

        return $"REVOKE ALL PRIVILEGES ON {BuildMySqlDatabaseScope(databaseName)} FROM {QuoteMySqlAccount(userName)};";
    }

    private static string BuildDropLoginQuery(ResolvedServerContext context, string userName)
        => IsSqlServer(context.Provider)
            ? $"IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = {QuoteSqlLiteral(userName)}) DROP LOGIN {QuoteSqlServerIdentifier(userName)};"
            : IsPostgreSql(context.Provider)
                ? $"DROP ROLE IF EXISTS {QuotePostgreSqlIdentifier(userName)};"
                : $"DROP USER IF EXISTS {QuoteMySqlAccount(userName)};";

    private static string BuildPostgreSqlShowUsersQuery(string databaseName)
    {
        var roleMap = GetPostgreSqlManagedRoleMap(databaseName);
        var ownerRole = roleMap["db_owner"];

        return $"""
SELECT
    member.rolname AS username,
    member.rolname AS loginname,
    COALESCE(string_agg(mapped.role_name, ', ' ORDER BY mapped.sort_order), '') AS roles
FROM pg_roles member
LEFT JOIN (
    SELECT
        auth.member,
        CASE role.rolname
            WHEN {QuotePostgreSqlLiteral(ownerRole)} THEN 'db_owner'
        END AS role_name,
        CASE role.rolname
            WHEN {QuotePostgreSqlLiteral(ownerRole)} THEN 1
        END AS sort_order
    FROM pg_auth_members auth
    INNER JOIN pg_roles role ON role.oid = auth.roleid
    WHERE role.rolname IN ({QuotePostgreSqlLiteral(ownerRole)})
) mapped ON mapped.member = member.oid
WHERE member.rolcanlogin
GROUP BY member.rolname
HAVING COUNT(mapped.role_name) > 0
ORDER BY member.rolname;
""";
    }

    private static Dictionary<string, string> GetPostgreSqlManagedRoleMap(string databaseName)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["db_owner"] = BuildPostgreSqlManagedRoleName(databaseName, "owner")
        };

    private static string BuildMySqlShowUsersQuery(string databaseName)
        => $"""
SELECT
    derived.username,
    derived.loginname,
    derived.roles
FROM (
    SELECT
        `User` AS username,
        CONCAT(`User`, '@', `Host`) AS loginname,
        CASE
            WHEN Select_priv = 'Y'
             AND Insert_priv = 'Y'
             AND Update_priv = 'Y'
             AND Delete_priv = 'Y'
             AND Create_priv = 'Y'
             AND Drop_priv = 'Y'
             AND References_priv = 'Y'
             AND Index_priv = 'Y'
             AND Alter_priv = 'Y'
             AND Create_tmp_table_priv = 'Y'
             AND Lock_tables_priv = 'Y'
             AND Create_view_priv = 'Y'
             AND Show_view_priv = 'Y'
             AND Create_routine_priv = 'Y'
             AND Alter_routine_priv = 'Y'
             AND Execute_priv = 'Y'
             AND Event_priv = 'Y'
             AND Trigger_priv = 'Y'
            THEN 'db_owner'
            ELSE ''
        END AS roles
    FROM mysql.db
    WHERE Db = {QuoteMySqlLiteral(databaseName)}
      AND `User` <> ''
) AS derived
WHERE derived.roles <> ''
ORDER BY derived.username, derived.loginname;
""";

    private static string BuildEnsurePostgreSqlManagedRolesQuery(string databaseName)
    {
        var roleMap = GetPostgreSqlManagedRoleMap(databaseName);
        var quotedDatabase = QuotePostgreSqlIdentifier(databaseName);
        var query = new StringBuilder();

        foreach (var managedRole in roleMap.Values)
        {
            query.AppendLine($"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = {QuotePostgreSqlLiteral(managedRole)}) THEN CREATE ROLE {QuotePostgreSqlIdentifier(managedRole)} NOLOGIN; END IF; END $$;");
        }

        var ownerRole = QuotePostgreSqlIdentifier(roleMap["db_owner"]);

        query.AppendLine($"GRANT CONNECT, TEMP, CREATE ON DATABASE {quotedDatabase} TO {ownerRole};");
        query.AppendLine($"GRANT ALL PRIVILEGES ON DATABASE {quotedDatabase} TO {ownerRole};");
        query.AppendLine($"GRANT USAGE, CREATE ON SCHEMA public TO {ownerRole};");
        query.AppendLine($"GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO {ownerRole};");
        query.AppendLine($"GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO {ownerRole};");
        query.AppendLine($"GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA public TO {ownerRole};");
        query.AppendLine($"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL PRIVILEGES ON TABLES TO {ownerRole};");
        query.AppendLine($"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL PRIVILEGES ON SEQUENCES TO {ownerRole};");
        query.AppendLine($"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL PRIVILEGES ON FUNCTIONS TO {ownerRole};");

        return query.ToString();
    }

    private static string BuildPostgreSqlManagedRoleName(string databaseName, string suffix)
    {
        var normalized = new string(databaseName
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray())
            .Trim('_');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "database";
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(databaseName))).ToLowerInvariant()[..8];
        var prefix = $"sqlmgr_{suffix}_";
        var maxBodyLength = 63 - prefix.Length - hash.Length - 1;
        if (normalized.Length > maxBodyLength)
        {
            normalized = normalized[..maxBodyLength];
        }

        return $"{prefix}{normalized}_{hash}";
    }

    private static List<string> NormalizeRoles(IReadOnlyList<string> inputRoles, string provider)
    {
        var normalized = new List<string>();
        var isSqlServer = IsSqlServer(provider);
        foreach (var role in inputRoles)
        {
            normalized.Add(role.Trim().ToLowerInvariant() switch
            {
                "dbowner" or "db_owner" => "db_owner",
                "dbreader" or "db_reader" or "db_datareader" when isSqlServer => "db_datareader",
                "dbwriter" or "db_writer" or "db_datawriter" when isSqlServer => "db_datawriter",
                _ when isSqlServer => throw new UserInputException($"Unsupported role '{role}' for {SqlProviders.GetDisplayName(provider)}. Use dbowner/db_owner, dbreader/db_reader/db_datareader, or dbwriter/db_writer/db_datawriter."),
                _ => throw new UserInputException($"Unsupported role '{role}' for {SqlProviders.GetDisplayName(provider)}. PostgreSQL and MySQL/MariaDB support dbowner/db_owner only.")
            });
        }

        return normalized.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> SplitRoleList(string roles)
        => string.IsNullOrWhiteSpace(roles)
            ? []
            : roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static string BuildServerConnectionTestQuery(string provider)
        => IsSqlServer(provider)
            ? "SELECT CAST(1 AS int);"
            : "SELECT 1;";

    private static string GetAdministrativeExecutionDatabase(ResolvedServerContext context, string databaseName)
        => SqlProviders.Normalize(context.Provider) == SqlProviders.MySql
            ? context.AdminDatabase
            : databaseName;

    private static string BuildConnectionString(ResolvedServerContext context, string database, string username, string? password)
    {
        if (IsSqlServer(context.Provider))
        {
            return ServerConnectionOptions.BuildSqlServerUserConnectionString(
                context.ServerName,
                context.Port,
                database,
                username,
                password,
                context.SqlServerTrustMode);
        }

        if (IsPostgreSql(context.Provider))
        {
            return ServerConnectionOptions.BuildPostgreSqlUserConnectionString(
                context.ServerName,
                context.Port,
                database,
                username,
                password,
                context.PostgreSqlSslMode,
                context.PostgreSqlPooling,
                context.Timeouts.ConnectionTimeoutSeconds,
                context.Timeouts.CommandTimeoutSeconds);
        }

        return ServerConnectionOptions.BuildMySqlUserConnectionString(
            context.ServerName,
            context.Port,
            database,
            username,
            password,
            context.MySqlSslMode,
            context.MySqlPooling,
            context.MySqlAllowPublicKeyRetrieval,
            context.Timeouts.ConnectionTimeoutSeconds,
            context.Timeouts.CommandTimeoutSeconds);
    }

    private static string BuildConnectionString(ServerConfig server, string database, string username, string? password)
    {
        if (IsSqlServer(server.Provider))
        {
            return ServerConnectionOptions.BuildSqlServerUserConnectionString(
                server.ServerName,
                server.Port,
                database,
                username,
                password,
                server.SqlServerTrustMode);
        }

        if (IsPostgreSql(server.Provider))
        {
            return ServerConnectionOptions.BuildPostgreSqlUserConnectionString(
                server.ServerName,
                server.Port,
                database,
                username,
                password,
                server.PostgreSqlSslMode,
                server.PostgreSqlPooling,
                server.ConnectionTimeoutSeconds,
                server.CommandTimeoutSeconds);
        }

        return ServerConnectionOptions.BuildMySqlUserConnectionString(
            server.ServerName,
            server.Port,
            database,
            username,
            password,
            server.MySqlSslMode,
            server.MySqlPooling,
            server.MySqlAllowPublicKeyRetrieval,
            server.ConnectionTimeoutSeconds,
            server.CommandTimeoutSeconds);
    }

    private static string BuildConnectionString(string provider, string server, int? port, string database, string username, string? password)
    {
        if (IsSqlServer(provider))
        {
            return ServerConnectionOptions.BuildSqlServerUserConnectionString(
                server,
                port,
                database,
                username,
                password,
                string.Empty);
        }

        if (IsPostgreSql(provider))
        {
            return ServerConnectionOptions.BuildPostgreSqlUserConnectionString(
                server,
                port,
                database,
                username,
                password,
                string.Empty,
                null,
                null,
                null);
        }

        return ServerConnectionOptions.BuildMySqlUserConnectionString(
            server,
            port,
            database,
            username,
            password,
            string.Empty,
            null,
            null,
            null,
            null);
    }

    private static string BuildSqlServerConnectionTarget(string server, int? port)
        => ServerConnectionOptions.BuildSqlServerDataSource(server, port);

    private static bool IsSqlServer(string provider)
        => SqlProviders.Normalize(provider) == SqlProviders.SqlServer;

    private static bool IsPostgreSql(string provider)
        => SqlProviders.Normalize(provider) == SqlProviders.PostgreSql;

    private static string QuoteSqlServerIdentifier(string name)
        => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string QuotePostgreSqlIdentifier(string name)
        => $"\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string QuoteMySqlIdentifier(string name)
        => $"`{name.Replace("`", "``", StringComparison.Ordinal)}`";

    private static string QuoteSqlLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string QuotePostgreSqlLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string QuoteMySqlLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string QuoteMySqlAccount(string userName)
        => $"{QuoteMySqlLiteral(userName)}@'%'";

    private static string BuildMySqlDatabaseScope(string databaseName)
        => $"{QuoteMySqlIdentifier(databaseName)}.*";

    private static string Require(string? value, string errorMessage)
        => string.IsNullOrWhiteSpace(value)
            ? throw new UserInputException(errorMessage)
            : value;

    private static void LogHandledException(string context, Exception exception)
        => ErrorLogger.LogException(context, exception);

    private static async Task<OperationResult> ExecuteAsync(Func<Task<OperationResult>> action)
    {
        try
        {
            return await action();
        }
        catch (UserInputException exception)
        {
            return OperationResult.Failure(exception.Message, 2);
        }
        catch (SqlException exception)
        {
            LogHandledException("SQL Server operation failed", exception);

            if (exception.Number == -2)
            {
                return OperationResult.Failure("SQL Server operation timed out.");
            }

            return OperationResult.Failure($"SQL Server error: {exception.Message}");
        }
        catch (NpgsqlException exception)
        {
            LogHandledException("PostgreSQL operation failed", exception);
            return OperationResult.Failure($"PostgreSQL error: {exception.Message}");
        }
        catch (MySqlException exception)
        {
            LogHandledException("MySQL operation failed", exception);
            return OperationResult.Failure($"MySQL error: {exception.Message}");
        }
        catch (IOException exception)
        {
            LogHandledException("File operation failed", exception);
            return OperationResult.Failure($"File error: {exception.Message}");
        }
        catch (JsonException exception)
        {
            LogHandledException("Config parsing failed", exception);
            return OperationResult.Failure($"Config file is invalid JSON: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            LogHandledException("Access denied during operation", exception);
            return OperationResult.Failure($"Access denied: {exception.Message}");
        }
        catch (TimeoutException exception)
        {
            LogHandledException("Operation timed out", exception);
            return OperationResult.Failure(exception.Message);
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Failure("Operation cancelled.", 130);
        }
        catch (Exception exception)
        {
            LogHandledException("Unexpected service error", exception);
            return OperationResult.Failure($"Unexpected error: {exception.Message}");
        }
    }

    private static async Task<OperationResult<T>> ExecuteAsync<T>(Func<Task<OperationResult<T>>> action)
    {
        try
        {
            return await action();
        }
        catch (UserInputException exception)
        {
            return OperationResult<T>.Failure(exception.Message, 2);
        }
        catch (SqlException exception)
        {
            LogHandledException("SQL Server operation failed", exception);

            if (exception.Number == -2)
            {
                return OperationResult<T>.Failure("SQL Server operation timed out.");
            }

            return OperationResult<T>.Failure($"SQL Server error: {exception.Message}");
        }
        catch (NpgsqlException exception)
        {
            LogHandledException("PostgreSQL operation failed", exception);
            return OperationResult<T>.Failure($"PostgreSQL error: {exception.Message}");
        }
        catch (MySqlException exception)
        {
            LogHandledException("MySQL operation failed", exception);
            return OperationResult<T>.Failure($"MySQL error: {exception.Message}");
        }
        catch (IOException exception)
        {
            LogHandledException("File operation failed", exception);
            return OperationResult<T>.Failure($"File error: {exception.Message}");
        }
        catch (JsonException exception)
        {
            LogHandledException("Config parsing failed", exception);
            return OperationResult<T>.Failure($"Config file is invalid JSON: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            LogHandledException("Access denied during operation", exception);
            return OperationResult<T>.Failure($"Access denied: {exception.Message}");
        }
        catch (TimeoutException exception)
        {
            LogHandledException("Operation timed out", exception);
            return OperationResult<T>.Failure(exception.Message);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<T>.Failure("Operation cancelled.", 130);
        }
        catch (Exception exception)
        {
            LogHandledException("Unexpected service error", exception);
            return OperationResult<T>.Failure($"Unexpected error: {exception.Message}");
        }
    }

    private sealed record ResolvedPassword(string Password, string SourceMessage);
}
