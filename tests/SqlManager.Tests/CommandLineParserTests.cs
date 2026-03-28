namespace SqlManager.Tests;

public sealed class CommandLineParserTests
{
    [Fact]
    public void Parse_ReadsProviderPortAndAdminDatabaseOptions()
    {
        var result = CommandLineParser.Parse(
            [
                "add-server",
                "--server-name", "pg01.contoso.local",
                "--provider", "postgresql",
                "--port", "5432",
                "--admin-database", "postgres",
                "--admin-username", "postgres"
            ],
            "sql-config.json");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value);
        Assert.Equal(CommandKind.AddServer, result.Value!.Command);
        Assert.Equal("pg01.contoso.local", result.Value.Options.ServerName);
        Assert.Equal(SqlProviders.PostgreSql, result.Value.Options.Provider);
        Assert.Equal(5432, result.Value.Options.Port);
        Assert.Equal("postgres", result.Value.Options.AdminDatabase);
        Assert.Equal("postgres", result.Value.Options.AdminUsername);
    }

    [Fact]
    public void Parse_NormalizesProviderAliases()
    {
        var result = CommandLineParser.Parse(
            ["add-server", "--server-name", "sql01", "--provider", "mssql"],
            "sql-config.json");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value);
        Assert.Equal(SqlProviders.SqlServer, result.Value!.Options.Provider);
    }

    [Fact]
    public void Parse_FailsForInvalidPort()
    {
        var result = CommandLineParser.Parse(
            ["add-server", "--server-name", "pg01", "--provider", "postgresql", "--port", "0"],
            "sql-config.json");

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Port must be a positive integer.", result.Message);
    }

    [Fact]
    public void Parse_ReadsEnableConfigEncryptionCommandAndPassword()
    {
        var result = CommandLineParser.Parse(
            ["enable-config-encryption", "--config-path", "sql-config.json", "--encryption-password", "ComplexPass!123"],
            "fallback.json");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value);
        Assert.Equal(CommandKind.EnableConfigEncryption, result.Value!.Command);
        Assert.Equal("ComplexPass!123", result.Value.Options.EncryptionPassword);
        Assert.Equal("sql-config.json", result.Value.Options.ConfigPath);
    }

    [Fact]
    public void Parse_ReadsDisableConfigEncryptionAlias()
    {
        var result = CommandLineParser.Parse(
            ["decrypt-config"],
            "sql-config.json");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value);
        Assert.Equal(CommandKind.DisableConfigEncryption, result.Value!.Command);
    }

    [Fact]
    public void Parse_ReadsMigrateConfigEncryptionFormatCommandAndPassword()
    {
        var result = CommandLineParser.Parse(
            ["migrate-config-encryption-format", "--config-path", "sql-config.json", "--encryption-password", "ComplexPass!123"],
            "fallback.json");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value);
        Assert.Equal(CommandKind.MigrateConfigEncryptionFormat, result.Value!.Command);
        Assert.Equal("ComplexPass!123", result.Value.Options.EncryptionPassword);
        Assert.Equal("sql-config.json", result.Value.Options.ConfigPath);
    }

    [Fact]
    public void Parse_ReadsTestUserLoginCommandAndUserPasswordAlias()
    {
        var result = CommandLineParser.Parse(
            ["test-user-login", "--database-name", "securejournal", "--user-name", "securejournal", "--user-password", "Secret123!"],
            "sql-config.json");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value);
        Assert.Equal(CommandKind.TestUserLogin, result.Value!.Command);
        Assert.Equal("securejournal", result.Value.Options.DatabaseName);
        Assert.Equal("securejournal", result.Value.Options.UserName);
        Assert.Equal("Secret123!", result.Value.Options.NewUserPassword);
    }
}
