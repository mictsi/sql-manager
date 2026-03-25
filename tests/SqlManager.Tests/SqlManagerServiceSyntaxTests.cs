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
            "Server=sql01.contoso.local,1433;Database=LabDb;User ID=app_user;Password=Secret123!;Encrypt=True;TrustServerCertificate=True;",
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
            "Host=pg01.contoso.local;Port=5432;Database=appdb;Username=app_user;Password=Secret123!;Ssl Mode=Require;",
            connectionString);
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
        var readerRole = InvokePrivateStatic<string>("BuildPostgreSqlManagedRoleName", "appdb", "reader");
        var writerRole = InvokePrivateStatic<string>("BuildPostgreSqlManagedRoleName", "appdb", "writer");

        var query = InvokePrivateStatic<string>(
            "BuildPostgreSqlRoleMembershipSyncQuery",
            "appdb",
            "app_user",
            (IReadOnlyCollection<string>)new[] { "db_datareader" },
            false);

        Assert.Contains($"CREATE ROLE \"{ownerRole}\" NOLOGIN;", query);
        Assert.Contains($"CREATE ROLE \"{readerRole}\" NOLOGIN;", query);
        Assert.Contains($"CREATE ROLE \"{writerRole}\" NOLOGIN;", query);
        Assert.Contains($"GRANT \"{readerRole}\" TO \"app_user\";", query);
        Assert.Contains($"REVOKE \"{ownerRole}\" FROM \"app_user\";", query);
        Assert.Contains($"REVOKE \"{writerRole}\" FROM \"app_user\";", query);
    }

    [Fact]
    public void BuildPostgreSqlShowUsersQuery_MapsManagedRolesToGenericRoles()
    {
        var query = InvokePrivateStatic<string>("BuildPostgreSqlShowUsersQuery", "appdb");

        Assert.Contains("THEN 'db_owner'", query);
        Assert.Contains("THEN 'db_datareader'", query);
        Assert.Contains("THEN 'db_datawriter'", query);
        Assert.Contains("FROM pg_auth_members", query);
        Assert.Contains("WHERE member.rolcanlogin", query);
    }

    private static T InvokePrivateStatic<T>(string methodName, params object?[] arguments)
    {
        var method = typeof(SqlManagerService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, arguments);
        Assert.NotNull(result);
        return (T)result!;
    }
}