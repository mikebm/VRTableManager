using System.Text.Json;
using System.Text.Json.Serialization;

namespace VrTableManager.Services;

public sealed class AppSettings
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string VpxDir { get; set; } = Path.Combine(Home, "VPinball", "VPinballX");
    public string TablesDir { get; set; } = Path.Combine(Home, "VPinball", "Tables");
    /// <summary>Future Pinball install folder (contains BAM\FPLoader.exe). Empty until found.</summary>
    public string FpDir { get; set; } = "";
    /// <summary>BAM's docs say FPLoader should run as administrator.</summary>
    public bool FpRunAsAdmin { get; set; } = true;
    /// <summary>Folder containing UEVRInjector.exe (Pinball FX in VR). Empty until found.</summary>
    public string UevrDir { get; set; } = "";
    /// <summary>Write the pinball-tuned UEVR settings into Pinball FX / M's UEVR profile before each VR launch.</summary>
    public bool ApplyPinballUevrProfile { get; set; } = true;
    /// <summary>Show the "before you start" checklist before Pinball FX / M VR launches.</summary>
    public bool ShowZenReminder { get; set; } = true;
    /// <summary>How many table/ROM downloads run at the same time (1–4); the rest queue.</summary>
    public int MaxParallelDownloads { get; set; } = 2;
    public bool HideAdult { get; set; } = true;
    /// <summary>0 = runtime default, otherwise 72/80/90/120.</summary>
    public int VrRefreshRate { get; set; }
    /// <summary>Restore this window after a table exits so you can pick the next one.</summary>
    public bool ReturnAfterPlay { get; set; } = true;
    /// <summary>Path of VrTableManager.exe, recorded at startup so the VR lobby can launch tables through it.</summary>
    public string ManagerExe { get; set; } = "";

    /// <summary>Where this instance was loaded from (tests and the lobby can point at another settings.json).</summary>
    [JsonIgnore] public string? SourcePath { get; private set; }

    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VrTableManager");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DefaultPath => Path.Combine(DataDir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load() => LoadFrom(DefaultPath);

    public static AppSettings LoadFrom(string path)
    {
        var s = new AppSettings();
        try
        {
            if (File.Exists(path))
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? s;
        }
        catch { /* corrupt settings: fall back to defaults */ }
        s.SourcePath = path;
        return s;
    }

    public void Save() => File.WriteAllText(SourcePath ?? DefaultPath, JsonSerializer.Serialize(this, Json));
}
