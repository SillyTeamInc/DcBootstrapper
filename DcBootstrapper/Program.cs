using System.Diagnostics;
using System.Net.Http.Headers;

namespace DcBootstrapper;

class Program
{
    static async Task Main()
    {
        Console.WriteLine("🌙 Starting Discord Canary + Equicord Bootstrapper...");
        var bootstrapper = new Bootstrapper();
        await bootstrapper.RunAsync();
    }
}

class Bootstrapper
{
    private const string DiscordUrl = "https://discord.com/api/download/canary?platform=linux&format=tar.gz";
    private const string EquilotlUrl = "https://github.com/Equicord/Equilotl/releases/latest/download/EquilotlCli-linux";
    private const string DwiUrl = "https://github.com/SillyTeamInc/DWIPatcher/releases/download/bleeding-edge/DWIPatcher";

    private readonly string _baseDir;
    private readonly string _cacheDir;
    private readonly string _installDir;
    
    private readonly string _discordTarPath;
    private readonly string _equilotlPath;
    private readonly string _dwiPath;
    private readonly string _discordAppDir;

    public Bootstrapper()
    {
        _baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordCanaryCustom");
        _cacheDir = Path.Combine(_baseDir, "Cache");
        _installDir = Path.Combine(_baseDir, "App");
        
        _discordAppDir = Path.Combine(_installDir, "DiscordCanary");
        
        _discordTarPath = Path.Combine(_cacheDir, "discord.tar.gz");
        _equilotlPath = Path.Combine(_cacheDir, "EquilotlCli-linux");
        _dwiPath = Path.Combine(_cacheDir, "DWIPatcher");

        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(_installDir);
    }

    public async Task RunAsync()
    {
        try
        {
            bool discordUpdated = await SmartDownloadAsync(DiscordUrl, _discordTarPath, "Discord Canary");
            bool equilotlUpdated = await SmartDownloadAsync(EquilotlUrl, _equilotlPath, "Equilotl CLI");
            bool dwiUpdated = await SmartDownloadAsync(DwiUrl, _dwiPath, "DWIPatcher");

            if (discordUpdated || !Directory.Exists(_discordAppDir))
            {
                ExtractDiscord();
                SetupDesktopEntry();
                PatchWithEquicord();
                PatchWithDwi();
            }
            else
            {
                if (equilotlUpdated || dwiUpdated)
                {
                    PatchWithEquicord();
                    PatchWithDwi();
                }
                
            }

            LaunchDiscord();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[ERROR] {ex.Message}");
            Console.WriteLine(ex.ToString());
            Console.ResetColor();
        }
    }

    private async Task<bool> SmartDownloadAsync(string url, string destination, string displayName)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36");

        Console.Write($"[*] Checking {displayName}... ");

        if (File.Exists(destination))
        {
            try 
            {
                var request = new HttpRequestMessage(HttpMethod.Head, url);
                var response = await client.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    long? remoteSize = response.Content.Headers.ContentLength;
                    long localSize = new FileInfo(destination).Length;

                    if (remoteSize.HasValue && remoteSize.Value == localSize)
                    {
                        Console.WriteLine("Up to date.");
                        return false;
                    }
                }
            }
            catch { /* fallback */ }
        }

        Notify("New Update", "Downloading update for " + displayName + "!");
        Console.WriteLine("Downloading update...");
        var data = await client.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(destination, data);
        return true;
    }

    private void ExtractDiscord()
    {
        Console.WriteLine($"[*] Extracting Discord to {_installDir}...");
        
        if (Directory.Exists(_discordAppDir))
        {
            Console.WriteLine("[!] Removing old installation!");
            Directory.Delete(_discordAppDir, true);
        }

        RunProcess("tar", $"-xzf {_discordTarPath} -C {_installDir}");
    }

    private void PatchWithEquicord()
    {
        Console.WriteLine("[*] Patching with Equilotl...");
        RunProcess("chmod", $"+x {_equilotlPath}");

        string args = $"-install -location {_discordAppDir}";
        RunProcess(_equilotlPath, args, throwOnError: false);
    }

    private void PatchWithDwi()
    {
        Console.WriteLine("[*] Patching with DWIPatcher...");
        RunProcess("chmod", $"+x {_dwiPath}");

        string resourcesPath = Path.Combine(_discordAppDir, "resources");
        RunProcess(_dwiPath, $"\"{resourcesPath}\"", throwOnError: false);
    }

    private void LaunchDiscord()
    {
        string binary = Path.Combine(_discordAppDir, "DiscordCanary");
        Console.WriteLine($"[*] Launching Discord Canary...");

        string cmd = CommandExists("kstart6") ? "kstart6" : (CommandExists("kstart") ? "kstart" : "setsid");
        string args = (cmd.Contains("kstart")) ? $"-- {binary}" : binary;

        RunProcess(cmd, args, workingDirectory: _discordAppDir, waitForExit: false);
    }
    
    private void SetupDesktopEntry()
    {
        Console.WriteLine("[*] Patching .desktop file...");

        string desktopPath = Path.Combine(_discordAppDir, "discord-canary.desktop");
        string iconPath = Path.Combine(_discordAppDir, "discord.png");
        string bootstrapperPath = Environment.ProcessPath ?? throw new Exception("Could not determine bootstrapper path.");

        if (!File.Exists(desktopPath))
        {
            Console.WriteLine("    [!] Internal .desktop file not found. Creating a fresh one...");
            File.WriteAllText(desktopPath, "[Desktop Entry]\nType=Application\nName=Discord Canary");
        }

        var lines = File.ReadAllLines(desktopPath).ToList();
    
        var updates = new Dictionary<string, string>
        {
            { "Exec", $"\"{bootstrapperPath}\"" },
            { "Path", _discordAppDir },
            { "StartupWMClass", "discord-canary" }, 
            { "Comment", "Discord Canary patched with Equicord and DWI" }
        };

        for (int i = 0; i < lines.Count; i++)
        {
            foreach (var key in updates.Keys)
            {
                if (lines[i].StartsWith($"{key}="))
                {
                    lines[i] = $"{key}={updates[key]}";
                    updates.Remove(key);
                    break;
                }
            }
        }

        foreach (var remaining in updates)
        {
            lines.Add($"{remaining.Key}={remaining.Value}");
        }

        File.WriteAllLines(desktopPath, lines);
        RunProcess("chmod", $"+x \"{desktopPath}\"");
    
        Console.WriteLine($"    Successfully patched grouping for: {desktopPath}");
        
        string userAppsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applications");
        string linkPath = Path.Combine(userAppsFolder, "discord-canary-custom.desktop");

        if (!File.Exists(linkPath))
        {
            RunProcess("ln", $"-sf \"{desktopPath}\" \"{linkPath}\"");
        }
        
        Console.WriteLine($"    Desktop entry setup complete. ({linkPath})");
    }

    private bool CommandExists(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo { FileName = "which", Arguments = command, UseShellExecute = false, CreateNoWindow = true });
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch { return false; }
    }

    public void Notify(string title, string body)
    {
        RunProcess("notify-send", $"-u normal \"{title}\" \"{body}\" --app-name \"Discord Canary Bootstrapper\"");
    }

    private int RunProcess(string fileName, string args = "", string workingDirectory = "", bool waitForExit = true, bool throwOnError = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) throw new Exception($"Failed to start: {fileName}");

        if (waitForExit)
        {
            proc.WaitForExit();
            if (throwOnError && proc.ExitCode != 0) throw new Exception($"{fileName} failed: {proc.ExitCode}");
            return proc.ExitCode;
        }
        return 0;
    }
}