using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// The VR lobby's loading video (played on a big screen while a table starts) and wheel art (the game's logo over
// the selected cabinet): My Tables → MORE for one table, Setup → VR lobby for the loading video every table shares.
public sealed partial class MainViewModel
{
    const string VideoFiles = "*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.f4v";

    // ---- Loading video: one table ----

    /// <summary>Takes the loading video out of the catalog's media pack (Popper's Loading\ video).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task GetLoadingVideo(InstalledItem item)
    {
        var table = item.Table;
        if (!item.CanGetLoadingVideo || !TryBeginBusy(table.TablePath))
        {
            return;
        }

        var job = new JobItem($"Loading video for {table.Manifest.GameName}")
        {
            FileNames = [table.Manifest.GameName],
        };
        Jobs.Insert(0, job);
        var problems = new List<string>();
        try
        {
            foreach (var src in AttractAudioSourcesFor(table).Where(s => !s.IsDirect))
            {
                job.Report($"Getting the {src.Label}…");
                try
                {
                    var file = await DownloadAttractSourceAsync(job, src);
                    job.Report("Taking the loading video out…");
                    await KeepWheelFromAsync(file, table, job.Cts.Token);
                    using var video = await Task.Run(() =>
                        LoadingVideo.FromPack(file, table.Manifest.GameName, job.Cts.Token)
                    );
                    if (video == null)
                    {
                        CleanJobDownloads(job);
                        problems.Add($"the {src.Label} has no loading video");
                        continue;
                    }
                    job.Report("Making the loading screen's frames…");
                    var info = await Task.Run(() =>
                        VideoFrames.LoadingFromVideo(
                            video.Path,
                            LoadingVideo.TableFolder(table.Folder),
                            job.Cts.Token
                        )
                    );
                    CleanJobDownloads(job);
                    if (info == null)
                    {
                        problems.Add(
                            $"Windows couldn't play the {src.Label}'s loading video ({video.Ext})"
                        );
                        continue;
                    }
                    job.Finish(
                        $"✓ Saved a {info.Frames / (double)info.Fps:0.#} s loading video from the {src.Label}. The VR lobby plays it while this table starts."
                    );
                    SetHasLoadingVideo(table.TablePath, true);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    CleanJobDownloads(job);
                    problems.Add($"the {src.Label} failed ({e.Message})");
                }
            }
            job.Finish(
                "No loading video: "
                    + string.Join("; ", problems)
                    + ". Use my own loading video… turns any video you have into one.",
                error: true
            );
        }
        catch (OperationCanceledException)
        {
            job.Finish("Cancelled", error: true);
        }
        finally
        {
            CleanJobDownloads(job);
            EndBusy(table.TablePath);
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

        if (!TryBeginBusy(table.TablePath))
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
            EndBusy(table.TablePath);
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
            PlayStatus = "Couldn't remove the loading video: " + e.Message;
            return;
        }
        SetHasLoadingVideo(item.Table.TablePath, false);
        PlayStatus = HasDefaultLoadingVideo
            ? $"Removed {item.Title}'s loading video: it plays the one for every table now."
            : $"Removed {item.Title}'s loading video.";
    }

