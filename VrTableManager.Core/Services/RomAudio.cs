using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using OpenMcdf;

namespace VrTableManager.Services;

/// <summary>
/// A table's own sound for the VR lobby, recorded from its ROM: ROM tables have no music files, their
/// sound comes from the emulated machine. This runs the ROM in PinMAME (VPX's own pinmame64.dll),
/// drops coins, presses Start and keeps the game-start music as a WAV in
/// %APPDATA%\VrTableManager\attract-audio. It runs in real time (about 40 s) and works on a scratch
/// copy of the ROM with empty nvram, so the table's own scores, credits and settings are never touched.
/// Coin and Start go in as the keys VPinMAME uses ("5" and "1"), so it works for any machine type.
/// </summary>
public static partial class RomAudio
{
    public static string CacheDir => Path.Combine(AppSettings.DataDir, "attract-audio");

    /// <summary>The recording for this ROM, and the marker left when a ROM gave nothing worth keeping.</summary>
    public static string FileFor(string rom) => Path.Combine(CacheDir, rom.ToLowerInvariant() + ".wav");
    static string NoneFor(string rom) => Path.Combine(CacheDir, rom.ToLowerInvariant() + ".none");

    /// <summary>The ROM this table loads, if it's in the table's pinmame/roms folder.</summary>
    public static string? RomOf(InstalledTable table) =>
        table.IsVpx ? table.Manifest.RomNames.FirstOrDefault(r => File.Exists(Path.Combine(table.Folder, "pinmame", "roms", r + ".zip"))) : null;

    /// <summary>The recorded sound for this table, if there is one.</summary>
    public static string? Cached(InstalledTable table) => RomOf(table) is { } rom && File.Exists(FileFor(rom)) ? FileFor(rom) : null;

    /// <summary>True when this table has a ROM that hasn't been recorded (or tried) yet.</summary>
    public static bool NeedsRecording(InstalledTable table) =>
        RomOf(table) is { } rom && !File.Exists(FileFor(rom)) && !File.Exists(NoneFor(rom));

    public static string PinmameDll(AppSettings settings) => Path.Combine(settings.VpxDir, "plugins", "pinmame", "pinmame64.dll");

    // When things happen, in seconds after the ROM starts: boot (Stern SAM takes longest), Escape, coins, Start.
    const double EscapeAt = 12, CoinAt = 20, StartAt = 24, KeepFor = 25;
    const double Total = StartAt + KeepFor;
    const int SampleRate = 44100;

    /// <summary>
    /// Records this table's ROM into the cache. Blocks for about 40 s; one at a time per process
    /// (PinMAME is a single instance). Returns the WAV, or null when the ROM is missing, didn't run,
    /// or stayed silent (remembered, so it isn't tried again).
    /// </summary>
    public static string? Record(InstalledTable table, string pinmameDll, Action<string>? log = null)
    {
        var rom = RomOf(table);
        if (rom == null || !File.Exists(pinmameDll)) return null;
        var scratch = Path.Combine(Path.GetTempPath(), "vrtm-romaudio-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // Every zip from the table (PinMAME clones need their parent set beside them), fresh nvram.
            var roms = Directory.CreateDirectory(Path.Combine(scratch, "roms")).FullName;
            Directory.CreateDirectory(Path.Combine(scratch, "nvram"));
            Directory.CreateDirectory(Path.Combine(scratch, "cfg"));
            foreach (var zip in Directory.EnumerateFiles(Path.Combine(table.Folder, "pinmame", "roms"), "*.zip"))
                File.Copy(zip, Path.Combine(roms, Path.GetFileName(zip)));

            var (samples, channels, rate) = Run(pinmameDll, scratch + Path.DirectorySeparatorChar, rom, TroughSwitches(table.TablePath), log);
            Directory.CreateDirectory(CacheDir);
            var clip = Clip(samples, channels, rate);
            if (clip == null)
            {
                log?.Invoke($"{rom}: nothing to keep ({samples.Length / Math.Max(1, channels) / (double)rate:F1} s recorded)");
                File.WriteAllText(NoneFor(rom), "");
                return null;
            }
            var file = FileFor(rom);
            WriteWav(file, clip, channels, rate);
            return file;
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch { }
        }
    }

