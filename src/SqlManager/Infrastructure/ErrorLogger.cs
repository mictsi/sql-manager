using System.Reflection;
using System.Text;

namespace SqlManager;

internal static class ErrorLogger
{
    private static readonly object SyncRoot = new();

    internal static string LogFilePath => BuildLogFilePath(
        AppContext.BaseDirectory,
        Environment.ProcessPath,
        Assembly.GetEntryAssembly()?.GetName().Name);

    public static void LogException(string context, Exception exception)
        => TryWriteEntry(context, exception.ToString());

    public static void LogMessage(string context, string message)
        => TryWriteEntry(context, message);

    internal static string BuildLogFilePath(string? baseDirectory, string? processPath, string? assemblyName)
    {
        var directory = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        var fileStem = GetFileStem(processPath);

        if (string.IsNullOrWhiteSpace(fileStem))
        {
            fileStem = assemblyName;
        }

        if (string.IsNullOrWhiteSpace(fileStem))
        {
            fileStem = "application";
        }

        return Path.Combine(directory, $"{fileStem}.log");
    }

    private static string? GetFileStem(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        var lastSeparatorIndex = processPath.LastIndexOfAny(['\\', '/']);
        var fileName = lastSeparatorIndex >= 0
            ? processPath[(lastSeparatorIndex + 1)..]
            : processPath;

        return Path.GetFileNameWithoutExtension(fileName);
    }

    internal static void WriteEntry(string context, string details, string? logFilePath)
    {
        var path = string.IsNullOrWhiteSpace(logFilePath) ? LogFilePath : logFilePath;
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var normalizedDetails = string.IsNullOrWhiteSpace(details) ? "(no details)" : details.TrimEnd();
        var builder = new StringBuilder()
            .Append('[')
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"))
            .Append("] ")
            .Append(context)
            .AppendLine()
            .AppendLine(normalizedDetails)
            .AppendLine();

        lock (SyncRoot)
        {
            File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
        }
    }

    private static void TryWriteEntry(string context, string details)
    {
        try
        {
            WriteEntry(context, details, null);
        }
        catch
        {
            // Error logging must never fail the main operation.
        }
    }
}