    void SetHasLoadingVideo(string tablePath, bool on)
    {
        foreach (
            var i in _allInstalled.Where(i =>
                string.Equals(i.Table.TablePath, tablePath, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            i.HasLoadingVideo = on;
        }
    }

    // ---- Loading video: every table (Setup → VR lobby) ----

    /// <summary>"Loading video for every table: 24 s, with sound" or "None…".</summary>
    [ObservableProperty]
    public partial string DefaultLoadingVideoText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasDefaultLoadingVideo { get; set; }

    void RefreshDefaultLoadingVideo()
    {
        var v = LoadingVideo.In(LoadingVideo.DefaultFolder);
        HasDefaultLoadingVideo = v != null;
        DefaultLoadingVideoText =
            v == null
                ? "None: tables without their own loading video show only the table and its lamps."
                : $"{v.Info.Frames / (double)v.Info.Fps:0.#} s, {v.Info.Width}×{v.Info.Height}, "
                    + (v.Audio != null ? "with sound." : "no sound.")
                    + " Plays for every table without its own.";
    }

    [RelayCommand]
    async Task ChooseDefaultLoadingVideo()
    {
        if (await PickLoadingVideoAsync("Choose a loading video for every table") is not { } file)
        {
            return;
        }

        await MakeLoadingVideoAsync(
            file,
            LoadingVideo.DefaultFolder,
            "every table",
            "while a table without its own starts"
        );
        RefreshDefaultLoadingVideo();
    }

    [RelayCommand]
    void RemoveDefaultLoadingVideo()
    {
        try
        {
            LoadingVideo.Remove(LoadingVideo.DefaultFolder);
        }
        catch (Exception e)
        {
            PlayStatus = "Couldn't remove the loading video: " + e.Message;
        }
        RefreshDefaultLoadingVideo();
    }

    Task<string?> PickLoadingVideoAsync(string title) =>
        _ui.PickFileAsync(
            title,
            VideoFiles,
            startFolder: Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        );

    /// <summary>Turns a video into a loading video in <paramref name="folder"/>, as a job. True when it worked.</summary>
    async Task<bool> MakeLoadingVideoAsync(string video, string folder, string forWhat, string when)
    {
        var job = new JobItem($"Loading video for {forWhat}") { FileNames = [forWhat] };
        Jobs.Insert(0, job);
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

    IReadOnlyList<(MediaPackAudio.Source Source, bool WheelPack)> WheelArtSourcesFor(
        InstalledTable t
    ) =>
        t.HasOwnFolder
        && t.Manifest.GameId != ""
        && _allGames.FirstOrDefault(g => g.Id == t.Manifest.GameId) is { } game
            ? WheelArt.SourcesFor(game)
            : [];

    /// <summary>Downloads the catalog's wheel art (or, failing that, its media packs) until one has this game's wheel.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task GetWheelArt(InstalledItem item)
    {
        var table = item.Table;
        if (!item.CanGetWheelArt || !TryBeginBusy(table.TablePath))
        {
            return;
        }

        var job = new JobItem($"Wheel art for {table.Manifest.GameName}")
        {
            FileNames = [table.Manifest.GameName],
        };
        Jobs.Insert(0, job);
        var problems = new List<string>();
        try
        {
            foreach (var (src, wheelPack) in item.WheelArtSources)
            {
                job.Report($"Getting the {src.Label}…");
                try
                {
                    var file = await DownloadAttractSourceAsync(job, src);
                    job.Report("Taking the wheel out…");
                    var saved = await Task.Run(() =>
                        WheelArt.Save(
                            file,
                            table.Folder,
                            WheelArt.Game.Of(table.Manifest),
                            wheelPack,
                            job.Cts.Token
                        )
                    );
                    CleanJobDownloads(job);
                    if (saved != null)
                    {
                        job.Finish(
                            $"✓ Saved medias\\{Path.GetFileName(saved)} from the {src.Label}. The VR lobby shows it over the cabinet when you pick this table."
                        );
                        SetHasWheelArt(table.TablePath, true);
                        return;
                    }
                    problems.Add(
                        wheelPack
                            ? $"the {src.Label} has no wheel named after this game"
                            : $"the {src.Label} has no wheel"
                    );
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    CleanJobDownloads(job);
                    problems.Add($"the {src.Label} failed ({e.Message})");
                }
            }
            job.Finish(
                "No wheel art: "
                    + string.Join("; ", problems)
                    + ". MORE → My artwork → VR lobby wheel art… uses a picture you have.",
                error: true
            );
        }
        catch (OperationCanceledException)
        {
            job.Finish("Cancelled", error: true);
        }
        finally
        {
            CleanJobDownloads(job);
            EndBusy(table.TablePath);
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
            PlayStatus = "Couldn't remove the wheel art: " + e.Message;
            return;
        }
        SetHasWheelArt(item.Table.TablePath, false);
        PlayStatus = $"Removed {item.Title}'s wheel art.";
    }

    /// <summary>
    /// While a media pack is downloaded anyway (for the lobby video, attract audio or loading video), keeps its
    /// wheel when the table has none yet: it reads the pack's file list and one small picture, and saves a second
    /// download later. Cancelling the job stops it too.
    /// </summary>
    async Task KeepWheelFromAsync(string downloaded, InstalledTable table, CancellationToken ct)
    {
        if (!table.HasOwnFolder || WheelArt.Saved(table.Folder) != null)
        {
            return;
        }

        try
        {
            if (
                await Task.Run(() =>
                    WheelArt.Save(
                        downloaded,
                        table.Folder,
                        WheelArt.Game.Of(table.Manifest),
                        wheelPack: false,
                        ct
                    )
                ) != null
            )
            {
                SetHasWheelArt(table.TablePath, true);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        { /* the wheel is a bonus; the download was for something else */
        }
    }

    void SetHasWheelArt(string tablePath, bool on)
    {
        foreach (
            var i in _allInstalled.Where(i =>
                string.Equals(i.Table.TablePath, tablePath, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            i.HasWheelArt = on;
        }
    }
}
