namespace VrTableManager.Lobby;

/// <summary>
/// Where the VR lobby keeps each table's 3D toys once read (VrLobby's ToyCache, one ".toys" file per table under
/// vr-lobby\toys), and keeping that folder within the size chosen on Setup (<see cref="Services.AppSettings.LobbyToyCacheGb"/>):
/// the tables used longest ago go first, and are read again from their table the next time they're picked.
/// </summary>
public static class ToyFiles
{
    public static string Dir => Path.Combine(LobbyFiles.Root, "toys");

    /// <summary>The file holding one table's toys (named like its cabinet's, see <see cref="CabinetFiles.FileFor"/>).</summary>
    public static string FileFor(string vpx, string? dir = null) =>
        Path.ChangeExtension(CabinetFiles.FileFor(vpx, dir ?? Dir), ".toys");

    /// <summary>Deletes one table's saved toys (My Tables → MORE); the lobby reads them again when it's next picked.</summary>
    public static bool Clear(string vpx)
    {
        try
        {
            var file = FileFor(vpx);
            if (!File.Exists(file))
            {
                return false;
            }

            File.Delete(file);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Deletes every table's saved toys (Setup → VR lobby); returns how many bytes that freed.</summary>
    public static long ClearAll(string? dir = null)
    {
        long freed = 0;
        try
        {
            foreach (var f in Files(dir ?? Dir).ToList())
            {
                try
                {
                    var size = f.Length;
                    f.Delete();
                    freed += size;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return freed;
    }

    /// <summary>
    /// Deletes the saved toys of these tables that <paramref name="isCurrent"/> says can't be used any more (the table
    /// changed since, another budget, an older format): they'd be read again anyway, and till then they count against
    /// the size chosen on Setup. PREPARE TABLES does this before it works out the room left; without it, toys saved for
    /// tables updated while toys were off filled that room, and nothing was prepared until the cache was cleared.
    /// Returns how many files went. Never throws: a file in use is skipped.
    /// </summary>
    public static int DropStale(
        IEnumerable<string> vpxs,
        Func<string, bool> isCurrent,
        string? dir = null
    )
    {
        dir ??= Dir;
        var dropped = 0;
        foreach (var vpx in vpxs)
        {
            try
            {
                var file = FileFor(vpx, dir);
                if (File.Exists(file) && !isCurrent(vpx))
                {
                    File.Delete(file);
                    dropped++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return dropped;
    }

    /// <summary>The sizes Setup offers, in GB; 0 is no limit.</summary>
    public static readonly IReadOnlyList<int> LimitsGb = [0, 1, 2, 5, 10, 20, 50];

    public static long Bytes(int gb) => gb <= 0 ? 0 : gb * 1024L * 1024 * 1024;

    static IEnumerable<FileInfo> Files(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*.toys") : [];

    /// <summary>How much the toys take now, in bytes.</summary>
    public static long SizeBytes(string? dir = null)
    {
        try
        {
            return Files(dir ?? Dir).Sum(f => f.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Marks a table's toys as just used (the file's date; the file itself says which table version it holds).</summary>
    public static void MarkUsed(string file)
    {
        try
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Deletes the toys used longest ago until the folder is within <paramref name="limitBytes"/> (0: no limit, nothing
    /// deleted). <paramref name="keep"/> (the file just written or on show) is never deleted, even if it alone is over.
    /// Returns how many files went. Never throws: a file in use is skipped.
    /// </summary>
    public static int Prune(long limitBytes, string? keep = null, string? dir = null)
    {
        if (limitBytes <= 0)
        {
            return 0;
        }

        try
        {
            var files = Files(dir ?? Dir).OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            var deleted = 0;
            foreach (var f in files)
            {
                if (total <= limitBytes)
                {
                    break;
                }

                if (
                    keep != null
                    && string.Equals(
                        f.FullName,
                        Path.GetFullPath(keep),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    continue;
                }

                try
                {
                    var size = f.Length; // before deleting: afterwards it throws, the file being gone
                    f.Delete();
                    total -= size;
                    deleted++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return deleted;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
