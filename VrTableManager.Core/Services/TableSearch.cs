using System.Globalization;
using System.Text;

namespace VrTableManager.Services;

/// <summary>What a table or catalog machine can be found by. Null or empty fields are skipped.</summary>
public sealed record SearchFields(
    string Title,
    string? Manufacturer = null,
    int? Year = null,
    IEnumerable<string>? Authors = null,
    IEnumerable<string>? Themes = null,
    IEnumerable<string>? Designers = null,
    IEnumerable<string>? Roms = null
);

/// <summary>
/// A table's search fields cleaned up once (<see cref="TableSearch.Index"/>), so each keystroke only compares words.
/// </summary>
public sealed class SearchEntry
{
    internal readonly List<(string[] Words, string Joined, double Weight, bool Typos)> Fields = [];
    internal string Title = "";
    internal HashSet<string> TitleWords = [];
    internal string TitleJoined = "";
    internal string[] Initials = [];
    internal int? Year;
}

/// <summary>
/// Forgiving search for the search boxes in Get Tables and My Tables. Every word typed has to match something,
/// in any order and in any field (name, maker, year, authors, themes, designers, ROM), and a word matches when
/// it's the same, the start of a word, inside a word, one or two letters off (typos, in the name and maker), or the initials of the
/// name ("afm" → Attack from Mars). Case, accents and punctuation don't count, "&" is "and", and "Spider-Man"
/// is found by "spiderman" too. <see cref="Score"/> is 0 for no match, higher for better matches.
/// </summary>
public sealed class TableSearch
{
    const double TitleWeight = 1.0,
        MakerWeight = 0.8,
        RomWeight = 0.7,
        PeopleWeight = 0.6,
        ThemeWeight = 0.6;

    readonly string[] _words;
    readonly string _joined;

    TableSearch(string[] words)
    {
        _words = words;
        _joined = string.Concat(words);
    }

    /// <summary>The search for what was typed, or null when there's nothing to search for.</summary>
    public static TableSearch? Parse(string? query)
    {
        var words = Words(query);
        // "the" says nothing ("the getaway" finds The Getaway either way), unless it's all that was typed.
        var useful = words.Where(w => w != "the").ToArray();
        if (useful.Length > 0)
        {
            words = useful;
        }
        return words.Length == 0 ? null : new TableSearch(words);
    }

    /// <summary>What an installed table is found by: its own record, plus its catalog entry's themes and designers.</summary>
    public static SearchEntry Index(Models.TableManifest m, Models.VpsGame? game)
    {
        var f = TableFilters.FactsOf(m, game);
        return Index(
            new SearchFields(
                // A renamed table is found by both names.
                m.OriginalName
                    is { } original
                    ? $"{m.GameName} {original}"
                    : m.GameName,
                f.Manufacturer,
                f.Year,
                m.Authors,
                f.Themes,
                game?.Designers,
                m.RomNames
            )
        );
    }

    public static SearchEntry Index(SearchFields f)
    {
        var e = new SearchEntry { Year = f.Year is > 0 ? f.Year : null };
        void Add(string? text, double weight)
        {
            var words = Words(text);
            if (words.Length > 0)
            {
                // Typos count only in the name and maker. Themes and people are too many short words:
                // "kiss" one letter off the theme "Kids" would bring in 140 machines.
                e.Fields.Add((words, string.Concat(words), weight, weight >= MakerWeight));
            }
        }

        var title = Words(f.Title);
        e.TitleWords = [.. title];
        // Without "the", as the search drops it too: "lord of the rings" is the whole name of The Lord of the Rings.
        var noThe = title.Where(w => w != "the").ToArray();
        e.Title = string.Join(' ', noThe.Length > 0 ? noThe : title);
        e.TitleJoined = string.Concat(title);
        Add(f.Title, TitleWeight);
        // Initials with and without "the" or all the small words: Attack from Mars → afm and am,
        // The Addams Family → taf and af, The Lord of the Rings → tlotr, lotr and lr.
        var initials = new HashSet<string>
        {
            string.Concat(title.Select(w => w[0])),
            string.Concat(
                (title.Length > 1 && title[0] == "the" ? title[1..] : title).Select(w => w[0])
            ),
            string.Concat(title.Where(w => !SmallWords.Contains(w)).Select(w => w[0])),
        };
        e.Initials = [.. initials.Where(i => i.Length >= 2)];
        Add(f.Manufacturer, MakerWeight);
        foreach (var a in f.Authors ?? [])
        {
            Add(a, PeopleWeight);
        }

        foreach (var d in f.Designers ?? [])
        {
            Add(d, PeopleWeight);
        }

        foreach (var t in f.Themes ?? [])
        {
            Add(t, ThemeWeight);
        }

        foreach (var r in f.Roms ?? [])
        {
            Add(r, RomWeight);
        }

        return e;
    }

