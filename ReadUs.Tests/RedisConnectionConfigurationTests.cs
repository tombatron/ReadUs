using System;
using JetBrains.Annotations;
using ReadUs.Exceptions;
using Xunit;

namespace ReadUs.Tests;

[UsedImplicitly]
public class RedisConnectionConfigurationTests
{
    [Fact]
    public void ItCanImplicitlyConvertFromUri()
    {
        var connectionUri = new Uri("redis://localhost:6379?connectionName=Tombatron");

        RedisConnectionConfiguration configuration = connectionUri;

        Assert.Equal("localhost", configuration.ServerAddress);
        Assert.Equal(6379, configuration.ServerPort);
        Assert.Equal("Tombatron", configuration.ConnectionName);
    }

    [Fact]
    public void ItWillThrowAnExceptionIfRedisIsntTheScheme()
    {
        var connectionUri = new Uri("http://www.google.com");

        var exception = Assert.Throws<RedisConnectionConfigurationException>(() =>
        {
            RedisConnectionConfiguration configuration = connectionUri;
        });

        Assert.Equal("The provided scheme `http` is invalid, it must be `redis`.", exception.Message);
    }
}