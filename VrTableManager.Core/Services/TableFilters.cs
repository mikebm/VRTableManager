namespace VrTableManager.Services;

/// <summary>What the maker, era, type and theme filters look at, for a catalog machine or an installed table.</summary>
public sealed record TableFacts(
    string? Manufacturer,
    int? Year,
    string? Type,
    IReadOnlyList<string>? Themes
);

/// <summary>
/// The maker / era / type / theme filters shared by Get Tables and My Tables. Each "Any …" choice lets everything
/// through; a table missing what a filter asks about (no year, say) doesn't pass that filter.
/// </summary>
public static class TableFilters
{
    public const string AnyMaker = "Any maker",
        AnyEra = "Any era",
        AnyType = "Any type",
        AnyTheme = "Any theme";

    public static readonly string[] Eras =
    [
        AnyEra,
        "1930s–50s",
        "1960s",
        "1970s",
        "1980s",
        "1990s",
        "2000s",
        "2010s",
        "2020s",
    ];

    public static readonly string[] Types =
    [
        AnyType,
        "Solid state",
        "Electro-mechanical",
        "Pure mechanical",
        "Originals",
    ];

    /// <summary>
    /// An installed table's facts: maker and year from its own record (else the catalog's), type and themes from its
    /// catalog entry (<paramref name="game"/>, null when it isn't linked to one).
    /// </summary>
    public static TableFacts FactsOf(Models.TableManifest m, Models.VpsGame? game) =>
        new(
            string.IsNullOrWhiteSpace(m.Manufacturer) ? game?.Manufacturer : m.Manufacturer,
            m.Year ?? game?.Year,
            game?.Type,
            game?.Theme
        );

    public static bool Matches(
        TableFacts f,
        string? maker,
        string? era,
        string? type,
        string? theme
    ) =>
        MatchesMaker(f, maker)
        && MatchesEra(f, era)
        && MatchesType(f, type)
        && MatchesTheme(f, theme);

    static bool MatchesMaker(TableFacts f, string? maker) =>
        maker is null
        || maker == AnyMaker
        || string.Equals(f.Manufacturer?.Trim(), maker, StringComparison.OrdinalIgnoreCase);

    static bool MatchesEra(TableFacts f, string? era)
    {
        if (era is null || era == AnyEra)
        {
            return true;
        }

        if (f.Year is not > 0)
        {
            return false;
        }

        var decade = f.Year.Value / 10 * 10;
        return era == Eras[1] ? decade <= 1950 : era.StartsWith(decade.ToString());
    }

    static bool MatchesType(TableFacts f, string? type) =>
        type switch
        {
            "Solid state" => f.Type == "SS",
            "Electro-mechanical" => f.Type == "EM",
            "Pure mechanical" => f.Type == "PM",
            "Originals" => string.Equals(
                f.Manufacturer?.Trim(),
                "Original",
                StringComparison.OrdinalIgnoreCase
            ),
            _ => true,
        };

    static bool MatchesTheme(TableFacts f, string? theme) =>
        theme is null
        || theme == AnyTheme
        || (
            f.Themes?.Any(t => string.Equals(t.Trim(), theme, StringComparison.OrdinalIgnoreCase))
            ?? false
        );

    /// <summary>
    /// The choices for a maker or theme list: "Any …" first, then each value seen at least <paramref name="min"/>
    /// times, most common first (ties A–Z).
    /// </summary>
    public static List<string> Choices(string any, IEnumerable<string?> values, int min = 1) =>
        [
            any,
            .. values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .GroupBy(v => v!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= min)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key),
        ];
}
