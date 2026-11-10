using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>
/// The search boxes in Get Tables and My Tables (TableSearch) and the maker / era / type / theme filters they share
/// (TableFilters): typos, initials, words in any order, and the best match first, against the real catalog.
/// </summary>
static class TableSearchChecks
{
    public static List<(bool, string)> Run(List<VpsGame> games)
    {
        var results = new List<(bool, string)>();
        void Check(bool ok, string name) => results.Add((ok, name));

        // ---- Offline: words, typos, initials ----
        Check(
            TableSearch
                .Words("Bram Stoker's Dracula")
                .SequenceEqual(["bram", "stokers", "dracula"]),
            "search words: apostrophes dropped"
        );
        Check(
            TableSearch
                .Words("Spider-Man & Friends")
                .SequenceEqual(["spider", "man", "and", "friends"]),
            "search words: punctuation splits, & is and"
        );
        Check(
            TableSearch.Words("Pokémon").SequenceEqual(["pokemon"]),
            "search words: accents dropped"
        );
        Check(TableSearch.Parse("   ") == null, "search: blank is no search");
        Check(
            TableSearch.Distance("famly", "family", 1) == 1,
            "search: one letter missing is one edit"
        );
        Check(
            TableSearch.Distance("addmas", "addams", 2) == 1,
            "search: two letters swapped is one edit"
        );
        Check(
            TableSearch.Distance("abcdef", "uvwxyz", 2) == 3,
            "search: over the limit stops at limit + 1"
        );

        var afm = TableSearch.Index(
            new SearchFields("Attack from Mars", "Bally", 1995, ["VPW"], ["Outer Space"])
        );
        var taf = TableSearch.Index(
            new SearchFields("The Addams Family", "Bally", 1992, null, ["Movie"])
        );
        var tz = TableSearch.Index(
            new SearchFields("Twilight Zone", "Bally", 1993, null, ["TV Show"])
        );
        var dh = TableSearch.Index(new SearchFields("Die Hard Trilogy", "Original", 2023));
        double S(string q, SearchEntry e) => TableSearch.Parse(q)!.Score(e);

        Check(
            S("afm", afm) > 0 && S("afm", taf) == 0,
            "search: initials find Attack from Mars, only it"
        );
        Check(S("taf", taf) > 0 && S("af", taf) > 0, "search: initials with or without The");
        Check(S("tz", tz) > 0, "search: tz finds Twilight Zone");
        Check(S("adams famly", taf) > 0, "search: typos still find The Addams Family");
        Check(S("twilite", tz) > 0, "search: a typo while still typing finds Twilight");
        Check(S("mars attack", afm) > 0, "search: words in any order");
        Check(
            S("bally 1995", afm) > 0 && S("bally 1995", taf) == 0,
            "search: maker and year together"
        );
        Check(S("90s space", afm) > 0 && S("80s space", afm) == 0, "search: decade and theme");
        Check(S("1990s", taf) > 0, "search: 1990s is the decade");
        Check(S("mars xyzzy", afm) == 0, "search: every word has to match");
        Check(
            S("de hard", dh) > 0 && S("die hrd", dh) > 0,
            "search: a typo in a short word, judged on the whole phrase"
        );
        Check(S("dehard", dh) > 0, "search: the phrase typo with the spaces left out");
        Check(
            S("de hard", afm) == 0 && S("xe hard", dh) == 0,
            "search: the phrase typo needs the first letter right"
        );
        Check(
            TableSearch.PrefixDistance("de hard", "die hard trilogy", 1) == 1,
            "search: prefix distance ignores the rest of the name"
        );
        Check(S("mar", afm) > 0, "search: the start of a word");
        Check(S("ttack", afm) > 0, "search: inside a word (3 letters or more)");
        Check(S("xttack", afm) == 0, "search: a typo needs the first letter right");
        Check(S("the", taf) > 0, "search: \"the\" alone still searches");
        Check(
            S("attack from mars", afm) > S("attack", afm) && S("attack", afm) > S("atack", afm),
            "search: the whole name beats part of it, which beats a typo"
        );
        Check(
            S("bally", afm) < S("attack", afm),
            "search: the name counts for more than the maker"
        );

        // From the review: short decades, whole words over name prefixes, no typo junk from themes and people.
        var y2020 = TableSearch.Index(new SearchFields("Some Original", "Original", 2023));
        var y2012 = TableSearch.Index(new SearchFields("AC/DC", "Stern", 2012));
        var y2004 = TableSearch.Index(new SearchFields("Elvis", "Stern", 2004));
        Check(
            S("20s", y2020) > 0 && S("10s", y2012) > 0 && S("00s", y2004) > 0 && S("90s", taf) > 0,
            "search: 20s, 10s and 00s are this century; 90s is still the 1990s"
        );
        Check(
            S("20s", taf) == 0 && S("90s", y2020) == 0,
            "search: short decades don't cross centuries"
        );
        var marsupilami = TableSearch.Index(new SearchFields("Marsupilami", "Original", 2022));
        Check(
            S("mars", afm) > S("mars", marsupilami),
            "search: a whole word of the name beats the name merely starting with it"
        );
        var kids = TableSearch.Index(
            new SearchFields("Charlie Brown", "Original", 2023, ["Kiss Author"], ["Kids"])
        );
        Check(S("kiss", kids) > 0, "search: themes and people still match exactly");
        var kidsOnly = TableSearch.Index(
            new SearchFields("Charlie Brown", "Original", 2023, null, ["Kids"], ["Kidd"])
        );
        Check(
            S("kiss", kidsOnly) == 0,
            "search: no typos against themes or people (kiss is not Kids)"
        );
        Check(S("balyy", afm) > 0, "search: typos still count in the maker");
        Check(
            TableSearch.Distance("medieval madness", "medevil madnes", 3) == 3
                && TableSearch.Distance("medieval madness", "medevil madnes", 2) == 3,
            "search: edit distance on long inputs, and its early stop"
        );
        Check(
            TableSearch.PrefixDistance("medevil mad", "medieval madness and more", 2) == 2
                && TableSearch.PrefixDistance("xedevil mad", "medieval madness and more", 2) == 3,
            "search: prefix distance on long inputs, cut to the length that can match"
        );

        // ---- Filters ----
        var facts = new TableFacts("Williams", 1992, "SS", ["Movie", "Fantasy"]);
        Check(
            TableFilters.Matches(facts, "williams", "1990s", "Solid state", "movie"),
            "filters: all four match"
        );
        Check(!TableFilters.Matches(facts, null, "1980s", null, null), "filters: wrong era");
        Check(
            !TableFilters.Matches(facts, null, null, "Electro-mechanical", null),
            "filters: wrong type"
        );
        Check(!TableFilters.Matches(facts, null, null, null, "Horror"), "filters: wrong theme");
        Check(
            TableFilters.Matches(
                facts,
                TableFilters.AnyMaker,
                TableFilters.AnyEra,
                TableFilters.AnyType,
                TableFilters.AnyTheme
            ),
            "filters: Any lets everything through"
        );
        Check(
            !TableFilters.Matches(
                new TableFacts("Gottlieb", null, null, null),
                null,
                "1970s",
                null,
                null
            )
                && TableFilters.Matches(
                    new TableFacts("Gottlieb", 1948, "EM", null),
                    null,
                    "1930s–50s",
                    null,
                    null
                ),
            "filters: no year fails an era; 1930s–50s takes the 40s"
        );
        Check(
            !TableFilters.Matches(
                new TableFacts("Bally", 1990, null, null),
                null,
                null,
                null,
                "Movie"
            ),
            "filters: a table with no themes (not linked) fails a theme filter"
        );
        var choices = TableFilters.Choices(
            TableFilters.AnyMaker,
            ["Bally", "Williams", "bally ", null, "", "Stern"]
        );
        Check(
            choices.SequenceEqual([TableFilters.AnyMaker, "Bally", "Stern", "Williams"]),
            "filters: choices are Any, then most common, ties A–Z, case and spaces merged"
        );

        // ---- Against the real catalog ----
        if (games.Count < 500)
        {
            return results;
        }

        var index = games.ToDictionary(
            g => g,
            g =>
                TableSearch.Index(
                    new SearchFields(
                        g.Name,
                        g.Manufacturer,
                        g.Year,
                        g.TableFiles?.SelectMany(t => t.Authors ?? []),
                        g.Theme,
                        g.Designers
                    )
                )
        );
        List<VpsGame> Find(string q)
        {
            var s = TableSearch.Parse(q)!;
            return
            [
                .. index
                    .Select(kv => (kv.Key, Score: s.Score(kv.Value)))
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .Select(x => x.Key),
            ];
        }
        bool TopIs(string q, string name) =>
            Find(q).Take(3).Any(g => g.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

        Check(
            TopIs("afm", "Attack from Mars"),
            "catalog search: afm → Attack from Mars in the top 3"
        );
        Check(
            TopIs("adams famly", "Addams Family"),
            "catalog search: adams famly → The Addams Family"
        );
        Check(
            TopIs("twilite zone", "Twilight Zone"),
            "catalog search: twilite zone → Twilight Zone"
        );
        Check(
            TopIs("medival madnes", "Medieval Madness"),
            "catalog search: medival madnes → Medieval Madness"
        );
        Check(TopIs("spiderman", "Spider-Man"), "catalog search: spiderman → Spider-Man");
        Check(TopIs("lotr", "Lord of the Rings"), "catalog search: lotr → The Lord of the Rings");
        Check(
            Find("de hard").FirstOrDefault()?.Name.StartsWith("Die Hard") == true,
            "catalog search: de hard → Die Hard first"
        );
        var kiss = Find("kiss");
        Check(
            kiss.Count < 20
                && kiss.Take(2)
                    .All(g => g.Name.Contains("KISS", StringComparison.OrdinalIgnoreCase)),
            $"catalog search: kiss → KISS first, no typo junk ({kiss.Count} found)"
        );
        var mars = Find("mars");
        var afmAt = mars.FindIndex(g => g.Name == "Attack from Mars");
        var marsupilamiAt = mars.FindIndex(g => g.Name == "Marsupilami");
        Check(
            afmAt >= 0 && (marsupilamiAt < 0 || afmAt < marsupilamiAt),
            "catalog search: mars puts Attack from Mars above Marsupilami"
        );
        Check(Find("20s").Count > 100, "catalog search: 20s finds this decade's tables");
        Check(
            Find("lord of the rings").First().Name.StartsWith("The Lord of the Rings"),
            "catalog search: the exact name comes first, \"the\" or not"
        );
        var timed = System.Diagnostics.Stopwatch.StartNew();
        foreach (
            var q in new[] { "a", "at", "att", "atta", "attac", "attack", "attack f", "attack fr" }
        )
        {
            Find(q);
        }
        Console.WriteLine(
            $"catalog search: 8 keystrokes over {games.Count} machines in {timed.ElapsedMilliseconds} ms"
        );
        Check(
            timed.ElapsedMilliseconds < 2000,
            "catalog search: fast enough to search as you type"
        );
        Check(Find("zqxjvw").Count == 0, "catalog search: nonsense finds nothing");

        var themes = TableFilters.Choices(
            TableFilters.AnyTheme,
            games.SelectMany(g => g.Theme ?? []),
            20
        );
        Check(
            themes.Count > 10 && themes.Contains("Movie"),
            $"catalog themes: {themes.Count - 1} with 20+ machines"
        );
        return results;
    }
}
