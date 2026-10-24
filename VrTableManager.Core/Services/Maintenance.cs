using System.IO.Compression;
using VrTableManager.Models;

namespace VrTableManager.Services;

/// <summary>A folder the app uses, with its size, shown on the Setup tab's Storage panel.</summary>
public sealed record StorageEntry(string Name, string Description, IReadOnlyList<string> Paths, long Bytes, bool CanClear)
{
    public string SizeText => DiskUsage.Format(Bytes);
}

public static class DiskUsage
{
    public static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Sum(f => f.Length);
        }
        catch { return 0; }
    }

    /// <summary>A table's disk use: its whole folder when it has its own, otherwise just the table file.</summary>
    public static long SizeOf(InstalledTable t, AppSettings s) =>
        t.IsVpx && !SamePath(t.Folder, s.TablesDir) ? SizeOf(t.Folder)
        : File.Exists(t.TablePath) ? SizeOf(t.TablePath) : 0;

    public static string Format(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0} MB",
        _ => $"{bytes / 1073741824.0:0.0} GB",
    };

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Where things live, for the "open folder" buttons and the Storage panel.</summary>
public static class AppFolders
{
    public static string Downloads => Path.Combine(AppSettings.DataDir, "downloads");
    public static string ArtworkCache => Path.Combine(AppSettings.DataDir, "img-cache");
    public static string CabinetCache => Path.Combine(AppSettings.DataDir, "cabinet-cache");
    public static string BrowserProfile => Path.Combine(AppSettings.DataDir, "browser-profile");
    public static string Godot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrTableManager", "godot");
    public static string Backups => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VR Table Manager backups");

    /// <summary>VPX's own settings and log folder (%APPDATA%\VPinballX).</summary>
    public static string VpxPrefs => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPinballX");

    /// <summary>VPX's texture caches: one per table folder, plus the shared ones in its settings folder.</summary>
    public static List<string> VpxTextureCaches(AppSettings s)
    {
        var dirs = new List<string>();
        try
        {
            if (Directory.Exists(VpxPrefs))
                dirs.AddRange(Directory.EnumerateDirectories(VpxPrefs, "Cache", SearchOption.AllDirectories));
            if (Directory.Exists(s.TablesDir))
                dirs.AddRange(Directory.EnumerateDirectories(s.TablesDir).Select(d => Path.Combine(d, "cache")).Where(Directory.Exists));
        }
        catch { /* list what we could */ }
        return dirs;
    }

    public static List<StorageEntry> Survey(AppSettings s)
    {
        StorageEntry E(string name, string desc, bool clear, params string[] paths) =>
            new(name, desc, paths, paths.Sum(DiskUsage.SizeOf), clear);
        var caches = VpxTextureCaches(s);
        return
        [
            E("Tables", "Your VPX tables, with their ROMs and settings.", false, s.TablesDir),
            new("VPX texture caches", "VPX saves processed textures so tables start faster. Safe to clear; the next start of each table is slower.",
                caches, caches.Sum(DiskUsage.SizeOf), true),
            E("Unfinished downloads", "Leftovers from downloads that were cut off. Safe to clear.", true, Downloads),
            E("Artwork cache", "Table pictures shown in this app and the lobby. Downloaded again when needed.", true, ArtworkCache),
            E("Lobby cabinet cache", "Playfield images the VR lobby made from your tables. Rebuilt when needed.", true, CabinetCache),
            E("Sign-in browser", "The browser profile that keeps you signed in to the table sites. Not cleared here (you'd be signed out).", false, BrowserProfile),
            E("Godot (lobby engine)", "The engine the VR lobby runs on.", false, Godot),
        ];
    }

    /// <summary>Deletes what's inside the folders (not the folders themselves). Returns the bytes freed.</summary>
    public static long Clear(StorageEntry entry)
    {
        long freed = 0;
        foreach (var dir in entry.Paths.Where(Directory.Exists))
        {
            foreach (var f in Directory.EnumerateFiles(dir))
                try { var len = new FileInfo(f).Length; File.Delete(f); freed += len; } catch { }
            foreach (var d in Directory.EnumerateDirectories(dir))
                try { var len = DiskUsage.SizeOf(d); Directory.Delete(d, true); freed += len; } catch { }
        }
        return freed;
    }
}

