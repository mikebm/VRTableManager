using System.Text.RegularExpressions;
using VrTableManager.Models;

namespace VrTableManager.Services;

/// <summary>
/// A table's wheel art: the game's logo on a transparent background, which front ends (PinUP Popper, PinballX,
/// VPX Launcher) show on their selection wheel. The VR lobby shows it over the selected cabinet. From, best first:
/// <list type="number">
/// <item>a picture you chose (<see cref="UserArtwork.Kind.Wheel"/>, <c>artwork\wheel.png</c>);</item>
/// <item>one saved from the catalog's wheel art or media pack, as <c>medias\(Wheel) &lt;table&gt;.png</c>.</item>
/// </list>
/// </summary>
public static class WheelArt
{
    public const string Prefix = "(Wheel) ";
    public static readonly string[] ImageExts = [".png", ".webp", ".jpg", ".jpeg"];
    const long MaxBytes = 10 * 1024 * 1024;

    /// <summary>The wheel art saved in a table folder's medias folder, or null.</summary>
    public static string? Saved(string tableFolder)
    {
        var medias = Path.Combine(tableFolder, "medias");
        try
        {
            return Directory.Exists(medias)
                ? Directory
                    .EnumerateFiles(medias, Prefix + "*")
                    .Where(f =>
                        ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant())
                        && new FileInfo(f).Length > 0
                    )
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The wheel picture's file: yours, else the saved one; null when there's none.</summary>
    public static string? Find(InstalledTable t) =>
        UserArtwork.Find(t, UserArtwork.Kind.Wheel) ?? (t.HasOwnFolder ? Saved(t.Folder) : null);

    /// <summary>The art key for <see cref="Find"/>'s picture (a "local:" key the lobby's loader reads), or null.</summary>
    public static string? Key(InstalledTable t) =>
        Find(t) is { } f ? UserArtwork.LocalKey(f) : null;

    public static void RemoveSaved(string tableFolder)
    {
        while (Saved(tableFolder) is { } f)
        {
            File.Delete(f);
        }
    }

    /// <summary>
    /// Where this game's wheel art can come from, best first: the catalog's wheel art downloads (small; a
    /// <c>WheelPack</c>), then its media packs (big, but they have a <c>Wheel\</c> folder).
    /// </summary>
    public static List<(MediaPackAudio.Source Source, bool WheelPack)> SourcesFor(VpsGame game)
    {
        var result = new List<(MediaPackAudio.Source, bool)>();
        foreach (var f in game.WheelArtFiles ?? [])
        {
            if (
                f.Urls?.FirstOrDefault(u =>
                    !u.Broken && Uri.IsWellFormedUriString(u.Url, UriKind.Absolute)
                ) is
                { } u
            )
            {
                result.Add(
                    (
                        new MediaPackAudio.Source(
                            u.Url,
                            false,
                            "wheel art"
                                + (
                                    f.Authors is { Count: > 0 } a
                                        ? " by " + string.Join(", ", a)
                                        : ""
                                )
                        ),
                        true
                    )
                );
            }
        }

        result.AddRange(
            MediaPackAudio.SourcesFor(game).Where(s => !s.IsDirect).Select(s => (s, false))
        );
        return result;
    }

    /// <summary>The game a wheel is for: its name, and the maker and year that tell editions and namesakes apart.</summary>
    public sealed record Game(string Name, string? Manufacturer = null, int? Year = null)
    {
        public static Game Of(TableManifest m) => new(m.MatchName, m.Manufacturer, m.Year);
    }

    /// <summary>
    /// How well a file name names <paramref name="game"/>, or null when it names another game. "Flash (Williams
    /// 1979)" is Flash exactly; "Flash Gordon (Bally 1981)" only contains the name, and its year says it's another
    /// game. A "(Wheel) " in front (VPX's medias naming) is ignored. 0 when the name says nothing either way.
    /// </summary>
    public static int? NameMatch(string fileName, Game game)
    {
        var name = Regex.Replace(fileName, @"^\s*\(wheel\)\s*", "", RegexOptions.IgnoreCase);
        var title = MediaPackFiles.Normalize(name.Split('(')[0]);
        var wanted = MediaPackFiles.Normalize(game.Name);
        if (title.Length < 3 || wanted.Length < 3)
        {
            return 0;
        }

        int score;
        if (title == wanted)
        {
            score = 40;
        }
        else if (title.Contains(wanted) || wanted.Contains(title))
        {
            score = 15;
        }
        else
        {
            return 0;
        }

        var brackets = Regex.Match(name, @"\((.*?)\)");
        if (brackets.Success)
        {
            var inside = brackets.Groups[1].Value;
            var year = Regex.Match(inside, @"\b(?:19|20)\d\d\b");
            if (year.Success && game.Year is > 0)
            {
                if (year.Value != game.Year.ToString())
                {
                    return null; // the same name, another machine (or edition)
                }

                score += 15;
            }

            if (
                MediaPackFiles.Normalize(game.Manufacturer ?? "") is { Length: > 2 } maker
                && MediaPackFiles.Normalize(inside).Contains(maker)
            )
            {
                score += 5;
            }
        }
        return score;
    }

    /// <summary>
    /// How good an entry is as the game's wheel picture, or null when it isn't one. In a media pack only the
    /// front ends' wheel folders count (Popper's <c>Wheel\</c>, PinballX's <c>Wheel Images\</c>). A wheel art
    /// download (<paramref name="wheelPack"/>) may hold many games' wheels, so there a picture counts only when it's
    /// named after this game or has a plain name ("wheel.png"); <see cref="Save"/> also takes a pack's only picture.
    /// </summary>
    public static int? Score(string entryPath, long size, Game game, bool wheelPack)
    {
        var ext = Path.GetExtension(entryPath).ToLowerInvariant();
        if (size <= 0 || size > MaxBytes || !ImageExts.Contains(ext))
        {
            return null;
        }

        var (folders, file) = MediaPackFiles.Parts(entryPath);
        var inWheelFolder = folders.Any(f => f.Contains("wheel"));
        if (NameMatch(Path.GetFileNameWithoutExtension(entryPath), game) is not { } named)
        {
            return null; // named after another game
        }

        if (wheelPack)
        {
            var plain = Regex.IsMatch(file, @"^(wheel|logo|hd|\d)*$");
            if (named == 0 && !plain)
            {
                return null; // another game's wheel in a mega pack
            }
        }
        else if (!inWheelFolder && !file.Contains("wheel"))
        {
            return null; // a media pack's other pictures: backglass, flyer, playfield…
        }

        var score = named;
        if (inWheelFolder || file.Contains("wheel"))
        {
            score += 20;
        }

        if (ext is ".png" or ".webp")
        {
            score += 10; // transparent around the logo
        }

        return score;
    }

    /// <summary>The entry <see cref="Save"/> takes: the best-scoring one, else a wheel pack's only picture.</summary>
    public static string? Pick(
        IReadOnlyList<(string Path, long Size)> entries,
        Game game,
        bool wheelPack
    )
    {
        if (MediaPackFiles.Pick(entries, (p, size) => Score(p, size, game, wheelPack)) is { } best)
        {
            return best;
        }

        if (!wheelPack)
        {
            return null;
        }

        // A single-game download whose picture is named some other way ("AFM_wheel.png").
        var pictures = entries
            .Where(e =>
                e.Size is > 0 and <= MaxBytes
                && ImageExts.Contains(Path.GetExtension(e.Path).ToLowerInvariant())
            )
            .ToList();
        return pictures.Count == 1 ? pictures[0].Path : null;
    }

    /// <summary>
    /// Saves the best wheel picture from a downloaded media pack or wheel art download as
    /// <c>medias\(Wheel) &lt;folder name&gt;.&lt;ext&gt;</c>. Returns the saved path, or null when there's none.
    /// </summary>
    public static string? Save(
        string downloaded,
        string tableFolder,
        Game game,
        bool wheelPack,
        CancellationToken ct = default
    ) =>
        MediaPackFiles.Open(
            downloaded,
            entries => Pick(entries, game, wheelPack),
            wheelPack ? ImageExts : [],
            (src, entry) =>
            {
                var medias = Directory
                    .CreateDirectory(Path.Combine(tableFolder, "medias"))
                    .FullName;
                var dest = Path.Combine(
                    medias,
                    Prefix
                        + Path.GetFileName(Path.TrimEndingDirectorySeparator(tableFolder))
                        + Path.GetExtension(entry).ToLowerInvariant()
                );
                var tmp = dest + ".new";
                MediaPackFiles.WriteFile(src, tmp, ct);
                RemoveSaved(tableFolder);
                File.Move(tmp, dest, overwrite: true);
                File.SetLastWriteTimeUtc(dest, DateTime.UtcNow); // a new art key, so the lobby doesn't show the old one
                return dest;
            },
            ct
        );
}
