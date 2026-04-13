using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DcBootstrapper;

public class Config
{ 
    public string? DiscordBranch { get; set; } 
    public string? InstallPath { get; set; }
    public bool? MakeApplicationsSymlink { get; set; }

    
    [JsonIgnore]    
    public string DiscordUrl => DiscordBranch?.ToLower() switch
    {
        "canary" => "https://discord.com/api/download/canary?platform=linux&format=tar.gz",
        "ptb" => "https://discord.com/api/download/ptb?platform=linux&format=tar.gz",
        _ => "https://discord.com/api/download?platform=linux&format=tar.gz"
    };
    
    [JsonIgnore]
    public string ExecutableName => DiscordBranch?.ToLower() switch
    {
        "canary" => "DiscordCanary",
        "ptb" => "DiscordPTB",
        _ => "Discord"
    };
    
    [JsonIgnore]
    public string WmName => DiscordBranch?.ToLower() switch
    {
        "canary" => "discord-canary",
        "ptb" => "discord-ptb",
        _ => "discord"
    };

    [JsonIgnore]
    public string DesktopName => WmName + ".desktop";
    
    [JsonIgnore]
    public string ProperBranch => DiscordBranch?.Length > 0 ? char.ToUpper(DiscordBranch[0]) + DiscordBranch[1..] : string.Empty;
    
    [JsonIgnore]
    public string ExecutablePath => Path.Combine(InstallPath ?? string.Empty, ExecutableName);
    
    [JsonIgnore]
    public static Config Default => new Config
    {
        DiscordBranch = "stable",
        InstallPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordCustom"),
        MakeApplicationsSymlink = true
    };
}

public static class ConfigManager
{
    // Put the config file in .config/DiscordCustom.json
    private static readonly string ConfigFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "DiscordCustom.json");
    public static Config? CurrentConfig { get; private set; } = null;
    
    public static void LoadConfig()
    {
        if (!File.Exists(ConfigFilePath))
        {
            CurrentConfig = Config.Default;
            SaveConfig();

            FileInfo configFileInfo = new FileInfo(ConfigFilePath);
            
            string symlinkPath = Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName) ?? string.Empty, "config.json");
            if (File.Exists(symlinkPath) || Directory.Exists(symlinkPath))
            {
                File.Delete(symlinkPath);
            }
            File.CreateSymbolicLink(symlinkPath, configFileInfo.FullName);
            
            Console.WriteLine("No config found, creating default config at " + ConfigFilePath);
            return;
        }

        string json = File.ReadAllText(ConfigFilePath);
        CurrentConfig = JsonSerializer.Deserialize<Config>(json);
        
        // kinda hacky solution to null values
        bool updated = false;
        foreach (var property in typeof(Config).GetProperties())
        {
            if (property.GetValue(CurrentConfig) == null)            {
                property.SetValue(CurrentConfig, property.GetValue(Config.Default));
                updated = true;
            }
        }
        if (updated) SaveConfig();

        return;
    }

    public static void SaveConfig()
    {
        string json = JsonSerializer.Serialize(CurrentConfig, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigFilePath, json);
    }
}