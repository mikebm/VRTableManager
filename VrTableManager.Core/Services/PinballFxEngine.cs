using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using VrTableManager.Models;

namespace VrTableManager.Services;

public sealed record PfxInstall(string Store, string InstallDir, string LaunchUri);

/// <summary>
/// A Zen Studios Unreal Engine pinball game that plays in VR through UEVR.
/// Pinball FX and Pinball M (now sold as "Pinball FX Midnight") work the same way.
/// </summary>
public sealed record ZenGame(
    string EngineId,
    string Title,
    string SteamAppId,
    string[] EpicNames,
    string DefaultProcessName,
    string HeaderImage);

public static class ZenGames
{
    public static readonly ZenGame PinballFx = new(
        TableManifest.PinballFx, "Pinball FX", "2328760", ["Pinball FX"],
        "PinballFX-Win64-Shipping", "https://cdn.cloudflare.steamstatic.com/steam/apps/2328760/header.jpg");

    // Steam lists app 2337640 as "Pinball FX Midnight"; it launched as Pinball M and Epic may still use that name.
    public static readonly ZenGame PinballM = new(
        TableManifest.PinballM, "Pinball M", "2337640", ["Pinball M", "Pinball FX Midnight"],
        "PinballM-Win64-Shipping", "https://cdn.cloudflare.steamstatic.com/steam/apps/2337640/header.jpg");

    public static readonly IReadOnlyList<ZenGame> All = [PinballFx, PinballM];

    public static ZenGame? For(string engineId) => All.FirstOrDefault(g => g.EngineId == engineId);
}

/// <summary>
/// Pinball FX / Pinball M in VR through UEVR (github.com/praydog/UEVR).
///
/// What's automated: find the game (Steam or Epic), start UEVRInjector.exe with
/// --attach=&lt;game&gt;-Win64-Shipping (it waits for the game, then injects), start the game,
/// and close the injector when the game exits.
///
/// What isn't possible: these games have no command line to open a specific table, and owned
/// tables (DLC) aren't listed anywhere on disk, so each game shows up as one entry and you
/// pick the table inside it. The injector's OpenXR/OpenVR choice is a setting it remembers
/// from its own window; there's no command-line flag for it.
/// </summary>
public static partial class PfxSetup
{
    // Pinball FX values, kept for existing callers.
    public static string SteamAppId => ZenGames.PinballFx.SteamAppId;
    public static string ProcessName => ZenGames.PinballFx.DefaultProcessName;
    public static string HeaderImage => ZenGames.PinballFx.HeaderImage;

    /// <summary>UEVR keeps one profile folder per game, named after the game's process.</summary>
    public static string ProfileDirFor(ZenGame g) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnrealVRMod", ResolveProcessName(g));

    public static string ProfileDir => ProfileDirFor(ZenGames.PinballFx);

    public static bool HasProfileFor(ZenGame g)
    {
        var dir = ProfileDirFor(g);
        return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
    }

    public static bool HasProfile => HasProfileFor(ZenGames.PinballFx);

    public static PfxInstall? Detect(ZenGame? g = null)
    {
        g ??= ZenGames.PinballFx;
        return OperatingSystem.IsWindows() ? DetectSteam(g) ?? DetectEpic(g) : null;
    }

    /// <summary>
    /// The game's real process name, read from its install (…\Binaries\Win64\&lt;name&gt;-Win64-Shipping.exe),
    /// so UEVR attaches to the right process even if Zen renames it. Falls back to the usual name.
    /// </summary>
    public static string ResolveProcessName(ZenGame g)
    {
        if (Detect(g) is not { } inst) return g.DefaultProcessName;
        return ProcessNames.GetOrAdd(inst.InstallDir, dir => FindShippingExe(dir) ?? g.DefaultProcessName);
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ProcessNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Unreal layout: &lt;install&gt;\&lt;Project&gt;\Binaries\Win64\&lt;Project&gt;-Win64-Shipping.exe (only that folder is searched; installs are huge).</summary>
    public static string? FindShippingExe(string installDir)
    {
        try
        {
            foreach (var project in Directory.EnumerateDirectories(installDir))
            {
                var bin = Path.Combine(project, "Binaries", "Win64");
                if (!Directory.Exists(bin)) continue;
                var exe = Directory.EnumerateFiles(bin, "*-Win64-Shipping.exe").FirstOrDefault();
                if (exe != null) return Path.GetFileNameWithoutExtension(exe);
            }
        }
        catch { /* unreadable folder: use the default */ }
        return null;
    }

    // ---- Steam ---------------------------------------------------------------

    static PfxInstall? DetectSteam(ZenGame g)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string? steam;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steam = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
        }
        catch { return null; }
        if (steam == null) return null;

        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        var libraries = File.Exists(vdf) ? ParseLibraryFolders(File.ReadAllText(vdf)) : [];
        if (!libraries.Contains(steam, StringComparer.OrdinalIgnoreCase)) libraries.Insert(0, steam);

