using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// The VR lobby: launching it, and Setup → VR lobby.
public sealed partial class LobbyViewModel(MainViewModel app) : ChildViewModel(app)
{
    /// <summary>The VR lobby records a ROM table's start-up music in the background the first time you stop on it.</summary>
    [ObservableProperty]
    public partial bool LobbyRecordsRomAudio { get; set; }

    /// <summary>The VR lobby plays lobby videos on the selected cabinet's playfield.</summary>
    [ObservableProperty]
    public partial bool LobbyPlayfieldVideos { get; set; }

    /// <summary>The VR lobby shows a loading screen until the picked table takes the headset over.</summary>
    [ObservableProperty]
    public partial bool LobbyWaitsForTable { get; set; }

    /// <summary>The VR lobby stays open, hidden, while you play, and takes the headset back when the table closes.</summary>
    [ObservableProperty]
    public partial bool LobbyStaysOpen { get; set; }

    /// <summary>The loading screen plays the table's loading video (or the one for every table) on a big screen.</summary>
    [ObservableProperty]
    public partial bool LobbyLoadingVideos { get; set; }

    partial void OnLobbyRecordsRomAudioChanged(bool value) =>
        SaveSetting(s => s.LobbyRecordsRomAudio = value);

    partial void OnLobbyPlayfieldVideosChanged(bool value) =>
        SaveSetting(s => s.LobbyPlayfieldVideos = value);

    partial void OnLobbyWaitsForTableChanged(bool value) =>
        SaveSetting(s => s.LobbyWaitsForTable = value);

    partial void OnLobbyStaysOpenChanged(bool value) => SaveSetting(s => s.LobbyStaysOpen = value);

    partial void OnLobbyLoadingVideosChanged(bool value) =>
        SaveSetting(s => s.LobbyLoadingVideos = value);

    internal override void LoadSettings(AppSettings s)
    {
        LobbyRecordsRomAudio = s.LobbyRecordsRomAudio;
        LobbyPlayfieldVideos = s.LobbyPlayfieldVideos;
        LobbyWaitsForTable = s.LobbyWaitsForTable;
        LobbyStaysOpen = s.LobbyStaysOpen;
        LobbyLoadingVideos = s.LobbyLoadingVideos;
        LobbyCabinetCount = s.LobbyCabinetCount;
        LobbyCabinetScale = s.LobbyCabinetScalePercent;
    }

    // ---- VR lobby ------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsLobbyRunning { get; set; }

