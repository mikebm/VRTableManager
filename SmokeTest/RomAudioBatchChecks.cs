using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>
/// Attract music recorded ahead of time (RomAudioBatch) and the cache behind it (RomAudio), without a real ROM:
/// the length and format in the cache key, the "gives nothing" marker, which tables a run records, how many run at
/// once, the recording command line, the music-length setting from older settings files, and WAV → MP3.
/// Everything runs in a data folder of its own (VRTM_DATA_DIR), never the player's cache.
/// </summary>
public static class RomAudioBatchChecks
{
    public static IEnumerable<(bool ok, string name)> Run(string root)
    {
        var data = Path.Combine(root, "romaudio-data");
        var was = Environment.GetEnvironmentVariable("VRTM_DATA_DIR");
        Environment.SetEnvironmentVariable("VRTM_DATA_DIR", data);
        try
        {
            foreach (var c in Checks(root))
            {
                yield return c;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("VRTM_DATA_DIR", was);
            RomAudio.ToMp3 = null;
        }
    }

    static InstalledTable Table(string root, string name, string? rom, bool zip = true)
    {
        var folder = Path.Combine(root, "romaudio-tables", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".vpx"), "");
        if (rom != null && zip)
        {
            Directory.CreateDirectory(Path.Combine(folder, "pinmame", "roms"));
            File.WriteAllText(Path.Combine(folder, "pinmame", "roms", rom + ".zip"), "zip");
        }
        return new InstalledTable(
            new TableManifest
            {
                Engine = TableManifest.Vpx,
                GameName = name,
                RomNames = rom != null ? [rom] : [],
            },
            Path.Combine(folder, name + ".vpx"),
            folder
        );
    }

