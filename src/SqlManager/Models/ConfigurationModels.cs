namespace SqlManager;

internal static class SqlProviders
{
    public const string SqlServer = "sqlserver";
    public const string PostgreSql = "postgresql";

    public static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? SqlServer
            : value.Trim().ToLowerInvariant() switch
            {
                "sqlserver" or "sql-server" or "mssql" => SqlServer,
                "postgresql" or "postgres" or "pgsql" => PostgreSql,
                _ => throw new UserInputException($"Unsupported provider '{value}'. Use sqlserver or postgresql.")
            };

    public static string GetDisplayName(string provider)
        => Normalize(provider) == PostgreSql ? "PostgreSQL" : "SQL Server";

    public static string GetDefaultAdminDatabase(string provider)
        => Normalize(provider) == PostgreSql ? "postgres" : "master";
}

internal sealed class SqlManagerConfig
{
    public string SelectedServerName { get; set; } = string.Empty;
    public bool EncryptPasswords { get; set; }
    public string EncryptionKey { get; set; } = string.Empty;
    public SqlTimeoutConfig Timeouts { get; set; } = new();
    public List<ServerConfig> Servers { get; set; } = [];
}

internal sealed class SqlTimeoutConfig
{
    public const int DefaultConnectionTimeoutSeconds = 15;
    public const int DefaultCommandTimeoutSeconds = 30;

    public int ConnectionTimeoutSeconds { get; set; } = DefaultConnectionTimeoutSeconds;
    public int CommandTimeoutSeconds { get; set; } = DefaultCommandTimeoutSeconds;
}

internal sealed class ServerConfig
{
    public string ServerName { get; set; } = string.Empty;
    public string Provider { get; set; } = SqlProviders.SqlServer;
    public int? Port { get; set; }
    public string AdminDatabase { get; set; } = string.Empty;
    public string AdminUsername { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;
    public bool Encrypted { get; set; }
    public List<DatabaseConfig> Databases { get; set; } = [];
}

internal sealed class DatabaseConfig
{
    public string DatabaseName { get; set; } = string.Empty;
    public List<UserConfig> Users { get; set; } = [];
}

internal sealed class UserConfig
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool Encrypted { get; set; }
    public List<string> Roles { get; set; } = [];
    public string ConnectionString { get; set; } = string.Empty;
}

internal sealed record DatabaseUserRow(string UserName, string LoginName, string Roles);

internal sealed record ResolvedServerContext(
    SqlManagerConfig Config,
    ServerConfig? ServerConfig,
    string ServerName,
    string Provider,
    int? Port,
    string AdminDatabase,
    string AdminUsername,
    string AdminPassword,
    SqlTimeoutConfig Timeouts);
