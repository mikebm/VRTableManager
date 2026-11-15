using System.Runtime.InteropServices;

namespace VrTableManager.Services;

/// <summary>
/// Loads a DLL that belongs to another program (VPX's OpenXR loader or PinMAME, UEVR's loader) from a private copy
/// instead of where it lives. A loaded DLL stays locked until the manager exits (freeing one isn't safe while it may
/// still have threads running), and locking the files in the VPX folder made VPX updates fail with "being used by
/// another process" after a VR launch or a ROM check. The copy is keyed on the original's size and time, so an
/// updated DLL gets a fresh copy; every caller asking for the same original gets the same copy, and so the same
/// loaded module. Falls back to the original if the copy can't be made.
/// </summary>
public static class NativeCopy
{
    public static IntPtr Load(string path) => NativeLibrary.Load(PathFor(path));

    public static string PathFor(string path)
    {
        try
        {
            var src = new FileInfo(path);
            var dir = Path.Combine(Root, $"{src.Length:x}-{src.LastWriteTimeUtc.Ticks:x}");
            var copy = Path.Combine(dir, src.Name);
            lock (InUse)
            {
                InUse.Add(dir);
            }
            RemoveOld(src.Name);
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
                    File.Delete(tmp);
                }
            }
            return copy;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }
}
