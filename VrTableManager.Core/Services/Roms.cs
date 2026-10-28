using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace VrTableManager.Services;

/// <summary>
/// Works out which PinMAME ROM a VPX table loads. VPS often lists several ROM versions for a
/// machine, but each table script names exactly one: <c>Const cGameName = "mm_109c"</c>.
/// Unlocked .vpx files keep the script as plain text, so a byte scan finds it without parsing
/// the file format.
/// </summary>
public static partial class RomDetector
{
    public static string? FromVpx(string vpxPath) => Inspect(vpxPath).GameName;

    /// <summary>
    /// Reads the table script for the ROM name (cGameName) and whether it drives PinMAME at all
    /// (LoadVPM / VPinMAME.Controller). Original tables often set cGameName for other things
    /// (high scores, backglass) without using a ROM; those don't need one.
    /// </summary>
    public static VpxRomInfo Inspect(string vpxPath)
    {
        if (!File.Exists(vpxPath)) return new(null, false);
        const int Chunk = 4 << 20, Overlap = 256;
        var buffer = new byte[Chunk + Overlap];
        var carry = 0;
        string? gameName = null;
        var pinMame = false;
        var aliasTargets = new List<string>();
        using var fs = File.OpenRead(vpxPath);
        int read, chunksAfter = 0;
        // Alias lines sit near cGameName (Mass Effect's is six lines on), so read one more chunk after
        // both answers are in rather than the whole file (tables run to hundreds of MB).
        while (chunksAfter < 2 && (read = fs.Read(buffer, carry, Chunk)) > 0)
        {
            if (gameName != null && pinMame) chunksAfter++;
            var total = carry + read;
            var text = Encoding.Latin1.GetString(buffer, 0, total);
            gameName ??= FromScript(text);
            pinMame = pinMame || PinMameRegex().IsMatch(text);
            foreach (var a in AliasTargetsIn(text))
                if (!aliasTargets.Contains(a, StringComparer.OrdinalIgnoreCase)) aliasTargets.Add(a);
            // Keep the tail so a match split across chunks is still found.
            var keep = Math.Min(Overlap, total);
            Buffer.BlockCopy(buffer, total - keep, buffer, 0, keep);
            carry = keep;
        }
        return new(gameName, pinMame) { AliasTargets = aliasTargets };
    }

    /// <summary>
    /// The cGameName the script actually uses. Tables often list alternatives commented out
    /// (<c>' Const cGameName = "afm_113"</c>), so only a line that isn't a comment counts.
    /// </summary>
    public static string? FromScript(string script)
    {
        foreach (var line in CodeLinesWith(script, "cGameName"))
            if (GameNameRegex().Match(line) is { Success: true } m) return m.Groups[1].Value;
        return null;
    }

    /// <summary>ROM names on (uncommented) script lines that mention an alias, e.g. <c>EnsureVPMAlias cGameName, "afm_113b"</c>.</summary>
    public static List<string> AliasTargetsIn(string script) =>
        CodeLinesWith(script, "alias").Select(l => AliasRegex().Match(l)).Where(m => m.Success)
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static bool UsesPinMame(string script) => PinMameRegex().IsMatch(script);

    /// <summary>
    /// The code part of each line containing <paramref name="word"/> (scripts end lines with \r\n or a
    /// lone \r): up to the word, nothing after a ' comment mark. A plain search finds the few lines
    /// first, so this stays fast over the hundreds of MB of a table file.
    /// </summary>
    static IEnumerable<string> CodeLinesWith(string text, string word)
    {
        for (var i = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(word, i + word.Length, StringComparison.OrdinalIgnoreCase))
        {
            var start = text.LastIndexOfAny(['\r', '\n'], i) + 1;
            var end = text.IndexOfAny(['\r', '\n'], i);
            if (end < 0) end = text.Length;
            // A "line" can run through binary data; only look nearby.
            start = Math.Max(start, i - 300);
            end = Math.Min(end, i + 300);
            var line = text[start..end];
            var comment = line.IndexOf('\'');
            if (comment >= 0 && comment < i - start) continue; // the word is inside a comment
            yield return comment >= 0 ? line[..comment] : line;
        }
    }

    [GeneratedRegex("\\bcGameName[ \\t]*=[ \\t]*\"([A-Za-z0-9_]{2,32})\"", RegexOptions.IgnoreCase)]
    private static partial Regex GameNameRegex();

    [GeneratedRegex("alias[^\"]*\"([a-z0-9_]{2,32})\"", RegexOptions.IgnoreCase)]
    private static partial Regex AliasRegex();

    [GeneratedRegex("LoadVPM|VPinMAME\\.Controller", RegexOptions.IgnoreCase)]
    private static partial Regex PinMameRegex();
}

/// <param name="GameName">cGameName from the script, if found.</param>
/// <param name="UsesPinMame">The script loads PinMAME, so it needs a ROM.</param>
public sealed record VpxRomInfo(string? GameName, bool UsesPinMame)
{
    /// <summary>
    /// ROM names the script ties to an alias. When cGameName isn't a real PinMAME game (Mass Effect
    /// runs "afm_113b_me"), one of these is what it stands for; PinMAME learns that from alias.txt.
    /// </summary>
    public IReadOnlyList<string> AliasTargets { get; init; } = [];

    /// <summary>The script was read and it doesn't use PinMAME: no ROM needed, whatever cGameName says.</summary>
    public bool NoRomNeeded => GameName != null && !UsesPinMame;
}

