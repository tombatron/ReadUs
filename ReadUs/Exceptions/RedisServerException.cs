namespace ReadUs.Exceptions;

public class RedisServerException : Exception
{
    public RedisServerException()
    {
    }

    public RedisServerException(string message, string redisError) : base(message)
    {
        RedisError = redisError;
    }

    public RedisServerException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public string? RedisError { get; set; }
}