    /// <summary>0 when <paramref name="e"/> doesn't match; otherwise how well (higher is better).</summary>
    public double Score(SearchEntry e)
    {
        // Initials: one short word that spells the name ("tz", "afm").
        if (
            _words.Length == 1
            && _words[0].Length is >= 2 and <= 5
            && e.Initials.Contains(_words[0])
        )
        {
            return 1.9;
        }

        var total = 0.0;
        foreach (var q in _words)
        {
            var best = WordScore(q, e);
            if (best == 0)
            {
                // "pin bot" for Pinbot: the words typed run together are part of the name.
                return _words.Length > 1 && _joined.Length >= 4 && e.TitleJoined.Contains(_joined)
                    ? 0.8 + Bonus(e)
                    : PhraseScore(e);
            }
            total += best;
        }
        return total / _words.Length + Bonus(e);
    }

    /// <summary>
    /// Typos in short words, judged on the whole phrase: "de hard" is one letter off "die hard", though "de" alone
    /// is too short to guess at. The phrase typed is compared with the name from the start of any of its words
    /// (the start of the name counts most), allowing 1 typo from 5 letters, 2 from 10 and 3 from 16.
    /// </summary>
    double PhraseScore(SearchEntry e)
    {
        var typed = string.Join(' ', _words);
        if (typed.Length < 5)
        {
            return 0;
        }

        var allowed =
            typed.Length >= 16 ? 3
            : typed.Length >= 10 ? 2
            : 1;
        var best = 0.0;
        for (var start = 0; start < e.Title.Length; start++)
        {
            if ((start > 0 && e.Title[start - 1] != ' ') || e.Title[start] != typed[0])
            {
                continue;
            }

            var d = PrefixDistance(typed, e.Title.AsSpan(start), allowed);
            if (d <= allowed)
            {
                var s = (start == 0 ? 0.8 : 0.6) - 0.1 * (d - 1);
                best = Math.Max(best, s);
            }
        }
        // "dehard" for Die Hard: the same, with the spaces left out of both.
        if (
            best < 0.8
            && _joined.Length >= 5
            && e.TitleJoined.Length > 0
            && e.TitleJoined[0] == _joined[0]
        )
        {
            var d = PrefixDistance(_joined, e.TitleJoined.AsSpan(), _joined.Length >= 10 ? 2 : 1);
            if (d <= (_joined.Length >= 10 ? 2 : 1))
            {
                best = Math.Max(best, 0.7 - 0.1 * (d - 1));
            }
        }
        return best;
    }

    /// <summary>The fewest edits that turn <paramref name="a"/> into the start of <paramref name="b"/> (or all of it); over max is max + 1.</summary>
    public static int PrefixDistance(string a, ReadOnlySpan<char> b, int max)
    {
        // Only the first a.Length + max letters of b can be part of the match.
        if (b.Length > a.Length + max)
        {
            b = b[..(a.Length + max)];
        }
        return Edits(a, b, max, prefix: true);
    }

    /// <summary>Edits (insert, delete, change, swap two letters) from a to b; anything over max is max + 1.</summary>
    public static int Distance(string a, string b, int max) =>
        Math.Abs(a.Length - b.Length) > max ? max + 1 : Edits(a, b, max, prefix: false);

