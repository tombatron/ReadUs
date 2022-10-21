using System;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.ResultModels;

namespace ReadUs;

public interface IRedisConnection : IDisposable
{
    bool IsConnected { get; }

    void Connect();

    Task ConnectAsync(CancellationToken cancellationToken = default);

    RoleResult Role();

    Task<RoleResult> RoleAsync(CancellationToken cancellationToken = default);

    byte[] SendCommand(RedisCommandEnvelope command);

    Task<byte[]> SendCommandAsync(RedisCommandEnvelope command, CancellationToken cancellationToken = default);
}