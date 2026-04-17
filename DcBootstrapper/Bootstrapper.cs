using System.Diagnostics;
using System.Formats.Tar;
using DcBootstrapper.Utils;
using EmniProgress.Backends;
using EmniProgress.Backends.KDE;
using EmniProgress.Core;
using EmniProgress.Factory;

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
    
    private static bool _userCancel = true;
    

    public async Task RunAsync()
    {
        try
        {
            Directory.SetCurrentDirectory("/tmp");
            
            // kinda stinky but whatever it works
            await using var fakeProggers = (CompositeProgressBackend)EmniFactory.Create();
            var kde = fakeProggers.GetBackend<KdeProgressBackend>();
            // todo: add functionality to EmniProgress to only use main backend
            IProgressBackend proggers = kde != null ? kde : fakeProggers;
            if (kde != null)
            {
                kde.OnCancel(() =>
                {
                    if (!_userCancel) return Task.CompletedTask;
                    Console.WriteLine("[*] Update cancelled by user.");
                    kde.CancelAsync("Cancelled by user!");
                    Environment.Exit(0);
                    return Task.CompletedTask;
                });
            }
            await proggers.StartAsync("Checking for updates...", "Initializing...", $"Discord {ConfigManager.CurrentConfig?.ProperBranch} Bootstrapper {Updater.GetCurrentTag()}", "update");
            
            await proggers.UpdateAsync(0, "Checking Discord...");
            bool discordUpdated = await SmartDownloadAsync(ConfigManager.CurrentConfig?.DiscordUrl!, _discordTarPath, "Discord");
            
            await proggers.UpdateAsync(0, "Checking Equilotl...");
            bool equilotlUpdated = await SmartDownloadAsync(EquilotlUrl, _equilotlPath, "Equilotl CLI");
            
            await proggers.UpdateAsync(0, "Checking DWIPatcher...");
            bool dwiUpdated = await SmartDownloadAsync(DwiUrl, _dwiPath, "DWIPatcher");
            
            // todo: add options to toggle off patching equicord and dwi
            //       and also add the ability to change discord's launch flags.
            //       would be nice to have.
            //       maybe also custom patches? idfk lol
            if (discordUpdated || !Directory.Exists(_discordAppDir))
            {
                await proggers.UpdateAsync(0, "Extracting Discord...");
                await ExtractDiscord();
                
                await proggers.UpdateAsync(0, "Setting up desktop entry...");
                SetupDesktopEntry();
                
                await proggers.UpdateAsync(0, "Applying equicord patch...");
                PatchWithEquicord();
                
                await proggers.UpdateAsync(0, "Applying DWI patch...");
                PatchWithDwi();
            }
            else
            {
                if (equilotlUpdated || dwiUpdated)
                {
                    await proggers.UpdateAsync(0, "Applying equicord patch...");
                    PatchWithEquicord();
                    await proggers.UpdateAsync(0, "Applying DWI patch...");
                    PatchWithDwi();
                }
            }

            _userCancel = false;
            await proggers.CancelAsync("Download finished");
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
    
    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len} {sizes[order]}";
    }

    private async Task<bool> SmartDownloadAsync(string url, string destination, string displayName)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36");
        string cacheFile = destination + ".meta";
        string newMeta = "";

        Console.Write($"[*] Checking {displayName}... ");

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Head, url);
            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                string? remoteEtag = response.Headers.ETag?.Tag;
                string? remoteLastModified = response.Content.Headers.LastModified?.ToString("R");
                newMeta = remoteEtag ?? remoteLastModified ?? "";

                if (File.Exists(destination) && File.Exists(cacheFile))
                {
                    string cached = await File.ReadAllTextAsync(cacheFile);
                    if (!string.IsNullOrEmpty(newMeta) && cached == newMeta)
                    {
                        Console.WriteLine("Up to date.");
                        return false;
                    }
                }
            }
        }
        catch { /* fallback */ }

        Console.WriteLine($"Downloading update...");

        var downloader = new Downloader(url, destination, isMultithreaded: true);
        await downloader.DownloadFileMultithreaded(Environment.ProcessorCount);

        if (!string.IsNullOrEmpty(newMeta) && File.Exists(destination))
            await File.WriteAllTextAsync(cacheFile, newMeta);

        return File.Exists(destination);
    }

    private async Task ExtractDiscord()
    {
        if (OperatingSystem.IsWindows()) return;

        Console.WriteLine($"[*] Extracting Discord to {_installDir}...");

        if (Directory.Exists(_discordAppDir))
        {
            Console.WriteLine("[!] Removing old installation!");
            Directory.Delete(_discordAppDir, true);
        }
        
        // this is a one-time thing so i'm not making into a util
        await using var tarStream = File.OpenRead(_discordTarPath);
        await using var gzipStream = new System.IO.Compression.GZipStream(tarStream, System.IO.Compression.CompressionMode.Decompress);
        await using var tarReader = new TarReader(gzipStream);
        while (await tarReader.GetNextEntryAsync() is { } entry)
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
        MakeExecutable(_equilotlPath);

        string args = $"-install -location {_discordAppDir}";
        ProcessUtil.RunProcess(_equilotlPath, args, throwOnError: false);
    }

    private void PatchWithDwi()
    {
        Console.WriteLine("[*] Patching with DWIPatcher...");
        MakeExecutable(_dwiPath);

        string resourcesPath = Path.Combine(_discordAppDir, "resources");
        ProcessUtil.RunProcess(_dwiPath, $"\"{resourcesPath}\"", throwOnError: false);
    }

    private void LaunchDiscord()
    {
        string binary = Path.Combine(_discordAppDir, ConfigManager.CurrentConfig?.ExecutableName ?? "DiscordCanary");
        Console.WriteLine($"[*] Launching Discord from {binary}...");

        string cmd =
            CommandExists("kstart6", out string kstart6Path) ? kstart6Path :
            CommandExists("kstart",  out string kstartPath)  ? kstartPath  :
            CommandExists("setsid",  out string setsidPath)  ? setsidPath  :
            throw new Exception("Could not find kstart6, kstart, or setsid.");

        var extraArgs = ConfigManager.CurrentConfig?.LaunchArgs ?? new List<string>();
        string discordArgs = extraArgs.Count > 0 ? string.Join(" ", extraArgs) : "";
        string args = Path.GetFileName(cmd).Contains("kstart")
            ? $"-- {binary} {discordArgs}".TrimEnd()
            : $"{binary} {discordArgs}".TrimEnd();

        var envVars = ConfigManager.CurrentConfig?.EnvVars;
        ProcessUtil.RunProcess(cmd, args, workingDirectory: _discordAppDir, waitForExit: false, envVars: envVars);
    }

    private void SetupDesktopEntry()
    {
        Console.WriteLine("[*] Patching .desktop file...");

        string desktopPath = Path.Combine(_discordAppDir, ConfigManager.CurrentConfig?.DesktopName ?? "discord-canary.desktop");
        string bootstrapperPath =
            Environment.ProcessPath ?? throw new Exception("Could not determine bootstrapper path.");

        if (!File.Exists(desktopPath))
        {
            Console.WriteLine("    [!] Internal .desktop file not found. Creating a fresh one...");
            File.WriteAllText(desktopPath, "[Desktop Entry]\nType=Application\nName=Discord " + ConfigManager.CurrentConfig?.ProperBranch);
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
        MakeExecutable(desktopPath);

        Console.WriteLine($"    Successfully patched grouping for: {desktopPath}");

        string userAppsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "applications");
        string linkPath = Path.Combine(userAppsFolder, "discord-custom.desktop");
        
        if (File.Exists(linkPath))
        {
            try
            {
                // Sometimes this throws an exception even though the file exists and it 
                // really annoys me lol its so intermittent
                Console.WriteLine($"    Removing old desktop entry link at {linkPath}...");
                File.Delete(linkPath);
            } catch (Exception ex)
            {
                Console.WriteLine($"    [ERROR] Failed to remove old desktop entry link: {ex}");
            }
        }
        
        // make sure .local/share/applications exists
        if (!Directory.Exists(userAppsFolder))
        {
            Console.WriteLine($"    Creating user applications folder at {userAppsFolder}...");
            Directory.CreateDirectory(userAppsFolder);
        }
        
        if (ConfigManager.CurrentConfig?.MakeApplicationsSymlink == true) 
            File.CreateSymbolicLink(linkPath, desktopPath);

        Console.WriteLine($"    Desktop entry setup complete. ({linkPath})");
    }

    private bool CommandExists(string command, out string fullPath)
    {
        fullPath = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/which", Arguments = command,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, WorkingDirectory = "/tmp"
            };
            using var process = Process.Start(psi);
            if (process == null) return false;
            fullPath = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && !string.IsNullOrEmpty(fullPath);
        }
        catch { return false; }
    }
    
    private static void MakeExecutable(string path)
    {
        
        const UnixFileMode executableMode =
            UnixFileMode.UserRead  | UnixFileMode.UserWrite  | UnixFileMode.UserExecute  |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;


        File.SetUnixFileMode(path, executableMode);
    }

    
}