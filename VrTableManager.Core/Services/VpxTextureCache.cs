using System.Security;
using System.Text;
using OpenMcdf;

namespace VrTableManager.Services;

/// <summary>
/// VPX 10.8.1 keeps a list of the textures a table uses in <c>cache\&lt;table&gt;\used_textures.xml</c> next to the
/// table, and writes it when the table closes. Until that list exists, the table plays very dark (seen in VR with
/// Ace of Speed, 24 and Apollo 13), so every table's first launch was dark and the second one was fine.
/// <para>
/// Before a launch, a table without the list gets one made from the table file: every image its parts and table
/// settings name. VPX's own list is the same images (the ones it actually drew), with the color grade (LUT) and
/// normal maps marked linear; this marks those the same way. VPX rewrites the list on exit, so after the first
/// play it's VPX's own again.
/// </para>
/// </summary>
public static class VpxTextureCache
{
    public static string ListPath(string vpx) =>
        Path.Combine(
            Path.GetDirectoryName(vpx)!,
            "cache",
            Path.GetFileNameWithoutExtension(vpx),
            "used_textures.xml"
        );

    /// <summary>
    /// Writes the list when the table has none. Returns a line for the play log when it did, else null. Never
    /// throws: a table must start even if this can't be done.
    /// </summary>
    public static string? Prepare(string vpx)
    {
        try
        {
            var path = ListPath(vpx);
            if (File.Exists(path) || !File.Exists(vpx))
            {
                return null;
            }

            var used = UsedTextures(vpx);
            if (used.Count == 0)
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ToXml(used), new UTF8Encoding(false));
            return $"wrote VPX's texture list for the first launch ({used.Count} textures)";
        }
        catch (Exception e)
        {
            return "texture list skipped: " + e.Message;
        }
    }

    /// <summary>The images the table's parts and settings name, in file order; linear for the color grade and normal maps.</summary>
    public static List<(string Name, bool Linear)> UsedTextures(string vpx)
    {
        using var root = RootStorage.OpenRead(vpx);
        var game = root.OpenStorage("GameStg");
        var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; game.TryOpenStream("Image" + i, out var s) && s != null; i++)
        {
            using (s)
            {
                if (Strings(ReadAll(s), 0).FirstOrDefault(t => t.Tag == "NAME").Value is { } name)
                {
                    images.TryAdd(name, name);
                }
            }
        }

        var used = new List<(string Name, bool Linear)>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Use(string value, bool linear)
        {
            if (!images.TryGetValue(value, out var name))
            {
                return;
            }

            if (!index.TryGetValue(name, out var at))
            {
                index[name] = used.Count;
                used.Add((name, linear));
            }
            else if (linear)
            {
                used[at] = (name, true);
            }
        }
        // Table settings: the playfield image (IMAG) and the color grade (IMCG). The ball's and the environment's
        // images aren't in VPX's list either.
        using (var gd = game.OpenStream("GameData"))
        {
            foreach (var (tag, value) in Strings(ReadAll(gd), 0))
            {
                if (tag is "IMAG" or "IMCG")
                {
                    Use(value, tag == "IMCG");
                }
            }
        }
        // Parts: any setting naming an image (IMAG, IMAB, SIMG, a light's IMG1…); NRMA is a primitive's normal map.
        for (int i = 0; game.TryOpenStream("GameItem" + i, out var s) && s != null; i++)
        {
            using (s)
            {
                foreach (var (tag, value) in Strings(ReadAll(s), 4))
                {
                    if (tag != "NAME")
                    {
                        Use(value, tag == "NRMA");
                    }
                }
            }
        }

        return used;
    }

    public static string ToXml(IEnumerable<(string Name, bool Linear)> used)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<textures>\n");
        foreach (var (name, linear) in used)
        {
            sb.Append(
                $"    <texture linear=\"{(linear ? "true" : "false")}\" age=\"0\">{SecurityElement.Escape(name)}</texture>\n"
            );
        }

        return sb.Append("</textures>\n").ToString();
    }

    static byte[] ReadAll(Stream s)
    {
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// The string settings among a stream's records (length, tag, then a length-prefixed string), up to its end
    /// or a part's drag points. CODE's length doesn't cover the script; a second length after the tag does.
    /// </summary>
    static IEnumerable<(string Tag, string Value)> Strings(byte[] d, int start)
    {
        for (int p = start; p + 8 <= d.Length; )
        {
            int len = BitConverter.ToInt32(d, p);
            if (len < 4 || p + 4 + len > d.Length)
            {
                yield break;
            }

            var tag = Encoding.ASCII.GetString(d, p + 4, 4);
            if (tag is "ENDB" or "DPNT")
            {
                yield break;
            }

            if (tag == "CODE")
            {
                if (p + 12 > d.Length)
                {
                    yield break;
                }

                p += 12 + Math.Max(0, BitConverter.ToInt32(d, p + 8));
                continue;
            }
            if (len >= 8 && BitConverter.ToInt32(d, p + 8) is var n && n > 0 && n == len - 8)
            {
                yield return (tag, Encoding.Latin1.GetString(d, p + 12, n));
            }

            p += 4 + len;
        }
    }
}
