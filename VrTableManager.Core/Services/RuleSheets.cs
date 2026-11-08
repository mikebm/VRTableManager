using System.Net;
using System.Text.RegularExpressions;
using VrTableManager.Models;

namespace VrTableManager.Services;

/// <summary>A page about how to play a table, opened in the browser.</summary>
public sealed record RuleLink(string Label, string Url);

/// <summary>
/// Where to read a table's rules: The Pinball Primer's rules-and-strategy page for the real machine
/// (pinballprimer.github.io, about a thousand games, matched by name and year), then the catalog's
/// instruction cards (ruleFiles, VPUniverse download pages).
/// </summary>
public static partial class RuleSheets
{
    const string BaseUrl = "https://pinballprimer.github.io/";
    const string ListUrl = BaseUrl + "gamelist.html";
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    static string CachePath => Path.Combine(AppSettings.DataDir, "pinballprimer-gamelist.html");

    /// <summary>"Addams Family, The (Bally Williams, DMD, 1992)" → its page.</summary>
    public sealed record PrimerEntry(string Name, string? Manufacturer, int? Year, string Url);

    /// <summary>The Pinball Primer's game list, refreshed weekly; the cached copy when offline, empty without one.</summary>
    public static async Task<List<PrimerEntry>> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var fresh =
                File.Exists(CachePath)
                && DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < MaxAge;
            if (!fresh)
            {
                try
                {
                    var html = await Http.Client.GetStringAsync(ListUrl, ct);
                    if (Parse(html).Count > 0)
                        await File.WriteAllTextAsync(CachePath, html, ct);
                }
                catch when (File.Exists(CachePath))
                { /* offline: the old list will do */
                }
            }
            return File.Exists(CachePath) ? Parse(await File.ReadAllTextAsync(CachePath, ct)) : [];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }
    }

    public static List<PrimerEntry> Parse(string html)
    {
        var result = new List<PrimerEntry>();
        foreach (Match m in EntryRegex().Matches(html))
        {
            var text = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
            var open = text.LastIndexOf(" (", StringComparison.Ordinal);
            if (open <= 0 || !text.EndsWith(')'))
                continue;
            var details = text[(open + 2)..^1].Split(',', StringSplitOptions.TrimEntries);
            int? year = int.TryParse(details[^1], out var y) ? y : null;
            result.Add(
                new PrimerEntry(
                    text[..open].Trim(),
                    details.Length > 1 ? details[0] : null,
                    year,
                    BaseUrl + m.Groups[1].Value
                )
            );
        }
        return result;
    }

    /// <summary>
    /// The Primer page for a machine: the same name (and year, when both know it), or, when both years are known
    /// and equal, one name holding the other ("Metallica (Premium Monsters)" is "Metallica"). A table author's
    /// name in front ("JP's Terminator 3") is tried without it. Manufacturers aren't compared: the Primer writes
    /// "Bally Williams" where the catalog has "Williams".
    /// </summary>
    public static string? PrimerUrl(string name, int? year, IReadOnlyList<PrimerEntry> entries) =>
        Match(name, year, entries)
        ?? (
            AuthorPrefixRegex().Match(name) is { Success: true } m
                ? Match(name[m.Length..], year, entries)
                : null
        );

    static string? Match(string name, int? year, IReadOnlyList<PrimerEntry> entries)
    {
        var key = Normalize(name);
        if (key.Length < 3)
            return null;
        return entries
            .Where(e => year is not > 0 || e.Year is not > 0 || e.Year == year)
            .Select(e => (e, k: Normalize(e.Name)))
            .Where(x =>
                x.k == key
                || (
                    year is > 0
                    && x.e.Year == year
                    && Math.Min(x.k.Length, key.Length) >= 4
                    && (x.k.Contains(key) || key.Contains(x.k))
                )
            )
            .OrderByDescending(x => x.k == key)
            .ThenByDescending(x => x.k.Length)
            .Select(x => x.e.Url)
            .FirstOrDefault();
    }

    /// <summary>"JP's " in front of a table name (the table author's version of a machine).</summary>
    [GeneratedRegex("^[A-Za-z]{1,12}'s +")]
    private static partial Regex AuthorPrefixRegex();

    /// <summary>The Primer page first, then the catalog's instruction cards (other languages last).</summary>
    public static List<RuleLink> LinksFor(
        string name,
        int? year,
        VpsGame? game,
        IReadOnlyList<PrimerEntry> entries
    )
    {
        var links = new List<RuleLink>();
        if (PrimerUrl(name, year, entries) is { } primer)
            links.Add(new("Rules and strategy (The Pinball Primer)", primer));
        foreach (
            var f in (game?.RuleFiles ?? [])
                .OrderBy(f => !string.IsNullOrWhiteSpace(f.Comment))
                .ThenByDescending(f => f.UpdatedAt ?? 0)
        )
            if (Addons.UsableUrl(f) is { } url)
                links.Add(
                    new(
                        "Instruction card"
                            + (
                                f.Authors is { Count: > 0 } a
                                    ? " by " + string.Join(", ", a.Take(2))
                                    : ""
                            )
                            + (
                                string.IsNullOrWhiteSpace(f.Comment) ? "" : $" ({f.Comment.Trim()})"
                            ),
                        url
                    )
                );
        return links;
    }

    /// <summary>"Getaway: High Speed II, The" and "The Getaway - High Speed II" both become "getawayhighspeedii".</summary>
    static string Normalize(string name)
    {
        var s = WebUtility.HtmlDecode(name).Trim().ToLowerInvariant();
        if (s.EndsWith(", the"))
            s = s[..^5];
        if (s.StartsWith("the "))
            s = s[4..];
        return new string(s.Where(char.IsLetterOrDigit).ToArray());
    }

    [GeneratedRegex("href=\"([a-z0-9_]+_[A-Za-z0-9]+\\.html)\">([^<]+)</a>")]
    private static partial Regex EntryRegex();
}
