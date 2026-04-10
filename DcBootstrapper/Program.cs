using System.Net.Http.Headers;

namespace DcBootstrapper;

class Program
{
    static async Task Main()
    {
        var bootstrapper = new Bootstrapper();
        await bootstrapper.RunAsync();
    }
}