    static IEnumerable<(bool ok, string name)> Checks(string root)
    {
        var cache = RomAudio.CacheDir;
        yield return (
            cache.StartsWith(
                Path.GetFullPath(Path.Combine(root, "romaudio-data")),
                StringComparison.OrdinalIgnoreCase
            ),
            "attract music checks use a cache of their own"
        );

        // ---- Cache key: length and format in the name ----
        yield return (
            Path.GetFileName(RomAudio.FileFor("AFM_113", 10)) == "afm_113-10s.mp3"
                && Path.GetFileName(RomAudio.FileFor("afm_113", 20, ".wav")) == "afm_113-20s.wav"
                && Path.GetFileName(RomAudio.FileFor("afm_113", 25)) == "afm_113.mp3"
                && Path.GetFileName(RomAudio.FileFor("afm_113", 25, ".wav")) == "afm_113.wav",
            "music cache key: ROM, length and format (25 s keeps the name from before lengths)"
        );
        yield return (
            RomAudio.ClampSeconds(0) == 10
                && RomAudio.ClampSeconds(2) == 5
                && RomAudio.ClampSeconds(99) == 30
                && RomAudio.ClampSeconds(15) == 15
                && RomAudio.Lengths.All(l => RomAudio.ClampSeconds(l) == l)
                && RomAudio.Lengths.Contains(RomAudio.DefaultSeconds),
            "music length: 5–30 s, 10 by default, every length Setup offers is in range"
        );

        var afm = Table(root, "afm", "afm_113");
        Directory.CreateDirectory(cache);
        yield return (
            RomAudio.NeedsRecording(afm, 10) && RomAudio.Cached(afm, 10) == null,
            "a ROM table with nothing recorded needs recording"
        );
        File.WriteAllText(Path.Combine(cache, "afm_113.wav"), "legacy"); // a recording from before lengths
        yield return (
            RomAudio.Cached(afm, 10) == Path.Combine(cache, "afm_113.wav")
                && RomAudio.NeedsRecording(afm, 10)
                && !RomAudio.NeedsRecording(afm, 25),
            "an older recording plays until the new length is recorded, and counts as the 25 s one"
        );
        File.WriteAllText(Path.Combine(cache, "afm_113-10s.mp3"), "mp3");
        yield return (
            RomAudio.Cached(afm, 10) == Path.Combine(cache, "afm_113-10s.mp3")
                && !RomAudio.NeedsRecording(afm, 10)
                && RomAudio.NeedsRecording(afm, 15),
            "the recording at this length wins; another length is recorded again"
        );
        File.WriteAllText(Path.Combine(cache, "afm_113b-10s.mp3"), "other ROM");
        yield return (
            RomAudio.SecondsOf(Path.Combine(cache, "afm_113b-10s.mp3"), "afm_113") == null
                && RomAudio.SecondsOf("afm_113-8s.mp3", "afm_113") == 8
                && RomAudio.SecondsOf("afm_113.none", "afm_113") == null,
            "another ROM's clip (afm_113b) or a marker isn't taken for afm_113's"
        );

        // ---- "Gives nothing" marker ----
        var mm = Table(root, "mm", "mm_109c");
        var mmRoms = Path.Combine(mm.Folder, "pinmame", "roms");
        File.WriteAllText(Path.Combine(cache, "mm_109c.none"), ""); // from before lengths
        yield return (
            !RomAudio.NeedsRecording(mm, 10)
                && !RomAudio.NeedsRecording(mm, 25)
                && RomAudio.NeedsRecording(mm, 30),
            "an older \"gives nothing\" still counts, up to the 25 s it was tried for"
        );
        RomAudio.MarkNothing("mm_109c", mmRoms, 10);
        yield return (
            !RomAudio.NeedsRecording(mm, 10)
                && !RomAudio.NeedsRecording(mm, 5)
                && RomAudio.NeedsRecording(mm, 20),
            "\"gives nothing\" holds for lengths up to the one tried"
        );
        File.WriteAllText(Path.Combine(mmRoms, "mm_109.zip"), "parent set");
        yield return (
            RomAudio.NeedsRecording(mm, 10),
            "adding a ROM zip (a parent set) tries a silent ROM again"
        );

        // ---- Clip length ----
        const int rate = 1000;
        var samples = new short[80 * rate];
        for (var i = 30 * rate; i < 70 * rate; i++)
        {
            samples[i] = (short)(i % 2 == 0 ? 1000 : -1000); // 40 s of sound
        }
        var clip = RomAudio.Clip(samples, 1, rate, seconds: 10);
        yield return (
            clip != null && clip.Length == 10 * rate && Math.Abs((int)clip[^1]) < 100,
            $"a clip is cut to its length and still fades out ({clip?.Length / (double)rate:F1} s)"
        );
        var late = RomAudio.Clip(samples, 1, rate, from: 55 * rate, seconds: 30);
        yield return (
            late != null && late.Length is > 15 * rate and < 16 * rate,
            $"a clip starts where Start was really pressed, for a ROM that ran slow ({late?.Length / (double)rate:F1} s)"
        );
        yield return (
            RomAudio.RunSeconds(10) < RomAudio.RunSeconds(30) && RomAudio.RunSeconds(10) <= 40,
            $"a 10 s clip runs the ROM {RomAudio.RunSeconds(10)} s (was 49 s)"
        );

        // ---- Which tables a run records ----
        var plain = Table(root, "plain", "tz_92");
        var withMusic = Table(root, "withmusic", "taf_l7");
        Directory.CreateDirectory(Path.Combine(withMusic.Folder, "music"));
        File.WriteAllBytes(Path.Combine(withMusic.Folder, "music", "attract.mp3"), [1, 2, 3]);
        var noZip = Table(root, "nozip", "cv_14", zip: false);
        var noRom = Table(root, "original", null);
        var sameRom = Table(root, "tz-mod", "tz_92");
        var fp = new InstalledTable(
            new TableManifest
            {
                Engine = TableManifest.FuturePinball,
                GameName = "fp",
                RomNames = ["x"],
            },
            Path.Combine(root, "fp.fpt"),
            root
        );
        var todo = RomAudioBatch.Todo([plain, withMusic, noZip, noRom, sameRom, fp, afm, mm], 10);
        yield return (
            todo.Select(t => t.Rom).Order().SequenceEqual(["mm_109c", "tz_92"])
                && todo.Single(t => t.Rom == "tz_92").Table == plain,
            "a run records each ROM table without music once: not one with a sound file, no ROM zip, already recorded, or not VPX "
                + $"({string.Join(", ", todo.Select(t => t.Rom))})"
        );

        // A table with its own music mustn't hide another on the same ROM that has none (it's listed first here).
        var tafMod = Table(root, "taf-mod", "taf_l7");
        var shared = RomAudioBatch.Todo([withMusic, tafMod], 10);
        yield return (
            shared.Count == 1 && shared[0].Table == tafMod,
            "a table sharing its ROM with one that has its own music still gets recorded"
        );

        // Recordings end with whoever started them: a child joins the job that's closed when this process exits.
        if (OperatingSystem.IsWindows())
        {
            using var child = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 5 127.0.0.1 >nul")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                }
            )!;
            var adopted = ChildProcesses.Adopt(child);
            try
            {
                child.Kill(entireProcessTree: true);
            }
            catch { }
            yield return (
                adopted,
                "a recording process is tied to the one that started it (Windows job object)"
            );
        }

