using VrTableManager.Models;

namespace VrTableManager.Services;

/// <summary>
/// Links a table that didn't come from the catalog (copied in by Import tables, or by an older version of it)
/// to its catalog game, so it gets the catalog's picture, backglass, add-ons and rules. Only links when sure:
/// <list type="bullet">
/// <item>the table's ROM is listed for a catalog game whose name also matches the table's file or folder, or</item>
/// <item>the name alone matches one game clearly better than any other.</item>
/// </list>
/// <see cref="TryLink"/> (automatic, after every scan) never sets the table file id: a wrong version shows updates
/// that aren't. <see cref="AutoLink"/> (when you ask: AUTO-LINK, or after Import tables) also links the version
/// when <see cref="TableFileGuess"/> is sure of it.
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
            .Select(TableFileMatch.Prepare)
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

    public enum AutoLinkResult
    {
        /// <summary>Already has its version, or isn't a table this can link (not VPX, no vrtm.json of its own).</summary>
        Skipped,

        /// <summary>Linked to its catalog version: update checks compare it now.</summary>
        Linked,

        /// <summary>The game isn't clear from its names and ROM (or, in a folder with no vrtm.json yet, the version
        /// isn't either): nothing changed.</summary>
        NoGame,

        /// <summary>The game is known (and linked), but not which of its tables this is.</summary>
        NoVersion,
    }

    /// <summary>
    /// AUTO-LINK (asked for, unlike <see cref="TryLink"/>): links a table to its catalog game, and to the table file
    /// (version) <see cref="TableFileGuess"/> is sure of, so updates are checked. The manifest needs saving unless
    /// the result is <see cref="AutoLinkResult.Skipped"/>. Reads the .vpx's Table Info, so run it off the UI thread.
    /// </summary>
    public static AutoLinkResult AutoLink(InstalledTable t, IReadOnlyList<VpsGame> games)
    {
        var m = t.Manifest;
        if (
            !t.IsVpx
            || t.SharedFolder
            || m.TableFileId != ""
            || games.Count == 0
            // A folder of its own: one you set up by hand gets a vrtm.json marked as yours (TableManifest.UserFolder).
            || !TableInstaller.CanRename(t)
        )
        {
            return AutoLinkResult.Skipped;
        }
        // In a folder of yours, only a sure version is worth writing a vrtm.json for, not the game alone.
        var hasManifest = File.Exists(Path.Combine(t.Folder, "vrtm.json"));

        string[] names = [Path.GetFileName(t.TablePath), Path.GetFileName(t.Folder)];
        var game =
            m.GameId != ""
                ? games.FirstOrDefault(g => g.Id == m.GameId)
                : Find(m.RomNames, [.. names, m.GameName], games);
        if (game == null)
        {
            return AutoLinkResult.NoGame;
        }

        var guess = TableFileGuess.Find(game, names, TableFileGuess.ReadInfo(t.TablePath));
        if (guess == null && !hasManifest)
        {
            return AutoLinkResult.NoGame; // nothing written: see hasManifest
        }

        if (m.GameId == "")
        {
            Apply(m, game);
        }
        m.CatalogLink = Math.Max(m.CatalogLink, Version);
        if (guess == null)
        {
            return AutoLinkResult.NoVersion;
        }

        ApplyFile(m, guess.File, guess.Version);
        return AutoLinkResult.Linked;
    }

    /// <summary>
    /// Links the table file, as installing it would have, without renaming the table or touching its ROMs.
    /// <paramref name="version"/> is the version the table is (older than the table file's when an update is out).
    /// </summary>
    public static void ApplyFile(TableManifest m, VpsTableFile tf, string? version)
    {
        m.TableFileId = tf.Id;
        m.Version = version;
        m.SourceUpdatedAt = tf.UpdatedAt;
        if (m.Authors.Count == 0)
        {
            m.Authors = tf.Authors ?? [];
        }
        if (m.Features.Count == 0)
        {
            m.Features = VpuRemix.FeaturesOf(tf);
        }
        m.SourceUrl ??= tf.Urls?.FirstOrDefault()?.Url;
        if (!string.IsNullOrWhiteSpace(tf.ImgUrl))
        {
            m.ImgUrl = tf.ImgUrl;
        }
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
