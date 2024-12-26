using ReadUs.Exceptions;

namespace ReadUs;

internal sealed class RedisServerExceptionEventArgs
{
    internal RedisServerExceptionEventArgs(RedisServerException exception)
    {
        Exception = exception;
    }

    internal RedisServerException Exception { get; }
}