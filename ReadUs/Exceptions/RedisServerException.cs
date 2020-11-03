using System;

namespace ReadUs.Exceptions
{
    public class RedisServerException : Exception
    {
        public RedisServerException()
        {
        }

        public RedisServerException(string message) : base(message)
        {
        }

        public RedisServerException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}