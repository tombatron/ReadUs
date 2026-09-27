namespace ReadUs;

/// <summary>One command to submit as part of a batch, via <c>ExecuteBatchAsync</c> on <see cref="RedisClient"/> or a Cluster/Sentinel client wrapping one (project spec §5's "pipelining means batching per-destination-node, not per-call").</summary>
public readonly struct RedisBatchCommand
{
    public required ReadOnlyMemory<byte> CommandName { get; init; }

    public required ReadOnlyMemory<byte>[] Args { get; init; }
}
