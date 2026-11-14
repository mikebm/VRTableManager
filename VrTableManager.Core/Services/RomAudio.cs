using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using OpenMcdf;

namespace VrTableManager.Services;

/// <summary>
/// A table's own sound for the VR lobby, recorded from its ROM: ROM tables have no music files, their
/// sound comes from the emulated machine. This runs the ROM in PinMAME (VPX's own pinmame64.dll),
/// drops coins, presses Start and keeps the game-start music as a WAV in
/// %APPDATA%\VrTableManager\vr-lobby\attract-audio. It runs in real time (about 25 s plus the clip's length) and
/// works on a scratch copy of the ROM with empty nvram, so the table's own scores, credits and settings are never
/// touched. Coin and Start go in as the keys VPinMAME uses ("5" and "1"), so it works for any machine type.
/// PinMAME is one instance per process, so recordings run in parallel only as separate processes
/// (<see cref="RomAudioBatch"/>): each has its own scratch folder and writes its WAV through a temp file of its own.
/// </summary>
public static partial class RomAudio
{
    public static string CacheDir => Lobby.LobbyFiles.AttractAudio;

    /// <summary>How long a clip may be (AppSettings.AttractMusicSeconds), and how long the lobby plays a table's music.</summary>
    public const int MinSeconds = 5,
        MaxSeconds = 30,
        DefaultSeconds = 10;

    /// <summary>Recordings from before clips had a length kept up to 25 s after Start, as &lt;rom&gt;.wav.</summary>
    public const int LegacySeconds = 25;

    /// <summary>The lengths Setup offers.</summary>
    public static IReadOnlyList<int> Lengths { get; } = [5, 8, 10, 15, 20, 25, 30];

    public static int ClampSeconds(int seconds) =>
        seconds <= 0 ? DefaultSeconds : Math.Clamp(seconds, MinSeconds, MaxSeconds);

    /// <summary>
    /// Turns a recorded WAV into an MP3 (first path in, second out), true when it was written and checked. Set by the
    /// manager (Windows' own Media Foundation encoder); without it, clips stay WAVs.
    /// </summary>
    public static Func<string, string, bool>? ToMp3 { get; set; }

    /// <summary>
    /// The recording of this ROM at this length: the length and the format are part of the name (afm_113-10s.mp3),
    /// so a new length is recorded again and an old WAV never stands in the way of its MP3. 25 s keeps the name
    /// from before lengths (afm_113.wav, afm_113.mp3), so those recordings still count.
    /// </summary>
    public static string FileFor(string rom, int seconds, string ext = ".mp3")
    {
        seconds = ClampSeconds(seconds);
        var name = rom.ToLowerInvariant();
        return Path.Combine(
            CacheDir,
            (seconds == LegacySeconds ? name : $"{name}-{seconds}s") + ext
        );
    }

    /// <summary>The recording of this ROM at exactly this length (MP3 first, else a WAV), or null.</summary>
    static string? ExactFor(string rom, int seconds) =>
        new[] { FileFor(rom, seconds), FileFor(rom, seconds, ".wav") }.FirstOrDefault(File.Exists);

