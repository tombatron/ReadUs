using System;
using System.Threading.Tasks;
using ReadUs.Commands;
using Cons = System.Console;

namespace ReadUs.Console;

public static class Playground
{
    public static async Task Run()
    {
        var connectionString = new Uri("redis://localhost:6379");

        var testKey = Guid.NewGuid().ToString("N");

        var pool = RedisConnectionPool.Create(connectionString);
        
        var commands = await pool.GetDatabase();

        await commands.LeftPush(testKey, ["Never eat soggy waffles."]);

        Cons.WriteLine(await commands.ListLength(testKey));
    }
}