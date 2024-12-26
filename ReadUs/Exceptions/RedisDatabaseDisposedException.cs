using System;

namespace ReadUs.Exceptions;

public class RedisDatabaseDisposedException : Exception
{
    public RedisDatabaseDisposedException()
    {
    }

    public RedisDatabaseDisposedException(string message) : base(message)
    {
    }

    public RedisDatabaseDisposedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}