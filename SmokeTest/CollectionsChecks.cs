using VrTableManager.Lobby;
using VrTableManager.Services;

/// <summary>
/// Your collections of tables (TableCollections, collections.json shared with the VR lobby), and moving a table's
/// favourite and plays to its new file name when an update renames it (PlayStats.MoveTable).
/// </summary>
static class CollectionsChecks
{
    public static List<(bool, string)> Run(string root)
    {
        var results = new List<(bool, string)>();
        void Check(bool ok, string name) => results.Add((ok, name));

        var dir = Path.Combine(root, "collections");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "collections.json");
        var afm = Path.Combine(dir, "Attack from Mars", "AFM v1.0.vpx");
        var mm = Path.Combine(dir, "Medieval Madness", "MM.vpx");

        Check(
            TableCollections.Load(path).Collections.Count == 0,
            "collections: none to start with"
        );
        Check(
            TableCollections.CleanName("  Party   night ") == "Party night"
                && TableCollections.CleanName("   ") == null
                && TableCollections.CleanName(new string('x', 60))!.Length
                    == TableCollections.MaxNameLength,
            "collections: names trimmed, spaces squeezed, at most 40 letters, never blank"
        );

        string? made = null;
        var c = TableCollections.Mutate(
            x =>
            {
                made = x.Create(" Party ");
                x.Set("party", afm, true);
                x.Set("Party", mm, true);
                x.Set("Party", afm, true); // twice: still once
                x.Create("Practice");
                x.Set("Practice", mm, true);
            },
            path
        );
        Check(made == "Party", "collections: created under its cleaned name");
        Check(
            c.Find("PARTY")!.Tables.Count == 2 && c.Contains("Party", afm.ToUpperInvariant()),
            "collections: names and table paths match whatever the case; a table is in once"
        );
        Check(
            c.NamesOf(mm).SequenceEqual(["Party", "Practice"]),
            "collections: a table's collections, in list order"
        );
        Check(
            TableCollections.Load(path).Find("Practice")?.Contains(mm) == true
                && !File.ReadAllText(path).Contains("\"Names\""),
            "collections: saved and read back (only the collections themselves are written)"
        );

        Check(
            c.Create("party") == null
                && c.Create("Favourites", LobbyNav.Filters) == null
                && c.Create("") == null,
            "collections: no duplicates, no blank names, none named like a lobby filter"
        );
        Check(
            c.Rename("Party", "Party Night")
                && !c.Rename("Practice", "party night")
                && !c.Rename("Practice", "Decade", LobbyNav.Filters)
                && c.Rename("Practice", "practice")
                && c.Find("practice")!.Name == "practice",
            "collections: rename, but not onto another's name or a lobby filter; changing only capitals is fine"
        );
        // Those renames were on a copy in memory: save them.
        TableCollections.Mutate(
            x =>
            {
                x.Rename("Party", "Party Night");
                x.Rename("Practice", "practice");
            },
            path
        );

        c = TableCollections.Mutate(x => x.Set("Party Night", afm, false), path);
        Check(
            !c.Contains("Party Night", afm) && c.Contains("Party Night", mm),
            "collections: take a table out"
        );

        var afm2 = Path.Combine(dir, "Attack from Mars", "AFM v1.1.vpx");
        c = TableCollections.Mutate(
            x =>
            {
                x.Set("Party Night", afm, true);
                x.MoveTable(afm, afm2);
            },
            path
        );
        Check(
            c.Contains("Party Night", afm2) && !c.Contains("Party Night", afm),
            "collections: a table moved to a new file name stays in its collections"
        );

        c = TableCollections.Mutate(x => x.Delete("practice"), path);
        Check(c.Find("practice") == null && c.Collections.Count == 1, "collections: delete one");

