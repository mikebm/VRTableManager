using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace VrTableManager.Services;

/// <summary>
/// Reads the backglass picture out of a .directb2s file: an XML file whose pictures are base64 attributes. The one
/// wanted is <c>&lt;BackglassImage Value="…"/&gt;</c>, the glass itself (lit-bulb overlays, the DMD/grille picture and
/// a thumbnail are separate). It sits near the end, after the bulb pictures, in files of 5–30 MB, so the file is read
/// with a forward-only <see cref="XmlReader"/> and only that one attribute is kept. The bottom <c>GrillHeight</c>
/// pixels of the picture are the speaker grille in some backglasses; they're reported so the lobby can trim them off.
/// </summary>
public static class DirectB2s
{
    /// <summary>Bump when what's extracted changes, so cached copies are made again.</summary>
    const int Version = 1;

    /// <summary>The backglass picture (PNG/JPEG bytes) and the grille height in pixels, or null when there's none.</summary>
    public static (byte[] Image, int GrillHeight)? ReadBackglass(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1 << 16
        );
        using var xml = XmlReader.Create(stream, settings);
        var grill = 0;
        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            // GrillHeight is in the header, before any picture.
            if (xml.Name == "GrillHeight")
            {
                _ = int.TryParse(xml.GetAttribute("Value"), out grill);
            }
            else if (xml.Name == "BackglassImage")
            {
                // Base64 with line breaks in it, which Convert skips.
                var b64 = xml.GetAttribute("Value");
                if (string.IsNullOrWhiteSpace(b64))
                {
                    return null;
                }

                var bytes = Convert.FromBase64String(b64);
                return bytes.Length < 16 ? null : (bytes, Math.Max(0, grill));
            }
        }

        return null;
    }

    static (string File, string Meta, string None)? CacheFiles(string path, string cacheDir)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists)
        {
            return null;
        }

        var key = Convert.ToHexString(
            SHA1.HashData(
                Encoding.UTF8.GetBytes(
                    $"{Path.GetFullPath(path).ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{Version}"
                )
            )
        );
        var stem = Path.Combine(cacheDir, "b2s-" + key);
        return (stem + ".img", stem + ".grill", stem + ".none");
    }

    /// <summary>
    /// The backglass picture as a file in <paramref name="cacheDir"/> and its grille height, extracting it the first
    /// time. Keyed on the file's path, size and date, so a replaced backglass is read again; a file without a picture
    /// is remembered too. Null when there's no picture. Never throws.
    /// </summary>
    public static (string File, int GrillHeight)? CachedBackglass(string path, string cacheDir)
    {
        try
        {
            if (CacheFiles(path, cacheDir) is not var (file, meta, none))
            {
                return null;
            }

            Directory.CreateDirectory(cacheDir);
            if (File.Exists(file))
            {
                var g =
                    File.Exists(meta) && int.TryParse(File.ReadAllText(meta), out var h) ? h : 0;
                return (file, g);
            }

            if (File.Exists(none))
            {
                return null;
            }

            if (ReadBackglass(path) is not var (bytes, grill))
            {
                File.WriteAllText(none, "");
                return null;
            }

            File.WriteAllText(
                meta,
                grill.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
            var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, file, overwrite: true);
            return (file, grill);
        }
        catch
        {
            return null; // locked or not a backglass file: the lobby falls back to the catalog's picture
        }
    }

    /// <summary>
    /// How many rows to trim off the bottom of a <paramref name="height"/>-pixel backglass: its grille, when the file
    /// gives one that leaves most of the picture (some give the grille of a different-sized original).
    /// </summary>
    public static int TrimFor(int height, int grillHeight) =>
        grillHeight > 0 && grillHeight < height * 0.4 ? grillHeight : 0;
}
