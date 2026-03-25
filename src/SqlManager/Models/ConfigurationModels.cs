namespace SqlManager;

internal sealed class SqlManagerConfig
{
    public string SelectedServerName { get; set; } = string.Empty;
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
    public string AdminUsername { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;
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
    public List<string> Roles { get; set; } = [];
    public string ConnectionString { get; set; } = string.Empty;
}

internal sealed record DatabaseUserRow(string UserName, string LoginName, string Roles);

internal sealed record ResolvedServerContext(
    SqlManagerConfig Config,
    ServerConfig? ServerConfig,
    string ServerName,
    string AdminUsername,
    string AdminPassword,
    SqlTimeoutConfig Timeouts);
