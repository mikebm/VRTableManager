using System.Text;
using System.Text.RegularExpressions;
using OpenMcdf;
using VrTableManager.Models;

namespace VrTableManager.Services;

/// <summary>
/// Which of a catalog game's table files (versions) an imported table is, so update checks can compare it.
/// Goes on the author and version found in the table's file and folder names and in the .vpx's own
/// TableInfo (what VPX's Table Info dialog shows). Only answers when one table file clearly fits:
/// <list type="bullet">
/// <item>one table file has the table's version (by its author, when the author can be told), or</item>
/// <item>one table file is by the table's author and the table's version is older than it (an update, shown as one).</item>
/// </list>
/// Anything else (no author or version to go on, several fits, a version newer than the catalog's) stays
/// unguessed: a wrong guess shows updates that aren't, or hides ones that are.
/// </summary>
public static partial class TableFileGuess
{
    /// <summary>What the .vpx says about itself (VPX's Table Info dialog). Any of it can be blank or stale.</summary>
    public sealed record TableInfo(string? Name, string? Author, string? Version);

    /// <summary>The table file the table is, and the version to record for it (the table's own, when older).</summary>
    public sealed record Guess(VpsTableFile File, string? Version);

    /// <summary>Reads the TableInfo streams of a .vpx, or null when it can't be read.</summary>
    public static TableInfo? ReadInfo(string vpxPath)
    {
        try
        {
            using var root = RootStorage.OpenRead(vpxPath);
            if (!root.TryOpenStorage("TableInfo", out var info) || info == null)
            {
                return null;
            }

            string? Read(string name)
            {
                if (!info.TryOpenStream(name, out var s) || s == null)
                {
                    return null;
                }

                using (s)
                {
                    // VPX writes these as UTF-16 with no length or terminator; keep it short either way.
                    var len = (int)Math.Min(s.Length, 4096);
                    var buf = new byte[len];
                    var read = 0;
                    while (read < len)
                    {
                        var n = s.Read(buf, read, len - read);
                        if (n <= 0)
                        {
                            break;
                        }
                        read += n;
                    }
                    var text = Encoding
                        .Unicode.GetString(buf, 0, read & ~1)
                        .Trim('\0', ' ', '\r', '\n', '\t');
                    return text == "" ? null : text;
                }
            }

            return new TableInfo(Read("TableName"), Read("AuthorName"), Read("TableVersion"));
        }
        catch (Exception e)
            when (e
                    is IOException
                        or UnauthorizedAccessException
                        or FormatException
                        or InvalidDataException
            )
        {
            return null;
        }
        catch (Exception)
        {
            return null; // not a compound file (or a damaged one): nothing to go on
        }
    }

