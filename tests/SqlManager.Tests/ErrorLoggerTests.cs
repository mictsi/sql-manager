namespace SqlManager.Tests;

public sealed class ErrorLoggerTests
{
    [Fact]
    public void BuildLogFilePath_UsesExecutableNameInBaseDirectory()
    {
        var path = ErrorLogger.BuildLogFilePath(
            @"C:\apps\sql-manager",
            @"C:\apps\sql-manager\sql-manager.exe",
            "sql-manager");

        Assert.Equal(Path.Combine(@"C:\apps\sql-manager", "sql-manager.log"), path);
    }

    [Fact]
    public void WriteEntry_WritesContextAndDetailsToLogFile()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var logPath = Path.Combine(tempDirectory, "sql-manager.log");

            ErrorLogger.WriteEntry("Config load failed", "Boom", logPath);

            Assert.True(File.Exists(logPath));

            var contents = File.ReadAllText(logPath);
            Assert.Contains("Config load failed", contents);
            Assert.Contains("Boom", contents);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}