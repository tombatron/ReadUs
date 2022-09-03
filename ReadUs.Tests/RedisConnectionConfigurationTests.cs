using ReadUs.Exceptions;
using System;
using Xunit;

namespace ReadUs.Tests
{
    public class RedisConnectionConfigurationTests
    {
        [Fact]
        public void ItCanImplicitlyConvertFromUri()
        {
            var connectionUri = new Uri("redis://localhost:6379?connectionsPerNode=10");

            RedisConnectionConfiguration configuration = connectionUri;

            Assert.Equal("localhost", configuration.ServerAddress);
            Assert.Equal(6379, configuration.ServerPort);
            Assert.Equal(10, configuration.ConnectionsPerNode);
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

        [Fact]
        public void ItWillThrowAnExceptionIfConnectionsPerNodeIsntAValidInteger()
        {
            var connectionUri = new Uri("redis://localhost?connectionsPerNode=whatever");

            var exception = Assert.Throws<RedisConnectionConfigurationException>(() =>
            {
                RedisConnectionConfiguration configuration = connectionUri;
            });

            Assert.Equal("`connectionsPerNode` must be a valid integer.", exception.Message);
        }
    }
}