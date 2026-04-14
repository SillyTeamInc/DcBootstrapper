using System.Net.Http.Headers;

namespace DcBootstrapper;

class Program
{
    static async Task Main()
    {
        // !!! FUCK YOU CA1416
        if (OperatingSystem.IsWindows()) return;
        

        var bootstrapper = new Bootstrapper();
        await bootstrapper.RunAsync();
    }
}