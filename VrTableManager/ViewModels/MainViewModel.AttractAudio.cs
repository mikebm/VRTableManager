using CommunityToolkit.Mvvm.Input;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Attract audio for the VR lobby from the catalog's sound files and media packs (My Tables → MORE).
// Only on request: packs are mostly videos and can be hundreds of MB, of which one .mp3 is kept
// (a pack's own audio file, or else a minute of a video's sound track converted to MP3).
public sealed partial class MainViewModel
{
    IReadOnlyList<MediaPackAudio.Source> AttractAudioSourcesFor(InstalledTable t) =>
        t.IsVpx && t.Manifest.GameId != "" && _allGames.FirstOrDefault(g => g.Id == t.Manifest.GameId) is { } game
            ? MediaPackAudio.SourcesFor(game)
            : [];

    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task GetAttractAudio(InstalledItem item)
    {
        var table = item.Table;
        if (!TryBeginBusy(table.TablePath)) return;
        try { await GetAttractAudioCoreAsync(table, item.AttractAudioSources); }
        finally { EndBusy(table.TablePath); }
    }

    async Task GetAttractAudioCoreAsync(InstalledTable table, IReadOnlyList<MediaPackAudio.Source> sources)
    {
        var job = new JobItem($"Attract audio for {table.Manifest.GameName}");
        Jobs.Insert(0, job);
        var problems = new List<string>();
        try
        {
            // Sound files first (one small .mp3, no sign-in), then each media pack until one has audio.
            foreach (var src in sources)
            {
                job.Report($"Getting the {src.Label}…");
                try
                {
                    var file = await WithDownloadSlotAsync(job, async () =>
                    {
                        var downloads = JobDownloadDir(job);
                        if (!src.IsDirect) return await _browser.DownloadAsync(src.Url, downloads, [], job.Report, job.Cts.Token);
                        var dest = Path.Combine(downloads, TableInstaller.SafeName(Uri.UnescapeDataString(Path.GetFileName(new Uri(src.Url).AbsolutePath))));
                        await Http.DownloadFileAsync(src.Url, dest, job.ProgressReporter, job.Cts.Token);
                        return dest;
                    });
                    job.Report("Taking the audio out…");
                    var saved = await Task.Run(() => MediaPackAudio.Save(file, table.Folder, table.Manifest.GameName, CompressedAudio.Converter));
                    CleanJobDownloads(job); // the pack's videos aren't kept
                    if (saved != null)
                    {
                        job.Finish($"✓ Saved music\\{Path.GetFileName(saved)} ({DiskUsage.Format(new FileInfo(saved).Length)}) from the {src.Label}. The VR lobby plays it when you pick this table.");
                        SetHasAttractAudio(table.TablePath, true);
                        return;
                    }
                    problems.Add($"the {src.Label} has no usable audio");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    CleanJobDownloads(job);
                    problems.Add($"the {src.Label} failed ({e.Message})");
                }
            }
            job.Finish(sources.Count == 0 ? "The catalog has no sound file or media pack for this table."
                : "No attract audio found: " + string.Join("; ", problems) + ".", error: true);
        }
        catch (OperationCanceledException)
        {
            job.Finish("Cancelled", error: true);
        }
        finally
        {
            CleanJobDownloads(job);
        }
    }

    [RelayCommand]
    void RemoveAttractAudio(InstalledItem item)
    {
        try { MediaPackAudio.Remove(item.Table.Folder); }
        catch (Exception e) { PlayStatus = "Couldn't remove the attract audio: " + e.Message; return; }
        SetHasAttractAudio(item.Table.TablePath, false);
        PlayStatus = $"Removed {item.Title}'s attract audio.";
    }

    void SetHasAttractAudio(string tablePath, bool on)
    {
        foreach (var i in _allInstalled.Where(i => string.Equals(i.Table.TablePath, tablePath, StringComparison.OrdinalIgnoreCase)))
            i.HasAttractAudio = on;
    }
}
