using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VrTableManager.Services;

public sealed record VpxRelease(string Tag, string AssetName, string Url, long Size);

/// <param name="Latest">Newest BGFX build that's fine to use.</param>
/// <param name="Withdrawn">Newest BGFX build renamed "DO NOT USE", when it's newer than <paramref name="Latest"/>.</param>
public sealed record VpxReleases(VpxRelease? Latest, VpxRelease? Withdrawn);

/// <summary>Installs Visual Pinball X (BGFX build, which does VR through OpenXR) and prepares a VR settings file.</summary>
public static class VpxSetup
{
    const string ReleasesApi = "https://api.github.com/repos/vpinball/vpinball/releases?per_page=30";
    const string VersionFile = "vrtm-version.txt";

    /// <summary>Asset name suffix for this OS. BGFX is the build with OpenXR VR support.</summary>
    static string AssetSuffix =>
        OperatingSystem.IsWindows() ? "-windows-x64-Release.zip"
        : OperatingSystem.IsMacOS() ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "-macos-arm64-Release.dmg" : "-macos-x64-Release.dmg")
        : "-linux-x64-Release.tar.gz";

    /// <summary>The newest usable BGFX build, or null.</summary>
    public static async Task<VpxRelease?> FindLatestAsync(CancellationToken ct) => (await FindReleasesAsync(ct)).Latest;

    /// <summary>
    /// What GitHub offers: the newest BGFX build that's fine to use, and the newest one the VPX team has
    /// since renamed "DO NOT USE" when that's newer still. Those are never installed or offered as
    /// updates; the app keeps whatever VPX is installed and says why.
    /// </summary>
    public static async Task<VpxReleases> FindReleasesAsync(CancellationToken ct) =>
        ParseReleases(await Http.Client.GetStringAsync(ReleasesApi, ct));

    public static VpxReleases ParseReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        VpxRelease? withdrawn = null;
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.GetProperty("draft").GetBoolean()) continue;
            var name = rel.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var asset = BgfxAsset(rel);
            if (asset == null) continue;
            if (!name.StartsWith("DO NOT USE", StringComparison.OrdinalIgnoreCase)) return new VpxReleases(asset, withdrawn);
            withdrawn ??= asset;
        }
        return new VpxReleases(null, withdrawn);
    }

    static VpxRelease? BgfxAsset(JsonElement rel)
    {
        foreach (var asset in rel.GetProperty("assets").EnumerateArray())
        {
            var an = asset.GetProperty("name").GetString() ?? "";
            if (an.StartsWith("VPinballX_BGFX-") && an.EndsWith(AssetSuffix))
                return new VpxRelease(rel.GetProperty("tag_name").GetString()!, an,
                    asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64());
        }
        return null;
    }

    /// <summary>The Setup tab's line about VPX updates, and whether to offer one.</summary>
    public static (string Text, bool HasUpdate) DescribeUpdate(VpxReleases r, string? installed)
    {
        var note = r.Withdrawn is { } w
            ? $" The VPX team has marked newer builds (up to {w.Tag}) \"DO NOT USE\" on GitHub, so they aren't offered."
            : "";
        if (r.Latest == null)
            return (installed != null
                ? $"Keeping your installed {installed}.{note}"
                : r.Withdrawn != null
                    ? $"No VR (BGFX) build is available to install right now.{note} Check github.com/vpinball/vpinball/releases."
                    : "No VPX release found for this OS.", false);
        if (installed == null) return ($"Not installed. Latest is {r.Latest.Tag}.{note}", false);
        if (installed == r.Latest.Tag) return ($"Up to date ({installed}).{note}", false);
        // Installed something newer than the newest usable build (e.g. a build later marked DO NOT USE): keep it.
        if (installed == r.Withdrawn?.Tag || IsNewer(installed, r.Latest.Tag)) return ($"Keeping your installed {installed}.{note}", false);
        return ($"{installed} → {r.Latest.Tag}", true);
    }

    /// <summary>Compares tags like "v10.8.1-5436-af26b2d93" by their version and build number.</summary>
    static bool IsNewer(string a, string b)
    {
        static (Version v, int build) Parse(string t)
        {
            var parts = t.TrimStart('v').Split('-');
            return (Version.TryParse(parts[0], out var v) ? v : new Version(0, 0),
                parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0);
        }
        var (va, ba) = Parse(a);
        var (vb, bb) = Parse(b);
        return va != vb ? va > vb : ba > bb;
    }

    public static string? InstalledVersion(AppSettings s)
    {
        var f = Path.Combine(s.VpxDir, VersionFile);
        return File.Exists(f) && FindExe(s) != null ? File.ReadAllText(f).Trim() : null;
    }

    public static async Task InstallAsync(AppSettings s, VpxRelease rel, IProgress<double> progress, Action<string> status, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), rel.AssetName);
        status($"Downloading {rel.AssetName}…");
        await Http.DownloadFileAsync(rel.Url, tmp, progress, ct);

        if (tmp.EndsWith(".dmg"))
        {
            // macOS: hand the disk image to Finder; the user drags the app out.
            Process.Start("open", tmp);
            status("Opened the VPX disk image. Drag VPinballX to Applications.");
            return;
        }

        status("Extracting…");
        Directory.CreateDirectory(s.VpxDir);
        await Task.Run(() => TableInstaller.Extract(tmp, s.VpxDir), ct);
        await File.WriteAllTextAsync(Path.Combine(s.VpxDir, VersionFile), rel.Tag, ct);
        File.Delete(tmp);
        EnsureVrIni(s);
    }

    public static string? FindExe(AppSettings s)
    {
        if (!Directory.Exists(s.VpxDir)) return null;
        var files = Directory.EnumerateFiles(s.VpxDir, "VPinballX*", SearchOption.AllDirectories)
            .Where(f => OperatingSystem.IsWindows() ? f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) : Path.GetExtension(f) == "")
            .ToList();
        return files.FirstOrDefault(f => Path.GetFileName(f).Contains("BGFX", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault();
    }

    public static string VrIniPath(AppSettings s) => Path.Combine(s.VpxDir, "VrTableManager-VR.ini");

    /// <summary>
    /// VPX reads [PlayerVR] AskToTurnOn (0 = Enabled, 1 = Autodetect, 2 = Disabled).
    /// A separate ini passed with -Ini keeps the desktop settings untouched.
    /// </summary>
    public static void EnsureVrIni(AppSettings s)
    {
        var ini = VrIniPath(s);
        IniFile.Set(ini, "PlayerVR", "AskToTurnOn", "0");
        var rate = s.VrRefreshRate switch { 72 => 1, 80 => 2, 90 => 3, 120 => 4, _ => 0 };
        IniFile.Set(ini, "PlayerVR", "DisplayRefreshRate", rate.ToString());
        // VPX's built-in PinMAME reads each table's own pinmame\roms, nvram and alias.txt (where this app
        // puts them); without it VPX falls back to a separately installed VPinMAME and its global roms
        // folder. Serum and AltSound are its colorization and sound companions, read from the table folder.
        var on = s.UseBuiltInPinmame ? "1" : "0";
        foreach (var plugin in BuiltInPinmamePlugins) IniFile.Set(ini, "Plugin." + plugin, "Enable", on);
    }

    public static readonly string[] BuiltInPinmamePlugins = ["PinMAME", "Serum", "AltSound"];

    /// <summary>Which OpenXR runtime VPX will talk to (Windows only).</summary>
    public static string DescribeOpenXrRuntime()
    {
        if (!OperatingSystem.IsWindows()) return "Check your OpenXR runtime (e.g. SteamVR or Monado).";
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1");
            var path = key?.GetValue("ActiveRuntime") as string;
            if (string.IsNullOrEmpty(path)) return "No OpenXR runtime found. Install the Meta Quest Link app (or SteamVR / Virtual Desktop).";
            return $"Active OpenXR runtime: {OpenXrRuntimes.NameFor(path)}";
        }
        catch (Exception e)
        {
            return "Couldn't read OpenXR runtime: " + e.Message;
        }
    }
}

