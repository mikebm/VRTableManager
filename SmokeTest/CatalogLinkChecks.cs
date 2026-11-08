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
