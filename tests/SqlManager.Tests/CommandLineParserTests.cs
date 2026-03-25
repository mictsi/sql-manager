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
}