        foreach (var lib in libraries)
        {
            var acf = Path.Combine(lib, "steamapps", $"appmanifest_{g.SteamAppId}.acf");
            if (!File.Exists(acf)) continue;
            var dir = ParseInstallDir(File.ReadAllText(acf));
            if (dir == null) continue;
            var full = Path.Combine(lib, "steamapps", "common", dir);
            if (Directory.Exists(full)) return new PfxInstall("Steam", full, $"steam://rungameid/{g.SteamAppId}");
        }
        return null;
    }

    public static List<string> ParseLibraryFolders(string vdf) =>
        PathRegex().Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).ToList();

    public static string? ParseInstallDir(string acf) => InstallDirRegex().Match(acf) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();

    [GeneratedRegex("\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();

    // ---- Epic ----------------------------------------------------------------

    static PfxInstall? DetectEpic(ZenGame g)
    {
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests)) return null;
        foreach (var item in Directory.EnumerateFiles(manifests, "*.item"))
        {
            try
            {
                if (ParseEpicManifest(File.ReadAllText(item), g) is { } found) return found;
            }
            catch { /* unreadable manifest: skip */ }
        }
        return null;
    }

    public static PfxInstall? ParseEpicManifest(string json, ZenGame? g = null)
    {
        g ??= ZenGames.PinballFx;
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string? Get(string name) => r.TryGetProperty(name, out var v) ? v.GetString() : null;
        // Exact names only: "Pinball FX" must not match Pinball FX2/FX3 (not Unreal games) or "Pinball FX Midnight".
        var name = Get("DisplayName")?.Trim();
        if (!g.EpicNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) return null;
        var dir = Get("InstallLocation");
        if (string.IsNullOrEmpty(dir)) return null;
        var uri = $"com.epicgames.launcher://apps/{Get("CatalogNamespace")}%3A{Get("CatalogItemId")}%3A{Get("AppName")}?action=launch&silent=true";
        return new PfxInstall("Epic", dir, uri);
    }

    // ---- UEVR ----------------------------------------------------------------

    public static string InjectorPath(AppSettings s) => Path.Combine(s.UevrDir, "UEVRInjector.exe");

    public static IEnumerable<string> UevrCandidateDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, "VPinball", "UEVR");
        yield return Path.Combine(home, "Downloads", "UEVR");
        yield return Path.Combine(home, "Documents", "UEVR");
        yield return @"C:\UEVR";
        yield return @"C:\Games\UEVR";
    }

    public static string? AutoDetectUevr() =>
        UevrCandidateDirs().FirstOrDefault(d => File.Exists(Path.Combine(d, "UEVRInjector.exe")));

    /// <summary>Unpacks a downloaded UEVR profile zip into %APPDATA%\UnrealVRMod\&lt;game process&gt;.</summary>
    public static void ImportProfile(string zip, ZenGame? g = null)
    {
        g ??= ZenGames.PinballFx;
        var target = ProfileDirFor(g);
        var tmp = Path.Combine(Path.GetTempPath(), "vrtm-uevr-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            TableInstaller.Extract(zip, tmp);
            // Profiles are zipped either flat or inside a folder named after the game exe.
            var root = tmp;
            var entries = Directory.GetFileSystemEntries(tmp);
            if (entries.Length == 1 && Directory.Exists(entries[0])) root = entries[0];
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(target, Path.GetRelativePath(root, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    public static string Describe(AppSettings s, ZenGame? g = null)
    {
        g ??= ZenGames.PinballFx;
        if (!OperatingSystem.IsWindows()) return $"{g.Title} + UEVR is Windows-only.";
        var game = Detect(g);
        var parts = new List<string>
        {
            game == null ? $"{g.Title} not found (Steam or Epic)." : $"{g.Title} found ({game.Store}).",
            File.Exists(InjectorPath(s)) ? "UEVR found." : "UEVR not found. Install it from Updates above, or choose its folder.",
        };
        if (game != null)
            parts.Add(HasProfileFor(g) ? $"{g.Title} UEVR profile installed." : $"No UEVR profile for {g.Title} yet (optional, but recommended).");
        return string.Join(" ", parts);
    }
}

/// <summary>Launches a Zen pinball game with UEVR injected. One instance per game.</summary>
public class ZenUevrEngine(ZenGame game) : IPinballEngine
{
    public ZenGame Game { get; } = game;
    public string Id => Game.EngineId;
    public string DisplayName => $"{Game.Title} (UEVR)";
    public bool SupportsDesktopMode => true;
    public bool ManagesFiles => false;
    public string DescribeSetup(AppSettings s) => PfxSetup.Describe(s, Game);

    public IEnumerable<InstalledTable> ListExtraTables(AppSettings s)
    {
        if (PfxSetup.Detect(Game) is not { } found) yield break;
        yield return new InstalledTable(new TableManifest
        {
            Engine = Game.EngineId,
            GameName = Game.Title,
            Manufacturer = "Zen Studios",
            ImgUrl = Game.HeaderImage,
            VpxFile = $"{found.Store} — pick your table in the game",
        }, found.InstallDir, found.InstallDir);
    }

    public async Task<Process> LaunchAsync(AppSettings s, InstalledTable table, bool vr, CancellationToken ct)
    {
        var found = PfxSetup.Detect(Game) ?? throw new InvalidOperationException($"{Game.Title} isn't installed (Steam or Epic).");
        var processName = PfxSetup.ResolveProcessName(Game);

        Process? injector = null;
        if (vr)
        {
            var exe = PfxSetup.InjectorPath(s);
            if (!File.Exists(exe)) throw new InvalidOperationException("UEVRInjector.exe wasn't found (Setup tab).");
            // Pinball-tuned UEVR settings (level table, menus in front, Quest controllers off).
            // Applied here so launches from the VR lobby get them too.
            if (s.ApplyPinballUevrProfile)
            {
                try { UevrPinballProfile.Apply(Game); }
                catch { /* a locked or unreadable profile shouldn't stop the game */ }
            }
            // The injector polls for this process name, injects once the game has a window
            // and has loaded D3D, and remembers its OpenXR/OpenVR choice from last time.
            injector = Process.Start(new ProcessStartInfo(exe, $"--attach={processName}")
            {
                WorkingDirectory = s.UevrDir,
                UseShellExecute = true,
            });
        }

        Process.Start(new ProcessStartInfo(found.LaunchUri) { UseShellExecute = true });

        // The store launcher starts the real game process; wait for it to appear.
        var deadline = DateTime.UtcNow.AddMinutes(3);
        Process? running = null;
        while (running == null && DateTime.UtcNow < deadline)
        {
            running = Process.GetProcessesByName(processName).FirstOrDefault();
            if (running == null) await Task.Delay(1000, ct);
        }
        if (running == null)
        {
            CloseQuietly(injector);
            throw new TimeoutException($"{Game.Title} didn't start within 3 minutes.");
        }

        // UEVR's injector stays open after injecting; close it when the game exits.
        // (Own Process handle: the caller disposes the one we return.)
        if (injector != null)
        {
            var watch = Process.GetProcessById(running.Id);
            _ = watch.WaitForExitAsync().ContinueWith(_ => { CloseQuietly(injector); watch.Dispose(); }, TaskScheduler.Default);
        }
        return running;
    }

    static void CloseQuietly(Process? p)
    {
        if (p == null) return;
        try
        {
            if (p.HasExited) return;
            p.CloseMainWindow();
            if (!p.WaitForExit(5000)) p.Kill();
        }
        catch { /* already gone */ }
    }
}

public sealed class PinballFxUevrEngine() : ZenUevrEngine(ZenGames.PinballFx);

public sealed class PinballMUevrEngine() : ZenUevrEngine(ZenGames.PinballM);