    /// <summary>
    /// From Start onwards (the coin chimes before it are dropped), cut at the first long silence after
    /// the music, normalised and faded out. Null when there's hardly any sound.
    /// </summary>
    public static short[]? Clip(short[] samples, int channels, int rate)
    {
        var from = Math.Min(samples.Length, (int)((StartAt - 0.3) * rate) * channels);
        var clip = samples.AsSpan(from).ToArray();
        // Loudness per 100 ms block.
        var block = rate / 10 * channels;
        var blocks = clip.Length / block;
        if (blocks == 0) return null;
        var rms = new double[blocks];
        for (var b = 0; b < blocks; b++)
        {
            double sum = 0;
            for (var i = b * block; i < (b + 1) * block; i++) sum += (double)clip[i] * clip[i];
            rms[b] = Math.Sqrt(sum / block);
        }
        const double Quiet = 15; // about -67 dBFS: emulated machines play quietly, the clip is normalised below
        var loud = rms.Count(r => r > Quiet);
        if (loud < 20) return null; // under 2 s of sound
        // Start at the first sound, end after the last block before 3 s of quiet.
        var first = Array.FindIndex(rms, r => r > Quiet);
        var last = first;
        for (var b = first; b < blocks; b++)
        {
            if (rms[b] > Quiet) last = b;
            else if (b - last >= 30) break;
        }
        var start = first * block;
        var end = Math.Min(clip.Length, (last + 3) * block);
        var cut = clip.AsSpan(start, end - start).ToArray();

        var peak = cut.Max(s => Math.Abs((int)s));
        var gain = peak > 0 ? Math.Min(32.0, 0.8 * short.MaxValue / peak) : 1;
        var fade = Math.Min(cut.Length, rate * channels); // last second
        for (var i = 0; i < cut.Length; i++)
        {
            var g = gain;
            var left = cut.Length - i;
            if (left < fade) g *= (double)left / fade;
            cut[i] = (short)Math.Clamp(cut[i] * g, short.MinValue, short.MaxValue);
        }
        return cut;
    }

    /// <summary>
    /// The switches that see balls sitting in the trough, read from the table script: a machine started
    /// with an empty trough says "locating pinballs" and plays nothing. Scripts name them in one of a few
    /// ways: <c>bsTrough.InitSw 0, 21, 20, 19, 18, …</c> (the first number is the entry switch), or
    /// <c>Sub sw32_Hit: Controller.Switch(32) = 1: UpdateTrough</c>, or <c>Sub Trough2_Hit: Controller.Switch(16) = 1</c>,
    /// or for a cvpmTrough <c>With bsTrough … .initSwitches Array(32, 33, 34, 35)</c>.
    /// </summary>
    public static IReadOnlyList<int> TroughSwitches(string vpxPath)
    {
        try { return TroughSwitchesIn(Script(vpxPath)); }
        catch { return []; }
    }

