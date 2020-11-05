using System;
using System.Threading.Tasks;
using Xunit;

namespace ReadUs.Tests.Integration
{
    public sealed class RedisCommandsTests : IDisposable
    {
        private readonly RedisCommandsPool _pool;

        public RedisCommandsTests()
        {
            _pool = new RedisCommandsPool("::1", 6379);
        }

        [Fact]
        public async Task Select_Changes_Database()
        {
            var testKey = Guid.NewGuid().ToString("N");

            using var commands = await _pool.GetAsync();

            await commands.SelectAsync(10);

            await commands.SetAsync(testKey, "Hello World");

            await commands.SelectAsync(0);

            await commands.SetAsync(testKey, "Goodnight Moon");

            await commands.SelectAsync(10);

            var databaseTenValue = await commands.GetAsync(testKey);

            await commands.SelectAsync(0);

            var databaseZeroValue = await commands.GetAsync(testKey);

            Assert.Equal("Hello World", databaseTenValue);
            Assert.Equal("Goodnight Moon", databaseZeroValue);
        }

        [Fact]
        public async Task Get_Retrieves_Value()
        {
            var testKey = Guid.NewGuid().ToString("N");

            using var commands = await _pool.GetAsync();

            await commands.SetAsync(testKey, "The quick brown fox jumped over the lazy moon.");

            var retrievedValue = await commands.GetAsync(testKey);

            Assert.Equal("The quick brown fox jumped over the lazy moon.", retrievedValue);
        }

        [Fact]
        public async Task Set_Assigns_Value()
        {
            var testKey = Guid.NewGuid().ToString("N");

            using var commands = await _pool.GetAsync();

            await commands.SetAsync(testKey, "Never eat soggy waffles.");

            var retrievedValue = await commands.GetAsync(testKey);

            Assert.Equal("Never eat soggy waffles.", retrievedValue);
        }

        [Fact]
        public async Task Llen_Gets_List_Length()
        {
            var testKey = Guid.NewGuid().ToString("N");

            using var commands = await _pool.GetAsync();

            var initialLength = await commands.LlenAsync(testKey);

            await commands.LPushAsync(testKey, "Yo");

            var finalLength = await commands.LlenAsync(testKey);

            Assert.Equal(0, initialLength);
            Assert.Equal(1, finalLength);
        }

        public void Dispose()
        {
            _pool.Dispose();
        }
    }
}
