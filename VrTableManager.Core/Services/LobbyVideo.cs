using System.Diagnostics;
using System.Text.Json;
using SharpCompress.Archives;

namespace VrTableManager.Services;

/// <summary>
/// A moving playfield for the VR lobby's selected cabinet: a short loop of the table's attract mode, seen from
/// above. Two sources, best first:
/// <list type="number">
/// <item>the catalog's media pack (the files PinUP Popper / PinballX use), whose playfield video is kept as
/// <c>medias\(Playfield) &lt;table&gt;.mp4</c> (VPX's file layout) and turned into frames;</item>
/// <item>a recording made on this PC: VPX's own <c>-CaptureAttract</c> runs the table's attract mode in a small
/// cabinet-view window and saves the frames (QOI images in <c>&lt;table folder&gt;\Capture</c>).</item>
/// </list>
/// Either way the lobby gets numbered JPEG frames plus <c>video.json</c> in <c>medias\lobby-video</c>, which it
/// streams onto the playfield (Godot can't play MP4, and frames need no codec). Turning pictures into JPEGs needs
/// an image library, so that part is the manager's (<see cref="IFrameSink"/>).
/// </summary>
public static class LobbyVideo
{
    public const string FolderName = "lobby-video";
    public const string InfoFile = "video.json";
    /// <summary>Frame rate of the lobby loop: smooth enough for flashing inserts, small on disk.</summary>
    public const int Fps = 15;
    /// <summary>At most this many frames (12 s); attract shows repeat, and VPX trims its recording to one loop.</summary>
    public const int MaxFrames = 180;
    /// <summary>Portrait frame size the lobby uses (the playfield seen from above, top of the table up).</summary>
    public const int Width = 720, Height = 1280;

    public sealed record Info(int Fps, int Frames, int Width, int Height, string Source);

    public static string Folder(string tableFolder) => Path.Combine(tableFolder, "medias", FolderName);
    public static string FramePath(string folder, int index) => Path.Combine(folder, $"{index:0000}.jpg");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The lobby video saved in this table folder, or null (none, or incomplete).</summary>
    public static Info? Existing(string tableFolder)
    {
        var folder = Folder(tableFolder);
        try
        {
            var path = Path.Combine(folder, InfoFile);
            if (!File.Exists(path)) return null;
            var info = JsonSerializer.Deserialize<Info>(File.ReadAllText(path));
            return info is { Frames: > 0, Fps: > 0 } && File.Exists(FramePath(folder, info.Frames - 1)) ? info : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void Remove(string tableFolder)
    {
        var folder = Folder(tableFolder);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    /// <summary>Writes one picture as a lobby frame: scaled to fit <see cref="Width"/>×<see cref="Height"/>, turned, JPEG.</summary>
    public interface IFrameSink
    {
        /// <param name="rgba">8-bit RGBA, rows from the top.</param>
        /// <param name="quarterTurns">Clockwise quarter turns to apply first (0–3).</param>
        void Write(int width, int height, byte[] rgba, int quarterTurns, string destJpg);
    }

    /// <summary>
    /// Collects frames in a staging folder and swaps it in on <see cref="Commit"/>, so a failed or cancelled run
    /// leaves the previous video as it was.
    /// </summary>
    public sealed class Builder : IDisposable
    {
        readonly string _tableFolder, _staging, _source;
        public int Count { get; private set; }

        public Builder(string tableFolder, string source)
        {
            _tableFolder = tableFolder;
            _source = source;
            _staging = Folder(tableFolder) + ".new";
            if (Directory.Exists(_staging)) Directory.Delete(_staging, true);
            Directory.CreateDirectory(_staging);
        }

        public string NextFramePath() => FramePath(_staging, Count++);

        public Info Commit()
        {
            if (Count == 0) throw new InvalidDataException("No frames.");
            var info = new Info(Fps, Count, Width, Height, _source);
            File.WriteAllText(Path.Combine(_staging, InfoFile), JsonSerializer.Serialize(info, Json));
            Remove(_tableFolder);
            Directory.Move(_staging, Folder(_tableFolder));
            return info;
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_staging)) Directory.Delete(_staging, true); } catch { }
        }
    }

    // ---- From a media pack ---------------------------------------------------------------

