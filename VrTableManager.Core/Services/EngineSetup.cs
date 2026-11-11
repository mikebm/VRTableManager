using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VrTableManager.Services;

/// <param name="Nightly">A build from VPX's master branch (GitHub Actions), not a published release.</param>
public sealed record VpxRelease(
    string Tag,
    string AssetName,
    string Url,
    long Size,
    bool Nightly = false
);

/// <param name="Latest">Newest BGFX build that's fine to use.</param>
/// <param name="Withdrawn">Newest BGFX build renamed "DO NOT USE", when it's newer than <paramref name="Latest"/>.</param>
public sealed record VpxReleases(VpxRelease? Latest, VpxRelease? Withdrawn);

/// <summary>
/// A build the Setup tab's build list offers, so a player can go back to an older one (a nightly that broke
/// something, say). A release knows its download; a nightly only its run, whose download is looked up when it's
/// installed (<see cref="VpxSetup.ResolveAsync"/>), so listing them costs one GitHub call, not one per build.
/// </summary>
public sealed record VpxBuildChoice(
    string Label,
    DateTimeOffset Date,
    VpxRelease? Release = null,
    string? ArtifactsUrl = null,
    string? Sha = null
)
{
    /// <summary>The build VPX's folder has now (shown in the list).</summary>
    public bool Installed { get; init; }

    public override string ToString() => Installed ? Label + " · installed" : Label;
}

/// <summary>Installs Visual Pinball X (BGFX build, which does VR through OpenXR) and prepares a VR settings file.</summary>
public static class VpxSetup
{
    const string ReleasesApi =
        "https://api.github.com/repos/vpinball/vpinball/releases?per_page=30";
    const string VersionFile = "vrtm-version.txt";

    /// <summary>Asset name suffix for this OS. BGFX is the build with OpenXR VR support.</summary>
    static string AssetSuffix =>
        OperatingSystem.IsWindows() ? "-windows-x64-Release.zip"
        : OperatingSystem.IsMacOS()
            ? (
                RuntimeInformation.OSArchitecture == Architecture.Arm64
                    ? "-macos-arm64-Release.dmg"
                    : "-macos-x64-Release.dmg"
            )
        : "-linux-x64-Release.tar.gz";

    /// <summary>The newest usable BGFX build, or null.</summary>
    public static async Task<VpxRelease?> FindLatestAsync(
        CancellationToken ct,
        bool useDoNotUse = false,
        bool nightly = false
    ) => (await FindReleasesAsync(ct, useDoNotUse, nightly)).Latest;

    /// <summary>
    /// What GitHub offers: the newest BGFX build that's fine to use, and the newest one the VPX team has
    /// since renamed "DO NOT USE" when that's newer still. Unless <paramref name="useDoNotUse"/> (the
    /// "use DO NOT USE builds" setting), those are never installed or offered as updates; the app keeps
    /// whatever VPX is installed and says why. With <paramref name="nightly"/> (the "nightly builds" setting),
    /// the newest master-branch build stands in for the release when it's newer.
    /// </summary>
    public static async Task<VpxReleases> FindReleasesAsync(
        CancellationToken ct,
        bool useDoNotUse = false,
        bool nightly = false
    )
    {
        var releases = ParseReleases(
            await Http.Client.GetStringAsync(ReleasesApi, ct),
            useDoNotUse
        );
        return nightly ? WithNightly(releases, await FindNightlyAsync(ct)) : releases;
    }

    /// <summary>The nightly build in place of the release when it's newer (it then supersedes any "DO NOT USE" note too).</summary>
    public static VpxReleases WithNightly(VpxReleases r, VpxRelease? nightly) =>
        nightly == null || (r.Latest != null && !IsNewer(nightly.Tag, r.Latest.Tag))
            ? r
            : new VpxReleases(
                nightly,
                r.Withdrawn != null && IsNewer(r.Withdrawn.Tag, nightly.Tag) ? r.Withdrawn : null
            );

