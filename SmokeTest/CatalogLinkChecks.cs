using System.Diagnostics;
using System.Text.Json;
using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>Import tables: linking copied-in tables to their catalog game (picture, backglass, add-ons).</summary>
static class CatalogLinkChecks
{
    static VpsGame Game(string id, string name, string maker, int year, params string[] roms) =>
        new()
        {
            Id = id,
            Name = name,
            Manufacturer = maker,
            Year = year,
            ImgUrl = $"https://img/{id}.png",
            TableFiles = [new VpsTableFile { Id = id + "-t", TableFormat = "VPX" }],
            RomFiles = roms.Select(r => new VpsFile { Version = r }).ToList(),
        };

    public static List<(bool, string)> Run(string root, IReadOnlyList<VpsGame> catalog)
    {
        var afm = Game("afm", "Attack from Mars", "Bally", 1995, "afm_113b", "afm_113");
        var attack = Game("atk", "Attack", "Playmatic", 1980);
        var batDe = Game("bde", "Batman", "Data East", 1991, "btmn_106");
        var batSt = Game("bst", "Batman", "Stern", 2008, "bdk_294");
        var jp = Game("jp", "Jurassic Park", "Data East", 1993, "jupk_513");
        List<VpsGame> games = [afm, attack, batDe, batSt, jp];

        var results = new List<(bool, string)>();
        results.Add(
            (
                CatalogLink.Find(["afm_113b"], ["AFM_VPW.vpx", "AFM"], games) == null
                    && CatalogLink.Find(["afm_113b"], ["Attack From Mars VPW 2.0.vpx"], games)
                        == afm,
                "catalog link: a ROM match still needs the name to agree a little (no linking a retheme on a donor ROM)"
            )
        );
        results.Add(
            (
                CatalogLink.Find(["btmn_106"], ["Batman.vpx"], games) == batDe
                    && CatalogLink.Find([], ["Batman.vpx"], games) == null
                    && CatalogLink.Find([], ["Batman (Stern 2008).vpx"], games) == batSt,
                "catalog link: same-named games are told apart by ROM, or maker and year, else left unlinked"
            )
        );
        results.Add(
            (
                CatalogLink.Find(
                    [],
                    ["JurassicPark_1.2.vpx", "Jurassic Park (Data East 1993) 1.2"],
                    games
                ) == jp
                    && CatalogLink.Find([], ["My Original Table.vpx"], games) == null,
                "catalog link: a clear name match links without a ROM; an unknown name doesn't"
            )
        );

        // The pass over an imported table: links, writes vrtm.json, and only tries once.
        var folder = Path.Combine(root, "catalog-link", "Jurassic Park VPW");
        Directory.CreateDirectory(folder);
        var vpx = Path.Combine(folder, "Jurassic Park VPW.vpx");
        File.WriteAllText(vpx, "x");
        var m = new TableManifest
        {
            GameName = "Jurassic Park VPW",
            VpxFile = "Jurassic Park VPW.vpx",
            RomNames = ["jupk_513"],
        };
        File.WriteAllText(Path.Combine(folder, "vrtm.json"), JsonSerializer.Serialize(m));
        var t = new InstalledTable(m, vpx, folder);
        var first = CatalogLink.TryLink(t, games);
        TableInstaller.SaveManifest(t);
        var saved = JsonSerializer.Deserialize<TableManifest>(
            File.ReadAllText(Path.Combine(folder, "vrtm.json"))
        )!;
        results.Add(
            (
                first
                    && saved.GameId == "jp"
                    && saved.TableFileId == ""
                    && saved.ImgUrl == "https://img/jp.png"
                    && saved.Year == 1993
                    && saved.GameName == "Jurassic Park VPW"
                    && saved.CatalogLink == CatalogLink.Version
                    && !CatalogLink.TryLink(new InstalledTable(saved, vpx, folder), games),
                "catalog link: an imported table gets the game id and picture (no table file id), once"
            )
        );

        var unmatched = new TableManifest { GameName = "Nope", VpxFile = "Nope.vpx" };
        var hand = Path.Combine(root, "catalog-link", "Hand made");
        Directory.CreateDirectory(hand);
        results.Add(
            (
                CatalogLink.TryLink(
                    new InstalledTable(unmatched, Path.Combine(hand, "Nope.vpx"), hand),
                    games
                ) == false
                    && unmatched.CatalogLink == 0,
                "catalog link: a folder without its own vrtm.json is left alone (no manifest written into it)"
            )
        );

        // AUTO-LINK's version guess: the author and version in the names (or Table Info) must point to one table file.
        VpsTableFile Tf(string id, string version, params string[] authors) =>
            new()
            {
                Id = id,
                TableFormat = "VPX",
                Version = version,
                Authors = [.. authors],
                Urls = [new VpsUrl { Url = $"https://x/{id}" }],
                UpdatedAt = 5,
            };
        var mmGame = new VpsGame
        {
            Id = "mm",
            Name = "Medieval Madness",
            TableFiles =
            [
                Tf("mm-vpw", "2.0.1", "VPW", "Flupper"),
                Tf("mm-dv", "1.0.0", "DarthVito"),
                Tf("mm-old", "1.2", "Rothbauerw"),
                Tf("mm-mod", "1.2", "Rothbauerw", "Bigus1"),
            ],
        };
        TableFileGuess.Guess? G(string file, TableFileGuess.TableInfo? info = null) =>
            TableFileGuess.Find(mmGame, [file], info);
        results.Add(
            (
                G("Medieval Madness (Williams 1997) VPW v2.0.1.vpx")?.File.Id == "mm-vpw"
                    && G("MM VPW v.2.0.1.vpx")?.File.Id == "mm-vpw"
                    && G("Medieval Madness.vpx", new("MM", "VPW", "2.0.1"))?.File.Id == "mm-vpw"
                    && G("MM_Bigus1_1.2.vpx")?.File.Id == "mm-mod"
                    // A folder name keeps its last ".1" (only a .vpx name has an extension).
                    && TableFileGuess.Find(
                        mmGame,
                        ["Medieval Madness.vpx", "Medieval Madness VPW 2.0.1"],
                        null
                    )
                        is { Version: "2.0.1", File.Id: "mm-vpw" },
                "auto-link: a version in the name (v2.0.1, v.2.0.1), folder or Table Info picks the table file, the authors decide between same versions"
            )
        );
        var older = G("Medieval Madness VPW 1.9.vpx");
        results.Add(
            (
                older?.File.Id == "mm-vpw"
                    && older.Version == "1.9"
                    // The name's version beats a stale Table Info one.
                    && G("Medieval Madness 1.5.vpx", new(null, "VPW", "2.0.1"))
                        is { Version: "1.5", File.Id: "mm-vpw" }
                    && G("Medieval Madness VPW 2.5.vpx") == null,
                "auto-link: the author's older version links with its own version (an update shows); a newer one doesn't link"
            )
        );
        results.Add(
            (
                G("Medieval Madness (Williams 1997).vpx") == null
                    && G("Medieval Madness Rothbauerw.vpx") == null
                    && G("Medieval Madness 1.0.vpx") == null
                    && G("MM v.2.0.1.vpx") == null
                    && G("VR ROOM Medieval Madness VPW v01.vpx") == null
                    && G("Medieval Madness VPW 1.5 VR 1.1.vpx") == null,
                "auto-link: no version, two equal fits, a version with no author, a v01, or two versions: no guess"
            )
        );

        var afolder = Path.Combine(root, "catalog-link", "Medieval Madness VPW");
        Directory.CreateDirectory(afolder);
        var avpx = Path.Combine(afolder, "Medieval Madness VPW v2.0.1.vpx");
        File.WriteAllText(avpx, "not a compound file");
        var am = new TableManifest
        {
            GameName = "Medieval Madness VPW",
            VpxFile = Path.GetFileName(avpx),
            CatalogLink = CatalogLink.Version, // the automatic pass already tried it
        };
        File.WriteAllText(Path.Combine(afolder, "vrtm.json"), JsonSerializer.Serialize(am));
        var at = new InstalledTable(am, avpx, afolder);
        var auto = CatalogLink.AutoLink(at, [.. games, mmGame]);
        results.Add(
            (
                auto == CatalogLink.AutoLinkResult.Linked
                    && am.GameId == "mm"
                    && am.TableFileId == "mm-vpw"
                    && am.Version == "2.0.1"
                    && am.GameName == "Medieval Madness VPW"
                    && TableUpdates.Find([at], [mmGame]).Count == 0
                    && CatalogLink.AutoLink(at, [mmGame]) == CatalogLink.AutoLinkResult.Skipped,
                "auto-link: links game and version (no rename, no false update), and leaves a linked table alone"
            )
        );

        // A table folder of your own (no vrtm.json): linked only when the version is sure, the manifest marked as yours.
        var mine = Path.Combine(root, "catalog-link", "My MM");
        Directory.CreateDirectory(mine);
        var mineVpx = Path.Combine(mine, "Medieval Madness VPW v2.0.1.vpx");
        File.WriteAllText(mineVpx, "x");
        var mineTable = new InstalledTable(
            new TableManifest { GameName = "My MM", VpxFile = Path.GetFileName(mineVpx) },
            mineVpx,
            mine
        );
        var mineResult = CatalogLink.AutoLink(mineTable, [mmGame]);
        if (mineResult == CatalogLink.AutoLinkResult.Linked)
        {
            TableInstaller.SaveManifest(mineTable);
        }
        var unsure = Path.Combine(root, "catalog-link", "My other MM");
        Directory.CreateDirectory(unsure);
        var unsureVpx = Path.Combine(unsure, "Medieval Madness.vpx");
        File.WriteAllText(unsureVpx, "x");
        var unsureTable = new InstalledTable(
            new TableManifest { GameName = "My other MM", VpxFile = "Medieval Madness.vpx" },
            unsureVpx,
            unsure
        );
        results.Add(
            (
                mineResult == CatalogLink.AutoLinkResult.Linked
                    && mineTable.Manifest is { TableFileId: "mm-vpw", UserFolder: true }
                    && CatalogLink.AutoLink(unsureTable, [mmGame])
                        == CatalogLink.AutoLinkResult.NoGame
                    && unsureTable.Manifest.GameId == ""
                    && !File.Exists(Path.Combine(unsure, "vrtm.json")),
                "auto-link: a folder of your own is linked when sure (vrtm.json marked as yours), else left untouched"
            )
        );

        // Tables installed from the catalog know their real version: the guess should agree when it guesses.
        if (catalog.Count > 0)
        {
            var byId = catalog.ToDictionary(g => g.Id);
            int right = 0,
                wrong = 0,
                none = 0;
            foreach (
                var inst in TableInstaller
                    .Scan(new AppSettings())
                    .Where(x => x.IsVpx && x.Manifest.TableFileId != "")
            )
            {
                if (!byId.TryGetValue(inst.Manifest.GameId, out var g))
                {
                    continue;
                }
                var guess = TableFileGuess.Find(
                    g,
                    [Path.GetFileName(inst.TablePath), Path.GetFileName(inst.Folder)],
                    TableFileGuess.ReadInfo(inst.TablePath)
                );
                if (guess == null)
                {
                    none++;
                }
                else if (guess.File.Id == inst.Manifest.TableFileId)
                {
                    right++;
                }
                else
                {
                    wrong++;
                }
            }
            if (right + wrong == 0)
            {
                Console.WriteLine(
                    "SKIP  auto-link guesses vs installed tables (none from the catalog here)"
                );
            }
            else
            {
                Console.WriteLine(
                    $"      auto-link on installed tables: {right} right, {wrong} wrong, {none} not guessed"
                );
                results.Add(
                    (
                        wrong * 10 <= right + wrong,
                        "auto-link: on tables installed from the catalog, at most 1 guess in 10 is wrong"
                    )
                );
            }
        }

        // The real catalog: well-known names link to the right game, and a first pass is cheap.
        if (catalog.Count > 0)
        {
            string? Name(string[] roms, params string[] names) =>
                CatalogLink.Find(roms, names, catalog)?.Name;
            var afmReal = Name(["afm_113b"], "Attack From Mars (Bally 1995) VPW 2.0.vpx");
            var mm = Name(["mm_109c"], "Medieval Madness (Williams 1997)");
            var tz = Name([], "Twilight Zone (Bally 1993) VPW 2.1.vpx");
            Console.WriteLine($"      real catalog: afm→{afmReal}, mm→{mm}, tz→{tz}");
            results.Add(
                (
                    afmReal == "Attack from Mars"
                        && mm == "Medieval Madness"
                        && tz?.StartsWith("Twilight Zone") == true,
                    "catalog link: real catalog links well-known tables by ROM and by name"
                )
            );

            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 100; i++)
            {
                CatalogLink.Find(
                    ["xyz_" + i],
                    [$"Some Table {i} (Maker 1990) v1.{i}.vpx"],
                    catalog
                );
            }
            sw.Stop();
            Console.WriteLine(
                $"      real catalog: 100 tables tried in {sw.ElapsedMilliseconds} ms"
            );
            results.Add(
                (
                    sw.ElapsedMilliseconds < 5000,
                    "catalog link: a first pass over 100 tables is quick"
                )
            );
        }

        return results;
    }
}