    static readonly string[] VideoExts = [".mp4", ".m4v", ".f4v", ".mov", ".wmv", ".avi", ".mkv"];
    static readonly string[] ArchiveExts = [".zip", ".rar", ".7z"];

    /// <summary>
    /// How good an archive entry is as the playfield video, or null when it isn't one. PinUP Popper keeps it in
    /// <c>PlayField\</c>, PinballX/HyperPin in <c>Table Videos\</c> (the cabinet view) or <c>Videos\Table\</c>;
    /// VPX's layout names it <c>(Playfield) …</c>. Backglass, DMD, topper, loading and help videos are never it.
    /// </summary>
    public static int? PlayfieldScore(string entryPath, long size, string gameName)
    {
        if (size <= 0 || !VideoExts.Contains(Path.GetExtension(entryPath).ToLowerInvariant())) return null;
        var parts = entryPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Normalize).ToArray();
        var folders = parts[..^1];
        var file = Normalize(Path.GetFileNameWithoutExtension(entryPath));
        string[] others = ["backglass", "backbox", "dmd", "fulldmd", "topper", "loading", "launch", "help", "info", "flyer", "wheel", "menu", "other", "instruction", "tutorial", "audio", "desktop"];
        if (folders.Any(f => others.Any(f.Contains)) || others.Any(o => file.StartsWith(o))) return null;
        var score = 0;
        if (folders.Any(f => f is "playfield" or "tablevideos" or "tablevideo" or "table")) score += 50;
        else if (folders.Any(f => f.Contains("playfield") || f.Contains("table"))) score += 25;
        else if (!file.Contains("playfield")) return null; // a video somewhere else is some other screen
        if (file.Contains("playfield")) score += 20;
        if (Normalize(gameName) is { Length: > 2 } name && file.Contains(name)) score += 10;
        if (Path.GetExtension(entryPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) score += 5; // plays as is; .f4v needs a copy
        return score;
    }

    public static string? PickPlayfield(IEnumerable<(string path, long size)> entries, string gameName) =>
        entries.Select(e => (e.path, e.size, score: PlayfieldScore(e.path, e.size, gameName)))
            .Where(e => e.score != null)
            .OrderByDescending(e => e.score).ThenByDescending(e => e.size).ThenBy(e => e.path, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.path).FirstOrDefault();