    // Every push to VPX's master branch builds all platforms in the "vpinball" workflow. GitHub only hands
    // workflow artifacts to signed-in users; nightly.link serves the same files publicly.
    const string NightlyRunsApi =
        "https://api.github.com/repos/vpinball/vpinball/actions/workflows/vpinball.yml/runs?branch=master&event=push&status=success&per_page=1";

    // The build list's nightlies: VPX's master branch builds several times a day, so this reaches back about a week.
    const string NightlyRunsListApi =
        "https://api.github.com/repos/vpinball/vpinball/actions/workflows/vpinball.yml/runs?branch=master&event=push&status=success&per_page=60";
    const string NightlyDownload = "https://nightly.link/vpinball/vpinball/actions/artifacts/";

    /// <summary>The BGFX build from the newest successful master-branch run, or null.</summary>
    public static async Task<VpxRelease?> FindNightlyAsync(CancellationToken ct) =>
        ParseNightlyRuns(await Http.Client.GetStringAsync(NightlyRunsApi, ct)).FirstOrDefault()
            is { } run
            ? await ResolveAsync(run, ct)
            : null;

    /// <summary>
    /// Every build the build list offers, releases first, each newest first: the releases (those marked "DO NOT USE" only with
    /// <paramref name="useDoNotUse"/>), and with <paramref name="nightly"/> the recent nightlies too.
    /// </summary>
    public static async Task<List<VpxBuildChoice>> FindBuildsAsync(
        CancellationToken ct,
        bool useDoNotUse,
        bool nightly
    )
    {
        var releases = Http.Client.GetStringAsync(ReleasesApi, ct);
        var runs = nightly ? Http.Client.GetStringAsync(NightlyRunsListApi, ct) : null;
        var builds = ParseReleaseList(await releases, useDoNotUse);
        if (runs != null)
        {
            builds.AddRange(ParseNightlyRuns(await runs));
        }
        // Releases first (a handful, easy to find by version), then the nightlies: by date alone a week of nightlies
        // would bury every release.
        return [.. builds.OrderBy(b => b.Release == null).ThenByDescending(b => b.Date)];
    }

    /// <summary>Every release with a BGFX build for this OS (not drafts; "DO NOT USE" ones only when allowed).</summary>
    public static List<VpxBuildChoice> ParseReleaseList(string json, bool useDoNotUse)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<VpxBuildChoice>();
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            var name = rel.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var doNotUse = name.StartsWith("DO NOT USE", StringComparison.OrdinalIgnoreCase);
            if (
                rel.GetProperty("draft").GetBoolean()
                || (doNotUse && !useDoNotUse)
                || BgfxAsset(rel) is not { } asset
            )
            {
                continue;
            }

