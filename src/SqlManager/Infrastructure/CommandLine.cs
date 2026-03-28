using System.Collections.ObjectModel;

namespace SqlManager;

internal enum CommandKind
{
    Help,
    Version,
    Tui,
    ViewConfig,
    InitConfig,
    AddServer,
    SelectServer,
    SyncServer,
    ShowDatabases,
    CreateDatabase,
    RemoveDatabase,
    CreateUser,
    AddRole,
    RemoveRole,
    ShowUsers,
    TestUserLogin,
    RemoveUser,
    UpdatePassword,
    EnableConfigEncryption,
    DisableConfigEncryption,
    MigrateConfigEncryptionFormat
}

internal enum RemovalScope
{
    Database,
    Server,
    Both
}

internal sealed record DatabaseRoleAssignment(string DatabaseName, IReadOnlyList<string> Roles);

internal sealed class CommandOptions
{
    public required CommandKind Command { get; set; }
    public required string ConfigPath { get; set; }
    public string? ServerIdentifier { get; set; }
    public string? DisplayName { get; set; }
    public string? ServerName { get; set; }
    public string? Provider { get; set; }
    public int? Port { get; set; }
    public string? AdminDatabase { get; set; }
    public string? AdminUsername { get; set; }
    public string? AdminPassword { get; set; }
    public string? PostgreSqlSslMode { get; set; }
    public bool? PostgreSqlPooling { get; set; }
    public string? SqlServerTrustMode { get; set; }
    public int? ConnectionTimeoutSeconds { get; set; }
    public int? CommandTimeoutSeconds { get; set; }
    public string? DatabaseName { get; set; }
    public IReadOnlyList<string> DatabaseNames { get; set; } = Array.Empty<string>();
    public string? UserName { get; set; }
    public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();
    public IReadOnlyList<DatabaseRoleAssignment> DatabaseRoleAssignments { get; set; } = Array.Empty<DatabaseRoleAssignment>();
    public string? NewUserPassword { get; set; }
    public string? EncryptionPassword { get; set; }
    public RemovalScope RemovalScope { get; set; } = RemovalScope.Both;
    public bool RemoveServerLogin { get; set; }
    public bool NonInteractive { get; set; }
}

internal sealed class ParsedArguments
{
    public required CommandKind Command { get; init; }
    public required CommandOptions Options { get; init; }
}

