using System;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.ResultModels;

namespace ReadUs;

public interface IRedisConnection : IDisposable
{
    string ConnectionName { get; }
    bool IsConnected { get; }

    void Connect();

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Result<RoleResult> Role();

    Task<Result<RoleResult>> RoleAsync(CancellationToken cancellationToken = default);

    Result<byte[]> SendCommand(RedisCommandEnvelope command);

    Task<Result<byte[]>> SendCommandAsync(RedisCommandEnvelope command, CancellationToken cancellationToken = default);
}