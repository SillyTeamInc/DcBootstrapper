using System.Diagnostics;
using System.Formats.Tar;

namespace DcBootstrapper;

class Bootstrapper
{
    private const string EquilotlUrl = "https://github.com/Equicord/Equilotl/releases/latest/download/EquilotlCli-linux";

    private const string DwiUrl = "https://github.com/SillyTeamInc/DWIPatcher/releases/download/bleeding-edge/DWIPatcher";

    // app
    private readonly string _installDir;
    private readonly string _discordAppDir;

    // cache
    private readonly string _discordTarPath;
    private readonly string _equilotlPath;
    private readonly string _dwiPath;

    public Bootstrapper()
    {
        ConfigManager.LoadConfig();
        
        var baseDir = ConfigManager.CurrentConfig?.InstallPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiscordCustom");
        var cacheDir = Path.Combine(baseDir, "Cache");
        _installDir = Path.Combine(baseDir, "App");
        
        _discordAppDir = Path.Combine(_installDir, ConfigManager.CurrentConfig?.ExecutableName ?? "DiscordCanary");

        _discordTarPath = Path.Combine(cacheDir, "discord.tar.gz");
        _equilotlPath = Path.Combine(cacheDir, "EquilotlCli-linux");
        _dwiPath = Path.Combine(cacheDir, "DWIPatcher");

        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(_installDir);
        
        Console.WriteLine($"[i] Base directory: {baseDir}");
        Console.WriteLine($"[i] Cache directory: {cacheDir}");
        Console.WriteLine($"[i] Install directory: {_installDir}");
        Console.WriteLine($"[i] Discord .tar.gz path: {_discordTarPath}");
        Console.WriteLine($"[i] Equilotl CLI path: {_equilotlPath}");
        Console.WriteLine($"[i] DWIPatcher path: {_dwiPath}");
        Console.WriteLine($"[c] Config: Branch: " + ConfigManager.CurrentConfig?.DiscordBranch);
        Console.WriteLine($"[c] Config: Install Path: " + ConfigManager.CurrentConfig?.InstallPath);
    }

    public async Task RunAsync()
    {
        try
        {
            
            bool discordUpdated = await SmartDownloadAsync(ConfigManager.CurrentConfig?.DiscordUrl!, _discordTarPath, "Discord");
            bool equilotlUpdated = await SmartDownloadAsync(EquilotlUrl, _equilotlPath, "Equilotl CLI");
            bool dwiUpdated = await SmartDownloadAsync(DwiUrl, _dwiPath, "DWIPatcher");

            if (discordUpdated || !Directory.Exists(_discordAppDir))
            {
                await ExtractDiscord();
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
            catch
            {
                /* fallback */
            }
        }

        Notify("New Update", "Downloading update for " + displayName + "!");
        Console.WriteLine("Downloading update...");
        /*var data = await client.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(destination, data);*/
        // Stream data to the file instead
        await using var responseStream = await client.GetStreamAsync(url);
        await using var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await responseStream.CopyToAsync(fileStream);
        Console.WriteLine(" - Written to " + destination);
        return File.Exists(destination);
    }

    private async Task ExtractDiscord()
    {
        Console.WriteLine($"[*] Extracting Discord to {_installDir}...");

        if (Directory.Exists(_discordAppDir))
        {
            Console.WriteLine("[!] Removing old installation!");
            Directory.Delete(_discordAppDir, true);
        }
        
        // this is a one-time thing so i'm not making into a util
        using var tarStream = File.OpenRead(_discordTarPath);
        using var gzipStream = new System.IO.Compression.GZipStream(tarStream, System.IO.Compression.CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream);
        while (tarReader.GetNextEntry() is TarEntry entry)
        {
            string targetPath = Path.Combine(_installDir, entry.Name);
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(targetPath);
            }
            else if (entry.EntryType == TarEntryType.RegularFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? _installDir);
                await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                try
                {
                    await entry.DataStream?.CopyToAsync(fileStream)!;
                    UnixFileMode mode = entry.Mode;
                    File.SetUnixFileMode(targetPath, mode);
                } catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed to extract {entry.Name}: {ex.Message}");
                }
            }
        }
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
        string binary = Path.Combine(_discordAppDir, ConfigManager.CurrentConfig?.ExecutableName ?? "DiscordCanary");
        Console.WriteLine($"[*] Launching Discord from {binary}...");

        string cmd = CommandExists("kstart6") ? "kstart6" : (CommandExists("kstart") ? "kstart" : "setsid");
        string args = (cmd.Contains("kstart")) ? $"-- {binary}" : binary;

        RunProcess(cmd, args, workingDirectory: _discordAppDir, waitForExit: false);
    }

    private void SetupDesktopEntry()
    {
        Console.WriteLine("[*] Patching .desktop file...");

        string desktopPath = Path.Combine(_discordAppDir, ConfigManager.CurrentConfig?.DesktopName ?? "discord-canary.desktop");
        string iconPath = Path.Combine(_discordAppDir, "discord.png");
        string bootstrapperPath =
            Environment.ProcessPath ?? throw new Exception("Could not determine bootstrapper path.");

        if (!File.Exists(desktopPath))
        {
            Console.WriteLine("    [!] Internal .desktop file not found. Creating a fresh one...");
            File.WriteAllText(desktopPath, "[Desktop Entry]\nType=Application\nName=Discord " + ConfigManager.CurrentConfig?.ProperBranch);
            Notify("Notice", "Internal .desktop file was missing. A new one has been created, but it may not be perfect. Consider reinstalling if you encounter issues with the desktop entry.");
        }

        var lines = File.ReadAllLines(desktopPath).ToList();

        var updates = new Dictionary<string, string>
        {
            { "Exec", $"\"{bootstrapperPath}\"" },
            { "Path", _discordAppDir },
            { "StartupWMClass", ConfigManager.CurrentConfig?.WmName ?? "discord-canary" },
            { "Comment", "Discord " + ConfigManager.CurrentConfig?.ProperBranch + " patched with Equicord and the Wayland Idle fix."  }
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

        string userAppsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "applications");
        string linkPath = Path.Combine(userAppsFolder, "discord-custom.desktop");

        if (File.Exists(linkPath))
        {
            Console.WriteLine($"    Removing old desktop entry link at {linkPath}...");
            File.Delete(linkPath);
        }
        
        // make sure .local/share/applications exists
        if (!Directory.Exists(userAppsFolder))
        {
            Console.WriteLine($"    Creating user applications folder at {userAppsFolder}...");
            Directory.CreateDirectory(userAppsFolder);
        }
        
        RunProcess("ln", $"-sf \"{desktopPath}\" \"{linkPath}\"");

        Console.WriteLine($"    Desktop entry setup complete. ({linkPath})");
    }

    private bool CommandExists(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
                { FileName = "which", Arguments = command, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Notify(string title, string body)
    {
        RunProcess("notify-send", $"-u normal \"{title}\" \"{body}\" --app-name \"Discord {ConfigManager.CurrentConfig?.ProperBranch} Bootstrapper\"", notify: false);
    }

    private int RunProcess(string fileName, string args = "", string workingDirectory = "", bool waitForExit = true,
        bool throwOnError = true, bool notify = true)

    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (notify) Console.WriteLine($"[i] Running: {fileName} {args}");

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