using VrTableManager.Services;

namespace VrTableManager.Lobby;

/// <summary>
/// The VR lobby's own caches, together under %APPDATA%\VrTableManager\vr-lobby, so they're one folder to find or clear:
/// <list type="bullet">
/// <item><see cref="Cabinets"/>: each table's VR cabinet as read from its .vpx (VrLobby's CabinetCache);</item>
/// <item><see cref="Pictures"/>: pictures read out of table files and .directb2s files (the playfield, the table's own
/// backglass and back panel, the .directb2s's backglass);</item>
/// <item><see cref="AttractAudio"/>: attract sound recorded from each table's ROM (<see cref="RomAudio"/>).</item>
/// </list>
/// Pictures fetched from the web stay in img-cache, which the manager shares. Nothing here is ever written where your
/// own pictures go (a table's artwork folder, <see cref="UserArtwork"/>). A table's playfield height map stays beside
/// its lobby video (re-recording the video drops it).
/// </summary>
public static class LobbyFiles
{
    public static string Root => Path.Combine(AppSettings.DataDir, "vr-lobby");

    public static string Cabinets => Ready(Path.Combine(Root, "cabinets"));
    public static string Pictures => Ready(Path.Combine(Root, "pictures"));
    public static string AttractAudio => Ready(Path.Combine(Root, "attract-audio"));

    /// <summary>The lobby's own files in the shared img-cache (before vr-lobby): what each name starts with.</summary>
    static readonly string[] PicturePrefixes = ["vpx-", "bgvpx-", "bpvpx-", "b2s-"];

    static readonly object Gate = new();
    static string? _migratedFor;

    /// <summary>Asked for a folder: the caches from before vr-lobby are moved in first (once per data folder).</summary>
    static string Ready(string dir)
    {
        Migrate();
        return dir;
    }

    /// <summary>
    /// Moves the caches from where they were before vr-lobby (cabinet-cache, attract-audio and the lobby's files in
    /// img-cache), so nothing has to be read or recorded again. Safe to run any number of times and from the lobby and
    /// the manager at once: each thing is moved only if it isn't there yet, one at a time, and one that can't be (in
    /// use) is left for next time. Never throws.
    /// </summary>
    public static void Migrate()
    {
        var data = AppSettings.DataDir;
        lock (Gate)
        {
            if (_migratedFor == data)
            {
                return;
            }

            _migratedFor = data;
        }

        try
        {
            var root = Path.Combine(data, "vr-lobby");
            MoveFolder(Path.Combine(data, "cabinet-cache"), Path.Combine(root, "cabinets"), null);
            MoveFolder(
                Path.Combine(data, "attract-audio"),
                Path.Combine(root, "attract-audio"),
                null
            );
            MoveFolder(
                Path.Combine(data, "img-cache"),
                Path.Combine(root, "pictures"),
                name =>
                    PicturePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            );
        }
        catch
        {
            // A cache that stays where it was is read again when needed: never worth stopping for.
        }
    }

    /// <summary>
    /// Moves the files of <paramref name="from"/> (those <paramref name="take"/> picks, or all) into
    /// <paramref name="to"/>, and removes <paramref name="from"/> when it's moved whole and left empty.
    /// </summary>
    static void MoveFolder(string from, string to, Func<string, bool>? take)
    {
        if (!Directory.Exists(from))
        {
            return;
        }

        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from).ToList())
        {
            var name = Path.GetFileName(file);
            if (take != null && !take(name))
            {
                continue;
            }

            try
            {
                var dest = Path.Combine(to, name);
                if (File.Exists(dest))
                {
                    File.Delete(file); // already there (moved before, or made since): the copy left behind goes
                }
                else
                {
                    File.Move(file, dest);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (take == null && !Directory.EnumerateFileSystemEntries(from).Any())
        {
            try
            {
                Directory.Delete(from);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
