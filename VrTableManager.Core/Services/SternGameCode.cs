using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VrTableManager.Services;

/// <summary>One downloadable game code file on Stern's site.</summary>
public sealed record SternCode(string Title, string Url);

/// <summary>
/// Stern publishes the game code of its older machines for free on wp.sternpinball.com, and for
/// Whitestar, SAM and Spike 1 games that code is the PinMAME ROM set: the chip images in a zip
/// (Lord of the Rings) or one .bin (Avatar, AC/DC). No sign-in is needed. The app looks there first
/// for Stern tables, picks the file whose version and edition match the ROM PinMAME wants, repacks
/// it as pinmame\roms\&lt;rom&gt;.zip and keeps it only if PinMAME says the set is complete.
/// </summary>
public static partial class SternGameCode
{
    public static string SearchUrl(string terms) =>
        "https://wp.sternpinball.com/?post_type=game_code&s=" + Uri.EscapeDataString(terms);

    public static bool IsSternUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.EndsWith("sternpinball.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>The site's search results, read from the JSON the page embeds (read-me files left out).</summary>
    public static async Task<List<SternCode>> SearchAsync(string searchUrl, CancellationToken ct) =>
        ParseResults(await Http.Client.GetStringAsync(searchUrl, ct));

    public static List<SternCode> ParseResults(string html)
    {
        var result = new List<SternCode>();
        if (PostsRegex().Match(html) is not { Success: true } m) return result;
        try
        {
            using var doc = JsonDocument.Parse(m.Groups[1].Value);
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                string Str(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var url = Str("download_type") == "file" ? Str("download_file") : Str("download_link");
                var title = Str("title");
                if (url == "" || title.Contains("read me", StringComparison.OrdinalIgnoreCase)) continue;
                var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;
                if (Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".pdf" or ".doc" or ".docx") continue;
                result.Add(new SternCode(title, url));
            }
        }
        catch (JsonException) { }
        return result;
    }

    /// <summary>
    /// Files worth trying for a ROM, best first: same version as PinMAME's description of it
    /// ("Avatar (V2.0)" ↔ "Avatar Pro 2.00 Game Code"), then the same edition (PinMAME's "Limited
    /// Edition" sets end in h and match Stern's LE/Premium code; the others match Pro).
    /// </summary>
    public static List<SternCode> Rank(IEnumerable<SternCode> codes, string? description)
    {
        var want = description != null ? Version(description) : null;
        var le = description?.Contains("Limited Edition", StringComparison.OrdinalIgnoreCase) == true;
        int Score(SternCode c)
        {
            var score = 0;
            if (want != null && Version(c.Title) == want) score += 10;
            var isLe = LeRegex().IsMatch(c.Title);
            if (isLe == le) score += 3;
            return score;
        }
        var list = codes.ToList();
        // With a version to go by, only files of that version; otherwise (one result, e.g. Lord of the Rings) all.
        var ranked = list.OrderByDescending(Score).ToList();
        return want != null && ranked.Any(c => Version(c.Title) == want) ? ranked.Where(c => Version(c.Title) == want).ToList()
            : list.Count == 1 ? ranked : [];
    }

    /// <summary>"V1.70.0" / "1.70" / "10.00" → "1.7" / "1.7" / "10": numbers compared without trailing zeros.</summary>
    static string? Version(string text)
    {
        if (VersionRegex().Match(text) is not { Success: true } m) return null;
        var parts = m.Value.TrimStart('V', 'v').Split('.').Select(p => p.TrimStart('0') is { Length: > 0 } t ? t : "0").ToList();
        // Drop trailing zero parts and zeros at the end of the minor part ("2.00" = "2.0" = "2").
        if (parts.Count > 1) parts[1] = parts[1].TrimEnd('0') is { Length: > 0 } mi ? mi : "0";
        while (parts.Count > 1 && parts[^1] == "0") parts.RemoveAt(parts.Count - 1);
        return string.Join(".", parts);
    }

    /// <summary>
    /// Downloads the best-matching file for <paramref name="rom"/> and installs it as the table's ROM.
    /// Returns the installed zip, or null when Stern has nothing that PinMAME accepts (the caller
    /// falls back to the catalog's other sources).
    /// </summary>
    public static async Task<string?> InstallAsync(InstalledTable table, string rom, IReadOnlyList<string> searchUrls,
        AppSettings s, Action<string> status, IProgress<double> progress, CancellationToken ct)
    {
        var pinmameDir = Path.Combine(table.Folder, "pinmame");
        var info = PinmameInfo.Get(s, pinmameDir, rom);
        var codes = new List<SternCode>();
        foreach (var url in searchUrls.Distinct())
        {
            status("Looking on Stern's site…");
            try { codes.AddRange(await SearchAsync(url, ct)); }
            catch (HttpRequestException) { }
        }
        var dest = Path.Combine(pinmameDir, "roms", rom + ".zip");
        foreach (var code in Rank(codes.DistinctBy(c => c.Url), info?.Description).Take(2))
        {
            var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vrtm-stern-" + Guid.NewGuid().ToString("N")[..8])).FullName;
            try
            {
                status($"Downloading {code.Title} from Stern…");
                // Its own folder: Stern's file can have the ROM's name already (lotr.zip).
                var file = Path.Combine(Directory.CreateDirectory(Path.Combine(work, "download")).FullName, Path.GetFileName(new Uri(code.Url).AbsolutePath));
                await Http.DownloadFileAsync(code.Url, file, progress, ct);
                status("Installing ROM…");
                var packed = Path.Combine(work, rom + ".zip");
                await Task.Run(() => Repack(file, packed), ct);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var backup = File.Exists(dest) ? dest + ".vrtm-old" : null;
                if (backup != null) File.Move(dest, backup, overwrite: true);
                File.Move(packed, dest, overwrite: true);
                // Keep it only if PinMAME agrees it's the set (without PinMAME to ask, trust the version match).
                if (info == null || PinmameInfo.Get(s, pinmameDir, rom) is { Found: true })
                {
                    if (backup != null) File.Delete(backup);
                    return dest;
                }
                File.Delete(dest);
                if (backup != null) File.Move(backup, dest);
            }
            catch (HttpRequestException) { }
            catch (InvalidDataException) { } // not a zip after all
            finally { try { Directory.Delete(work, true); } catch { } }
        }
        return null;
    }

    /// <summary>Stern's zip (chip images plus a read-me) or single .bin → a ROM zip holding just the code files.</summary>
    static void Repack(string downloaded, string romZip)
    {
        using var output = ZipFile.Open(romZip, ZipArchiveMode.Create);
        if (Path.GetExtension(downloaded).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var input = ZipFile.OpenRead(downloaded);
            foreach (var e in input.Entries.Where(e => e.Name != "" && Path.GetExtension(e.Name).ToLowerInvariant() is not (".txt" or ".pdf" or ".doc" or ".docx" or ".rtf")))
            {
                using var from = e.Open();
                using var to = output.CreateEntry(e.Name, CompressionLevel.Optimal).Open();
                from.CopyTo(to);
            }
        }
        else
        {
            output.CreateEntryFromFile(downloaded, Path.GetFileName(downloaded).ToLowerInvariant(), CompressionLevel.Optimal);
        }
    }

    [GeneratedRegex("\"posts\":(\\[.*?\\])\\}", RegexOptions.Singleline)]
    private static partial Regex PostsRegex();

    [GeneratedRegex("V?\\d+\\.\\d+(?:\\.\\d+)?")]
    private static partial Regex VersionRegex();

    [GeneratedRegex("\\b(LE|Limited|Premium)\\b", RegexOptions.IgnoreCase)]
    private static partial Regex LeRegex();
}
