using ReadUs.Protocol;

namespace ReadUs.Connections;

/// <summary>
/// What <see cref="RedisConnection.SendBlockingAsync"/> uses to issue <c>CLIENT
/// UNBLOCK</c> on a side channel (project spec §4's reserved control-connection
/// sub-pool, hazard H5 in docs/design/state-machines.md §2.6). A narrow interface
/// rather than a direct dependency on <c>ReadUs.Pooling.MultiplexedConnectionPool</c>
/// so the Connections layer doesn't need to know about pooling policy — it just needs
/// something that can execute one command and hand back the reply.
/// </summary>
internal interface IControlChannel
{
    ValueTask<RedisResult> ExecuteAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default);
}