    /// <summary>The length a cached file's name says it holds, or null when it isn't one of this ROM's clips.</summary>
    public static int? SecondsOf(string file, string rom)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is not (".mp3" or ".wav"))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        rom = rom.ToLowerInvariant();
        if (name == rom)
        {
            return LegacySeconds;
        }

        return
            name.StartsWith(rom + "-", StringComparison.Ordinal)
            && name.EndsWith('s')
            && int.TryParse(name[(rom.Length + 1)..^1], out var s)
            ? s
            : null;
    }

    /// <summary>Every recording of this ROM, whatever its length and format.</summary>
    static List<(string File, int Seconds)> AllFor(string rom)
    {
        if (!Directory.Exists(CacheDir))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(CacheDir, rom.ToLowerInvariant() + "*.*")
            .Select(f => (File: f, Seconds: SecondsOf(f, rom)))
            .Where(x => x.Seconds != null)
            .Select(x => (x.File, x.Seconds!.Value))
            .ToList();
    }

    static string NoneFor(string rom) => Path.Combine(CacheDir, rom.ToLowerInvariant() + ".none");

    /// <summary>Left when a ROM's PinMAME didn't stop after recording and gave no sound: it isn't tried again.</summary>
    static string StuckFor(string rom) => Path.Combine(CacheDir, rom.ToLowerInvariant() + ".stuck");

    /// <summary>The ROM this table loads, if it's in the table's pinmame/roms folder.</summary>
    public static string? RomOf(InstalledTable table) =>
        table.IsVpx
            ? table.Manifest.RomNames.FirstOrDefault(r =>
                File.Exists(Path.Combine(table.Folder, "pinmame", "roms", r + ".zip"))
            )
            : null;

    /// <summary>
    /// The recorded sound for this table: the one at this length, else one at another length (it plays until it's
    /// recorded again, cut to the length), so changing the length never silences a table meanwhile.
    /// </summary>
    public static string? Cached(InstalledTable table, int seconds)
    {
        if (RomOf(table) is not { } rom)
        {
            return null;
        }

        return ExactFor(rom, seconds)
            ?? AllFor(rom)
                .OrderByDescending(x => x.Seconds)
                .ThenBy(x =>
                    Path.GetExtension(x.File).Equals(".mp3", StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : 1
                )
                .Select(x => x.File)
                .FirstOrDefault();
    }

    /// <summary>
    /// Deletes half-written clips left in the cache by recordings that were killed (a closed lobby, a cancelled batch):
    /// <c>.tmp</c> files over 2 hours old, so one being written right now is never touched. Returns how many went.
    /// </summary>
    public static int CleanLeftovers()
    {
        if (!Directory.Exists(CacheDir))
        {
            return 0;
        }

        var gone = 0;
        var before = DateTime.UtcNow - TimeSpan.FromHours(2);
        foreach (var f in Directory.EnumerateFiles(CacheDir))
        {
            var name = Path.GetFileName(f);
            if (
                (
                    name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    || name.Contains(".tmp.", StringComparison.OrdinalIgnoreCase)
                )
                && File.GetLastWriteTimeUtc(f) < before
            )
            {
                try
                {
                    File.Delete(f);
                    gone++;
                }
                catch
                { /* in use, or gone */
                }
            }
        }
        return gone;
    }

    /// <summary>
    /// Turns the WAV clips in the cache (recorded before clips were MP3s, or when the encoder failed) into MP3s,
    /// each under the same name; a WAV is deleted only once its MP3 is written and checked. Returns how many were
    /// converted. Does nothing without <see cref="ToMp3"/>.
    /// </summary>
    public static int ConvertWavClips()
    {
        if (ToMp3 is not { } encode || !Directory.Exists(CacheDir))
        {
            return 0;
        }

        var done = 0;
        foreach (var wav in Directory.EnumerateFiles(CacheDir, "*.wav").ToList())
        {
            var mp3 = Path.ChangeExtension(wav, ".mp3");
            try
            {
                if (File.Exists(mp3) || encode(wav, mp3))
                {
                    File.Delete(wav);
                    done++;
                }
            }
            catch
            {
                // In use, or the encoder refused it: it stays a WAV, which plays just the same.
            }
        }
        return done;
    }

    /// <summary>
    /// True when this table has a ROM without a recording at this length that hasn't been found silent (at least
    /// this long, from the same ROM files) or stuck.
    /// </summary>
    public static bool NeedsRecording(InstalledTable table, int seconds) =>
        RomOf(table) is { } rom
        && ExactFor(rom, seconds) == null
        && !GaveNothing(rom, Path.Combine(table.Folder, "pinmame", "roms"), seconds)
        && !File.Exists(StuckFor(rom));

    /// <summary>
    /// The ROM was recorded for at least <paramref name="seconds"/> from these same zips and gave nothing. The marker
    /// says how long and which zips (sizes and dates): adding a parent set or a newer ROM tries again. An empty
    /// marker is one from before lengths (25 s, any zips).
    /// </summary>
    internal static bool GaveNothing(string rom, string romsFolder, int seconds)
    {
        var marker = NoneFor(rom);
        if (!File.Exists(marker))
        {
            return false;
        }

        try
        {
            var lines = File.ReadAllLines(marker).Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count == 0)
            {
                return ClampSeconds(seconds) <= LegacySeconds;
            }

            var tried = lines
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2)
                .GroupBy(p => p[0].Trim())
                .ToDictionary(g => g.Key, g => g.First()[1].Trim());
            return int.TryParse(tried.GetValueOrDefault("seconds"), out var s)
                && s >= ClampSeconds(seconds)
                && tried.GetValueOrDefault("roms") == Fingerprint(romsFolder);
        }
        catch (IOException)
        {
            return true; // being written by another recording of the same ROM
        }
    }

    /// <summary>Writes the "gave nothing" marker (<see cref="GaveNothing"/>).</summary>
    public static void MarkNothing(string rom, string romsFolder, int seconds) =>
        File.WriteAllText(
            NoneFor(rom),
            $"seconds={ClampSeconds(seconds)}\nroms={Fingerprint(romsFolder)}\n"
        );

    /// <summary>The ROM zips beside a table, as names, sizes and dates: changes when one is added or replaced.</summary>
    internal static string Fingerprint(string romsFolder)
    {
        if (!Directory.Exists(romsFolder))
        {
            return "";
        }

        var text = string.Join(
            ";",
            Directory
                .EnumerateFiles(romsFolder, "*.zip")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => $"{f.Name.ToLowerInvariant()}:{f.Length}:{f.LastWriteTimeUtc.Ticks}")
        );
        return Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(text))
        )[..16];
    }

    public static string PinmameDll(AppSettings settings) =>
        Path.Combine(settings.VpxDir, "plugins", "pinmame", "pinmame64.dll");

    // When things happen, in seconds of emulated time after the ROM starts (the sound it has made so far, so a ROM
    // slowed down by a busy PC still gets its keys at the right moment): boot (Stern SAM takes longest), Escape,
    // coins, Start.
    const double EscapeAt = 12,
        CoinAt = 20,
        StartAt = 24;

    /// <summary>Music often starts a moment after Start: this much more is recorded than the clip keeps.</summary>
    const double LeadIn = 3;

    /// <summary>How long a recording of this length runs, in emulated (and so about real) seconds.</summary>
    public static double RunSeconds(int seconds) => StartAt + ClampSeconds(seconds) + LeadIn;

    const int SampleRate = 44100;

    public enum Outcome
    {
        /// <summary>A clip was kept.</summary>
        Recorded,

        /// <summary>The ROM gave nothing worth keeping; remembered, so it isn't tried again.</summary>
        Nothing,

        /// <summary>Nothing came of it this time (no ROM, no PinMAME, cut short, too slow): tried again next time.</summary>
        Failed,
    }

    /// <summary>Records this table's ROM at this length (see the other overload).</summary>
    public static (Outcome Outcome, string? File) Record(
        InstalledTable table,
        string pinmameDll,
        int seconds,
        Action<string>? log = null
    ) =>
        RomOf(table) is { } rom
            ? Record(rom, table.Folder, table.TablePath, pinmameDll, seconds, log)
            : (Outcome.Failed, null);

    /// <summary>
    /// Records <paramref name="rom"/> (from the table folder's pinmame\roms) into the cache. Blocks for
    /// <see cref="RunSeconds"/>; one at a time per process (PinMAME is a single instance). A ROM that ran its whole
    /// time and gave nothing is remembered (<see cref="GaveNothing"/>); one cut short or run too slowly isn't, so a
    /// recording stopped half-way, or starved of CPU, never counts as "nothing".
    /// </summary>
    public static (Outcome Outcome, string? File) Record(
        string rom,
        string tableFolder,
        string vpxPath,
        string pinmameDll,
        int seconds,
        Action<string>? log = null
    )
    {
        seconds = ClampSeconds(seconds);
        var romsFolder = Path.Combine(tableFolder, "pinmame", "roms");
        if (
            !File.Exists(Path.Combine(romsFolder, rom + ".zip"))
            || !File.Exists(pinmameDll)
            || File.Exists(StuckFor(rom))
        )
        {
            return (Outcome.Failed, null);
        }

        var scratch = Path.Combine(
            Path.GetTempPath(),
            "vrtm-romaudio-" + Guid.NewGuid().ToString("N")[..8]
        );
        try
        {
            // Every zip from the table (PinMAME clones need their parent set beside them), fresh nvram.
            var roms = Directory.CreateDirectory(Path.Combine(scratch, "roms")).FullName;
            Directory.CreateDirectory(Path.Combine(scratch, "nvram"));
            Directory.CreateDirectory(Path.Combine(scratch, "cfg"));
            foreach (var zip in Directory.EnumerateFiles(romsFolder, "*.zip"))
            {
                File.Copy(zip, Path.Combine(roms, Path.GetFileName(zip)));
            }

            var wasStuck = _stuck;
            var run = Run(
                pinmameDll,
                scratch + Path.DirectorySeparatorChar,
                rom,
                TroughSwitches(vpxPath),
                RunSeconds(seconds),
                log
            );
            Directory.CreateDirectory(CacheDir);
            var clip = Clip(run.Samples, run.Channels, run.Rate, run.StartSample, seconds);
            if (clip == null)
            {
                if (_stuck && !wasStuck)
                {
                    // This ROM's PinMAME wouldn't stop and gave nothing: remembered, so the next run (a new
                    // process) doesn't spend another minute on it.
                    File.WriteAllText(StuckFor(rom), "");
                    return (Outcome.Nothing, null);
                }
                if (_stuck || run.End == RunEnd.Cut)
                {
                    // An earlier ROM's PinMAME still running, or this one ran too slowly to reach the end: not a
                    // verdict on the ROM. (One whose emulation ended by itself, RunEnd.Stopped, is remembered below.)
                    log?.Invoke($"{rom}: the recording didn't run its full time; not remembered");
                    return (Outcome.Failed, null);
                }

                log?.Invoke(
                    $"{rom}: nothing to keep ({run.Samples.Length / Math.Max(1, run.Channels) / (double)run.Rate:F1} s recorded)"
                );
                MarkNothing(rom, romsFolder, seconds);
                return (Outcome.Nothing, null);
            }
            // A WAV first; as an MP3 (a tenth of the size) when the encoder is there and its result checks out.
            var file = FileFor(rom, seconds, ".wav");
            WriteWav(file, clip, run.Channels, run.Rate);
            var mp3 = FileFor(rom, seconds);
            try
            {
                if (ToMp3?.Invoke(file, mp3) == true)
                {
                    File.Delete(file);
                    file = mp3;
                }
            }
            catch (Exception e)
            {
                log?.Invoke($"{rom}: kept as WAV ({e.Message})");
            }
            // This replaces the ROM's recordings at other lengths, and any "gave nothing" from a shorter try.
            foreach (var (other, _) in AllFor(rom).Where(x => !PathsEqual(x.File, file)))
            {
                TryDelete(other);
            }
            TryDelete(NoneFor(rom));
            return (Outcome.Recorded, file);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, true);
            }
            catch { }
        }
    }

    static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch { }
    }

    static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// From Start onwards (the coin chimes before it are dropped), cut at the first long silence after the music and
    /// at <paramref name="seconds"/>, normalised and faded out. <paramref name="from"/> is the sample Start was pressed
    /// at (by default where it falls when the ROM runs at full speed). Null when there's hardly any sound.
    /// </summary>
    public static short[]? Clip(
        short[] samples,
        int channels,
        int rate,
        long from = -1,
        int seconds = MaxSeconds
    )
    {
        var begin =
            from >= 0
                ? Math.Max(0, from - (long)(0.3 * rate) * channels)
                : (long)((StartAt - 0.3) * rate) * channels;
        var clip = samples.AsSpan((int)Math.Min(samples.Length, begin)).ToArray();
        // Loudness per 100 ms block.
        var block = rate / 10 * channels;
        var blocks = clip.Length / block;
        if (blocks == 0)
        {
            return null;
        }

        var rms = new double[blocks];
        for (var b = 0; b < blocks; b++)
        {
            double sum = 0;
            for (var i = b * block; i < (b + 1) * block; i++)
            {
                sum += (double)clip[i] * clip[i];
            }

            rms[b] = Math.Sqrt(sum / block);
        }
        const double Quiet = 15; // about -67 dBFS: emulated machines play quietly, the clip is normalised below
        var loud = rms.Count(r => r > Quiet);
        if (loud < 20)
        {
            return null; // under 2 s of sound
        }
        // Start at the first sound, end after the last block before 3 s of quiet.
        var first = Array.FindIndex(rms, r => r > Quiet);
        var last = first;
        for (var b = first; b < blocks; b++)
        {
            if (rms[b] > Quiet)
            {
                last = b;
            }
            else if (b - last >= 30)
            {
                break;
            }
        }
        var start = first * block;
        var end = Math.Min(
            Math.Min(clip.Length, (last + 3) * block),
            start + ClampSeconds(seconds) * rate * channels
        );
        var cut = clip.AsSpan(start, end - start).ToArray();

        var peak = cut.Max(s => Math.Abs((int)s));
        var gain = peak > 0 ? Math.Min(32.0, 0.8 * short.MaxValue / peak) : 1;
        var fade = Math.Min(cut.Length, rate * channels); // last second
        for (var i = 0; i < cut.Length; i++)
        {
            var g = gain;
            var left = cut.Length - i;
            if (left < fade)
            {
                g *= (double)left / fade;
            }

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
        try
        {
            return TroughSwitchesIn(Script(vpxPath));
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<int> TroughSwitchesIn(string script)
    {
        var found = new List<int>();
        var inTroughWith = false;
        foreach (var raw in script.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("With ", StringComparison.OrdinalIgnoreCase))
            {
                inTroughWith = line.Contains("trough", StringComparison.OrdinalIgnoreCase);
            }
            else if (line.StartsWith("End With", StringComparison.OrdinalIgnoreCase))
            {
                inTroughWith = false;
            }

            if (
                line.StartsWith('\'')
                || !(inTroughWith || line.Contains("trough", StringComparison.OrdinalIgnoreCase))
            )
            {
                continue;
            }

            if (InitSwitches().Match(line) is { Success: true } array)
            {
                found.AddRange(
                    array
                        .Groups[1]
                        .Value.Split(',')
                        .Select(x => int.TryParse(x.Trim(), out var n) ? n : 0)
                );
            }
            else if (InitSw().Match(line) is { Success: true } init)
            {
                found.AddRange(
                    init.Groups[1]
                        .Value.Split(',')
                        .Skip(1)
                        .Select(x => int.TryParse(x.Trim(), out var n) ? n : 0)
                );
            }
            else if (line.Contains("_Hit", StringComparison.OrdinalIgnoreCase))
            {
                found.AddRange(SwitchOn().Matches(line).Select(m => int.Parse(m.Groups[1].Value)));
            }
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
    internal static string Script(string vpxPath)
    {
        using var root = RootStorage.OpenRead(vpxPath);
        using var s = root.OpenStorage("GameStg").OpenStream("GameData");
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        for (var i = 0; i + 8 <= bytes.Length; )
        {
            var len = BitConverter.ToInt32(bytes, i);
            if (len < 4 || i + 4 + len > bytes.Length)
            {
                break;
            }

            if (Encoding.ASCII.GetString(bytes, i + 4, 4) == "CODE")
            {
                // CODE is the one record whose length field doesn't cover its data: a second length follows.
                var codeLen = BitConverter.ToInt32(bytes, i + 8);
                return Encoding.Latin1.GetString(
                    bytes,
                    i + 12,
                    Math.Min(codeLen, bytes.Length - i - 12)
                );
            }
            i += 4 + len;
        }
        return "";
    }

    public static void WriteWav(string file, short[] samples, int channels, int rate)
    {
        var tmp = $"{file}.{Guid.NewGuid():N}.tmp"; // its own: another recording may write the same ROM
        using (var w = new BinaryWriter(File.Create(tmp)))
        {
            var bytes = samples.Length * 2;
            w.Write("RIFF"u8);
            w.Write(36 + bytes);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1);
            w.Write((short)channels);
            w.Write(rate);
            w.Write(rate * channels * 2);
            w.Write((short)(channels * 2));
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(bytes);
            w.Write(MemoryMarshal.AsBytes(samples.AsSpan()));
        }
        File.Move(tmp, file, overwrite: true);
    }

    // ---- libpinmame -------------------------------------------------------------------

    /// <summary>libpinmame has one global state: one user at a time (also <see cref="PinmameInfo"/>).</summary>
    internal static readonly object OneAtATime = new();

    /// <summary>A recording's PinMAME didn't stop: nothing more is recorded in this process.</summary>
    static volatile bool _stuck;

    /// <summary>A recording's PinMAME didn't stop in this process: PinMAME can't be used again until it ends.</summary>
    internal static bool Stuck => _stuck;

    /// <summary>The callbacks of a PinMAME that didn't stop, which it may still call.</summary>
    static readonly List<Delegate> KeptAlive = [];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct Config
    {
        public int AudioFormat; // 0: 16-bit
        public int SampleRate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string VpmPath;
        public IntPtr OnStateUpdated,
            OnDisplayAvailable,
            OnDisplayUpdated,
            OnAudioAvailable,
            OnAudioUpdated,
            OnMechAvailable,
            OnMechUpdated,
            OnSolenoidUpdated,
            OnConsoleDataUpdated,
            IsKeyPressed,
            OnLogMessage,
            OnSoundCommand;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AudioInfo
    {
        public int Format,
            Channels;
        public double SampleRate,
            FramesPerSecond;
        public int SamplesPerFrame,
            BufferSize;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int AudioAvailableFn(ref AudioInfo info, IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int AudioUpdatedFn(IntPtr buffer, int samples, IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void DisplayUpdatedFn(int index, IntPtr data, IntPtr layout, IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int IsKeyPressedFn(int key, IntPtr user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void SetConfigFn(ref Config config);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int RunFn([MarshalAs(UnmanagedType.LPStr)] string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void VoidFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int IntFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void SetIntFn(int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void SetSwitchFn(int sw, int state);

    // PINMAME_KEYCODE (same numbering as MAME's): VPinMAME's 7 is the service "Escape" button,
    // 5 a coin and 1 Start.
    const int KeyNumber1 = 27,
        KeyNumber5 = 31,
        KeyNumber7 = 33;

    /// <summary>
    /// Runs the ROM for <paramref name="total"/> seconds of emulated time and returns its sound. Fresh nvram makes
    /// many machines stop on a "factory settings restored / totals cleared" message until a service button is
    /// pressed, so Escape goes first; then coins and Start. The keys go by emulated time (the sound received so
    /// far), not the wall clock: on a busy PC the ROM runs slower than real time, and Start pressed by the clock
    /// came while it was still booting. A run that falls far behind is given up (<see cref="RunEnd.Cut"/>). When
    /// <paramref name="log"/> is given, the main display's last picture is written to it as text, which shows where
    /// a silent ROM got stuck.
    /// </summary>
    static RunResult Run(
        string dll,
        string vpmPath,
        string rom,
        IReadOnlyList<int> balls,
        double total,
        Action<string>? log
    )
    {
        lock (OneAtATime)
        {
            if (_stuck)
            {
                return new([], 1, SampleRate, -1, RunEnd.Cut);
            }

            var lib = NativeLibrary.Load(dll);
            T Fn<T>(string name) =>
                Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
            var setConfig = Fn<SetConfigFn>("PinmameSetConfig");
            var run = Fn<RunFn>("PinmameRun");
            var stop = Fn<VoidFn>("PinmameStop");
            var isRunning = Fn<IntFn>("PinmameIsRunning");
            var handleKeyboard = Fn<SetIntFn>("PinmameSetHandleKeyboard");
            var handleMechanics = Fn<SetIntFn>("PinmameSetHandleMechanics");
            var setSwitch = Fn<SetSwitchFn>("PinmameSetSwitch");

            var audio = new List<short>(SampleRate * 2 * (int)total);
            int channels = 1,
                rate = SampleRate;
            long frames = 0, // sample frames received: the emulated clock
                startFrame = -1; // when Start was pressed
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // Emulated seconds; the wall clock only until the first sound arrives.
            double Now()
            {
                var f = Interlocked.Read(ref frames);
                return f > 0 ? f / (double)rate : clock.Elapsed.TotalSeconds;
            }
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
                lock (audio)
                {
                    audio.AddRange(chunk);
                }

                Interlocked.Add(ref frames, samples);
                return samples;
            };
            byte[]? display = null;
            int displayWidth = 0;
            DisplayUpdatedFn onDisplay = (index, data, layout, _) =>
            {
                if (index != 0 || data == IntPtr.Zero || log == null)
                {
                    return;
                }

                var l = Marshal.PtrToStructure<DisplayLayout>(layout);
                if (l.Width <= 0 || l.Height <= 0 || l.Width > 256 || l.Height > 64)
                {
                    return;
                }

                var pixels = new byte[l.Width * l.Height];
                Marshal.Copy(data, pixels, 0, pixels.Length);
                (display, displayWidth) = (pixels, l.Width);
            };
            IsKeyPressedFn isKeyPressed = (key, _) =>
            {
                var t = Now();
                if (key == KeyNumber1 && Pressed(t, StartAt))
                {
                    Interlocked.CompareExchange(ref startFrame, Interlocked.Read(ref frames), -1);
                }

                return key switch
                {
                    KeyNumber7 => Pressed(t, EscapeAt) || Pressed(t, EscapeAt + 1),
                    // Four coins (enough credits whatever the pricing), then Start.
                    KeyNumber5 => Pressed(t, CoinAt)
                        || Pressed(t, CoinAt + 0.6)
                        || Pressed(t, CoinAt + 1.2)
                        || Pressed(t, CoinAt + 1.8),
                    KeyNumber1 => Pressed(t, StartAt),
                    _ => false,
                }
                    ? 1
                    : 0;
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
            var end = RunEnd.Cut;
            try
            {
                setConfig(ref config);
                handleKeyboard(1);
                handleMechanics(1);
                var status = run(rom);
                if (status != 0)
                {
                    log?.Invoke($"{rom}: PinMAME didn't start it (status {status})");
                    return new([], 1, SampleRate, -1, RunEnd.NotStarted);
                }
                clock.Restart();
                var ballsIn = false;
                // Given up when it falls far behind real time (a PC too busy to run it properly).
                var giveUpAt = total * 1.5 + 15;
                while (Now() < total && isRunning() != 0 && clock.Elapsed.TotalSeconds < giveUpAt)
                {
                    // Balls waiting in the trough, so Start starts a game. Not before the machine is up
                    // (setting switches right after PinmameRun crashes it).
                    if (!ballsIn && clock.Elapsed.TotalSeconds > 2 && Now() > 2)
                    {
                        foreach (var sw in balls)
                        {
                            setSwitch(sw, 1);
                        }

                        ballsIn = true;
                    }
                    Thread.Sleep(100);
                }
                end =
                    Now() >= total - 0.5 ? RunEnd.Finished
                    : isRunning() == 0 ? RunEnd.Stopped
                    : RunEnd.Cut;
                stop();
                for (var i = 0; i < 50 && isRunning() != 0; i++)
                {
                    Thread.Sleep(100);
                }
            }
            finally
            {
                if (isRunning() != 0)
                {
                    // Still running: PinMAME keeps calling these, so they must never be collected, and the one
                    // PinMAME this process has can't be set up for another ROM.
                    _stuck = true;
                    KeptAlive.AddRange([onAvailable, onUpdated, onDisplay, isKeyPressed]);
                    log?.Invoke(
                        $"{rom}: PinMAME didn't stop; no more recordings until the app restarts"
                    );
                }
                GC.KeepAlive(onAvailable);
                GC.KeepAlive(onUpdated);
                GC.KeepAlive(onDisplay);
                GC.KeepAlive(isKeyPressed);
            }
            if (display != null && log != null)
            {
                for (var y = 0; y < display.Length / displayWidth; y++)
                {
                    log(
                        new string(
                            display
                                .AsSpan(y * displayWidth, displayWidth)
                                .ToArray()
                                .Select(p => p > 0 ? '#' : ' ')
                                .ToArray()
                        ).TrimEnd()
                    );
                }
            }

            lock (audio)
            {
                var sf = Interlocked.Read(ref startFrame);
                return new(audio.ToArray(), channels, rate, sf >= 0 ? sf * channels : -1, end);
            }
        }

        static bool Pressed(double t, double at) => t >= at && t < at + 0.25;
    }

    enum RunEnd
    {
        /// <summary>Ran its whole time.</summary>
        Finished,

        /// <summary>PinMAME wouldn't start the ROM (missing parent set, unknown game): the same every time.</summary>
        NotStarted,

        /// <summary>
        /// The emulation ended by itself before its time: the ROM does that every time, so a run that gave nothing is a
        /// verdict on it (remembered), unlike <see cref="Cut"/>.
        /// </summary>
        Stopped,

        /// <summary>Fell too far behind real time to finish (a busy PC): not a verdict on the ROM.</summary>
        Cut,
    }

    /// <param name="StartSample">The sample (all channels counted) at which Start was pressed, or -1.</param>
    readonly record struct RunResult(
        short[] Samples,
        int Channels,
        int Rate,
        long StartSample,
        RunEnd End
    );

    [StructLayout(LayoutKind.Sequential)]
    struct DisplayLayout
    {
        public int Type,
            Top,
            Left,
            Length,
            Width,
            Height,
            Depth;
    }
}
