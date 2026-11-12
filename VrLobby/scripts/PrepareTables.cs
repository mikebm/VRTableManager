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
/// videos are turned off.</item>
/// </list>
/// All three are read one table after another, while its file is still in Windows' cache. Tables already done are
/// skipped, so running it again only does new and updated ones. Progress goes to standard output, one line each,
/// for the manager to show: <c>PREPARE-TOTAL todo all</c>, <c>PREPARE i todo title</c>,
/// <c>PREPARE-ERROR title: message</c>, <c>PREPARE-DONE prepared failed</c>.
/// </summary>
static class PrepareTables
{
    public static int Run(IEnumerable<LobbyTable> library, AppSettings settings, string? cabinetDir)
    {
        var artDir = ArtLoader.CacheDir;
        var tables = library
            .Select(t => (Table: t, Vpx: Main.VpxFile(t)))
            .Where(x => x.Vpx != null && File.Exists(x.Vpx))
            .DistinctBy(x => x.Vpx, StringComparer.OrdinalIgnoreCase)
            // In folder order: tables near each other on the disk are read one after another.
            .OrderBy(x => x.Vpx, StringComparer.OrdinalIgnoreCase)
            .Select(x => (x.Table, Vpx: x.Vpx!))
            .ToList();
        var todo = tables
            .Select(x =>
                (x.Table, x.Vpx, Needs: Needs(x.Table, x.Vpx, settings, cabinetDir, artDir))
            )
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

                if (needs.Cabinet && cabinetDir != null)
                {
                    CabinetCache.Save(vpx, cabinetDir, RealCabinetLoader.Parse(vpx));
                }

                if (needs.Depth is { } videoFolder)
                {
                    PlayfieldDepth.Load(videoFolder, vpx);
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

    readonly record struct Work(bool Cabinet, bool Playfield, string? Depth)
    {
        public bool Any => Cabinet || Playfield || Depth != null;
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
        var depth =
            settings.LobbyPlayfieldVideos
            && LobbyArt.PlayfieldVideo(t) is var (folder, _)
            && !PlayfieldDepth.IsKnown(folder, vpx)
                ? folder
                : null;
        return new Work(cabinet, playfield, depth);
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
