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
            var pool = new RedisClusterCommandsPool("tombaserver.local", 7000);

            var db = await pool.GetAsync();

            var keyValues = Enumerable.Range(0, 100_000).Select(x=> new KeyValuePair<RedisKey, string>(Guid.NewGuid().ToString("N"), "whatever"));

            var sw = Stopwatch.StartNew();

            await db.SetMultipleAsync(keyValues.ToArray());

            sw.Stop();

            Console.WriteLine($"Done in {sw.ElapsedMilliseconds}ms");
        }
    }
}
