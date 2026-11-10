using VrTableManager.Lobby;
using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>
/// Smart collections (SmartCollections): the ready-made ones, the ones you save from the search and filters
/// (SmartRule, kept in collections.json), and how their names live alongside your own collections.
/// </summary>
static class SmartChecks
{
    static SmartTable Table(
        string path,
        string title = "Attack from Mars",
        string engine = TableManifest.Vpx,
        string maker = "Bally",
        int year = 1995,
        string type = "SS",
        string[]? themes = null,
        bool favourite = false,
        int plays = 0,
        DateTime? lastPlayed = null,
        DateTime? installedAt = null,
        bool needsRom = false
    )
    {
        var facts = new TableFacts(maker, year, type, themes ?? ["Outer Space"]);
        return new SmartTable(
            path,
            engine,
            facts,
            TableSearch.Index(new SearchFields(title, maker, year, null, facts.Themes)),
            favourite,
            plays,
            lastPlayed,
            installedAt ?? new DateTime(2020, 1, 1),
            needsRom
        );
    }

    public static List<(bool, string)> Run(string root)
    {
        var results = new List<(bool, string)>();
        void Check(bool ok, string name) => results.Add((ok, name));

        var now = new DateTime(2026, 11, 10);
        bool In(string name, SmartTable t) => SmartCollections.FindBuiltIn(name)!.Test(t, now);

        // ---- Ready-made ----
        Check(
            In("Never played", Table("a")) && !In("Never played", Table("a", plays: 2)),
            "smart: never played"
        );
        Check(
            In("New this month", Table("a", installedAt: now.AddDays(-3)))
                && !In("New this month", Table("a", installedAt: now.AddDays(-45))),
            "smart: new this month (installed in the last 30 days)"
        );
        Check(
            In("Not played in 3 months", Table("a", plays: 4, lastPlayed: now.AddDays(-120)))
                && !In("Not played in 3 months", Table("a", plays: 4, lastPlayed: now.AddDays(-10)))
                && !In("Not played in 3 months", Table("a")),
            "smart: not played in 3 months (played before, not lately; never played isn't in it)"
        );
        Check(
            In("Forgotten favourites", Table("a", favourite: true, lastPlayed: now.AddDays(-90)))
                && In("Forgotten favourites", Table("a", favourite: true))
                && !In(
                    "Forgotten favourites",
                    Table("a", favourite: true, lastPlayed: now.AddDays(-5))
                )
                && !In("Forgotten favourites", Table("a", lastPlayed: now.AddDays(-90))),
            "smart: forgotten favourites (starred, not played in 60 days)"
        );
        Check(
            In("Needs a ROM", Table("a", needsRom: true)) && !In("Needs a ROM", Table("a")),
            "smart: needs a ROM"
        );

        // ---- Saved rules ----
        var col = new TableCollections();
        col.Create("Party");
        col.Set("Party", "a", true);
        bool Match(SmartRule r, SmartTable t) => SmartCollections.Matches(r, t, col);
        var afm = Table("a");
        var em = Table(
            "b",
            "Big Indian",
            maker: "Gottlieb",
            year: 1974,
            type: "EM",
            themes: ["American West"]
        );
        var fp = Table(
            "c",
            "Some FP Table",
            engine: TableManifest.FuturePinball,
            maker: "Original",
            year: 2010
        );
        Check(
            new SmartRule().IsEmpty && Match(new SmartRule(), em),
            "smart rule: an empty one narrows nothing"
        );
        Check(
            Match(new SmartRule { Era = "1990s" }, afm)
                && !Match(new SmartRule { Era = "1990s" }, em),
            "smart rule: era"
        );
        Check(
            Match(new SmartRule { Type = "Electro-mechanical", Theme = "american west" }, em)
                && !Match(new SmartRule { Type = "Electro-mechanical" }, afm),
            "smart rule: type and theme"
        );
        Check(
            Match(new SmartRule { Engine = "Future Pinball" }, fp)
                && !Match(new SmartRule { Engine = "Future Pinball" }, afm)
                && Match(new SmartRule { Engine = "All engines" }, afm),
            "smart rule: engine (and any other words mean all)"
        );
        Check(
            Match(new SmartRule { Collection = "party" }, afm)
                && !Match(new SmartRule { Collection = "Party" }, em),
            "smart rule: in one of your collections"
        );
        Check(
            Match(new SmartRule { Search = "atack mars" }, afm)
                && !Match(new SmartRule { Search = "indian" }, afm),
            "smart rule: the search, typos and all"
        );
        Check(
            new SmartRule
            {
                Era = "1990s",
                Theme = "Movie",
                Search = "afm",
                Maker = "Any maker",
            }.Describe() == "1990s · Movie · “afm”",
            "smart rule: described in words, leaving out the Any ones"
        );

        // ---- Names, saved in collections.json ----
        var dir = Path.Combine(root, "smart");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "collections.json");
        string? saved = null,
            clash = null,
            ready = null,
            mineClash = null;
        var c = TableCollections.Mutate(
            x =>
            {
                x.Create("Party");
                saved = x.SaveSmart(
                    new SmartRule
                    {
                        Name = " 90s in Party ",
                        Era = "1990s",
                        Collection = "Party",
                    }
                );
                clash = x.SaveSmart(new SmartRule { Name = "party", Era = "1980s" });
                ready = x.SaveSmart(new SmartRule { Name = "never played", Era = "1980s" });
                mineClash = x.Create("90S IN PARTY");
            },
            path
        );
        Check(
            saved == "90s in Party" && c.FindSmart("90s in party") != null,
            "smart: saved under its cleaned name"
        );
        Check(
            clash == null && ready == null && mineClash == null,
            "smart: names are unique across your collections, saved and ready-made smart ones"
        );
        c = TableCollections.Mutate(x => x.Rename("Party", "Party night"), path);
        Check(
            c.FindSmart("90s in Party")!.Collection == "Party night",
            "smart: renaming a collection keeps the smart ones that use it working"
        );
        c = TableCollections.Mutate(x => x.Rename("90s in Party", "Nineties"), path);
        Check(
            c.FindSmart("Nineties") != null && c.FindSmart("90s in Party") == null,
            "smart: rename a saved one"
        );
        var toReady = true;
        TableCollections.Mutate(x => toReady = x.Rename("Nineties", "Needs a ROM"), path);
        Check(
            !toReady && TableCollections.Load(path).FindSmart("Nineties") != null,
            "smart: can't be renamed to a ready-made one's name"
        );
        c = TableCollections.Mutate(x => x.Delete("nineties"), path);
        Check(
            c.Smart.Count == 0 && c.Find("Party night") != null,
            "smart: delete a saved one, collections untouched"
        );
        File.WriteAllText(
            path,
            """{"Collections":[{"Name":"Dup","Tables":[]}],"Smart":[{"Name":"dup","Era":"1990s"},{"Name":"Never Played"},{"Name":"Ok","Era":"1970s"},{"Name":"ok"}]}"""
        );
        var tidy = TableCollections.Load(path);
        Check(
            tidy.SmartNames.SequenceEqual(["dup (2)", "Never Played (2)", "Ok", "ok (2)"])
                && tidy.FindSmart("Ok")!.Search == "",
            "smart: a hand-edited file ends up with one per name, never a collection's or a ready-made one's"
        );
        Check(
            TableCollections.Load(path).Collections.Count == 1
                && !File.ReadAllText(path).Contains("SmartNames"),
            "smart: only the collections and rules themselves are written"
        );