            var date = DateOf(rel, "published_at");
            list.Add(
                new VpxBuildChoice(
                    $"{asset.Tag} · {date:yyyy-MM-dd}" + (doNotUse ? " · marked DO NOT USE" : ""),
                    date,
                    Release: asset
                )
            );
        }
        return list;
    }

    /// <summary>Successful master-branch runs (one nightly build each), newest first.</summary>
    public static List<VpxBuildChoice> ParseNightlyRuns(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<VpxBuildChoice>();
        foreach (var run in doc.RootElement.GetProperty("workflow_runs").EnumerateArray())
        {
            var sha = run.GetProperty("head_sha").GetString() ?? "";
            var date = DateOf(run, "created_at");
            list.Add(
                new VpxBuildChoice(
                    $"Nightly · {date:yyyy-MM-dd HH:mm} UTC · {sha[..Math.Min(9, sha.Length)]}",
                    date,
                    ArtifactsUrl: run.GetProperty("artifacts_url").GetString() + "?per_page=100",
                    Sha: sha
                )
            );
        }
        return list;
    }

    static DateTimeOffset DateOf(JsonElement e, string property) =>
        e.TryGetProperty(property, out var d)
        && d.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(d.GetString(), out var date)
            ? date
            : DateTimeOffset.MinValue;

    /// <summary>
    /// The download for a build in the list: a release's own, or a nightly run's BGFX artifact (null when GitHub
    /// no longer has it: workflow artifacts expire).
    /// </summary>
    public static async Task<VpxRelease?> ResolveAsync(
        VpxBuildChoice build,
        CancellationToken ct
    ) =>
        build.Release
        ?? (
            build.ArtifactsUrl is { } url
                ? ParseNightlyArtifacts(await Http.Client.GetStringAsync(url, ct))
                : null
        );

    /// <summary>Whether <paramref name="installed"/> (a tag like "v10.8.1-6625-2ad1916b2") is this build.</summary>
    public static bool IsInstalled(VpxBuildChoice build, string? installed) =>
        installed != null
        && (
            build.Release is { } r
                ? installed == r.Tag || SameBuild(installed, r.Tag)
                : build.Sha is { Length: >= 7 } sha
                    && installed.Split('-') is [_, _, var hash, ..]
                    && hash.Length >= 7
                    && sha.StartsWith(hash, StringComparison.OrdinalIgnoreCase)
        );

    public static VpxRelease? ParseNightlyArtifacts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var a in doc.RootElement.GetProperty("artifacts").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (
                a.GetProperty("expired").GetBoolean()
                || !name.StartsWith("VPinballX_BGFX-")
                || !name.EndsWith(AssetSuffix)
            )
            {
                continue;
            }
            // "VPinballX_BGFX-10.8.1-6625-2ad1916b2-windows-x64-Release.zip" → "v10.8.1-6625-2ad1916b2"
            var tag = "v" + name["VPinballX_BGFX-".Length..^AssetSuffix.Length];
            return new VpxRelease(
                tag,
                name,
                NightlyDownload + a.GetProperty("id").GetInt64() + ".zip",
                a.GetProperty("size_in_bytes").GetInt64(),
                Nightly: true
            );
        }
        return null;
    }

    public static VpxReleases ParseReleases(string json, bool useDoNotUse = false)
    {
        using var doc = JsonDocument.Parse(json);
        VpxRelease? withdrawn = null;
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.GetProperty("draft").GetBoolean())
            {
                continue;
            }

            var name = rel.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var asset = BgfxAsset(rel);
            if (asset == null)
            {
                continue;
            }

            if (useDoNotUse || !name.StartsWith("DO NOT USE", StringComparison.OrdinalIgnoreCase))
            {
                return new VpxReleases(asset, withdrawn);
            }

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
            {
                return new VpxRelease(
                    rel.GetProperty("tag_name").GetString()!,
                    an,
                    asset.GetProperty("browser_download_url").GetString()!,
                    asset.GetProperty("size").GetInt64()
                );
            }
        }
        return null;
    }

    /// <summary>
    /// The Setup tab's line about VPX updates, and whether to offer one. <paramref name="kept"/> is a build the
    /// player picked from the build list (<see cref="AppSettings.VpxKeptBuild"/>): while it's installed, newer
    /// builds are mentioned but not offered, so going back to an older build doesn't get undone by an update.
    /// </summary>
    public static (string Text, bool HasUpdate) DescribeUpdate(
        VpxReleases r,
        string? installed,
        string? kept = null
    )
    {
        var note = r.Withdrawn is { } w
            ? $" The VPX team has marked newer builds (up to {w.Tag}) \"DO NOT USE\" on GitHub, so they aren't offered."
            : "";
        if (r.Latest == null)
        {
            return (
                installed != null ? $"Keeping your installed {installed}.{note}"
                : r.Withdrawn != null
                    ? $"No VR (BGFX) build is available to install right now.{note} Check github.com/vpinball/vpinball/releases."
                : "No VPX release found for this OS.",
                false
            );
        }

        var latest = r.Latest.Nightly ? $"{r.Latest.Tag} (nightly)" : r.Latest.Tag;
        if (installed == null)
        {
            return ($"Not installed. Latest is {latest}.{note}", false);
        }

        if (installed == r.Latest.Tag || SameBuild(installed, r.Latest.Tag))
        {
            return ($"Up to date ({installed}).{note}", false);
        }
        if (kept != null && (installed == kept || SameBuild(installed, kept)))
        {
            return (
                $"Keeping {installed}, the build you picked. Newest is {latest}: INSTALL / UPDATE VPX goes back to it.",
                false
            );
        }
        // Installed something newer than the newest usable build (e.g. a build later marked DO NOT USE): keep it.
        if (installed == r.Withdrawn?.Tag || IsNewer(installed, r.Latest.Tag))
        {
            return ($"Keeping your installed {installed}.{note}", false);
        }

        return ($"{installed} → {latest}", true);
    }

    static (Version v, int build) ParseTag(string t)
    {
        var parts = t.TrimStart('v').Split('-');
        return (
            Version.TryParse(parts[0], out var v) ? v : new Version(0, 0),
            parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0
        );
    }

    /// <summary>Compares tags like "v10.8.1-5436-af26b2d93" by their version and build number.</summary>
    static bool IsNewer(string a, string b)
    {
        var (va, ba) = ParseTag(a);
        var (vb, bb) = ParseTag(b);
        return va != vb ? va > vb : ba > bb;
    }

    static bool SameBuild(string a, string b) => ParseTag(a) == ParseTag(b);

    /// <summary>
    /// The installed build's tag: the one this app wrote when it installed VPX, else (VPX unzipped by hand into the
    /// folder) read from the program's own version ("10.8.1.5436.af26b2d93" → "v10.8.1-5436-af26b2d93").
    /// </summary>
    public static string? InstalledVersion(AppSettings s)
    {
        if (FindExe(s) is not { } exe)
        {
            return null;
        }

        var f = Path.Combine(s.VpxDir, VersionFile);
        if (File.Exists(f))
        {
            return File.ReadAllText(f).Trim();
        }

        return TagFromProductVersion(FileVersionInfo.GetVersionInfo(exe).ProductVersion);
    }

    public static string? TagFromProductVersion(string? productVersion)
    {
        var p = (productVersion ?? "").Trim().Split('.');
        if (p.Length < 4 || !p.Take(4).All(x => int.TryParse(x, out _)))
        {
            return null;
        }

        return $"v{p[0]}.{p[1]}.{p[2]}-{p[3]}" + (p.Length > 4 && p[4] != "" ? "-" + p[4] : "");
    }

    public static async Task InstallAsync(
        AppSettings s,
        VpxRelease rel,
        IProgress<double> progress,
        Action<string> status,
        CancellationToken ct
    )
    {
        var tmp = Path.Combine(Path.GetTempPath(), rel.AssetName);
        status($"Downloading {rel.AssetName} ({rel.Size / 1_048_576} MB)…");
        try
        {
            await Http.DownloadFileAsync(rel.Url, tmp, progress, ct);
        }
        catch
        {
            try
            {
                File.Delete(tmp);
            }
            catch { } // don't leave a half download in Temp
            throw;
        }
        // A nightly comes as a GitHub artifact: always a zip. On Windows that zip is the build itself; elsewhere
        // it holds the .dmg / .tar.gz, which is taken out and used as the download.
        if (rel.Nightly && !rel.AssetName.EndsWith(".zip"))
        {
            var wrapped = tmp + ".zip";
            File.Move(tmp, wrapped, overwrite: true);
            using (var zip = ZipFile.OpenRead(wrapped))
            {
                (
                    zip.GetEntry(rel.AssetName)
                    ?? throw new InvalidDataException(
                        $"{rel.AssetName} wasn't in the nightly download."
                    )
                ).ExtractToFile(tmp, overwrite: true);
            }

            File.Delete(wrapped);
        }

        if (tmp.EndsWith(".dmg"))
        {
            // macOS: hand the disk image to Finder; the user drags the app out.
            Process.Start("open", tmp);
            status("Opened the VPX disk image. Drag VPinballX to Applications.");
            return;
        }

        status("Extracting… (antivirus may scan the new files; this can take a minute)");
        Directory.CreateDirectory(s.VpxDir);
        await Task.Run(() => TableInstaller.Extract(tmp, s.VpxDir), ct);
        await File.WriteAllTextAsync(Path.Combine(s.VpxDir, VersionFile), rel.Tag, ct);
        File.Delete(tmp);
        EnsureVrIni(s);
    }

    public static string? FindExe(AppSettings s)
    {
        if (!Directory.Exists(s.VpxDir))
        {
            return null;
        }

        var files = Directory
            .EnumerateFiles(s.VpxDir, "VPinballX*", SearchOption.AllDirectories)
            .Where(f =>
                OperatingSystem.IsWindows()
                    ? f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    : Path.GetExtension(f) == ""
            )
            .ToList();
        return files.FirstOrDefault(f =>
                Path.GetFileName(f).Contains("BGFX", StringComparison.OrdinalIgnoreCase)
            ) ?? files.FirstOrDefault();
    }

    /// <summary>The manager's VR overrides on top of the player's own VPinballX.ini (<see cref="VpxIniLayers"/>).</summary>
    public static string VrIniPath(AppSettings s) =>
        Path.Combine(s.VpxDir, "VrTableManager-VR.ini");

    /// <summary>The manager's desktop overrides on top of the player's own VPinballX.ini (<see cref="VpxIniLayers"/>).</summary>
    public static string DesktopIniPath(AppSettings s) =>
        Path.Combine(s.VpxDir, "VrTableManager-Desktop.ini");

    /// <summary>
    /// VPX reads [PlayerVR] AskToTurnOn (0 = Enabled, 1 = Autodetect, 2 = Disabled).
    /// These are overrides: the player's own VPinballX.ini stays untouched.
    /// </summary>
    public static void EnsureVrIni(AppSettings s)
    {
        var ini = VrIniPath(s);
        IniFile.Set(ini, "PlayerVR", "AskToTurnOn", "0");
        var rate = s.VrRefreshRate switch
        {
            72 => 1,
            80 => 2,
            90 => 3,
            120 => 4,
            _ => 0,
        };
        IniFile.Set(ini, "PlayerVR", "DisplayRefreshRate", rate.ToString());
        SetPlugins(s, ini);
        // VPX's default backglass ([PlayerVR] AddBackglass) isn't set here: it's picked per table at launch
        // (VrBackglassFor), or left to VPX's own setting when that's turned off.
        // Controls aren't copied in: VR play inherits the ones in the player's own VPinballX.ini (and keeps
        // following them) unless it has its own (VpxInput.Import, or remapped during VR play).
    }

    /// <summary>
    /// VPX's default backglass for one VR launch: "0" for a VR table, "1" for a desktop or cabinet one, null to leave
    /// VPX's own setting. The PuP and B2S plugins draw into a backglass display VPX only puts in the VR scene when the
    /// table has its own VR backglass, so a desktop table's PuP pack or .directb2s needs VPX's default one added. A
    /// VR table has its own backbox and DMD, and VPX's panel gets in their way. A table without catalog features
    /// (added from a loose file) isn't known either way, so VPX decides. VPX's table setting still beats this.
    /// </summary>
    public static string? VrBackglassFor(AppSettings s, Models.TableManifest? table) =>
        !s.VrAutoBackglass || table is null || table.Features.Count == 0 ? null
        : table.Features.Any(f =>
            f.Equals("VR", StringComparison.OrdinalIgnoreCase)
            || f.Equals("Hybrid", StringComparison.OrdinalIgnoreCase)
        )
            ? "0"
        : "1";

    /// <summary>
    /// Desktop play: VR off even if the player's own settings turn it on, and the same plugins as VR, so the
    /// table's ROMs, PuP pack and backglass are found in its own folder (without the built-in PinMAME, VPX asks a
    /// separately installed VPinMAME, which looks in its own roms folder).
    /// </summary>
    public static void EnsureDesktopIni(AppSettings s)
    {
        var ini = DesktopIniPath(s);
        IniFile.Set(ini, "PlayerVR", "AskToTurnOn", "2");
        SetPlugins(s, ini);
    }

    static void SetPlugins(AppSettings s, string ini)
    {
        // VPX's built-in PinMAME reads each table's own pinmame\roms, nvram and alias.txt (where this app
        // puts them); without it VPX falls back to a separately installed VPinMAME and its global roms
        // folder. Serum, VNI and AltSound are its colorization and sound companions, read from the table folder.
        var on = s.UseBuiltInPinmame ? "1" : "0";
        foreach (var plugin in BuiltInPinmamePlugins)
        {
            IniFile.Set(ini, "Plugin." + plugin, "Enable", on);
        }
        // PinUP Player plugin: plays a table's pupvideos\<pack> on the backglass/DMD; tables without one are unaffected.
        IniFile.Set(ini, "Plugin." + PupPlugin, "Enable", s.UsePupPlugin ? "1" : "0");
        // FlexDMD stands in for the FlexDMD/UltraDMD components many originals draw their DMD with (Jaws,
        // Mass Effect); B2S shows a table's .directb2s backglass. Set explicitly rather than left to VPX's default.
        var display = s.UseDisplayPlugins ? "1" : "0";
        foreach (var plugin in DisplayPlugins)
        {
            IniFile.Set(ini, "Plugin." + plugin, "Enable", display);
        }
    }

    /// <summary>Id of VPX's PinUP Player plugin (plugins\pup\plugin.cfg).</summary>
    public const string PupPlugin = "PUP";

    /// <summary>VPX's plugins for script-drawn DMDs and .directb2s backglasses.</summary>
    public static readonly string[] DisplayPlugins = ["FlexDMD", "B2S"];

    public static readonly string[] BuiltInPinmamePlugins = ["PinMAME", "Serum", "VNI", "AltSound"];

    /// <summary>Which OpenXR runtime VPX will talk to (Windows only).</summary>
    public static string DescribeOpenXrRuntime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Check your OpenXR runtime (e.g. SteamVR or Monado).";
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Khronos\OpenXR\1"
            );
            var path = key?.GetValue("ActiveRuntime") as string;
            if (string.IsNullOrEmpty(path))
            {
                return "No OpenXR runtime found. Install the Meta Quest Link app (or SteamVR / Virtual Desktop).";
            }

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
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Future Pinball"
        );
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Future Pinball"
        );
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "VPinball",
            "Future Pinball"
        );
    }

    public static string? AutoDetect() =>
        OperatingSystem.IsWindows()
            ? CandidateDirs()
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "Future Pinball.exe")))
            : null;

    public static string LoaderPath(AppSettings s) => Path.Combine(s.FpDir, "BAM", "FPLoader.exe");

    public static string Describe(AppSettings s)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Future Pinball is Windows-only.";
        }

        if (
            string.IsNullOrWhiteSpace(s.FpDir)
            || !File.Exists(Path.Combine(s.FpDir, "Future Pinball.exe"))
        )
        {
            return "Future Pinball not found. Install it, then choose its folder.";
        }

        if (!File.Exists(LoaderPath(s)))
        {
            return "Future Pinball found, but BAM is missing (BAM\\FPLoader.exe). Install BAM to enable VR.";
        }

        var vr =
            File.Exists(Path.Combine(s.FpDir, "BAM", "openvr_api.dll"))
            || Directory
                .EnumerateFiles(
                    Path.Combine(s.FpDir, "BAM"),
                    "*OpenVR*",
                    SearchOption.TopDirectoryOnly
                )
                .Any();
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

    public string DescribeSetup(AppSettings s) =>
        VpxSetup.InstalledVersion(s) is { } v
            ? $"Installed: {v}. {VpxSetup.DescribeOpenXrRuntime()}"
            : "Not installed.";

    public Task<Process> LaunchAsync(
        AppSettings s,
        InstalledTable table,
        bool vr,
        CancellationToken ct
    ) => Task.FromResult(Launcher.StartVpx(s, table.TablePath, vr, table.Manifest));
}

