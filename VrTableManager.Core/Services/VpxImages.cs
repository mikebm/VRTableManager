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
        if (!game.TryOpenStream("GameData", out var data) || data == null) return null;
        using (data)
        {
            foreach (var (tag, payload) in Records(ReadAll(data, 4 * 1024 * 1024)))
                if (tag == "IMAG") return ReadString(payload);
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
            if (!game.TryOpenStream("Image" + i, out var s) || s == null) break;
            using (s)
            {
                string? name = null;
                int w = 0, h = 0;
                foreach (var (tag, payload) in Records(ReadAll(s, HeaderBytes)))
                {
                    if (tag == "NAME" && name == null) name = ReadString(payload);
                    else if (tag == "WDTH" && payload.Length >= 4) w = BitConverter.ToInt32(payload, 0);
                    else if (tag == "HGHT" && payload.Length >= 4) h = BitConverter.ToInt32(payload, 0);
                    else if (tag is "JPEG" or "BITS") break; // the picture itself starts here
                }
                if (name != null) list.Add(new VpxImageInfo(i, name, w, h));
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

    public static VpxImageInfo? PickPlayfield(IReadOnlyList<VpxImageInfo> images, string? namedPlayfield)
    {
        var named = images.FirstOrDefault(i => namedPlayfield != null && string.Equals(i.Name, namedPlayfield, StringComparison.OrdinalIgnoreCase));
        if (named != null && IsPlayfieldShaped(named)) return named;
        // Shape alone isn't enough: VLM-baked tables (e.g. Metallica) have no flat playfield picture,
        // and their biggest portrait image is a light overlay. Those fall back to the VPS picture.
        return images.Where(i => IsPlayfieldShaped(i) && LooksLikePlayfieldName(i.Name))
            .OrderByDescending(i => (long)i.Width * i.Height)
            .FirstOrDefault();
    }

    static bool LooksLikePlayfieldName(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("playfield") || n == "pf" || n.StartsWith("pf_") || n.StartsWith("pf ") || n.EndsWith("_pf") || n.Contains("_pf_");
    }

    /// <summary>The encoded bytes of Image{index}: the DATA record that follows its JPEG marker.</summary>
    public static byte[]? ExtractImage(string vpxPath, int index)
    {
        using var root = RootStorage.OpenRead(vpxPath);
        var game = root.OpenStorage("GameStg");
        if (!game.TryOpenStream("Image" + index, out var s) || s == null) return null;
        using (s)
        {
            var inJpeg = false;
            foreach (var (tag, payload) in Records(ReadAll(s, int.MaxValue)))
            {
                if (tag == "JPEG") inJpeg = true;      // an encoded picture follows (despite the name: PNG, JPEG, WebP…)
                else if (tag == "BITS") return null;    // old raw bitmap: not worth decoding here
                else if (inJpeg && tag == "DATA") return payload;
            }
        }
        return null;
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
            var fi = new FileInfo(vpxPath);
            if (!fi.Exists) return null;
            var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
                $"{Path.GetFullPath(vpxPath).ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}")));
            Directory.CreateDirectory(cacheDir);
            var file = Path.Combine(cacheDir, "vpx-" + key + ".img");
            var none = Path.Combine(cacheDir, "vpx-" + key + ".none");
            if (File.Exists(file)) return file;
            if (File.Exists(none)) return null;
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

    // ---- BIFF ----------------------------------------------------------------------

    /// <summary>Walks [int32 length][tag][data] records; stops at the end or at anything that doesn't parse.</summary>
    static IEnumerable<(string Tag, byte[] Data)> Records(byte[] buf)
    {
        var pos = 0;
        while (pos + 8 <= buf.Length)
        {
            var len = BitConverter.ToInt32(buf, pos);
            if (len < 4 || pos + 4 + len > buf.Length) yield break;
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
        if (data.Length < 4) return null;
        var n = BitConverter.ToInt32(data, 0);
        if (n < 0 || 4 + n > data.Length) return null;
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
            if (n <= 0) break;
            read += n;
        }
        return read == length ? buf : buf[..read];
    }
}
