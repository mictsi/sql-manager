using System.Reflection;

namespace SqlManager;

internal static class AppVersion
{
    public static string DisplayVersion { get; } = ResolveDisplayVersion();

    private static string ResolveDisplayVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0.0";
    }
}