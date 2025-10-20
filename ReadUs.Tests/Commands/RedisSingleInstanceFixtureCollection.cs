using Xunit;

namespace ReadUs.Commands.Tests;

[CollectionDefinition(nameof(RedisSingleInstanceFixtureCollection))]
public class RedisSingleInstanceFixtureCollection : ICollectionFixture<RedisSingleInstanceFixture>
{
    
}