public sealed class FuturePinballEngine : IPinballEngine
{
    public string Id => Models.TableManifest.FuturePinball;
    public string DisplayName => "Future Pinball (BAM)";

    // BAM decides VR vs desktop itself (VR when BAM-OpenVR is installed and SteamVR is running).
    public bool SupportsDesktopMode => false;

    public string DescribeSetup(AppSettings s) => FpSetup.Describe(s);

    public Task<Process> LaunchAsync(
        AppSettings s,
        InstalledTable table,
        bool vr,
        CancellationToken ct
    ) => Task.FromResult(Launcher.StartFp(s, table.TablePath));
}

public static class Engines
{
    public static readonly IReadOnlyList<IPinballEngine> All =
    [
        new VpxEngine(),
        new FuturePinballEngine(),
        new PinballFxUevrEngine(),
        new PinballMUevrEngine(),
    ];

    public static IPinballEngine For(string id) => All.FirstOrDefault(e => e.Id == id) ?? All[0];
}

public static class Launcher
{
    public static Process StartVpx(
        AppSettings s,
        string tablePath,
        bool vr,
        Models.TableManifest? table = null
    )
    {
        var exe =
            VpxSetup.FindExe(s)
            ?? throw new InvalidOperationException(
                "Visual Pinball X isn't installed yet (Setup tab)."
            );
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
        };
        if (vr)
        {
            VpxSetup.EnsureVrIni(s);
        }
        else
        {
            VpxSetup.EnsureDesktopIni(s);
        }
        // Only in this launch's play file, not the overrides: the next table gets its own pick.
        var extra = new List<(string, string, string)>();
        // A VR cabinet with a blank backglass whose PuP pack draws one: VPX's backglass, moved onto the cabinet by
        // the script copy (VrBackglass). Without it on, the PuP plugin's backglass shows nowhere in VR.
        var cabinetBackglass =
            vr && s.UsePupPlugin && s.VrAutoBackglass ? VrBackglass.Find(tablePath) : null;
        if (cabinetBackglass != null)
        {
            extra.Add(("PlayerVR", "AddBackglass", "1"));
        }
        else if (vr && VpxSetup.VrBackglassFor(s, table) is { } backglass)
        {
            extra.Add(("PlayerVR", "AddBackglass", backglass));
        }
        if (s.UsePupPlugin && PupRootFor(tablePath) is { } pupFolder)
        {
            extra.Add(("Plugin." + VpxSetup.PupPlugin, "PUPFolder", pupFolder));
        }
        // Fit VR to the player: a real cabinet's lockbar height and eye position until they pick their own, and in
        // real-size mode this table's own lockbar width (never kept, or the next table would get it).
        List<(string Section, string Key)> notKept = [];
        if (vr)
        {
            var fit = VrFit.LaunchSettings(s, VpxIniLayers.Effective(s, vr: true), tablePath);
            extra.AddRange(fit.Extra);
            notKept.AddRange(fit.NotKept);
            if (fit.Extra.Count > 0)
            {
                PlaySession.Log(
                    $"{Path.GetFileName(tablePath)}: VR fit "
                        + string.Join(", ", fit.Extra.Select(e => $"{e.Key} = {e.Value}"))
                );
            }
        }
        var play = VpxIniLayers.Build(
            s,
            vr,
            extra: extra,
            notKept: notKept,
            // A position the table keeps for itself isn't everyone's, and a height change in the headset is this
            // table's own (VrFit.OwnPositionKeys, KeepTableHeight), as is a graphics setting its .ini sets
            // (VpxGraphics.OwnTableKeys, e.g. its render scale). All read the table's .ini when it closes.
            afterPlay: vr && tablePath.EndsWith(".vpx", StringComparison.OrdinalIgnoreCase)
                ? new VpxIniLayers.AfterPlay(
                    NotKept: () =>
                        VrFit
                            .OwnPositionKeys(tablePath)
                            .Concat(VpxGraphics.OwnTableKeys(tablePath)),
                    After: (before, after) => VrFit.KeepTableHeight(tablePath, before, after)
                )
                : null
        );
        if (
            VrRoomSwitch.Prepare(tablePath, vr, s.AutoVrRoom, s.UsePupPlugin, cabinetBackglass) is
            { } changed
        )
        {
            PlaySession.Log($"{Path.GetFileName(tablePath)}: {changed}");
        }
        // Without VPX's texture list (written when a table closes) a table's first launch plays very dark.
        if (VpxTextureCache.Prepare(tablePath) is { } textures)
        {
            PlaySession.Log($"{Path.GetFileName(tablePath)}: {textures}");
        }
        // PuP packs on the layout for this launch: the VR one (packs installed before VR layouts were chosen, or
        // copied from another install, get theirs now, once), or the pack's desktop one for desktop play.
        if (s.UsePupPlugin)
        {
            var pupDir = Path.Combine(Path.GetDirectoryName(tablePath)!, PupPacks.FolderName);
            foreach (var pack in PupPackOptions.SwitchFor(pupDir, vr))
            {
                PlaySession.Log(
                    $"{Path.GetFileName(tablePath)}: PuP pack switched to its {(vr ? "VR" : "desktop")} layout ({pack})"
                );
            }
            if (vr)
            {
                foreach (var pack in PupPackOptions.SetUpOnce(pupDir))
                {
                    PlaySession.Log(
                        $"{Path.GetFileName(tablePath)}: PuP pack switched to its VR layout ({pack})"
                    );
                }
            }
            // After the switch: a pack put back on its VR layout by this launch gets them too.
            foreach (var pack in PupPackOptions.WriteMissingEchoes(pupDir))
            {
                PlaySession.Log(
                    $"{Path.GetFileName(tablePath)}: PuP pack's layout files written as its .bat does ({pack})"
                );
            }
        }

