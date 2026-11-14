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

        if (await PickLoadingVideoAsync($"Choose a loading video for {item.Title}") is not { } pick)
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
                    pick.File,
                    pick.QuarterTurns,
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

    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task RotateLoadingVideo(InstalledItem item)
    {
        var table = item.Table;
        if (!item.HasLoadingVideo || !App.Library.TryBeginBusy(table.TablePath))
        {
            return;
        }

        try
        {
            await RotateLoadingVideoAsync(
                LoadingVideo.TableFolder(table.Folder),
                item.Title,
                "while this table starts"
            );
        }
        finally
        {
            App.Library.EndBusy(table.TablePath);
        }
    }

    /// <summary>A video file to make a loading video from, and which way to turn it (asked after the file); null when cancelled.</summary>
    internal async Task<(string File, int QuarterTurns)?> PickLoadingVideoAsync(string title)
    {
        var file = await Ui.PickFileAsync(
            title,
            VideoFiles,
            startFolder: Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        );
        if (file == null)
        {
            return null;
        }

        var pick = await Ui.ChoiceAsync(
            "Rotation",
            $"{Path.GetFileName(file)}\n\nSome PinUP Popper launch videos were made turned a quarter, for a cabinet's "
                + "screen, so they show sideways. Turn it so it's upright; its shape turns with it.",
            ["NONE", "90° CLOCKWISE", "90° ANTICLOCKWISE", "CANCEL"]
        );
        return pick switch
        {
            0 => (file, 0),
            1 => (file, 1),
            2 => (file, -1),
            _ => null,
        };
    }

    /// <summary>
    /// Turns the loading video in <paramref name="folder"/> a quarter, as a job: made again from the video you
    /// chose when it's still there (no quality lost), else by turning its frames. True when it worked.
    /// </summary>
    internal async Task<bool> RotateLoadingVideoAsync(string folder, string forWhat, string when)
    {
        var origin = LoadingVideo.OriginIn(folder);
        var source = origin?.Video is { } v && File.Exists(v) ? v : null;
        var pick = await Ui.ChoiceAsync(
            $"Rotate the loading video for {forWhat}",
            "Turns it a quarter, keeping its shape (a wide video becomes a tall one). "
                + (
                    source != null
                        ? $"It's made again from {Path.GetFileName(source)}."
                        : "Its frames are turned (the video it came from isn't at hand any more)."
                ),
            ["90° CLOCKWISE", "90° ANTICLOCKWISE", "CANCEL"]
        );
        if (pick is not (0 or 1))
        {
            return false;
        }

        var turn = pick == 0 ? 1 : -1;
        var way = turn > 0 ? "clockwise" : "anticlockwise";
        if (source != null)
        {
            return await MakeLoadingVideoAsync(
                source,
                origin!.QuarterTurns + turn,
                folder,
                forWhat,
                $"{when}, now turned {way}"
            );
        }

        var job = new JobItem($"Loading video for {forWhat}") { FileNames = [forWhat] };
        App.Downloads.Jobs.Insert(0, job);
        try
        {
            job.Report("Turning the loading screen's frames…");
            var info = await Task.Run(() => VideoFrames.RotateLoading(folder, turn, job.Cts.Token));
            job.Finish(
                info == null
                    ? "There's no loading video to turn."
                    : $"✓ Turned the loading video {way}: {info.Width}×{info.Height} now.",
                error: info == null
            );
            return info != null;
        }
        catch (OperationCanceledException)
        {
            job.Finish("Cancelled", error: true);
        }
        catch (Exception e)
        {
            job.Finish("Couldn't turn the loading video: " + e.Message, error: true);
        }
        return false;
    }

    /// <summary>
    /// Turns a video into a loading video in <paramref name="folder"/>, turned <paramref name="quarterTurns"/>
    /// clockwise (−1 anticlockwise), as a job. True when it worked.
    /// </summary>
    internal async Task<bool> MakeLoadingVideoAsync(
        string video,
        int quarterTurns,
        string folder,
        string forWhat,
        string when
    )
    {
        var job = new JobItem($"Loading video for {forWhat}") { FileNames = [forWhat] };
        App.Downloads.Jobs.Insert(0, job);
        try
        {
            job.Report("Making the loading screen's frames…");
            var info = await Task.Run(() =>
                VideoFrames.LoadingFromVideo(
                    video,
                    folder,
                    job.Cts.Token,
                    quarterTurns,
                    rememberSource: true
                )
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
        t.HasOwnFolder
        && t.Manifest.GameId != ""
        && App.Catalog.CatalogGame(t.Manifest.GameId) is { } game
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
