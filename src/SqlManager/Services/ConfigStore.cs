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

        var config = root["servers"] is not null
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

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config, SerializerOptions), cancellationToken);
    }

    private static SqlManagerConfig ParseCurrent(JsonNode root)
    {
        var config = new SqlManagerConfig
        {
            SelectedServerName = root["selectedServerName"]?.GetValue<string>() ?? string.Empty,
            Timeouts = new SqlTimeoutConfig
            {
                ConnectionTimeoutSeconds = root["timeouts"]?["connectionTimeoutSeconds"]?.GetValue<int>() ?? SqlTimeoutConfig.DefaultConnectionTimeoutSeconds,
                CommandTimeoutSeconds = root["timeouts"]?["commandTimeoutSeconds"]?.GetValue<int>() ?? SqlTimeoutConfig.DefaultCommandTimeoutSeconds
            }
        };

        foreach (var serverNode in root["servers"]?.AsArray() ?? [])
        {
            if (serverNode is null)
            {
                continue;
            }

            var server = new ServerConfig
            {
                ServerName = serverNode["serverName"]?.GetValue<string>() ?? string.Empty,
                Provider = serverNode["provider"]?.GetValue<string>() ?? SqlProviders.SqlServer,
                Port = serverNode["port"]?.GetValue<int?>(),
                AdminDatabase = serverNode["adminDatabase"]?.GetValue<string>() ?? string.Empty,
                AdminUsername = serverNode["adminUsername"]?.GetValue<string>() ?? string.Empty,
                AdminPassword = serverNode["adminPassword"]?.GetValue<string>() ?? string.Empty
            };

            foreach (var databaseNode in serverNode["databases"]?.AsArray() ?? [])
            {
                if (databaseNode is null)
                {
                    continue;
                }

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

    private static void Normalize(SqlManagerConfig config)
    {
        config.SelectedServerName ??= string.Empty;
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

        foreach (var server in config.Servers)
        {
            server.ServerName ??= string.Empty;
            server.Provider = SqlProviders.Normalize(server.Provider);
            if (server.Port is <= 0)
            {
                server.Port = null;
            }

            server.AdminDatabase ??= string.Empty;
            server.AdminUsername ??= string.Empty;
            server.AdminPassword ??= string.Empty;
            server.Databases ??= [];

            foreach (var database in server.Databases)
            {
                database.DatabaseName ??= string.Empty;
                database.Users ??= [];

                foreach (var user in database.Users)
                {
                    user.Username ??= string.Empty;
                    user.Password ??= string.Empty;
                    user.ConnectionString ??= string.Empty;
                    user.Roles ??= [];
                }
            }
        }
    }
}