internal static class CommandLineParser
{
    public static OperationResult<ParsedArguments> Parse(string[] args, string defaultConfigPath)
    {
        try
        {
            if (args.Length == 0)
            {
                return OperationResult<ParsedArguments>.Success(
                    new ParsedArguments
                    {
                        Command = CommandKind.Tui,
                        Options = new CommandOptions
                        {
                            Command = CommandKind.Tui,
                            ConfigPath = defaultConfigPath
                        }
                    },
                    "No command line arguments supplied.");
            }

            var optionMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string? rawCommand = null;

            for (var index = 0; index < args.Length; index++)
            {
                var token = args[index];
                if (IsOptionToken(token))
                {
                    ParseOption(token, args, ref index, optionMap);
                    continue;
                }

                rawCommand ??= token;
            }

            if (TryGetSingle(optionMap, "action", out var actionValue))
            {
                rawCommand = actionValue;
            }

            if (string.IsNullOrWhiteSpace(rawCommand)
                && (optionMap.ContainsKey("version") || optionMap.ContainsKey("v")))
            {
                rawCommand = "version";
            }

            var command = ResolveCommand(rawCommand);
            if (command is null)
            {
                return OperationResult<ParsedArguments>.Failure(
                    "Unknown command. Use 'help' to see valid commands.",
                    2);
            }

            var configPath = GetSingleOrDefault(optionMap, defaultConfigPath, "configpath", "config") ?? defaultConfigPath;
            var roles = ParseRoles(optionMap);
            var databaseNames = ParseDatabaseNames(optionMap);
            var removalScope = ParseRemovalScope(GetSingleOrDefault(optionMap, "Both", "removalscope"));
            var provider = ParseProvider(GetSingleOrDefault(optionMap, null, "provider", "serverprovider"));
            var port = ParseNullableInt(GetSingleOrDefault(optionMap, null, "port"), "Port must be a positive integer.");
            var connectionTimeout = ParseNullableInt(GetSingleOrDefault(optionMap, null, "connectiontimeout", "timeout"), "Connection timeout must be a positive integer.");
            var commandTimeout = ParseNullableInt(GetSingleOrDefault(optionMap, null, "commandtimeout"), "Command timeout must be a positive integer.");
            var pooling = ParseNullableBool(GetSingleOrDefault(optionMap, null, "pooling"), "Pooling must be true or false.");
            var parsed = new ParsedArguments
            {
                Command = command.Value,
                Options = new CommandOptions
                {
                    Command = command.Value,
                    ConfigPath = configPath,
                    ServerIdentifier = GetSingleOrDefault(optionMap, null, "serveridentifier", "identifier", "serverid"),
                    DisplayName = GetSingleOrDefault(optionMap, null, "displayname"),
                    ServerName = GetSingleOrDefault(optionMap, null, "servername", "server"),
                    Provider = provider,
                    Port = port,
                    AdminDatabase = GetSingleOrDefault(optionMap, null, "admindatabase", "maintenanceDatabase", "maintenanceDb"),
                    AdminUsername = GetSingleOrDefault(optionMap, null, "adminusername", "adminuser", "sqladmin"),
                    AdminPassword = GetSingleOrDefault(optionMap, null, "adminpassword"),
                    PostgreSqlSslMode = GetSingleOrDefault(optionMap, null, "sslmode"),
                    PostgreSqlPooling = pooling,
                    SqlServerTrustMode = GetSingleOrDefault(optionMap, null, "trustservercertificate", "trustmode"),
                    ConnectionTimeoutSeconds = connectionTimeout,
                    CommandTimeoutSeconds = commandTimeout,
                    DatabaseName = databaseNames.Count > 0 ? databaseNames[^1] : null,
                    DatabaseNames = databaseNames,
                    UserName = GetSingleOrDefault(optionMap, null, "username", "user"),
                    Roles = roles,
                    NewUserPassword = GetSingleOrDefault(optionMap, null, "newuserpassword", "userpassword"),
                    EncryptionPassword = GetSingleOrDefault(optionMap, null, "encryptionpassword", "configpassword", "unlockpassword"),
                    RemovalScope = removalScope,
                    RemoveServerLogin = optionMap.ContainsKey("removeserverlogin"),
                    NonInteractive = optionMap.ContainsKey("noninteractive")
                }
            };

            return OperationResult<ParsedArguments>.Success(parsed, "Command line parsed.");
        }
        catch (Exception exception)
        {
            return OperationResult<ParsedArguments>.Failure($"Invalid command line: {exception.Message}", 2);
        }
    }

    private static bool IsOptionToken(string token)
        => token.StartsWith("--", StringComparison.Ordinal)
            || token.StartsWith("-", StringComparison.Ordinal)
            || token.StartsWith("/", StringComparison.Ordinal);

    private static void ParseOption(string token, string[] args, ref int index, Dictionary<string, List<string>> optionMap)
    {
        var trimmed = token.TrimStart('-', '/');
        var equalsIndex = trimmed.IndexOf('=');
        string key;
        string? value = null;

        if (equalsIndex >= 0)
        {
            key = NormalizeKey(trimmed[..equalsIndex]);
            value = trimmed[(equalsIndex + 1)..];
        }
        else
        {
            key = NormalizeKey(trimmed);
            if (index + 1 < args.Length && !IsOptionToken(args[index + 1]))
            {
                index++;
                value = args[index];
            }
        }

        if (!optionMap.TryGetValue(key, out var values))
        {
            values = new List<string>();
            optionMap[key] = values;
        }

        values.Add(value ?? "true");
    }

