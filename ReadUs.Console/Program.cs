using System;
using System.Threading.Tasks;

namespace ReadUs.Console;

internal class Program
{
    private static async Task Main(string[] args)
    {
        var connectionString = new Uri("redis://192.168.86.40:6379?connectionsPerNode=5");

        using var pool = RedisConnectionPool.Create(connectionString);

        // if (db is RedisSingleInstanceDatabase sidb)
        // {
        //     var roleResult = sidb.UnderlyingConnection.Role();
        //     var asyncRoleResult = await sidb.UnderlyingConnection.RoleAsync();

        //     Console.WriteLine($"{roleResult}");
        // }

        // var keyValues = Enumerable.Range(0, 100_000)
        //     .Select(x => new KeyValuePair<RedisKey, string>(Guid.NewGuid().ToString("N"), "whatever"))
        //     .ToArray();

        // var sw = Stopwatch.StartNew();

        // await db.SetMultipleAsync(keyValues);

        // sw.Stop();

        // Console.WriteLine($"Done in {sw.ElapsedMilliseconds}ms");
        System.Console.WriteLine("Hi...");
        do
        {
            while (!System.Console.KeyAvailable)
            {
                var key = Guid.NewGuid().ToString("n");
                var value = Guid.NewGuid().ToString("n");

                System.Console.WriteLine($"Writing Key {key}...");

                try
                {
                    using var db = await pool.GetAsync();

                    await db.SetAsync(key, value);
                    var result = await db.GetAsync(key);
                    
                    System.Console.WriteLine($"Read back {result}");
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine(ex.Message);
                }

                System.Console.WriteLine("Waiting 250ms...");

                await Task.Delay(250);
            }
        } while (System.Console.ReadKey(true).Key != ConsoleKey.Escape);
    }
}