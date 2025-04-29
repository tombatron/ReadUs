using Xunit;

namespace ReadUs.Tests;

[CollectionDefinition(nameof(RedisClusterFixtureCollection))]
public class RedisClusterFixtureCollection : ICollectionFixture<RedisClusterFixture>
{
}