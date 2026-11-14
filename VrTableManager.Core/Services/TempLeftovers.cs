using System.Text.RegularExpressions;

namespace VrTableManager.Services;

/// <summary>
/// The app's work folders in Temp (vrtm-…) are deleted when the work is done, but that delete can't always
/// happen: a table install's unpacked download stays when a file in it is still open, and the lobby's ROM
/// recordings (vrtm-romaudio-…) stay while PinMAME still holds the ROM zips. They were never cleared after
/// that, hundreds of MB at a time. So at start-up, those older than a day are deleted.
/// <para>
/// Not deleted: vrtm-ini-….ini, your table .ini kept there when it couldn't be put back after an update (the
/// only copy left), and anything else in Temp.
/// </para>
/// </summary>
public static partial class TempLeftovers
{
    /// <summary>Deletes this app's work folders and files in <paramref name="temp"/> last written before
    /// <paramref name="olderThan"/> ago. Returns the bytes freed. Never throws; one in use is passed over.</summary>
    public static long Sweep(string temp, TimeSpan olderThan)
    {
        long freed = 0;
        var before = DateTime.Now - olderThan;
        List<string> dirs,
            files;
        try
        {
            dirs = Directory.EnumerateDirectories(temp, "vrtm-*").ToList();
            files = Directory.EnumerateFiles(temp, "vrtm-*").ToList();
        }
        catch (Exception)
        {
            return 0; // Temp unreadable: nothing to do
        }

        // Each on its own: one that vanishes or can't be read mid-sweep doesn't stop the rest.
        foreach (var dir in dirs)
        {
            try
            {
                if (WorkFolder().IsMatch(Path.GetFileName(dir)) && Newest(dir) < before)
                {
                    var size = DiskUsage.SizeOf(dir);
                    Directory.Delete(dir, recursive: true);
                    freed += size;
                }
            }
            catch (Exception) { } // in use or gone: passed over (what could go, went)
        }
        foreach (var file in files)
        {
            try
            {
                if (
                    WorkFile().IsMatch(Path.GetFileName(file))
                    && File.GetLastWriteTime(file) < before
                )
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    freed += size;
                }
            }
            catch (Exception) { }
        }
        return freed;
    }

    /// <summary>The newest write in the folder (a folder's own time doesn't change when a file deep in it does).</summary>
    static DateTime Newest(string dir)
    {
        var newest = Directory.GetLastWriteTime(dir);
        foreach (
            var f in new DirectoryInfo(dir).EnumerateFileSystemInfos(
                "*",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }
            )
        )
        {
            if (f.LastWriteTime > newest)
            {
                newest = f.LastWriteTime;
            }
        }
        return newest;
    }

    // vrtm-1a2b3c4d (an install), vrtm-romaudio-1a2b3c4d, vrtm-rom-…, vrtm-stern-…, vrtm-uevr-…, an add-on's
    // vrtm-<tag>-…, vrtm-update-1.2.3, and the smoke tests' vrtm-smoke-1a2b3c.
    [GeneratedRegex(
        @"^vrtm-(?:[a-z]+-)?[0-9a-f]{6,8}$|^vrtm-update-[\w.]+$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex WorkFolder();

    // A playfield video's frames source (vrtm-1a2b3c4d.mp4); never vrtm-ini-….ini.
    [GeneratedRegex(@"^vrtm-[0-9a-f]{8}\.mp4$", RegexOptions.IgnoreCase)]
    private static partial Regex WorkFile();
}