    /// <summary>
    /// The table file <paramref name="names"/> (the .vpx file name, its folder name) and <paramref name="info"/>
    /// point to among <paramref name="game"/>'s VPX table files, or null when it isn't clear.
    /// </summary>
    public static Guess? Find(VpsGame game, IEnumerable<string> names, TableInfo? info)
    {
        var files = (game.TableFiles ?? [])
            .Where(f => VpsCatalog.IsVpx(f) && !VpuRemix.IsRemix(f))
            .ToList();
        if (files.Count == 0)
        {
            return null;
        }

        var nameList = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

        // Authors: a table file's author in the names or the Table Info author. Not one whose name is part of
        // the game's own name ("Ace" for "Ace of Speed"), which every table of the game would match.
        // Whole words only ("Tom" isn't in "Custom"), a multi-word author ("Tom Tower") as consecutive words; a
        // long name (6+ letters) may also be joined to others ("Rothbauerw_Flupper", "VPWmod" no: VPW is short).
        var text = string.Join(" ", nameList.Append(info?.Author ?? ""));
        var words = Words(text);
        var joined = " " + string.Join(" ", words) + " ";
        var hay = Squash(text);
        var gameName = Squash(game.Name);
        bool Named(string author)
        {
            var a = Squash(author);
            if (a.Length < 3 || gameName.Contains(a))
            {
                return false;
            }
            return joined.Contains(
                    " " + string.Join(" ", Words(author)) + " ",
                    StringComparison.Ordinal
                ) || (a.Length >= 6 && hay.Contains(a));
        }
        int AuthorHits(VpsTableFile f) => (f.Authors ?? []).Count(Named);

        // Versions the table claims: "v2.4.1" / "v.2.4.1" / "1.2" in its names, else its Table Info version.
        // The names win: authors often leave Table Info's version behind (a 1.5 file saying 2.3 inside).
        // Only a .vpx name loses its extension: a folder "MM 2.0.1" would otherwise lose its ".1".
        var labelled = nameList
            .SelectMany(n => Labelled().Matches(n).Select(m => m.Groups[1].Value))
            .ToList();
        var dotted = nameList
            .SelectMany(n =>
                Dotted()
                    .Matches(
                        n.EndsWith(".vpx", StringComparison.OrdinalIgnoreCase)
                            ? Path.GetFileNameWithoutExtension(n)
                            : n
                    )
                    .Select(m => m.Groups[1].Value)
            )
            .ToList();
        var infoVersion = info?.Version is { } iv && iv.Any(char.IsDigit) ? iv : null;
        List<string> fromNames = [.. labelled, .. dotted];
        var claimed = (
            fromNames.Count > 0 ? fromNames
            : infoVersion != null ? [infoVersion]
            : []
        ).Select(Version).Where(v => v != "").ToHashSet();
        bool HasVersion(VpsTableFile f) =>
            Version(f.Version) is { Length: > 0 } v && claimed.Contains(v);

        // The table files with the most of the table's authors: a mod (Ext2k's conversion of Marty02's table)
        // names both, the original only one.
        var hits = files.ToDictionary(f => f, AuthorHits);
        var most = hits.Values.Max();
        var authored = most > 0 ? files.Where(f => hits[f] == most).ToList() : [];
        var pool = authored.Count > 0 ? authored : files;
        var same = pool.Where(HasVersion).ToList();
        if (same.Count == 1)
        {
            var f = same[0];
            // With no author to go on, a version alone is someone else's 1.5 as often as this one's: only when
            // the game has a single VPX table.
            if (authored.Count > 0 || files.Count == 1)
            {
                return new Guess(f, f.Version);
            }
            return null;
        }

        if (same.Count == 0 && authored.Count == 1)
        {
            // The author's table, an older version of it: link it so its update shows. Only on one clear,
            // dotted version: "v01" is as often a VR room's number, and two versions in the names may be two things.
            var mine =
                fromNames.Count > 0 ? fromNames
                : infoVersion != null ? [infoVersion]
                : [];
            var own = mine.Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                is [var one]
                ? one
                : null;
            var f = authored[0];
            if (own != null && PlainVersion().IsMatch(own) && Older(own, f.Version))
            {
                return new Guess(f, own.Trim());
            }
        }

        return null;
    }

    /// <summary>Lower-case runs of letters and digits: "Medieval Madness_DarthVito" → medieval, madness, darthvito.</summary>
    static List<string> Words(string s) =>
        WordRun().Matches(s.ToLowerInvariant()).Select(m => m.Value).ToList();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRun();

    /// <summary>"1.2", "2.0.1", "1.2a": dotted, and no leading zero ("01", "0.1" aren't a table's release).</summary>
    [GeneratedRegex(@"^[1-9]\d*(\.\d+)+[a-z]?$", RegexOptions.IgnoreCase)]
    private static partial Regex PlainVersion();

    /// <summary>Lower-case letters and digits only, so "Rothbauerw", "rothbauerw_" and "ROTHBAUERW" agree.</summary>
    static string Squash(string? s) =>
        new((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>A version to compare: no v, no trailing ".0"s ("v1.0" and "1" agree), lower case.</summary>
    static string Version(string? v)
    {
        var s = (v ?? "").Trim().TrimStart('v', 'V').TrimStart('.').Trim().ToLowerInvariant();
        while (s.EndsWith(".0", StringComparison.Ordinal))
        {
            s = s[..^2];
        }
        return s;
    }

    /// <summary>True when both are dotted numbers and <paramref name="mine"/> is the lower.</summary>
    static bool Older(string mine, string? theirs)
    {
        static int[]? Parts(string? v)
        {
            var s = Version(v);
            if (s == "")
            {
                return null;
            }
            var p = s.Split('.');
            var n = new int[p.Length];
            for (var i = 0; i < p.Length; i++)
            {
                if (!int.TryParse(p[i], out n[i]))
                {
                    return null;
                }
            }
            return n;
        }

        var a = Parts(mine);
        var b = Parts(theirs);
        if (a == null || b == null)
        {
            return false;
        }
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
            {
                return x < y;
            }
        }
        return false;
    }

    /// <summary>"v2.4.1", "v.2.4.1", "ver 1.2", "version 1.2a": a number someone marked as the version.</summary>
    [GeneratedRegex(@"(?i)(?<![a-z])v(?:er(?:sion)?)?\s?\.?\s?(\d+(?:\.\d+)*[a-z]?)(?![\d])")]
    private static partial Regex Labelled();

    /// <summary>A bare dotted number ("1.2", "2.0.1"), not part of a longer one. A year has no dot, so it's left out.</summary>
    [GeneratedRegex(@"(?<![\d.])(\d+\.\d+(?:\.\d+)*[a-z]?)(?![\d.])")]
    private static partial Regex Dotted();
}
