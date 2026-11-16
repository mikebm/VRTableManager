using VrTableManager.Lobby;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>
/// The lobby run without a window (<c>--headless -- --prepare-tables</c>, the manager's PREPARE TABLES): reads
/// every VPX table now for what the lobby would otherwise read from its .vpx the first time it shows the table,
/// and keeps it in the same caches the lobby uses:
/// <list type="bullet">
/// <item>its VR cabinet (<see cref="CabinetCache"/>), the slow one: about 10 s a table on a hard drive;</item>
/// <item>its playfield picture (<see cref="VpxImages.CachedPlayfield"/>);</item>
/// <item>its playfield's height map, for a table with a lobby video (<see cref="PlayfieldDepth"/>), unless lobby
/// videos are turned off or Setup chose 3D toys instead of parallax;</item>
/// <item>its 3D toys (<see cref="ToyCache"/>), when Setup chose them, until they fill the size chosen there: a second
/// or two a table, otherwise read the first
/// time the table is picked;</item>
/// <item>its own backglass, back panel and inner side wall pictures (<see cref="VpxImages.CachedBackglass"/>,
/// <see cref="VpxImages.CachedBackPanel"/>), and its .directb2s's backglass (<see cref="DirectB2s"/>): the
/// default cabinet wears them.</item>
/// </list>
/// They are read one table after another, while its file is still in Windows' cache. Tables already done are
/// skipped, so running it again only does new and updated ones. Progress goes to standard output, one line each,
/// for the manager to show: <c>PREPARE-TOTAL todo all</c>, <c>PREPARE i todo title</c>,
/// <c>PREPARE-ERROR title: message</c>, <c>PREPARE-DONE prepared failed</c>.
/// </summary>
static class PrepareTables
{
    public static int Run(IEnumerable<LobbyTable> library, AppSettings settings, string? cabinetDir)
    {
        var artDir = ArtLoader.ExtractDir;
        var toyLimit = ToyFiles.Bytes(settings.LobbyToyCacheGb);
        var tables = library
            .Select(t => (Table: t, Vpx: Main.VpxFile(t)))
            .Where(x => x.Vpx != null && File.Exists(x.Vpx))
            .DistinctBy(x => x.Vpx, StringComparer.OrdinalIgnoreCase)
            // In folder order: tables near each other on the disk are read one after another.
            .OrderBy(x => x.Vpx, StringComparer.OrdinalIgnoreCase)
            .Select(x => (x.Table, Vpx: x.Vpx!))
            .ToList();
        // The saved toys: the unusable ones go first (an older version, or saved from an older copy of their table, say
        // one updated while toys were off): they'd count against the space, and with a size chosen on Setup they could
        // fill it, so nothing was prepared. Then how much room is left. Already full: no toys are planned, so they
        // aren't counted as work that never happens.
        long toyBytes = 0;
        if (settings.LobbyTableToys)
        {
            ToyCache.DropStale(ToyCache.DefaultDir);
            ToyFiles.DropStale(
                tables.Select(x => x.Vpx),
                vpx => ToyCache.IsCurrent(vpx, ToyCache.DefaultDir, TableToysLoader.DefaultBudget)
            );
            toyBytes = ToyFiles.SizeBytes();
        }
        var toysFit = toyLimit <= 0 || toyBytes < toyLimit;
        var todo = tables
            .Select(x =>
                (x.Table, x.Vpx, Needs: Needs(x.Table, x.Vpx, settings, cabinetDir, artDir))
            )
            .Select(x => toysFit ? x : x with { Needs = x.Needs with { Toys = false } })
            .Where(x => x.Needs.Any)
            .ToList();
        Say($"PREPARE-TOTAL {todo.Count} {tables.Count}");

        var failed = 0;
        for (var i = 0; i < todo.Count; i++)
        {
            var (t, vpx, needs) = todo[i];
            Say($"PREPARE {i + 1} {todo.Count} {t.Title}");
            try
            {
                if (needs.Playfield)
                {
                    VpxImages.CachedPlayfield(vpx, artDir);
                }

                // The pictures the lobby otherwise reads out of the table file (or its .directb2s) the first time it
                // shows the table: its own backglass and back panel, and the .directb2s's backglass.
                if (needs.OwnBackglass)
                {
                    VpxImages.CachedBackglass(vpx, artDir);
                }

                if (needs.BackPanel)
                {
                    VpxImages.CachedBackPanel(vpx, artDir);
                }

                if (needs.Blades)
                {
                    VpxImages.CachedBladeLeft(vpx, artDir);
                    VpxImages.CachedBladeRight(vpx, artDir);
                }

                if (needs.B2s is { } b2s)
                {
                    DirectB2s.CachedBackglass(b2s, artDir);
                }

                if (needs.Cabinet && cabinetDir != null)
                {
                    CabinetCache.Save(vpx, cabinetDir, RealCabinetLoader.Parse(vpx));
                }

                if (needs.Depth is { } videoFolder)
                {
                    PlayfieldDepth.Load(videoFolder, vpx);
                }

                // Within the size chosen on Setup: once the toys fill it, the rest are read when they're first picked.
                if (needs.Toys && (toyLimit <= 0 || toyBytes < toyLimit))
                {
                    // One that can't be read is kept as having none, or every run would read it again.
                    ToyCache.Save(
                        vpx,
                        ToyCache.DefaultDir,
                        TableToysLoader.Parse(vpx, TableToysLoader.DefaultBudget) ?? ToyCache.None
                    );
                    var saved = new FileInfo(ToyFiles.FileFor(vpx));
                    toyBytes += saved.Exists ? saved.Length : 0;
                }
            }
            catch (Exception e)
            {
                // One table that can't be read mustn't stop the rest; the lobby falls back for it as it always has.
                failed++;
                Say($"PREPARE-ERROR {t.Title}: {e.Message}");
            }
        }
        Say($"PREPARE-DONE {todo.Count - failed} {failed}");
        return 0;
    }

