using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text.Json;
using DcBootstrapper.Utils;
using EmniProgress.Backends;
using EmniProgress.Core;
using Microsoft.Data.Sqlite;

namespace DcBootstrapper.Discord;

// note: this was made by just... reversing discord's new rust updater lmfao
// it's pretty good but i hate myself :3
// discord is bald as fuck for doing this 
public class DiscordUpdater
{
    private const string ManifestUrl = "https://updates.discord.com/distributions/app/manifests/latest";
    private const string UserAgent = "Discord-Updater/1";

    private readonly string _installDirectory;
    private readonly string _cacheDir;
    private readonly string _versionFile;
    private readonly string _installId;
    public static string DiscordAppDir = "";

    public DiscordUpdater(string installDir, string cacheDir)
    {
        _installDirectory = installDir;
        _cacheDir = cacheDir;
        _versionFile = Path.Combine(cacheDir, "discord_version.json");

        string idFile = Path.Combine(cacheDir, "install_id");
        if (!File.Exists(idFile))
            File.WriteAllText(idFile, Guid.NewGuid().ToString());
        _installId = File.ReadAllText(idFile).Trim();
    }

    public async Task<bool> UpdateAsync(IProgressBackend balls)
    {
        var manifest = await FetchManifestAsync();
        var state = LoadVersionState();

        string latestVersion = manifest.Full.VersionString;
        Console.WriteLine($"[*] Discord latest: {latestVersion}, installed: {state.HostVersion ?? "none"}");
        
        if (state.HostVersion != null && !ConfigManager.IsBreakingVersion(Program.CurrentBreakingVersion))
        {
            ConfigManager.SetBreakingVersion(Program.CurrentBreakingVersion);
            Console.WriteLine($"[*] Detected breaking update #{latestVersion}, clearing old versions...");
            
            string[] dirsToDelete =
            [
                Path.Combine(_installDirectory, $"app-{state.HostVersion}"),
                Path.Combine(_installDirectory, state.HostVersion),
                Bootstrapper.OldAppDir ?? ""
            ];
            
            foreach (var dir in dirsToDelete)
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Console.WriteLine($"    Removing {dir}...");
                    try
                    {
                        // i'm paranoid
                        if (dir.Contains("app-") || dir.Contains("discord") || dir.Contains("App"))
                            Directory.Delete(dir, true);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    Failed to remove {dir}: {ex.Message}");
                    }
                }
            }
            