        // ---- How many at once ----
        yield return (
            RomAudioBatch.Parallel(1) == 1
                && RomAudioBatch.Parallel(4) == 1
                && RomAudioBatch.Parallel(8) == 3
                && RomAudioBatch.Parallel(12) == 5
                && RomAudioBatch.Parallel(64) == 6,
            "recordings at once: half the processors less one, 1 to 6"
        );
        var est = RomAudioBatch.Estimate(10, 10, 3);
        yield return (
            est.TotalSeconds is > 4 * 30 and < 4 * 50
                && RomAudioBatch.Describe(est).StartsWith("about "),
            $"time estimate: 10 ROMs, 3 at a time, in 4 rounds ({RomAudioBatch.Describe(est)})"
        );

        var running = 0;
        var most = 0;
        var seen = new List<int>();
        var items = Enumerable
            .Range(0, 9)
            .Select(i => new RomAudioBatch.Item($"r{i}", plain))
            .ToList();
        var counts = RomAudioBatch
            .RunAsync(
                items,
                3,
                async (item, ct) =>
                {
                    var now = Interlocked.Increment(ref running);
                    lock (seen)
                    {
                        most = Math.Max(most, now);
                    }
                    await Task.Delay(40, ct);
                    Interlocked.Decrement(ref running);
                    return item.Rom == "r4" ? throw new IOException("crashed")
                        : item.Rom.EndsWith('1') ? RomAudio.Outcome.Nothing
                        : RomAudio.Outcome.Recorded;
                },
                (n, _, _) =>
                {
                    lock (seen)
                    {
                        seen.Add(n);
                    }
                },
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();
        yield return (
            most == 3
                && counts[RomAudio.Outcome.Recorded] == 7
                && counts[RomAudio.Outcome.Nothing] == 1
                && counts[RomAudio.Outcome.Failed] == 1
                && seen.Order().SequenceEqual(Enumerable.Range(1, 9)),
            $"a run keeps to its cap ({most} at once of 3) and counts every result, a crash as failed"
        );

        // ---- The recording command line ----
        var args = RomAudioBatch.Args(todo[0], 99, @"C:\VPX\plugins\pinmame\pinmame64.dll");
        yield return (
            RomAudioBatch.IsCommand([.. args])
                && RomAudioBatch.Parse([.. args]) is var (r, f, v, s, d)
                && r == todo[0].Rom
                && f == todo[0].Table.Folder
                && v == todo[0].Table.TablePath
                && s == 30
                && d.EndsWith("pinmame64.dll")
                && RomAudioBatch.Parse(["--record-rom-audio", "x"]) == null
                && RomAudioBatch.RunCommand(["--record-rom-audio", "x"])
                    == RomAudioBatch.ExitFailed,
            "the recording command line reads back (length kept in range); an incomplete one fails"
        );
        yield return (
            RomAudioBatch.RunCommand([
                .. RomAudioBatch.Args(todo[0], 10, Path.Combine(root, "no-pinmame.dll")),
            ]) == RomAudioBatch.ExitFailed
                && RomAudio.NeedsRecording(plain, 10),
            "without PinMAME a recording fails and isn't remembered as \"nothing\""
        );

        // ---- Settings from before the music length ----
        var old = Path.Combine(root, "romaudio-old-settings.json");
        File.WriteAllText(old, """{ "LobbyRecordsRomAudio": false }""");
        var o = AppSettings.LoadFrom(old);
        File.WriteAllText(old, """{ "AttractMusicSeconds": 99 }""");
        var big = AppSettings.LoadFrom(old);
        yield return (
            o.AttractSeconds == 10
                && !o.LobbyRecordsRomAudio
                && big.AttractSeconds == 30
                && big.LobbyRecordsRomAudio
                && new AppSettings().AttractSeconds == 10,
            "older settings: music length 10 s, the lobby's own recording kept as it was; out-of-range lengths kept in range"
        );

        // ---- WAV → MP3 ----
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }
        var wav = Path.Combine(root, "romaudio-tone.wav");
        var tone = new short[10 * 44100 * 2];
        for (var i = 0; i < tone.Length / 2; i++)
        {
            tone[2 * i] = tone[2 * i + 1] = (short)(
                8000 * Math.Sin(2 * Math.PI * 440 * i / 44100.0)
            );
        }
        RomAudio.WriteWav(wav, tone, 2, 44100);
        var mp3 = Path.Combine(root, "romaudio-tone.mp3");
        var ok = CompressedAudio.WavToMp3(wav, mp3);
        double length = 0;
        if (ok)
        {
            using var reader = new NAudio.Wave.MediaFoundationReader(mp3);
            length = reader.TotalTime.TotalSeconds;
        }
        var size = ok ? new FileInfo(mp3).Length : 0;
        yield return (
            ok
                && File.Exists(wav)
                && size is > 120_000 and < 220_000
                && Math.Abs(length - 10) < 0.5
                && !Directory.EnumerateFiles(root, "romaudio-tone.*.tmp.mp3").Any(),
            $"a 10 s stereo tone: WAV in ({new FileInfo(wav).Length / 1024} KB), 128 kbps MP3 out ({size / 1024} KB, {length:F1} s), no temp file left"
        );
        yield return (
            !CompressedAudio.WavToMp3(
                Path.Combine(root, "romaudio-tone.mp3"),
                Path.Combine(root, "bad.mp3")
            ) && !File.Exists(Path.Combine(root, "bad.mp3")),
            "a file that isn't a WAV gives no MP3"
        );

        // Older WAV clips in the cache become MP3s under the same name; a WAV goes only once its MP3 is there.
        File.Delete(Path.Combine(cache, "afm_113.wav"));
        File.Copy(wav, Path.Combine(cache, "afm_113-10s.wav"));
        File.Delete(Path.Combine(cache, "afm_113-10s.mp3"));
        File.WriteAllText(Path.Combine(cache, "broken-10s.wav"), "not a wav");
        RomAudio.ToMp3 = CompressedAudio.WavToMp3;
        var converted = RomAudio.ConvertWavClips();
        yield return (
            converted == 1
                && File.Exists(Path.Combine(cache, "afm_113-10s.mp3"))
                && !File.Exists(Path.Combine(cache, "afm_113-10s.wav"))
                && File.Exists(Path.Combine(cache, "broken-10s.wav"))
                && RomAudio.Cached(afm, 10) == Path.Combine(cache, "afm_113-10s.mp3"),
            "older WAV clips are turned into MP3s; one that can't be stays a WAV"
        );
    }
}
