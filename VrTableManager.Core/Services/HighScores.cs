using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace VrTableManager.Services;

/// <summary>One line of a machine's high-score table, e.g. ("Grand Champion", "ABC", 1234567890).</summary>
public sealed record HighScore(string Label, string Initials, long Score);

/// <summary>
/// Reads PinMAME high scores (and mode champions) from a table's .nv file. Where each game keeps
/// them comes from the open Pinball Memory Maps project (github.com/tomlogic/pinball-memory-maps,
/// formerly pinmame-nvram-maps): "This program makes use of content from the Pinball Memory Maps
/// project." The maps are cached in the app's data folder and refreshed monthly.
/// </summary>
/// <remarks>
/// Decoding follows the project's README and its reference parser (nvram_parser.py): the .nv file
/// starts with the platform's first "nvram" region (CPU addresses in the map are relative to that
/// region's base address); multibyte values use the platform's byte order unless the descriptor
/// overrides it; 4-bit regions use one nibble per address; "ch" strings use the map's char_map if
/// it has one. Supported encodings here: bcd, int (plus bool/bits/enum as plain integers) and ch.
/// </remarks>
public static class HighScores
{
    // GitHub redirects the old repo name and HEAD follows the default branch.
    const string ArchiveUrl = "https://github.com/tomlogic/pinball-memory-maps/archive/HEAD.zip";
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly Lock IndexLock = new();
    static (DateTime Stamp, Dictionary<string, string> Map)? _index;
    static readonly Lazy<string?> VpxDir = new(() => { try { return AppSettings.Load().VpxDir; } catch { return null; } });

    public static string MapsDir => Path.Combine(AppSettings.DataDir, "nvram-maps");
    static string IndexPath => Path.Combine(MapsDir, "index.json");

    /// <summary>Downloads/refreshes the NVRAM maps cache if missing or older than 30 days. Never throws (returns false on failure).</summary>
    public static async Task<bool> EnsureMapsAsync(CancellationToken ct)
    {
        try
        {
            await Gate.WaitAsync(ct);
            try
            {
                if (File.Exists(IndexPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(IndexPath) < MaxAge) return true;
                var zip = MapsDir + ".download.zip";
                var staging = MapsDir + ".new";
                await Http.DownloadFileAsync(ArchiveUrl, zip, null, ct);
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                ExtractMaps(zip, staging);
                File.Delete(zip);
                if (!File.Exists(Path.Combine(staging, "index.json"))) return false;
                File.SetLastWriteTimeUtc(Path.Combine(staging, "index.json"), DateTime.UtcNow);
                lock (IndexLock)
                {
                    if (Directory.Exists(MapsDir)) Directory.Delete(MapsDir, true);
                    Directory.Move(staging, MapsDir);
                    _index = null;
                }
                return true;
            }
            finally { Gate.Release(); }
        }
        catch { return false; }
    }

    /// <summary>Keeps index.json, platforms/*.json and maps/**/*.map.json from the repo archive.</summary>
    static void ExtractMaps(string zipPath, string dest)
    {
        var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            var slash = e.FullName.IndexOf('/');
            if (slash < 0 || e.FullName.EndsWith('/')) continue;
            var rel = e.FullName[(slash + 1)..];
            var keep = rel == "index.json"
                       || (rel.StartsWith("platforms/") && rel.EndsWith(".json"))
                       || (rel.StartsWith("maps/") && rel.EndsWith(".map.json"));
            if (!keep || rel.Contains("..")) continue;
            var path = Path.GetFullPath(Path.Combine(dest, rel));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var src = e.Open();
            using var dst = File.Create(path);
            src.CopyTo(dst);
        }
    }

    /// <summary>Reads scores for an installed table. Returns [] when there's no ROM name, no .nv file, or no map. Never throws. Synchronous, uses only the local cache.</summary>
    public static IReadOnlyList<HighScore> Read(InstalledTable table)
    {
        try
        {
            if (!table.IsVpx) return [];
            var nv = FindNv(table);
            return nv == null ? [] : Read(nv.Value.Rom, nv.Value.Path);
        }
        catch { return []; }
    }

