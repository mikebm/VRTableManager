using System.Security.Cryptography;
using System.Text;
using OpenMcdf;

namespace VrTableManager.Services;

/// <summary>One picture stored inside a .vpx file.</summary>
public sealed record VpxImageInfo(int Index, string Name, int Width, int Height);

/// <summary>
/// Reads pictures straight out of a .vpx table file. A .vpx is a compound file: GameStg/GameData holds
/// the table's settings (including the name of the playfield image), and GameStg/Image0..N each hold
/// one picture as the original PNG/JPEG/WebP bytes. Both use VPX's "BIFF" records:
/// [int32 length][4-char tag][length-4 bytes of data]. The playfield picture is usually 2048–4096 px,
/// far sharper than the small screenshots on VPS, so the VR lobby prefers it.
/// </summary>
public static class VpxImages
{
    const int HeaderBytes = 4096; // enough to read an image's name and size without loading the picture

    /// <summary>Name of the playfield image (GameData's IMAG record), or null.</summary>
    public static string? PlayfieldImageName(string vpxPath)
    {
        using var root = RootStorage.OpenRead(vpxPath);
        var game = root.OpenStorage("GameStg");
        if (!game.TryOpenStream("GameData", out var data) || data == null)
        {
            return null;
        }

        using (data)
        {
            foreach (var (tag, payload) in Records(ReadAll(data, 4 * 1024 * 1024)))
            {
                if (tag == "IMAG")
                {
                    return ReadString(payload);
                }
            }
        }
        return null;
    }

    /// <summary>Name and size of every picture in the table (cheap: only the start of each is read).</summary>
    public static List<VpxImageInfo> ListImages(string vpxPath)
    {
        var list = new List<VpxImageInfo>();
        using var root = RootStorage.OpenRead(vpxPath);
        var game = root.OpenStorage("GameStg");
        for (var i = 0; i < 10_000; i++)
        {
            if (!game.TryOpenStream("Image" + i, out var s) || s == null)
            {
                break;
            }

            using (s)
            {
                string? name = null;
                int w = 0,
                    h = 0;
                foreach (var (tag, payload) in Records(ReadAll(s, HeaderBytes)))
                {
                    if (tag == "NAME" && name == null)
                    {
                        name = ReadString(payload);
                    }
                    else if (tag == "WDTH" && payload.Length >= 4)
                    {
                        w = BitConverter.ToInt32(payload, 0);
                    }
                    else if (tag == "HGHT" && payload.Length >= 4)
                    {
                        h = BitConverter.ToInt32(payload, 0);
                    }
                    else if (tag is "JPEG" or "BITS")
                    {
                        break; // the picture itself starts here
                    }
                }
                if (name != null)
                {
                    list.Add(new VpxImageInfo(i, name, w, h));
                }
            }
        }
        return list;
    }

    /// <summary>
    /// The playfield picture: the image GameData names, if it's shaped like a playfield. Many modern
    /// tables draw the playfield on a 3D mesh and leave that name empty or pointing at something else
    /// (a ball texture, say), so otherwise: the biggest playfield-shaped image named like one
    /// ("playfield", "pf"). Returns the encoded bytes (PNG/JPEG/WebP…), or null.
    /// </summary>
    public static byte[]? ExtractPlayfield(string vpxPath)
    {
        var pick = PickPlayfield(ListImages(vpxPath), PlayfieldImageName(vpxPath));
        return pick == null ? null : ExtractImage(vpxPath, pick.Index);
    }

    /// <summary>Portrait, roughly 1:2 like a playfield (20.25" × 42"), and big enough to be the real thing.</summary>
    public static bool IsPlayfieldShaped(VpxImageInfo i) =>
        i.Width >= 512 && i.Height >= i.Width * 1.5 && i.Height <= i.Width * 3.2;

