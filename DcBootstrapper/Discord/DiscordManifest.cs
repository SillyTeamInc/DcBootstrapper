using System.Text.Json.Serialization;

namespace DcBootstrapper.Discord;

public class DiscordManifest
{
    [JsonPropertyName("full")] 
    public DistroPackage Full { get; set; } = new();

    [JsonPropertyName("deltas")] 
    public List<DistroPackage> Deltas { get; set; } = new();

    [JsonPropertyName("modules")]
    public Dictionary<string, ModuleInfo> Modules { get; set; } = new();

    [JsonPropertyName("required_modules")]
    public List<string> RequiredModules { get; set; } = new();

    [JsonPropertyName("metadata_version")] 
    public long MetadataVersion { get; set; }

    [JsonPropertyName("required_update")]
    public bool RequiredUpdate { get; set; }
}

public class DistroPackage
{
    [JsonPropertyName("host_version")] 
    public int[] HostVersion { get; set; } = [];

    [JsonPropertyName("package_sha256")] 
    public string Sha256 { get; set; } = "";
    [JsonPropertyName("url")] 
    public string Url { get; set; } = "";

    [JsonIgnore]
    public string VersionString => HostVersion.Length == 3
        ? $"{HostVersion[0]}.{HostVersion[1]}.{HostVersion[2]}"
        : "";
}

public class ModuleInfo
{
    [JsonPropertyName("full")] 
    public ModulePackage Full { get; set; } = new();
    [JsonPropertyName("deltas")]
    public List<ModulePackage> Deltas { get; set; } = new();
}

public class ModulePackage
{
    [JsonPropertyName("host_version")] 
    public int[] HostVersion { get; set; } = [];
    [JsonPropertyName("module_version")] 
    public int ModuleVersion { get; set; }
    [JsonPropertyName("package_sha256")]
    public string Sha256 { get; set; } = "";
    [JsonPropertyName("url")] 
    public string Url { get; set; } = "";
}

public class DeltaManifest
{
    [JsonPropertyName("manifest_version")] public int Version { get; set; }
    [JsonPropertyName("files")] public Dictionary<string, DeltaFileEntry> Files { get; set; } = new();
}

public class DeltaFileEntry
{
    [JsonPropertyName("Existing")] public HashEntry?  Existing { get; set; }
    [JsonPropertyName("New")]      public HashEntry?  New      { get; set; }
    [JsonPropertyName("Bsdiff")]   public BsdiffEntry? Bsdiff  { get; set; }
}

public class HashEntry
{
    [JsonPropertyName("Sha256")] public string Sha256 { get; set; } = "";
}

public class BsdiffEntry
{
    [JsonPropertyName("src_hash")]   public HashEntry SrcHash   { get; set; } = new();
    [JsonPropertyName("delta_hash")] public HashEntry DeltaHash { get; set; } = new();
    [JsonPropertyName("hash")]       public HashEntry Hash      { get; set; } = new();
    [JsonPropertyName("length")]     public long      Length    { get; set; }
}