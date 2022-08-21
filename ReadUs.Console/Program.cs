
using System.Threading.Tasks;

namespace ReadUs.Console
{
    using System;

    class Program
    {
        static async Task Main(string[] args)
        {
            var pool = new RedisClusterCommandsPool("tombaserver.local", 7000);

            var db = await pool.GetAsync();

            await db.SetAsync("hello", "world");
            await db.SetAsync("goodnight", "moon");

            for(var i = 0; i < 10_000; i++)
            {
                var key = Guid.NewGuid().ToString("n")[0..10];

                await db.SetAsync(key, ".");

                Console.WriteLine($"Wrote: {key}");
            }
        }
    }
}
