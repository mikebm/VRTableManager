using System.Runtime.InteropServices;

namespace VrTableManager.Services;

/// <summary>
/// Loads a DLL that belongs to another program (VPX's OpenXR loader or PinMAME, UEVR's loader) from a private copy
/// instead of where it lives. A loaded DLL stays locked until the manager exits (freeing one isn't safe while it may
/// still have threads running), and locking the files in the VPX folder made VPX updates fail with "being used by
/// another process" after a VR launch or a ROM check. The copy is keyed on the original's size and time, so an
/// updated DLL gets a fresh copy; every caller asking for the same original gets the same copy, and so the same
/// loaded module. Falls back to the original if the copy can't be made or won't load (antivirus, a policy that
/// blocks DLLs in Temp, a damaged copy).
/// </summary>
public static class NativeCopy
{
    public static IntPtr Load(string path)
    {
        var copy = PathFor(path);
        if (copy == path)
        {
            return NativeLibrary.Load(path);
        }

        try
        {
            return NativeLibrary.Load(copy);
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
        {
            TryDelete(Path.GetDirectoryName(copy)!); // damaged or blocked: the next run makes a new one
            return NativeLibrary.Load(path);
        }
    }

    public static string PathFor(string path)
    {
        try
        {
            RemoveOldOnce();
            var src = new FileInfo(path);
            var dir = Path.Combine(Root, $"{src.Length:x}-{src.LastWriteTimeUtc.Ticks:x}");
            var copy = Path.Combine(dir, src.Name);
            if (!File.Exists(copy) || new FileInfo(copy).Length != src.Length)
            {
                Directory.CreateDirectory(dir);
                var tmp = copy + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.Copy(path, tmp, overwrite: true);
                try
                {
                    File.Move(tmp, copy, overwrite: true);
                }
                catch (Exception e)
                    when ((e is IOException or UnauthorizedAccessException) && File.Exists(copy))
                {
                    // that copy is already loaded (an earlier call this run): it's the same file
                    TryDelete(tmp);
                }
            }
            return copy;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    static readonly string Root = Path.Combine(Path.GetTempPath(), "VrTableManager", "native");

    static int _cleaned;

    /// <summary>
    /// Once a run, before the first copy: deletes copies from earlier runs (older DLL versions, half-made copies left
    /// by a crash). Only folders untouched for a day go, so one another manager has just made and is about to load
    /// is never pulled from under it; one still loaded by a running manager is locked, can't be deleted, and is
    /// skipped. Never throws.
    /// </summary>
    static void RemoveOldOnce()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) != 0)
        {
            return;
        }

        try
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (var dir in Directory.EnumerateDirectories(Root).ToList())
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                {
                    TryDelete(dir);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the temp folder can't be read: leave it
        }
    }

    static void TryDelete(string fileOrDir)
    {
        try
        {
            if (Directory.Exists(fileOrDir))
            {
                Directory.Delete(fileOrDir, recursive: true);
            }
            else
            {
                File.Delete(fileOrDir);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // in use (a loaded copy) or not ours to delete: leave it
        }
    }
}
