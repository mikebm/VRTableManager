using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Attract music recorded from ROMs, ahead of time (RomAudioBatch). One routine, GenerateAttractMusicAsync, does it for
// every way in: Setup → VR lobby → Music → GENERATE ATTRACT MUSIC, GET LOBBY MEDIA and MORE → Get all VR lobby media
// on My Tables, PREPARE TABLES (as a second step you choose), and the question on LAUNCH VR LOBBY. Its settings sit
// together under Music: the clip length and the lobby's own fallback.
public sealed partial class LobbyViewModel
{
    /// <summary>The lengths Setup offers for a table's music in the lobby.</summary>
    public IReadOnlyList<int> AttractMusicLengths => RomAudio.Lengths;

    /// <summary>How many seconds of a table's music the lobby plays, and how long a recorded clip is.</summary>
    [ObservableProperty]
    public partial int AttractMusicSeconds { get; set; } = RomAudio.DefaultSeconds;

    partial void OnAttractMusicSecondsChanged(int value)
    {
        var seconds = RomAudio.ClampSeconds(value);
        if (seconds != value)
        {
            AttractMusicSeconds = seconds;
            return;
        }
        if (Settings.AttractMusicSeconds != seconds)
        {
            SaveSetting(s => s.AttractMusicSeconds = seconds);
        }
    }

    /// <summary>Music is being recorded from ROMs (one job under Downloads).</summary>
    [ObservableProperty]
    public partial bool IsGeneratingAttractMusic { get; set; }

    /// <summary>Recordings at once on this PC.</summary>
    static int MusicParallel => RomAudioBatch.Parallel();

    /// <summary>The ROMs among these tables still without attract music at the current length (off the UI thread).</summary>
    internal Task<List<RomAudioBatch.Item>> AttractMusicTodoAsync(
        IEnumerable<InstalledTable> tables
    )
    {
        var list = tables.ToList();
        var seconds = Settings.AttractSeconds;
        return Task.Run(() => RomAudioBatch.Todo(list, seconds));
    }

    /// <summary>"12 ROM tables have no attract music yet: recording it takes about 4 min, 3 at a time."</summary>
    internal string AttractMusicSummary(int count) =>
        $"{count} ROM table{(count == 1 ? " has" : "s have")} no attract music yet: recording it from the ROM"
        + (count == 1 ? "" : "s")
        + $" takes {RomAudioBatch.Describe(RomAudioBatch.Estimate(count, Settings.AttractSeconds, MusicParallel))}"
        + (count > 1 && MusicParallel > 1 ? $", {MusicParallel} at a time" : "")
        + ", in the background.";

    /// <summary>Setup → VR lobby → Music → GENERATE ATTRACT MUSIC: every table.</summary>
    [RelayCommand]
    Task GenerateAttractMusic() =>
        GenerateAttractMusicAsync(App.Library._allInstalled.Select(i => i.Table).ToList());

    /// <summary>Why music can't be recorded now (said in the status line), or null when it can.</summary>
    internal string? CantGenerateMusicWhy()
    {
        if (IsGeneratingAttractMusic)
        {
            return "Attract music is being recorded already: follow it under Downloads.";
        }

        if (IsLobbyRunning)
        {
            return "Close the VR lobby first: recording music runs the ROMs on this PC.";
        }

        if (!File.Exists(RomAudio.PinmameDll(Settings)))
        {
            return "Recording attract music needs VPX's own PinMAME (plugins\\pinmame\\pinmame64.dll in the VPX folder): "
                + "check Setup → VPX.";
        }

        return Environment.ProcessPath == null
            ? "Couldn't find this app's own program file."
            : null;
    }

    /// <summary>
    /// Records the attract music of the ROM tables among <paramref name="tables"/> that have none at the current
    /// length, several at once, as one job under Downloads. Older WAV recordings are turned into MP3s first. Returns
    /// how many were recorded, or null when it didn't run (said why in the status line) or was cancelled.
    /// </summary>
    /// <param name="quietIfNone">No job and no message when there's nothing to record (a table's Get all media).</param>
    internal async Task<int?> GenerateAttractMusicAsync(
        IReadOnlyList<InstalledTable> tables,
        bool quietIfNone = false
    )
    {
        if (CantGenerateMusicWhy() is { } why)
        {
            if (!quietIfNone)
            {
                App.Library.PlayStatus = why;
            }
            return null;
        }

        var exe = Environment.ProcessPath!;
        var dll = RomAudio.PinmameDll(Settings);
        var seconds = Settings.AttractSeconds;
        var parallel = MusicParallel;
        IsGeneratingAttractMusic = true;
        JobItem? job = null;
        try
        {
            RomAudio.ToMp3 = CompressedAudio.WavToMp3;
            var todo = await AttractMusicTodoAsync(tables);
            if (todo.Count == 0 && quietIfNone)
            {
                return 0;
            }

            job = new JobItem("Attract music for the VR lobby");
            App.Downloads.Jobs.Insert(0, job);
            job.Report("Turning older recordings into MP3s…");
            var converted = await Task.Run(() =>
            {
                RomAudio.CleanLeftovers();
                return RomAudio.ConvertWavClips();
            });
            if (todo.Count == 0)
            {
                job.Finish(
                    "Every ROM table already has its attract music."
                        + (converted > 0 ? $" Turned {converted} older recordings into MP3s." : "")
                );
                return 0;
            }

            var started = DateTime.UtcNow;
            var progress = job.ProgressReporter;
            job.Report(
                $"Recording {todo.Count} ROM{(todo.Count == 1 ? "" : "s")}, {parallel} at a time "
                    + $"({RomAudioBatch.Describe(RomAudioBatch.Estimate(todo.Count, seconds, parallel))})…"
            );
            var counts = await RomAudioBatch.RunAsync(
                todo,
                parallel,
                (item, ct) => RomAudioBatch.RecordInProcessAsync(exe, item, seconds, dll, ct),
                (n, item, _) =>
                {
                    progress.Report(n / (double)todo.Count);
                    job.Report(
                        $"{n} of {todo.Count}{TimeLeft(started, n, todo.Count)}: {item.Table.Manifest.GameName}"
                    );
                },
                job.Cts.Token
            );
            var recorded = counts[RomAudio.Outcome.Recorded];
            var nothing = counts[RomAudio.Outcome.Nothing];
            var failed = counts[RomAudio.Outcome.Failed];
            job.Finish(
                $"Recorded {recorded} of {todo.Count} in {Took(DateTime.UtcNow - started)}."
                    + (
                        nothing > 0
                            ? $" {nothing} gave no music (silent, or the ROM needs something it hasn't got); they're skipped from now on."
                            : ""
                    )
                    + (
                        failed > 0
                            ? $" {failed} couldn't be recorded this time; the next run tries again."
                            : ""
                    )
                    + (recorded > 0 ? " The VR lobby plays them when you stop on the table." : ""),
                error: recorded == 0 && failed > 0
            );
            return recorded;
        }
        catch (OperationCanceledException)
        {
            job?.Finish(
                "Cancelled. What was recorded is kept; the rest are recorded next time.",
                error: true
            );
            return null;
        }
        catch (Exception e)
        {
            if (job != null)
            {
                job.Finish("Couldn't record the attract music: " + e.Message, error: true);
            }
            else
            {
                App.Library.PlayStatus = "Couldn't record the attract music: " + e.Message;
            }
            return null;
        }
        finally
        {
            IsGeneratingAttractMusic = false;
        }
    }
}
