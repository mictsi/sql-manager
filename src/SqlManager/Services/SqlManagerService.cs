using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace SqlManager;

internal sealed class SqlManagerService
{
    private static readonly string[] SupportedRoles = ["db_owner", "db_datareader", "db_datawriter"];
    private const string TrashEntryTypeServer = "server";
    private const string TrashEntryTypeDatabase = "database";
    private const string TrashEntryTypeUser = "user";
    private readonly ConfigStore _configStore;
    private readonly ConfigPasswordProtector _configPasswordProtector;
    private readonly PasswordGenerator _passwordGenerator;
    private readonly SqlServerGateway _sqlServerGateway;
    private readonly PostgreSqlGateway _postgreSqlGateway;

    public SqlManagerService(ConfigStore configStore, ConfigPasswordProtector configPasswordProtector, PasswordGenerator passwordGenerator, SqlServerGateway sqlServerGateway, PostgreSqlGateway postgreSqlGateway)
    {
        _configStore = configStore;
        _configPasswordProtector = configPasswordProtector;
        _passwordGenerator = passwordGenerator;
        _sqlServerGateway = sqlServerGateway;
        _postgreSqlGateway = postgreSqlGateway;
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
                    if (FindServer(config, server.ServerName) is not null)
                    {
                        throw new UserInputException($"Server '{server.ServerName}' already exists in the config.");
                    }

                    AddVersion(server.VersionHistory, "Recovered from trash.", BuildServerVersionDetails(server));
                    config.Servers.Add(server);
                    if (string.IsNullOrWhiteSpace(config.SelectedServerName))
                    {
                        config.SelectedServerName = server.ServerName;
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
                var server = GetOrCreateServer(config, options.ServerName!, options.Provider, options.Port, options.AdminDatabase, options.AdminUsername, options.AdminPassword);
                server.AdminUsername = options.AdminUsername ?? server.AdminUsername;
                server.AdminPassword = options.AdminPassword ?? server.AdminPassword;
                config.SelectedServerName = options.ServerName!;
            }

            await SaveEditableConfigAsync(options.ConfigPath, config, options.EncryptionPassword, cancellationToken);
            return OperationResult.Success($"Config file is ready at '{options.ConfigPath}'.");
        });

    public Task<OperationResult> AddServerAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var serverName = Require(options.ServerName, "AddServer requires ServerName.");
            var config = await LoadEditableConfigAsync(options.ConfigPath, options.EncryptionPassword, cancellationToken);
            var existingServer = FindServer(config, serverName);
            var server = GetOrCreateServer(config, serverName, options.Provider, options.Port, options.AdminDatabase, options.AdminUsername, options.AdminPassword);
            AddVersion(server.VersionHistory, existingServer is null ? "Server added." : "Server updated.", BuildServerVersionDetails(server));
            if (string.IsNullOrWhiteSpace(config.SelectedServerName))
            {
                config.SelectedServerName = serverName;
            }

            await SaveEditableConfigAsync(options.ConfigPath, config, options.EncryptionPassword, cancellationToken);
            return OperationResult.Success($"Server '{serverName}' has been added to the config.");
        });

    public Task<OperationResult> UpdateServerAsync(
        string configPath,
        string existingServerName,
        string serverName,
        string? provider,
        int? port,
        string? adminDatabase,
        string adminUsername,
        string? adminPassword,
        string? encryptionPassword,
        CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var currentName = Require(existingServerName, "UpdateServer requires an existing server name.");
            var newServerName = Require(serverName, "UpdateServer requires ServerName.");
            var newAdminUsername = Require(adminUsername, "UpdateServer requires AdminUsername.");
            var config = await LoadEditableConfigAsync(configPath, encryptionPassword, cancellationToken);
            var server = FindServer(config, currentName)
                ?? throw new UserInputException($"Server '{currentName}' was not found in the config.");

            if (!currentName.Equals(newServerName, StringComparison.OrdinalIgnoreCase)
                && FindServer(config, newServerName) is not null)
            {
                throw new UserInputException($"Server '{newServerName}' already exists in the config.");
            }

            var normalizedProvider = SqlProviders.Normalize(string.IsNullOrWhiteSpace(provider) ? server.Provider : provider);
            var normalizedAdminDatabase = string.IsNullOrWhiteSpace(adminDatabase)
                ? SqlProviders.GetDefaultAdminDatabase(normalizedProvider)
                : adminDatabase!;

            server.ServerName = newServerName;
            server.Provider = normalizedProvider;
            server.Port = port;
            server.AdminDatabase = normalizedAdminDatabase;
            server.AdminUsername = newAdminUsername;
            server.AdminPassword = adminPassword ?? string.Empty;
            RefreshConnectionStrings(server);
            AddVersion(server.VersionHistory, "Server updated.", BuildServerVersionDetails(server));

            if (config.SelectedServerName.Equals(currentName, StringComparison.OrdinalIgnoreCase))
            {
                config.SelectedServerName = newServerName;
            }

            await SaveEditableConfigAsync(configPath, config, encryptionPassword, cancellationToken);
            return OperationResult.Success($"Server '{currentName}' was updated.");
        });

    public Task<OperationResult> SelectServerAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var serverName = Require(options.ServerName, "SelectServer requires ServerName.");
            var config = await LoadEditableConfigAsync(options.ConfigPath, options.EncryptionPassword, cancellationToken);
            if (FindServer(config, serverName) is null)
            {
                throw new UserInputException($"Server '{serverName}' was not found in the config.");
            }

            config.SelectedServerName = serverName;
            await SaveEditableConfigAsync(options.ConfigPath, config, options.EncryptionPassword, cancellationToken);
            return OperationResult.Success($"Selected server '{serverName}'.");
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
                        ConnectionString = BuildConnectionString(context.Provider, context.ServerName, context.Port, databaseName, userRow.UserName, password)
                    });
                    userCount++;
                }

                synchronizedDatabases.Add(databaseConfig);
            }

            var serverConfig = GetOrCreateServer(context.Config, context.ServerName, context.Provider, context.Port, context.AdminDatabase, context.AdminUsername, context.AdminPassword);
            serverConfig.Databases = synchronizedDatabases;
            serverConfig.AdminUsername = context.AdminUsername;
            serverConfig.AdminPassword = context.AdminPassword;
            context.Config.SelectedServerName = context.ServerName;
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
            else
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

            var serverConfig = GetOrCreateServer(context.Config, context.ServerName, context.Provider, context.Port, context.AdminDatabase, context.AdminUsername, context.AdminPassword);
            var databaseConfig = GetOrCreateDatabase(serverConfig, databaseName);
            AddVersion(databaseConfig.VersionHistory, "Database added to config.", BuildDatabaseVersionDetails(serverConfig, databaseConfig));
            context.Config.SelectedServerName = context.ServerName;
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
                : $"SELECT COUNT(1) FROM pg_database WHERE datname = {QuotePostgreSqlLiteral(databaseName)};";
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
            else
            {
                var quotedDatabase = QuotePostgreSqlIdentifier(databaseName);
                var query = $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = {QuotePostgreSqlLiteral(databaseName)} AND pid <> pg_backend_pid(); DROP DATABASE {quotedDatabase};";
                await ExecuteNonQueryAsync(context, adminPassword, query, context.AdminDatabase, cancellationToken);
            }

            var serverConfig = GetOrCreateServer(context.Config, context.ServerName, context.Provider, context.Port, context.AdminDatabase, context.AdminUsername, context.AdminPassword);
            SoftDeleteDatabase(context.Config, serverConfig, databaseName, options.EncryptionPassword);
            context.Config.SelectedServerName = context.ServerName;
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
                : $"SELECT COUNT(1) FROM pg_roles WHERE rolname = {QuotePostgreSqlLiteral(userName)};";
            var loginExists = await ExecuteScalarIntAsync(
                context,
                adminPassword,
                loginExistsQuery,
                context.AdminDatabase,
                cancellationToken) > 0;

            var resolvedPassword = ResolveRequestedPassword(options.NewUserPassword, loginExists, context.ServerConfig, userName);
            await EnsureServerLoginAsync(context, adminPassword, databaseName, userName, resolvedPassword.Password, loginExists, !string.IsNullOrWhiteSpace(options.NewUserPassword), cancellationToken);
            var membershipQuery = BuildRoleMembershipSyncQuery(context, databaseName, userName, roles, failIfUserMissing: false);
            await ExecuteNonQueryAsync(context, adminPassword, membershipQuery, databaseName, cancellationToken);

            var serverConfig = GetOrCreateServer(context.Config, context.ServerName, context.Provider, context.Port, context.AdminDatabase, context.AdminUsername, context.AdminPassword);
            var databaseConfig = GetOrCreateDatabase(serverConfig, databaseName);
            var userConfig = GetOrCreateUser(databaseConfig, userName);
            userConfig.Password = resolvedPassword.Password;
            userConfig.Roles = roles.ToList();
            userConfig.ConnectionString = BuildConnectionString(context.Provider, context.ServerName, context.Port, databaseName, userName, resolvedPassword.Password);
            AddVersion(userConfig.VersionHistory, "User created or updated.", BuildUserVersionDetails(serverConfig, databaseConfig, userConfig));
            context.Config.SelectedServerName = context.ServerName;
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
                : $"SELECT COUNT(1) FROM pg_roles WHERE rolname = {QuotePostgreSqlLiteral(userName)};";
            var loginExists = await ExecuteScalarIntAsync(
                context,
                adminPassword,
                loginExistsQuery,
                context.AdminDatabase,
                cancellationToken) > 0;

            var serverConfig = GetOrCreateServer(context.Config, context.ServerName, context.Provider, context.Port, context.AdminDatabase, context.AdminUsername, context.AdminPassword);
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
                    await ExecuteNonQueryAsync(context, adminPassword, dropUserQuery, assignment.DatabaseName, cancellationToken);
                    SoftDeleteUsersFromConfigDatabases(context.Config, serverConfig, userName, [assignment.DatabaseName], options.EncryptionPassword);
                    continue;
                }

                var syncQuery = BuildRoleMembershipSyncQuery(context, assignment.DatabaseName, userName, assignment.Roles, failIfUserMissing: false);
                await ExecuteNonQueryAsync(context, adminPassword, syncQuery, assignment.DatabaseName, cancellationToken);

                var databaseConfig = GetOrCreateDatabase(serverConfig, assignment.DatabaseName);
                var userConfig = GetOrCreateUser(databaseConfig, userName);
                userConfig.Password = passwordForConfig;
                userConfig.Roles = assignment.Roles.ToList();
                userConfig.ConnectionString = BuildConnectionString(context.Provider, context.ServerName, context.Port, assignment.DatabaseName, userName, passwordForConfig);
                AddVersion(userConfig.VersionHistory, "User access updated.", BuildUserVersionDetails(serverConfig, databaseConfig, userConfig));
            }

            context.Config.SelectedServerName = context.ServerName;
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
            await ExecuteNonQueryAsync(context, adminPassword, query, databaseName, cancellationToken);

            var serverConfig = GetOrCreateServer(context.Config, context.ServerName, context.Provider, context.Port, context.AdminDatabase, context.AdminUsername, context.AdminPassword);
            var databaseConfig = GetOrCreateDatabase(serverConfig, databaseName);
            var userConfig = GetOrCreateUser(databaseConfig, userName);
            userConfig.Roles = userConfig.Roles.Concat(roles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            userConfig.ConnectionString = BuildConnectionString(context.Provider, context.ServerName, context.Port, databaseName, userName, userConfig.Password);
            AddVersion(userConfig.VersionHistory, $"Roles added: {string.Join(", ", roles)}.", BuildUserVersionDetails(serverConfig, databaseConfig, userConfig));
            context.Config.SelectedServerName = context.ServerName;
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
            await ExecuteNonQueryAsync(context, adminPassword, query, databaseName, cancellationToken);

            var serverConfig = GetOrCreateServer(context.Config, context.ServerName, context.Provider, context.Port, context.AdminDatabase, context.AdminUsername, context.AdminPassword);
            var databaseConfig = GetOrCreateDatabase(serverConfig, databaseName);
            var userConfig = GetOrCreateUser(databaseConfig, userName);
            userConfig.Roles = userConfig.Roles.Except(roles, StringComparer.OrdinalIgnoreCase).ToList();
            userConfig.ConnectionString = BuildConnectionString(context.Provider, context.ServerName, context.Port, databaseName, userName, userConfig.Password);
            AddVersion(userConfig.VersionHistory, $"Roles removed: {string.Join(", ", roles)}.", BuildUserVersionDetails(serverConfig, databaseConfig, userConfig));
            context.Config.SelectedServerName = context.ServerName;
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
                await ExecuteNonQueryAsync(context, adminPassword, dropUserQuery, databaseName, cancellationToken);
            }

            var serverConfig = GetOrCreateServer(context.Config, context.ServerName, context.Provider, context.Port, context.AdminDatabase, context.AdminUsername, context.AdminPassword);
            if (selectedDatabases.Count > 0)
            {
                SoftDeleteUsersFromConfigDatabases(context.Config, serverConfig, userName, selectedDatabases, options.EncryptionPassword);
            }

            if (removeServerLogin)
            {
                var dropLoginQuery = BuildDropLoginQuery(context, userName);
                await ExecuteNonQueryAsync(context, adminPassword, dropLoginQuery, context.AdminDatabase, cancellationToken);
            }

            context.Config.SelectedServerName = context.ServerName;
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
                : $"SELECT COUNT(1) FROM pg_roles WHERE rolname = {QuotePostgreSqlLiteral(userName)};";
            var loginExists = await ExecuteScalarIntAsync(
                context,
                adminPassword,
                loginExistsQuery,
                context.AdminDatabase,
                cancellationToken);

            if (loginExists == 0)
            {
                var subject = IsSqlServer(context.Provider) ? "Login" : "Role";
                throw new UserInputException($"{subject} '{userName}' does not exist on '{context.ServerName}'.");
            }

            var newPassword = string.IsNullOrWhiteSpace(options.NewUserPassword)
                ? _passwordGenerator.Generate()
                : options.NewUserPassword!;
            await EnsureServerLoginAsync(context, adminPassword, context.AdminDatabase, userName, newPassword, true, true, cancellationToken);

            UpdateUserPasswordInConfig(GetOrCreateServer(context.Config, context.ServerName, context.Provider, context.Port, context.AdminDatabase, context.AdminUsername, context.AdminPassword), context.Provider, context.ServerName, context.Port, userName, newPassword);
            foreach (var database in GetOrCreateServer(context.Config, context.ServerName, context.Provider, context.Port, context.AdminDatabase, context.AdminUsername, context.AdminPassword).Databases)
            {
                foreach (var user in database.Users.Where(candidate => candidate.Username.Equals(userName, StringComparison.OrdinalIgnoreCase)))
                {
                    AddVersion(user.VersionHistory, "Password updated.", BuildUserVersionDetails(GetOrCreateServer(context.Config, context.ServerName, context.Provider, context.Port, context.AdminDatabase, context.AdminUsername, context.AdminPassword), database, user));
                }
            }
            context.Config.SelectedServerName = context.ServerName;
            await SaveEditableConfigAsync(options.ConfigPath, context.Config, options.EncryptionPassword, cancellationToken);

            return OperationResult.Success(
                $"Password updated for '{userName}'.",
                string.IsNullOrWhiteSpace(options.NewUserPassword)
                    ? "Password was generated automatically."
                    : "Password was set from the supplied value.");
        });

    private async Task<ResolvedServerContext> ResolveServerContextAsync(CommandOptions options, bool persistSelection, CancellationToken cancellationToken)
    {
        var config = await LoadEditableConfigAsync(options.ConfigPath, options.EncryptionPassword, cancellationToken);
        var serverName = options.ServerName;
        if (string.IsNullOrWhiteSpace(serverName))
        {
            serverName = SelectConfiguredServerName(config);
        }

        if (string.IsNullOrWhiteSpace(serverName))
        {
            throw new UserInputException("No server is configured. Run InitConfig or AddServer first, or pass ServerName explicitly.");
        }

        var serverConfig = FindServer(config, serverName);
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
        if (string.IsNullOrWhiteSpace(adminUsername))
        {
            throw new UserInputException("AdminUsername is required for this operation.");
        }

        if (persistSelection)
        {
            config.SelectedServerName = serverName;
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
            }

            await SaveEditableConfigAsync(options.ConfigPath, config, options.EncryptionPassword, cancellationToken);
        }

        return new ResolvedServerContext(config, serverConfig, serverName, provider, port, adminDatabase!, adminUsername, adminPassword ?? string.Empty, config.Timeouts);
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
                ServerName = server.ServerName,
                Provider = server.Provider,
                Port = server.Port,
                AdminDatabase = server.AdminDatabase,
                AdminUsername = server.AdminUsername,
                AdminPassword = server.AdminPassword,
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
                user.ConnectionString = BuildConnectionString(server.Provider, server.ServerName, server.Port, database.DatabaseName, user.Username, user.Password);
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
            ? config.Servers[0].ServerName
            : string.Empty;
    }

    private static ServerConfig? FindServer(SqlManagerConfig config, string serverName)
        => config.Servers.FirstOrDefault(server => server.ServerName.Equals(serverName, StringComparison.OrdinalIgnoreCase));

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
            $"Server: {server.ServerName}",
            $"Provider: {SqlProviders.GetDisplayName(server.Provider)}",
            $"Port: {(server.Port?.ToString() ?? "<default>")}",
            $"Admin Database: {server.AdminDatabase}",
            $"Admin User: {(string.IsNullOrWhiteSpace(server.AdminUsername) ? "<none>" : server.AdminUsername)}",
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

    private static ServerConfig GetOrCreateServer(SqlManagerConfig config, string serverName, string? provider, int? port, string? adminDatabase, string? adminUsername, string? adminPassword)
    {
        var normalizedProvider = SqlProviders.Normalize(provider);
        var normalizedAdminDatabase = string.IsNullOrWhiteSpace(adminDatabase)
            ? SqlProviders.GetDefaultAdminDatabase(normalizedProvider)
            : adminDatabase!;

        var server = FindServer(config, serverName);
        if (server is null)
        {
            server = new ServerConfig
            {
                ServerName = serverName,
                Provider = normalizedProvider,
                Port = port,
                AdminDatabase = normalizedAdminDatabase,
                AdminUsername = adminUsername ?? string.Empty,
                AdminPassword = adminPassword ?? string.Empty
            };
            config.Servers.Add(server);
        }
        else
        {
            server.Provider = normalizedProvider;
            server.Port = port;
            server.AdminDatabase = normalizedAdminDatabase;
            if (!string.IsNullOrWhiteSpace(adminUsername))
            {
                server.AdminUsername = adminUsername!;
            }
        }

        if (!string.IsNullOrWhiteSpace(adminPassword))
        {
            server.AdminPassword = adminPassword!;
        }

        if (string.IsNullOrWhiteSpace(config.SelectedServerName))
        {
            config.SelectedServerName = serverName;
        }

        return server;
    }

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

    private static string GetStoredPasswordForUser(ServerConfig? serverConfig, string userName)
    {
        if (serverConfig is null)
        {
            return string.Empty;
        }

        foreach (var database in serverConfig.Databases)
        {
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
        config.Trash.Add(CreateTrashEntry(TrashEntryTypeDatabase, database.EntryId, database.DatabaseName, server.ServerName, string.Empty, BuildDatabaseVersionDetails(server, database), SerializeTrashPayload(database), database.VersionHistory));
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
                config.Trash.Add(CreateTrashEntry(TrashEntryTypeUser, user.EntryId, user.Username, server.ServerName, database.DatabaseName, BuildUserVersionDetails(server, database, user), SerializeTrashPayload(user), user.VersionHistory));
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

    private static void UpdateUserPasswordInConfig(ServerConfig server, string provider, string serverName, int? port, string userName, string password)
    {
        foreach (var database in server.Databases)
        {
            foreach (var user in database.Users.Where(user => user.Username.Equals(userName, StringComparison.OrdinalIgnoreCase)))
            {
                user.Password = password;
                user.ConnectionString = BuildConnectionString(provider, serverName, port, database.DatabaseName, userName, password);
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

    private async Task ExecuteNonQueryAsync(ResolvedServerContext context, string adminPassword, string query, string database, CancellationToken cancellationToken)
    {
        if (IsSqlServer(context.Provider))
        {
            await _sqlServerGateway.ExecuteNonQueryAsync(context.ServerName, context.Port, context.AdminUsername, adminPassword, query, database, context.Timeouts, cancellationToken);
            return;
        }

        await _postgreSqlGateway.ExecuteNonQueryAsync(context.ServerName, context.Port, context.AdminUsername, adminPassword, query, database, context.Timeouts, cancellationToken);
    }

    private async Task<int> ExecuteScalarIntAsync(ResolvedServerContext context, string adminPassword, string query, string database, CancellationToken cancellationToken)
    {
        if (IsSqlServer(context.Provider))
        {
            return await _sqlServerGateway.ExecuteScalarIntAsync(context.ServerName, context.Port, context.AdminUsername, adminPassword, query, database, context.Timeouts, cancellationToken);
        }

        return await _postgreSqlGateway.ExecuteScalarIntAsync(context.ServerName, context.Port, context.AdminUsername, adminPassword, query, database, context.Timeouts, cancellationToken);
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
                cancellationToken);
        }

        return await _postgreSqlGateway.QueryNamesAsync(
            context.ServerName,
            context.Port,
            context.AdminUsername,
            adminPassword,
            "SELECT datname FROM pg_database WHERE datistemplate = false AND datallowconn = true AND datname NOT IN ('postgres') ORDER BY datname;",
            context.AdminDatabase,
            context.Timeouts,
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
                cancellationToken);
        }

        return await _postgreSqlGateway.QueryDatabaseUsersAsync(
            context.ServerName,
            context.Port,
            context.AdminUsername,
            adminPassword,
            databaseName,
            BuildPostgreSqlShowUsersQuery(databaseName),
            context.Timeouts,
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
            : BuildPostgreSqlRoleMembershipSyncQuery(databaseName, userName, desiredRoles, failIfUserMissing);

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

        foreach (var role in SupportedRoles)
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

        foreach (var role in SupportedRoles)
        {
            var managedRole = roleMap[role];
            queryBuilder.AppendLine(desiredRoles.Contains(role, StringComparer.OrdinalIgnoreCase)
                ? $"GRANT {QuotePostgreSqlIdentifier(managedRole)} TO {quotedUser};"
                : $"REVOKE {QuotePostgreSqlIdentifier(managedRole)} FROM {quotedUser};");
        }

        return queryBuilder.ToString();
    }

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

        var roleMap = GetPostgreSqlManagedRoleMap(databaseName);
        var query = new StringBuilder();
        query.AppendLine($"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = {QuotePostgreSqlLiteral(userName)}) THEN RAISE EXCEPTION 'Database user does not exist.'; END IF; END $$;");
        foreach (var role in roles)
        {
            query.AppendLine($"REVOKE {QuotePostgreSqlIdentifier(roleMap[role])} FROM {QuotePostgreSqlIdentifier(userName)};");
        }

        return query.ToString();
    }

    private static string BuildRemoveDatabaseUserQuery(ResolvedServerContext context, string databaseName, string userName)
    {
        if (IsSqlServer(context.Provider))
        {
            return $"IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = {QuoteSqlLiteral(userName)}) DROP USER {QuoteSqlServerIdentifier(userName)};";
        }

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

    private static string BuildDropLoginQuery(ResolvedServerContext context, string userName)
        => IsSqlServer(context.Provider)
            ? $"IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = {QuoteSqlLiteral(userName)}) DROP LOGIN {QuoteSqlServerIdentifier(userName)};"
            : $"DROP ROLE IF EXISTS {QuotePostgreSqlIdentifier(userName)};";

    private static string BuildPostgreSqlShowUsersQuery(string databaseName)
    {
        var roleMap = GetPostgreSqlManagedRoleMap(databaseName);
        var ownerRole = roleMap["db_owner"];
        var readerRole = roleMap["db_datareader"];
        var writerRole = roleMap["db_datawriter"];

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
            WHEN {QuotePostgreSqlLiteral(readerRole)} THEN 'db_datareader'
            WHEN {QuotePostgreSqlLiteral(writerRole)} THEN 'db_datawriter'
        END AS role_name,
        CASE role.rolname
            WHEN {QuotePostgreSqlLiteral(ownerRole)} THEN 1
            WHEN {QuotePostgreSqlLiteral(readerRole)} THEN 2
            WHEN {QuotePostgreSqlLiteral(writerRole)} THEN 3
        END AS sort_order
    FROM pg_auth_members auth
    INNER JOIN pg_roles role ON role.oid = auth.roleid
    WHERE role.rolname IN ({QuotePostgreSqlLiteral(ownerRole)}, {QuotePostgreSqlLiteral(readerRole)}, {QuotePostgreSqlLiteral(writerRole)})
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
            ["db_owner"] = BuildPostgreSqlManagedRoleName(databaseName, "owner"),
            ["db_datareader"] = BuildPostgreSqlManagedRoleName(databaseName, "reader"),
            ["db_datawriter"] = BuildPostgreSqlManagedRoleName(databaseName, "writer")
        };

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
        var readerRole = QuotePostgreSqlIdentifier(roleMap["db_datareader"]);
        var writerRole = QuotePostgreSqlIdentifier(roleMap["db_datawriter"]);

        query.AppendLine($"GRANT CONNECT, TEMP, CREATE ON DATABASE {quotedDatabase} TO {ownerRole};");
        query.AppendLine($"GRANT USAGE, CREATE ON SCHEMA public TO {ownerRole};");
        query.AppendLine($"GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO {ownerRole};");
        query.AppendLine($"GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO {ownerRole};");
        query.AppendLine($"GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA public TO {ownerRole};");
        query.AppendLine($"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL PRIVILEGES ON TABLES TO {ownerRole};");
        query.AppendLine($"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL PRIVILEGES ON SEQUENCES TO {ownerRole};");
        query.AppendLine($"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL PRIVILEGES ON FUNCTIONS TO {ownerRole};");

        query.AppendLine($"GRANT CONNECT ON DATABASE {quotedDatabase} TO {readerRole};");
        query.AppendLine($"GRANT USAGE ON SCHEMA public TO {readerRole};");
        query.AppendLine($"GRANT SELECT ON ALL TABLES IN SCHEMA public TO {readerRole};");
        query.AppendLine($"GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO {readerRole};");
        query.AppendLine($"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO {readerRole};");
        query.AppendLine($"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON SEQUENCES TO {readerRole};");

        query.AppendLine($"GRANT CONNECT ON DATABASE {quotedDatabase} TO {writerRole};");
        query.AppendLine($"GRANT USAGE ON SCHEMA public TO {writerRole};");
        query.AppendLine($"GRANT INSERT, UPDATE, DELETE, TRUNCATE ON ALL TABLES IN SCHEMA public TO {writerRole};");
        query.AppendLine($"GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO {writerRole};");
        query.AppendLine($"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT INSERT, UPDATE, DELETE, TRUNCATE ON TABLES TO {writerRole};");
        query.AppendLine($"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO {writerRole};");

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
        foreach (var role in inputRoles)
        {
            normalized.Add(role.Trim().ToLowerInvariant() switch
            {
                "dbowner" or "db_owner" => "db_owner",
                "dbreader" or "db_reader" or "db_datareader" => "db_datareader",
                "dbwriter" or "db_writer" or "db_datawriter" => "db_datawriter",
                _ => throw new UserInputException($"Unsupported role '{role}' for {SqlProviders.GetDisplayName(provider)}. Use dbowner/db_owner, dbreader/db_reader/db_datareader, or dbwriter/db_writer/db_datawriter.")
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

    private static string BuildConnectionString(string provider, string server, int? port, string database, string username, string? password)
    {
        var resolvedPassword = string.IsNullOrWhiteSpace(password) ? "<PASSWORD_REQUIRED>" : "********";
        if (IsSqlServer(provider))
        {
            var dataSource = port is > 0 ? $"{server},{port.Value}" : server;
            return $"Server={dataSource};Database={database};User ID={username};Password={resolvedPassword};Encrypt=True;TrustServerCertificate=True;";
        }

        var portSegment = port is > 0 ? $";Port={port.Value}" : string.Empty;
        return $"Host={server}{portSegment};Database={database};Username={username};Password={resolvedPassword};Ssl Mode=Require;";
    }

    private static bool IsSqlServer(string provider)
        => SqlProviders.Normalize(provider) == SqlProviders.SqlServer;

    private static string QuoteSqlServerIdentifier(string name)
        => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string QuotePostgreSqlIdentifier(string name)
        => $"\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string QuoteSqlLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string QuotePostgreSqlLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

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