    public static VpxImageInfo? PickPlayfield(
        IReadOnlyList<VpxImageInfo> images,
        string? namedPlayfield
    )
    {
        var named = images.FirstOrDefault(i =>
            namedPlayfield != null
            && string.Equals(i.Name, namedPlayfield, StringComparison.OrdinalIgnoreCase)
        );
        if (named != null && IsPlayfieldShaped(named) && !IsPartOfPlayfield(named.Name))
        {
            return named;
        }
        // Shape alone isn't enough: VLM-baked tables (e.g. Metallica) have no flat playfield picture,
        // and their biggest portrait image is a light overlay. Those fall back to the VPS picture.
        // Nor is the name, in a VLM-baked table: its playfield is baked into the VLM pictures, and what's left named
        // like one is the author's reference (Fish Tales: "FT_PF_BlueprintOverlayed_New", the art under half-clear
        // white parts outlines), which came out with white blotches. So there, only the picture GameData names.
        if (images.Any(i => i.Name.StartsWith("VLM.", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return images
            .Where(i =>
                IsPlayfieldShaped(i)
                && LooksLikePlayfieldName(i.Name)
                && !IsPartOfPlayfield(i.Name)
                && !IsReference(i.Name)
            )
            .OrderByDescending(i => (long)i.Width * i.Height)
            .FirstOrDefault();
    }

    /// <summary>A picture the author worked from, not the playfield: a blueprint, an overlay or a screenshot.</summary>
    static bool IsReference(string name) =>
        name.Contains("blueprint", StringComparison.OrdinalIgnoreCase)
        || name.Contains("overlay", StringComparison.OrdinalIgnoreCase)
        || name.Contains("screenshot", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Only part of the playfield ("lowerplayfield" on Black Knight 2000, whose upper half comes from another picture):
    /// shown alone it left the upper half black, so the VPS picture is used instead. Only a word that starts with
    /// lower/upper counts ("lowerplayfield", "Playfield_Upper", "PlayfieldLower"), not one that has it inside ("Flowers").
    /// </summary>
    static bool IsPartOfPlayfield(string name) => PartOfPlayfield.IsMatch(name);

    static readonly System.Text.RegularExpressions.Regex PartOfPlayfield = new(
        @"(?:^|(?<=[^A-Za-z]))(?i:lower|upper)|(?<=[a-z])(?:Lower|Upper)"
    );

    static bool LooksLikePlayfieldName(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("playfield")
            || n == "pf"
            || n.StartsWith("pf_")
            || n.StartsWith("pf ")
            || n.EndsWith("_pf")
            || n.Contains("_pf_");
    }

    /// <summary>The encoded bytes of Image{index}: the DATA record that follows its JPEG marker.</summary>
    public static byte[]? ExtractImage(string vpxPath, int index)
    {
        using var root = RootStorage.OpenRead(vpxPath);
        var game = root.OpenStorage("GameStg");
        if (!game.TryOpenStream("Image" + index, out var s) || s == null)
        {
            return null;
        }

        using (s)
        {
            var inJpeg = false;
            foreach (var (tag, payload) in Records(ReadAll(s, int.MaxValue)))
            {
                if (tag == "JPEG")
                {
                    inJpeg = true; // an encoded picture follows (despite the name: PNG, JPEG, WebP…)
                }
                else if (tag == "BITS")
                {
                    return null; // old raw bitmap: not worth decoding here
                }
                else if (inJpeg && tag == "DATA")
                {
                    return payload;
                }
            }
        }
        return null;
    }

    /// <summary>Raised when the choice of picture changes, so cached picks are made again (2: no "lower"/"upper" parts; 3: no references, nothing unnamed in VLM tables).</summary>
    const int PickVersion = 3;

    /// <summary>True when <see cref="CachedPlayfield"/> would answer from the cache, without reading the table.</summary>
    public static bool HasCachedPlayfield(string vpxPath, string cacheDir)
    {
        try
        {
            return PlayfieldCacheFiles(vpxPath, cacheDir) is var (file, none)
                && (File.Exists(file) || File.Exists(none));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The cached picture and the "no usable picture" marker for this version of the table; null if it's gone.</summary>
    static (string File, string None)? PlayfieldCacheFiles(string vpxPath, string cacheDir)
    {
        var fi = new FileInfo(vpxPath);
        if (!fi.Exists)
        {
            return null;
        }

        var key = Convert.ToHexString(
            SHA1.HashData(
                Encoding.UTF8.GetBytes(
                    $"{Path.GetFullPath(vpxPath).ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{PickVersion}"
                )
            )
        );
        return (
            Path.Combine(cacheDir, "vpx-" + key + ".img"),
            Path.Combine(cacheDir, "vpx-" + key + ".none")
        );
    }

    /// <summary>
    /// The playfield picture as a file in <paramref name="cacheDir"/>, extracting it the first time.
    /// Keyed on the table's path, size and date, so an updated table gets its new picture. A table with
    /// no usable picture is remembered too, so it isn't re-read every time. Never throws.
    /// </summary>
    public static string? CachedPlayfield(string vpxPath, string cacheDir)
    {
        try
        {
            if (PlayfieldCacheFiles(vpxPath, cacheDir) is not var (file, none))
            {
                return null;
            }

            Directory.CreateDirectory(cacheDir);
            if (File.Exists(file))
            {
                return file;
            }

            if (File.Exists(none))
            {
                return null;
            }

            var bytes = ExtractPlayfield(vpxPath);
            if (bytes == null || bytes.Length < 16)
            {
                File.WriteAllText(none, "");
                return null;
            }
            var tmp = file + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, file, overwrite: true);
            return file;
        }
        catch
        {
            return null; // locked, corrupt or not a compound file: the lobby falls back to the VPS picture
        }
    }

    // ---- Backglass ------------------------------------------------------------------

    /// <summary>
    /// The table's own backglass picture, by name: what a VR table shows in its backbox ("BackglassImage",
    /// "BackGlassImage", "backglass_50th", X-Men's "x-menBG") or a translite ("backboxtranslite"). Only the glass,
    /// made for this table, so it beats the catalog's picture, which is often a photo of the whole backbox front.
    /// Landscape-ish (0.9–1.9 wide per tall) and at least 600 px across; the biggest wins. Null when there's none.
    /// </summary>
    public static VpxImageInfo? PickBackglass(IEnumerable<VpxImageInfo> images) =>
        images
            .Where(i =>
                i.Width >= 600
                && i.Height > 0
                && (double)i.Width / i.Height is >= 0.9 and <= 1.9
                && LooksLikeBackglassName(i.Name)
            )
            .OrderByDescending(i => (long)i.Width * i.Height)
            .FirstOrDefault();

    static bool LooksLikeBackglassName(string name)
    {
        var n = name.ToLowerInvariant();
        if (
            n.Contains("scratch")
            || n.Contains("backdrop")
            || n.Contains("dmd")
            || n.Contains("grill")
            || n.Contains("lamp")
            || n.Contains("bulb")
            || n.Contains("light")
        )
        {
            return false;
        }

        return n.Contains("backglass")
            || n.Contains("back glass")
            || n.Contains("back_glass")
            || n.Contains("translite")
            || System.Text.RegularExpressions.Regex.IsMatch(n, @"(^|[^a-z])bg($|[^a-z])|[a-z]bg$");
    }

    /// <summary>
    /// What the cache knows about the table's own backglass picture, without reading the table: true (extracted, see
    /// <see cref="CachedBackglass"/>), false (it has none), null (not looked yet). Never throws.
    /// </summary>
    public static bool? KnownBackglass(string vpxPath, string cacheDir) =>
        Known(vpxPath, cacheDir, "bg");

    /// <summary>Like <see cref="KnownBackglass"/>, for the back panel picture (<see cref="CachedBackPanel"/>).</summary>
    public static bool? KnownBackPanel(string vpxPath, string cacheDir) =>
        Known(vpxPath, cacheDir, "bp");

    /// <summary>
    /// Raised when <see cref="PickBackglass"/>, <see cref="PickBackPanel"/> or <see cref="PickBlade"/> change, so their
    /// cached picks and "none" markers are made again without re-extracting every playfield (whose key carries
    /// <see cref="PickVersion"/>). Version 1 leaves the prefix as it was.
    /// </summary>
    const int ArtPickVersion = 1;

    static string Versioned(string prefix) =>
        ArtPickVersion > 1 ? $"{prefix}v{ArtPickVersion}-" : prefix;

    static bool? Known(string vpxPath, string cacheDir, string prefix)
    {
        try
        {
            if (PlayfieldCacheFiles(vpxPath, cacheDir) is not var (pf, pfNone))
            {
                return false;
            }

            prefix = Versioned(prefix);
            return File.Exists(Path.Combine(cacheDir, prefix + Path.GetFileName(pf))) ? true
                : File.Exists(Path.Combine(cacheDir, prefix + Path.GetFileName(pfNone))) ? false
                : null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The table's own backglass picture (<see cref="PickBackglass"/>) as a file in <paramref name="cacheDir"/>,
    /// extracted the first time; null when it has none (remembered too). Keyed like <see cref="CachedPlayfield"/>.
    /// Never throws.
    /// </summary>
    public static string? CachedBackglass(string vpxPath, string cacheDir) =>
        CachedPick(vpxPath, cacheDir, "bg", PickBackglass);

    /// <summary>
    /// The table's own back panel picture: the board behind the playfield, under the backbox ("backpanel", "backwall",
    /// "back_wall", "backboard"; X-Men's "backpanel", JAWS's "backwallamity"). A wide strip (1.8–4.5 wide per tall),
    /// at least 400 px across; the biggest wins. Not a VR room's walls ("VRBackWall", "VR_Wall_…"), nor a lighting pass.
    /// </summary>
    public static VpxImageInfo? PickBackPanel(IEnumerable<VpxImageInfo> images) =>
        images
            .Where(i =>
            {
                var n = i.Name.ToLowerInvariant();
                return i.Width >= 400
                    && i.Height > 0
                    && (double)i.Width / i.Height is >= 1.8 and <= 4.5
                    && (
                        n.Contains("backpanel")
                        || n.Contains("back panel")
                        || n.Contains("back_panel")
                        || n.Contains("backwall")
                        || n.Contains("back_wall")
                        || n.Contains("backboard")
                    )
                    && !n.StartsWith("vr")
                    && !n.Contains("room")
                    && !n.Contains("filament")
                    && !n.Contains("steel")
                    && !n.Contains("glass");
            })
            .OrderByDescending(i => (long)i.Width * i.Height)
            .FirstOrDefault();

    /// <summary>
    /// The table's own art for the inner side wall above the playfield (a "blade") on one side: a strip named for it
    /// ("bladel" / "blader", "sideleft" / "sideright", "LeftBlade…", "bladeLEFT…", "sidearts", "sidebladejaws",
    /// "SIDEWALL_…"), long and narrow (2.5 to 1 or more, either way round) unless it names its side. One for this side
    /// first, else one for both, else the other side's. Lit versions before unlit, flasher versions only when there's
    /// nothing else. Not the community kit's sheet (PinCab_Blades: mapped for its own parts), side rails, cabinet sides
    /// or wood trim. Null when there's none.
    /// </summary>
    public static VpxImageInfo? PickBlade(IEnumerable<VpxImageInfo> images, bool left)
    {
        var candidates = images
            .Select(i => (Info: i, Side: BladeSide(i)))
            .Where(c => c.Side != null)
            .ToList();
        int Rank((VpxImageInfo Info, char? Side) c)
        {
            var n = c.Info.Name.ToLowerInvariant();
            var side =
                c.Side == (left ? 'L' : 'R') ? 0
                : c.Side == 'B' ? 1
                : 2;
            var flash = n.Contains("flash") ? 1 : 0;
            var unlit = n.Contains("off") ? 1 : 0;
            return side * 100 + flash * 10 + unlit;
        }
        return candidates
            .OrderBy(Rank)
            .ThenByDescending(c => (long)c.Info.Width * c.Info.Height)
            .Select(c => c.Info)
            .FirstOrDefault();
    }

    /// <summary>'L' or 'R' for a blade picture named for a side, 'B' for one for both, null for anything else.</summary>
    static char? BladeSide(VpxImageInfo i)
    {
        var n = i.Name.ToLowerInvariant();
        if (
            i.Width < 256
            || i.Height <= 0
            || n.StartsWith("pincab")
            || n.StartsWith("vr")
            || n.Contains("rail")
            || n.Contains("wood")
            || n.Contains("sidecab")
            || n.Contains("pfside")
            || n.Contains("target")
            || n.Contains("shadow")
            || n.Contains("glow")
            || n.Contains("patch")
            || n.Contains("decal")
        )
        {
            return null;
        }

        var blade = n.Contains("blade");
        var sideArt =
            n.Contains("sidewall")
            || n.Contains("side wall")
            || n.Contains("sideart")
            || System.Text.RegularExpressions.Regex.IsMatch(n, @"^side(left|right|l|r)\b");
        if (!blade && !sideArt)
        {
            return null;
        }

        char side =
            n.Contains("left") ? 'L'
            : n.Contains("right") ? 'R'
            : System.Text.RegularExpressions.Regex.IsMatch(
                n,
                @"blade_?l(\b|\d|_)|^side_?l\b|bladel"
            )
                ? 'L'
            : System.Text.RegularExpressions.Regex.IsMatch(
                n,
                @"blade_?r(\b|\d|_)|^side_?r\b|blader"
            )
                ? 'R'
            : 'B';
        var ratio = (double)i.Width / i.Height;
        var strip = ratio >= 2.5 || ratio <= 0.4;
        // Only a strip: a squarish picture named for a blade is a baked lighting layer for that table's own wall
        // (The Addams Family's "bladeLEFT_giON": its playfield's reflection, mostly black), not art to lay on one.
        return strip ? side : null;
    }

    public static string? CachedBladeLeft(string vpxPath, string cacheDir) =>
        CachedPick(vpxPath, cacheDir, "bll", i => PickBlade(i, left: true));

    public static string? CachedBladeRight(string vpxPath, string cacheDir) =>
        CachedPick(vpxPath, cacheDir, "blr", i => PickBlade(i, left: false));

    public static bool? KnownBladeLeft(string vpxPath, string cacheDir) =>
        Known(vpxPath, cacheDir, "bll");

    public static bool? KnownBladeRight(string vpxPath, string cacheDir) =>
        Known(vpxPath, cacheDir, "blr");

    /// <summary>The table's own back panel picture (<see cref="PickBackPanel"/>), cached like <see cref="CachedBackglass"/>.</summary>
    public static string? CachedBackPanel(string vpxPath, string cacheDir) =>
        CachedPick(vpxPath, cacheDir, "bp", PickBackPanel);

    /// <summary>A picture picked out of the table by <paramref name="pick"/>, cached under <paramref name="prefix"/>; see <see cref="CachedBackglass"/>.</summary>
    static string? CachedPick(
        string vpxPath,
        string cacheDir,
        string prefix,
        Func<IEnumerable<VpxImageInfo>, VpxImageInfo?> pick
    )
    {
        try
        {
            if (PlayfieldCacheFiles(vpxPath, cacheDir) is not var (pf, pfNone))
            {
                return null;
            }

            prefix = Versioned(prefix);
            string file = Path.Combine(cacheDir, prefix + Path.GetFileName(pf)),
                none = Path.Combine(cacheDir, prefix + Path.GetFileName(pfNone));
            Directory.CreateDirectory(cacheDir);
            if (File.Exists(file))
            {
                return file;
            }

            if (File.Exists(none))
            {
                return null;
            }

            var picked = pick(ListImages(vpxPath));
            var bytes = picked == null ? null : ExtractImage(vpxPath, picked.Index);
            if (bytes == null || bytes.Length < 16)
            {
                File.WriteAllText(none, "");
                return null;
            }
            var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, file, overwrite: true);
            return file;
        }
        catch
        {
            return null; // locked, corrupt or not a compound file: the lobby does without
        }
    }

    // ---- BIFF ----------------------------------------------------------------------

    /// <summary>Walks [int32 length][tag][data] records; stops at the end or at anything that doesn't parse.</summary>
    static IEnumerable<(string Tag, byte[] Data)> Records(byte[] buf)
    {
        var pos = 0;
        while (pos + 8 <= buf.Length)
        {
            var len = BitConverter.ToInt32(buf, pos);
            if (len < 4 || pos + 4 + len > buf.Length)
            {
                yield break;
            }

            var tag = Encoding.ASCII.GetString(buf, pos + 4, 4);
            var data = new byte[len - 4];
            Buffer.BlockCopy(buf, pos + 8, data, 0, data.Length);
            yield return (tag, data);
            pos += 4 + len;
        }
    }

    /// <summary>VPX strings: [int32 length][bytes].</summary>
    static string? ReadString(byte[] data)
    {
        if (data.Length < 4)
        {
            return null;
        }

        var n = BitConverter.ToInt32(data, 0);
        if (n < 0 || 4 + n > data.Length)
        {
            return null;
        }

        return Encoding.Latin1.GetString(data, 4, n).TrimEnd('\0');
    }

    static byte[] ReadAll(Stream s, int max)
    {
        var length = (int)Math.Min(s.Length, max);
        var buf = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = s.Read(buf, read, length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }
        return read == length ? buf : buf[..read];
    }
}
