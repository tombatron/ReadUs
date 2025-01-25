using System;

namespace ReadUs.Exceptions;

public class RedisWrongTypeException(string message) : Exception(message);