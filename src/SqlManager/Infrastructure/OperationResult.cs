namespace SqlManager;

internal class OperationResult
{
    public bool Succeeded { get; }
    public string Message { get; }
    public IReadOnlyList<string> Details { get; }
    public int ExitCode { get; }

    protected OperationResult(bool succeeded, string message, int exitCode, IReadOnlyList<string>? details)
    {
        Succeeded = succeeded;
        Message = message;
        ExitCode = exitCode;
        Details = details ?? Array.Empty<string>();
    }

    public static OperationResult Success(string message, params string[] details)
        => new(true, message, 0, details);

    public static OperationResult Failure(string message, int exitCode = 1, params string[] details)
        => new(false, message, exitCode, details);
}

internal sealed class OperationResult<T> : OperationResult
{
    public T? Value { get; }

    private OperationResult(bool succeeded, T? value, string message, int exitCode, IReadOnlyList<string>? details)
        : base(succeeded, message, exitCode, details)
    {
        Value = value;
    }

    public static OperationResult<T> Success(T value, string message, params string[] details)
        => new(true, value, message, 0, details);

    public new static OperationResult<T> Failure(string message, int exitCode = 1, params string[] details)
        => new(false, default, message, exitCode, details);
}

internal sealed class UserInputException : Exception
{
    public UserInputException(string message)
        : base(message)
    {
    }
}
