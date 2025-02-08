using System.Threading.Tasks;

namespace ReadUs.Console;

internal class Program
{
    private static async Task Main(string[] args)
    {
        await PubSubScenario.Run();
    }
}