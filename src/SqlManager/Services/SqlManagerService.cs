using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SqlManager;

internal sealed class SqlManagerService
{
    private static readonly string[] SupportedRoles = ["db_owner", "db_datareader", "db_datawriter"];
    private readonly ConfigStore _configStore;
    private readonly PasswordGenerator _passwordGenerator;
    private readonly SqlServerGateway _sqlServerGateway;

    public SqlManagerService(ConfigStore configStore, PasswordGenerator passwordGenerator, SqlServerGateway sqlServerGateway)
    {
        _configStore = configStore;
        _passwordGenerator = passwordGenerator;
        _sqlServerGateway = sqlServerGateway;
    }

    public Task<OperationResult<SqlManagerConfig>> LoadConfigSummaryAsync(string configPath, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var config = await _configStore.LoadAsync(configPath, cancellationToken);
            return OperationResult<SqlManagerConfig>.Success(config, "Config loaded.");
        });

    public Task<OperationResult> InitializeConfigAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var config = await _configStore.LoadAsync(options.ConfigPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(options.ServerName))
            {
                var server = GetOrCreateServer(config, options.ServerName!, options.AdminUsername, options.AdminPassword);
                server.AdminUsername = options.AdminUsername ?? server.AdminUsername;
                server.AdminPassword = options.AdminPassword ?? server.AdminPassword;
                config.SelectedServerName = options.ServerName!;
            }

            await _configStore.SaveAsync(options.ConfigPath, config, cancellationToken);
            return OperationResult.Success($"Config file is ready at '{options.ConfigPath}'.");
        });

    public Task<OperationResult> AddServerAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var serverName = Require(options.ServerName, "AddServer requires ServerName.");
            var config = await _configStore.LoadAsync(options.ConfigPath, cancellationToken);
            GetOrCreateServer(config, serverName, options.AdminUsername, options.AdminPassword);
            if (string.IsNullOrWhiteSpace(config.SelectedServerName))
            {
                config.SelectedServerName = serverName;
            }

            await _configStore.SaveAsync(options.ConfigPath, config, cancellationToken);
            return OperationResult.Success($"Server '{serverName}' has been added to the config.");
        });

    public Task<OperationResult> SelectServerAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var serverName = Require(options.ServerName, "SelectServer requires ServerName.");
            var config = await _configStore.LoadAsync(options.ConfigPath, cancellationToken);
            if (FindServer(config, serverName) is null)
            {
                throw new UserInputException($"Server '{serverName}' was not found in the config.");
            }

            config.SelectedServerName = serverName;
            await _configStore.SaveAsync(options.ConfigPath, config, cancellationToken);
            return OperationResult.Success($"Selected server '{serverName}'.");
        });

    public Task<OperationResult> SyncServerAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, true, cancellationToken);
            var adminPassword = Require(context.AdminPassword, "SyncServer requires AdminPassword.");
            var databaseNames = await _sqlServerGateway.QueryNamesAsync(
                context.ServerName,
                context.AdminUsername,
                adminPassword,
                "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 ORDER BY name;",
                "master",
                context.Timeouts,
                cancellationToken);

            var synchronizedDatabases = new List<DatabaseConfig>();
            var userCount = 0;

            foreach (var databaseName in databaseNames)
            {
                var existingDatabase = context.ServerConfig?.Databases.FirstOrDefault(database => database.DatabaseName.Equals(databaseName, StringComparison.OrdinalIgnoreCase));
                var databaseConfig = new DatabaseConfig { DatabaseName = databaseName };
                var userRows = await _sqlServerGateway.QueryDatabaseUsersAsync(context.ServerName, context.AdminUsername, adminPassword, databaseName, context.Timeouts, cancellationToken);

                foreach (var userRow in userRows)
                {
                    var existingUser = existingDatabase?.Users.FirstOrDefault(user => user.Username.Equals(userRow.UserName, StringComparison.OrdinalIgnoreCase));
                    var password = existingUser?.Password ?? string.Empty;
                    databaseConfig.Users.Add(new UserConfig
                    {
                        Username = userRow.UserName,
                        Password = password,
                        Roles = SplitRoleList(userRow.Roles),
                        ConnectionString = BuildConnectionString(context.ServerName, databaseName, userRow.UserName, password)
                    });
                    userCount++;
                }

                synchronizedDatabases.Add(databaseConfig);
            }

            var serverConfig = GetOrCreateServer(context.Config, context.ServerName, context.AdminUsername, context.AdminPassword);
            serverConfig.Databases = synchronizedDatabases;
            serverConfig.AdminUsername = context.AdminUsername;
            serverConfig.AdminPassword = context.AdminPassword;
            context.Config.SelectedServerName = context.ServerName;
            await _configStore.SaveAsync(options.ConfigPath, context.Config, cancellationToken);

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
            var quotedDatabase = QuoteIdentifier(databaseName);
            var query = $"""
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = {QuoteLiteral(databaseName)})
BEGIN
    EXEC('CREATE DATABASE {quotedDatabase}');
END
""";

            await _sqlServerGateway.ExecuteNonQueryAsync(context.ServerName, context.AdminUsername, adminPassword, query, "master", context.Timeouts, cancellationToken);

            var serverConfig = GetOrCreateServer(context.Config, context.ServerName, context.AdminUsername, context.AdminPassword);
            GetOrCreateDatabase(serverConfig, databaseName);
            context.Config.SelectedServerName = context.ServerName;
            await _configStore.SaveAsync(options.ConfigPath, context.Config, cancellationToken);

            return OperationResult.Success($"Database '{databaseName}' is ready on '{context.ServerName}'.");
        });

    public Task<OperationResult> RemoveDatabaseAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, true, cancellationToken);
            var adminPassword = Require(context.AdminPassword, "RemoveDatabase requires AdminPassword.");
            var databaseName = Require(options.DatabaseName, "RemoveDatabase requires DatabaseName.");
            var databaseExists = await _sqlServerGateway.ExecuteScalarIntAsync(
                context.ServerName,
                context.AdminUsername,
                adminPassword,
                $"SELECT COUNT(1) FROM sys.databases WHERE name = {QuoteLiteral(databaseName)};",
                "master",
                context.Timeouts,
                cancellationToken) > 0;

            if (!databaseExists)
            {
                throw new UserInputException($"Database '{databaseName}' does not exist on '{context.ServerName}'.");
            }

            var quotedDatabase = QuoteIdentifier(databaseName);
            var query = $"ALTER DATABASE {quotedDatabase} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quotedDatabase};";
            await _sqlServerGateway.ExecuteNonQueryAsync(context.ServerName, context.AdminUsername, adminPassword, query, "master", context.Timeouts, cancellationToken);

            var serverConfig = GetOrCreateServer(context.Config, context.ServerName, context.AdminUsername, context.AdminPassword);
            serverConfig.Databases = serverConfig.Databases
                .Where(database => !database.DatabaseName.Equals(databaseName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            context.Config.SelectedServerName = context.ServerName;
            await _configStore.SaveAsync(options.ConfigPath, context.Config, cancellationToken);

            return OperationResult.Success($"Removed database '{databaseName}' from '{context.ServerName}'.");
        });

    public Task<OperationResult> CreateUserAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, true, cancellationToken);
            var adminPassword = Require(context.AdminPassword, "CreateUser requires AdminPassword.");
            var databaseName = Require(options.DatabaseName, "CreateUser requires DatabaseName.");
            var userName = Require(options.UserName, "CreateUser requires UserName.");
            var roles = NormalizeRoles(options.Roles);
            if (roles.Count == 0)
            {
                throw new UserInputException("CreateUser requires Roles.");
            }

            var loginExists = await _sqlServerGateway.ExecuteScalarIntAsync(
                context.ServerName,
                context.AdminUsername,
                adminPassword,
                $"SELECT COUNT(1) FROM sys.server_principals WHERE name = {QuoteLiteral(userName)};",
                "master",
                context.Timeouts,
                cancellationToken) > 0;

            var resolvedPassword = ResolveRequestedPassword(options.NewUserPassword, loginExists, context.ServerConfig, userName);
            var quotedUser = QuoteIdentifier(userName);
            var quotedDatabase = QuoteIdentifier(databaseName);

            if (!loginExists)
            {
                var createLogin = $"CREATE LOGIN {quotedUser} WITH PASSWORD = {QuoteLiteral(resolvedPassword.Password)}, CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = {quotedDatabase}";
                var query = $"EXEC({QuoteLiteral(createLogin)}); ALTER LOGIN {quotedUser} ENABLE;";
                await _sqlServerGateway.ExecuteNonQueryAsync(context.ServerName, context.AdminUsername, adminPassword, query, "master", context.Timeouts, cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(options.NewUserPassword))
            {
                var alterLogin = $"ALTER LOGIN {quotedUser} WITH PASSWORD = {QuoteLiteral(resolvedPassword.Password)}";
                var query = $"EXEC({QuoteLiteral(alterLogin)}); ALTER LOGIN {quotedUser} ENABLE;";
                await _sqlServerGateway.ExecuteNonQueryAsync(context.ServerName, context.AdminUsername, adminPassword, query, "master", context.Timeouts, cancellationToken);
            }

            var queryBuilder = new StringBuilder();
            queryBuilder.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = {QuoteLiteral(userName)})");
            queryBuilder.AppendLine("BEGIN");
            queryBuilder.AppendLine($"    CREATE USER {quotedUser} FOR LOGIN {quotedUser};");
            queryBuilder.AppendLine("END");
            foreach (var role in roles)
            {
                queryBuilder.AppendLine($"IF NOT EXISTS (");
                queryBuilder.AppendLine("    SELECT 1");
                queryBuilder.AppendLine("    FROM sys.database_role_members drm");
                queryBuilder.AppendLine("    INNER JOIN sys.database_principals rp ON drm.role_principal_id = rp.principal_id");
                queryBuilder.AppendLine("    INNER JOIN sys.database_principals mp ON drm.member_principal_id = mp.principal_id");
                queryBuilder.AppendLine($"    WHERE rp.name = {QuoteLiteral(role)} AND mp.name = {QuoteLiteral(userName)})");
                queryBuilder.AppendLine($"    ALTER ROLE {QuoteIdentifier(role)} ADD MEMBER {quotedUser};");
            }

            await _sqlServerGateway.ExecuteNonQueryAsync(context.ServerName, context.AdminUsername, adminPassword, queryBuilder.ToString(), databaseName, context.Timeouts, cancellationToken);

            var serverConfig = GetOrCreateServer(context.Config, context.ServerName, context.AdminUsername, context.AdminPassword);
            var databaseConfig = GetOrCreateDatabase(serverConfig, databaseName);
            var userConfig = GetOrCreateUser(databaseConfig, userName);
            userConfig.Password = resolvedPassword.Password;
            userConfig.Roles = roles.ToList();
            userConfig.ConnectionString = BuildConnectionString(context.ServerName, databaseName, userName, resolvedPassword.Password);
            context.Config.SelectedServerName = context.ServerName;
            await _configStore.SaveAsync(options.ConfigPath, context.Config, cancellationToken);

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
            var assignments = NormalizeDatabaseRoleAssignments(options.DatabaseRoleAssignments);
            if (assignments.Count == 0)
            {
                throw new UserInputException("SetUserAccess requires at least one database role selection.");
            }

            var loginExists = await _sqlServerGateway.ExecuteScalarIntAsync(
                context.ServerName,
                context.AdminUsername,
                adminPassword,
                $"SELECT COUNT(1) FROM sys.server_principals WHERE name = {QuoteLiteral(userName)};",
                "master",
                context.Timeouts,
                cancellationToken) > 0;

            var serverConfig = GetOrCreateServer(context.Config, context.ServerName, context.AdminUsername, context.AdminPassword);
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

                var quotedUser = QuoteIdentifier(userName);
                var quotedDatabase = QuoteIdentifier(firstAssignedDatabase);
                var createLogin = $"CREATE LOGIN {quotedUser} WITH PASSWORD = {QuoteLiteral(passwordForConfig)}, CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = {quotedDatabase}";
                var createLoginQuery = $"EXEC({QuoteLiteral(createLogin)}); ALTER LOGIN {quotedUser} ENABLE;";
                await _sqlServerGateway.ExecuteNonQueryAsync(context.ServerName, context.AdminUsername, adminPassword, createLoginQuery, "master", context.Timeouts, cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(options.NewUserPassword))
            {
                passwordForConfig = options.NewUserPassword!;
                passwordMessage = "Password was set from the supplied NewUserPassword value.";

                var quotedUser = QuoteIdentifier(userName);
                var alterLogin = $"ALTER LOGIN {quotedUser} WITH PASSWORD = {QuoteLiteral(passwordForConfig)}";
                var alterLoginQuery = $"EXEC({QuoteLiteral(alterLogin)}); ALTER LOGIN {quotedUser} ENABLE;";
                await _sqlServerGateway.ExecuteNonQueryAsync(context.ServerName, context.AdminUsername, adminPassword, alterLoginQuery, "master", context.Timeouts, cancellationToken);
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
                    var dropUserQuery = $"IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = {QuoteLiteral(userName)}) DROP USER {QuoteIdentifier(userName)};";
                    await _sqlServerGateway.ExecuteNonQueryAsync(context.ServerName, context.AdminUsername, adminPassword, dropUserQuery, assignment.DatabaseName, context.Timeouts, cancellationToken);
                    RemoveUserFromConfigDatabases(serverConfig, userName, [assignment.DatabaseName]);
                    continue;
                }

                var syncQuery = BuildRoleMembershipSyncQuery(userName, assignment.Roles, failIfUserMissing: false);
                await _sqlServerGateway.ExecuteNonQueryAsync(context.ServerName, context.AdminUsername, adminPassword, syncQuery, assignment.DatabaseName, context.Timeouts, cancellationToken);

                var databaseConfig = GetOrCreateDatabase(serverConfig, assignment.DatabaseName);
                var userConfig = GetOrCreateUser(databaseConfig, userName);
                userConfig.Password = passwordForConfig;
                userConfig.Roles = assignment.Roles.ToList();
                userConfig.ConnectionString = BuildConnectionString(context.ServerName, assignment.DatabaseName, userName, passwordForConfig);
            }

            context.Config.SelectedServerName = context.ServerName;
            await _configStore.SaveAsync(options.ConfigPath, context.Config, cancellationToken);

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
            var roles = NormalizeRoles(options.Roles);
            if (roles.Count == 0)
            {
                throw new UserInputException("AddRole requires Roles.");
            }

            var queryBuilder = new StringBuilder();
            queryBuilder.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = {QuoteLiteral(userName)})");
            queryBuilder.AppendLine("    THROW 50000, 'Database user does not exist.', 1;");

            foreach (var role in roles)
            {
                queryBuilder.AppendLine($"IF NOT EXISTS (");
                queryBuilder.AppendLine("    SELECT 1");
                queryBuilder.AppendLine("    FROM sys.database_role_members drm");
                queryBuilder.AppendLine("    INNER JOIN sys.database_principals rp ON drm.role_principal_id = rp.principal_id");
                queryBuilder.AppendLine("    INNER JOIN sys.database_principals mp ON drm.member_principal_id = mp.principal_id");
                queryBuilder.AppendLine($"    WHERE rp.name = {QuoteLiteral(role)} AND mp.name = {QuoteLiteral(userName)})");
                queryBuilder.AppendLine($"    ALTER ROLE {QuoteIdentifier(role)} ADD MEMBER {QuoteIdentifier(userName)};");
            }

            await _sqlServerGateway.ExecuteNonQueryAsync(context.ServerName, context.AdminUsername, adminPassword, queryBuilder.ToString(), databaseName, context.Timeouts, cancellationToken);

            var serverConfig = GetOrCreateServer(context.Config, context.ServerName, context.AdminUsername, context.AdminPassword);
            var databaseConfig = GetOrCreateDatabase(serverConfig, databaseName);
            var userConfig = GetOrCreateUser(databaseConfig, userName);
            userConfig.Roles = userConfig.Roles.Concat(roles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            userConfig.ConnectionString = BuildConnectionString(context.ServerName, databaseName, userName, userConfig.Password);
            context.Config.SelectedServerName = context.ServerName;
            await _configStore.SaveAsync(options.ConfigPath, context.Config, cancellationToken);

            return OperationResult.Success($"Added role(s) '{string.Join(", ", roles)}' to '{userName}' on '{databaseName}'.");
        });

    public Task<OperationResult> RemoveRoleAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, true, cancellationToken);
            var adminPassword = Require(context.AdminPassword, "RemoveRole requires AdminPassword.");
            var databaseName = Require(options.DatabaseName, "RemoveRole requires DatabaseName.");
            var userName = Require(options.UserName, "RemoveRole requires UserName.");
            var roles = NormalizeRoles(options.Roles);
            if (roles.Count == 0)
            {
                throw new UserInputException("RemoveRole requires Roles.");
            }

            var queryBuilder = new StringBuilder();
            queryBuilder.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = {QuoteLiteral(userName)})");
            queryBuilder.AppendLine("    THROW 50000, 'Database user does not exist.', 1;");

            foreach (var role in roles)
            {
                queryBuilder.AppendLine("IF EXISTS (");
                queryBuilder.AppendLine("    SELECT 1");
                queryBuilder.AppendLine("    FROM sys.database_role_members drm");
                queryBuilder.AppendLine("    INNER JOIN sys.database_principals rp ON drm.role_principal_id = rp.principal_id");
                queryBuilder.AppendLine("    INNER JOIN sys.database_principals mp ON drm.member_principal_id = mp.principal_id");
                queryBuilder.AppendLine($"    WHERE rp.name = {QuoteLiteral(role)} AND mp.name = {QuoteLiteral(userName)})");
                queryBuilder.AppendLine($"    ALTER ROLE {QuoteIdentifier(role)} DROP MEMBER {QuoteIdentifier(userName)};");
            }

            await _sqlServerGateway.ExecuteNonQueryAsync(context.ServerName, context.AdminUsername, adminPassword, queryBuilder.ToString(), databaseName, context.Timeouts, cancellationToken);

            var serverConfig = GetOrCreateServer(context.Config, context.ServerName, context.AdminUsername, context.AdminPassword);
            var databaseConfig = GetOrCreateDatabase(serverConfig, databaseName);
            var userConfig = GetOrCreateUser(databaseConfig, userName);
            userConfig.Roles = userConfig.Roles.Except(roles, StringComparer.OrdinalIgnoreCase).ToList();
            userConfig.ConnectionString = BuildConnectionString(context.ServerName, databaseName, userName, userConfig.Password);
            context.Config.SelectedServerName = context.ServerName;
            await _configStore.SaveAsync(options.ConfigPath, context.Config, cancellationToken);

            return OperationResult.Success($"Removed role(s) '{string.Join(", ", roles)}' from '{userName}' on '{databaseName}'.");
        });

    public Task<OperationResult<IReadOnlyList<DatabaseUserRow>>> ShowUsersAsync(CommandOptions options, CancellationToken cancellationToken)
        => ExecuteAsync(async () =>
        {
            var context = await ResolveServerContextAsync(options, true, cancellationToken);
            var adminPassword = Require(context.AdminPassword, "ShowUsers requires AdminPassword.");
            var databaseName = Require(options.DatabaseName, "ShowUsers requires DatabaseName.");
            var rows = await _sqlServerGateway.QueryDatabaseUsersAsync(context.ServerName, context.AdminUsername, adminPassword, databaseName, context.Timeouts, cancellationToken);
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
                var dropUserQuery = $"IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = {QuoteLiteral(userName)}) DROP USER {QuoteIdentifier(userName)};";
                await _sqlServerGateway.ExecuteNonQueryAsync(context.ServerName, context.AdminUsername, adminPassword, dropUserQuery, databaseName, context.Timeouts, cancellationToken);
            }

            var serverConfig = GetOrCreateServer(context.Config, context.ServerName, context.AdminUsername, context.AdminPassword);
            if (selectedDatabases.Count > 0)
            {
                RemoveUserFromConfigDatabases(serverConfig, userName, selectedDatabases);
            }

            if (removeServerLogin)
            {
                var dropLoginQuery = $"IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = {QuoteLiteral(userName)}) DROP LOGIN {QuoteIdentifier(userName)};";
                await _sqlServerGateway.ExecuteNonQueryAsync(context.ServerName, context.AdminUsername, adminPassword, dropLoginQuery, "master", context.Timeouts, cancellationToken);
            }

            context.Config.SelectedServerName = context.ServerName;
            await _configStore.SaveAsync(options.ConfigPath, context.Config, cancellationToken);

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
            var loginExists = await _sqlServerGateway.ExecuteScalarIntAsync(
                context.ServerName,
                context.AdminUsername,
                adminPassword,
                $"SELECT COUNT(1) FROM sys.server_principals WHERE name = {QuoteLiteral(userName)};",
                "master",
                context.Timeouts,
                cancellationToken);

            if (loginExists == 0)
            {
                throw new UserInputException($"Login '{userName}' does not exist on '{context.ServerName}'.");
            }

            var newPassword = string.IsNullOrWhiteSpace(options.NewUserPassword)
                ? _passwordGenerator.Generate()
                : options.NewUserPassword!;
            var alterLoginStatement = $"ALTER LOGIN {QuoteIdentifier(userName)} WITH PASSWORD = {QuoteLiteral(newPassword)}";
            await _sqlServerGateway.ExecuteNonQueryAsync(
                context.ServerName,
                context.AdminUsername,
                adminPassword,
                $"EXEC({QuoteLiteral(alterLoginStatement)});",
                "master",
                context.Timeouts,
                cancellationToken);

            UpdateUserPasswordInConfig(GetOrCreateServer(context.Config, context.ServerName, context.AdminUsername, context.AdminPassword), context.ServerName, userName, newPassword);
            context.Config.SelectedServerName = context.ServerName;
            await _configStore.SaveAsync(options.ConfigPath, context.Config, cancellationToken);

            return OperationResult.Success(
                $"Password updated for '{userName}'.",
                string.IsNullOrWhiteSpace(options.NewUserPassword)
                    ? "Password was generated automatically."
                    : "Password was set from the supplied value.");
        });

    private async Task<ResolvedServerContext> ResolveServerContextAsync(CommandOptions options, bool persistSelection, CancellationToken cancellationToken)
    {
        var config = await _configStore.LoadAsync(options.ConfigPath, cancellationToken);
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
            if (serverConfig is not null && !string.IsNullOrWhiteSpace(options.AdminUsername))
            {
                serverConfig.AdminUsername = options.AdminUsername!;
            }

            if (serverConfig is not null && !string.IsNullOrWhiteSpace(options.AdminPassword))
            {
                serverConfig.AdminPassword = options.AdminPassword!;
            }

            await _configStore.SaveAsync(options.ConfigPath, config, cancellationToken);
        }

        return new ResolvedServerContext(config, serverConfig, serverName, adminUsername, adminPassword ?? string.Empty, config.Timeouts);
    }

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

    private static ServerConfig GetOrCreateServer(SqlManagerConfig config, string serverName, string? adminUsername, string? adminPassword)
    {
        var server = FindServer(config, serverName);
        if (server is null)
        {
            server = new ServerConfig
            {
                ServerName = serverName,
                AdminUsername = adminUsername ?? string.Empty,
                AdminPassword = adminPassword ?? string.Empty
            };
            config.Servers.Add(server);
        }
        else if (!string.IsNullOrWhiteSpace(adminUsername))
        {
            server.AdminUsername = adminUsername!;
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

    private static void UpdateUserPasswordInConfig(ServerConfig server, string serverName, string userName, string password)
    {
        foreach (var database in server.Databases)
        {
            foreach (var user in database.Users.Where(user => user.Username.Equals(userName, StringComparison.OrdinalIgnoreCase)))
            {
                user.Password = password;
                user.ConnectionString = BuildConnectionString(serverName, database.DatabaseName, userName, password);
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

    private async Task<IReadOnlyList<string>> LoadServerDatabaseNamesAsync(ResolvedServerContext context, string adminPassword, CancellationToken cancellationToken)
        => await _sqlServerGateway.QueryNamesAsync(
            context.ServerName,
            context.AdminUsername,
            adminPassword,
            "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 ORDER BY name;",
            "master",
            context.Timeouts,
            cancellationToken);

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

    private static List<DatabaseRoleAssignment> NormalizeDatabaseRoleAssignments(IReadOnlyList<DatabaseRoleAssignment> assignments)
    {
        var normalized = new List<DatabaseRoleAssignment>();
        foreach (var assignment in assignments)
        {
            var databaseName = Require(assignment.DatabaseName, "Database role assignments require DatabaseName.");
            var roles = assignment.Roles.Count == 0
                ? []
                : NormalizeRoles(assignment.Roles);
            normalized.Add(new DatabaseRoleAssignment(databaseName, roles));
        }

        return normalized;
    }

    private static string BuildRoleMembershipSyncQuery(string userName, IReadOnlyCollection<string> desiredRoles, bool failIfUserMissing)
    {
        var quotedUser = QuoteIdentifier(userName);
        var queryBuilder = new StringBuilder();
        if (failIfUserMissing)
        {
            queryBuilder.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = {QuoteLiteral(userName)})");
            queryBuilder.AppendLine("    THROW 50000, 'Database user does not exist.', 1;");
        }
        else
        {
            queryBuilder.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = {QuoteLiteral(userName)})");
            queryBuilder.AppendLine("BEGIN");
            queryBuilder.AppendLine($"    CREATE USER {quotedUser} FOR LOGIN {quotedUser};");
            queryBuilder.AppendLine("END");
        }

        foreach (var role in SupportedRoles)
        {
            var quotedRole = QuoteIdentifier(role);
            var wantsRole = desiredRoles.Contains(role, StringComparer.OrdinalIgnoreCase);
            queryBuilder.AppendLine(wantsRole ? "IF NOT EXISTS (" : "IF EXISTS (");
            queryBuilder.AppendLine("    SELECT 1");
            queryBuilder.AppendLine("    FROM sys.database_role_members drm");
            queryBuilder.AppendLine("    INNER JOIN sys.database_principals rp ON drm.role_principal_id = rp.principal_id");
            queryBuilder.AppendLine("    INNER JOIN sys.database_principals mp ON drm.member_principal_id = mp.principal_id");
            queryBuilder.AppendLine($"    WHERE rp.name = {QuoteLiteral(role)} AND mp.name = {QuoteLiteral(userName)})");
            queryBuilder.AppendLine(wantsRole
                ? $"    ALTER ROLE {quotedRole} ADD MEMBER {quotedUser};"
                : $"    ALTER ROLE {quotedRole} DROP MEMBER {quotedUser};");
        }

        return queryBuilder.ToString();
    }

    private static List<string> NormalizeRoles(IReadOnlyList<string> inputRoles)
    {
        var normalized = new List<string>();
        foreach (var role in inputRoles)
        {
            normalized.Add(role.Trim().ToLowerInvariant() switch
            {
                "dbowner" or "db_owner" => "db_owner",
                "dbreader" or "db_reader" or "db_datareader" => "db_datareader",
                "dbwriter" or "db_writer" or "db_datawriter" => "db_datawriter",
                _ => throw new UserInputException($"Unsupported role '{role}'. Use dbowner/db_owner, dbreader/db_reader/db_datareader, or dbwriter/db_writer/db_datawriter.")
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

    private static string BuildConnectionString(string server, string database, string username, string? password)
        => $"Server={server};Database={database};User ID={username};Password={(string.IsNullOrWhiteSpace(password) ? "<PASSWORD_REQUIRED>" : password)};Encrypt=True;TrustServerCertificate=True;";

    private static string QuoteIdentifier(string name)
        => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string QuoteLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string Require(string? value, string errorMessage)
        => string.IsNullOrWhiteSpace(value)
            ? throw new UserInputException(errorMessage)
            : value;

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
            if (exception.Number == -2)
            {
                return OperationResult.Failure("SQL Server operation timed out.");
            }

            return OperationResult.Failure($"SQL Server error: {exception.Message}");
        }
        catch (IOException exception)
        {
            return OperationResult.Failure($"File error: {exception.Message}");
        }
        catch (JsonException exception)
        {
            return OperationResult.Failure($"Config file is invalid JSON: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return OperationResult.Failure($"Access denied: {exception.Message}");
        }
        catch (TimeoutException exception)
        {
            return OperationResult.Failure(exception.Message);
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Failure("Operation cancelled.", 130);
        }
        catch (Exception exception)
        {
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
            if (exception.Number == -2)
            {
                return OperationResult<T>.Failure("SQL Server operation timed out.");
            }

            return OperationResult<T>.Failure($"SQL Server error: {exception.Message}");
        }
        catch (IOException exception)
        {
            return OperationResult<T>.Failure($"File error: {exception.Message}");
        }
        catch (JsonException exception)
        {
            return OperationResult<T>.Failure($"Config file is invalid JSON: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return OperationResult<T>.Failure($"Access denied: {exception.Message}");
        }
        catch (TimeoutException exception)
        {
            return OperationResult<T>.Failure(exception.Message);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<T>.Failure("Operation cancelled.", 130);
        }
        catch (Exception exception)
        {
            return OperationResult<T>.Failure($"Unexpected error: {exception.Message}");
        }
    }

    private sealed record ResolvedPassword(string Password, string SourceMessage);
}
