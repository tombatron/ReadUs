using System;

namespace ReadUs.Exceptions;

public class RedisConnectionException(string message) : Exception(message);