using System;
using System.Threading.Tasks;
using Tombatron.Results;
using Cons = System.Console;

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
        Cons.WriteLine("Hi...");
        
        do
        {
            while (!Cons.KeyAvailable)
            {
                var key = Guid.NewGuid().ToString("n");
                var value = Guid.NewGuid().ToString("n");

                Cons.WriteLine($"Writing Key {key}...");

                var success = false;

                while (!success)
                {
                    using var db = await pool.GetAsync();

                    var setResult = await db.SetAsync(key, value);

                    if (setResult is Error err)
                    {
                        Cons.WriteLine($"There was an error executing the set command: {err.Message}");
                        
                        continue;
                    }

                    if (setResult is Ok)
                    {
                        var result = await db.GetAsync(key);

                        if (result is Ok<string> ok)
                        {
                            success = true;
                            
                            Cons.WriteLine($"Read back {result}");

                            continue;
                        }

                        if (result is Error<string> erro)
                        {
                            Cons.WriteLine($"There was an error executing the get command: {erro.Message}");   
                        }
                    }
                }

                Cons.WriteLine("Waiting 250ms...");

                await Task.Delay(250);
            }
        } while (Cons.ReadKey(true).Key != ConsoleKey.Escape);
    }
}