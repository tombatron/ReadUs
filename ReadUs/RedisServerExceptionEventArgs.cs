using ReadUs.Exceptions;

namespace ReadUs;

internal sealed class RedisServerExceptionEventArgs
{
    internal RedisServerException Exception { get; }

    internal RedisServerExceptionEventArgs(RedisServerException exception) =>
        Exception = exception;
}