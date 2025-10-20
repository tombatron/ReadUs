namespace ReadUs;

public interface IRedisConnection : IDisposable
{
    string ConnectionName { get; }
    bool IsConnected { get; }

    bool IsFaulted { get; }

    Result Connect();

    Task<Result> ConnectAsync(CancellationToken cancellationToken = default);

    Result<byte[]> SendCommand(RedisCommandEnvelope command);

    Task<Result<byte[]>> SendCommandAsync(RedisCommandEnvelope command, CancellationToken cancellationToken = default);
    
    Task SendCommandWithMultipleResponses(RedisCommandEnvelope command, Action<byte[]> onResponse, CancellationToken cancellationToken = default);
}