/// <summary>Reads VPX's log (vpinball.log) to explain why a table closed or didn't start in VR.</summary>
public static class VpxLog
{
    /// <summary>The most recently written vpinball.log (VPX keeps it in %APPDATA%\VPinballX\&lt;version&gt;).</summary>
    public static string? Find(AppSettings s)
    {
        var candidates = new List<string>();
        try
        {
            if (Directory.Exists(AppFolders.VpxPrefs))
                candidates.AddRange(Directory.EnumerateFiles(AppFolders.VpxPrefs, "vpinball.log", SearchOption.AllDirectories));
            if (Directory.Exists(s.VpxDir))
                candidates.AddRange(Directory.EnumerateFiles(s.VpxDir, "vpinball.log", SearchOption.TopDirectoryOnly));
        }
        catch { }
        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    public sealed record Session(DateTime? Started, bool ClosedCleanly, bool UsedVr, string? Table, List<string> Problems)
    {
        /// <summary>Plain-English hints for problems we recognise.</summary>
        public List<string> Hints { get; } = [];
    }

    /// <summary>Summarises the last VPX run in the log: errors (deduplicated), whether VR started, and how it ended.</summary>
    public static Session? LastSession(string logPath)
    {
        List<string> lines;
        try
        {
            // VPX keeps the file open while running; share it.
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            lines = sr.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        }
        catch { return null; }

        var start = lines.FindLastIndex(l => l.Contains("Starting VPX"));
        if (start < 0) return null;
        var session = lines.Skip(start).ToList();
        DateTime? started = DateTime.TryParse(session[0].Length >= 23 ? session[0][..23] : "", out var t) ? t : null;

        var problems = new List<string>();
        var seen = new HashSet<string>();
        foreach (var l in session)
        {
            var isError = l.Contains(" ERROR ") || l.Contains(" FATAL ");
            var isXrWarn = l.Contains(" WARN ") && (l.Contains("XR") || l.Contains("OpenXR", StringComparison.OrdinalIgnoreCase));
            if (!isError && !isXrWarn) continue;
            var msg = Message(l);
            if (seen.Add(Normalise(msg))) problems.Add(msg);
        }
        const string loadMarker = "] LoadGameFromFilename ";
        var table = session.Select(l => l.IndexOf(loadMarker, StringComparison.Ordinal) is var i and >= 0 ? l[(i + loadMarker.Length)..].Trim() : null)
            .FirstOrDefault(x => x != null);
        var result = new Session(started, session.Any(l => l.Contains("Closing VPX")),
            session.Any(l => l.Contains("VRDevice") || l.Contains("OPENXR")), table, problems);

        var all = string.Join("\n", session);
        if (all.Contains("FORM_FACTOR_UNAVAILABLE"))
            result.Hints.Add("The headset wasn't connected when VPX started, so it opened on the monitor. Start Quest Link (or Virtual Desktop) first.");
        if (all.Contains("Corrupted file: image"))
            result.Hints.Add("Some of the table's images are stored at a different size than declared. That's an issue in the table file; it usually plays fine.");
        if (problems.Any(p => p.Contains("rom", StringComparison.OrdinalIgnoreCase)
                              && (p.Contains("not found", StringComparison.OrdinalIgnoreCase) || p.Contains("could not", StringComparison.OrdinalIgnoreCase) || p.Contains("failed", StringComparison.OrdinalIgnoreCase))))
            result.Hints.Add("PinMAME couldn't load the ROM. Check the table has its ROM (My Tables → Get ROM / Add ROM).");
        if (!result.ClosedCleanly && !IsRunning())
            result.Hints.Add("VPX stopped without closing normally (it crashed). The errors above are the last things it logged.");
        return result;
    }

    static bool IsRunning() => System.Diagnostics.Process.GetProcessesByName("VPinballX_BGFX64").Length > 0
                               || System.Diagnostics.Process.GetProcessesByName("VPinballX64").Length > 0;

    /// <summary>"2026-10-18 13:14:32.977 ERROR [31080] [Texture::GetRawBitmap@1130] Corrupted…" → "Corrupted…"</summary>
    static string Message(string line)
    {
        var i = line.IndexOf("] [", StringComparison.Ordinal);
        if (i >= 0)
        {
            var j = line.IndexOf("] ", i + 3, StringComparison.Ordinal);
            if (j >= 0) return line[(j + 2)..].Trim();
        }
        return line.Length > 24 ? line[24..].Trim() : line;
    }

    // Collapse messages that only differ in names or numbers ("image 'x' width (2048)…").
    static string Normalise(string msg) =>
        System.Text.RegularExpressions.Regex.Replace(msg, @"'[^']*'|""[^""]*""|\d+", "#");
}

/// <summary>
/// Backs up what you'd hate to lose and can't download again: favourites and play stats, app and
/// VPX settings, per-table settings and high scores (PinMAME nvram/cfg), and the UEVR profiles.
/// Tables, ROMs and caches aren't included (they can be downloaded again).
/// </summary>
public static class Backup
{
    const string App = "app/", Vpx = "vpx/", VpxPrefs = "vpx-prefs/", Tables = "tables/", Uevr = "uevr/";
    static readonly string[] AppFiles = ["settings.json", "play-stats.json", "lobby-state.json"];

