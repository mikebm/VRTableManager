using SharpCompress.Archives;
using VrTableManager.Models;

namespace VrTableManager.Services;

/// <summary>
/// A table's attract/theme music for the VR lobby, taken from the community files PinUP Popper and
/// PinballX use: the catalog's sound files (plain .mp3 links) or its media packs, which keep the table
/// audio as <c>Audio\&lt;table&gt;.mp3</c> (PinUP) or <c>Table Audio\&lt;table&gt;.mp3</c> (PinballX/HyperPin)
/// among large videos. Only that one audio file is kept, as <c>&lt;table folder&gt;\music\attract.&lt;ext&gt;</c>,
/// which the lobby prefers (see <see cref="Lobby.LobbyArt.AttractSound"/>). The rest of the pack is thrown away.
/// Packs with only videos (most HyperPin packs) can give their best video's sound through a <see cref="Converter"/>.
/// </summary>
public static class MediaPackAudio
{
    public const string FileName = "attract";
    static readonly string[] AudioExts = [".mp3", ".ogg", ".wav"];
    static readonly string[] ArchiveExts = [".zip", ".rar", ".7z"];
    /// <summary>The lobby ignores sound files this big (keep in step with LobbyArt).</summary>
    public const long MaxBytes = 20 * 1024 * 1024;

    public sealed record Source(string Url, bool IsDirect, string Label);

    /// <summary>
    /// Turns other formats (AAC in .mp4 videos and .m4a files) into something the lobby plays.
    /// <c>Convert(input, workDir)</c> writes the result into workDir and returns it, or null when the file
    /// has no usable sound. Supplied by the manager (it needs Windows' Media Foundation).
    /// </summary>
    public sealed record Converter(IReadOnlyCollection<string> Exts, Func<string, string, string?> Convert);

    /// <summary>Most video/AAC entries to try converting from one pack before giving up.</summary>
    const int MaxConversions = 3;
    static readonly string[] AudioOnlyExts = [".m4a", ".aac", ".wma"];

    /// <summary>
    /// Where this game's audio can come from, best first: sound files that are a direct audio link
    /// named after the game (some are mislabelled, e.g. another machine's song), then media packs.
    /// </summary>
    public static List<Source> SourcesFor(VpsGame game)
    {
        var result = new List<Source>();
        foreach (var f in game.SoundFiles ?? [])
            foreach (var u in f.Urls ?? [])
                if (!u.Broken && DirectAudioUrl(u.Url) is { } direct && NamedAfter(direct, game))
                    result.Add(new Source(direct, true, "sound file"));
        foreach (var f in game.MediaPackFiles ?? [])
            if (f.Urls?.FirstOrDefault(u => !u.Broken && Uri.IsWellFormedUriString(u.Url, UriKind.Absolute)) is { } u)
                result.Add(new Source(u.Url, false, "media pack" + (f.Authors is { Count: > 0 } a ? " by " + string.Join(", ", a) : "")));
        return result;
    }

