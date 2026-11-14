using System.Text;
using VrTableManager.Lobby;
using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>
/// The VR lobby's backglass order (your picture → the installed .directb2s → the catalog's → title plate), reading
/// the picture out of a .directb2s, and the generic-cabinet setting.
/// </summary>
static class LobbyBackglassChecks
{
    public static List<(bool, string)> Run(string root)
    {
        var results = new List<(bool, string)>();
        var folder = Path.Combine(root, "b2s order table");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "vrtm.json"), "{}");
        var vpx = Path.Combine(folder, "Table.vpx");
        File.WriteAllBytes(vpx, [1]);
        var t = new InstalledTable(
            new TableManifest
            {
                GameId = "g9",
                GameName = "B2S Table",
                VpxFile = "Table.vpx",
            },
            vpx,
            folder
        );
        var lobby = LobbyLibrary.From(t);
        var index = new Dictionary<string, string> { ["g9"] = "https://example.com/vps-bg.jpg" };

        var noB2s = LobbyArt.BackglassFor(lobby, index);
        var noB2sNoIndex = LobbyArt.BackglassFor(lobby, new Dictionary<string, string>());

        // A .directb2s laid out as real ones are: header, bulb pictures (which mustn't be taken), then the glass.
        var glass = FakePng(300);
        var bulb = FakePng(40);
        var b2s = Backglasses.PathFor(t);
        File.WriteAllText(b2s, B2sXml(glass, bulb, grill: 120));

        var withB2s = LobbyArt.BackglassFor(lobby, index);
        results.Add(
            (
                noB2s == "https://example.com/vps-bg.jpg"
                    && noB2sNoIndex == null
                    && withB2s == LobbyArt.B2sScheme + b2s
                    && LobbyArt.BackglassFallback(lobby, index) == "https://example.com/vps-bg.jpg",
                "lobby backglass: the installed .directb2s before the catalog's picture, which stays its fallback"
            )
        );

        var read = DirectB2s.ReadBackglass(b2s);
        results.Add(
            (
                read is var (img, grill) && img.SequenceEqual(glass) && grill == 120,
                "lobby backglass: reads the BackglassImage (not a bulb picture) and the grille height out of a .directb2s"
            )
        );

        var cache = Path.Combine(root, "b2s-cache");
        var first = DirectB2s.CachedBackglass(b2s, cache);
        var again = DirectB2s.CachedBackglass(b2s, cache);
        results.Add(
            (
                first is var (f, g)
                    && File.ReadAllBytes(f).SequenceEqual(glass)
                    && g == 120
                    && again == first
                    && DirectB2s.TrimFor(1000, 120) == 120
                    && DirectB2s.TrimFor(200, 120) == 0
                    && DirectB2s.TrimFor(1000, 0) == 0,
                "lobby backglass: the .directb2s picture is cached with its grille height; an implausible grille isn't trimmed"
            )
        );

        var empty = Path.Combine(root, "empty.directb2s");
        File.WriteAllText(
            empty,
            "<DirectB2SData Version=\"1.2\"><Name Value=\"x\" /></DirectB2SData>"
        );
        results.Add(
            (
                DirectB2s.ReadBackglass(empty) == null
                    && DirectB2s.CachedBackglass(empty, cache) == null
                    && DirectB2s.CachedBackglass(Path.Combine(root, "missing.directb2s"), cache)
                        == null,
                "lobby backglass: a .directb2s without a picture, or none at all, gives nothing (the catalog's is used)"
            )
        );

        var pic = Path.Combine(root, "b2s-mine.png");
        File.WriteAllBytes(pic, FakePng(10));
        UserArtwork.Set(t, UserArtwork.Kind.Backglass, pic);
        results.Add(
            (
                UserArtwork.PathOf(LobbyArt.BackglassFor(lobby, index)!) != null,
                "lobby backglass: a picture you chose still wins over the .directb2s"
            )
        );
        UserArtwork.Remove(t, UserArtwork.Kind.Backglass);

        // The playfield picture out of the table: never the author's reference, and in a VLM-baked table only the
        // one GameData names (Fish Tales' blueprint overlay showed as white blotches).
        VpxImageInfo I(string name, int w = 2048, int h = 4096) => new(0, name, w, h);
        results.Add(
            (
                VpxImages.PickPlayfield(
                    [I("FT_PF_BlueprintOverlayed_New"), I("PF_screenshot")],
                    null
                ) == null
                    && VpxImages
                        .PickPlayfield([I("Playfield_overlay", 4096, 8192), I("Playfield")], null)
                        ?.Name == "Playfield"
                    && VpxImages.PickPlayfield(
                        [I("FT_PF_New"), I("VLM.Nestmap0", 4096, 4096)],
                        null
                    ) == null
                    && VpxImages
                        .PickPlayfield([I("WCS94_PF"), I("VLM.Nestmap0", 4096, 4096)], "WCS94_PF")
                        ?.Name == "WCS94_PF",
                "lobby playfield: no blueprint/overlay/screenshot pictures; a VLM table only gives the one it names"
            )
        );

        var s = new AppSettings();
        var ownByDefault = !LobbyArt.UsesGenericCabinet(lobby, s);
        s.SetGenericCabinetTable(vpx.ToUpperInvariant(), true);
        var perTable = LobbyArt.UsesGenericCabinet(lobby, s);
        s.SetGenericCabinetTable(vpx, false);
        var cleared =
            !LobbyArt.UsesGenericCabinet(lobby, s) && s.LobbyGenericCabinetTables.Count == 0;
        s.LobbyGenericCabinets = true;
        var all = LobbyArt.UsesGenericCabinet(lobby, s);
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        results.Add(
            (
                ownByDefault && perTable && cleared && all && back.LobbyGenericCabinets,
                "lobby cabinets: own VR cabinet by default; generic for every table or for one (by path, any case); saved"
            )
        );

        return results;
    }

    /// <summary>A PNG signature and some bytes: enough to tell pictures apart (nothing decodes it here).</summary>
    static byte[] FakePng(int size)
    {
        var b = new byte[size];
        new Random(size).NextBytes(b);
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        return b;
    }

    /// <summary>Base64 with the line breaks .NET's XML writer puts in .directb2s files (&#xD;&#xA; every 76 chars).</summary>
    static string Wrapped(byte[] bytes) =>
        Convert
            .ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks)
            .Replace("\r\n", "&#xD;&#xA;");

    static string B2sXml(byte[] glass, byte[] bulb, int grill)
    {
        var sb = new StringBuilder();
        sb.Append("<DirectB2SData Version=\"1.26\">\r\n");
        sb.Append("  <Name Value=\"Test\" />\r\n");
        sb.Append($"  <GrillHeight Value=\"{grill}\" />\r\n");
        sb.Append("  <Illumination>\r\n");
        sb.Append($"    <Bulb ID=\"1\" Image=\"{Wrapped(bulb)}\" />\r\n");
        sb.Append("  </Illumination>\r\n");
        sb.Append("  <Images>\r\n");
        sb.Append($"    <BackglassImage Value=\"{Wrapped(glass)}\" FileName=\"bg.png\" />\r\n");
        sb.Append($"    <ThumbnailImage Value=\"{Wrapped(bulb)}\" />\r\n");
        sb.Append("  </Images>\r\n");
        sb.Append("</DirectB2SData>");
        return sb.ToString();
    }
}
