using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>My Tables → ADD A TABLE FILE…: matching a downloaded file to the catalog by its name.</summary>
static class TableFileMatchChecks
{
    static VpsGame Game(
        string id,
        string name,
        string maker,
        int year,
        params (string version, string format, long updated)[] files
    ) =>
        new()
        {
            Id = id,
            Name = name,
            Manufacturer = maker,
            Year = year,
            TableFiles = files
                .Select(f => new VpsTableFile
                {
                    Id = id + f.version,
                    Version = f.version,
                    TableFormat = f.format,
                    UpdatedAt = f.updated,
                })
                .ToList(),
        };

    public static List<(bool, string)> Run()
    {
        var afm = Game(
            "afm",
            "Attack from Mars",
            "Bally",
            1995,
            ("1.3.1", "VPX", 30),
            ("2.0", "VPX", 20)
        );
        var attack = Game("atk", "Attack", "Playmatic", 1980, ("2.0", "VPX", 10));
        var v1 = Game("v1", "V.1", "IDSA", 1986, ("1.0", "VPX", 10));
        var elvira = Game(
            "elv",
            "Elvira and the Party Monsters",
            "Bally",
            1989,
            ("2.4", "VPX", 50),
            ("1.6.0", "VPX", 40)
        );
        var fpOnly = Game("fpo", "Space Station", "Williams", 1987, ("1.0", "FP", 10));
        List<VpsGame> games = [afm, attack, v1, elvira, fpOnly];

        var results = new List<(bool, string)>();
        results.Add(
            (
                TableFileMatch
                    .Guess("Attack From Mars (Bally 1995) VPW 2.0.zip", games)
                    .FirstOrDefault() == afm
                    && TableFileMatch.Guess("AttackFromMars_VPW_2.0.vpx", games).FirstOrDefault()
                        == afm,
                "add table file: the catalog game is guessed from the file name, also with the words run together"
            )
        );
        results.Add(
            (
                !TableFileMatch.Guess("Attack From Mars 1.0.zip", games).Contains(v1)
                    && TableFileMatch.Guess("random_stuff.zip", games).Count == 0,
                "add table file: a version number alone matches nothing, and an unrelated name gets no guess"
            )
        );
        results.Add(
            (
                TableFileMatch.PickTableFile(afm, "Attack From Mars VPW 2.0.zip")?.Version == "2.0"
                    && TableFileMatch.PickTableFile(afm, "Attack From Mars.zip")?.Version == "1.3.1"
                    && TableFileMatch.PickTableFile(elvira, "Elvira (Bally 1989) v1.6.zip")?.Version
                        == "1.6.0",
                "add table file: the version in the file name picks the catalog version (1.6 = 1.6.0), else the newest"
            )
        );
        results.Add(
            (
                TableFileMatch.Guess("Space Station.vpx", games).Count == 0
                    && TableFileMatch.Guess("Space Station.fpt", games).FirstOrDefault() == fpOnly,
                "add table file: a .vpx only matches games with a VPX table, a .fpt only Future Pinball ones"
            )
        );
        // The Walking Dead: an original of the same name (2016), the Pro and the LE (Stern 2014).
        var twdOriginal = Game("twdo", "The Walking Dead", "Original", 2016, ("1.0", "VPX", 90));
        var twdPro = Game("twdp", "The Walking Dead (Pro)", "Stern", 2014, ("1.0", "VPX", 10));
        var twdLe = Game(
            "twdle",
            "The Walking Dead (Limited Edition)",
            "Stern",
            2014,
            ("1.6", "VPX", 30),
            ("1.1.0", "VPX", 20),
            ("1.0.0", "VPX", 10)
        );
        List<VpsGame> twd = [twdOriginal, twdPro, twdLe, .. games];
        const string twdFile = "The Walking Dead LE-Premium 4K (Stern 2014).zip";
        results.Add(
            (
                TableFileMatch.Guess(twdFile, twd).FirstOrDefault() == twdLe,
                "add table file: \"LE\" in the file name is the Limited Edition, and another year counts against a game"
            )
        );
        // "Le" that is part of a name still matches, and a year that is part of a name isn't another year.
        var leMans = Game("lm", "Le Mans", "Williams", 1990, ("1.0", "VPX", 10));
        var year2000 = Game("y2k", "Party 2000", "Bally", 1999, ("1.0", "VPX", 10));
        results.Add(
            (
                TableFileMatch.Guess("Le Mans.vpx", [leMans]).FirstOrDefault() == leMans
                    && TableFileMatch.Score(leMans, "Le Mans.vpx") > 0.9
                    && TableFileMatch.Score(year2000, "Party 2000.vpx") > 0.9,
                "add table file: a name with \"Le\" or a year in it isn't hurt by the LE and other-year rules"
            )
        );
        var found = TableFileMatch.Search("walking dead", twdFile, twd);
        results.Add(
            (
                found.Count == 3
                    && found.Contains(twdLe)
                    && TableFileMatch.Search("afm", twdFile, twd).FirstOrDefault() == afm
                    && TableFileMatch.Search("", twdFile, twd).Count == 0
                    && TableFileMatch.Search("space station", twdFile, twd).Count == 0,
                "add table file: the catalog is searched as in Get Tables (initials too), only for games of the file's kind"
            )
        );
        results.Add(
            (
                !TableFileMatch.TableFileIsClear(twdLe, twdFile)
                    && TableFileMatch.TableFileIsClear(twdLe, "The Walking Dead LE 1.1.0.zip")
                    && TableFileMatch.TableFileIsClear(twdPro, twdFile)
                    && TableFileMatch.TableFiles(twdLe, twdFile).Count == 3,
                "add table file: the version is asked when the file name doesn't settle it"
            )
        );

        var (game, tf) = TableFileMatch.NotInCatalog(@"C:\Downloads\My_Cool.Table v1.zip");
        results.Add(
            (
                game.Id == ""
                    && game.Name == "My Cool Table v1"
                    && VpsCatalog.IsVpx(tf)
                    && game.TableFiles?.Single() == tf,
                "add table file: a table that isn't in the catalog is installed under its file name"
            )
        );
        return results;
    }
}
