using CommunityToolkit.Mvvm.Input;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// My Tables → MORE → My artwork: your own card picture, VR lobby playfield and backglass, and lobby video
// (UserArtwork; the files sit in the table's artwork\ folder, where you can also drop them by hand).
public sealed partial class LobbyMediaViewModel
{
    static string Pictures => string.Join(";", UserArtwork.PictureExts.Select(e => "*" + e));
    static string PicturesFolder => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    [RelayCommand]
    Task ChooseCardArt(InstalledItem item) =>
        ChooseArtAsync(item, UserArtwork.Kind.Card, "the card in My Tables");

    [RelayCommand]
    Task ChoosePlayfieldArt(InstalledItem item) =>
        ChooseArtAsync(item, UserArtwork.Kind.Playfield, "the VR lobby playfield");

    [RelayCommand]
    Task ChooseBackglassArt(InstalledItem item) =>
        ChooseArtAsync(item, UserArtwork.Kind.Backglass, "the VR lobby backglass");

    [RelayCommand]
    Task ChooseWheelArt(InstalledItem item) =>
        ChooseArtAsync(item, UserArtwork.Kind.Wheel, "the VR lobby wheel art (the game's logo)");

    async Task ChooseArtAsync(InstalledItem item, UserArtwork.Kind kind, string what)
    {
        if (
            await Ui.PickFileAsync(
                $"Choose a picture for {what} ({item.Title})",
                Pictures,
                startFolder: PicturesFolder
            )
            is not { } file
        )
        {
            return;
        }

        try
        {
            UserArtwork.Set(item.Table, kind, file);
        }
        catch (Exception e)
        {
            App.Library.PlayStatus = $"Couldn't use that picture: {e.Message}";
            return;
        }
        RefreshArtwork(item.Table.TablePath);
        App.Library.PlayStatus =
            kind == UserArtwork.Kind.Card
                ? $"{item.Title} now shows your picture."
                : $"{item.Title}'s cabinet uses your picture the next time the VR lobby opens.";
    }

    [RelayCommand]
    void RemoveUserArtwork(InstalledItem item)
    {
        try
        {
            foreach (var k in Enum.GetValues<UserArtwork.Kind>())
            {
                UserArtwork.Remove(item.Table, k);
            }
        }
        catch (Exception e)
        {
            App.Library.PlayStatus = "Couldn't remove your pictures: " + e.Message;
            return;
        }
        RefreshArtwork(item.Table.TablePath);
        App.Library.PlayStatus = $"{item.Title} is back to the catalog's and the table's own pictures.";
    }

    [RelayCommand]
    void OpenArtworkFolder(InstalledItem item) =>
        Ui.OpenExternal(Directory.CreateDirectory(UserArtwork.FolderFor(item.Table)).FullName);

    /// <summary>Turns a video you have (any length; the first 12 s are used) into the table's VR lobby video.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task UseOwnLobbyVideo(InstalledItem item)
    {
        var table = item.Table;
        if (!item.CanHaveLobbyVideo)
        {
            return;
        }

        if (
            await Ui.PickFileAsync(
                $"Choose a playfield video for {item.Title}",
                "*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv",
                startFolder: Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            )
            is not { } video
        )
        {
            return;
        }

        if (!App.Library.TryBeginBusy(table.TablePath))
        {
            return;
        }

        var job = new JobItem($"Lobby video for {table.Manifest.GameName}")
        {
            FileNames = [table.Manifest.MatchName],
        };
        App.Downloads.Jobs.Insert(0, job);
        try
        {
            job.Report("Making the lobby frames…");
            var info = await Task.Run(() =>
                VideoFrames.FromVideo(video, table.Folder, PlayfieldPicture(table), job.Cts.Token)
            );
            if (info == null)
            {
                job.Finish(
                    $"Windows couldn't play {Path.GetFileName(video)}. Try an .mp4 (H.264).",
                    error: true
                );
                return;
            }
            job.Finish(
                $"✓ Saved a {info.Frames / (double)info.Fps:0.#} s playfield loop from your video. The VR lobby plays it on the cabinet when you pick this table."
            );
            SetHasLobbyVideo(table.TablePath, true);
        }
        catch (OperationCanceledException)
        {
            job.Finish("Cancelled", error: true);
        }
        catch (Exception e)
        {
            job.Finish("Couldn't use that video: " + e.Message, error: true);
        }
        finally
        {
            App.Library.EndBusy(table.TablePath);
        }
    }

    void RefreshArtwork(string tablePath)
    {
        foreach (
            var i in App.Library._allInstalled.Where(i =>
                string.Equals(i.Table.TablePath, tablePath, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            i.RefreshUserArtwork();
        }
    }
}
