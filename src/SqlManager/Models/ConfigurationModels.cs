using System.Text.Json.Serialization;
using System.Globalization;

namespace SqlManager;

internal static class SqlProviders
{
    public const string SqlServer = "sqlserver";
    public const string PostgreSql = "postgresql";
    public const string MySql = "mysql";

    public static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? SqlServer
            : value.Trim().ToLowerInvariant() switch
            {
                "sqlserver" or "sql-server" or "mssql" => SqlServer,
                "postgresql" or "postgres" or "pgsql" => PostgreSql,
                "mysql" or "my-sql" or "mariadb" or "maria-db" or "maria" => MySql,
                _ => throw new UserInputException($"Unsupported provider '{value}'. Use sqlserver, postgresql, or mysql.")
            };

    public static string GetDisplayName(string provider)
        => Normalize(provider) switch
        {
            PostgreSql => "PostgreSQL",
            MySql => "MySQL / MariaDB",
            _ => "SQL Server"
        };

    public static int GetDefaultPort(string provider)
        => Normalize(provider) switch
        {
            PostgreSql => 5432,
            MySql => 3306,
            _ => 1433
        };

    public static string GetDefaultAdminDatabase(string provider)
        => Normalize(provider) switch
        {
            PostgreSql => "postgres",
            MySql => "mysql",
            _ => "master"
        };
}

