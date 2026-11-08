using VrTableManager.Lobby;
using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>Your own pictures for a table: where they're kept, and that they win in My Tables and the VR lobby.</summary>
static class UserArtworkChecks
{
    public static List<(bool, string)> Run(string root)
    {
        var results = new List<(bool, string)>();
        var folder = Path.Combine(root, "artwork table");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "vrtm.json"), "{}");
        var vpx = Path.Combine(folder, "t.vpx");
        File.WriteAllBytes(vpx, [1]);
        var t = new InstalledTable(
            new TableManifest
            {
                GameId = "g1",
                GameName = "Art Table",
                VpxFile = "t.vpx",
                ImgUrl = "https://example.com/card.jpg",
            },
            vpx,
            folder
        );
        var loose = new InstalledTable(
            new TableManifest { GameName = "Loose", VpxFile = "loose.vpx" },
            Path.Combine(root, "loose.vpx"),
            root
        );

        results.Add(
            (
                UserArtwork.FolderFor(t) == Path.Combine(folder, "artwork")
                    && UserArtwork
                        .FolderFor(loose)
                        .StartsWith(
                            Path.Combine(AppSettings.DataDir, "artwork"),
                            StringComparison.OrdinalIgnoreCase
                        ),
                "my artwork: kept in the table's artwork folder, or the app's data folder for a table without a folder of its own"
            )
        );

        var lobby = LobbyLibrary.From(t);
        var index = new Dictionary<string, string> { ["g1"] = "https://example.com/bg.jpg" };
        var before = (LobbyArt.PlayfieldSource(lobby), LobbyArt.BackglassFor(lobby, index));

        var pic = Path.Combine(root, "mine.PNG");
        File.WriteAllBytes(pic, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        UserArtwork.Set(t, UserArtwork.Kind.Playfield, pic);
        UserArtwork.Set(t, UserArtwork.Kind.Backglass, pic);
        var playfield = LobbyArt.PlayfieldSource(lobby);
        var backglass = LobbyArt.BackglassFor(lobby, index);
        results.Add(
            (
                before == ("vpx:" + vpx, "https://example.com/bg.jpg")
                    && UserArtwork.PathOf(playfield!)
                        == Path.Combine(folder, "artwork", "playfield.png")
                    && UserArtwork.PathOf(backglass!)
                        == Path.Combine(folder, "artwork", "backglass.png")
                    && UserArtwork.Find(t, UserArtwork.Kind.Card) == null,
                "my artwork: the VR lobby uses my playfield and backglass over the table file's and the catalog's"
            )
        );

        var jpg = Path.Combine(root, "other.jpg");
        File.WriteAllBytes(jpg, [0xFF, 0xD8, 0xFF, 1]);
        var oldKey = playfield;
        UserArtwork.Set(t, UserArtwork.Kind.Playfield, jpg);
        var files = Directory.GetFiles(UserArtwork.FolderFor(t), "playfield.*");
        results.Add(
            (
                files.Length == 1
                    && files[0].EndsWith(".jpg")
                    && UserArtwork.Key(t, UserArtwork.Kind.Playfield) != oldKey,
                "my artwork: a new picture replaces the old one (whatever its type) and gets a new key, so it's loaded again"
            )
        );

        string? error = null;
        try
        {
            UserArtwork.Set(t, UserArtwork.Kind.Card, vpx);
        }
        catch (InvalidOperationException e)
        {
            error = e.Message;
        }
        results.Add(
            (
                error != null && UserArtwork.PathOf("https://example.com/x.jpg") == null,
                "my artwork: only pictures are taken; web addresses aren't local pictures"
            )
        );

        foreach (var k in Enum.GetValues<UserArtwork.Kind>())
            UserArtwork.Remove(t, k);
        results.Add(
            (
                (LobbyArt.PlayfieldSource(lobby), LobbyArt.BackglassFor(lobby, index)) == before,
                "my artwork: removing it goes back to the table's and the catalog's pictures"
            )
        );
        return results;
    }
}