public static class RomSources
{
    /// <summary>
    /// A ROM entry that can actually be downloaded as a PinMAME ROM set. Stern's own site
    /// (sternpinball.com game_code) and *.bin entries are machine firmware updates, not ROMs.
    /// </summary>
    public static bool IsUsable(string url, string? romName)
    {
        if (romName != null && romName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Host.EndsWith("sternpinball.com", StringComparison.OrdinalIgnoreCase)) return false;
        return !u.AbsolutePath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens a VPUniverse file search for a ROM the catalog can't download.</summary>
    public static string VpuSearchUrl(string romName) =>
        "https://vpuniverse.com/search/?q=" + Uri.EscapeDataString(romName) + "&type=downloads_file";
}

/// <summary>Puts a downloaded ROM into a table's pinmame/roms folder.</summary>
public static class RomInstaller
{
    /// <summary>
    /// Handles the shapes ROM downloads come in: the ROM zip itself (any file name; it's saved as
    /// <paramref name="romName"/>.zip), or an archive with one or more ROM zips inside.
    /// Returns the installed file paths.
    /// </summary>
    public static List<string> Install(string downloaded, InstalledTable table, string romName)
    {
        var romsDir = Path.Combine(table.Folder, "pinmame", "roms");
        Directory.CreateDirectory(romsDir);
        var ext = Path.GetExtension(downloaded).ToLowerInvariant();

        if (ext == ".zip")
        {
            using var z = ZipFile.OpenRead(downloaded);
            var nested = z.Entries.Where(e => e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
            if (nested.Count == 0)
            {
                // The download is the ROM.
                var dest = Path.Combine(romsDir, romName + ".zip");
                File.Copy(downloaded, dest, overwrite: true);
                return [dest];
            }
            return PlaceNested(nested.Select(e => (e.Name, (Action<string>)(d => e.ExtractToFile(d, true)))).ToList(), romsDir, romName);
        }

        if (ext is ".7z" or ".rar")
        {
            var tmp = Path.Combine(Path.GetTempPath(), "vrtm-rom-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                TableInstaller.Extract(downloaded, tmp);
                var zips = Directory.EnumerateFiles(tmp, "*.zip", SearchOption.AllDirectories).ToList();
                if (zips.Count == 0) throw new InvalidDataException("No ROM zip inside the download.");
                return PlaceNested(zips.Select(f => (Path.GetFileName(f), (Action<string>)(d => File.Copy(f, d, true)))).ToList(), romsDir, romName);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        throw new InvalidDataException($"Unexpected ROM download type: {Path.GetFileName(downloaded)}");
    }

    /// <summary>
    /// Several downloads from one ROM page (every ROM version of a machine). All of them go into
    /// pinmame/roms under their own names: PinMAME "clone" versions only hold the files that differ
    /// and need their parent set beside them, so keeping the whole family is what makes the table's
    /// ROM load. A single download goes through <see cref="Install"/> (renamed to <paramref name="romName"/> if needed).
    /// </summary>
    public static List<string> InstallAll(IReadOnlyList<string> downloads, InstalledTable table, string romName)
    {
        if (downloads.Count == 1) return Install(downloads[0], table, romName);

        var romsDir = Path.Combine(table.Folder, "pinmame", "roms");
        Directory.CreateDirectory(romsDir);
        var placed = new List<string>();
        foreach (var file in downloads)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".zip")
            {
                using var z = ZipFile.OpenRead(file);
                var nested = z.Entries.Where(e => e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
                if (nested.Count == 0)
                {
                    var dest = Path.Combine(romsDir, Path.GetFileName(file));
                    File.Copy(file, dest, overwrite: true);
                    placed.Add(dest);
                }
                else
                {
                    foreach (var e in nested)
                    {
                        var dest = Path.Combine(romsDir, e.Name);
                        e.ExtractToFile(dest, true);
                        placed.Add(dest);
                    }
                }
            }
            else if (ext is ".7z" or ".rar")
            {
                var tmp = Path.Combine(Path.GetTempPath(), "vrtm-rom-" + Guid.NewGuid().ToString("N")[..8]);
                try
                {
                    TableInstaller.Extract(file, tmp);
                    foreach (var inner in Directory.EnumerateFiles(tmp, "*.zip", SearchOption.AllDirectories))
                    {
                        var dest = Path.Combine(romsDir, Path.GetFileName(inner));
                        File.Copy(inner, dest, true);
                        placed.Add(dest);
                    }
                }
                finally
                {
                    try { Directory.Delete(tmp, true); } catch { }
                }
            }
        }
        if (placed.Count == 0) throw new InvalidDataException("None of the downloads contained a ROM zip.");
        return placed;
    }

    static List<string> PlaceNested(List<(string name, Action<string> save)> zips, string romsDir, string romName)
    {
        var match = zips.Where(z => Path.GetFileNameWithoutExtension(z.name).Equals(romName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count == 0 && zips.Count == 1) match = [(romName + ".zip", zips[0].save)];
        if (match.Count == 0)
            throw new InvalidDataException($"The download has {zips.Count} ROM zips but none called {romName}.zip.");
        var placed = new List<string>();
        foreach (var (name, save) in match)
        {
            var dest = Path.Combine(romsDir, Path.GetFileName(name));
            save(dest);
            placed.Add(dest);
        }
        return placed;
    }
}
