using System.Text.Json;
using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>⋯ → Rename…: the new name is kept in vrtm.json, can go back, and isn't offered where there's no manifest.</summary>
static class RenameChecks
{
    public static List<(bool, string)> Run(string root)
    {
        var results = new List<(bool, string)>();

        var folder = Path.Combine(root, "rename", "Attack from Mars");
        Directory.CreateDirectory(folder);
        var vpx = Path.Combine(folder, "AFM.vpx");
        File.WriteAllText(vpx, "x");
        var m = new TableManifest
        {
            GameId = "afm",
            GameName = "Attack from Mars",
            VpxFile = "AFM.vpx",
        };
        var manifest = Path.Combine(folder, "vrtm.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(m));
        var t = new InstalledTable(m, vpx, folder);

        TableManifest Saved() =>
            JsonSerializer.Deserialize<TableManifest>(File.ReadAllText(manifest))!;

        var renamed = TableInstaller.CanRename(t) && TableInstaller.Rename(t, "  AFM   (my mod) ");
        var saved = Saved();
        results.Add(
            (
                renamed
                    && saved.GameName == "AFM (my mod)"
                    && saved.OriginalName == "Attack from Mars"
                    && saved.GameId == "afm",
                "rename: the new name (tidied) is saved in vrtm.json, with the old one, and the catalog link stays"
            )
        );

        var again = TableInstaller.Rename(t, "AFM again");
        results.Add(
            (
                again
                    && Saved().GameName == "AFM again"
                    && Saved().OriginalName == "Attack from Mars"
                    && !TableInstaller.Rename(t, "AFM again"),
                "rename: renaming again keeps the first name to go back to; the same name changes nothing"
            )
        );

        var back = TableInstaller.Rename(t, " ");
        results.Add(
            (
                back && Saved().GameName == "Attack from Mars" && Saved().OriginalName == null,
                "rename: a blank name goes back to the original"
            )
        );
        TableInstaller.Rename(t, "X");
        results.Add(
            (
                TableInstaller.Rename(t, "Attack from Mars") && Saved().OriginalName == null,
                "rename: typing the original name back counts as not renamed"
            )
        );

        TableInstaller.Rename(t, "Saucers");
        var rescanned = TableInstaller
            .Scan(new AppSettings { TablesDir = Path.Combine(root, "rename"), FpDir = "" })
            .FirstOrDefault(s => s.Folder == folder);
        results.Add(
            (
                rescanned?.Manifest.GameName == "Saucers" && rescanned.Manifest.GameId == "afm",
                "rename: a rescan shows the new name, still linked to the catalog"
            )
        );

        var renamedOld = new TableManifest
        {
            GameName = "Saucers",
            OriginalName = "Attack from Mars",
        };
        var plainOld = new TableManifest { GameName = "Attack from Mars" };
        results.Add(
            (
                TableInstaller.NameAfterInstall(renamedOld, "Attack From Mars")
                    == ("Saucers", "Attack From Mars")
                    && TableInstaller.NameAfterInstall(plainOld, "Attack From Mars")
                        == ("Attack From Mars", null)
                    && TableInstaller.NameAfterInstall(null, "Attack from Mars")
                        == ("Attack from Mars", null)
                    && TableInstaller.NameAfterInstall(renamedOld, "Saucers") == ("Saucers", null),
                "rename: the name rule for an update keeps your name (the catalog's latest is the one to go back "
                    + "to; a catalog name equal to yours counts as not renamed)"
            )
        );

        var stored = JsonSerializer.Serialize(renamedOld);
        var search = TableSearch.Parse("attack from mars");
        results.Add(
            (
                renamedOld.MatchName == "Attack from Mars"
                    && plainOld.MatchName == "Attack from Mars"
                    && WheelArt.Game.Of(renamedOld).Name == "Attack from Mars"
                    && !stored.Contains("MatchName")
                    && search != null
                    && search.Score(TableSearch.Index(renamedOld, null)) > 0
                    && TableSearch.Parse("saucers")!.Score(TableSearch.Index(renamedOld, null)) > 0,
                "rename: downloads, wheel packs and search still go by the game's own name (and search by yours)"
            )
        );

        var fpDir = Path.Combine(root, "rename-fp", "Tables");
        Directory.CreateDirectory(fpDir);
        var fpt = Path.Combine(fpDir, "Space Station.fpt");
        File.WriteAllText(fpt, "x");
        var fpSettings = new AppSettings
        {
            TablesDir = Path.Combine(root, "rename-fp", "none"),
            FpDir = Path.Combine(root, "rename-fp"),
        };
        var fp = TableInstaller.Scan(fpSettings).Single(s => s.TablePath == fpt);
        var fpRenamed = TableInstaller.CanRename(fp) && TableInstaller.Rename(fp, "Orbit");
        var fpAgain = TableInstaller.Scan(fpSettings).Single(s => s.TablePath == fpt);
        File.Delete(fpt);
        results.Add(
            (
                fpRenamed
                    && File.Exists(Path.Combine(fpDir, "Space Station.vrtm.json"))
                    && fpAgain.Manifest.GameName == "Orbit"
                    && fpAgain.Manifest.OriginalName == "Space Station"
                    && !TableInstaller.CanRename(fpAgain),
                "rename: a Future Pinball table keeps its name beside the .fpt; not once the .fpt is gone"
            )
        );

        var hand = Path.Combine(root, "rename", "Hand made");
        Directory.CreateDirectory(hand);
        var handVpx = Path.Combine(hand, "Mine.vpx");
        File.WriteAllText(handVpx, "x");
        var handTable = new InstalledTable(
            new TableManifest { GameName = "Hand made", VpxFile = "Mine.vpx" },
            handVpx,
            hand
        );
        var loose = new InstalledTable(
            new TableManifest { GameName = "Loose", VpxFile = "Loose.vpx" },
            Path.Combine(root, "rename", "Loose.vpx"),
            Path.Combine(root, "rename")
        )
        {
            SharedFolder = true,
        };
        var store = new InstalledTable(
            new TableManifest { Engine = TableManifest.PinballFx, GameName = "Pinball FX" },
            root,
            root
        );
        var refused = false;
        try
        {
            TableInstaller.Rename(handTable, "Nope");
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }
        results.Add(
            (
                !TableInstaller.CanRename(handTable)
                    && !TableInstaller.CanRename(loose)
                    && !TableInstaller.CanRename(store)
                    && refused
                    && !File.Exists(Path.Combine(hand, "vrtm.json")),
                "rename: not offered without a manifest of its own (no vrtm.json written into a hand-made folder)"
            )
        );

        return results;
    }
}