        // ---- From the review ----
        var favRule = new SmartRule { Favourites = true, Era = "1990s" };
        Check(
            Match(favRule, Table("a", favourite: true))
                && !Match(favRule, afm)
                && favRule.Describe() == "★ Favourites · 1990s",
            "smart rule: kept while showing Favourites, it's favourites only"
        );
        File.WriteAllText(
            path,
            """{"Collections":[{"Name":"Mine","Tables":[]}],"Smart":[{"Name":"Never played","Era":"1990s"},{"Name":"mine"},{"Name":"Mine (2)"}]}"""
        );
        var renamedOnLoad = TableCollections.Load(path);
        var renamedNames = renamedOnLoad.SmartNames.ToList();
        Check(
            renamedNames.Count == 3
                && renamedNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3
                && renamedNames[0] == "Never played (2)"
                && !renamedNames.Any(n =>
                    n.Equals("Mine", StringComparison.OrdinalIgnoreCase)
                    || SmartCollections.FindBuiltIn(n) != null
                ),
            $"smart: a saved one whose name clashes is renamed, not dropped ({string.Join(", ", renamedOnLoad.SmartNames)})"
        );
        File.WriteAllText(path, """{"Collections":[{"Name":"never played","Tables":[]}]}""");
        var capsOnly = false;
        var old = TableCollections.Mutate(
            x => capsOnly = x.Rename("never played", "Never Played"),
            path
        );
        Check(
            capsOnly && old.Find("Never Played")!.Name == "Never Played",
            "an older collection named like a ready-made one can still change its capitals"
        );
        return results;
    }
}