    /// <summary>A link straight to an audio file (Dropbox share links are turned into downloads), else null.</summary>
    public static string? DirectAudioUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !AudioExts.Contains(Path.GetExtension(u.AbsolutePath).ToLowerInvariant()))
            return null;
        if (u.Host.EndsWith("dropbox.com", StringComparison.OrdinalIgnoreCase))
            return u.GetLeftPart(UriPartial.Path) + "?dl=1";
        return u.ToString();
    }

    /// <summary>
    /// "Avatar (Stern 2010).mp3" for "James Cameron's Avatar": either title holds the other, and the
    /// year in brackets (when the file has one) is the game's.
    /// </summary>
    static bool NamedAfter(string url, VpsGame game)
    {
        var file = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath));
        var title = Normalize(file.Split('(')[0]);
        var name = Normalize(game.Name);
        if (title.Length < 3 || name.Length < 3 || !(title.Contains(name) || name.Contains(title))) return false;
        var year = System.Text.RegularExpressions.Regex.Match(file, @"\(.*?\b((?:19|20)\d\d)\b.*?\)");
        return !year.Success || game.Year is not > 0 || year.Groups[1].Value == game.Year.ToString();
    }

    static string Normalize(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The audio already saved for the lobby in this table folder, if any.</summary>
    public static string? Existing(string tableFolder) =>
        AudioExts.Select(e => Path.Combine(tableFolder, "music", FileName + e)).FirstOrDefault(File.Exists);

    public static void Remove(string tableFolder)
    {
        foreach (var e in AudioExts)
        {
            var p = Path.Combine(tableFolder, "music", FileName + e);
            if (File.Exists(p)) File.Delete(p);
        }
    }

    /// <summary>
    /// How good an archive entry is as the table's attract audio, or null when it can't be used
    /// (not .mp3/.ogg/.wav, empty, or too big for the lobby). PinUP's Audio\ and PinballX's Table Audio\
    /// win; launch sounds (AudioLaunch\, played when a table starts) and short call-outs come last.
    /// </summary>
    public static int? Score(string entryPath, long size, string gameName)
    {
        if (size <= 0 || size >= MaxBytes || !AudioExts.Contains(Path.GetExtension(entryPath).ToLowerInvariant())) return null;
        var parts = entryPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.ToLowerInvariant()).ToArray();
        var folders = parts[..^1].Select(Normalize).ToArray();
        var file = Normalize(Path.GetFileNameWithoutExtension(parts[^1]));
        var score = 0;
        if (folders.Any(f => f.Contains("launch") || f.Contains("exit") || f.Contains("callout"))) score -= 40;
        else if (folders.Any(f => f is "audio" or "tableaudio" or "tablesound" or "tablesounds")) score += 50;
        else if (folders.Any(f => f.Contains("audio") || f.Contains("music") || f.Contains("sound"))) score += 10;
        if (file.Contains("launch") || file.Contains("callout")) score -= 20;
        if (new[] { "attract", "theme", "intro", "music" }.Any(file.Contains)) score += 20;
        if (Normalize(gameName) is { Length: > 0 } name && file.Contains(name)) score += 15;
        return score;
    }

    /// <summary>The best entry for the attract audio among (path, size) pairs, or null when none is usable.</summary>
    public static string? Pick(IEnumerable<(string path, long size)> entries, string gameName) =>
        entries
            .Select(e => (e.path, e.size, score: Score(e.path, e.size, gameName)))
            .Where(e => e.score != null)
            .OrderByDescending(e => e.score)
            .ThenByDescending(e => e.size) // a whole theme over a jingle
            .ThenBy(e => e.path, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.path)
            .FirstOrDefault();

    /// <summary>
    /// How good a video or AAC file is as a source of the table's sound, or null when the converter can't
    /// read it. Audio-only files first, then attract videos, then table/playfield videos; promos and
    /// launch/loading clips last. Tutorials are never used (narration).
    /// </summary>
    public static int? ConvertScore(string entryPath, IReadOnlyCollection<string> convertible, string gameName)
    {
        var ext = Path.GetExtension(entryPath).ToLowerInvariant();
        if (!convertible.Contains(ext)) return null;
        var text = Normalize(entryPath);
        if (text.Contains("tutorial") || text.Contains("instruction")) return null; // narration, never music
        var score = AudioOnlyExts.Contains(ext) ? 100 + (Score(Path.ChangeExtension(entryPath, ".mp3"), 1, gameName) ?? 0) : 0;
        if (text.Contains("attract")) score += 30;
        if (text.Contains("tablevideo") || text.Contains("playfield")) score += 10;
        if (new[] { "promo", "launch", "loading", "exit", "topper", "dmd", "menu" }.Any(text.Contains)) score -= 20;
        if (Normalize(gameName) is { Length: > 0 } name && Normalize(Path.GetFileNameWithoutExtension(entryPath)).Contains(name)) score += 5;
        return score;
    }

    /// <summary>The video/AAC entries worth converting, best first (at most <see cref="MaxConversions"/>).</summary>
    public static List<string> PickConvertible(IEnumerable<string> entries, IReadOnlyCollection<string> convertible, string gameName) =>
        entries
            .Select(e => (e, score: ConvertScore(e, convertible, gameName)))
            .Where(e => e.score != null)
            .OrderByDescending(e => e.score)
            .ThenBy(e => e.e, StringComparer.OrdinalIgnoreCase)
            .Take(MaxConversions)
            .Select(e => e.e)
            .ToList();

    /// <summary>
    /// Saves a downloaded file as the table's attract audio: an audio file is used as is; from an archive
    /// (a media pack) only the best audio entry is extracted, looking one level into archives inside it
    /// when the pack itself holds none. With a <paramref name="converter"/>, packs without an audio file
    /// (and downloads that are a single video) give the sound of their best video, converted.
    /// Returns the saved path, or null when there's no usable audio.
    /// </summary>
    public static string? Save(string downloaded, string tableFolder, string gameName, Converter? converter = null)
    {
        var ext = Path.GetExtension(downloaded).ToLowerInvariant();
        if (AudioExts.Contains(ext))
        {
            if (Score(downloaded, new FileInfo(downloaded).Length, gameName) == null) return null;
            using var src = File.OpenRead(downloaded);
            return Write(src, ext, tableFolder);
        }
        if (ArchiveExts.Contains(ext)) return FromArchive(downloaded, tableFolder, gameName, converter, nested: true);
        // A single video (e.g. an .mp4 attract recording): only its sound track, if it can be converted.
        return converter != null && converter.Exts.Contains(ext) ? Converted(downloaded, tableFolder, converter) : null;
    }

    static string? Converted(string file, string tableFolder, Converter converter)
    {
        var workDir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        if (converter.Convert(file, workDir) is not { } output) return null;
        try
        {
            if (new FileInfo(output).Length is <= 0 or >= MaxBytes) return null;
            using var src = File.OpenRead(output);
            return Write(src, Path.GetExtension(output).ToLowerInvariant(), tableFolder);
        }
        finally { try { File.Delete(output); } catch { } }
    }

    static string? FromArchive(string archivePath, string tableFolder, string gameName, Converter? converter, bool nested)
    {
        using (var archive = ArchiveFactory.OpenArchive(archivePath))
        {
            var files = archive.Entries.Where(e => !e.IsDirectory && !string.IsNullOrEmpty(e.Key)).ToList();
            if (Pick(files.Select(e => (e.Key!, e.Size)), gameName) is { } best)
            {
                using var src = files.First(e => e.Key == best).OpenEntryStream();
                return Write(src, Path.GetExtension(best).ToLowerInvariant(), tableFolder);
            }
            if (converter != null)
            {
                foreach (var key in PickConvertible(files.Select(e => e.Key!), converter.Exts, gameName))
                {
                    var tmp = TempBeside(archivePath, Path.GetExtension(key));
                    try
                    {
                        using (var src = files.First(e => e.Key == key).OpenEntryStream())
                        using (var dst = File.Create(tmp))
                            src.CopyTo(dst);
                        if (Converted(tmp, tableFolder, converter) is { } saved) return saved;
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                }
            }
            if (!nested) return null;
            // Some packs wrap each front end's media in its own archive.
            foreach (var inner in files.Where(e => ArchiveExts.Contains(Path.GetExtension(e.Key!).ToLowerInvariant())))
            {
                var tmp = TempBeside(archivePath, Path.GetExtension(inner.Key!));
                try
                {
                    using (var src = inner.OpenEntryStream())
                    using (var dst = File.Create(tmp))
                        src.CopyTo(dst);
                    if (FromArchive(tmp, tableFolder, gameName, converter, nested: false) is { } saved) return saved;
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
        }
        return null;
    }

    /// <summary>A scratch file next to the download (the job's folder, deleted with it).</summary>
    static string TempBeside(string file, string ext) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, "inner-" + Guid.NewGuid().ToString("N")[..8] + ext);

    static string Write(Stream src, string ext, string tableFolder)
    {
        var music = Directory.CreateDirectory(Path.Combine(tableFolder, "music")).FullName;
        var dest = Path.Combine(music, FileName + ext);
        var tmp = dest + ".tmp";
        using (var dst = File.Create(tmp)) src.CopyTo(dst);
        Remove(tableFolder); // an older attract.* in another format would otherwise win or linger
        File.Move(tmp, dest, overwrite: true);
        return dest;
    }
}
