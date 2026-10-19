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
    public static string? FromVpx(string vpxPath)
    {
        if (!File.Exists(vpxPath)) return null;
        const int Chunk = 4 << 20, Overlap = 256;
        var buffer = new byte[Chunk + Overlap];
        var carry = 0;
        using var fs = File.OpenRead(vpxPath);
        int read;
        while ((read = fs.Read(buffer, carry, Chunk)) > 0)
        {
            var total = carry + read;
            var text = Encoding.Latin1.GetString(buffer, 0, total);
            if (GameNameRegex().Match(text) is { Success: true } m) return m.Groups[1].Value;
            // Keep the tail so a match split across chunks is still found.
            var keep = Math.Min(Overlap, total);
            Buffer.BlockCopy(buffer, total - keep, buffer, 0, keep);
            carry = keep;
        }
        return null;
    }

    public static string? FromScript(string script) =>
        GameNameRegex().Match(script) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex("cGameName\\s*=\\s*\"([A-Za-z0-9_]{2,32})\"", RegexOptions.IgnoreCase)]
    private static partial Regex GameNameRegex();
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
