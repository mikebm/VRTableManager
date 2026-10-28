using System.Runtime.InteropServices;

namespace VrTableManager.Services;

/// <param name="Name">PinMAME's name for the game (the ROM set).</param>
/// <param name="CloneOf">The parent set a clone takes missing files from, or null.</param>
/// <param name="Found">Every file the game needs is there (in its own zip or its parent's).</param>
public sealed record PinmameGame(string Name, string? CloneOf, string Description, bool Found);

/// <summary>
/// Asks VPX's own PinMAME (plugins\pinmame\pinmame64.dll) about ROM sets: whether a name is a real
/// game, which parent set a clone needs, and whether a table's pinmame\roms folder has everything.
/// That's the same check PinMAME makes when the table starts, so the app can say what's missing
/// before you're in the headset. Returns nothing (never throws) when VPX or the DLL isn't there.
/// </summary>
public static class PinmameInfo
{
    const int StatusOk = 0;

    /// <summary>The game, looking in <paramref name="pinmameDir"/> (a table's pinmame folder) for its ROMs.</summary>
    public static PinmameGame? Get(AppSettings s, string pinmameDir, string game) =>
        Query(RomAudio.PinmameDll(s), pinmameDir, [game]).GetValueOrDefault(game);

    /// <summary>The game and its parents, nearest first (e.g. afm_113b, afm_113).</summary>
    public static List<PinmameGame> Family(AppSettings s, string pinmameDir, string game)
    {
        var dll = RomAudio.PinmameDll(s);
        var chain = new List<PinmameGame>();
        for (var name = game; name != null && chain.Count < 4 && !chain.Any(g => g.Name == name);)
        {
            if (Query(dll, pinmameDir, [name]).GetValueOrDefault(name) is not { } g) break;
            chain.Add(g);
            name = g.CloneOf;
        }
        return chain;
    }

    /// <summary>Which of these names PinMAME knows (the rest are aliases, typos or not ROM names at all).</summary>
    public static Dictionary<string, PinmameGame> Query(string dll, string pinmameDir, IReadOnlyCollection<string> names)
    {
        var result = new Dictionary<string, PinmameGame>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows() || !File.Exists(dll) || names.Count == 0) return result;
        lock (RomAudio.OneAtATime)
        {
            try
            {
                var lib = NativeLibrary.Load(dll);
                var setConfig = Marshal.GetDelegateForFunctionPointer<SetConfigFn>(NativeLibrary.GetExport(lib, "PinmameSetConfig"));
                var getGame = Marshal.GetDelegateForFunctionPointer<GetGameFn>(NativeLibrary.GetExport(lib, "PinmameGetGame"));
                var dir = pinmameDir.EndsWith('\\') || pinmameDir.EndsWith('/') ? pinmameDir : pinmameDir + Path.DirectorySeparatorChar;
                var config = new Config { SampleRate = 48000, VpmPath = dir };
                setConfig(ref config);
                PinmameGame? last = null;
                GameCallback cb = (ref Game g, IntPtr _) => last = new PinmameGame(
                    Marshal.PtrToStringAnsi(g.Name) ?? "", Marshal.PtrToStringAnsi(g.CloneOf) is { Length: > 0 } c ? c : null,
                    Marshal.PtrToStringAnsi(g.Description) ?? "", g.Found != 0);
                foreach (var n in names)
                {
                    last = null;
                    if (getGame(n, cb, IntPtr.Zero) == StatusOk && last != null) result[n] = last;
                }
                GC.KeepAlive(cb);
            }
            catch { /* old or missing DLL: no answers */ }
        }
        return result;
    }

    // libpinmame.h: PinmameConfig (only the path matters here; callbacks stay null) and PinmameGame.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct Config
    {
        public int AudioFormat, SampleRate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string VpmPath;
        public IntPtr OnStateUpdated, OnDisplayAvailable, OnDisplayUpdated, OnAudioAvailable, OnAudioUpdated,
            OnMechAvailable, OnMechUpdated, OnSolenoidUpdated, OnConsoleDataUpdated, IsKeyPressed, OnLogMessage, OnSoundCommand;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Game
    {
        public IntPtr Name, CloneOf, Description, Year, Manufacturer;
        public uint Flags;
        public int Found;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetConfigFn(ref Config config);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void GameCallback(ref Game game, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetGameFn([MarshalAs(UnmanagedType.LPStr)] string name, GameCallback cb, IntPtr user);
}
