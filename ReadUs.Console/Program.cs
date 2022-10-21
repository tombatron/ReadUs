using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace ReadUs.Console
{
    using System;

    class Program
    {
        static async Task Main(string[] args)
        {
            var connectionString = new Uri("redis://192.168.86.40:7000?connectionsPerNode=5");

            using var pool = RedisConnectionPool.Create(connectionString);

            using var db = await pool.GetAsync();

            if (db is RedisSingleInstanceDatabase sidb)
            {
                var roleResult = sidb.UnderlyingConnection.Role();
                var asyncRoleResult = await sidb.UnderlyingConnection.RoleAsync();

                Console.WriteLine($"{roleResult}");
            }

            var keyValues = Enumerable.Range(0, 100_000)
                .Select(x => new KeyValuePair<RedisKey, string>(Guid.NewGuid().ToString("N"), "whatever"))
                .ToArray();

            var sw = Stopwatch.StartNew();

            await db.SetMultipleAsync(keyValues);

            sw.Stop();

            Console.WriteLine($"Done in {sw.ElapsedMilliseconds}ms");
        }
    }
}