    readonly record struct Work(
        bool Cabinet,
        bool Playfield,
        string? Depth,
        bool OwnBackglass = false,
        bool BackPanel = false,
        bool Blades = false,
        string? B2s = null,
        bool Toys = false
    )
    {
        public bool Any =>
            Cabinet
            || Playfield
            || Depth != null
            || OwnBackglass
            || BackPanel
            || Blades
            || B2s != null
            || Toys;
    }

    static Work Needs(
        LobbyTable t,
        string vpx,
        AppSettings settings,
        string? cabinetDir,
        string artDir
    )
    {
        var cabinet = cabinetDir != null && !CabinetCache.IsCurrent(vpx, cabinetDir);
        // Only when the lobby would read the playfield out of the table (not a picture you chose).
        var playfield =
            LobbyArt.PlayfieldSource(t) is { } src
            && src.StartsWith(LobbyArt.VpxScheme, StringComparison.Ordinal)
            && !VpxImages.HasCachedPlayfield(vpx, artDir);
        // With 3D toys the lobby shows no parallax, so no height maps: the toys instead.
        var depth =
            settings.LobbyPlayfieldVideos
            && !settings.LobbyTableToys
            && LobbyArt.PlayfieldVideo(t) is var (folder, _)
            && !PlayfieldDepth.IsKnown(folder, vpx)
                ? folder
                : null;
        var b2s = Backglasses.PathFor(t.Source);
        return new Work(
            cabinet,
            playfield,
            depth,
            OwnBackglass: VpxImages.KnownBackglass(vpx, artDir) == null,
            BackPanel: VpxImages.KnownBackPanel(vpx, artDir) == null,
            Blades: VpxImages.KnownBladeLeft(vpx, artDir) == null
                || VpxImages.KnownBladeRight(vpx, artDir) == null,
            B2s: File.Exists(b2s) && !DirectB2s.IsCached(b2s, artDir) ? b2s : null,
            Toys: settings.LobbyTableToys
                && !ToyCache.IsCurrent(vpx, ToyCache.DefaultDir, TableToysLoader.DefaultBudget)
        );
    }

    /// <summary>A line for the manager, written straight away (Godot's own printing may hold it back).</summary>
    static void Say(string line)
    {
        // As UTF-8, which the manager reads it as, whatever the console's code page: titles keep their accents.
        var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
        Stdout.Write(bytes);
        Stdout.Flush();
    }

    static readonly Stream Stdout = Console.OpenStandardOutput();
}
