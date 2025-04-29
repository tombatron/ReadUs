using Xunit;

namespace ReadUs.Tests;

[CollectionDefinition(nameof(RedisSingleInstanceFixtureCollection))]
public class RedisSingleInstanceFixtureCollection : ICollectionFixture<RedisSingleInstanceFixture>
{
}