    /// <summary>
    /// One click: get Godot (first time), rebuild the lobby if its code changed, start it, and get
    /// out of the way until you're done. The lobby quits while a table runs (handing the headset to
    /// the table) and is restarted afterwards, so this waits until neither is running.
    /// </summary>
    [RelayCommand]
    async Task LaunchLobbyAsync()
    {
        if (App.Media.IsBatchRecording && App.Media._batchUsesVpx)
        {
            App.Library.PlayStatus =
                "Tables are being recorded in VPX: wait for them, or press STOP, then launch the VR lobby.";
            return;
        }

        if (IsPreparingLobbyTables)
        {
            App.Library.PlayStatus =
                "Tables are being prepared for the VR lobby: wait for it, or cancel it under Downloads, then launch.";
            return;
        }

        if (IsLobbyRunning)
        {
            return;
        }

        // One question per launch: preparing the tables first, else the lobby video tip.
        var prepare = await LobbyPrepareTipAsync();
        if (!prepare.Launch || (!prepare.Shown && !await App.Media.LobbyVideoTipAsync()))
        {
            return;
        }

        if (!LobbyRunner.Available)
        {
            App.Library.PlayStatus = "Couldn't find the VrLobby folder next to this app.";
            return;
        }
        // An exported lobby (a release) brings its own .NET. Run as a project, the lobby uses this app's own .NET
        // when it can, else the installed one. Off the UI thread: it runs mklink and copies hostfxr.dll.
        var exported = LobbyRunner.FindExported() != null;
        var (dotnetRoot, noBundled) = exported
            ? (null, "")
            : await Task.Run(() =>
            {
                var r = LobbyDotnet.Prepare(
                    AppContext.BaseDirectory,
                    LobbyDotnet.DefaultRoot,
                    Environment.Version.ToString(),
                    out var why
                );
                return (r, why);
            });
        if (
            !exported
            && dotnetRoot == null
            && !LobbyDotnet.HasLobbyRuntime(LobbyDotnet.DotnetRoots())
        )
        {
            // Without one Godot opens a black, empty lobby with no error to go on.
            var missing = new JobItem("VR lobby");
            App.Downloads.Jobs.Insert(0, missing);
            missing.Finish(
                $"The VR lobby couldn't use this app's own .NET ({noBundled}) and no .NET 10 or newer is installed. Install \".NET Runtime 10\" for Windows x64 from "
                    + LobbyDotnet.RuntimeUrl
                    + ", then start the lobby again.",
                error: true
            );
            App.Library.PlayStatus = "";
            return;
        }
        if (!await App.Play.EnsureHeadsetAsync())
        {
            App.Library.PlayStatus = "";
            return;
        }

        IsLobbyRunning = true;
        // Listed under Downloads only while it's being prepared (first-time Godot download, a rebuild) or if it
        // fails: once the lobby is up there's nothing to follow, and a finished entry would need clearing.
        var job = new JobItem("VR lobby");
        App.Downloads.Jobs.Insert(0, job);
        var hidden = false;
        try
        {
            var program = await LobbyRunner.PrepareAsync(
                dotnetRoot,
                job.ProgressReporter,
                job.Report,
                job.Cts.Token
            );
            using var lobby = LobbyRunner.Start(program, dotnetRoot);
            App.Downloads.Jobs.Remove(job);
            App.Library.PlayStatus =
                "VR lobby running. Put your headset on; this window comes back when you quit the lobby.";
            Ui.HideForPlay();
            hidden = true;
            await WaitForLobbySessionAsync(lobby, program.ProcessName);
            App.Library.PlayStatus = "Back from the VR lobby.";
        }
        catch (OperationCanceledException)
        {
            App.Downloads.Jobs.Remove(job);
            App.Library.PlayStatus = "VR lobby cancelled.";
        }
        catch (Exception e)
        {
            job.Finish("Couldn't start the lobby: " + e.Message, error: true);
            App.Library.PlayStatus = "";
        }
        finally
        {
            IsLobbyRunning = false;
            if (hidden)
            {
                Ui.ShowAfterPlay();
            }

            App.Library.RefreshInstalled(); // favourites and plays may have changed in the lobby
        }
    }

    /// <summary>Waits for the lobby to exit, then keeps waiting while a table it launched (and the lobby it restarts) runs.</summary>
    static async Task WaitForLobbySessionAsync(
        System.Diagnostics.Process lobby,
        string godotProcessName
    )
    {
        var lobbyStarted = lobby.StartTime;
        await lobby.WaitForExitAsync();
        var self = Environment.ProcessId;
        bool StartedSinceLobby(System.Diagnostics.Process p)
        {
            try
            {
                return p.Id != self && p.StartTime >= lobbyStarted;
            }
            catch
            {
                return false;
            }
        }
        bool Busy() =>
            System.Diagnostics.Process.GetProcessesByName(godotProcessName).Length > 0
            // "VrTableManager.exe --play …" is how the lobby launches a table (another manager window
            // you opened yourself started earlier, so it doesn't count).
            || System
                .Diagnostics.Process.GetProcessesByName("VrTableManager")
                .Any(StartedSinceLobby);
        // Allow a few seconds for the hand-over between the lobby quitting and the table (or lobby) starting.
        var idleSince = DateTime.UtcNow;
        while (DateTime.UtcNow - idleSince < TimeSpan.FromSeconds(4))
        {
            await Task.Delay(500);
            if (Busy())
            {
                idleSince = DateTime.UtcNow;
            }
        }
    }

    // ---- Loading video: every table (Setup → VR lobby) ----

    /// <summary>"Loading video for every table: 24 s, with sound" or "None…".</summary>
    [ObservableProperty]
    public partial string DefaultLoadingVideoText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasDefaultLoadingVideo { get; set; }

    internal void RefreshDefaultLoadingVideo()
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
        if (
            await App.Media.PickLoadingVideoAsync("Choose a loading video for every table")
            is not { } file
        )
        {
            return;
        }

        await App.Media.MakeLoadingVideoAsync(
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
            App.Library.PlayStatus = "Couldn't remove the loading video: " + e.Message;
        }
        RefreshDefaultLoadingVideo();
    }
}