    private static string NormalizeKey(string key)
        => string.Concat(key.Where(character => character is not '-' and not '_')).ToLowerInvariant();

    private static CommandKind? ResolveCommand(string? rawCommand)
    {
        if (string.IsNullOrWhiteSpace(rawCommand))
        {
            return CommandKind.Tui;
        }

        return NormalizeKey(rawCommand) switch
        {
            "help" or "h" or "?" => CommandKind.Help,
            "version" or "ver" => CommandKind.Version,
            "tui" or "interactive" => CommandKind.Tui,
            "viewconfig" or "status" => CommandKind.ViewConfig,
            "initconfig" => CommandKind.InitConfig,
            "addserver" => CommandKind.AddServer,
            "selectserver" => CommandKind.SelectServer,
            "syncserver" => CommandKind.SyncServer,
            "showdatabases" => CommandKind.ShowDatabases,
            "createdatabase" => CommandKind.CreateDatabase,
            "removedatabase" => CommandKind.RemoveDatabase,
            "createuser" => CommandKind.CreateUser,
            "addrole" => CommandKind.AddRole,
            "removerole" => CommandKind.RemoveRole,
            "showusers" => CommandKind.ShowUsers,
            "testuserlogin" or "testlogin" or "testuserconnection" => CommandKind.TestUserLogin,
            "removeuser" => CommandKind.RemoveUser,
            "updatepassword" => CommandKind.UpdatePassword,
            "enableconfigencryption" or "encryptconfig" => CommandKind.EnableConfigEncryption,
            "disableconfigencryption" or "decryptconfig" => CommandKind.DisableConfigEncryption,
            "migrateconfigencryptionformat" or "migrateencryptedconfigformat" or "migrateconfigencryption" => CommandKind.MigrateConfigEncryptionFormat,
            _ => null
        };
    }

    private static bool TryGetSingle(IReadOnlyDictionary<string, List<string>> optionMap, string key, out string value)
    {
        if (optionMap.TryGetValue(key, out var values) && values.Count > 0)
        {
            value = values[^1];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string? GetSingleOrDefault(IReadOnlyDictionary<string, List<string>> optionMap, string? defaultValue, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (TryGetSingle(optionMap, key, out var value))
            {
                return value;
            }
        }

        return defaultValue;
    }

    private static ReadOnlyCollection<string> ParseRoles(IReadOnlyDictionary<string, List<string>> optionMap)
    {
        if (!optionMap.TryGetValue("roles", out var values))
        {
            return Array.Empty<string>().AsReadOnly();
        }

        var roles = values
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return roles.AsReadOnly();
    }

    private static ReadOnlyCollection<string> ParseDatabaseNames(IReadOnlyDictionary<string, List<string>> optionMap)
    {
        var values = new List<string>();
        foreach (var key in new[] { "databasename", "database" })
        {
            if (!optionMap.TryGetValue(key, out var keyValues))
            {
                continue;
            }

            values.AddRange(keyValues.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        }

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    private static RemovalScope ParseRemovalScope(string? rawScope)
    {
        if (string.IsNullOrWhiteSpace(rawScope))
        {
            return RemovalScope.Both;
        }

        return NormalizeKey(rawScope) switch
        {
            "database" => RemovalScope.Database,
            "server" => RemovalScope.Server,
            "both" => RemovalScope.Both,
            _ => throw new InvalidOperationException("RemovalScope must be Database, Server, or Both.")
        };
    }

    private static string? ParseProvider(string? rawProvider)
        => string.IsNullOrWhiteSpace(rawProvider) ? null : SqlProviders.Normalize(rawProvider);

    private static int? ParseNullableInt(string? rawValue, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        if (!int.TryParse(rawValue, out var value) || value <= 0)
        {
            throw new InvalidOperationException(errorMessage);
        }

        return value;
    }

    private static bool? ParseNullableBool(string? rawValue, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        return rawValue.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "1" => true,
            "false" or "no" or "0" => false,
            _ => throw new InvalidOperationException(errorMessage)
        };
    }
}
