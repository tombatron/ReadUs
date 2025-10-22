using Xunit;

namespace ReadUs.Tests.Commands;

[CollectionDefinition(nameof(RedisSingleInstanceFixtureCollection))]
public class RedisSingleInstanceFixtureCollection : ICollectionFixture<RedisSingleInstanceFixture>
{
    
}