    public sealed record Result(int Files, List<string> Skipped);

    public static Result Create(string zipPath, AppSettings s)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var count = 0;
        void Add(string file, string entry)
        {
            if (!File.Exists(file)) return;
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var dst = zip.CreateEntry(entry.Replace('\\', '/'), CompressionLevel.Optimal).Open();
            src.CopyTo(dst);
            count++;
        }

        foreach (var f in AppFiles) Add(Path.Combine(AppSettings.DataDir, f), App + f);
        Add(VpxSetup.VrIniPath(s), Vpx + Path.GetFileName(VpxSetup.VrIniPath(s)));
        if (Directory.Exists(AppFolders.VpxPrefs))
            foreach (var ini in Directory.EnumerateFiles(AppFolders.VpxPrefs, "*.ini", SearchOption.AllDirectories))
                Add(ini, VpxPrefs + Path.GetRelativePath(AppFolders.VpxPrefs, ini));

        if (Directory.Exists(s.TablesDir))
        {
            foreach (var folder in Directory.EnumerateDirectories(s.TablesDir))
            {
                var name = Path.GetFileName(folder);
                foreach (var f in Directory.EnumerateFiles(folder))
                    if (Path.GetExtension(f).ToLowerInvariant() is ".ini" or ".json")
                        Add(f, $"{Tables}{name}/{Path.GetFileName(f)}");
                foreach (var sub in new[] { Path.Combine("pinmame", "nvram"), Path.Combine("pinmame", "cfg") })
                {
                    var dir = Path.Combine(folder, sub);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        Add(f, $"{Tables}{name}/{Path.GetRelativePath(folder, f)}");
                }
            }
        }

        foreach (var g in new[] { ZenGames.PinballFx, ZenGames.PinballM })
        {
            var dir = PfxSetup.ProfileDirFor(g);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.txt"))
                Add(f, $"{Uevr}{Path.GetFileName(dir)}/{Path.GetFileName(f)}");
        }
        return new Result(count, []);
    }

    /// <summary>Puts files back where they came from. Tables that aren't installed any more are skipped.</summary>
    public static Result Restore(string zipPath, AppSettings s)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var count = 0;
        var skipped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var uevrRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnrealVRMod");
        foreach (var e in zip.Entries.Where(e => e.Name != ""))
        {
            var name = e.FullName;
            string? dest =
                name.StartsWith(App) ? Under(AppSettings.DataDir, name[App.Length..]) :
                name.StartsWith(Vpx) ? Under(s.VpxDir, name[Vpx.Length..]) :
                name.StartsWith(VpxPrefs) ? Under(AppFolders.VpxPrefs, name[VpxPrefs.Length..]) :
                name.StartsWith(Uevr) ? Under(uevrRoot, name[Uevr.Length..]) :
                name.StartsWith(Tables) ? TableDest(name[Tables.Length..]) :
                null;
            if (dest == null) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, overwrite: true);
            count++;
        }
        return new Result(count, [.. skipped]);

        string? TableDest(string rel)
        {
            var folder = rel.Split('/')[0];
            if (!Directory.Exists(Path.Combine(s.TablesDir, folder))) { skipped.Add(folder); return null; }
            return Under(s.TablesDir, rel);
        }
    }

    /// <summary>Joins and refuses paths that would land outside <paramref name="root"/> (a tampered zip).</summary>
    static string? Under(string root, string rel)
    {
        var full = Path.GetFullPath(Path.Combine(root, rel));
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    public static string DefaultFileName => $"VR Table Manager backup {DateTime.Now:yyyy-MM-dd HHmm}.zip";
}
