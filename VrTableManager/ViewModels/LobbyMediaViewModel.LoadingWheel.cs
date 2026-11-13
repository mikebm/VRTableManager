using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// The VR lobby's loading video (played on a big screen while a table starts) and wheel art (the game's logo over
// the selected cabinet) for one table: My Tables → MORE.
public sealed partial class LobbyMediaViewModel
{
    const string VideoFiles = "*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.f4v";

    // ---- Loading video: one table ----

    /// <summary>Takes the loading video out of the catalog's media pack (Popper's Loading\ video), and its wheel when the table has none.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task GetLoadingVideo(InstalledItem item)
    {
        var table = item.Table;
        if (!item.CanGetLoadingVideo || !App.Library.TryBeginBusy(table.TablePath))
        {
            return;
        }

        try
        {
            await GetLobbyMediaCoreAsync(
                table,
                new() { Loading = true },
                LobbyMedia.Needs.Of(table).And(new() { Wheel = true }),
                "Use my own loading video… turns any video you have into one."
            );
        }
        finally
        {
            App.Library.EndBusy(table.TablePath);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task UseOwnLoadingVideo(InstalledItem item)
    {
        var table = item.Table;
        if (!item.CanHaveLoadingVideo)
        {
            return;
        }

        if (await PickLoadingVideoAsync($"Choose a loading video for {item.Title}") is not { } file)
        {
            return;
        }

        if (!App.Library.TryBeginBusy(table.TablePath))
        {
            return;
        }

        try
        {
            if (
                await MakeLoadingVideoAsync(
                    file,
                    LoadingVideo.TableFolder(table.Folder),
                    table.Manifest.GameName,
                    "while this table starts"
                )
            )
            {
                SetHasLoadingVideo(table.TablePath, true);
            }
        }
        finally
        {
            App.Library.EndBusy(table.TablePath);
        }
    }

    [RelayCommand]
    void RemoveLoadingVideo(InstalledItem item)
    {
        if (item.IsBusy)
        {
            return; // a job for this table would put it back when it finishes
        }

        try
        {
            LoadingVideo.Remove(LoadingVideo.TableFolder(item.Table.Folder));
        }
        catch (Exception e)
        {
            App.Library.PlayStatus = "Couldn't remove the loading video: " + e.Message;
            return;
        }
        SetHasLoadingVideo(item.Table.TablePath, false);
        App.Library.PlayStatus = App.Lobby.HasDefaultLoadingVideo
            ? $"Removed {item.Title}'s loading video: it plays the one for every table now."
            : $"Removed {item.Title}'s loading video.";
    }

    void SetHasLoadingVideo(string tablePath, bool on)
    {
        foreach (
            var i in App.Library._allInstalled.Where(i =>
                string.Equals(i.Table.TablePath, tablePath, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            i.HasLoadingVideo = on;
        }
    }

    internal Task<string?> PickLoadingVideoAsync(string title) =>
        Ui.PickFileAsync(
            title,
            VideoFiles,
            startFolder: Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        );

    /// <summary>Turns a video into a loading video in <paramref name="folder"/>, as a job. True when it worked.</summary>
    internal async Task<bool> MakeLoadingVideoAsync(string video, string folder, string forWhat, string when)
    {
        var job = new JobItem($"Loading video for {forWhat}") { FileNames = [forWhat] };
        App.Downloads.Jobs.Insert(0, job);
        try
        {
            job.Report("Making the loading screen's frames…");
            var info = await Task.Run(() =>
                VideoFrames.LoadingFromVideo(video, folder, job.Cts.Token)
            );
            if (info == null)
            {
                job.Finish(
                    $"Windows couldn't play {Path.GetFileName(video)}. Try an .mp4 (H.264).",
                    error: true
                );
                return false;
            }
            var sound = LoadingVideo.AudioIn(folder) != null ? ", with its sound" : "";
            job.Finish(
                $"✓ Saved a {info.Frames / (double)info.Fps:0.#} s loading video{sound}. The VR lobby plays it {when}."
            );
            return true;
        }
        catch (OperationCanceledException)
        {
            job.Finish("Cancelled", error: true);
        }
        catch (Exception e)
        {
            job.Finish("Couldn't use that video: " + e.Message, error: true);
        }
        return false;
    }

    // ---- Wheel art ----

    internal IReadOnlyList<(MediaPackAudio.Source Source, bool WheelPack)> WheelArtSourcesFor(
        InstalledTable t
    ) =>
        t.HasOwnFolder && t.Manifest.GameId != "" && App.Catalog.CatalogGame(t.Manifest.GameId) is { } game
            ? WheelArt.SourcesFor(game)
            : [];

    /// <summary>Downloads the catalog's wheel art (or, failing that, its media packs) until one has this game's wheel.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task GetWheelArt(InstalledItem item)
    {
        var table = item.Table;
        if (!item.CanGetWheelArt || !App.Library.TryBeginBusy(table.TablePath))
        {
            return;
        }

        try
        {
            await GetLobbyMediaCoreAsync(
                table,
                new() { Wheel = true },
                hint: "MORE → My artwork → VR lobby wheel art… uses a picture you have."
            );
        }
        finally
        {
            App.Library.EndBusy(table.TablePath);
        }
    }

    [RelayCommand]
    void RemoveWheelArt(InstalledItem item)
    {
        if (item.IsBusy)
        {
            return; // a job for this table would put it back when it finishes
        }

        try
        {
            WheelArt.RemoveSaved(item.Table.Folder);
        }
        catch (Exception e)
        {
            App.Library.PlayStatus = "Couldn't remove the wheel art: " + e.Message;
            return;
        }
        SetHasWheelArt(item.Table.TablePath, false);
        App.Library.PlayStatus = $"Removed {item.Title}'s wheel art.";
    }

    void SetHasWheelArt(string tablePath, bool on)
    {
        foreach (
            var i in App.Library._allInstalled.Where(i =>
                string.Equals(i.Table.TablePath, tablePath, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            i.HasWheelArt = on;
        }
    }

}
