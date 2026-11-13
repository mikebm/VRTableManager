using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Setup → Updates: VR Table Manager's own updates from its GitHub releases.
public sealed partial class UpdatesViewModel
{
    AppRelease? _appLatest;

    public string AppVersionText => $"VR Table Manager {AppUpdater.CurrentVersion}";

    [ObservableProperty]
    public partial string AppUpdateText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasAppUpdate { get; set; }

    [ObservableProperty]
    public partial bool IsUpdatingApp { get; set; }

    [ObservableProperty]
    public partial double AppUpdateProgress { get; set; }

    /// <summary>The app is closing so the update can be copied in (the script waits for it to exit).</summary>
    public bool ClosingForUpdate { get; private set; }

    /// <summary>At startup: say how the last update went, then offer a new release unless it was skipped.</summary>
    internal async Task StartupAppUpdateAsync()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--updated"))
        {
            await Ui.ChoiceAsync(
                "Updated",
                $"VR Table Manager is now version {AppUpdater.CurrentVersion}.",
                ["OK"]
            );
        }
        else if (args.Contains("--update-failed"))
        {
            await Ui.ChoiceAsync(
                "Update didn't finish",
                "The new files couldn't be copied into the app's folder (a file may have been in use). You're still on "
                    + AppUpdater.CurrentVersion
                    + ". Try again from Setup → Updates, or download the release yourself.",
                ["OK"]
            );
        }

        await CheckAppUpdateAsync();
        if (!HasAppUpdate || _appLatest is not { } rel || rel.Version == Settings.SkippedAppVersion)
        {
            return;
        }

        var notes = rel.Notes.Trim();
        if (notes.Length > 600)
        {
            notes = notes[..600].TrimEnd() + "…";
        }

        var pick = await Ui.ChoiceAsync(
            $"VR Table Manager {rel.Version} is out",
            $"You have {AppUpdater.CurrentVersion}. Update now? The app closes, updates and opens again; your tables and settings stay as they are.",
            ["UPDATE NOW", "SKIP THIS VERSION", "LATER"],
            notes == "" ? null : notes
        );
        if (pick == 0)
        {
            await UpdateAppAsync();
        }
        else if (pick == 1)
        {
            Settings.SkippedAppVersion = rel.Version;
            Settings.Save();
            AppUpdateText =
                $"{rel.Version} is available (skipped; it won't be offered at startup again).";
        }
    }

    [RelayCommand]
    async Task CheckAppUpdateAsync()
    {
        if (AppUpdater.IsDevBuild)
        {
            AppUpdateText = $"{AppUpdater.CurrentVersion} (built from source; update with git).";
            HasAppUpdate = false;
            return;
        }
        try
        {
            _appLatest = await AppUpdater.FindLatestAsync(CancellationToken.None);
            HasAppUpdate =
                _appLatest != null
                && AppUpdater.IsNewer(_appLatest.Version, AppUpdater.CurrentVersion);
            AppUpdateText =
                _appLatest == null ? $"{AppUpdater.CurrentVersion}. No release found on GitHub."
                : HasAppUpdate
                    ? $"{AppUpdater.CurrentVersion} → {_appLatest.Version} ({_appLatest.Size / 1_048_576} MB)"
                : $"Up to date ({AppUpdater.CurrentVersion}).";
        }
        catch (Exception e)
        {
            HasAppUpdate = false;
            AppUpdateText = "Couldn't check GitHub: " + e.Message;
        }
    }

    [RelayCommand]
    async Task UpdateAppAsync()
    {
        if (IsUpdatingApp || _appLatest is not { } rel)
        {
            return;
        }

        if (App.Lobby.IsLobbyRunning)
        {
            AppUpdateText = "Quit the VR lobby first, then update.";
            return;
        }
        if (!AppUpdater.CanWriteInstallDir())
        {
            // Under Program Files or another protected folder: let the user replace it themselves.
            AppUpdateText =
                "This app's folder can't be written to, so it can't update itself. Download the new release and unzip it over this one.";
            Ui.OpenExternal(rel.PageUrl);
            return;
        }
        var running = App.Downloads.Jobs.Count(j => !j.IsDone && !j.IsError);
        if (
            running > 0
            && !await Ui.ConfirmAsync(
                "Update now?",
                $"{running} job{(running == 1 ? " is" : "s are")} still running. Unfinished downloads are offered again after the update.",
                "Update now"
            )
        )
        {
            return;
        }

        IsUpdatingApp = true;
        try
        {
            var progress = new Progress<double>(p => AppUpdateProgress = p * 100);
            await AppUpdater.PrepareAsync(
                rel,
                progress,
                s => Avalonia.Threading.Dispatcher.UIThread.Post(() => AppUpdateText = s),
                CancellationToken.None
            );
            // You were asked about running jobs above: the window closes without asking again.
            ClosingForUpdate = true;
            (
                Avalonia.Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime
            )?.Shutdown();
        }
        catch (Exception e)
        {
            AppUpdateText = "Update failed: " + e.Message;
            IsUpdatingApp = false;
        }
    }

    [RelayCommand]
    void OpenAppReleases() => Ui.OpenExternal(_appLatest?.PageUrl ?? AppUpdater.ReleasesPage);
}