    static string Normalize(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// Saves a downloaded media pack's playfield video as <c>medias\(Playfield) &lt;folder name&gt;.&lt;ext&gt;</c>
    /// (looking one level into archives inside it), or uses a downloaded video as it is. Returns the saved
    /// path, or null when the pack has no playfield video.
    /// </summary>
    public static string? SavePlayfieldVideo(string downloaded, string tableFolder, string gameName)
    {
        var ext = Path.GetExtension(downloaded).ToLowerInvariant();
        if (VideoExts.Contains(ext))
        {
            using var src = File.OpenRead(downloaded);
            return WriteVideo(src, ext, tableFolder);
        }
        return ArchiveExts.Contains(ext) ? FromArchive(downloaded, tableFolder, gameName, nested: true) : null;
    }

    /// <summary>The playfield video saved in the table's medias folder, if any.</summary>
    public static string? ExistingPlayfieldVideo(string tableFolder)
    {
        var medias = Path.Combine(tableFolder, "medias");
        return Directory.Exists(medias)
            ? Directory.EnumerateFiles(medias, "(Playfield) *").FirstOrDefault(f => VideoExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            : null;
    }

    static string? FromArchive(string archivePath, string tableFolder, string gameName, bool nested)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var files = archive.Entries.Where(e => !e.IsDirectory && !string.IsNullOrEmpty(e.Key)).ToList();
        if (PickPlayfield(files.Select(e => (e.Key!, e.Size)), gameName) is { } best)
        {
            using var src = files.First(e => e.Key == best).OpenEntryStream();
            return WriteVideo(src, Path.GetExtension(best).ToLowerInvariant(), tableFolder);
        }
        if (!nested) return null;
        foreach (var inner in files.Where(e => ArchiveExts.Contains(Path.GetExtension(e.Key!).ToLowerInvariant())))
        {
            var tmp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(archivePath))!, "inner-" + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(inner.Key!));
            try
            {
                using (var src = inner.OpenEntryStream())
                using (var dst = File.Create(tmp))
                    src.CopyTo(dst);
                if (FromArchive(tmp, tableFolder, gameName, nested: false) is { } saved) return saved;
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
        return null;
    }

    static string WriteVideo(Stream src, string ext, string tableFolder)
    {
        var medias = Directory.CreateDirectory(Path.Combine(tableFolder, "medias")).FullName;
        var dest = Path.Combine(medias, $"(Playfield) {Path.GetFileName(Path.TrimEndingDirectorySeparator(tableFolder))}{ext}");
        var tmp = dest + ".tmp";
        using (var dst = File.Create(tmp)) src.CopyTo(dst);
        if (ExistingPlayfieldVideo(tableFolder) is { } old && !old.Equals(dest, StringComparison.OrdinalIgnoreCase)) File.Delete(old);
        File.Move(tmp, dest, overwrite: true);
        return dest;
    }

    // ---- Recorded with VPX ---------------------------------------------------------------

    /// <summary>Landscape window VPX records in; cabinet view turns the table a quarter so it fills it.</summary>
    public const int CaptureWidth = 1280, CaptureHeight = 720;

    /// <summary>
    /// The settings file for a recording: the VR one (same plugins, ROMs and controls) with VR off, cabinet view
    /// (the playfield from above) in a small window and sound muted.
    /// </summary>
    public static string WriteCaptureIni(AppSettings s)
    {
        VpxSetup.EnsureVrIni(s);
        var ini = Path.Combine(AppSettings.DataDir, "lobby-capture.ini");
        File.Copy(VpxSetup.VrIniPath(s), ini, overwrite: true);
        IniFile.Set(ini, "PlayerVR", "AskToTurnOn", "2"); // VR disabled
        IniFile.SetMany(ini, "Player",
        [
            ("BGSet", "1"), // cabinet view: the playfield from above
            ("PlayfieldFullScreen", "0"),
            ("PlayfieldWidth", CaptureWidth.ToString()),
            ("PlayfieldHeight", CaptureHeight.ToString()),
            ("SoundVolume", "0"),
            ("MusicVolume", "0"),
        ]);
        // The plugins stay as for VR play: a table script that creates the B2S or PuP object stops at a script
        // error (a dialog nobody answers) when they're off. Only the playfield window is captured.
        return ini;
    }

    /// <summary>VPX writes its recording here: <c>&lt;table folder&gt;\Capture\Playfield_00001.qoi</c>, ….</summary>
    public static string CaptureFolder(InstalledTable t) => Path.Combine(t.Folder, "Capture");

    public static List<string> CapturedFrames(InstalledTable t) =>
        Directory.Exists(CaptureFolder(t))
            ? Directory.EnumerateFiles(CaptureFolder(t), "Playfield_*.qoi").Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    public static void DeleteCapture(InstalledTable t)
    {
        foreach (var f in CapturedFrames(t)) File.Delete(f);
        try { Directory.Delete(CaptureFolder(t)); } catch (IOException) { } // other files there aren't ours
    }

    /// <summary>
    /// Runs VPX's attract-mode recording for a table and returns its frames (oldest first). VPX opens a small
    /// window, skips the start-up, records up to <see cref="MaxFrames"/> frames and trims them to one loop of
    /// the attract show, then closes by itself.
    /// </summary>
    public static async Task<List<string>> RecordAsync(AppSettings s, InstalledTable t, CancellationToken ct)
    {
        var exe = VpxSetup.FindExe(s) ?? throw new InvalidOperationException("Visual Pinball X isn't installed yet (Setup tab).");
        if (!t.IsVpx || !File.Exists(t.TablePath)) throw new InvalidOperationException("Only VPX tables can be recorded.");
        DeleteCapture(t);
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        foreach (var a in CaptureArgs(WriteCaptureIni(s), t.TablePath)) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("VPX didn't start.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3)); // start-up plus 12 s of frames; a stuck table shouldn't wait forever
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("VPX didn't finish recording within 3 minutes.");
        }
        return CapturedFrames(t);
    }

    public static List<string> CaptureArgs(string iniPath, string tablePath) =>
        ["-Ini", iniPath, "-CaptureAttract", MaxFrames.ToString(), Fps.ToString(), tablePath];
}
