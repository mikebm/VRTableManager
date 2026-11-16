using System.Text.RegularExpressions;
using VrTableManager.Models;

namespace VrTableManager.Services;

/// <summary>
/// Installing a table file you already downloaded (My Tables → ADD A TABLE FILE…): which catalog game it is,
/// guessed from its file name, and which of the game's table files (version) it most likely is. A file that
/// isn't in the catalog is installed under a stand-in entry named after the file.
/// </summary>
public static partial class TableFileMatch
{
    public static readonly string[] Extensions =
    [
        ".zip",
        ".rar",
        ".7z",
        ".vpx",
        ".fpt",
        VpuRemix.PatchExt,
    ];

    public static bool IsFuturePinball(string file) =>
        Path.GetExtension(file).Equals(".fpt", StringComparison.OrdinalIgnoreCase);

    // Words in file names that say nothing about which game it is.
    static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "vpx",
        "vpw",
        "vr",
        "fs",
        "fss",
        "dt",
        "desktop",
        "cab",
        "cabinet",
        "mod",
        "edition",
        "table",
        "tables",
        "final",
        "release",
        "the",
        "and",
        "of",
        "a",
        "version",
        "ver",
        "rev",
        "beta",
        "fix",
        "fixed",
        "update",
        "updated",
        "remaster",
        "remastered",
        "pup",
        "puppack",
        "b2s",
        "directb2s",
        "rom",
        "roms",
        "zip",
        "rar",
        "7z",
        "fpt",
        "x",
        "v",
        "hybrid",
    };

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"\d+(\.\d+)+")]
    private static partial Regex VersionNumber();

    static List<string> Words(string s) =>
        Word().Matches(s.ToLowerInvariant()).Select(m => m.Value).ToList();

    static List<string> NameWords(string s) => Words(s).Where(w => !Noise.Contains(w)).ToList();

    /// <summary>A machine year in a file name ("Stern 2014"), to tell a remake from the original.</summary>
    static bool IsYear(string w) =>
        w.Length == 4 && int.TryParse(w, out var y) && y is >= 1930 and <= 2039;

    /// <summary>A file name split into words once, to score against every catalog game (see <see cref="Score(VpsGame, FileWords)"/>).</summary>
    public sealed class FileWords
    {
        internal FileWords(string fileName)
        {
            Words = NameWords(Path.GetFileNameWithoutExtension(fileName));
            Set = [.. Words];
            // Also accept the name's words run together ("AttackFromMars", "attack_from_mars" split differently).
            Joined = string.Concat(Words);
        }

        internal readonly List<string> Words;
        internal readonly HashSet<string> Set;
        internal readonly string Joined;
    }

    public static FileWords Prepare(string fileName) => new(fileName);

    /// <summary>A catalog game's name and maker split into words, kept with the strings they came from.</summary>
    sealed class GameWords(string name, string? maker)
    {
        public readonly string FromName = name;
        public readonly string? FromMaker = maker;
        public readonly List<string> Name = NameWords(name);
        public readonly HashSet<string> NameSet = [.. NameWords(name)];
        public readonly List<string> Maker = string.IsNullOrWhiteSpace(maker)
            ? []
            : NameWords(maker);
    }

    /// <summary>
    /// Per catalog game, built once: AUTO-LINK scores every installed table against every game, and splitting
    /// the game's name again each time was most of its run time. Rebuilt if the game's name or maker changes.
    /// </summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        VpsGame,
        GameWords
    > GameWordsIndex = new();

    static GameWords WordsOf(VpsGame g)
    {
        if (
            GameWordsIndex.TryGetValue(g, out var w)
            && ReferenceEquals(w.FromName, g.Name)
            && ReferenceEquals(w.FromMaker, g.Manufacturer)
        )
        {
            return w;
        }

        w = new GameWords(g.Name, g.Manufacturer);
        GameWordsIndex.AddOrUpdate(g, w);
        return w;
    }

    /// <summary>How well a game's name matches the file name: 0 (no shared word) to about 1.2 (every word, plus maker and year).</summary>
    public static double Score(VpsGame g, string fileName) => Score(g, Prepare(fileName));

    /// <inheritdoc cref="Score(VpsGame, string)"/>
    public static double Score(VpsGame g, FileWords f)
    {
        var file = f.Words;
        if (file.Count == 0)
        {
            return 0;
        }

        var gw = WordsOf(g);
        var name = gw.Name;
        if (name.Count == 0)
        {
            return 0;
        }

        var fileSet = f.Set;
        var joined = f.Joined;
        // "LE" in a file name is how the catalog's "(Limited Edition)" gets shortened ("The Walking Dead LE").
        bool Found(string w) =>
            fileSet.Contains(w)
            || (w == "limited" && fileSet.Contains("le"))
            || (w.Length >= 3 && joined.Contains(w));
        var hits = name.Count(Found);
        // Numbers alone (a version, a year) don't identify a game: "V.1" mustn't match every "1.0" file.
        if (!name.Any(w => !w.All(char.IsDigit) && Found(w)))
        {
            return 0;
        }
        // Share of the game's words found, less a little for file words the name doesn't explain.
        var extra = file.Count(w =>
            !gw.NameSet.Contains(w)
            && !w.All(char.IsDigit)
            && !(w == "le" && gw.NameSet.Contains("limited"))
        );
        // More words matched wins a tie ("Attack from Mars" over "Attack" for "AttackFromMars").
        var score = hits / (double)name.Count - 0.04 * extra + 0.03 * hits;
        if (gw.Maker.Any(fileSet.Contains))
        {
            score += 0.1;
        }

        if (g.Year is > 0)
        {
            if (fileSet.Contains(g.Year.Value.ToString()))
            {
                score += 0.1;
            }
            // The file names another year: "The Walking Dead (Stern 2014)" isn't the 2016 original of that name.
            else if (file.Any(w => IsYear(w) && !gw.NameSet.Contains(w)))
            {
                score -= 0.15;
            }
        }

        return score;
    }

    /// <summary>The likeliest catalog games for a file, best first (only games with a table file of its kind).</summary>
    public static List<VpsGame> Guess(string file, IEnumerable<VpsGame> games, int take = 3)
    {
        var fp = IsFuturePinball(file);
        var name = Prepare(Path.GetFileName(file));
        return games
            .Where(g => g.TableFiles?.Any(t => VpsCatalog.IsVpx(t) != fp) == true)
            .Select(g => (g, s: Score(g, name)))
            .Where(x => x.s >= 0.5)
            .OrderByDescending(x => x.s)
            .ThenByDescending(x => x.g.UpdatedAt ?? 0)
            .Take(take)
            .Select(x => x.g)
            .ToList();
    }

    /// <summary>
    /// Which of the game's table files this is: one whose version number appears in the file name (and then
    /// whose authors do), else the most recently released one of its kind.
    /// </summary>
    public static VpsTableFile? PickTableFile(VpsGame g, string file) =>
        TableFiles(g, file).FirstOrDefault();

    /// <summary>The game's table files of the file's kind, likeliest first (see <see cref="PickTableFile"/>).</summary>
    public static List<VpsTableFile> TableFiles(VpsGame g, string file)
    {
        var fp = IsFuturePinball(file);
        var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        var versions = VersionsIn(name);
        var words = Words(name).ToHashSet();
        return (g.TableFiles ?? [])
            .Where(t => VpsCatalog.IsVpx(t) != fp)
            .OrderByDescending(t => SameVersion(t.Version, versions))
            .ThenByDescending(t => (t.Authors ?? []).Count(a => Words(a).Any(words.Contains)))
            .ThenByDescending(t => t.ReleasedAt ?? 0)
            .ToList();
    }

    /// <summary>
    /// Whether <see cref="PickTableFile"/> is sure: the game has one table file of the kind, or the file name's
    /// version number matches exactly one. Otherwise ask, since update checks compare against the one picked.
    /// </summary>
    public static bool TableFileIsClear(VpsGame g, string file)
    {
        var candidates = TableFiles(g, file);
        if (candidates.Count <= 1)
        {
            return true;
        }

        var versions = VersionsIn(Path.GetFileNameWithoutExtension(file).ToLowerInvariant());
        return candidates.Count(t => SameVersion(t.Version, versions)) == 1;
    }

    /// <summary>Search fields per catalog game, built once (the search box refills the list on every key).</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        VpsGame,
        SearchEntry
    > SearchIndex = new();

    static HashSet<string> VersionsIn(string name) =>
        VersionNumber().Matches(name).Select(m => m.Value).ToHashSet();

    /// <summary>
    /// Catalog games found by what was typed, as Get Tables' search finds them, best first (only games with a
    /// table file of the file's kind).
    /// </summary>
    public static List<VpsGame> Search(
        string? query,
        string file,
        IEnumerable<VpsGame> games,
        int take = 40
    )
    {
        if (TableSearch.Parse(query) is not { } search)
        {
            return [];
        }

        var fp = IsFuturePinball(file);
        return games
            .Where(g => g.TableFiles?.Any(t => VpsCatalog.IsVpx(t) != fp) == true)
            .Select(g => (g, s: search.Score(SearchIndex.GetValue(g, TableSearch.Index))))
            .Where(x => x.s > 0)
            .OrderByDescending(x => x.s)
            .ThenBy(x => x.g.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(x => x.g)
            .ToList();
    }

    /// <summary>"1.6" in the file name matches a catalog version "1.6", "v1.6" or "1.6.0".</summary>
    static bool SameVersion(string? version, HashSet<string> inName)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var v = Trim0(version.Trim().TrimStart('v', 'V'));
        return inName.Any(n => Trim0(n) == v);
        static string Trim0(string s)
        {
            while (s.EndsWith(".0") && s.Count(c => c == '.') > 1)
            {
                s = s[..^2];
            }

            return s;
        }
    }

    /// <summary>A stand-in catalog entry for a table that isn't in the catalog, named after its file.</summary>
    public static (VpsGame Game, VpsTableFile TableFile) NotInCatalog(string file)
    {
        var raw = Path.GetFileNameWithoutExtension(file);
        var title = string.Join(
                " ",
                Regex.Split(raw.Replace('_', ' ').Replace('.', ' '), @"\s+").Where(w => w != "")
            )
            .Trim();
        if (title == "")
        {
            title = "Table";
        }

        var tf = new VpsTableFile { Id = "", TableFormat = IsFuturePinball(file) ? "FP" : "VPX" };
        return (
            new VpsGame
            {
                Id = "",
                Name = title,
                TableFiles = [tf],
            },
            tf
        );
    }
}
