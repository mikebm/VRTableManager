using CommunityToolkit.Mvvm.Input;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Attract audio for the VR lobby from the catalog's sound files and media packs (My Tables → MORE).
// Only on request: packs are mostly videos and can be hundreds of MB, of which one .mp3 is kept
// (a pack's own audio file, or else a minute of a video's sound track converted to MP3).
public sealed partial class LobbyMediaViewModel
{
    internal IReadOnlyList<MediaPackAudio.Source> AttractAudioSourcesFor(InstalledTable t) =>
        t.IsVpx
        && t.Manifest.GameId != ""
        && App.Catalog.CatalogGame(t.Manifest.GameId) is { } game
            ? MediaPackAudio.SourcesFor(game)
            : [];

    /// <summary>The table's sound file, or else a media pack's audio (and its wheel when the table has none).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task GetAttractAudio(InstalledItem item)
    {
        var table = item.Table;
        if (!item.CanGetAttractAudio || !App.Library.TryBeginBusy(table.TablePath))
        {
            return;
        }

        try
        {
            await GetLobbyMediaCoreAsync(
                table,
                new() { Audio = true },
                LobbyMedia.Needs.Of(table).And(new() { Wheel = true }),
                "Or drop your own attract.mp3 into the table's music folder."
            );
        }
        finally
        {
            App.Library.EndBusy(table.TablePath);
        }
    }

    /// <summary>Downloads a sound file or media pack into the job's folder and returns the file.</summary>
    async Task<string> DownloadAttractSourceAsync(JobItem job, MediaPackAudio.Source src)
    {
        var downloads = DownloadsViewModel.JobDownloadDir(job);
        // In your browser: no download slot (see FetchAsync).
        if (!src.IsDirect && !SitePolicy.AllowsAutomation(src.Url))
        {
            return await App.Downloads.Assisted.DownloadAsync(src.Url, downloads, job);
        }

        return await App.Downloads.WithDownloadSlotAsync(
            job,
            async () =>
            {
                if (!src.IsDirect)
                {
                    return await App.Downloads.Browser.DownloadAsync(
                        src.Url,
                        downloads,
                        [],
                        job.Report,
                        job.Cts.Token
                    );
                }

                var dest = Path.Combine(
                    downloads,
                    TableInstaller.SafeName(
                        Uri.UnescapeDataString(Path.GetFileName(new Uri(src.Url).AbsolutePath))
                    )
                );
                await Http.DownloadFileAsync(src.Url, dest, job.ProgressReporter, job.Cts.Token);
                return dest;
            }
        );
    }

    [RelayCommand]
    void RemoveAttractAudio(InstalledItem item)
    {
        try
        {
            MediaPackAudio.Remove(item.Table.Folder);
        }
        catch (Exception e)
        {
            App.Library.PlayStatus = "Couldn't remove the attract audio: " + e.Message;
            return;
        }
        SetHasAttractAudio(item.Table.TablePath, false);
        App.Library.PlayStatus = $"Removed {item.Title}'s attract audio.";
    }

    void SetHasAttractAudio(string tablePath, bool on)
    {
        foreach (
            var i in App.Library._allInstalled.Where(i =>
                string.Equals(i.Table.TablePath, tablePath, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            i.HasAttractAudio = on;
        }
    }
}
