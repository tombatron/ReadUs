namespace ReadUs.Transactions;

/// <summary>Thrown when the server rejects a transaction command (WATCH/MULTI/a queued command/EXEC).</summary>
public sealed class RedisTransactionException : Exception
{
    public RedisTransactionException(string message) : base(message)
    {
    }
}
