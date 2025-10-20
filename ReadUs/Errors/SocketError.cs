using System.Runtime.CompilerServices;

namespace ReadUs.Errors;

internal sealed class SocketError : IErrorDetails
{
    public IErrorResult? ChildError { get; }
    public string[] Messages { get; }
    public string CallerFilePath { get; }
    public int CallerLineNumber { get; }

    public SocketError(IErrorResult? childError, string[] messages, string callerFilePath, int callerLineNumber)
    {
        ChildError = childError;
        Messages = messages;
        CallerFilePath = callerFilePath;
        CallerLineNumber = callerLineNumber;
    }

    public static Result<T> Create<T>(
        string message,
        IErrorResult? childError = null,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0) where T : notnull =>
        Create<T>([message], childError, callerFilePath, callerLineNumber);

    public static Result<T> Create<T>(
        string[] messages,
        IErrorResult? childError = null,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0) where T : notnull =>
        Result<T>.Error(new SocketError(childError, messages, callerFilePath, callerLineNumber));
    
}