/// <summary>Finds Future Pinball + BAM. Both are installed by the user; this just locates and checks them.</summary>
public static class FpSetup
{
    public static IEnumerable<string> CandidateDirs()
    {
        yield return @"C:\Games\Future Pinball";
        yield return @"C:\Future Pinball";
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Future Pinball");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Future Pinball");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "VPinball", "Future Pinball");
    }

    public static string? AutoDetect() => OperatingSystem.IsWindows()
        ? CandidateDirs().FirstOrDefault(d => File.Exists(Path.Combine(d, "Future Pinball.exe")))
        : null;

    public static string LoaderPath(AppSettings s) => Path.Combine(s.FpDir, "BAM", "FPLoader.exe");

    public static string Describe(AppSettings s)
    {
        if (!OperatingSystem.IsWindows()) return "Future Pinball is Windows-only.";
        if (string.IsNullOrWhiteSpace(s.FpDir) || !File.Exists(Path.Combine(s.FpDir, "Future Pinball.exe")))
            return "Future Pinball not found. Install it, then choose its folder.";
        if (!File.Exists(LoaderPath(s)))
            return "Future Pinball found, but BAM is missing (BAM\\FPLoader.exe). Install BAM to enable VR.";
        var vr = File.Exists(Path.Combine(s.FpDir, "BAM", "openvr_api.dll"))
            || Directory.EnumerateFiles(Path.Combine(s.FpDir, "BAM"), "*OpenVR*", SearchOption.TopDirectoryOnly).Any();
        return vr
            ? "Future Pinball + BAM-OpenVR ready. Start SteamVR before playing."
            : "Future Pinball + BAM found, but BAM-OpenVR isn't. Add the BAM-OpenVR files for VR.";
    }
}

