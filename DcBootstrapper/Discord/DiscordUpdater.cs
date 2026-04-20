using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text.Json;
using DcBootstrapper.Utils;
using EmniProgress.Backends;
using EmniProgress.Core;

namespace DcBootstrapper.Discord;

// note: this was made by just... reversing discord's new rust updater lmfao
// it's pretty good but i hate myself :3
// discord is bald as fuck for doing this 
public class DiscordUpdater
{
    private const string ManifestUrl = "https://updates.discord.com/distributions/app/manifests/latest";
    private const string UserAgent = "Discord-Updater/1";

    private readonly string _installDir;
    private readonly string _cacheDir;
    private readonly string _versionFile;
    private readonly string _installId;

    public DiscordUpdater(string installDir, string cacheDir)
    {
        _installDir = installDir;
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
        _currentVersion = latestVersion;
        Console.WriteLine($"[*] Discord latest: {latestVersion}, installed: {state.HostVersion ?? "none"}");

        bool hostUpdated = false;

        if (state.HostVersion != latestVersion)
        {
            await balls.UpdateAsync(0, "Downloading full Discord host...");
            Console.WriteLine($"[*] Downloading full Discord host {latestVersion}...");
            string distroPath = Path.Combine(_cacheDir, "discord_host.distro");
            await DownloadAndVerifyAsync(manifest.Full.Url, distroPath, manifest.Full.Sha256, "Discord host");
            await ExtractDistroAsync(distroPath, _installDir);
            state.HostVersion = latestVersion;
            hostUpdated = true;
        }

        string moduleDir = GetModuleInstallDir();
        Directory.CreateDirectory(moduleDir);
        
        string[] modulesToDownload = ConfigManager.InsertModules(manifest.RequiredModules.ToArray());
        string[] allAvailableModules = manifest.Modules.Keys.ToArray();
        ConfigManager.CurrentConfig!.AvailableModules = allAvailableModules;
        ConfigManager.SaveConfig();
        
        Console.WriteLine($"[*] Required modules: {string.Join(", ", manifest.RequiredModules)}");
        Console.WriteLine($"[*] Modules to download (after config): {string.Join(", ", modulesToDownload)}");
        
        foreach (var moduleName in modulesToDownload)
        {
            if (!manifest.Modules.TryGetValue(moduleName, out var moduleInfo))
            {
                Console.WriteLine($"    Skipping module {moduleName} (not found in manifest)");
                continue;
            }

            var pkg = moduleInfo.Full;
            string moduleKey = $"{pkg.HostVersion[0]}.{pkg.HostVersion[1]}.{pkg.HostVersion[2]}_{pkg.ModuleVersion}";

            if (state.ModuleKeys.TryGetValue(moduleName, out string? installedKey) && installedKey == moduleKey)
                continue;

            Console.WriteLine($"[*] Downloading module {moduleName} v{pkg.ModuleVersion}...");
            await balls.UpdateAsync(0, "Downloading module " + moduleName + "...");
            string modulePath = Path.Combine(_cacheDir, $"{moduleName}.distro");
            await DownloadAndVerifyAsync(pkg.Url, modulePath, pkg.Sha256, moduleName);

            string moduleInstallDir = Path.Combine(moduleDir, moduleName);
            Directory.CreateDirectory(moduleInstallDir);
            await ExtractDistroAsync(modulePath, moduleInstallDir);
            state.ModuleKeys[moduleName] = moduleKey;
        }

        await WriteInstalledJsonAsync(moduleDir, manifest);

        SaveVersionState(state);
        return hostUpdated;
    }

    private string? _currentVersion;

    private string GetModuleInstallDir()
    {
        string configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "discord" + ConfigManager.CurrentConfig?.DiscordBranch?.ToLower(),
            _currentVersion ?? "1.0.0",
            "modules"
        );
        return configDir;
    }

    private async Task WriteInstalledJsonAsync(string moduleDir, DiscordManifest manifest)
    {
        var installed = manifest.RequiredModules
            .Where(m => manifest.Modules.ContainsKey(m))
            .ToDictionary(
                m => m,
                m => manifest.Modules[m].Full.ModuleVersion
            );

        string json = JsonSerializer.Serialize(installed, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(moduleDir, "installed.json"), json);
    }

    public static async Task<bool> IsDistroAvailableAsync(string channel)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.Timeout = TimeSpan.FromSeconds(5);

            string url = // install id isn't required but i am not risking it
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

    private async Task DownloadAndVerifyAsync(string url, string destination, string expectedSha256, string displayName)
    {
        var downloader = new Downloader(url, destination, isMultithreaded: true);
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

    private void SaveVersionState(InstallState state) =>
        File.WriteAllText(_versionFile,
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

    private class InstallState
    {
        [JsonPropertyName("host_version")] public string? HostVersion { get; set; }
        [JsonPropertyName("module_keys")]  public Dictionary<string, string> ModuleKeys { get; set; } = new();
    }
}