namespace SqlManager.Tests;

public sealed class ConfigEncryptionTests
{
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

            var stored = await store.LoadAsync(filePath, CancellationToken.None);
            Assert.True(stored.EncryptPasswords);
            Assert.False(string.IsNullOrWhiteSpace(stored.EncryptionKey));

            var storedServer = Assert.Single(stored.Servers);
            Assert.True(storedServer.Encrypted);
            Assert.NotEqual("ServerAdminSecret123!", storedServer.AdminPassword);

            var storedUser = Assert.Single(Assert.Single(storedServer.Databases).Users);
            Assert.True(storedUser.Encrypted);
            Assert.NotEqual("reader-password", storedUser.Password);
            Assert.Contains("Password=********", storedUser.ConnectionString, StringComparison.Ordinal);

            var lockedSummary = await service.LoadConfigSummaryAsync(filePath, CancellationToken.None);
            Assert.True(lockedSummary.Succeeded);
            var lockedServer = Assert.Single(lockedSummary.Value!.Servers);
            Assert.Equal(string.Empty, lockedServer.AdminPassword);
            Assert.True(lockedServer.Encrypted);
            Assert.Equal(string.Empty, Assert.Single(Assert.Single(lockedServer.Databases).Users).Password);

            var unlockedSummary = await service.LoadConfigSummaryAsync(filePath, "ComplexPass!123", CancellationToken.None);
            Assert.True(unlockedSummary.Succeeded);
            var unlockedServer = Assert.Single(unlockedSummary.Value!.Servers);
            Assert.Equal("ServerAdminSecret123!", unlockedServer.AdminPassword);
            Assert.True(unlockedServer.Encrypted);

            var unlockedUser = Assert.Single(Assert.Single(unlockedServer.Databases).Users);
            Assert.Equal("reader-password", unlockedUser.Password);
            Assert.True(unlockedUser.Encrypted);
            Assert.Contains("Password=********", unlockedUser.ConnectionString, StringComparison.Ordinal);
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

            var stored = await store.LoadAsync(filePath, CancellationToken.None);
            Assert.False(stored.EncryptPasswords);
            Assert.Equal(string.Empty, stored.EncryptionKey);

            var server = Assert.Single(stored.Servers);
            Assert.False(server.Encrypted);
            Assert.Equal("ServerAdminSecret123!", server.AdminPassword);

            var user = Assert.Single(Assert.Single(server.Databases).Users);
            Assert.False(user.Encrypted);
            Assert.Equal("reader-password", user.Password);
            Assert.Contains("Password=********", user.ConnectionString, StringComparison.Ordinal);
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
                "sql01.contoso.local",
                "pg01.contoso.local",
                SqlProviders.PostgreSql,
                5432,
                "postgres",
                "postgres",
                "PostgresAdminSecret123!",
                null,
                CancellationToken.None);

            Assert.True(updateResult.Succeeded);

            var stored = await store.LoadAsync(filePath, CancellationToken.None);
            Assert.Equal("pg01.contoso.local", stored.SelectedServerName);

            var server = Assert.Single(stored.Servers);
            Assert.Equal("pg01.contoso.local", server.ServerName);
            Assert.Equal(SqlProviders.PostgreSql, server.Provider);
            Assert.Equal(5432, server.Port);
            Assert.Equal("postgres", server.AdminDatabase);
            Assert.Equal("postgres", server.AdminUsername);
            Assert.Equal("PostgresAdminSecret123!", server.AdminPassword);

            var user = Assert.Single(Assert.Single(server.Databases).Users);
            Assert.Contains("Host=pg01.contoso.local;Port=5432;Database=LabDB;Username=lab_reader;Password=********;Ssl Mode=Require;", user.ConnectionString, StringComparison.Ordinal);
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
            SelectedServerName = "sql01.contoso.local",
            Servers =
            [
                new ServerConfig
                {
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
                                    ConnectionString = "Server=sql01.contoso.local;Database=LabDB;User ID=lab_reader;Password=********;Encrypt=True;TrustServerCertificate=True;"
                                }
                            ]
                        }
                    ]
                }
            ]
        };
}