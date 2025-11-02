using System;
using System.Threading.Tasks;
using ReadUs.Commands;
using Cons = System.Console;

namespace ReadUs.Console;

public static class PubSubScenario
{
    public static async Task Run()
    {
        var connectionString = new Uri("redis://localhost:6379");

        using var pool = RedisConnectionPool.Create(connectionString);

        var db = pool.GetDatabase();

        Cons.WriteLine("Subscribing to `test_channel`.");
        await db.Subscribe("test_channel", MessageReceived);
        
        do
        {
            while (!Cons.KeyAvailable)
            {
                var value = Guid.NewGuid().ToString("n");

                Cons.WriteLine($"Publishing `{value}` to 'test_channel'.");
                await db.Publish("test_channel", $"Sending Value: {value}");

                await Task.Delay(2500);
            }
        } while (Cons.ReadKey(true).Key != ConsoleKey.Escape);
    }

    static void MessageReceived(string message)
    {
        Cons.WriteLine($"Received message: {message}");
    }
}