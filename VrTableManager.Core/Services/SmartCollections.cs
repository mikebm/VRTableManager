using VrTableManager.Models;

namespace VrTableManager.Services;

/// <summary>
/// A smart collection you saved in My Tables: the search and filters that were showing, kept under a name. Its
/// tables are worked out each time, so it stays up to date. Empty or "Any …" parts don't narrow anything.
/// </summary>
public sealed class SmartRule
{
    public string Name { get; set; } = "";
    public string Search { get; set; } = "";

    /// <summary>"VPX", "Future Pinball", "Pinball FX" or "Pinball M" (the engine filter's own words).</summary>
    public string Engine { get; set; } = "";
    public string Maker { get; set; } = "";
    public string Era { get; set; } = "";
    public string Type { get; set; } = "";
    public string Theme { get; set; } = "";

    /// <summary>One of your own collections, by name.</summary>
    public string Collection { get; set; } = "";

    /// <summary>Only starred tables (saved while My Tables showed Favourites).</summary>
    public bool Favourites { get; set; }

    /// <summary>The search, read once rather than once per table.</summary>
    internal TableSearch? ParsedSearch
    {
        get
        {
            if (_parsedFor != Search)
            {
                _parsed = TableSearch.Parse(Search);
                _parsedFor = Search;
            }
            return _parsed;
        }
    }
    TableSearch? _parsed;
    string? _parsedFor;

    /// <summary>What it keeps, in words: "VPX · 1990s · Movie · “afm”".</summary>
    public string Describe() =>
        string.Join(
            " · ",
            new[]
            {
                Favourites ? "★ Favourites" : "",
                Engine,
                Maker,
                Era,
                Type,
                Theme,
                Collection == "" ? "" : $"in “{Collection}”",
                Search == "" ? "" : $"“{Search}”",
            }.Where(s =>
                !string.IsNullOrWhiteSpace(s) && !s.StartsWith("Any ", StringComparison.Ordinal)
            )
        );

    /// <summary>True when it doesn't narrow anything (nothing worth saving).</summary>
    public bool IsEmpty => Describe() == "";
}

/// <summary>What a smart collection looks at for one installed table.</summary>
public sealed record SmartTable(
    string Path,
    string Engine,
    TableFacts Facts,
    SearchEntry Search,
    bool Favourite,
    int Plays,
    DateTime? LastPlayed,
    DateTime InstalledAt,
    bool NeedsRom
)
{
    /// <summary>
    /// Built from the table, its catalog entry (null when not linked) and the shared play stats. Pass the
    /// <paramref name="search"/> entry when it's cached; otherwise it's made here.
    /// </summary>
    public static SmartTable From(
        InstalledTable t,
        VpsGame? game,
        PlayStats stats,
        SearchEntry? search = null
    ) =>
        new(
            t.TablePath,
            t.Manifest.Engine,
            TableFilters.FactsOf(t.Manifest, game),
            search ?? TableSearch.Index(t.Manifest, game),
            stats.IsFavourite(t.TablePath),
            stats.PlaysOf(t.TablePath),
            stats.LastPlayedOf(t.TablePath),
            t.Manifest.InstalledAt,
            t.MissingRoms.Count > 0
        );
}

/// <summary>
/// Smart collections: ready-made ones (<see cref="BuiltIn"/>) and the ones you save (<see cref="SmartRule"/>, kept in
/// collections.json). Shared by My Tables and the VR lobby, which both work them out from the same facts.
/// </summary>
public static class SmartCollections
{
    public sealed record Ready(
        string Name,
        string Description,
        Func<SmartTable, DateTime, bool> Test
    );

    public static readonly Ready[] BuiltIn =
    [
        new("Never played", "Tables you haven't played yet", (t, _) => t.Plays == 0),
        new(
            "New this month",
            "Installed in the last 30 days",
            (t, now) => t.InstalledAt > now.AddDays(-30)
        ),
        new(
            "Not played in 3 months",
            "Played before, but not in the last 90 days",
            (t, now) => t.LastPlayed is { } l && l < now.AddDays(-90)
        ),
        new(
            "Forgotten favourites",
            "Starred, but not played in the last 60 days",
            (t, now) => t.Favourite && (t.LastPlayed is not { } l || l < now.AddDays(-60))
        ),
        new(
            "Needs a ROM",
            "Tables that can't start until their ROM is added",
            (t, _) => t.NeedsRom
        ),
    ];

    public static IEnumerable<string> BuiltInNames => BuiltIn.Select(b => b.Name);

    public static Ready? FindBuiltIn(string? name) =>
        BuiltIn.FirstOrDefault(b =>
            string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>The engine filter's words for each engine.</summary>
    public static readonly Dictionary<string, string> EngineIds = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["VPX"] = TableManifest.Vpx,
        ["Future Pinball"] = TableManifest.FuturePinball,
        ["Pinball FX"] = TableManifest.PinballFx,
        ["Pinball M"] = TableManifest.PinballM,
    };

    /// <summary>Whether a table is in a saved smart collection.</summary>
    public static bool Matches(SmartRule r, SmartTable t, TableCollections collections)
    {
        if (EngineIds.TryGetValue(r.Engine, out var engine) && t.Engine != engine)
        {
            return false;
        }

        if (
            !TableFilters.Matches(
                t.Facts,
                Blank(r.Maker),
                Blank(r.Era),
                Blank(r.Type),
                Blank(r.Theme)
            )
        )
        {
            return false;
        }

        if (Blank(r.Collection) is { } c && !collections.Contains(c, t.Path))
        {
            return false;
        }

        if (r.Favourites && !t.Favourite)
        {
            return false;
        }

        return r.ParsedSearch is not { } s || s.Score(t.Search) > 0;
    }

    /// <summary>Whether a table is in the smart collection called <paramref name="name"/> (ready-made or saved).</summary>
    public static bool Matches(
        string name,
        SmartTable t,
        TableCollections collections,
        DateTime now
    ) =>
        FindBuiltIn(name) is { } b
            ? b.Test(t, now)
            : collections.FindSmart(name) is { } r && Matches(r, t, collections);

    public static bool Exists(string? name, TableCollections collections) =>
        FindBuiltIn(name) != null || collections.FindSmart(name) != null;

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
