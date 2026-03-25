using System.Text.Json;

namespace SqlManager.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public async Task LoadAsync_NormalizesProviderAliasAndPort()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"sql-manager-config-{Guid.NewGuid():N}.json");

        try
        {
            var json = """
            {
              "selectedServerName": "pg01",
              "servers": [
                {
                  "serverName": "pg01",
                  "provider": "pgsql",
                  "port": 0,
                  "adminDatabase": "",
                  "adminUsername": "postgres",
                  "adminPassword": "secret",
                  "databases": []
                }
              ]
            }
            """;

            await File.WriteAllTextAsync(filePath, json);

            var store = new ConfigStore();
            var config = await store.LoadAsync(filePath, CancellationToken.None);

            var server = Assert.Single(config.Servers);
            Assert.Equal(SqlProviders.PostgreSql, server.Provider);
            Assert.Null(server.Port);
            Assert.Equal(string.Empty, server.AdminDatabase);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task LoadAsync_LegacyConfigDefaultsToSqlServer()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"sql-manager-legacy-{Guid.NewGuid():N}.json");

        try
        {
            var json = """
            {
              "sqlAdmin": {
                "username": "sa",
                "serverName": "sql01.contoso.local"
              },
              "databases": [
                {
                  "serverName": "sql01.contoso.local",
                  "databaseName": "LabDb",
                  "users": []
                }
              ]
            }
            """;

            await File.WriteAllTextAsync(filePath, json);

            var store = new ConfigStore();
            var config = await store.LoadAsync(filePath, CancellationToken.None);

            var server = Assert.Single(config.Servers);
            Assert.Equal(SqlProviders.SqlServer, server.Provider);
            Assert.Equal("sql01.contoso.local", server.ServerName);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task SaveAsync_WritesProviderFields()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"sql-manager-save-{Guid.NewGuid():N}.json");

        try
        {
            var config = new SqlManagerConfig
            {
                SelectedServerName = "pg01",
                Servers =
                [
                    new ServerConfig
                    {
                        ServerName = "pg01",
                        Provider = SqlProviders.PostgreSql,
                        Port = 5432,
                        AdminDatabase = "postgres",
                        AdminUsername = "postgres",
                        AdminPassword = "secret"
                    }
                ]
            };

            var store = new ConfigStore();
            await store.SaveAsync(filePath, config, CancellationToken.None);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(filePath));
            var server = document.RootElement.GetProperty("servers")[0];
            Assert.Equal("postgresql", server.GetProperty("provider").GetString());
            Assert.Equal(5432, server.GetProperty("port").GetInt32());
            Assert.Equal("postgres", server.GetProperty("adminDatabase").GetString());
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}