using System;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.ResultModels;

namespace ReadUs;

public interface IRedisConnection : IDisposable
{
    string ConnectionName { get; }
    bool IsConnected { get; }

    bool IsFaulted { get; }

    Result Connect();

    Task<Result> ConnectAsync(CancellationToken cancellationToken = default);

    Result<RoleResult> Role();
    
    Task<Result<RoleResult>> RoleAsync(CancellationToken cancellationToken = default);
    
    Result<ClusterSlots> Slots();
    
    Task<Result<ClusterSlots>> SlotsAsync(CancellationToken cancellationToken = default);

    Result<byte[]> SendCommand(RedisCommandEnvelope command);

    Task<Result<byte[]>> SendCommandAsync(RedisCommandEnvelope command, CancellationToken cancellationToken = default);
    
    Task SendCommandWithMultipleResponses(RedisCommandEnvelope command, Action<byte[]> onResponse, CancellationToken cancellationToken = default);
}