/// <summary>
/// A pinball program this app can launch. Add a new class here (and to <see cref="Engines.All"/>)
/// to support another one, e.g. Pinball FX through UEVR.
/// </summary>
public interface IPinballEngine
{
    /// <summary>Matches <see cref="Models.TableManifest.Engine"/>.</summary>
    string Id { get; }
    string DisplayName { get; }
    bool SupportsDesktopMode { get; }
    /// <summary>One-line readiness summary for the Setup tab.</summary>
    string DescribeSetup(AppSettings s);
    /// <summary>Tables this engine owns that don't come from the file installer (e.g. store-bought tables).</summary>
    IEnumerable<InstalledTable> ListExtraTables(AppSettings s) => [];
    /// <summary>False when the tables belong to a store (Steam/Epic), so the app must not remove files.</summary>
    bool ManagesFiles => true;
    /// <summary>Starts the table and returns the process whose exit means "back to the picker".</summary>
    Task<Process> LaunchAsync(AppSettings s, InstalledTable table, bool vr, CancellationToken ct);
}

public sealed class VpxEngine : IPinballEngine
{
    public string Id => Models.TableManifest.Vpx;
    public string DisplayName => "Visual Pinball X";
    public bool SupportsDesktopMode => true;
    public string DescribeSetup(AppSettings s) => VpxSetup.InstalledVersion(s) is { } v
        ? $"Installed: {v}. {VpxSetup.DescribeOpenXrRuntime()}"
        : "Not installed.";
    public Task<Process> LaunchAsync(AppSettings s, InstalledTable table, bool vr, CancellationToken ct) =>
        Task.FromResult(Launcher.StartVpx(s, table.TablePath, vr));
}

public sealed class FuturePinballEngine : IPinballEngine
{
    public string Id => Models.TableManifest.FuturePinball;
    public string DisplayName => "Future Pinball (BAM)";
    // BAM decides VR vs desktop itself (VR when BAM-OpenVR is installed and SteamVR is running).
    public bool SupportsDesktopMode => false;
    public string DescribeSetup(AppSettings s) => FpSetup.Describe(s);
    public Task<Process> LaunchAsync(AppSettings s, InstalledTable table, bool vr, CancellationToken ct) =>
        Task.FromResult(Launcher.StartFp(s, table.TablePath));
}

public static class Engines
{
    public static readonly IReadOnlyList<IPinballEngine> All = [new VpxEngine(), new FuturePinballEngine(), new PinballFxUevrEngine(), new PinballMUevrEngine()];
    public static IPinballEngine For(string id) => All.FirstOrDefault(e => e.Id == id) ?? All[0];
}

public static class Launcher
{
    public static Process StartVpx(AppSettings s, string tablePath, bool vr)
    {
        var exe = VpxSetup.FindExe(s) ?? throw new InvalidOperationException("Visual Pinball X isn't installed yet (Setup tab).");
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        if (vr) VpxSetup.EnsureVrIni(s);
        foreach (var a in VpxArgs(vr ? VpxSetup.VrIniPath(s) : null, tablePath)) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    /// <summary>
    /// In VPX 10.8.1, -Play runs the player on its own (no editor) and the process exits when the
    /// table closes. -Minimized / -ExtMinimized only apply to editor commands; combining them with
    /// -Play is a command-line error ("must be used in conjunction with a command that uses the
    /// Win32 editor"), so they must not be passed.
    /// </summary>
    public static List<string> VpxArgs(string? iniPath, string tablePath)
    {
        var args = new List<string>();
        if (iniPath != null) args.AddRange(["-Ini", iniPath]);
        args.AddRange(["-Play", tablePath]);
        return args;
    }

    public static Process StartFp(AppSettings s, string tablePath)
    {
        var loader = FpSetup.LoaderPath(s);
        if (!File.Exists(loader)) throw new InvalidOperationException("BAM's FPLoader.exe wasn't found (Setup tab).");
        var psi = new ProcessStartInfo(loader)
        {
            WorkingDirectory = Path.GetDirectoryName(loader)!,
            // Verb "runas" needs the shell, which means arguments go in as one string.
            UseShellExecute = s.FpRunAsAdmin,
            Verb = s.FpRunAsAdmin ? "runas" : "",
            Arguments = $"/open \"{tablePath}\" /play /exit",
        };
        return Process.Start(psi)!;
    }
}