internal sealed class SqlManagerConfig
{
    public string SelectedServerName { get; set; } = string.Empty;
    public string ThemeName { get; set; } = TerminalThemeCatalog.DefaultThemeName;
    public bool EncryptPasswords { get; set; }
    public string EncryptionKey { get; set; } = string.Empty;
    [JsonIgnore]
    public string EncryptedPayload { get; set; } = string.Empty;
    public SqlTimeoutConfig Timeouts { get; set; } = new();
    public List<ServerConfig> Servers { get; set; } = [];
    public List<TrashEntry> Trash { get; set; } = [];
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
    public string EntryId { get; set; } = Guid.NewGuid().ToString("N");
    public string ServerIdentifier { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string Provider { get; set; } = SqlProviders.SqlServer;
    public int? Port { get; set; }
    public string AdminDatabase { get; set; } = string.Empty;
    public string AdminUsername { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;
    public string PostgreSqlSslMode { get; set; } = string.Empty;
    public bool? PostgreSqlPooling { get; set; }
    public string MySqlSslMode { get; set; } = string.Empty;
    public bool? MySqlPooling { get; set; }
    public bool? MySqlAllowPublicKeyRetrieval { get; set; }
    public string SqlServerTrustMode { get; set; } = string.Empty;
    public int? ConnectionTimeoutSeconds { get; set; }
    public int? CommandTimeoutSeconds { get; set; }
    public string DatabasesPayload { get; set; } = string.Empty;
    public bool Encrypted { get; set; }
    public List<DatabaseConfig> Databases { get; set; } = [];
    public List<EntryVersion> VersionHistory { get; set; } = [];
}

internal sealed class DatabaseConfig
{
    public string EntryId { get; set; } = Guid.NewGuid().ToString("N");
    public string DatabaseName { get; set; } = string.Empty;
    public List<UserConfig> Users { get; set; } = [];
    public List<EntryVersion> VersionHistory { get; set; } = [];
}

internal sealed class UserConfig
{
    public string EntryId { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool Encrypted { get; set; }
    public List<string> Roles { get; set; } = [];
    public string ConnectionString { get; set; } = string.Empty;
    public List<EntryVersion> VersionHistory { get; set; } = [];
}

internal sealed class EntryVersion
{
    public int VersionNumber { get; set; }
    public string ChangedAtUtc { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}

internal sealed class TrashEntry
{
    public string TrashId { get; set; } = Guid.NewGuid().ToString("N");
    public string EntryType { get; set; } = string.Empty;
    public string EntryId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ParentServerName { get; set; } = string.Empty;
    public string ParentDatabaseName { get; set; } = string.Empty;
    public string DeletedAtUtc { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public List<EntryVersion> VersionHistory { get; set; } = [];
}

internal sealed record DatabaseUserRow(string UserName, string LoginName, string Roles);

internal sealed record ResolvedServerContext(
    SqlManagerConfig Config,
    ServerConfig? ServerConfig,
    string ServerIdentifier,
    string ServerDisplayName,
    string ServerName,
    string Provider,
    int? Port,
    string AdminDatabase,
    string AdminUsername,
    string AdminPassword,
    SqlTimeoutConfig Timeouts,
    string PostgreSqlSslMode,
    bool PostgreSqlPooling,
    string MySqlSslMode,
    bool MySqlPooling,
    bool MySqlAllowPublicKeyRetrieval,
    string SqlServerTrustMode);

internal static class ServerConnections
{
    public static string GetIdentifier(ServerConfig server)
        => string.IsNullOrWhiteSpace(server.ServerIdentifier)
            ? string.Empty
            : server.ServerIdentifier.Trim();

    public static string GetDisplayName(ServerConfig server)
        => string.IsNullOrWhiteSpace(server.DisplayName)
            ? (!string.IsNullOrWhiteSpace(server.ServerName) ? server.ServerName : GetIdentifier(server))
            : server.DisplayName;

    public static string GetSelectionDisplay(ServerConfig server)
    {
        var displayName = GetDisplayName(server);
        var identifier = GetIdentifier(server);
        return string.IsNullOrWhiteSpace(identifier) || identifier.Equals(displayName, StringComparison.OrdinalIgnoreCase)
            ? displayName
            : $"{displayName} [{identifier}]";
    }

    public static bool TryParseIdentifier(string? value, out int identifier)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            identifier = parsed;
            return true;
        }

        identifier = 0;
        return false;
    }

    public static string NormalizeExistingIdentifier(string? existingIdentifier, ISet<int> usedIdentifiers)
    {
        if (TryParseIdentifier(existingIdentifier, out var parsedIdentifier)
            && usedIdentifiers.Add(parsedIdentifier))
        {
            return parsedIdentifier.ToString(CultureInfo.InvariantCulture);
        }

        return AllocateNextIdentifier(usedIdentifiers).ToString(CultureInfo.InvariantCulture);
    }

    public static string ReserveIdentifier(string? requestedIdentifier, ISet<int> usedIdentifiers)
    {
        if (TryParseIdentifier(requestedIdentifier, out var parsedIdentifier)
            && usedIdentifiers.Add(parsedIdentifier))
        {
            return parsedIdentifier.ToString(CultureInfo.InvariantCulture);
        }

        return AllocateNextIdentifier(usedIdentifiers).ToString(CultureInfo.InvariantCulture);
    }

    public static string GetNextIdentifier(IEnumerable<ServerConfig> servers)
    {
        var usedIdentifiers = new HashSet<int>();
        foreach (var server in servers)
        {
            if (TryParseIdentifier(server.ServerIdentifier, out var identifier))
            {
                usedIdentifiers.Add(identifier);
            }
        }

        return AllocateNextIdentifier(usedIdentifiers).ToString(CultureInfo.InvariantCulture);
    }

    public static ServerConfig? FindBySelectionKey(IEnumerable<ServerConfig> servers, string? selectionKey)
    {
        if (string.IsNullOrWhiteSpace(selectionKey))
        {
            return null;
        }

        var identifierMatch = servers.FirstOrDefault(server => GetIdentifier(server).Equals(selectionKey, StringComparison.OrdinalIgnoreCase));
        if (identifierMatch is not null)
        {
            return identifierMatch;
        }

        var hostMatches = servers
            .Where(server => server.ServerName.Equals(selectionKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return hostMatches.Count == 1 ? hostMatches[0] : null;
    }

    public static bool MatchesSelectionKey(string? selectionKey, string? serverIdentifier, string? serverName)
    {
        if (string.IsNullOrWhiteSpace(selectionKey))
        {
            return false;
        }

        return (!string.IsNullOrWhiteSpace(serverIdentifier)
                && serverIdentifier.Equals(selectionKey, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(serverName)
                && serverName.Equals(selectionKey, StringComparison.OrdinalIgnoreCase));
    }

    private static int AllocateNextIdentifier(ISet<int> usedIdentifiers)
    {
        var nextIdentifier = 1;
        while (!usedIdentifiers.Add(nextIdentifier))
        {
            nextIdentifier++;
        }

        return nextIdentifier;
    }
}