    /// <summary>Same, from a ROM name and nv file path (for tests).</summary>
    public static IReadOnlyList<HighScore> Read(string romName, string nvPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(romName) || !File.Exists(nvPath)) return [];
            if (MapPathFor(romName) is not { } mapPath) return [];
            var map = File.ReadAllText(mapPath);
            var platformName = (JsonNode.Parse(map)?["_metadata"]?["platform"] as JsonValue)?.ToString();
            var platformPath = platformName == null ? null : Path.Combine(MapsDir, "platforms", platformName + ".json");
            var platform = platformPath != null && File.Exists(platformPath) ? File.ReadAllText(platformPath) : null;
            return Decode(map, platform, File.ReadAllBytes(nvPath));
        }
        catch { return []; }
    }

    /// <summary>
    /// The .nv file for a table: its own pinmame/nvram folder (VPX 10.8.1's per-table layout) first,
    /// newest file if several ROMs have one; for tables without that folder, VPX's VPinMAME/nvram and the
    /// classic VPinMAME install's.
    /// </summary>
    static (string Rom, string Path)? FindNv(InstalledTable table)
    {
        var roms = table.Manifest.RomNames.ToList();
        var romDir = Path.Combine(table.Folder, "pinmame", "roms");
        if (Directory.Exists(romDir)) roms.AddRange(Directory.EnumerateFiles(romDir, "*.zip").Select(Path.GetFileNameWithoutExtension).OfType<string>());
        roms = roms.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (roms.Count == 0) return null;

        var own = roms.Select(r => (Rom: r, Path: Path.Combine(table.Folder, "pinmame", "nvram", r + ".nv")))
            .Where(x => File.Exists(x.Path))
            .OrderByDescending(x => File.GetLastWriteTimeUtc(x.Path))
            .FirstOrDefault();
        if (own.Path != null) return own;
        // A table with its own pinmame folder keeps its scores there; a shared folder's file would be
        // another install's scores (e.g. an old VPinMAME setup), not this table's.
        if (Directory.Exists(Path.Combine(table.Folder, "pinmame"))) return null;

        foreach (var dir in SharedNvramDirs())
            foreach (var r in roms)
                if (File.Exists(Path.Combine(dir, r + ".nv"))) return (r, Path.Combine(dir, r + ".nv"));
        return null;
    }

    static IEnumerable<string> SharedNvramDirs()
    {
        if (VpxDir.Value is { Length: > 0 } vpx) yield return Path.Combine(vpx, "VPinMAME", "nvram");
        string? classic = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Freeware\Visual PinMame\globals");
                classic = key?.GetValue("nvram_directory") as string;
            }
            catch { }
        }
        if (!string.IsNullOrWhiteSpace(classic)) yield return classic;
    }

    static string? MapPathFor(string rom)
    {
        if (!File.Exists(IndexPath)) return null;
        Dictionary<string, string> index;
        lock (IndexLock)
        {
            var stamp = File.GetLastWriteTimeUtc(IndexPath);
            if (_index is not { } cached || cached.Stamp != stamp)
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (JsonNode.Parse(File.ReadAllText(IndexPath)) is JsonObject o)
                    foreach (var (k, v) in o)
                        if (!k.StartsWith('_') && v is JsonValue s && s.TryGetValue<string>(out var p)) map[k] = p;
                _index = cached = (stamp, map);
            }
            index = cached.Map;
        }
        if (!index.TryGetValue(rom, out var rel) || rel.Contains("..")) return null;
        var path = Path.Combine(MapsDir, rel.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : null;
    }

    // ---- Decoding ---------------------------------------------------------------------

    /// <summary>
    /// Decodes high_scores, then mode_champions, from a map (and its platform file) and the raw .nv
    /// bytes. Entries with neither initials nor a score are left out. For tests and tools; never throws.
    /// </summary>
    public static IReadOnlyList<HighScore> Decode(string mapJson, string? platformJson, byte[] nv)
    {
        var result = new List<HighScore>();
        try
        {
            if (JsonNode.Parse(mapJson) is not JsonObject map) return result;
            var meta = map["_metadata"] as JsonObject ?? new JsonObject();
            var platform = platformJson == null ? null : JsonNode.Parse(platformJson) as JsonObject;
            var mem = new Memory(platform, meta, nv);
            foreach (var section in new[] { "high_scores", "mode_champions" })
            {
                if (map[section] is not JsonArray entries) continue;
                foreach (var entry in Expand(entries))
                {
                    try { if (Entry(entry, mem) is { } hs) result.Add(hs); }
                    catch { /* one odd entry shouldn't hide the rest */ }
                }
            }
        }
        catch { }
        return result;
    }

    static HighScore? Entry(JsonObject e, Memory mem)
    {
        var label = (e["label"] as JsonValue)?.ToString() ?? "";
        if (label.StartsWith('_')) return null;
        var initials = e["initials"] is JsonObject i ? Text(i, mem) : null;
        var score = e["score"] is JsonObject s ? Number(s, mem) : null;
        if (string.IsNullOrEmpty(initials) && (score ?? 0) == 0) return null;
        return new HighScore(label, initials ?? "", score ?? 0);
    }

    /// <summary>Applies "repeat": copies of the template with {#} numbered from 1 and each nested start moved by step.</summary>
    static IEnumerable<JsonObject> Expand(JsonArray entries)
    {
        foreach (var node in entries)
        {
            if (node is not JsonObject e) continue;
            if (e["repeat"] is not JsonObject rep) { yield return e; continue; }
            var count = (int)Math.Min(ToLong(rep["count"]) ?? 0, 1000);
            var step = ToLong(rep["step"]) ?? 0;
            for (var n = 0; n < count; n++)
            {
                var copy = (JsonObject)e.DeepClone();
                copy.Remove("repeat");
                foreach (var key in new[] { "label", "short_label" })
                    if (copy[key] is JsonValue v && v.TryGetValue<string>(out var text))
                        copy[key] = text.Replace("{#}", (n + 1).ToString(CultureInfo.InvariantCulture));
                foreach (var (_, child) in copy.ToList())
                    if (child is JsonObject d)
                        d["start"] = (ToLong(d["start"]) ?? 0) + step * n;
                yield return copy;
            }
        }
    }

    /// <summary>bcd / int (and other integer encodings), with scale and offset applied. Null when out of range.</summary>
    static long? Number(JsonObject d, Memory mem)
    {
        var enc = Str(d["encoding"]);
        var bytes = mem.Bytes(d, enc);
        if (bytes == null) return null;
        decimal value = 0;
        switch (enc)
        {
            case "bcd":
                foreach (var b in bytes) value = value * 100 + Digit(b >> 4) * 10 + Digit(b & 0xF);
                break;
            case "int" or "bits" or "bool" or "enum":
                foreach (var b in bytes) value = value * 256 + b;
                break;
            default:
                return null;
        }
        value *= ToDecimal(d["scale"]) ?? 1;
        value += ToDecimal(d["offset"]) ?? 0;
        return value <= 0 ? 0 : value >= long.MaxValue ? long.MaxValue : (long)value;

        static int Digit(int n) => n > 9 ? 0 : n;
    }

    /// <summary>"ch" strings, as nvram_parser.py formats them: an unused entry (equal to its default) is "".</summary>
    static string? Text(JsonObject d, Memory mem)
    {
        if (Str(d["encoding"]) != "ch") return null;
        var bytes = mem.Bytes(d, "ch");
        if (bytes == null) return null;
        var charMap = Str(mem.Meta["char_map"]);
        var stopAtNull = (Str(d["null"]) ?? "ignore") != "ignore";
        var sb = new StringBuilder();
        foreach (var b in bytes)
        {
            char c;
            if (charMap != null)
            {
                if (b >= charMap.Length) continue;
                c = charMap[b];
            }
            else if (b == 0 && stopAtNull) break;
            else c = (char)b;
            if (IsPrintable(c)) sb.Append(c);
        }
        var text = sb.ToString();
        if (text == (Str(d["default"]) ?? "   ")) return "";
        return text.Trim();
    }

    /// <summary>Python's str.isprintable() for Latin-1: no control/format characters or non-space separators.</summary>
    static bool IsPrintable(char c) => c == ' ' || char.GetUnicodeCategory(c) is not (
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
        or UnicodeCategory.OtherNotAssigned or UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator);

    /// <summary>The .nv contents placed at the platform's NVRAM address, plus the layout's byte order and nibble settings.</summary>
    sealed class Memory
    {
        public readonly JsonObject Meta;
        readonly byte[] _nv;
        readonly long _base;
        readonly int _size;
        readonly bool _bigEndian;
        readonly List<(long Address, long Size, string Nibble)> _layout = [];

        public Memory(JsonObject? platform, JsonObject meta, byte[] nv)
        {
            Meta = meta;
            _nv = nv;
            if (platform?["memory_layout"] is JsonArray regions)
            {
                var found = false;
                foreach (var r in regions.OfType<JsonObject>())
                {
                    var address = ToLong(r["address"]) ?? 0;
                    var size = ToLong(r["size"]) ?? 0;
                    _layout.Add((address, size, Str(r["nibble"]) ?? "both"));
                    if (!found && Str(r["type"]) == "nvram")
                    {
                        found = true;
                        _base = address;
                        _size = (int)Math.Min(size, nv.Length);
                    }
                }
                _bigEndian = Str(platform["endian"]) != "little";
            }
            else
            {
                // Maps without a platform: the whole file at address 0 (as the reference parser does).
                _size = nv.Length;
                _layout.Add((0, nv.Length, Str(meta["nibble"]) ?? "both"));
                _bigEndian = Str(meta["endian"]) != "little";
            }
        }

        /// <summary>The descriptor's bytes: little-endian numbers reversed, nibbles combined, mask applied.</summary>
        public List<int>? Bytes(JsonObject d, string? encoding)
        {
            var offsets = Offsets(d);
            if (offsets.Count == 0) return null;
            var bytes = new List<int>(offsets.Count);
            foreach (var a in offsets)
            {
                var i = a - _base;
                if (i < 0 || i >= _size) return null;
                bytes.Add(_nv[i]);
            }

            var littleEndian = (Str(d["endian"]) ?? (_bigEndian ? "big" : "little")) == "little";
            if (littleEndian && encoding is "bcd" or "int" or "bits" or "bool") bytes.Reverse();

            var nibble = NibbleFor(d, offsets[0]);
            if (nibble != "both")
            {
                // Same pairing as the reference parser: an odd count leaves the first nibble on its own.
                var packed = new List<int>();
                var value = 0;
                for (var k = 0; k < bytes.Count; k++)
                {
                    var n = nibble == "low" ? bytes[k] & 0x0F : bytes[k] >> 4;
                    value = (value << 4) + n;
                    if ((bytes.Count - k - 1) % 2 == 0) { packed.Add(value); value = 0; }
                }
                bytes = packed;
            }

            if (ToLong(d["mask"]) is { } mask)
                for (var k = 0; k < bytes.Count; k++) bytes[k] &= (int)mask;
            return bytes;
        }

        string NibbleFor(JsonObject d, long address)
        {
            if (d["packed"] is JsonValue p && p.TryGetValue<bool>(out var packed) && !packed) return "low";
            if (Str(d["nibble"]) is { } own) return own;
            foreach (var (start, size, nibble) in _layout)
                if (address >= start && address < start + size) return nibble;
            return "both";
        }

        static List<long> Offsets(JsonObject d)
        {
            var start = ToLong(d["start"]) ?? 0;
            if (d["offsets"] is JsonArray list)
                return list.Select(o => ToLong(o)).OfType<long>().Select(o => start + o).ToList();
            var end = start;
            if (ToLong(d["length"]) is { } length) end = start + Math.Clamp(length, 1, 4096) - 1;
            else if (ToLong(d["end"]) is { } e && e >= start && e - start < 4096) end = e;
            var result = new List<long>();
            for (var a = start; a <= end; a++) result.Add(a);
            return result;
        }
    }

    // ---- JSON helpers (map numbers are ints or "0x…" strings) ----------------------------

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static long? ToLong(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        if (!v.TryGetValue<string>(out var s)) return null;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null;
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : null;
    }

    static decimal? ToDecimal(JsonNode? n)
    {
        if (n is JsonValue v && v.TryGetValue<double>(out var d) && v.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            return (decimal)d;
        return ToLong(n);
    }
}
