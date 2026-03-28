using Microsoft.Data.SqlClient;
using Npgsql;

namespace SqlManager;

internal static class PostgreSqlSslModes
{
    public const string Disable = "disable";
    public const string Prefer = "prefer";
    public const string Require = "require";
    public const string VerifyCa = "verifyca";
    public const string VerifyFull = "verifyfull";

    public static IReadOnlyList<string> Choices { get; } = [Prefer, Require, Disable, VerifyCa, VerifyFull];

    public static string GetDefaultForNewServers() => Prefer;

    public static string NormalizeConfigured(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : Normalize(value);

    public static string GetEffective(string? value)
        => string.IsNullOrWhiteSpace(value) ? Require : Normalize(value);

    public static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new UserInputException("PostgreSQL SSL mode is required.")
            : value.Trim().ToLowerInvariant() switch
            {
                "disable" => Disable,
                "prefer" => Prefer,
                "require" => Require,
                "verifyca" or "verify-ca" => VerifyCa,
                "verifyfull" or "verify-full" => VerifyFull,
                _ => throw new UserInputException($"Unsupported PostgreSQL SSL mode '{value}'.")
            };

    public static string GetDisplayName(string? value)
        => GetEffective(value) switch
        {
            Disable => "Disable",
            Prefer => "Prefer",
            Require => "Require",
            VerifyCa => "VerifyCA",
            VerifyFull => "VerifyFull",
            _ => "Require"
        };

    public static string GetPickerDisplayName(string? value)
        => GetEffective(value) == Prefer
            ? "Prefer (Default)"
            : GetDisplayName(value);

    public static SslMode ToNpgsqlSslMode(string? value)
        => GetEffective(value) switch
        {
            Disable => SslMode.Disable,
            Prefer => SslMode.Prefer,
            Require => SslMode.Require,
            VerifyCa => SslMode.VerifyCA,
            VerifyFull => SslMode.VerifyFull,
            _ => SslMode.Require
        };
}

internal static class SqlServerTrustModes
{
    public const string False = "false";
    public const string True = "true";
    public const string Strict = "strict";

    public static IReadOnlyList<string> Choices { get; } = [False, True, Strict];

    public static string GetDefaultForNewServers() => False;

    public static string NormalizeConfigured(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : Normalize(value);

    public static string GetEffective(string? value)
        => string.IsNullOrWhiteSpace(value) ? True : Normalize(value);

    public static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new UserInputException("SQL Server trust mode is required.")
            : value.Trim().ToLowerInvariant() switch
            {
                "false" or "no" => False,
                "true" or "yes" => True,
                "strict" => Strict,
                _ => throw new UserInputException($"Unsupported SQL Server trust mode '{value}'.")
            };

    public static string GetDisplayName(string? value)
        => GetEffective(value) switch
        {
            False => "Encrypt=False, Trust=False",
            True => "Encrypt=True, Trust=True",
            Strict => "Encrypt=Strict, Trust=False",
            _ => "Encrypt=True, Trust=True"
        };

    public static string GetPickerDisplayName(string? value)
        => GetEffective(value) == False
            ? "False / False (Default)"
            : GetDisplayName(value);

    public static string GetEncryptKeywordValue(string? value)
        => GetEffective(value) switch
        {
            False => "False",
            Strict => "Strict",
            _ => "True"
        };

    public static bool GetTrustServerCertificateValue(string? value)
        => GetEffective(value) == True;

    public static void Apply(SqlConnectionStringBuilder builder, string? value)
    {
        switch (GetEffective(value))
        {
            case Strict:
                builder.Encrypt = SqlConnectionEncryptOption.Strict;
                builder.TrustServerCertificate = false;
                break;
            case True:
                builder.Encrypt = SqlConnectionEncryptOption.Mandatory;
                builder.TrustServerCertificate = true;
                break;
            default:
                builder.Encrypt = SqlConnectionEncryptOption.Optional;
                builder.TrustServerCertificate = false;
                break;
        }
    }
}

internal static class ServerConnectionOptions
{
    public static bool GetEffectivePostgreSqlPooling(bool? pooling)
        => pooling ?? true;

    public static int GetEffectiveConnectionTimeoutSeconds(int? configuredValue, int fallbackValue = SqlTimeoutConfig.DefaultConnectionTimeoutSeconds)
        => configuredValue is > 0 ? configuredValue.Value : fallbackValue;

    public static int GetEffectiveCommandTimeoutSeconds(int? configuredValue, int fallbackValue = SqlTimeoutConfig.DefaultCommandTimeoutSeconds)
        => configuredValue is > 0 ? configuredValue.Value : fallbackValue;

    public static string BuildSqlServerDataSource(string server, int? port)
    {
        var normalizedServer = server.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)
            ? server
            : $"tcp:{server}";

        if (port is > 0 && !normalizedServer.Contains(',', StringComparison.Ordinal))
        {
            return $"{normalizedServer},{port.Value}";
        }

        return normalizedServer;
    }

    public static string BuildSqlServerUserConnectionString(
        string server,
        int? port,
        string database,
        string username,
        string? password,
        string? trustMode)
    {
        var resolvedPassword = string.IsNullOrWhiteSpace(password) ? string.Empty : "********";
        return $"Server={BuildSqlServerDataSource(server, port)};Initial Catalog={database};User ID={username};Password={resolvedPassword};Encrypt={SqlServerTrustModes.GetEncryptKeywordValue(trustMode)};TrustServerCertificate={SqlServerTrustModes.GetTrustServerCertificateValue(trustMode)};";
    }

    public static string BuildPostgreSqlUserConnectionString(
        string server,
        int? port,
        string database,
        string username,
        string? password,
        string? sslMode,
        bool? pooling,
        int? connectionTimeoutSeconds,
        int? commandTimeoutSeconds)
    {
        var resolvedPassword = string.IsNullOrWhiteSpace(password) ? "<PASSWORD_REQUIRED>" : "********";
        var effectivePort = port is > 0 ? port.Value : SqlProviders.GetDefaultPort(SqlProviders.PostgreSql);
        return $"Host={server};Database={database};Username={username};Password={resolvedPassword};Ssl Mode={PostgreSqlSslModes.GetDisplayName(sslMode)};Port={effectivePort};Timeout={GetEffectiveConnectionTimeoutSeconds(connectionTimeoutSeconds)};Command Timeout={GetEffectiveCommandTimeoutSeconds(commandTimeoutSeconds)};Pooling={GetEffectivePostgreSqlPooling(pooling).ToString().ToLowerInvariant()};";
    }
}
