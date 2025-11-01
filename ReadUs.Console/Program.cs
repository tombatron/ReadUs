using System.Diagnostics;
using System.Threading.Tasks;

namespace ReadUs.Console;

internal class Program
{
    private static async Task Main(string[] args)
    {
        Trace.Listeners.Add(new ConsoleTraceListener());
        
        // Start the chaos runner which will bring up a local redis cluster (via docker compose)
        // and then exercise the client while randomly stopping/starting nodes.
        await ChaosRunner.Run();
        // await Playground.Run();
    }
}