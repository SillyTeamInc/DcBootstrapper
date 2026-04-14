using System.Diagnostics;
using System.Net.Http.Headers;
using DcBootstrapper.Utils;

namespace DcBootstrapper;

class Program
{
    static async Task Main(string[] args)
    {
        // !!! FUCK YOU CA1416
        if (OperatingSystem.IsWindows()) return;

        Console.WriteLine($"DcBootstrapper {Updater.GetCurrentTag()} ({ThisAssembly.Git.Branch}-{ThisAssembly.Git.Commit})");
        ConfigManager.LoadConfig();
        
        if (await Updater.CheckAndUpdateAsync())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName  = Environment.ProcessPath!,
                Arguments = string.Join(' ', args),
                UseShellExecute = false
            });
            return;
        }

        if (Debugger.IsAttached) Debugger.Break();
        var bootstrapper = new Bootstrapper();
        await bootstrapper.RunAsync();
    }
}