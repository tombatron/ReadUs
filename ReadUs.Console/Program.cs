using System.Diagnostics;
using System.Threading.Tasks;

namespace ReadUs.Console;

internal class Program
{
    private static async Task Main(string[] args)
    {
        Trace.Listeners.Add(new ConsoleTraceListener());
        
        await PubSubScenario.Run();
    }
}