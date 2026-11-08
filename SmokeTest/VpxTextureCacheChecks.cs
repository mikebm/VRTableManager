using System.Text;
using VrTableManager.Services;

/// <summary>VPX's texture list (cache\&lt;table&gt;\used_textures.xml), made from the table file before a first launch.</summary>
static class VpxTextureCacheChecks
{
    static byte[] Rec(string tag, byte[] data) =>
        [.. BitConverter.GetBytes(data.Length + 4), .. Encoding.ASCII.GetBytes(tag), .. data];

    static byte[] Str(string tag, string value) =>
        Rec(tag, [.. BitConverter.GetBytes(value.Length), .. Encoding.Latin1.GetBytes(value)]);

    static byte[] Int(string tag, int value) => Rec(tag, BitConverter.GetBytes(value));

    static void Write(OpenMcdf.Storage stg, string name, byte[] data)
    {
        using var s = stg.CreateStream(name);
        s.Write(data, 0, data.Length);
    }

    public static List<(bool, string)> Run(string root)
    {
        var results = new List<(bool, string)>();
        var dir = Path.Combine(root, "texture cache", "Ace & Co");
        Directory.CreateDirectory(dir);
        var vpx = Path.Combine(dir, "Ace & Co 1.1.vpx");
        using (var cfb = OpenMcdf.RootStorage.Create(vpx))
        {
            var game = cfb.CreateStorage("GameStg");
            string[] images = ["Playfield", "LUT_8", "Wood", "TargetNormal", "Unused", "Flash<1>"];
            for (int i = 0; i < images.Length; i++)
                Write(game, "Image" + i, [.. Str("NAME", images[i]), .. Rec("ENDB", [])]);
            // The script sits between settings: its record length doesn't cover it, so a parser that trusts it
            // reads the script as records and misses IMCG after it.
            var code = Encoding.Latin1.GetBytes("Table1.ColorGradeImage = \"Unused\"\r\n");
            Write(
                game,
                "GameData",
                [
                    .. Str("IMAG", "Playfield"),
                    .. BitConverter.GetBytes(4),
                    .. "CODE"u8,
                    .. BitConverter.GetBytes(code.Length),
                    .. code,
                    .. Str("IMCG", "LUT_8"),
                    .. Str("EIMG", "Wood"),
                    .. Rec("ENDB", []),
                ]
            );
            // A primitive: its image, a normal map, a material that happens to share an image's name; then drag points.
            Write(
                game,
                "GameItem0",
                [
                    .. BitConverter.GetBytes(19),
                    .. Str("NAME", "Unused"),
                    .. Str("IMAG", "Wood"),
                    .. Str("NRMA", "TargetNormal"),
                    .. Int("SIDS", 4),
                    .. Rec("DPNT", []),
                    .. Str("IMAG", "Unused"),
                    .. Rec("ENDB", []),
                ]
            );
            Write(
                game,
                "GameItem1",
                [
                    .. BitConverter.GetBytes(20),
                    .. Str("NAME", "f"),
                    .. Str("IMAG", "flash<1>"),
                    .. Str("IMAB", "Nope"),
                    .. Rec("ENDB", []),
                ]
            );
        }

        var used = VpxTextureCache.UsedTextures(vpx);
        results.Add(
            (
                used.SequenceEqual([
                    ("Playfield", false),
                    ("LUT_8", true),
                    ("Wood", false),
                    ("TargetNormal", true),
                    ("Flash<1>", false),
                ]),
                $"texture list: the images the table and its parts name, color grade and normal map linear, not item names or drag points ({string.Join(", ", used)})"
            )
        );

        var list = VpxTextureCache.ListPath(vpx);
        var line = VpxTextureCache.Prepare(vpx);
        var xml = File.Exists(list) ? File.ReadAllText(list) : "";
        results.Add(
            (
                line != null
                    && list == Path.Combine(dir, "cache", "Ace & Co 1.1", "used_textures.xml")
                    && xml.Contains("<texture linear=\"true\" age=\"0\">LUT_8</texture>")
                    && xml.Contains(">Flash&lt;1&gt;</texture>")
                    && System.Xml.Linq.XDocument.Parse(xml).Root!.Elements("texture").Count() == 5,
                "texture list: written to cache\\<table name>\\used_textures.xml before a first launch, as valid XML"
            )
        );

        File.WriteAllText(list, "vpx's own");
        results.Add(
            (
                VpxTextureCache.Prepare(vpx) == null
                    && File.ReadAllText(list) == "vpx's own"
                    && VpxTextureCache.Prepare(Path.Combine(dir, "missing.vpx")) == null
                    && VpxTextureCache.Prepare(
                        Path.Combine(root, "texture cache", "not a table.vpx")
                    ) == null,
                "texture list: VPX's own list is left alone; a missing table is skipped"
            )
        );
        File.WriteAllText(Path.Combine(root, "texture cache", "broken.vpx"), "not a compound file");
        results.Add(
            (
                VpxTextureCache.Prepare(Path.Combine(root, "texture cache", "broken.vpx"))
                    is { } skipped
                    && skipped.StartsWith("texture list skipped"),
                "texture list: a file it can't read is skipped, never thrown"
            )
        );
        return results;
    }
}
