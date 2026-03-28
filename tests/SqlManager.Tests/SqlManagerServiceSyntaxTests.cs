using System.Reflection;

namespace SqlManager.Tests;

public sealed class SqlManagerServiceSyntaxTests
{
    [Fact]
    public void BuildConnectionString_UsesSqlServerFormat()
    {
        var connectionString = InvokePrivateStatic<string>(
            "BuildConnectionString",
            SqlProviders.SqlServer,
            "sql01.contoso.local",
            1433,
            "LabDb",
            "app_user",
            "Secret123!");

        Assert.Equal(
            "Server=tcp:sql01.contoso.local,1433;Initial Catalog=LabDb;User ID=app_user;Password=********;Encrypt=True;TrustServerCertificate=True;",
            connectionString);
    }

    [Fact]
    public void BuildConnectionString_UsesPostgreSqlFormat()
    {
        var connectionString = InvokePrivateStatic<string>(
            "BuildConnectionString",
            SqlProviders.PostgreSql,
            "pg01.contoso.local",
            5432,
            "appdb",
            "app_user",
            "Secret123!");

        Assert.Equal(
            "Host=pg01.contoso.local;Database=appdb;Username=app_user;Password=********;Ssl Mode=Require;Port=5432;Timeout=15;Command Timeout=30;Pooling=true;",
            connectionString);
    }

    [Fact]
    public void BuildSqlServerUserConnectionString_UsesStrictEncryptionWhenRequested()
    {
        var connectionString = ServerConnectionOptions.BuildSqlServerUserConnectionString(
            "myserver.database.windows.net",
            1433,
            "mydb",
            "myuser",
            "secret",
            SqlServerTrustModes.Strict);

        Assert.Equal(
            "Server=tcp:myserver.database.windows.net,1433;Initial Catalog=mydb;User ID=myuser;Password=********;Encrypt=Strict;TrustServerCertificate=False;",
            connectionString);
    }

    [Fact]
    public void BuildPostgreSqlUserConnectionString_UsesConfiguredSecurityAndTimeouts()
    {
        var connectionString = ServerConnectionOptions.BuildPostgreSqlUserConnectionString(
            "pg01.contoso.local",
            5432,
            "appdb",
            "app_user",
            "Secret123!",
            PostgreSqlSslModes.VerifyFull,
            true,
            20,
            45);

        Assert.Equal(
            "Host=pg01.contoso.local;Database=appdb;Username=app_user;Password=********;Ssl Mode=VerifyFull;Port=5432;Timeout=20;Command Timeout=45;Pooling=true;",
            connectionString);
    }

    [Fact]
    public void BuildServerConnectionTestQuery_UsesSqlServerProbe()
    {
        var query = InvokePrivateStatic<string>(
            "BuildServerConnectionTestQuery",
            SqlProviders.SqlServer);

        Assert.Equal("SELECT CAST(1 AS int);", query);
    }

    [Fact]
    public void BuildServerConnectionTestQuery_UsesPostgreSqlProbe()
    {
        var query = InvokePrivateStatic<string>(
            "BuildServerConnectionTestQuery",
            SqlProviders.PostgreSql);

        Assert.Equal("SELECT 1;", query);
    }

    [Fact]
    public void BuildSqlServerRoleMembershipSyncQuery_UsesAlterRoleSyntax()
    {
        var query = InvokePrivateStatic<string>(
            "BuildSqlServerRoleMembershipSyncQuery",
            "app_user",
            (IReadOnlyCollection<string>)new[] { "db_datareader" },
            false);

        Assert.Contains("CREATE USER [app_user] FOR LOGIN [app_user];", query);
        Assert.Contains("ALTER ROLE [db_datareader] ADD MEMBER [app_user];", query);
        Assert.Contains("ALTER ROLE [db_owner] DROP MEMBER [app_user];", query);
        Assert.Contains("ALTER ROLE [db_datawriter] DROP MEMBER [app_user];", query);
    }

    [Fact]
    public void BuildPostgreSqlRoleMembershipSyncQuery_UsesGrantAndRevokeSyntax()
    {
        var ownerRole = InvokePrivateStatic<string>("BuildPostgreSqlManagedRoleName", "appdb", "owner");

        var query = InvokePrivateStatic<string>(
            "BuildPostgreSqlRoleMembershipSyncQuery",
            "appdb",
            "app_user",
            (IReadOnlyCollection<string>)new[] { "db_owner" },
            false);

        Assert.Contains($"CREATE ROLE \"{ownerRole}\" NOLOGIN;", query);
        Assert.Contains($"GRANT \"{ownerRole}\" TO \"app_user\";", query);
        Assert.DoesNotContain("reader", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("writer", query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPostgreSqlShowUsersQuery_MapsOwnerRoleOnly()
    {
        var query = InvokePrivateStatic<string>("BuildPostgreSqlShowUsersQuery", "appdb");

        Assert.Contains("THEN 'db_owner'", query);
        Assert.DoesNotContain("db_datareader", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("db_datawriter", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FROM pg_auth_members", query);
        Assert.Contains("WHERE member.rolcanlogin", query);
    }

    [Fact]
    public void BuildEnsurePostgreSqlManagedRolesQuery_GrantsCompleteDatabaseRightsToOwnerRole()
    {
        var ownerRole = InvokePrivateStatic<string>("BuildPostgreSqlManagedRoleName", "appdb", "owner");
        var query = InvokePrivateStatic<string>("BuildEnsurePostgreSqlManagedRolesQuery", "appdb");

        Assert.Contains($"CREATE ROLE \"{ownerRole}\" NOLOGIN;", query);
        Assert.Contains("GRANT ALL PRIVILEGES ON DATABASE \"appdb\"", query);
        Assert.Contains($"TO \"{ownerRole}\";", query);
        Assert.DoesNotContain("reader", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("writer", query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeRoles_PostgreSql_RejectsReaderAndWriterRoles()
    {
        var exception = Assert.Throws<TargetInvocationException>(() =>
            InvokePrivateStatic<IReadOnlyList<string>>(
                "NormalizeRoles",
                (IReadOnlyList<string>)new[] { "db_datareader" },
                SqlProviders.PostgreSql));

        Assert.IsType<UserInputException>(exception.InnerException);
        Assert.Contains("PostgreSQL supports dbowner/db_owner only", exception.InnerException!.Message);
    }

    private static T InvokePrivateStatic<T>(string methodName, params object?[] arguments)
    {
        var method = typeof(SqlManagerService)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .SingleOrDefault(candidate => candidate.Name == methodName && candidate.GetParameters().Length == arguments.Length);
        Assert.NotNull(method);

        var result = method!.Invoke(null, arguments);
        Assert.NotNull(result);
        return (T)result!;
    }
}
