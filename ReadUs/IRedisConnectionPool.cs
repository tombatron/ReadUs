namespace ReadUs;

public interface IRedisConnectionPool : IDisposable
{
    IRedisDatabase GetDatabase(int databaseId = 0);
}