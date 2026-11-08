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

    /// <summary>
    /// Once a download is installed, delete the file your browser saved (in your Downloads folder, or wherever you
    /// pointed I SAVED IT). A failed install keeps the file, so you can try again with it.
    /// </summary>
    public bool DeleteDownloadsAfterInstall { get; set; } = true;
    public bool HideAdult { get; set; } = true;

    /// <summary>
    /// Install and update to VPX builds the VPX team has labelled "DO NOT USE" on GitHub. Those are the current
    /// BGFX builds with the newest VR work; off: only builds without that label are offered.
    /// </summary>
    public bool VpxUseDoNotUseBuilds { get; set; } = true;

    /// <summary>
    /// Also install and update to VPX's nightly BGFX builds (built from every change to its master branch) when
    /// they're newer than the newest release. Untested by the VPX team; off by default.
    /// </summary>
    public bool VpxUseNightlyBuilds { get; set; }

    /// <summary>0 = runtime default, otherwise 72/80/90/120.</summary>
    public int VrRefreshRate { get; set; }

    /// <summary>Meta Quest Link's spacewarp before VR launches: "Leave as is", "Off" or "Auto" (<see cref="MetaAsw"/>).</summary>
    public string MetaAswMode { get; set; } = "Leave as is";

    /// <summary>Before a VR launch, warn when the active OpenXR runtime is SteamVR (an extra layer for a Quest).</summary>
    public bool WarnSteamVrRuntime { get; set; } = true;

    /// <summary>
    /// VR and desktop play use VPX's built-in PinMAME (plugin) rather than a separately installed VPinMAME, so ROMs,
    /// aliases and Serum files come from each table's own folder.
    /// </summary>
    public bool UseBuiltInPinmame { get; set; } = true;

    /// <summary>
    /// VR and desktop play turn on VPX's PinUP Player plugin, which shows a table's PuP pack (pupvideos\ in its folder)
    /// on the cabinet's backglass and DMD.
    /// </summary>
    public bool UsePupPlugin { get; set; } = true;

    /// <summary>
    /// VR and desktop play turn on VPX's FlexDMD plugin (DMDs drawn by the table script, as many originals do) and its B2S
    /// plugin (the table's .directb2s backglass).
    /// </summary>
    public bool UseDisplayPlugins { get; set; } = true;

    /// <summary>
    /// VR play picks VPX's default backglass display ([PlayerVR] AddBackglass) per table: added for desktop and cabinet
    /// tables, so their PuP pack or .directb2s has somewhere to show; left out for VR tables, which bring their own
    /// backbox and DMD (<see cref="VpxSetup.VrBackglassFor"/>). Off: VPX's own setting (its menu, saved globally) decides.
    /// </summary>
    public bool VrAutoBackglass { get; set; } = true;

    /// <summary>
    /// Desktop play turns off VPX's bloom ([Player] ForceBloomOff = 1) for each launch, on top of the player's own
    /// settings (<see cref="VpxIniLayers.Build"/>). Off: VPX's own setting decides. VR play has its own bloom setting
    /// in the VR graphics panel (<see cref="VpxGraphics.Bloom"/>).
    /// </summary>
    public bool DesktopBloomOff { get; set; }

    /// <summary>
    /// Tables with a hand-set VR room switch in their script (VRRoom = 0 / 1) get it set for the mode being
    /// played: on in VR, off on the desktop (<see cref="VrRoomSwitch"/>). Off: every table plays as it came.
    /// </summary>
    public bool AutoVrRoom { get; set; } = true;

    /// <summary>
    /// The VR lobby records a ROM table's start-up music the first time you settle on it (RomAudio): a
    /// background PinMAME run of about 50 s, once per table. Off: only audio files already there play.
    /// </summary>
    public bool LobbyRecordsRomAudio { get; set; } = true;

    /// <summary>
    /// After you pick a table, the VR lobby stays in the headset on a loading screen until the table takes the
    /// headset over (or fails, and says why). Off: the lobby closes at once, as before, and the headset shows
    /// an empty view while the table loads. For OpenXR runtimes that won't hand the headset from one app to another.
    /// </summary>
    public bool LobbyWaitsForTable { get; set; } = true;

    /// <summary>
    /// With <see cref="LobbyWaitsForTable"/>: the VR lobby keeps running, hidden, while you play, and takes the
    /// headset straight back when the table closes instead of starting up again. Off: it closes once the table
    /// has the headset, and the manager starts it again afterwards.
    /// </summary>
    public bool LobbyStaysOpen { get; set; } = true;

    /// <summary>How big the cards in My Tables are: "Large", "Medium", "Small" or "Text" (no pictures).</summary>
    public string CardSize { get; set; } = "Large";

    /// <summary>Tables (by path) whose PuP DMD fix (<see cref="VrDmdFix"/>) the user said not to ask about again.</summary>
    public List<string> DmdFixDeclined { get; set; } = [];

    /// <summary>After a VPX table closes, show its log's errors when it crashed or ended within seconds.</summary>
    public bool ExplainFailedLaunches { get; set; } = true;

    /// <summary>Restore this window after a table exits so you can pick the next one.</summary>
    public bool ReturnAfterPlay { get; set; } = true;

    /// <summary>A VR Table Manager release the user chose to skip: not offered at startup (Setup still shows it).</summary>
    public string SkippedAppVersion { get; set; } = "";

    /// <summary>Path of VrTableManager.exe, recorded at startup so the VR lobby can launch tables through it.</summary>
    public string ManagerExe { get; set; } = "";

    /// <summary>Where this instance was loaded from (tests and the lobby can point at another settings.json).</summary>
    [JsonIgnore]
    public string? SourcePath { get; private set; }

    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VrTableManager"
            );
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
            {
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? s;
            }
        }
        catch
        { /* corrupt settings: fall back to defaults */
        }
        s.SourcePath = path;
        return s;
    }

    public void Save() =>
        File.WriteAllText(SourcePath ?? DefaultPath, JsonSerializer.Serialize(this, Json));
}
