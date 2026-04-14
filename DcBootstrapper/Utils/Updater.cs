namespace DcBootstrapper.Utils;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

public class Updater
{
    public static string GetRepository()
    {
        var repoUrl = ThisAssembly.Git.RepositoryUrl.Replace(".git", "").TrimEnd('/');
        int lastSlash = repoUrl.LastIndexOf('/');
        int secondLastSlash = repoUrl.LastIndexOf('/', lastSlash - 1);
        if (secondLastSlash == -1 || lastSlash <= secondLastSlash)
            throw new Exception("Invalid repository URL: " + repoUrl);
        return repoUrl[(secondLastSlash + 1)..];
    }

    public static string GetCurrentTag()
    {
        return string.IsNullOrEmpty(ThisAssembly.Git.BaseTag)
            ? ThisAssembly.Git.Branch
            : ThisAssembly.Git.BaseTag;
    }

    public static async Task<bool> CheckAndUpdateAsync()
    {
        string currentTag = GetCurrentTag();
        Console.Write($"[*] Checking for bootstrapper update (current: {currentTag})... ");

        GhReleaseInfo release;
        try
        {
            release = await FetchLatestReleaseAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"skipped (couldn't fetch release info: {ex.Message})");
            return false;
        }

        string latestTag = release.TagName;

        // Strip 'v' prefix for comparison
        string currentVer = currentTag.TrimStart('v');
        string latestVer  = latestTag.TrimStart('v');

        if (!Version.TryParse(currentVer, out var current))
        {
            Console.WriteLine($"skipped (couldn't parse current version: {currentVer})");
            return false;
        }
        if (!Version.TryParse(latestVer, out var latest))
        {
            Console.WriteLine($"skipped (couldn't parse latest version: {latestVer})");
            return false;
        }

        if (latest <= current)
        {
            Console.WriteLine("Up to date.");
            return false;
        }

        Console.WriteLine($"Update available: {currentTag} -> {latestTag}");

        if (ConfigManager.CurrentConfig?.AutoUpdateBootstrapper == false)
        {
            Console.WriteLine("Auto-update is disabled in config, skipping update.");
            return false;
        }
        

        var asset = release.Assets.FirstOrDefault(a =>
            a.Name.Equals("DcBootstrapper", StringComparison.OrdinalIgnoreCase));

        if (asset == null)
            throw new Exception("No Linux asset found in latest release.");

        string selfPath = Environment.ProcessPath
            ?? throw new Exception("Could not determine bootstrapper path.");
        string tempPath = selfPath + ".new";

        var downloader = new Downloader(asset.BrowserDownloadUrl, tempPath, isMultithreaded: true);
        Console.WriteLine($"[*] Downloading update using {Environment.ProcessorCount} threads...");
        await downloader.DownloadFileMultithreaded(Environment.ProcessorCount);

        MakeExecutable(tempPath);
        File.Move(tempPath, selfPath, overwrite: true);

        Console.WriteLine("[*] Bootstrapper updated, restarting");
        return true;
    }

    private static async Task<GhReleaseInfo> FetchLatestReleaseAsync()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DcBootstrapper");

        string url = $"https://api.github.com/repos/{GetRepository()}/releases/latest";
        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
            throw new Exception($"GitHub API returned {response.StatusCode}");

        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GhReleaseInfo>(body)
               ?? throw new Exception("Failed to deserialize release info.");
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        const UnixFileMode mode =
            UnixFileMode.UserRead   | UnixFileMode.UserWrite  | UnixFileMode.UserExecute  |
            UnixFileMode.GroupRead  | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead  | UnixFileMode.OtherExecute;

        File.SetUnixFileMode(path, mode);
    }
}

public class GhReleaseInfo
{
    [JsonPropertyName("tag_name")]  public string TagName { get; set; } = "";
    [JsonPropertyName("html_url")]  public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("body")]      public string Body    { get; set; } = "";
    [JsonPropertyName("assets")]    public List<GhReleaseAsset> Assets { get; set; } = new();
}

public class GhReleaseAsset
{
    [JsonPropertyName("name")]                 public string Name               { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
}