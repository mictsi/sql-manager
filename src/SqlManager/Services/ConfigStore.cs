using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlManager;

internal sealed class ConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<SqlManagerConfig> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return CreateDefaultConfig();
        }

        await using var stream = File.OpenRead(path);
        var root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        if (root is null)
        {
            return CreateDefaultConfig();
        }

        var config = root["encryptedPayload"] is not null
            ? ParseEncryptedEnvelope(root)
            : root["servers"] is not null
            ? ParseCurrent(root)
            : ParseLegacy(root);

        Normalize(config);
        return config;
    }

    public async Task SaveAsync(string path, SqlManagerConfig config, CancellationToken cancellationToken)
    {
        Normalize(config);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = config.EncryptPasswords && !string.IsNullOrWhiteSpace(config.EncryptedPayload)
            ? JsonSerializer.Serialize(new EncryptedConfigEnvelope
            {
                ThemeName = config.ThemeName,
                EncryptPasswords = true,
                EncryptionKey = config.EncryptionKey,
                EncryptedPayload = config.EncryptedPayload
            }, SerializerOptions)
            : JsonSerializer.Serialize(config, SerializerOptions);

        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    internal static void NormalizeLoadedConfig(SqlManagerConfig config)
        => Normalize(config);

    private static SqlManagerConfig ParseEncryptedEnvelope(JsonNode root)
        => new()
        {
            ThemeName = root["themeName"]?.GetValue<string>() ?? TerminalThemeCatalog.DefaultThemeName,
            EncryptPasswords = root["encryptPasswords"]?.GetValue<bool>() ?? true,
            EncryptionKey = root["encryptionKey"]?.GetValue<string>() ?? string.Empty,
            EncryptedPayload = root["encryptedPayload"]?.GetValue<string>() ?? string.Empty
        };

    private static SqlManagerConfig ParseCurrent(JsonNode root)
    {
        var config = new SqlManagerConfig
        {
            SelectedServerName = root["selectedServerName"]?.GetValue<string>() ?? string.Empty,
            ThemeName = root["themeName"]?.GetValue<string>() ?? TerminalThemeCatalog.DefaultThemeName,
            EncryptPasswords = root["encryptPasswords"]?.GetValue<bool>() ?? false,
            EncryptionKey = root["encryptionKey"]?.GetValue<string>() ?? string.Empty,
            Timeouts = new SqlTimeoutConfig
            {
                ConnectionTimeoutSeconds = root["timeouts"]?["connectionTimeoutSeconds"]?.GetValue<int>() ?? SqlTimeoutConfig.DefaultConnectionTimeoutSeconds,
                CommandTimeoutSeconds = root["timeouts"]?["commandTimeoutSeconds"]?.GetValue<int>() ?? SqlTimeoutConfig.DefaultCommandTimeoutSeconds
            },
            Trash = ParseTrash(root["trash"]?.AsArray())
        };

        foreach (var serverNode in root["servers"]?.AsArray() ?? [])
        {
            if (serverNode is null)
            {
                continue;
            }

            var server = new ServerConfig
            {
                EntryId = serverNode["entryId"]?.GetValue<string>() ?? string.Empty,
                ServerIdentifier = serverNode["serverIdentifier"]?.GetValue<string>() ?? string.Empty,
                DisplayName = serverNode["displayName"]?.GetValue<string>() ?? string.Empty,
                ServerName = serverNode["serverName"]?.GetValue<string>() ?? string.Empty,
                Provider = serverNode["provider"]?.GetValue<string>() ?? SqlProviders.SqlServer,
                Port = serverNode["port"]?.GetValue<int?>(),
                AdminDatabase = serverNode["adminDatabase"]?.GetValue<string>() ?? string.Empty,
                AdminUsername = serverNode["adminUsername"]?.GetValue<string>() ?? string.Empty,
                AdminPassword = serverNode["adminPassword"]?.GetValue<string>() ?? string.Empty,
                PostgreSqlSslMode = serverNode["postgreSqlSslMode"]?.GetValue<string>() ?? string.Empty,
                PostgreSqlPooling = serverNode["postgreSqlPooling"]?.GetValue<bool?>(),
                SqlServerTrustMode = serverNode["sqlServerTrustMode"]?.GetValue<string>() ?? string.Empty,
                ConnectionTimeoutSeconds = serverNode["connectionTimeoutSeconds"]?.GetValue<int?>(),
                CommandTimeoutSeconds = serverNode["commandTimeoutSeconds"]?.GetValue<int?>(),
                DatabasesPayload = serverNode["databasesPayload"]?.GetValue<string>() ?? string.Empty,
                Encrypted = serverNode["encrypted"]?.GetValue<bool>() ?? false,
                VersionHistory = ParseVersionHistory(serverNode["versionHistory"]?.AsArray())
            };

            foreach (var databaseNode in serverNode["databases"]?.AsArray() ?? [])
            {
                if (databaseNode is null)
                {
                    continue;
                }

                var database = new DatabaseConfig
                {
                    EntryId = databaseNode["entryId"]?.GetValue<string>() ?? string.Empty,
                    DatabaseName = databaseNode["databaseName"]?.GetValue<string>() ?? string.Empty,
                    VersionHistory = ParseVersionHistory(databaseNode["versionHistory"]?.AsArray())
                };

                foreach (var userNode in databaseNode["users"]?.AsArray() ?? [])
                {
                    if (userNode is null)
                    {
                        continue;
                    }

                    database.Users.Add(new UserConfig
                    {
                        EntryId = userNode["entryId"]?.GetValue<string>() ?? string.Empty,
                        Username = userNode["username"]?.GetValue<string>() ?? string.Empty,
                        Password = userNode["password"]?.GetValue<string>() ?? string.Empty,
                        Encrypted = userNode["encrypted"]?.GetValue<bool>() ?? false,
                        ConnectionString = userNode["connectionString"]?.GetValue<string>() ?? string.Empty,
                        Roles = userNode["roles"]?.AsArray().Select(role => role?.GetValue<string>() ?? string.Empty).Where(role => !string.IsNullOrWhiteSpace(role)).ToList() ?? [],
                        VersionHistory = ParseVersionHistory(userNode["versionHistory"]?.AsArray())
                    });
                }

                server.Databases.Add(database);
            }

            config.Servers.Add(server);
        }

        return config;
    }

    private static SqlManagerConfig ParseLegacy(JsonNode root)
    {
        var legacyAdminUsername = root["sqlAdmin"]?["username"]?.GetValue<string>() ?? string.Empty;
        var legacySelectedServer = root["sqlAdmin"]?["serverName"]?.GetValue<string>() ?? string.Empty;
        var config = CreateDefaultConfig();
        var groupedDatabases = new Dictionary<string, List<JsonNode>>(StringComparer.OrdinalIgnoreCase);

        foreach (var databaseNode in root["databases"]?.AsArray() ?? [])
        {
            if (databaseNode is null)
            {
                continue;
            }

            var serverName = databaseNode["serverName"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(serverName))
            {
                serverName = legacySelectedServer;
            }

            if (string.IsNullOrWhiteSpace(serverName))
            {
                continue;
            }

            if (!groupedDatabases.TryGetValue(serverName, out var group))
            {
                group = [];
                groupedDatabases[serverName] = group;
            }

            group.Add(databaseNode);
        }

        foreach (var group in groupedDatabases)
        {
            var server = new ServerConfig
            {
                ServerIdentifier = group.Key,
                DisplayName = group.Key,
                ServerName = group.Key,
                Provider = SqlProviders.SqlServer,
                AdminUsername = legacyAdminUsername,
                AdminPassword = string.Empty
            };

            foreach (var databaseNode in group.Value)
            {
                var database = new DatabaseConfig
                {
                    DatabaseName = databaseNode["databaseName"]?.GetValue<string>() ?? string.Empty
                };

                foreach (var userNode in databaseNode["users"]?.AsArray() ?? [])
                {
                    if (userNode is null)
                    {
                        continue;
                    }

                    database.Users.Add(new UserConfig
                    {
                        Username = userNode["username"]?.GetValue<string>() ?? string.Empty,
                        Password = userNode["password"]?.GetValue<string>() ?? string.Empty,
                        ConnectionString = userNode["connectionString"]?.GetValue<string>() ?? string.Empty,
                        Roles = userNode["roles"]?.AsArray().Select(role => role?.GetValue<string>() ?? string.Empty).Where(role => !string.IsNullOrWhiteSpace(role)).ToList() ?? []
                    });
                }

                server.Databases.Add(database);
            }

            config.Servers.Add(server);
        }

        if (!string.IsNullOrWhiteSpace(legacySelectedServer) && config.Servers.All(server => !server.ServerName.Equals(legacySelectedServer, StringComparison.OrdinalIgnoreCase)))
        {
            config.Servers.Add(new ServerConfig
            {
                ServerIdentifier = legacySelectedServer,
                DisplayName = legacySelectedServer,
                ServerName = legacySelectedServer,
                Provider = SqlProviders.SqlServer,
                AdminUsername = legacyAdminUsername,
                AdminPassword = string.Empty
            });
        }

        config.SelectedServerName = !string.IsNullOrWhiteSpace(legacySelectedServer)
            ? legacySelectedServer
            : config.Servers.Count == 1
                ? config.Servers[0].ServerName
                : string.Empty;

        return config;
    }

    private static SqlManagerConfig CreateDefaultConfig() => new();

    private static List<EntryVersion> ParseVersionHistory(JsonArray? historyNodes)
    {
        var versions = new List<EntryVersion>();
        foreach (var node in historyNodes ?? [])
        {
            if (node is null)
            {
                continue;
            }

            versions.Add(new EntryVersion
            {
                VersionNumber = node["versionNumber"]?.GetValue<int>() ?? 0,
                ChangedAtUtc = node["changedAtUtc"]?.GetValue<string>() ?? string.Empty,
                Summary = node["summary"]?.GetValue<string>() ?? string.Empty,
                Details = node["details"]?.GetValue<string>() ?? string.Empty
            });
        }

        return versions;
    }

    private static List<TrashEntry> ParseTrash(JsonArray? trashNodes)
    {
        var trash = new List<TrashEntry>();
        foreach (var node in trashNodes ?? [])
        {
            if (node is null)
            {
                continue;
            }

            trash.Add(new TrashEntry
            {
                TrashId = node["trashId"]?.GetValue<string>() ?? string.Empty,
                EntryType = node["entryType"]?.GetValue<string>() ?? string.Empty,
                EntryId = node["entryId"]?.GetValue<string>() ?? string.Empty,
                DisplayName = node["displayName"]?.GetValue<string>() ?? string.Empty,
                ParentServerName = node["parentServerName"]?.GetValue<string>() ?? string.Empty,
                ParentDatabaseName = node["parentDatabaseName"]?.GetValue<string>() ?? string.Empty,
                DeletedAtUtc = node["deletedAtUtc"]?.GetValue<string>() ?? string.Empty,
                Details = node["details"]?.GetValue<string>() ?? string.Empty,
                PayloadJson = node["payloadJson"]?.GetValue<string>() ?? string.Empty,
                VersionHistory = ParseVersionHistory(node["versionHistory"]?.AsArray())
            });
        }

        return trash;
    }

    private static void Normalize(SqlManagerConfig config)
    {
        config.SelectedServerName ??= string.Empty;
        config.ThemeName = TerminalThemeCatalog.NormalizeThemeName(config.ThemeName);
        config.EncryptionKey ??= string.Empty;
        config.Timeouts ??= new SqlTimeoutConfig();
        if (config.Timeouts.ConnectionTimeoutSeconds <= 0)
        {
            config.Timeouts.ConnectionTimeoutSeconds = SqlTimeoutConfig.DefaultConnectionTimeoutSeconds;
        }

        if (config.Timeouts.CommandTimeoutSeconds <= 0)
        {
            config.Timeouts.CommandTimeoutSeconds = SqlTimeoutConfig.DefaultCommandTimeoutSeconds;
        }

        config.Servers ??= [];
        config.Trash ??= [];

        var desiredSelectedServer = config.SelectedServerName;
        var usedIdentifiers = new HashSet<int>();
        ServerConfig? selectedServer = null;
        foreach (var server in config.Servers)
        {
            var existingIdentifier = server.ServerIdentifier;
            server.EntryId = string.IsNullOrWhiteSpace(server.EntryId) ? Guid.NewGuid().ToString("N") : server.EntryId;
            server.ServerIdentifier = ServerConnections.NormalizeExistingIdentifier(existingIdentifier, usedIdentifiers);
            if (selectedServer is null && ServerConnections.MatchesSelectionKey(desiredSelectedServer, existingIdentifier, server.ServerName))
            {
                selectedServer = server;
            }

            server.DisplayName = string.IsNullOrWhiteSpace(server.DisplayName) ? server.ServerName : server.DisplayName.Trim();
            server.ServerName ??= string.Empty;
            server.Provider = SqlProviders.Normalize(server.Provider);
            if (server.Port is <= 0)
            {
                server.Port = null;
            }

            server.AdminDatabase ??= string.Empty;
            server.AdminUsername ??= string.Empty;
            server.AdminPassword ??= string.Empty;
            server.PostgreSqlSslMode = PostgreSqlSslModes.NormalizeConfigured(server.PostgreSqlSslMode);
            server.SqlServerTrustMode = SqlServerTrustModes.NormalizeConfigured(server.SqlServerTrustMode);
            if (server.ConnectionTimeoutSeconds is <= 0)
            {
                server.ConnectionTimeoutSeconds = null;
            }

            if (server.CommandTimeoutSeconds is <= 0)
            {
                server.CommandTimeoutSeconds = null;
            }

            server.DatabasesPayload ??= string.Empty;
            server.Encrypted = config.EncryptPasswords && server.Encrypted && !string.IsNullOrWhiteSpace(server.AdminPassword);
            server.Databases ??= [];
            server.VersionHistory ??= [];

            foreach (var database in server.Databases)
            {
                database.EntryId = string.IsNullOrWhiteSpace(database.EntryId) ? Guid.NewGuid().ToString("N") : database.EntryId;
                database.DatabaseName ??= string.Empty;
                database.Users ??= [];
                database.VersionHistory ??= [];

                foreach (var user in database.Users)
                {
                    user.EntryId = string.IsNullOrWhiteSpace(user.EntryId) ? Guid.NewGuid().ToString("N") : user.EntryId;
                    user.Username ??= string.Empty;
                    user.Password ??= string.Empty;
                    user.Encrypted = config.EncryptPasswords && user.Encrypted && !string.IsNullOrWhiteSpace(user.Password);
                    user.ConnectionString ??= string.Empty;
                    user.Roles ??= [];
                    user.VersionHistory ??= [];

                    EnsureUserHistory(server, database, user);
                }

                EnsureDatabaseHistory(server, database);
            }

            EnsureServerHistory(server);
        }

        selectedServer ??= ServerConnections.FindBySelectionKey(config.Servers, desiredSelectedServer);
        config.SelectedServerName = selectedServer is not null
            ? ServerConnections.GetIdentifier(selectedServer)
            : config.Servers.Count == 1
                ? ServerConnections.GetIdentifier(config.Servers[0])
                : string.Empty;

        foreach (var trashEntry in config.Trash)
        {
            trashEntry.TrashId = string.IsNullOrWhiteSpace(trashEntry.TrashId) ? Guid.NewGuid().ToString("N") : trashEntry.TrashId;
            trashEntry.EntryId ??= string.Empty;
            trashEntry.EntryType ??= string.Empty;
            trashEntry.DisplayName ??= string.Empty;
            trashEntry.ParentServerName ??= string.Empty;
            trashEntry.ParentDatabaseName ??= string.Empty;
            trashEntry.DeletedAtUtc ??= string.Empty;
            trashEntry.Details ??= string.Empty;
            trashEntry.PayloadJson ??= string.Empty;
            trashEntry.VersionHistory ??= [];
        }

        if (!config.EncryptPasswords)
        {
            config.EncryptionKey = string.Empty;
        }
    }

    private static void EnsureServerHistory(ServerConfig server)
    {
        if (server.VersionHistory.Count > 0)
        {
            return;
        }

        server.VersionHistory.Add(new EntryVersion
        {
            VersionNumber = 1,
            ChangedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Summary = "Imported current state.",
            Details = BuildServerDetails(server)
        });
    }

    private static void EnsureDatabaseHistory(ServerConfig server, DatabaseConfig database)
    {
        if (database.VersionHistory.Count > 0)
        {
            return;
        }

        database.VersionHistory.Add(new EntryVersion
        {
            VersionNumber = 1,
            ChangedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Summary = "Imported current state.",
            Details = BuildDatabaseDetails(server, database)
        });
    }

    private static void EnsureUserHistory(ServerConfig server, DatabaseConfig database, UserConfig user)
    {
        if (user.VersionHistory.Count > 0)
        {
            return;
        }

        user.VersionHistory.Add(new EntryVersion
        {
            VersionNumber = 1,
            ChangedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Summary = "Imported current state.",
            Details = BuildUserDetails(server, database, user)
        });
    }

    private static string BuildServerDetails(ServerConfig server)
        => string.Join(Environment.NewLine,
        [
            $"Connection Identifier: {ServerConnections.GetIdentifier(server)}",
            $"Display Name: {ServerConnections.GetDisplayName(server)}",
            $"Server: {server.ServerName}",
            $"Provider: {SqlProviders.GetDisplayName(server.Provider)}",
            $"Port: {(server.Port?.ToString() ?? "<default>")}",
            $"Admin Database: {server.AdminDatabase}",
            $"Admin User: {(string.IsNullOrWhiteSpace(server.AdminUsername) ? "<none>" : server.AdminUsername)}",
            $"SSL Mode: {PostgreSqlSslModes.GetDisplayName(server.PostgreSqlSslMode)}",
            $"SQL Server TLS Mode: {SqlServerTrustModes.GetDisplayName(server.SqlServerTrustMode)}",
            $"Connection Timeout: {ServerConnectionOptions.GetEffectiveConnectionTimeoutSeconds(server.ConnectionTimeoutSeconds)}",
            $"Command Timeout: {ServerConnectionOptions.GetEffectiveCommandTimeoutSeconds(server.CommandTimeoutSeconds)}",
            $"Pooling: {ServerConnectionOptions.GetEffectivePostgreSqlPooling(server.PostgreSqlPooling)}",
            $"Password State: {(server.Encrypted ? "encrypted" : string.IsNullOrWhiteSpace(server.AdminPassword) ? "missing" : "saved")}",
            $"Tracked Databases: {server.Databases.Count}",
            $"Tracked Users: {server.Databases.Sum(database => database.Users.Count)}"
        ]);

    private static string BuildDatabaseDetails(ServerConfig server, DatabaseConfig database)
        => string.Join(Environment.NewLine,
        [
            $"Server: {server.ServerName}",
            $"Database: {database.DatabaseName}",
            $"Tracked Users: {database.Users.Count}",
            $"Users: {(database.Users.Count == 0 ? "<none>" : string.Join(", ", database.Users.Select(user => user.Username).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)))}"
        ]);

    private static string BuildUserDetails(ServerConfig server, DatabaseConfig database, UserConfig user)
        => string.Join(Environment.NewLine,
        [
            $"Server: {server.ServerName}",
            $"Database: {database.DatabaseName}",
            $"User: {user.Username}",
            $"Password State: {(user.Encrypted ? "encrypted" : string.IsNullOrWhiteSpace(user.Password) ? "missing" : "saved")}",
            $"Roles: {(user.Roles.Count == 0 ? "<none>" : string.Join(", ", user.Roles))}",
            $"Connection String: {user.ConnectionString}"
        ]);

    private sealed class EncryptedConfigEnvelope
    {
        public string ThemeName { get; set; } = TerminalThemeCatalog.DefaultThemeName;
        public bool EncryptPasswords { get; set; }
        public string EncryptionKey { get; set; } = string.Empty;
        public string EncryptedPayload { get; set; } = string.Empty;
    }
}
