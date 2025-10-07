using System;
using System.Threading.Tasks;
using ReadUs.Commands;
using Tombatron.Results;
using Cons = System.Console;

namespace ReadUs.Console;

public static class BasicScenario
{
    public static async Task Run()
    {
        var connectionString = new Uri("redis://localhost:6379");

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
                    var db = await pool.GetDatabase();

                    var setResult = await db.Set(key, value);

                    if (setResult is Error err)
                    {
                        Cons.WriteLine($"There was an error executing the set command: {err.Message}");
                        
                        continue;
                    }

                    if (setResult is Ok)
                    {
                        var result = await db.Get(key);

                        if (result is Ok<string> ok)
                        {
                            success = true;
                            
                            Cons.WriteLine($"Read back key `{key}`: {ok.Value}");

                            continue;
                        }

                        if (result is Error<string> erro)
                        {
                            Cons.WriteLine($"There was an error executing the get command: {erro.Message}");   
                        }
                    }
                }

                Cons.WriteLine("Waiting 250ms...");

                await Task.Delay(2500);
            }
        } while (Cons.ReadKey(true).Key != ConsoleKey.Escape);
    }
}