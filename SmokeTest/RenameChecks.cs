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

        // ⋯ → Fix table match…: a table added as Not in the catalog, linked to its catalog game and version.
        var twdFolder = Path.Combine(root, "relink", "The Walking Dead LE Premium 4K Stern 2014");
        Directory.CreateDirectory(twdFolder);
        var twdVpx = Path.Combine(twdFolder, "TWD LE 4K.vpx");
        File.WriteAllText(twdVpx, "x");
        var twdManifest = Path.Combine(twdFolder, "vrtm.json");
        var unmatched = new TableManifest
        {
            GameName = "The Walking Dead LE Premium 4K Stern 2014",
            VpxFile = "TWD LE 4K.vpx",
            RomNames = ["twd_160"],
        };
        File.WriteAllText(twdManifest, JsonSerializer.Serialize(unmatched));
        var twdTable = new InstalledTable(unmatched, twdVpx, twdFolder);
        var flupper = new VpsTableFile
        {
            Id = "twd-flupper",
            Version = "1.1.0",
            TableFormat = "VPX",
            Authors = ["Flupper"],
            UpdatedAt = 1234,
        };
        var le = new VpsGame
        {
            Id = "twdle",
            Name = "The Walking Dead (Limited Edition)",
            Manufacturer = "Stern",
            Year = 2014,
            ImgUrl = "https://example.invalid/twd.png",
            TableFiles = [flupper],
        };
        TableManifest SavedTwd() =>
            JsonSerializer.Deserialize<TableManifest>(File.ReadAllText(twdManifest))!;
        var relinked = TableInstaller.Relink(twdTable, le, flupper);
        var s = SavedTwd();
        results.Add(
            (
                relinked
                    && s.GameId == "twdle"
                    && s.TableFileId == "twd-flupper"
                    && s.SourceUpdatedAt == 1234
                    && s.GameName == "The Walking Dead (Limited Edition)"
                    && s.OriginalName == null
                    && s.Year == 2014
                    && s.ImgUrl == le.ImgUrl
                    && s.RomNames.SequenceEqual(["twd_160"])
                    && s.CatalogLink >= CatalogLink.Version
                    && !TableInstaller.Relink(twdTable, le, flupper)
                    && TableUpdates.Find([twdTable], [le]).Count == 0,
                "fix table match: a table not in the catalog is linked to the game and version picked, takes its name, keeps its ROM"
            )
        );
        TableInstaller.Rename(twdTable, "TWD");
        var newer = new VpsTableFile
        {
            Id = "twd-vpw",
            Urls = [new VpsUrl { Url = "https://example.invalid/twd" }],
            Version = "1.6",
            TableFormat = "VPX",
            UpdatedAt = 99,
        };
        le.TableFiles = [flupper, newer];
        TableInstaller.Relink(twdTable, le, newer);
        var upToDate = TableUpdates.Find([twdTable], [le]).Count == 0;
        newer.Version = "1.7"; // the author releases a new version of the one picked
        s = SavedTwd();
        results.Add(
            (
                s.GameName == "TWD"
                    && s.OriginalName == "The Walking Dead (Limited Edition)"
                    && s.TableFileId == "twd-vpw"
                    && upToDate
                    && TableUpdates.Find([twdTable], [le]).Count == 1,
                "fix table match: a name you gave the table stays, and updates are checked against the version picked"
            )
        );

        // Fix table match and ROMs: a script that doesn't name its ROM takes the new game's catalog list.
        var romFolder = Path.Combine(root, "relink", "Unreadable ROM");
        Directory.CreateDirectory(romFolder);
        var romVpx = Path.Combine(romFolder, "Mars.vpx");
        File.WriteAllText(romVpx, "x");
        var romManifest = Path.Combine(romFolder, "vrtm.json");
        var wrong = new TableManifest
        {
            GameId = "wrong",
            TableFileId = "wrong1",
            GameName = "Attack",
            VpxFile = "Mars.vpx",
            RomNames = ["atk_l1"], // the wrongly matched game's catalog ROM
        };
        File.WriteAllText(romManifest, JsonSerializer.Serialize(wrong));
        var romTable = new InstalledTable(wrong, romVpx, romFolder);
        var afmFile = new VpsTableFile
        {
            Id = "afm1",
            Version = "2.0",
            TableFormat = "VPX",
        };
        var afmGame = new VpsGame
        {
            Id = "afm",
            Name = "Attack from Mars",
            TableFiles = [afmFile],
            RomFiles =
            [
                new VpsFile { Version = "afm_113b" },
                new VpsFile { Version = "afm_113" },
                new VpsFile { Version = "AFM_FW.bin" },
            ],
        };
        TableInstaller.Relink(romTable, afmGame, afmFile, new VpxRomInfo(null, true));
        var fromCatalog = JsonSerializer
            .Deserialize<TableManifest>(File.ReadAllText(romManifest))!
            .RomNames.SequenceEqual(["afm_113b", "afm_113"]);

        // The ROM the script names stays; a ROM set up as an alias stays; no ROM needed clears them.
        var named = new TableManifest
        {
            GameName = "A",
            VpxFile = "Mars.vpx",
            RomNames = ["afm_113b"],
        };
        File.WriteAllText(romManifest, JsonSerializer.Serialize(named));
        var namedTable = new InstalledTable(named, romVpx, romFolder);
        TableInstaller.Relink(namedTable, afmGame, afmFile, new VpxRomInfo("afm_113b", true));
        var aliased = new TableManifest
        {
            GameName = "B",
            VpxFile = "Mars.vpx",
            RomNames = ["sw_101"],
            RomAlias = "sw_le",
        };
        File.WriteAllText(romManifest, JsonSerializer.Serialize(aliased));
        TableInstaller.Relink(
            new InstalledTable(aliased, romVpx, romFolder),
            afmGame,
            afmFile,
            new VpxRomInfo(null, true)
        );
        var original = new TableManifest
        {
            GameName = "C",
            VpxFile = "Mars.vpx",
            RomNames = ["x"],
        };
        File.WriteAllText(romManifest, JsonSerializer.Serialize(original));
        TableInstaller.Relink(
            new InstalledTable(original, romVpx, romFolder),
            afmGame,
            afmFile,
            new VpxRomInfo("mygame", false) // sets cGameName, never runs PinMAME
        );
        results.Add(
            (
                fromCatalog
                    && named.RomNames.SequenceEqual(["afm_113b"])
                    && aliased.RomNames.SequenceEqual(["sw_101"])
                    && aliased.RomAlias == "sw_le"
                    && original.RomNames.Count == 0,
                "fix table match: ROMs as installing works them out (script's ROM, else the new game's list; an alias stays)"
            )
        );

        return results;
    }
}
