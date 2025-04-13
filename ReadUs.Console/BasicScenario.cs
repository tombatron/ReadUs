using System;
using System.Threading.Tasks;
using Tombatron.Results;
using Cons = System.Console;

namespace ReadUs.Console;

public static class BasicScenario
{
    public static async Task Run()
    {
        var connectionString = new Uri("redis://192.168.86.40:6379?connectionsPerNode=5");

        using var pool = RedisConnectionPool.Create(connectionString);

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