        File.WriteAllText(path, "{ not json");
        Check(
            TableCollections.Load(path).Collections.Count == 0,
            "collections: a broken file reads as none"
        );
        File.WriteAllText(
            path,
            """{"Collections":[{"Name":" A ","Tables":["x.vpx","X.VPX",""]},{"Name":"a","Tables":[]},{"Name":"","Tables":[]}]}"""
        );
        var tidy = TableCollections.Load(path);
        Check(
            tidy.Collections.Count == 1
                && tidy.Collections[0].Name == "A"
                && tidy.Collections[0].Tables.Count == 1,
            "collections: a hand-edited file is tidied (repeats and blanks dropped)"
        );

        // ---- Play stats follow a renamed table ----
        var stats = new PlayStats();
        stats.SetFavourite(afm, true);
        stats.RecordPlay(afm, new DateTime(2026, 1, 2), TimeSpan.FromMinutes(10));
        Check(
            stats.MoveTable(afm, afm2)
                && stats.IsFavourite(afm2)
                && stats.PlaysOf(afm2) == 1
                && stats.PlaysOf(afm) == 0,
            "stats: favourite and plays move to the new file name"
        );
        stats.RecordPlay(afm, new DateTime(2026, 3, 4), TimeSpan.FromMinutes(5));
        Check(
            stats.MoveTable(afm, afm2)
                && stats.PlaysOf(afm2) == 2
                && Math.Abs(stats.MinutesOf(afm2) - 15) < 0.01
                && stats.LastPlayedOf(afm2) == new DateTime(2026, 3, 4)
                && stats.IsFavourite(afm2),
            "stats: moving onto a path that has stats adds them together"
        );
        Check(
            !stats.MoveTable(mm, afm2) && !stats.MoveTable(afm2, afm2),
            "stats: nothing to move, nothing moved"
        );

        // ---- From the review ----
        var longA = new string('L', 45) + " one";
        var longB = new string('L', 45) + " two";
        File.WriteAllText(
            path,
            $$"""{"Collections":[{"Name":"{{longA}}","Tables":["a.vpx"]},{"Name":"{{longB}}","Tables":["b.vpx"]}]}"""
        );
        var merged = TableCollections.Load(path);
        Check(
            merged.Collections.Count == 1 && merged.Collections[0].Tables.Count == 2,
            "collections: two names that only differ past 40 letters become one, keeping both tables"
        );

        c = TableCollections.Mutate(
            x =>
            {
                x.Create("Case");
                x.Set("Case", afm2, true);
                x.MoveTable(afm2, afm2.ToUpperInvariant());
            },
            path
        );
        Check(
            c.Contains("Case", afm2),
            "collections: a move where only the capitals differ keeps the table"
        );

        var quiet = Path.Combine(root, "collections-quiet");
        Directory.CreateDirectory(quiet);
        TableInstaller.KeepStatsAndCollections(afm, afm2, Path.Combine(quiet, "settings.json"));
        Check(
            !File.Exists(Path.Combine(quiet, "play-stats.json"))
                && !File.Exists(Path.Combine(quiet, "collections.json")),
            "update with nothing to move writes no stats or collections files"
        );

        // A hand-edited collection named like a lobby filter never takes the filter over.
        var lobbyDir = Path.Combine(root, "collections-lobby");
        Directory.CreateDirectory(lobbyDir);
        File.WriteAllText(Path.Combine(lobbyDir, "lobby-state.json"), """{"Filter":"decade"}""");
        File.WriteAllText(
            Path.Combine(lobbyDir, "collections.json"),
            """{"Collections":[{"Name":"decade","Tables":["x.vpx"]},{"Name":"Mine","Tables":[]}]}"""
        );
        var ls = LobbyState.Load(Path.Combine(lobbyDir, "lobby-state.json"));
        Check(
            ls.Filter == "Decade"
                && !ls.IsCollection("decade")
                && LobbyNav
                    .FiltersFor(ls)
                    .Count(f => f.Equals("decade", StringComparison.OrdinalIgnoreCase)) == 1
                && ls.IsCollection("mine"),
            "lobby: a built-in filter wins over a collection with the same name, whatever the capitals"
        );
        return results;
    }
}
