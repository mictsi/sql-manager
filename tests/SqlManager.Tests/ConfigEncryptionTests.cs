namespace SqlManager.Tests;

public sealed class ConfigEncryptionTests
{
        [Fact]
        public async Task ConfigStore_LoadAsync_AddsEntryIdsAndBaselineVersionHistory()
        {
                var filePath = Path.Combine(Path.GetTempPath(), $"sql-manager-history-normalize-{Guid.NewGuid():N}.json");

                try
                {
                        await File.WriteAllTextAsync(filePath,
                                """
                                {
                                    "selectedServerName": "sql01.contoso.local",
                                    "servers": [
                                        {
                                            "serverName": "sql01.contoso.local",
                                            "provider": "sqlserver",
                                            "adminDatabase": "master",
                                            "adminUsername": "sa",
                                            "adminPassword": "ServerAdminSecret123!",
                                            "databases": [
                                                {
                                                    "databaseName": "LabDB",
                                                    "users": [
                                                        {
                                                            "username": "lab_reader",
                                                            "password": "reader-password",
                                                            "roles": ["db_datareader"],
                                                            "connectionString": "Server=tcp:sql01.contoso.local,1433;Database=LabDB;Persist Security Info=False;User ID=lab_reader;Password=;Encrypt=True;TrustServerCertificate=True;Connection Timeout=30;"
                                                        }
                                                    ]
                                                }
                                            ]
                                        }
                                    ]
                                }
                                """,
                                CancellationToken.None);

                        var config = await new ConfigStore().LoadAsync(filePath, CancellationToken.None);

                        var server = Assert.Single(config.Servers);
                        var database = Assert.Single(server.Databases);
                        var user = Assert.Single(database.Users);

                        Assert.False(string.IsNullOrWhiteSpace(server.EntryId));
                        Assert.Equal("1", server.ServerIdentifier);
                        Assert.Equal("sql01.contoso.local", server.DisplayName);
                        Assert.False(string.IsNullOrWhiteSpace(database.EntryId));
                        Assert.False(string.IsNullOrWhiteSpace(user.EntryId));

                        var serverVersion = Assert.Single(server.VersionHistory);
                        var databaseVersion = Assert.Single(database.VersionHistory);
                        var userVersion = Assert.Single(user.VersionHistory);

                        Assert.Equal(1, serverVersion.VersionNumber);
                        Assert.Equal(1, databaseVersion.VersionNumber);
                        Assert.Equal(1, userVersion.VersionNumber);
                        Assert.Equal("Imported current state.", serverVersion.Summary);
                        Assert.Equal("Imported current state.", databaseVersion.Summary);
                        Assert.Equal("Imported current state.", userVersion.Summary);
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
    public void ConfigPasswordProtector_RoundTripsEncryptedSecret()
    {
        var protector = new ConfigPasswordProtector();
        const string password = "ComplexPass!123";
        const string secret = "ServerAdminSecret123!";

        var encryptionKey = protector.CreateEncryptionKey(password);
        var ciphertext = protector.EncryptSecret(secret, password);

        Assert.StartsWith("smkey:v2:", encryptionKey, StringComparison.Ordinal);
        Assert.StartsWith("smenc:v2:", ciphertext, StringComparison.Ordinal);
        Assert.True(protector.VerifyUnlockPassword(password, encryptionKey));
        Assert.NotEqual(secret, ciphertext);
        Assert.Equal(secret, protector.DecryptSecret(ciphertext, password));
    }

    [Fact]
    public async Task ConfigurePasswordEncryptionAsync_EncryptsStoredSecretsAndSupportsLockedAndUnlockedViews()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"sql-manager-encryption-{Guid.NewGuid():N}.json");

        try
        {
            var store = new ConfigStore();
            await store.SaveAsync(filePath, CreatePlaintextConfig(), CancellationToken.None);

            var service = CreateService();
            var enableResult = await service.ConfigurePasswordEncryptionAsync(filePath, true, "ComplexPass!123", CancellationToken.None);

            Assert.True(enableResult.Succeeded);

            var fileContent = await File.ReadAllTextAsync(filePath, CancellationToken.None);
            Assert.Contains("\"encryptedPayload\"", fileContent, StringComparison.Ordinal);
            Assert.DoesNotContain("sql01.contoso.local", fileContent, StringComparison.Ordinal);
            Assert.DoesNotContain("LabDB", fileContent, StringComparison.Ordinal);
            Assert.DoesNotContain("lab_reader", fileContent, StringComparison.Ordinal);
            Assert.DoesNotContain("ServerAdminSecret123!", fileContent, StringComparison.Ordinal);
            Assert.DoesNotContain("reader-password", fileContent, StringComparison.Ordinal);

            var stored = await store.LoadAsync(filePath, CancellationToken.None);
            Assert.True(stored.EncryptPasswords);
            Assert.False(string.IsNullOrWhiteSpace(stored.EncryptionKey));
            Assert.False(string.IsNullOrWhiteSpace(stored.EncryptedPayload));
            Assert.Empty(stored.Servers);

            var lockedSummary = await service.LoadConfigSummaryAsync(filePath, CancellationToken.None);
            Assert.True(lockedSummary.Succeeded);
            Assert.NotNull(lockedSummary.Value);
            Assert.True(lockedSummary.Value!.EncryptPasswords);
            Assert.Empty(lockedSummary.Value.Servers);

            var unlockedSummary = await service.LoadConfigSummaryAsync(filePath, "ComplexPass!123", CancellationToken.None);
            Assert.True(unlockedSummary.Succeeded);
            var unlockedServer = Assert.Single(unlockedSummary.Value!.Servers);
            Assert.Equal("ServerAdminSecret123!", unlockedServer.AdminPassword);
            Assert.True(unlockedServer.Encrypted);
            Assert.Equal(string.Empty, unlockedSummary.Value.EncryptedPayload);

            var unlockedUser = Assert.Single(Assert.Single(unlockedServer.Databases).Users);
            Assert.Equal("reader-password", unlockedUser.Password);
            Assert.True(unlockedUser.Encrypted);
            Assert.Contains("Password=********;", unlockedUser.ConnectionString, StringComparison.Ordinal);
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
    public async Task ConfigurePasswordEncryptionAsync_DisablesEncryptionAndRestoresPlaintextSecrets()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"sql-manager-encryption-disable-{Guid.NewGuid():N}.json");

        try
        {
            var store = new ConfigStore();
            await store.SaveAsync(filePath, CreatePlaintextConfig(), CancellationToken.None);

            var service = CreateService();
            await service.ConfigurePasswordEncryptionAsync(filePath, true, "ComplexPass!123", CancellationToken.None);

            var disableResult = await service.ConfigurePasswordEncryptionAsync(filePath, false, "ComplexPass!123", CancellationToken.None);
            Assert.True(disableResult.Succeeded);

            var fileContent = await File.ReadAllTextAsync(filePath, CancellationToken.None);
            Assert.DoesNotContain("\"encryptedPayload\"", fileContent, StringComparison.Ordinal);
            Assert.Contains("sql01.contoso.local", fileContent, StringComparison.Ordinal);
            Assert.Contains("LabDB", fileContent, StringComparison.Ordinal);

            var stored = await store.LoadAsync(filePath, CancellationToken.None);
            Assert.False(stored.EncryptPasswords);
            Assert.Equal(string.Empty, stored.EncryptionKey);
            Assert.Equal(string.Empty, stored.EncryptedPayload);

            var server = Assert.Single(stored.Servers);
            Assert.False(server.Encrypted);
            Assert.Equal("ServerAdminSecret123!", server.AdminPassword);
            Assert.Equal(string.Empty, server.DatabasesPayload);

            var user = Assert.Single(Assert.Single(server.Databases).Users);
            Assert.False(user.Encrypted);
            Assert.Equal("reader-password", user.Password);
            Assert.Contains("Password=********;", user.ConnectionString, StringComparison.Ordinal);
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
        public async Task ConfigurePasswordEncryptionAsync_RewritesOldEncryptedFormatToFullEnvelope()
        {
                var filePath = Path.Combine(Path.GetTempPath(), $"sql-manager-encryption-migrate-{Guid.NewGuid():N}.json");

                try
                {
                        var protector = new ConfigPasswordProtector();
                        const string encryptionPassword = "ComplexPass!123";
                        var encryptionKey = protector.CreateEncryptionKey(encryptionPassword);

                        await File.WriteAllTextAsync(filePath,
                                $$"""
                                {
                                    "selectedServerName": "sql01.contoso.local",
                                    "encryptPasswords": true,
                                    "encryptionKey": "{{encryptionKey}}",
                                    "timeouts": {
                                        "connectionTimeoutSeconds": 15,
                                        "commandTimeoutSeconds": 30
                                    },
                                    "servers": [
                                        {
                                            "serverName": "sql01.contoso.local",
                                            "provider": "sqlserver",
                                            "adminDatabase": "master",
                                            "adminUsername": "sa",
                                            "adminPassword": "{{protector.EncryptSecret("ServerAdminSecret123!", encryptionPassword)}}",
                                            "encrypted": true,
                                            "databases": [
                                                {
                                                    "databaseName": "LabDB",
                                                    "users": [
                                                        {
                                                            "username": "lab_reader",
                                                            "password": "{{protector.EncryptSecret("reader-password", encryptionPassword)}}",
                                                            "encrypted": true,
                                                            "roles": ["db_datareader"],
                                                            "connectionString": "Server=tcp:sql01.contoso.local,1433;Database=LabDB;Persist Security Info=False;User ID=lab_reader;Password=;Encrypt=True;TrustServerCertificate=True;Connection Timeout=30;"
                                                        }
                                                    ]
                                                }
                                            ]
                                        }
                                    ]
                                }
                                """,
                                CancellationToken.None);

                        var service = CreateService();
                        var migrateResult = await service.ConfigurePasswordEncryptionAsync(filePath, true, encryptionPassword, CancellationToken.None);

                        Assert.True(migrateResult.Succeeded);

                        var fileContent = await File.ReadAllTextAsync(filePath, CancellationToken.None);
                        Assert.Contains("\"encryptedPayload\"", fileContent, StringComparison.Ordinal);
                        Assert.DoesNotContain("sql01.contoso.local", fileContent, StringComparison.Ordinal);
                        Assert.DoesNotContain("LabDB", fileContent, StringComparison.Ordinal);
                        Assert.DoesNotContain("lab_reader", fileContent, StringComparison.Ordinal);

                        var unlockedSummary = await service.LoadConfigSummaryAsync(filePath, encryptionPassword, CancellationToken.None);
                        Assert.True(unlockedSummary.Succeeded);
                        Assert.Equal("sql01.contoso.local", Assert.Single(unlockedSummary.Value!.Servers).ServerName);
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
    public async Task UpdateServerAsync_RenamesServerAndRefreshesConnectionStrings()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"sql-manager-update-server-{Guid.NewGuid():N}.json");

        try
        {
            var store = new ConfigStore();
            await store.SaveAsync(filePath, CreatePlaintextConfig(), CancellationToken.None);

            var service = CreateService();
            var updateResult = await service.UpdateServerAsync(
                filePath,
                "1",
                "Postgres Admin",
                "pg01.contoso.local",
                SqlProviders.PostgreSql,
                5432,
                "postgres",
                "postgres",
                "PostgresAdminSecret123!",
                PostgreSqlSslModes.Prefer,
                true,
                null,
                15,
                30,
                null,
                CancellationToken.None);

            Assert.True(updateResult.Succeeded);

            var stored = await store.LoadAsync(filePath, CancellationToken.None);
            Assert.Equal("1", stored.SelectedServerName);

            var server = Assert.Single(stored.Servers);
            Assert.Equal("1", server.ServerIdentifier);
            Assert.Equal("Postgres Admin", server.DisplayName);
            Assert.Equal("pg01.contoso.local", server.ServerName);
            Assert.Equal(SqlProviders.PostgreSql, server.Provider);
            Assert.Equal(5432, server.Port);
            Assert.Equal("postgres", server.AdminDatabase);
            Assert.Equal("postgres", server.AdminUsername);
            Assert.Equal("PostgresAdminSecret123!", server.AdminPassword);
            Assert.Equal(PostgreSqlSslModes.Prefer, server.PostgreSqlSslMode);
            Assert.True(server.PostgreSqlPooling);
            Assert.Equal(15, server.ConnectionTimeoutSeconds);
            Assert.Equal(30, server.CommandTimeoutSeconds);

            var user = Assert.Single(Assert.Single(server.Databases).Users);
            Assert.Equal(
                "Host=pg01.contoso.local;Database=LabDB;Username=lab_reader;Password=********;Ssl Mode=Prefer;Port=5432;Timeout=15;Command Timeout=30;Pooling=true;",
                user.ConnectionString);
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
    public async Task AddServerAsync_AllowsSameHostWhenAllocatingANewConnectionId()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"sql-manager-duplicate-add-{Guid.NewGuid():N}.json");

        try
        {
            var store = new ConfigStore();
            await store.SaveAsync(filePath, CreatePlaintextConfig(), CancellationToken.None);

            var service = CreateService();
            var addResult = await service.AddServerAsync(new CommandOptions
            {
                Command = CommandKind.AddServer,
                ConfigPath = filePath,
                DisplayName = "Duplicate SQL",
                ServerName = "sql01.contoso.local",
                Provider = SqlProviders.PostgreSql,
                Port = 5432,
                AdminDatabase = "postgres",
                AdminUsername = "postgres",
                AdminPassword = "PostgresAdminSecret123!"
            }, CancellationToken.None);

            Assert.True(addResult.Succeeded);

            var stored = await store.LoadAsync(filePath, CancellationToken.None);
            Assert.Equal(2, stored.Servers.Count);
            Assert.Contains(stored.Servers, server => server.ServerIdentifier == "1" && server.Provider == SqlProviders.SqlServer);
            Assert.Contains(stored.Servers, server => server.ServerIdentifier == "2" && server.Provider == SqlProviders.PostgreSql);
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
    public async Task AddServerAsync_AllowsSameHostWithDifferentConnectionIdentifiers()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"sql-manager-duplicate-host-{Guid.NewGuid():N}.json");

        try
        {
            var store = new ConfigStore();
            await store.SaveAsync(filePath, CreatePlaintextConfig(), CancellationToken.None);

            var service = CreateService();
            var addResult = await service.AddServerAsync(new CommandOptions
            {
                Command = CommandKind.AddServer,
                ConfigPath = filePath,
                DisplayName = "SQL Reporting",
                ServerName = "sql01.contoso.local",
                Provider = SqlProviders.SqlServer,
                Port = 1433,
                AdminDatabase = "master",
                AdminUsername = "readonly_admin",
                AdminPassword = "AnotherSecret123!"
            }, CancellationToken.None);

            Assert.True(addResult.Succeeded);

            var stored = await store.LoadAsync(filePath, CancellationToken.None);
            Assert.Equal(2, stored.Servers.Count);
            Assert.Contains(stored.Servers, server => server.ServerIdentifier == "1");
            Assert.Contains(stored.Servers, server => server.ServerIdentifier == "2" && server.ServerName == "sql01.contoso.local");
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
    public async Task AddServerAsync_IgnoresSuppliedIdentifierAndAllocatesNextNumericId()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"sql-manager-auto-id-{Guid.NewGuid():N}.json");

        try
        {
            var store = new ConfigStore();
            await store.SaveAsync(filePath, CreatePlaintextConfig(), CancellationToken.None);

            var service = CreateService();
            var addResult = await service.AddServerAsync(new CommandOptions
            {
                Command = CommandKind.AddServer,
                ConfigPath = filePath,
                ServerIdentifier = "999",
                DisplayName = "SQL Secondary",
                ServerName = "sql02.contoso.local",
                Provider = SqlProviders.SqlServer,
                Port = 1433,
                AdminDatabase = "master",
                AdminUsername = "sa",
                AdminPassword = "AnotherSecret123!"
            }, CancellationToken.None);

            Assert.True(addResult.Succeeded);

            var stored = await store.LoadAsync(filePath, CancellationToken.None);
            Assert.Contains(stored.Servers, server => server.ServerName == "sql02.contoso.local" && server.ServerIdentifier == "2");
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
        public async Task MigrateEncryptedConfigFormatAsync_UpgradesOldEncryptedConfigWithoutToggle()
        {
                var filePath = Path.Combine(Path.GetTempPath(), $"sql-manager-explicit-migrate-{Guid.NewGuid():N}.json");

                try
                {
                        var protector = new ConfigPasswordProtector();
                        const string encryptionPassword = "ComplexPass!123";
                        var encryptionKey = protector.CreateEncryptionKey(encryptionPassword);

                        await File.WriteAllTextAsync(filePath,
                                $$"""
                                {
                                    "selectedServerName": "sql01.contoso.local",
                                    "encryptPasswords": true,
                                    "encryptionKey": "{{encryptionKey}}",
                                    "timeouts": {
                                        "connectionTimeoutSeconds": 15,
                                        "commandTimeoutSeconds": 30
                                    },
                                    "servers": [
                                        {
                                            "serverName": "sql01.contoso.local",
                                            "provider": "sqlserver",
                                            "adminDatabase": "master",
                                            "adminUsername": "sa",
                                            "adminPassword": "{{protector.EncryptSecret("ServerAdminSecret123!", encryptionPassword)}}",
                                            "encrypted": true,
                                            "databases": [
                                                {
                                                    "databaseName": "LabDB",
                                                    "users": [
                                                        {
                                                            "username": "lab_reader",
                                                            "password": "{{protector.EncryptSecret("reader-password", encryptionPassword)}}",
                                                            "encrypted": true,
                                                            "roles": ["db_datareader"],
                                                            "connectionString": "Server=tcp:sql01.contoso.local,1433;Database=LabDB;Persist Security Info=False;User ID=lab_reader;Password=;Encrypt=True;TrustServerCertificate=True;Connection Timeout=30;"
                                                        }
                                                    ]
                                                }
                                            ]
                                        }
                                    ]
                                }
                                """,
                                CancellationToken.None);

                        var service = CreateService();
                        var migrateResult = await service.MigrateEncryptedConfigFormatAsync(filePath, encryptionPassword, CancellationToken.None);

                        Assert.True(migrateResult.Succeeded);

                        var fileContent = await File.ReadAllTextAsync(filePath, CancellationToken.None);
                        Assert.Contains("\"encryptedPayload\"", fileContent, StringComparison.Ordinal);
                        Assert.DoesNotContain("sql01.contoso.local", fileContent, StringComparison.Ordinal);
                        Assert.DoesNotContain("LabDB", fileContent, StringComparison.Ordinal);
                        Assert.DoesNotContain("lab_reader", fileContent, StringComparison.Ordinal);
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
    public async Task RestoreTrashItemAsync_RestoresDatabaseAndPreservesVersionHistory()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"sql-manager-trash-restore-{Guid.NewGuid():N}.json");

        try
        {
            var store = new ConfigStore();
            await store.SaveAsync(filePath, new SqlManagerConfig
            {
                SelectedServerName = "sql01.contoso.local",
                Servers =
                [
                    new ServerConfig
                    {
                        EntryId = "server-1",
                        ServerName = "sql01.contoso.local",
                        Provider = SqlProviders.SqlServer,
                        AdminDatabase = "master",
                        AdminUsername = "sa",
                        AdminPassword = "ServerAdminSecret123!",
                        VersionHistory =
                        [
                            new EntryVersion
                            {
                                VersionNumber = 1,
                                ChangedAtUtc = "2025-01-01T00:00:00.0000000Z",
                                Summary = "Imported current state.",
                                Details = "Server baseline"
                            }
                        ]
                    }
                ],
                Trash =
                [
                    new TrashEntry
                    {
                        TrashId = "trash-db-1",
                        EntryType = "database",
                        EntryId = "database-1",
                        DisplayName = "LabDB",
                        ParentServerName = "sql01.contoso.local",
                        ParentDatabaseName = string.Empty,
                        DeletedAtUtc = "2025-01-02T00:00:00.0000000Z",
                        Details = "Database deleted from config",
                        PayloadJson = System.Text.Json.JsonSerializer.Serialize(new DatabaseConfig
                        {
                            EntryId = "database-1",
                            DatabaseName = "LabDB",
                            Users =
                            [
                                new UserConfig
                                {
                                    EntryId = "user-1",
                                    Username = "lab_reader",
                                    Password = "reader-password",
                                    Roles = ["db_datareader"],
                                    ConnectionString = "Server=tcp:sql01.contoso.local,1433;Database=LabDB;Persist Security Info=False;User ID=lab_reader;Password=;Encrypt=True;TrustServerCertificate=True;Connection Timeout=30;",
                                    VersionHistory =
                                    [
                                        new EntryVersion
                                        {
                                            VersionNumber = 1,
                                            ChangedAtUtc = "2025-01-01T00:00:00.0000000Z",
                                            Summary = "Imported current state.",
                                            Details = "User baseline"
                                        }
                                    ]
                                }
                            ],
                            VersionHistory =
                            [
                                new EntryVersion
                                {
                                    VersionNumber = 1,
                                    ChangedAtUtc = "2025-01-01T00:00:00.0000000Z",
                                    Summary = "Imported current state.",
                                    Details = "Database baseline"
                                }
                            ]
                        }),
                        VersionHistory =
                        [
                            new EntryVersion
                            {
                                VersionNumber = 1,
                                ChangedAtUtc = "2025-01-01T00:00:00.0000000Z",
                                Summary = "Imported current state.",
                                Details = "Database baseline"
                            }
                        ]
                    }
                ]
            }, CancellationToken.None);

            var service = CreateService();
            var restoreResult = await service.RestoreTrashItemAsync(filePath, "trash-db-1", null, CancellationToken.None);

            Assert.True(restoreResult.Succeeded);

            var stored = await store.LoadAsync(filePath, CancellationToken.None);
            var server = Assert.Single(stored.Servers);
            var database = Assert.Single(server.Databases);
            var user = Assert.Single(database.Users);

            Assert.Equal("LabDB", database.DatabaseName);
            Assert.Equal("lab_reader", user.Username);
            Assert.Empty(stored.Trash);
            Assert.Equal(2, database.VersionHistory.Count);
            Assert.Equal("Recovered from trash.", database.VersionHistory[^1].Summary);
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
    public async Task TestUserLoginAsync_FailsWhenNoPasswordIsStoredOrSupplied()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"sql-manager-test-login-{Guid.NewGuid():N}.json");

        try
        {
            var config = CreatePlaintextConfig();
            config.Servers[0].Databases[0].Users[0].Password = string.Empty;

            var store = new ConfigStore();
            await store.SaveAsync(filePath, config, CancellationToken.None);

            var service = CreateService();
            var result = await service.TestUserLoginAsync(new CommandOptions
            {
                Command = CommandKind.TestUserLogin,
                ConfigPath = filePath,
                ServerName = "sql01.contoso.local",
                DatabaseName = "LabDB",
                UserName = "lab_reader"
            }, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("No password is stored for this user. Supply --user-password to test the login.", result.Message);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    private static SqlManagerService CreateService()
        => new(new ConfigStore(), new ConfigPasswordProtector(), new PasswordGenerator(), new SqlServerGateway(), new PostgreSqlGateway());

    private static SqlManagerConfig CreatePlaintextConfig()
        => new()
        {
            SelectedServerName = "1",
            Servers =
            [
                new ServerConfig
                {
                    ServerIdentifier = "1",
                    DisplayName = "Primary SQL",
                    ServerName = "sql01.contoso.local",
                    Provider = SqlProviders.SqlServer,
                    AdminDatabase = "master",
                    AdminUsername = "sa",
                    AdminPassword = "ServerAdminSecret123!",
                    Databases =
                    [
                        new DatabaseConfig
                        {
                            DatabaseName = "LabDB",
                            Users =
                            [
                                new UserConfig
                                {
                                    Username = "lab_reader",
                                    Password = "reader-password",
                                    Roles = ["db_datareader"],
                                    ConnectionString = "Server=tcp:sql01.contoso.local,1433;Database=LabDB;Persist Security Info=False;User ID=lab_reader;Password=;Encrypt=True;TrustServerCertificate=True;Connection Timeout=30;"
                                }
                            ]
                        }
                    ]
                }
            ]
        };
}
