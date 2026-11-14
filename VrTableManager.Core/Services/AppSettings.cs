using System.Text.Json;
using System.Text.Json.Serialization;

namespace VrTableManager.Services;

public sealed class AppSettings
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string VpxDir { get; set; } = Path.Combine(Home, "VPinball", "VPinballX");

    /// <summary>
    /// The table folder new tables install (and import) into: Setup's "install here" one. My Tables and the VR lobby
    /// list the tables in it and in <see cref="OtherTablesDirs"/> alike.
    /// </summary>
    public string TablesDir { get; set; } = Path.Combine(Home, "VPinball", "Tables");

    /// <summary>
    /// More folders holding tables (another drive, say, when the first fills up), besides <see cref="TablesDir"/>.
    /// Their tables are listed, played, updated and given add-ons where they are; only new installs go elsewhere.
    /// </summary>
    public List<string> OtherTablesDirs { get; set; } = [];

    /// <summary>Every table folder, the install one first, each once.</summary>
    [JsonIgnore]
    public List<string> AllTablesDirs
    {
        get
        {
            var all = new List<string>();
            foreach (var d in OtherTablesDirs.Prepend(TablesDir))
            {
                if (!string.IsNullOrWhiteSpace(d) && !all.Any(a => SamePath(a, d)))
                {
                    all.Add(d);
                }
            }
            return all;
        }
    }

    /// <summary>
    /// <paramref name="folder"/> is one of the table folders themselves (where loose .vpx files share a folder), not
    /// a table's own folder inside one.
    /// </summary>
    public bool IsTablesRoot(string folder) => AllTablesDirs.Any(d => SamePath(d, folder));

    static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase
            );
        }
        catch (Exception e)
            when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Future Pinball install folder (contains BAM\FPLoader.exe). Empty until found.</summary>
    public string FpDir { get; set; } = "";

    /// <summary>BAM's docs say FPLoader should run as administrator.</summary>
    public bool FpRunAsAdmin { get; set; } = true;

    /// <summary>Folder containing UEVRInjector.exe (Pinball FX in VR). Empty until found.</summary>
    public string UevrDir { get; set; } = "";

    /// <summary>Write the pinball-tuned UEVR settings into Pinball FX / M's UEVR profile before each VR launch.</summary>
    public bool ApplyPinballUevrProfile { get; set; } = true;

    /// <summary>On LAUNCH VR LOBBY, say how lobby videos bring the cabinets to life (while some tables have none).</summary>
    public bool ShowLobbyVideoTip { get; set; } = true;

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

    /// <summary>
    /// The VPX build the player picked from the Setup tab's build list (often an older one, when a newer build broke
    /// something). While it's installed, newer builds aren't offered as updates; INSTALL / UPDATE VPX clears it.
    /// </summary>
    public string? VpxKeptBuild { get; set; }

    /// <summary>Where the main window was when the app last closed, so it opens there again. Null until then.</summary>
    public WindowPlacement? MainWindow { get; set; }

    /// <summary>0 = runtime default, otherwise 72/80/90/120.</summary>
    public int VrRefreshRate { get; set; }

    /// <summary>Meta Quest Link's spacewarp before VR launches: "Leave as is", "Off" or "Auto" (<see cref="MetaAsw"/>).</summary>
    public string MetaAswMode { get; set; } = "Leave as is";

    /// <summary>Your height (cm), for the VR lockbar height (<see cref="VrFit"/>). Null until entered.</summary>
    public double? VrPlayerHeightCm { get; set; }

    /// <summary>How VR tables are sized: <see cref="VrFit.SizeReal"/>, <see cref="VrFit.SizeSame"/> or <see cref="VrFit.SizeVpx"/>. Real size until the player picks.</summary>
    public string VrTableSizing { get; set; } = VrFit.SizeReal;

    /// <summary>
    /// The player saved a fit (FIT EVERY TABLE TO ME). Until then VR launches get a real cabinet's values for any not
    /// set; after, an empty value means VPX's own default (<see cref="VrFit.LaunchSettings"/>). RESET TO DEFAULTS clears it.
    /// </summary>
    public bool VrFitSaved { get; set; }

    /// <summary>The lockbar width every table gets in <see cref="VrFit.SizeSame"/> mode (cm); a WPC table's real one.</summary>
    public double VrSameLockbarWidthCm { get; set; } = 57.1;

    /// <summary>Show and type VR fit lengths in feet and inches.</summary>
    public bool VrUseInches { get; set; }

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
    /// backbox and DMD (<see cref="VpxSetup.VrBackglassFor"/>), except a VR table with a blank backglass its PuP pack
    /// draws, where it's added and moved onto the cabinet (<see cref="VrBackglass"/>). Off: VPX's own setting (its
    /// menu, saved globally) decides.
    /// </summary>
    public bool VrAutoBackglass { get; set; } = true;

    /// <summary>
    /// Desktop play turns off VPX's bloom ([Player] ForceBloomOff = 1) for each launch, on top of the player's own
    /// settings (<see cref="VpxIniLayers.Build"/>). Off: VPX's own setting decides. VR play has its own bloom setting
    /// in the VR graphics panel (<see cref="VpxGraphics.Bloom"/>).
    /// </summary>
    public bool DesktopBloomOff { get; set; }

    /// <summary>
    /// VR play uses the VR graphics panel's settings (render scale, AA, reflections…) on top of the player's own
    /// VPinballX.ini. Off: those are left out of every VR launch, so VPX's own graphics settings decide; the panel's
    /// values are kept for turning it back on (<see cref="VpxIniLayers"/>).
    /// </summary>
    public bool UseVrGraphics { get; set; } = true;

    /// <summary>
    /// Where a key set on Setup → Controls is saved (<see cref="VpxKeyMap.Set"/>): the player's own VPinballX.ini (desktop
    /// and VR), or the VR or desktop overrides only.
    /// </summary>
    public ControlsTarget ControlsTarget { get; set; } = ControlsTarget.Everywhere;

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
    /// Cabinets in view play their table's lobby video on the playfield (medias\lobby-video), with its depth map. Off:
    /// still playfields, and the lobby never reads the videos or builds depth maps (weaker PCs, slow drives, testing).
    /// </summary>
    public bool LobbyPlayfieldVideos { get; set; } = true;

    /// <summary>On LAUNCH VR LOBBY, offer PREPARE TABLES while many tables haven't been read for the lobby yet.</summary>
    public bool AskToPrepareLobbyTables { get; set; } = true;

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

    /// <summary>
    /// With <see cref="LobbyWaitsForTable"/>: the loading screen plays the table's loading video (<see cref="LoadingVideo"/>),
    /// or the one for every table, on a big screen with its sound. Off: only the table and its lamps.
    /// </summary>
    public bool LobbyLoadingVideos { get; set; } = true;

    /// <summary>
    /// The VR lobby drives the cabinet's feedback through the player's DirectOutput (DOF) as PinUP Popper's menu does
    /// (<see cref="Lobby.LobbyDof"/>): flippers, Start and the rest fire the effects set up for "pinupmenu" in the DOF
    /// Config Tool. Off by default: DOF holds the output boards while the lobby runs (it lets go before a table starts).
    /// </summary>
    public bool LobbyDof { get; set; }

    /// <summary>
    /// How big the VR lobby's cabinets are, in percent of a real machine (75–125, see <see cref="Lobby.LobbyLayout"/>).
    /// A bigger cabinet keeps the same gap to its neighbours, so the ring grows with it.
    /// </summary>
    public int LobbyCabinetScalePercent { get; set; } = Lobby.LobbyLayout.DefaultScalePercent;

    /// <summary>
    /// How many cabinets stand in the VR lobby's ring, the selected one included (odd, 5–21). Fewer than fit at the
    /// default spacing may show when big cabinets would otherwise reach the wall (<see cref="Lobby.LobbyLayout.Ring"/>).
    /// </summary>
    public int LobbyCabinetCount { get; set; } = Lobby.LobbyLayout.DefaultCount;

    /// <summary>
    /// Every table stands in the VR lobby on the generic cabinet, wearing its own playfield and backglass, instead of
    /// the VR cabinet built into its file (some look poor out of their table). Off: a table's own cabinet when it has one.
    /// </summary>
    public bool LobbyGenericCabinets { get; set; }

    /// <summary>Tables (by path) shown on the generic cabinet in the VR lobby even when the rest use their own (My Tables).</summary>
    public List<string> LobbyGenericCabinetTables { get; set; } = [];

    public bool IsGenericCabinetTable(string tablePath) =>
        LobbyGenericCabinetTables.Contains(tablePath, StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds or removes a table from <see cref="LobbyGenericCabinetTables"/>.</summary>
    public void SetGenericCabinetTable(string tablePath, bool generic)
    {
        LobbyGenericCabinetTables.RemoveAll(p =>
            string.Equals(p, tablePath, StringComparison.OrdinalIgnoreCase)
        );
        if (generic)
        {
            LobbyGenericCabinetTables.Add(tablePath);
        }
    }

    /// <summary>How big the cards in My Tables are: "Large", "Medium", "Small" or "Text" (no pictures).</summary>
    public string CardSize { get; set; } = "Large";

    /// <summary>The Setup section that was showing last ("vpx", "headset", …), so Setup opens there again.</summary>
    public string SetupSection { get; set; } = "vpx";

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
            // VRTM_DATA_DIR (for testing): a whole other set of settings, stats and caches, e.g. a big fake library.
            var dir = Environment.GetEnvironmentVariable("VRTM_DATA_DIR") is { Length: > 0 } other
                ? Path.GetFullPath(other) // the app and the lobby it starts agree, whatever their working folder
                : Path.Combine(
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

    /// <summary>
    /// settings.json was there but couldn't be read (in use, no access), so these are defaults: <see cref="Save"/>
    /// leaves the file alone rather than write defaults over every setting. A later load that reads it clears it
    /// (copy the loaded value across when reloading into an existing instance).
    /// </summary>
    [JsonIgnore]
    public bool Unreadable { get; set; }

    public static AppSettings LoadFrom(string path)
    {
        var s = new AppSettings();
        string? text = null;
        try
        {
            // Shared with the VR lobby and the --play launcher; read while another one saves it.
            text = PlayStats.ReadShared(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            s.Unreadable = true;
        }
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                s = JsonSerializer.Deserialize<AppSettings>(text) ?? s;
            }
            catch (JsonException)
            {
                // Corrupt settings: fall back to defaults, keeping the file beside them so nothing is lost for good.
                try
                {
                    File.Copy(path, path + ".bad", overwrite: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    s.Unreadable = true;
                }
            }
        }
        s.SourcePath = path;
        return s;
    }

    /// <summary>Written beside the file and swapped in, so a crash or power cut mid-save leaves the old settings whole.</summary>
    public void Save()
    {
        if (Unreadable)
        {
            return;
        }

        PlayStats.WriteAtomic(SourcePath ?? DefaultPath, JsonSerializer.Serialize(this, Json));
    }
}

/// <summary>
/// A window's normal (not maximized) spot: top-left corner in screen pixels, size in the window's own units, and
/// whether it was maximized over it.
/// </summary>
public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}