        foreach (var a in VpxArgs(play.Path, tablePath))
        {
            psi.ArgumentList.Add(a);
        }

        var proc = Process.Start(psi)!;
        VpxIniLayers.SaveBackOnExit(proc, play, PlaySession.Log);
        return proc;
    }

    /// <summary>
    /// The PinUP Player plugin's folder for this launch: the table's own folder, when it has a pupvideos\.
    /// The plugin finds the pack there either way, but until the pack opens, a script's PuPlayer.GetRoot is the
    /// plugin's global folder, which is empty when none is set. Scripts that build paths from it as they load
    /// (<c>PuPlayer.GetRoot &amp; "\" &amp; cGameName</c>) need it to be the table's pupvideos\. The plugin
    /// (10.8.1 rev 6625) returns it without a trailing backslash, unlike PinUP Player itself; scripts that leave
    /// the backslash out get one from <see cref="PupRootPaths"/>.
    /// </summary>
    public static string? PupRootFor(string tablePath)
    {
        var dir = Path.GetDirectoryName(tablePath);
        return dir != null && Directory.Exists(Path.Combine(dir, PupPacks.FolderName)) ? dir : null;
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
        if (iniPath != null)
        {
            args.AddRange(["-Ini", iniPath]);
        }

        args.AddRange(["-Play", tablePath]);
        return args;
    }

    public static Process StartFp(AppSettings s, string tablePath)
    {
        var loader = FpSetup.LoaderPath(s);
        if (!File.Exists(loader))
        {
            throw new InvalidOperationException("BAM's FPLoader.exe wasn't found (Setup tab).");
        }

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