    public static IReadOnlyList<int> TroughSwitchesIn(string script)
    {
        var found = new List<int>();
        var inTroughWith = false;
        foreach (var raw in script.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("With ", StringComparison.OrdinalIgnoreCase)) inTroughWith = line.Contains("trough", StringComparison.OrdinalIgnoreCase);
            else if (line.StartsWith("End With", StringComparison.OrdinalIgnoreCase)) inTroughWith = false;
            if (line.StartsWith('\'') || !(inTroughWith || line.Contains("trough", StringComparison.OrdinalIgnoreCase))) continue;
            if (InitSwitches().Match(line) is { Success: true } array)
                found.AddRange(array.Groups[1].Value.Split(',').Select(x => int.TryParse(x.Trim(), out var n) ? n : 0));
            else if (InitSw().Match(line) is { Success: true } init)
                found.AddRange(init.Groups[1].Value.Split(',').Skip(1).Select(x => int.TryParse(x.Trim(), out var n) ? n : 0));
            else if (line.Contains("_Hit", StringComparison.OrdinalIgnoreCase))
                found.AddRange(SwitchOn().Matches(line).Select(m => int.Parse(m.Groups[1].Value)));
        }
        return found.Where(n => n > 0).Distinct().Take(8).ToList();
    }

    [GeneratedRegex(@"\.InitSw\s*\(?\s*([\d\s,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex InitSw();
    [GeneratedRegex(@"\.InitSwitches\s*\(?\s*Array\s*\(([\d\s,]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex InitSwitches();
    [GeneratedRegex(@"Controller\.Switch\s*\(\s*(\d+)\s*\)\s*=\s*1\b", RegexOptions.IgnoreCase)]
    private static partial Regex SwitchOn();

    /// <summary>The table script: the CODE record of GameStg/GameData.</summary>
    static string Script(string vpxPath)
    {
        using var root = RootStorage.OpenRead(vpxPath);
        using var s = root.OpenStorage("GameStg").OpenStream("GameData");
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        for (var i = 0; i + 8 <= bytes.Length;)
        {
            var len = BitConverter.ToInt32(bytes, i);
            if (len < 4 || i + 4 + len > bytes.Length) break;
            if (Encoding.ASCII.GetString(bytes, i + 4, 4) == "CODE")
            {
                // CODE is the one record whose length field doesn't cover its data: a second length follows.
                var codeLen = BitConverter.ToInt32(bytes, i + 8);
                return Encoding.Latin1.GetString(bytes, i + 12, Math.Min(codeLen, bytes.Length - i - 12));
            }
            i += 4 + len;
        }
        return "";
    }

    internal static void WriteWav(string file, short[] samples, int channels, int rate)
    {
        var tmp = file + ".tmp";
        using (var w = new BinaryWriter(File.Create(tmp)))
        {
            var bytes = samples.Length * 2;
            w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate);
            w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
            w.Write("data"u8); w.Write(bytes);
            w.Write(MemoryMarshal.AsBytes(samples.AsSpan()));
        }
        File.Move(tmp, file, overwrite: true);
    }

    // ---- libpinmame -------------------------------------------------------------------

    /// <summary>libpinmame has one global state: one user at a time (also <see cref="PinmameInfo"/>).</summary>
    internal static readonly object OneAtATime = new();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct Config
    {
        public int AudioFormat;   // 0: 16-bit
        public int SampleRate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string VpmPath;
        public IntPtr OnStateUpdated, OnDisplayAvailable, OnDisplayUpdated, OnAudioAvailable, OnAudioUpdated,
            OnMechAvailable, OnMechUpdated, OnSolenoidUpdated, OnConsoleDataUpdated, IsKeyPressed, OnLogMessage, OnSoundCommand;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AudioInfo
    {
        public int Format, Channels;
        public double SampleRate, FramesPerSecond;
        public int SamplesPerFrame, BufferSize;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AudioAvailableFn(ref AudioInfo info, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AudioUpdatedFn(IntPtr buffer, int samples, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DisplayUpdatedFn(int index, IntPtr data, IntPtr layout, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IsKeyPressedFn(int key, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetConfigFn(ref Config config);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int RunFn([MarshalAs(UnmanagedType.LPStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VoidFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IntFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetIntFn(int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetSwitchFn(int sw, int state);

    // PINMAME_KEYCODE (same numbering as MAME's): VPinMAME's 7 is the service "Escape" button,
    // 5 a coin and 1 Start.
    const int KeyNumber1 = 27, KeyNumber5 = 31, KeyNumber7 = 33;

    /// <summary>
    /// Runs the ROM for <see cref="Total"/> seconds and returns its sound. Fresh nvram makes many machines
    /// stop on a "factory settings restored / totals cleared" message until a service button is pressed,
    /// so Escape goes first; then coins and Start. When <paramref name="log"/> is given, the main display's
    /// last picture is written to it as text, which shows where a silent ROM got stuck.
    /// </summary>
    static (short[] Samples, int Channels, int Rate) Run(string dll, string vpmPath, string rom, IReadOnlyList<int> balls, Action<string>? log)
    {
        lock (OneAtATime)
        {
            var lib = NativeLibrary.Load(dll);
            T Fn<T>(string name) => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
            var setConfig = Fn<SetConfigFn>("PinmameSetConfig");
            var run = Fn<RunFn>("PinmameRun");
            var stop = Fn<VoidFn>("PinmameStop");
            var isRunning = Fn<IntFn>("PinmameIsRunning");
            var handleKeyboard = Fn<SetIntFn>("PinmameSetHandleKeyboard");
            var handleMechanics = Fn<SetIntFn>("PinmameSetHandleMechanics");
            var setSwitch = Fn<SetSwitchFn>("PinmameSetSwitch");

            var audio = new List<short>(SampleRate * 2 * (int)Total);
            int channels = 1, rate = SampleRate;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            AudioAvailableFn onAvailable = (ref AudioInfo info, IntPtr _) =>
            {
                channels = Math.Max(1, info.Channels);
                rate = (int)info.SampleRate;
                return info.SamplesPerFrame;
            };
            AudioUpdatedFn onUpdated = (buffer, samples, _) =>
            {
                var n = samples * channels;
                var chunk = new short[n];
                Marshal.Copy(buffer, chunk, 0, n);
                lock (audio) audio.AddRange(chunk);
                return samples;
            };
            byte[]? display = null;
            int displayWidth = 0;
            DisplayUpdatedFn onDisplay = (index, data, layout, _) =>
            {
                if (index != 0 || data == IntPtr.Zero || log == null) return;
                var l = Marshal.PtrToStructure<DisplayLayout>(layout);
                if (l.Width <= 0 || l.Height <= 0 || l.Width > 256 || l.Height > 64) return;
                var pixels = new byte[l.Width * l.Height];
                Marshal.Copy(data, pixels, 0, pixels.Length);
                (display, displayWidth) = (pixels, l.Width);
            };
            IsKeyPressedFn isKeyPressed = (key, _) =>
            {
                var t = clock.Elapsed.TotalSeconds;
                return key switch
                {
                    KeyNumber7 => Pressed(t, EscapeAt) || Pressed(t, EscapeAt + 1),
                    // Four coins (enough credits whatever the pricing), then Start.
                    KeyNumber5 => Pressed(t, CoinAt) || Pressed(t, CoinAt + 0.6) || Pressed(t, CoinAt + 1.2) || Pressed(t, CoinAt + 1.8),
                    KeyNumber1 => Pressed(t, StartAt),
                    _ => false,
                } ? 1 : 0;
            };
            var config = new Config
            {
                AudioFormat = 0,
                SampleRate = SampleRate,
                VpmPath = vpmPath,
                OnDisplayUpdated = Marshal.GetFunctionPointerForDelegate(onDisplay),
                OnAudioAvailable = Marshal.GetFunctionPointerForDelegate(onAvailable),
                OnAudioUpdated = Marshal.GetFunctionPointerForDelegate(onUpdated),
                IsKeyPressed = Marshal.GetFunctionPointerForDelegate(isKeyPressed),
            };
            try
            {
                setConfig(ref config);
                handleKeyboard(1);
                handleMechanics(1);
                var status = run(rom);
                if (status != 0) { log?.Invoke($"{rom}: PinMAME didn't start it (status {status})"); return ([], 1, SampleRate); }
                clock.Restart();
                var ballsIn = false;
                while (clock.Elapsed.TotalSeconds < Total && isRunning() != 0)
                {
                    // Balls waiting in the trough, so Start starts a game. Not before the machine is up
                    // (setting switches right after PinmameRun crashes it).
                    if (!ballsIn && clock.Elapsed.TotalSeconds > 2)
                    {
                        foreach (var sw in balls) setSwitch(sw, 1);
                        ballsIn = true;
                    }
                    Thread.Sleep(100);
                }
                stop();
                for (var i = 0; i < 50 && isRunning() != 0; i++) Thread.Sleep(100);
            }
            finally
            {
                GC.KeepAlive(onAvailable);
                GC.KeepAlive(onUpdated);
                GC.KeepAlive(onDisplay);
                GC.KeepAlive(isKeyPressed);
            }
            if (display != null && log != null)
                for (var y = 0; y < display.Length / displayWidth; y++)
                    log(new string(display.AsSpan(y * displayWidth, displayWidth).ToArray().Select(p => p > 0 ? '#' : ' ').ToArray()).TrimEnd());
            lock (audio) return (audio.ToArray(), channels, rate);
        }

        static bool Pressed(double t, double at) => t >= at && t < at + 0.25;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DisplayLayout
    {
        public int Type, Top, Left, Length, Width, Height, Depth;
    }
}
