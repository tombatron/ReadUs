namespace ReadUs;

public interface IRedisConnectionPool : IDisposable
{
    Task<IRedisDatabase> GetDatabase(int databaseId = 0, CancellationToken cancellationToken = default);
}