namespace ReadUs.Sentinel;

/// <summary>Thrown when quorum-based master discovery (project spec §6) can't reach a majority agreement across the configured sentinels after several attempts.</summary>
public sealed class SentinelDiscoveryException : Exception
{
    public SentinelDiscoveryException(string message) : base(message)
    {
    }

    public SentinelDiscoveryException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
