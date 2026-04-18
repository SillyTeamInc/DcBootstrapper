using System.Diagnostics;
using System.Net.Http.Headers;
using DcBootstrapper.Utils;
using EmniProgress.Backends;
using EmniProgress.Backends.KDE;
using EmniProgress.Factory;

namespace DcBootstrapper;

class Program
{
    static async Task Main(string[] args)
    {
        if (OperatingSystem.IsWindows()) return;

        Console.WriteLine($"DcBootstrapper {Updater.GetCurrentTag()} ({ThisAssembly.Git.Branch}-{ThisAssembly.Git.Commit})");
        ConfigManager.LoadConfig();
        
        if (await Updater.CheckAndUpdateAsync())
        {
            args = args.Append($"--previous-version:{Updater.GetCurrentTag()}").Append($"--updated").ToArray();
            Process.Start(new ProcessStartInfo
            {
                FileName  = Environment.ProcessPath!,
                Arguments = string.Join(' ', args),
                UseShellExecute = false
            });
            return;
        }
        
        if (args.Contains("--updated"))
        {
            NotifyUtil.Notify("Bootstrapper Updated", $"Updated to version {Updater.GetCurrentTag()}!");
        }
        
        var bootstrapper = new Bootstrapper();
        await bootstrapper.RunAsync();
    }
}