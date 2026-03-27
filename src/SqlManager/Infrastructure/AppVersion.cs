using System.Globalization;
using System.Reflection;

namespace SqlManager;

internal static class AppVersion
{
    private const string DefaultRepositoryUrl = "https://github.com/mictsi/sql-manager";
    private const string DefaultDescription = "Manage SQL Server and PostgreSQL servers, databases, users, and configuration from a terminal UI or CLI.";
    private static readonly Assembly CurrentAssembly = Assembly.GetExecutingAssembly();

    public static string DisplayVersion { get; } = ResolveDisplayVersion();
    public static string Description { get; } = ResolveDescription();
    public static string RepositoryUrl { get; } = ResolveRepositoryUrl();
    public static string Copyright { get; } = ResolveCopyright();
    public static string BuildDate { get; } = ResolveBuildDate();

    private static string ResolveDisplayVersion()
    {
        return CurrentAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? CurrentAssembly.GetName().Version?.ToString()
            ?? "0.0.0.0";
    }

    private static string ResolveDescription()
        => CurrentAssembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description
            ?? DefaultDescription;

    private static string ResolveRepositoryUrl()
        => ResolveMetadataValue("RepositoryUrl")
            ?? DefaultRepositoryUrl;

    private static string ResolveCopyright()
        => CurrentAssembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
            ?? "Copyright @mictsi";

    private static string ResolveBuildDate()
    {
        var value = ResolveMetadataValue("BuildDate");
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var buildDate))
        {
            return buildDate.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        }

        return value;
    }

    private static string? ResolveMetadataValue(string key)
        => CurrentAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?
            .Value;
}