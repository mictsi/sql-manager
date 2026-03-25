using System.Text;
using Spectre.Console;

namespace SqlManager;

internal static class Program
{
    private static int _fatalErrorWritten;
    private static int _cancelRequested;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        RegisterGlobalHandlers();

        using var cancellationSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (Interlocked.Increment(ref _cancelRequested) == 1)
            {
                cancellationSource.Cancel();
                return;
            }

            Environment.Exit(130);
        };

        var ui = new TerminalUi(AnsiConsole.Console);
        var app = new SqlManagerApplication(
            ui,
            new SqlManagerService(
                new ConfigStore(),
                new PasswordGenerator(),
                new SqlServerGateway(),
                new PostgreSqlGateway()));

        try
        {
            return await app.RunAsync(args, cancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            ui.WriteWarning("Operation cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            ErrorLogger.LogException("Fatal application error", exception);
            ui.WriteFatal(exception.Message);
            return 1;
        }
    }

    private static void RegisterGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                ErrorLogger.LogException("Unhandled application error", exception);
                SafeWriteFatal($"Unhandled error: {exception.Message}");
            }
            else
            {
                ErrorLogger.LogMessage("Unhandled application error", "Unhandled exception object was not an Exception instance.");
                SafeWriteFatal("Unhandled error occurred.");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            eventArgs.SetObserved();
            ErrorLogger.LogException("Background task error", eventArgs.Exception.GetBaseException());
            SafeWriteFatal($"Background task error: {eventArgs.Exception.GetBaseException().Message}");
        };
    }

    private static void SafeWriteFatal(string message)
    {
        if (Interlocked.Exchange(ref _fatalErrorWritten, 1) == 1)
        {
            return;
        }

        try
        {
            Console.Error.WriteLine(message);
        }
        catch
        {
            // Last-resort error reporting must not throw.
        }
    }
}

