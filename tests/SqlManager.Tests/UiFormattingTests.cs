using System.Reflection;

namespace SqlManager.Tests;

public sealed class UiFormattingTests
{
    [Fact]
    public void BuildServerChoiceLabel_IncludesActiveMarkerProviderAndAdmin()
    {
        var server = new ServerConfig
        {
            ServerName = "pg01.contoso.local",
            Provider = SqlProviders.PostgreSql,
            AdminUsername = "postgres"
        };

        var label = InvokePrivateStatic<string>(
            typeof(SqlManagerApplication),
            "BuildServerChoiceLabel",
            server,
            "PG01.CONTOSO.LOCAL");

        Assert.Equal("* pg01.contoso.local | provider: PostgreSQL | admin: postgres", label);
    }

    [Fact]
    public void BuildServerChoiceLabel_UsesNoneForMissingAdmin()
    {
        var server = new ServerConfig
        {
            ServerName = "sql01.contoso.local",
            Provider = SqlProviders.SqlServer,
            AdminUsername = string.Empty
        };

        var label = InvokePrivateStatic<string>(
            typeof(SqlManagerApplication),
            "BuildServerChoiceLabel",
            server,
            "another-server");

        Assert.Equal("sql01.contoso.local | provider: SQL Server | admin: <none>", label);
    }

    [Fact]
    public void BuildServerLine_IncludesProviderPasswordStateDatabaseAndUserCounts()
    {
        var server = new ServerConfig
        {
            ServerName = "pg01.contoso.local",
            Provider = SqlProviders.PostgreSql,
            AdminUsername = "postgres",
            AdminPassword = "secret",
            Databases =
            [
                new DatabaseConfig
                {
                    DatabaseName = "appdb",
                    Users = [ new UserConfig { Username = "reader" } ]
                },
                new DatabaseConfig
                {
                    DatabaseName = "auditdb",
                    Users =
                    [
                        new UserConfig { Username = "writer" },
                        new UserConfig { Username = "owner" }
                    ]
                }
            ]
        };

        var line = InvokePrivateStatic<string>(
            typeof(TerminalGuiRunner),
            "BuildServerLine",
            server);

        Assert.Equal(
            "pg01.contoso.local | provider: PostgreSQL | admin: postgres | password: saved | dbs: 2 | users: 3",
            line);
    }

    [Fact]
    public void BuildServerLine_ReportsEncryptedPasswordState()
    {
        var server = new ServerConfig
        {
            ServerName = "sql01.contoso.local",
            Provider = SqlProviders.SqlServer,
            AdminUsername = "sa",
            AdminPassword = "smenc:v1:placeholder",
            Encrypted = true
        };

        var line = InvokePrivateStatic<string>(
            typeof(TerminalGuiRunner),
            "BuildServerLine",
            server);

        Assert.Equal(
            "sql01.contoso.local | provider: SQL Server | admin: sa | password: encrypted | dbs: 0 | users: 0",
            line);
    }

    [Fact]
    public void BuildAboutSummaryText_IncludesCopyrightAndBuildLabel()
    {
        var aboutText = InvokePrivateStatic<string>(
            typeof(TerminalGuiRunner),
            "BuildAboutSummaryText");

        Assert.Contains("SQL Manager", aboutText);
        Assert.Contains("Copyright @mictsi", aboutText);
        Assert.Contains("Built:", aboutText);
    }

    [Fact]
    public void HelpContent_CommandReference_IncludesHelpCommandAndDefaultPath()
    {
        var text = HelpContent.BuildCommandReferenceText("C:/temp/sql-config.json");

        Assert.Contains("sql-manager help", text);
        Assert.Contains("Default config path: C:/temp/sql-config.json", text);
    }

    [Fact]
    public void HelpContent_HelpOverview_IncludesFileShortcuts()
    {
        var text = HelpContent.BuildHelpOverviewText("C:/temp/sql-config.json");

        Assert.Contains("Ctrl+S", text);
        Assert.Contains("Ctrl+Q", text);
        Assert.Contains("Snake, Pong, and Tetris", text);
        Assert.Contains("Help: open version details", text);
    }

    private static T InvokePrivateStatic<T>(Type type, string methodName, params object?[] arguments)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, arguments);
        Assert.NotNull(result);
        return (T)result!;
    }
}