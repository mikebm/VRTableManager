using System.Diagnostics;

namespace VrTableManager.Services;

/// <summary>
/// Attract music recorded from ROMs (<see cref="RomAudio"/>) for many tables, ahead of time: the one routine behind
/// Setup → VR lobby → GENERATE ATTRACT MUSIC, GET LOBBY MEDIA, PREPARE TABLES and the LAUNCH VR LOBBY question, and
/// the VR lobby's own fallback for the table you've stopped on. Each recording runs in a process of its own
/// (<c>VrTableManager.exe --record-rom-audio …</c>), because PinMAME is one instance per process: that is what lets
/// several run at once (<see cref="Parallel"/>), and a ROM that crashes the emulator takes down only its own process.
/// Each process works in its own scratch folder (copied ROM zips, empty nvram) and writes its clip through a temp file
/// of its own, so two can never write over each other.
/// </summary>
public static class RomAudioBatch
{
    public const string Flag = "--record-rom-audio";

    /// <summary>Exit codes of <see cref="RunCommand"/>; anything else (a crash, a kill) counts as failed.</summary>
    public const int ExitRecorded = 0,
        ExitNothing = 10,
        ExitFailed = 11;

    /// <summary>One ROM to record, from one table that has it.</summary>
    public sealed record Item(string Rom, InstalledTable Table);

    /// <summary>
    /// The tables that need their attract music recorded at this length: VPX tables with their ROM in pinmame\roms,
    /// no sound file of their own (music folder, media pack), and no recording at this length yet nor a "gives
    /// nothing" verdict for it. One per ROM (tables sharing a ROM share its recording), in title order.
    /// </summary>
    public static List<Item> Todo(IEnumerable<InstalledTable> tables, int seconds) =>
        tables
            .Where(t => t.IsVpx && !t.IsIncomplete)
            .Select(t => (Table: t, Rom: RomAudio.RomOf(t)))
            .Where(x => x.Rom != null)
            // Filtered before one per ROM is kept: a table with music of its own (or a verdict for its own ROM files)
            // mustn't hide another table on the same ROM that still needs it.
            .Where(x => Lobby.LobbyArt.LooseSound(x.Table) == null)
            .Where(x => RomAudio.NeedsRecording(x.Table, seconds))
            .DistinctBy(x => x.Rom!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Table.Manifest.GameName, StringComparer.OrdinalIgnoreCase)
            .Select(x => new Item(x.Rom!, x.Table))
            .ToList();

    /// <summary>
    /// How many recordings run at once on a PC with this many logical processors. Each one keeps an emulator busy in
    /// real time (about one core) and must keep up with it, or its music comes out wrong: half the processors, less
    /// one for you and the app, at most 6 (more only fills the disk with copied ROMs at the same moment).
    /// </summary>
    public static int Parallel(int processors) => Math.Clamp(processors / 2 - 1, 1, 6);

    public static int Parallel() => Parallel(Environment.ProcessorCount);

    /// <summary>About how long recording <paramref name="count"/> ROMs takes, <paramref name="parallel"/> at a time.</summary>
    public static TimeSpan Estimate(int count, int seconds, int parallel) =>
        TimeSpan.FromSeconds(
            Math.Ceiling(count / (double)Math.Max(1, parallel)) * (RomAudio.RunSeconds(seconds) + 5)
        );

    /// <summary>"about 12 min", "about 1 h 5 min", "under a minute".</summary>
    public static string Describe(TimeSpan t) =>
        t.TotalMinutes < 1 ? "under a minute"
        : t.TotalHours < 1 ? $"about {(int)Math.Ceiling(t.TotalMinutes)} min"
        : $"about {(int)t.TotalHours} h {t.Minutes} min";

