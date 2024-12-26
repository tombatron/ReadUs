using System;

namespace ReadUs.Exceptions;

public class RedisConnectionConfigurationException : Exception
{
    public RedisConnectionConfigurationException()
    {
    }

    public RedisConnectionConfigurationException(string message) : base(message)
    {
    }

    public RedisConnectionConfigurationException(string message, Exception innerException) : base(message,
        innerException)
    {
    }
}