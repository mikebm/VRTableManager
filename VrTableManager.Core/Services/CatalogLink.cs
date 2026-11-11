using VrTableManager.Models;

namespace VrTableManager.Services;

/// <summary>
/// Links a table that didn't come from the catalog (copied in by Import tables, or by an older version of it)
/// to its catalog game, so it gets the catalog's picture, backglass, add-ons and rules. Only links when sure:
/// <list type="bullet">
/// <item>the table's ROM is listed for a catalog game whose name also matches the table's file or folder, or</item>
/// <item>the name alone matches one game clearly better than any other.</item>
/// </list>
/// It never sets the table file id: which version of the table this is isn't known, and a guess would show
/// updates that aren't.
/// </summary>
public static class CatalogLink
{
    /// <summary>Bump when matching learns something new, so unlinked tables are tried again.</summary>
    public const int Version = 1;

    /// <summary>Score the name alone needs (see <see cref="TableFileMatch.Score"/>; 1 ≈ every word of the game's name).</summary>
    const double NameOnly = 0.9;

    /// <summary>How far ahead of the next game (by name) the best one must be when there's no ROM to go on.</summary>
    const double Margin = 0.15;

    /// <summary>With a matching ROM, the name only has to agree a little (a word of it), to rule out rethemes on a donor ROM.</summary>
    const double WithRom = 0.3;

    /// <summary>
    /// The catalog game the table is, or null when it isn't clear. <paramref name="names"/> are what the table
    /// is called on disk: its .vpx file name and its folder name.
    /// </summary>
    public static VpsGame? Find(
        IEnumerable<string> romNames,
        IEnumerable<string> names,
        IReadOnlyList<VpsGame> games
    )
    {
        var vpxGames = games.Where(g => g.TableFiles?.Any(VpsCatalog.IsVpx) == true).ToList();
        // Score() strips an extension, which would eat "2.0" off a folder name: give every name one.
        var labels = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => Path.GetFileNameWithoutExtension(n) + ".vpx")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        double Score(VpsGame g) =>
            labels.Count == 0 ? 0 : labels.Max(l => TableFileMatch.Score(g, l));

        var roms = romNames
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (roms.Count > 0)
        {
            var byRom = vpxGames
                .Where(g =>
                    g.RomFiles?.Any(r => r.Version is { } v && roms.Contains(v.Trim())) == true
                )
                .Select(g => (g, s: Score(g)))
                .Where(x => x.s >= WithRom)
                .OrderByDescending(x => x.s)
                .ThenByDescending(x => x.g.UpdatedAt ?? 0)
                .FirstOrDefault();
            if (byRom.g != null)
            {
                return byRom.g;
            }
        }

        var ranked = vpxGames
            .Select(g => (g, s: Score(g)))
            .Where(x => x.s > 0)
            .OrderByDescending(x => x.s)
            .Take(2)
            .ToList();
        if (ranked.Count == 0 || ranked[0].s < NameOnly)
        {
            return null;
        }

        return ranked.Count == 1 || ranked[0].s - ranked[1].s >= Margin ? ranked[0].g : null;
    }

    /// <summary>
    /// Tries to link an unlinked VPX table and records that it tried. Returns true when the manifest changed
    /// (linked, or only marked as tried) and should be saved.
    /// </summary>
    public static bool TryLink(InstalledTable t, IReadOnlyList<VpsGame> games)
    {
        var m = t.Manifest;
        if (
            !t.IsVpx
            || t.SharedFolder
            || m.GameId != ""
            || m.CatalogLink >= Version
            || games.Count == 0
            // Only tables with a vrtm.json of their own: writing one into a folder you set up by hand would
            // make this app treat the folder as its own (Remove deletes the whole folder).
            || !File.Exists(Path.Combine(t.Folder, "vrtm.json"))
        )
        {
            return false;
        }

        m.CatalogLink = Version;
        var game = Find(
            m.RomNames,
            [Path.GetFileName(t.TablePath), Path.GetFileName(t.Folder), m.GameName],
            games
        );
        if (game != null)
        {
            Apply(m, game);
        }

        return true;
    }

    /// <summary>Fills in what the catalog knows about the game (not the table file: see the class summary).</summary>
    public static void Apply(TableManifest m, VpsGame game)
    {
        m.GameId = game.Id;
        m.Manufacturer ??= game.Manufacturer;
        m.Year ??= game.Year;
        // "imgUrl": "" in the spreadsheet means no picture, like a missing one.
        if (string.IsNullOrWhiteSpace(m.ImgUrl))
        {
            m.ImgUrl =
                (string.IsNullOrWhiteSpace(game.ImgUrl) ? null : game.ImgUrl)
                ?? game.TableFiles?.Where(VpsCatalog.IsVpx)
                    .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.ImgUrl))
                    ?.ImgUrl;
        }
    }
}