    /// <summary>The command line that records one ROM.</summary>
    public static List<string> Args(Item item, int seconds, string pinmameDll) =>
        [
            Flag,
            item.Rom,
            item.Table.Folder,
            item.Table.TablePath,
            RomAudio
                .ClampSeconds(seconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            pinmameDll,
        ];

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == Flag;

    /// <summary>Reads <see cref="Args"/> back; null when they aren't complete.</summary>
    public static (string Rom, string Folder, string Vpx, int Seconds, string Dll)? Parse(
        string[] args
    ) =>
        IsCommand(args)
        && args.Length >= 6
        && int.TryParse(
            args[4],
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var s
        )
            ? (args[1], args[2], args[3], RomAudio.ClampSeconds(s), args[5])
            : null;

    /// <summary>The recording process itself (<c>VrTableManager.exe --record-rom-audio …</c>): records, then exits.</summary>
    public static int RunCommand(string[] args)
    {
        if (Parse(args) is not var (rom, folder, vpx, seconds, dll))
        {
            return ExitFailed;
        }

        try
        {
            var (outcome, _) = RomAudio.Record(rom, folder, vpx, dll, seconds, Console.WriteLine);
            return outcome switch
            {
                RomAudio.Outcome.Recorded => ExitRecorded,
                RomAudio.Outcome.Nothing => ExitNothing,
                _ => ExitFailed,
            };
        }
        catch (Exception e)
        {
            Console.WriteLine($"{rom}: {e.Message}");
            return ExitFailed;
        }
    }

    /// <summary>
    /// Starts one recording process and waits for it: below normal priority, so the PC (and a VR lobby) stays
    /// smooth; killed when cancelled or when it runs far past its time, either way without a "gives nothing" verdict.
    /// </summary>
    public static async Task<RomAudio.Outcome> RecordInProcessAsync(
        string exe,
        Item item,
        int seconds,
        string pinmameDll,
        CancellationToken ct
    )
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in Args(item, seconds, pinmameDll))
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi);
        if (p == null)
        {
            return RomAudio.Outcome.Failed;
        }

        // Ends with whoever started it (the manager closing mid-batch, the lobby quitting), and is killed the moment
        // it's cancelled, not on a later continuation that may never run (a UI or Godot thread shutting down).
        ChildProcesses.Adopt(p);
        using var killOnCancel = ct.Register(() => Kill(p));
        try
        {
            p.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        { /* already gone, or not allowed */
        }
        p.OutputDataReceived += (_, _) => { }; // read, so a full pipe can't stall it
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(RomAudio.RunSeconds(seconds) * 1.5 + 60));
        try
        {
            await p.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(p);
            ct.ThrowIfCancellationRequested();
            return RomAudio.Outcome.Failed; // ran far too long
        }
        return p.ExitCode switch
        {
            ExitRecorded => RomAudio.Outcome.Recorded,
            ExitNothing => RomAudio.Outcome.Nothing,
            _ => RomAudio.Outcome.Failed,
        };
    }

    static void Kill(Process p)
    {
        try
        {
            p.Kill(entireProcessTree: true);
        }
        catch
        { /* already gone */
        }
    }

    /// <summary>
    /// Records every item, at most <paramref name="parallel"/> at a time, with <paramref name="record"/> doing one
    /// (<see cref="RecordInProcessAsync"/>, or a stand-in in tests). <paramref name="done"/> gets each result as it
    /// comes, with how many have finished. Cancelling stops the waiting ones and the running ones.
    /// </summary>
    public static async Task<Dictionary<RomAudio.Outcome, int>> RunAsync(
        IReadOnlyList<Item> items,
        int parallel,
        Func<Item, CancellationToken, Task<RomAudio.Outcome>> record,
        Action<int, Item, RomAudio.Outcome>? done,
        CancellationToken ct
    )
    {
        var counts = new Dictionary<RomAudio.Outcome, int>
        {
            [RomAudio.Outcome.Recorded] = 0,
            [RomAudio.Outcome.Nothing] = 0,
            [RomAudio.Outcome.Failed] = 0,
        };
        var finished = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, parallel));
        var tasks = items
            .Select(async item =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    RomAudio.Outcome r;
                    try
                    {
                        r = await record(item, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        r = RomAudio.Outcome.Failed;
                    }
                    int n;
                    lock (counts)
                    {
                        counts[r]++;
                        n = ++finished;
                    }
                    done?.Invoke(n, item, r);
                }
                finally
                {
                    gate.Release();
                }
            })
            .ToList();
        await Task.WhenAll(tasks);
        return counts;
    }
}