            string userAppsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "applications");
            string linkPath = Path.Combine(userAppsFolder, "discord-custom.desktop");
            
            if (File.Exists(linkPath) || Directory.Exists(linkPath))
            {
                Console.WriteLine($"    Removing old symlink at {linkPath}...");
                try
                {
                    File.Delete(linkPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    Failed to remove old symlink: {ex.Message}");
                }
            }
            
            state = new InstallState();
        }

        string symPath = Path.Combine(_installDirectory, $"app-{latestVersion}");
        DiscordAppDir = symPath;
        Console.WriteLine($"[*] Discord app path: {DiscordAppDir}");

        bool hostUpdated = false;

        if (state.HostVersion != latestVersion)
        {
            string oldAppDir = Path.Combine(_installDirectory, $"app-{state.HostVersion}");
            if (Directory.Exists(oldAppDir))     
            {
                Console.WriteLine($"[*] Removing old Discord host at {oldAppDir}...");
                try
                {
                    // i'm paranoid
                    if (oldAppDir.Contains("app-"))
                        Directory.Delete(oldAppDir, true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    Failed to remove old host: {ex.Message}");
                }
            } else
            {
                Console.WriteLine($"[*] No existing host found at {oldAppDir}? Skipping removal!!");
            }
            
            await balls.UpdateAsync(0, "Downloading full Discord host...");
            Console.WriteLine($"[*] Downloading full Discord host {latestVersion}...");
            string distroPath = Path.Combine(_cacheDir, "discord_host.distro");
            await DownloadAndVerifyAsync(manifest.Full.Url, distroPath, manifest.Full.Sha256, "Discord host", false);
            await ExtractDistroAsync(distroPath, DiscordAppDir);
            state.HostVersion = latestVersion;
            hostUpdated = true;
        }

        string[] modulesToDownload = ConfigManager.InsertModules(manifest.RequiredModules.ToArray());
        string[] allAvailableModules = manifest.Modules.Keys.ToArray();
        ConfigManager.CurrentConfig!.AvailableModules = allAvailableModules;
        ConfigManager.SaveConfig();

        Console.WriteLine($"[*] Required modules: {string.Join(", ", manifest.RequiredModules)}");
        Console.WriteLine($"[*] Modules to download (after config): {string.Join(", ", modulesToDownload)}");

        // 1. Setup the concurrency limit
        bool multiDownloadModules = ConfigManager.CurrentConfig?.MultiDownloadModules ?? false;
        int maxConcurrentDownloads = ConfigManager.CurrentConfig?.MaxConcurrentDownloads ?? 3;
        int totalModules = modulesToDownload.Length;
        int completedCount = 0;
        SemaphoreSlim semaphore = new SemaphoreSlim(multiDownloadModules ? maxConcurrentDownloads : 1);
        
        var downloadTasks = modulesToDownload.Select(async moduleName =>
        {
            await semaphore.WaitAsync();
            try
            {
                if (!manifest.Modules.TryGetValue(moduleName, out var moduleInfo))
                {
                    Console.WriteLine($"    Skipping module {moduleName} (not found in manifest)");
                    return;
                }

                var pkg = moduleInfo.Full;
                string moduleKey = $"{pkg.HostVersion[0]}.{pkg.HostVersion[1]}.{pkg.HostVersion[2]}_{pkg.ModuleVersion}";

                lock (state.ModuleKeys)
                {
                    if (state.ModuleKeys.TryGetValue(moduleName, out string? installedKey) && installedKey == moduleKey)
                        return;
                }

                Console.WriteLine($"[*] Downloading module {moduleName} v{pkg.ModuleVersion}...");

                string modulePath = Path.Combine(_cacheDir, $"{moduleName}.distro");
                await DownloadAndVerifyAsync(pkg.Url, modulePath, pkg.Sha256, moduleName, multiDownloadModules);

                string moduleInstallDir = Path.Combine(DiscordAppDir, "modules", $"{moduleName}-{pkg.ModuleVersion}", moduleName);
                Directory.CreateDirectory(moduleInstallDir);
                await ExtractDistroAsync(modulePath, moduleInstallDir);

                lock (state.ModuleKeys)
                {
                    state.ModuleKeys[moduleName] = moduleKey;
                }
                
                int current = Interlocked.Increment(ref completedCount);
                await balls.UpdateAsync(0, $"Updated {current}/{totalModules} modules...");
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(downloadTasks);

        SaveVersionState(state);
        await balls.UpdateAsync(0, "Updating database...");
        RecreateInstallerDb(ConfigManager.CurrentConfig?.DiscordBranch ?? "stable", latestVersion,
            JsonSerializer.Serialize(manifest));

        return hostUpdated;
    }

    public static string GetLatestAppPath()
    {
        return DiscordAppDir != ""
            ? DiscordAppDir
            : throw new Exception("Discord has not been updated yet, app path is not available.");
    }

    private void RecreateInstallerDb(string branch, string versionStr, string rawManifestJson)
    {
        string dbPath = Path.Combine(_installDirectory, "installer.db");
        Directory.CreateDirectory(_installDirectory);

        var versionParts = versionStr.Split('.').Select(int.Parse).ToArray();

        var hostManifest = new DbManifest();
        foreach (var file in Directory.GetFiles(DiscordAppDir, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(DiscordAppDir, file);

            hostManifest.Files[relativePath] = new DbFileEntry { New = new DbHash { Sha256 = ComputeSha256(file) } };
        }

        var installedState = new[]
        {
            new
            {
                host_version = new
                {
                    host = new { name = "app", release_channel = branch.ToLower(), platform = "linux", arch = "x64" },
                    version = versionParts
                },
                install_state = "Installed",
                distro_manifest = hostManifest,
                modules = ConfigManager.CurrentConfig!.AvailableModules.Select(mod => new
                {
                    module_version = new
                    {
                        module = new
                        {
                            host_version = new
                            {
                                host = new { name = "app", release_channel = branch, platform = "linux", arch = "x64" },
                                version = versionParts
                            },
                            name = mod
                        },
                        version = 1
                    },
                    distro_manifest = new DbManifest(),
                    install_state = "Installed"
                }).ToArray()
            }
        };

        string installedStateJson = JsonSerializer.Serialize(installedState);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS key_values (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL); DELETE FROM key_values;";
        command.ExecuteNonQuery();

        command.CommandText =
            "INSERT INTO key_values (key, value) VALUES ('install_id', @id), (@hostKey, @hostVal), (@latestKey, @latestVal);";
        command.Parameters.AddWithValue("@id", $"\"{_installId}\"");
        command.Parameters.AddWithValue("@hostKey", $"host/app/{branch}/linux/x64");
        command.Parameters.AddWithValue("@hostVal", installedStateJson);
        command.Parameters.AddWithValue("@latestKey", $"latest/host/app/{branch}/linux/x64");
        command.Parameters.AddWithValue("@latestVal", rawManifestJson);
        command.ExecuteNonQuery();
    }


    public static async Task<bool> IsDistroAvailableAsync(string channel)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.Timeout = TimeSpan.FromSeconds(5);

            string url =
                $"{ManifestUrl}?install_id=00000000-0000-0000-0000-000000000000&channel={channel}&platform=linux&arch=x64&platform_version=1.0.0";
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode) return false;

            var body = await response.Content.ReadAsStringAsync();
            var manifest = JsonSerializer.Deserialize<DiscordManifest>(body);
            return manifest?.Full?.Url is not null && manifest.Full.Url.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<DiscordManifest> FetchManifestAsync()
    {
        string platformVersion = GetPlatformVersion();
        string url =
            $"{ManifestUrl}?install_id={_installId}&channel={ConfigManager.CurrentConfig?.DiscordBranch?.ToLower()}" +
            $"&platform=linux&arch=x64&platform_version={platformVersion}";

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<DiscordManifest>(json)
               ?? throw new Exception("Failed to deserialize Discord manifest.");
    }

    private async Task DownloadAndVerifyAsync(string url, string destination, string expectedSha256, string displayName, bool isConcurrent)
    {
        var downloader = new Downloader(url, destination, isMultithreaded: true, isConcurrent: isConcurrent);
        await downloader.DownloadFileMultithreaded();

        Console.Write($"    Verifying {displayName}... ");
        string actual = ComputeSha256(destination);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destination);
            throw new Exception(
                $"SHA256 mismatch for {displayName}.\n  Expected: {expectedSha256}\n  Got:      {actual}");
        }

        Console.WriteLine("OK");
    }

    private static async Task ExtractDistroAsync(string distroPath, string targetDir)
    {
        Console.WriteLine($"    Extracting to {targetDir}...");
        Directory.CreateDirectory(targetDir);

        await using var fileStream = File.OpenRead(distroPath);
        await using var brotliStream = new BrotliStream(fileStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(brotliStream);

        while (await tarReader.GetNextEntryAsync() is { } entry)
        {
            string entryName = entry.Name.StartsWith("files/")
                ? entry.Name["files/".Length..]
                : entry.Name;

            if (string.IsNullOrEmpty(entryName)) continue;

            string targetPath = Path.Combine(targetDir, entryName);

            if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(targetPath);
            else if (entry.EntryType == TarEntryType.RegularFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? targetDir);
                await using var outStream =
                    new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await entry.DataStream?.CopyToAsync(outStream)!;
                File.SetUnixFileMode(targetPath, entry.Mode);
            }
        }
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string GetPlatformVersion()
    {
        try
        {
            var lines = File.ReadAllLines("/etc/os-release");
            var id = lines.FirstOrDefault(l => l.StartsWith("VERSION_ID="))
                ?.Split('=')[1].Trim('"');
            return id ?? "1.0.0";
        }
        catch
        {
            return "1.0.0";
        }
    }

    private InstallState LoadVersionState()
    {
        if (!File.Exists(_versionFile)) return new InstallState();
        try
        {
            return JsonSerializer.Deserialize<InstallState>(File.ReadAllText(_versionFile))
                   ?? new InstallState();
        }
        catch
        {
            return new InstallState();
        }
    }

    private void SaveVersionState(InstallState state) => File.WriteAllText(_versionFile,
        JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

    private class InstallState
    {
        [JsonPropertyName("host_version")] public string? HostVersion { get; set; }
        [JsonPropertyName("module_keys")] public Dictionary<string, string> ModuleKeys { get; set; } = new();
    }
}