using CommunityToolkit.Mvvm.Input;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// A moving playfield for the VR lobby (My Tables → MORE): the playfield video from the catalog's media pack
// first; when there's none (or it has no playfield video), VPX's own recording of the table's attract mode.
public sealed partial class MainViewModel
{
    /// <summary>One VPX recording at a time: each opens a window and runs the table.</summary>
    readonly SemaphoreSlim _recording = new(1, 1);

    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task GetLobbyVideo(InstalledItem item)
    {
        var table = item.Table;
        if (!TryBeginBusy(table.TablePath)) return;
        var record = false;
        try
        {
            var packs = AttractAudioSourcesFor(table).Where(s => !s.IsDirect).ToList();
            var problem = packs.Count == 0 ? "The catalog has no media pack for this table." : await LobbyVideoFromPacksAsync(table, packs);
            if (problem == null) return;
            record = await _ui.ChoiceAsync($"Record {item.Title}'s attract mode?",
                problem + "\n\nVPX can record the table's attract mode instead: a small VPX window opens for about half a minute " +
                "(sound off) and closes by itself. Don't use the PC for anything else meanwhile.",
                ["RECORD IN VPX", "CANCEL"]) == 0;
        }
        finally { EndBusy(table.TablePath); }
        if (record) await RecordLobbyVideo(item);
    }

    /// <summary>Tries each media pack until one has a playfield video. Null when it worked, else what went wrong.</summary>
    async Task<string?> LobbyVideoFromPacksAsync(InstalledTable table, IReadOnlyList<MediaPackAudio.Source> packs)
    {
        var job = new JobItem($"Lobby video for {table.Manifest.GameName}") { FileNames = [table.Manifest.GameName] };
        Jobs.Insert(0, job);
        var problems = new List<string>();
        try
        {
            foreach (var src in packs)
            {
                job.Report($"Getting the {src.Label}…");
                try
                {
                    var file = await DownloadAttractSourceAsync(job, src);
                    job.Report("Taking the playfield video out…");
                    var video = await Task.Run(() => LobbyVideo.SavePlayfieldVideo(file, table.Folder, table.Manifest.GameName));
                    // The same pack has the table's music: keep it too when there's none yet (saves a second download).
                    if (MediaPackAudio.Existing(table.Folder) == null)
                        try
                        {
                            if (await Task.Run(() => MediaPackAudio.Save(file, table.Folder, table.Manifest.GameName, CompressedAudio.Converter)) != null)
                                SetHasAttractAudio(table.TablePath, true);
                        }
                        catch (Exception) { /* the video is what was asked for */ }
                    CleanJobDownloads(job);
                    if (video == null) { problems.Add($"the {src.Label} has no playfield video"); continue; }
                    job.Report("Making the lobby frames…");
                    var info = await Task.Run(() => VideoFrames.FromVideo(video, table.Folder, PlayfieldPicture(table), job.Cts.Token));
                    if (info == null) { problems.Add($"Windows couldn't play the {src.Label}'s playfield video ({Path.GetExtension(video)})"); continue; }
                    job.Finish($"✓ Saved a {info.Frames / (double)info.Fps:0.#} s playfield loop from the {src.Label}. The VR lobby plays it on the cabinet when you pick this table.");
                    SetHasLobbyVideo(table.TablePath, true);
                    return null;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    CleanJobDownloads(job);
                    problems.Add($"the {src.Label} failed ({e.Message})");
                }
            }
            var why = "No playfield video: " + string.Join("; ", problems) + ".";
            job.Finish(why, error: true);
            return why;
        }
        catch (OperationCanceledException)
        {
            job.Finish("Cancelled", error: true);
            return "The download was cancelled.";
        }
        finally { CleanJobDownloads(job); }
    }

    /// <summary>The table's own playfield picture, to tell which way a sideways video is turned.</summary>
    static byte[]? PlayfieldPicture(InstalledTable t)
    {
        try { return VpxImages.ExtractPlayfield(t.TablePath); }
        catch (Exception) { return null; }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task RecordLobbyVideo(InstalledItem item)
    {
        var table = item.Table;
        if (!TryBeginBusy(table.TablePath)) return;
        var job = new JobItem($"Recording {table.Manifest.GameName} for the lobby") { FileNames = [table.Manifest.GameName] };
        Jobs.Insert(0, job);
        try
        {
            if (!_recording.Wait(0))
            {
                job.Report("Waiting for the other recording to finish…");
                await _recording.WaitAsync(job.Cts.Token);
            }
            try
            {
                job.Report("VPX is recording the attract mode (a small window, sound off)…");
                var frames = await LobbyVideo.RecordAsync(Settings, table, job.Cts.Token);
                if (frames.Count == 0)
                {
                    job.Finish("VPX recorded nothing. Check that the table starts in VPX (Play) and try again.", error: true);
                    return;
                }
                job.Report($"Making the lobby frames from {frames.Count} recorded frames…");
                var info = await Task.Run(() => VideoFrames.FromCapture(frames, table.Folder, job.Cts.Token));
                job.Finish($"✓ Recorded a {info.Frames / (double)info.Fps:0.#} s attract loop. The VR lobby plays it on the cabinet when you pick this table. " +
                           "Tables built only for VR may show their VR room instead of the playfield; remove it then.");
                SetHasLobbyVideo(table.TablePath, true);
            }
            finally
            {
                try { LobbyVideo.DeleteCapture(table); } catch { } // VPX's full-size frames are large
                _recording.Release();
            }
        }
        catch (OperationCanceledException) { job.Finish("Cancelled", error: true); }
        catch (Exception e) { job.Finish("Recording failed: " + e.Message, error: true); }
        finally { EndBusy(table.TablePath); }
    }

    [RelayCommand]
    void RemoveLobbyVideo(InstalledItem item)
    {
        try
        {
            LobbyVideo.Remove(item.Table.Folder);
            if (LobbyVideo.ExistingPlayfieldVideo(item.Table.Folder) is { } video) File.Delete(video);
        }
        catch (Exception e) { PlayStatus = "Couldn't remove the lobby video: " + e.Message; return; }
        SetHasLobbyVideo(item.Table.TablePath, false);
        PlayStatus = $"Removed {item.Title}'s lobby video.";
    }

    void SetHasLobbyVideo(string tablePath, bool on)
    {
        foreach (var i in _allInstalled.Where(i => string.Equals(i.Table.TablePath, tablePath, StringComparison.OrdinalIgnoreCase)))
            i.HasLobbyVideo = on;
    }
}
