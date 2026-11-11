using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// A moving playfield for the VR lobby (My Tables → MORE): the playfield video from the catalog's media pack
// first; when there's none (or it has no playfield video), VPX's own recording of the table's attract mode.
public sealed partial class MainViewModel
{
    /// <summary>One VPX recording at a time: each opens a window and runs the table.</summary>
    readonly SemaphoreSlim _recording = new(1, 1);

    /// <summary>
    /// On LAUNCH VR LOBBY, while some tables have no lobby video: says that videos bring the cabinets to life,
    /// and how to get them. Only lobby users see it, every launch until they tick it off. Returns false to stop
    /// the launch (they chose to record first, or closed the window).
    /// </summary>
    async Task<bool> LobbyVideoTipAsync()
    {
        if (!Settings.ShowLobbyVideoTip || IsBatchRecording)
        {
            return true;
        }

        var without = _allInstalled.Count(i =>
            i.CanHaveLobbyVideo && !i.Table.IsIncomplete && !i.HasLobbyVideo
        );
        if (without == 0)
        {
            return true;
        }

        string[] buttons =
        [
            "LAUNCH VR LOBBY",
            "LAUNCH, DON'T SHOW AGAIN",
            "RECORD LOBBY VIDEOS FIRST",
        ];
        var pick = await _ui.ChoiceAsync(
            "Bring the VR lobby to life",
            $"{without} of your tables {(without == 1 ? "has" : "have")} no lobby video yet. With one, a table's cabinet "
                + "shows its playfield moving, with ramps and toys standing up, instead of a still picture.\n\n"
                + "• RECORD LOBBY VIDEOS FIRST (below) records every table that has none, one after another "
                + "(about 40 s each, in a small VPX window).\n\n"
                + "• GET LOBBY MEDIA on My Tables takes them from the catalog's media packs instead, with each table's "
                + "loading video, music and logo, and records the rest.\n\n"
                + "• Or one table at a time: its ⋯ menu → Get all VR lobby media.",
            buttons
        );
        if (pick == 1)
        {
            Settings.ShowLobbyVideoTip = false;
            Settings.Save();
        }
        else if (pick == 2)
        {
            await RecordAllLobbyVideosCommand.ExecuteAsync(null);
        }

        return pick is 0 or 1;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task GetLobbyVideo(InstalledItem item)
    {
        var table = item.Table;
        if (!item.CanHaveLobbyVideo || !TryBeginBusy(table.TablePath))
        {
            return;
        }

        LobbyMediaResult? r;
        try
        {
            // The pack has the table's music and wheel too: kept when the table has none (saves another download).
            r = await GetLobbyMediaCoreAsync(
                table,
                new() { Video = true },
                LobbyMedia.Needs.Of(table).And(new() { Audio = true, Wheel = true })
            );
        }
        finally
        {
            EndBusy(table.TablePath);
        }

        if (r is { Left.Video: true })
        {
            await OfferRecordingAsync(item, LobbyMedia.WhyNoVideo(r.Problems, r.HadPack));
        } // null: you cancelled it, so no offer to record instead
    }

    /// <summary>The table's own playfield picture, to tell which way a sideways video is turned.</summary>
    static byte[]? PlayfieldPicture(InstalledTable t)
    {
        try
        {
            return VpxImages.ExtractPlayfield(t.TablePath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    Task RecordLobbyVideo(InstalledItem item) => RecordLobbyVideoCoreAsync(item);

    /// <summary>Records one table's attract mode in VPX. False when the recording was cancelled.</summary>
    /// <param name="started">Gets the job as soon as it exists (the batch uses it to stop the running recording).</param>
    async Task<bool> RecordLobbyVideoCoreAsync(InstalledItem item, Action<JobItem>? started = null)
    {
        var table = item.Table;
        if (!item.CanHaveLobbyVideo)
        {
            return true;
        }

        if (!TryBeginBusy(table.TablePath))
        {
            return true; // something else is running for this table: skip it
        }

        var job = new JobItem($"Recording {table.Manifest.GameName} for the lobby")
        {
            FileNames = [table.Manifest.GameName],
        };
        Jobs.Insert(0, job);
        started?.Invoke(job);
        try
        {
            if (!_recording.Wait(0))
            {
                job.Report("Waiting for the other recording to finish…");
                await _recording.WaitAsync(job.Cts.Token);
            }
            try
            {
                // A table whose machine script is missing stops before it draws anything: say why instead of
                // running VPX for nothing.
                if (
                    await Task.Run(() => MachineScript.Missing(table.TablePath, Settings)) is
                    { } vbs
                )
                {
                    job.Finish(
                        $"This table needs {vbs}, which isn't installed, so it can't start in VPX. It's usually offered next to "
                            + $"the ROM on the ROM's download page: save it into the table's folder ({table.Folder}), then record again.",
                        error: true
                    );
                    return true;
                }
                job.Report("VPX is recording the attract mode (a small window, sound off)…");
                var frames = await LobbyVideo.RecordAsync(Settings, table, job.Cts.Token);
                if (frames.Count == 0)
                {
                    job.Finish(
                        "VPX recorded nothing. Check that the table starts in VPX (Play) and try again.",
                        error: true
                    );
                    return true;
                }
                job.Report($"Making the lobby frames from {frames.Count} recorded frames…");
                var info = await Task.Run(() =>
                    VideoFrames.FromCapture(frames, table.Folder, job.Cts.Token)
                );
                job.Finish(
                    $"✓ Recorded a {info.Frames / (double)info.Fps:0.#} s attract loop. The VR lobby plays it on the cabinet when you pick this table. "
                        + "Tables built only for VR may show their VR room instead of the playfield; remove it then."
                );
                SetHasLobbyVideo(table.TablePath, true);
            }
            finally
            {
                try
                {
                    LobbyVideo.DeleteCapture(table);
                }
                catch { } // VPX's full-size frames are large
                _recording.Release();
            }
        }
        catch (OperationCanceledException)
        {
            job.Finish("Cancelled", error: true);
            return false;
        }
        catch (Exception e)
        {
            job.Finish("Recording failed: " + e.Message, error: true);
        }
        finally
        {
            EndBusy(table.TablePath);
        }
        return true;
    }

    // ---- Record every table at once (the lobby video tip's RECORD LOBBY VIDEOS FIRST) ----

    /// <summary>A batch of recordings is running; the toolbar shows its progress and a STOP button.</summary>
    [ObservableProperty]
    public partial bool IsBatchRecording { get; set; }

    [ObservableProperty]
    public partial string BatchRecordingText { get; set; } = "";
    JobItem? _batchJob;
    bool _batchStop;

    /// <summary>The running batch records tables in VPX: the VR lobby can't start until it's done.</summary>
    bool _batchUsesVpx;

    /// <summary>
    /// Records a lobby video in VPX for every VPX table that has none, one after another, so their cabinets get
    /// a moving playfield with real depth (ramps and toys stand up only over a video). No downloads: recording
    /// needs nothing but VPX. Tables still missing their ROM are left out (they'd only record an error screen).
    /// Cancelling a recording, or STOP, ends the batch.
    /// </summary>
    [RelayCommand]
    async Task RecordAllLobbyVideos()
    {
        if (IsBatchRecording)
        {
            return;
        }

        if (IsLobbyRunning)
        {
            PlayStatus = "Close the VR lobby first: recording runs the tables in VPX.";
            return;
        }
        var without = _allInstalled
            .Where(i =>
                i.CanHaveLobbyVideo
                && !i.Table.IsIncomplete
                && LobbyVideo.Existing(i.Table.Folder) == null
            )
            .DistinctBy(i => i.Table.TablePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var todo = without.Where(i => !i.NeedsRom).ToList();
        var noRom = without.Count - todo.Count;
        var noRomNote =
            noRom > 0
                ? $" {noRom} more need{(noRom == 1 ? "s" : "")} a ROM first and {(noRom == 1 ? "is" : "are")} skipped."
                : "";
        if (todo.Count == 0)
        {
            PlayStatus =
                without.Count == 0
                    ? "Every table already has a lobby video."
                    : "No table can be recorded yet:" + noRomNote;
            return;
        }
        var minutes = Math.Max(1, (int)Math.Ceiling(todo.Count * 40 / 60.0));
        if (
            await _ui.ChoiceAsync(
                $"Record lobby videos for {todo.Count} table{(todo.Count == 1 ? "" : "s")}?",
                $"VPX opens each table in a small window, records its attract mode for about half a minute (sound off) and closes it, "
                    + $"one after another: about {minutes} minute{(minutes == 1 ? "" : "s")} in all. Leave the PC alone meanwhile.{noRomNote}\n\n"
                    + "Tables built only for VR may record their VR room instead of the playfield; remove that table's lobby video afterwards (MORE).",
                ["RECORD ALL", "CANCEL"]
            ) != 0
        )
        {
            return;
        }

        await RecordLobbyVideosAsync(todo);
    }

    /// <summary>
    /// Records <paramref name="todo"/> in VPX one after another, with the toolbar's STOP, and says how it went.
    /// The caller has asked and checked the VR lobby isn't running. Cancelling a recording, or STOP, ends the batch.
    /// </summary>
    async Task RecordLobbyVideosAsync(List<InstalledItem> todo)
    {
        IsBatchRecording = true;
        _batchUsesVpx = true;
        _batchStop = false;
        int recorded = 0,
            failed = 0;
        var stopped = false;
        try
        {
            for (var n = 0; n < todo.Count; n++)
            {
                if (_batchStop)
                {
                    stopped = true;
                    break;
                }
                BatchRecordingText = $"■  STOP RECORDING ({n + 1}/{todo.Count})";
                var item = todo[n];
                if (!await RecordLobbyVideoCoreAsync(item, j => _batchJob = j))
                {
                    stopped = true;
                    break;
                }
                if (LobbyVideo.Existing(item.Table.Folder) != null)
                {
                    recorded++;
                }
                else
                {
                    failed++;
                }
            }
        }
        finally
        {
            IsBatchRecording = false;
            _batchUsesVpx = false;
            _batchJob = null;
        }
        PlayStatus =
            $"Recorded lobby videos for {recorded} table{(recorded == 1 ? "" : "s")}."
            + (failed > 0 ? $" {failed} didn't record (the Downloads list says why)." : "")
            + (stopped ? $" Stopped; {todo.Count - recorded - failed} not done." : "");
    }

    [RelayCommand]
    void StopBatchRecording()
    {
        _batchStop = true;
        _batchJob?.Cts.Cancel();
    }

    [RelayCommand]
    void RemoveLobbyVideo(InstalledItem item)
    {
        if (item.IsBusy)
        {
            return; // a job for this table would put it back when it finishes
        }

        try
        {
            LobbyVideo.Remove(item.Table.Folder);
            if (LobbyVideo.ExistingPlayfieldVideo(item.Table.Folder) is { } video)
            {
                File.Delete(video);
            }
        }
        catch (Exception e)
        {
            PlayStatus = "Couldn't remove the lobby video: " + e.Message;
            return;
        }
        SetHasLobbyVideo(item.Table.TablePath, false);
        PlayStatus = $"Removed {item.Title}'s lobby video.";
    }

    void SetHasLobbyVideo(string tablePath, bool on)
    {
        foreach (
            var i in _allInstalled.Where(i =>
                string.Equals(i.Table.TablePath, tablePath, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            i.HasLobbyVideo = on;
        }
    }
}