    /// <summary>
    /// The edit table behind <see cref="Distance"/> and <see cref="PrefixDistance"/>, a row at a time (optimal
    /// string alignment: a swap of two neighbouring letters is one edit). It stops once a whole row is over
    /// <paramref name="max"/>, since later rows can't come back under it. With <paramref name="prefix"/>, the
    /// answer is the best match against any start of b; otherwise against all of it.
    /// </summary>
    static int Edits(string a, ReadOnlySpan<char> b, int max, bool prefix)
    {
        var prev2 = new int[b.Length + 1];
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    v = Math.Min(v, prev2[j - 2] + 1);
                }
                cur[j] = v;
                rowMin = Math.Min(rowMin, v);
            }
            if (rowMin > max)
            {
                return max + 1;
            }
            (prev2, prev, cur) = (prev, cur, prev2);
        }
        if (!prefix)
        {
            return Math.Min(prev[b.Length], max + 1);
        }

        var min = max + 1;
        foreach (var v in prev)
        {
            min = Math.Min(min, v);
        }
        return min;
    }

    /// <summary>
    /// The name being, or starting with, what was typed counts for most. Every word typed being a whole word of the
    /// name counts a little more than the name merely starting with it: "mars" puts Attack from Mars above Marsupilami.
    /// </summary>
    double Bonus(SearchEntry e)
    {
        var typed = string.Join(' ', _words);
        if (e.Title == typed)
        {
            return 1.0;
        }

        var wholeWords = _words.All(e.TitleWords.Contains) ? 0.45 : 0;
        return wholeWords + (e.Title.StartsWith(typed, StringComparison.Ordinal) ? 0.5 : 0);
    }

    static double WordScore(string q, SearchEntry e)
    {
        var best = YearScore(q, e.Year);
        foreach (var (words, joined, weight, typos) in e.Fields)
        {
            if (best >= weight)
            {
                continue; // can't do better in a lighter field
            }

            var s = 0.0;
            foreach (var w in words)
            {
                s = Math.Max(s, Match(q, w, typos));
                if (s == 1)
                {
                    break;
                }
            }
            // "spiderman" for Spider-Man: the word is inside the field with its spaces taken out.
            if (s < 0.7 && q.Length >= 4 && joined.Contains(q))
            {
                s = 0.7;
            }
            best = Math.Max(best, s * weight);
        }
        return best;
    }

    /// <summary>"1992", "1990s" or "90s" against the machine's year. Two-digit decades under 30 are this century ("20s" is the 2020s).</summary>
    static double YearScore(string q, int? year)
    {
        if (year is not { } y || q.Length < 3 || !char.IsAsciiDigit(q[0]))
        {
            return 0;
        }

        if (q.Length == 4 && int.TryParse(q, out var exact))
        {
            return exact == y ? 0.8 : 0;
        }

        if (q.EndsWith('s') && int.TryParse(q[..^1], out var dec))
        {
            var decade = y / 10 * 10;
            var typed =
                dec >= 1900 ? dec
                : dec < 30 ? 2000 + dec
                : 1900 + dec;
            return typed == decade ? 0.7 : 0;
        }
        return 0;
    }

    /// <summary>
    /// How well one typed word matches one word of a field: 1 the same, down to 0.4 for two typos (none when
    /// <paramref name="typos"/> is false).
    /// </summary>
    public static double Match(string q, string w, bool typos = true)
    {
        if (q == w)
        {
            return 1;
        }

        if (w.StartsWith(q, StringComparison.Ordinal))
        {
            return 0.9;
        }

        if (q.Length >= 3 && w.Contains(q, StringComparison.Ordinal))
        {
            return 0.7;
        }

        // Typos: only in words of 4 letters or more, and the first letter has to be right.
        if (!typos || q.Length < 4 || q[0] != w[0])
        {
            return 0;
        }

        var allowed = q.Length >= 6 ? 2 : 1;
        var d = Distance(q, w, allowed);
        if (d <= allowed)
        {
            return d == 1 ? 0.6 : 0.45;
        }

        // Still typing a long word, with a typo so far: "twilite" → Twilight.
        if (w.Length > q.Length)
        {
            d = Distance(q, w[..q.Length], allowed);
            if (d <= allowed)
            {
                return d == 1 ? 0.5 : 0.4;
            }
        }
        return 0;
    }

    static readonly HashSet<string> SmallWords =
    [
        "the",
        "a",
        "an",
        "of",
        "and",
        "from",
        "to",
        "in",
        "on",
    ];

    /// <summary>Lower case, no accents or apostrophes, "&" as "and", anything else that isn't a letter or digit splits words.</summary>
    public static string[] Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (c == '&')
            {
                sb.Append(" and ");
            }
            else if (c is '\'' or '’' or '`')
            {
                // Stoker's → stokers
            }
            else
            {
                sb.Append(' ');
            }
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
