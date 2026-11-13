using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Setup → Storage, Backup and Troubleshooting, and the folder shortcuts there.
public sealed partial class MaintenanceViewModel(MainViewModel app) : ChildViewModel(app)
{
    /// <summary>After a VPX table crashes or closes within seconds, show what its log says.</summary>
    [ObservableProperty]
    public partial bool ExplainFailedLaunches { get; set; }

    partial void OnExplainFailedLaunchesChanged(bool value) =>
        SaveSetting(s => s.ExplainFailedLaunches = value);

    internal override void LoadSettings(AppSettings s) =>
        ExplainFailedLaunches = s.ExplainFailedLaunches;

    [RelayCommand]
    void OpenDataFolder() => App.OpenFolder(AppSettings.DataDir);

    [RelayCommand]
    void OpenVpxPrefsFolder() => App.OpenFolder(AppFolders.VpxPrefs);

    [RelayCommand]
    void OpenUevrFolder()
    {
        if (!string.IsNullOrWhiteSpace(Settings.UevrDir))
        {
            App.OpenFolder(Settings.UevrDir);
        }
    }

    [RelayCommand]
    void OpenFpFolder()
    {
        if (!string.IsNullOrWhiteSpace(Settings.FpDir))
        {
            App.OpenFolder(Settings.FpDir);
        }
    }

    [RelayCommand]
    void OpenPlayLog()
    {
        if (File.Exists(PlaySession.LogPath))
        {
            Ui.OpenExternal(PlaySession.LogPath);
        }
        else
        {
            App.OpenFolder(AppSettings.DataDir);
        }
    }

    // ---- Storage panel -----------------------------------------------------------------------

    public ObservableCollection<StorageEntry> Storage { get; } = [];

    [ObservableProperty]
    public partial bool IsSurveyingStorage { get; set; }

    [ObservableProperty]
    public partial string StorageStatus { get; set; } = "";

    [RelayCommand]
    async Task RefreshStorageAsync()
    {
        IsSurveyingStorage = true;
        try
        {
            var entries = await Task.Run(() => AppFolders.Survey(Settings));
            Storage.Clear();
            foreach (var e in entries)
            {
                Storage.Add(e);
            }

            StorageStatus = $"{DiskUsage.Format(entries.Sum(e => e.Bytes))} in total";
        }
        catch (Exception e)
        {
            StorageStatus = "Couldn't measure the folders: " + e.Message;
        }
        finally
        {
            IsSurveyingStorage = false;
        }
    }

    [RelayCommand]
    void OpenStorage(StorageEntry entry)
    {
        if (entry.Paths.Count == 1)
        {
            App.OpenFolder(entry.Paths[0]);
        }
        else if (entry.Paths.Count > 1)
        {
            App.OpenFolder(Path.GetDirectoryName(entry.Paths[0])!);
        }
    }

    [RelayCommand]
    async Task ClearStorageAsync(StorageEntry entry)
    {
        if (!entry.CanClear)
        {
            return;
        }

        var pick = await Ui.ChoiceAsync(
            $"Clear {entry.Name.ToLowerInvariant()}?",
            $"{entry.Description}\n\nFrees about {entry.SizeText}.",
            ["CLEAR", "CANCEL"]
        );
        if (pick != 0)
        {
            return;
        }

        // Running and queued jobs download into downloads\<job id>: those stay.
        var keep = App.Downloads.Jobs.Where(j => j.IsRunning).Select(j => j.Id).ToList();
        long freed;
        try
        {
            freed = await Task.Run(() => AppFolders.Clear(entry, keep));
        }
        catch (Exception e)
        {
            StorageStatus = "Couldn't clear it: " + e.Message;
            return;
        }
        await RefreshStorageAsync();
        StorageStatus = $"✓ Freed {DiskUsage.Format(freed)}. " + StorageStatus;
    }

    // ---- Backup ------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string BackupStatus { get; set; } = "";

    [RelayCommand]
    async Task CreateBackupAsync()
    {
        Directory.CreateDirectory(AppFolders.Backups);
        var path = await Ui.PickSaveFileAsync(
            "Save backup",
            Backup.DefaultFileName,
            "*.zip",
            AppFolders.Backups
        );
        if (path == null)
        {
            return;
        }

        try
        {
            var r = await Task.Run(() => Backup.Create(path, Settings));
            BackupStatus = $"✓ Backed up {r.Files} files to {path}";
        }
        catch (Exception e)
        {
            BackupStatus = "Backup failed: " + e.Message;
        }
    }

    [RelayCommand]
    async Task RestoreBackupAsync()
    {
        var path = await Ui.PickFileAsync("Choose a VR Table Manager backup", "*.zip");
        if (path == null)
        {
            return;
        }

        var pick = await Ui.ChoiceAsync(
            "Restore this backup?",
            "Favourites, play stats, app and VPX settings, table settings, high scores and UEVR profiles are put back as they were in the backup, "
                + "replacing the current ones. Tables that aren't installed any more are skipped.\n\nClose any running table first.",
            ["RESTORE", "CANCEL"]
        );
        if (pick != 0)
        {
            return;
        }

        try
        {
            var r = await Task.Run(() => Backup.Restore(path, Settings));
            App.ReloadSettings();
            BackupStatus =
                $"✓ Restored {r.Files} files."
                + (
                    r.Skipped.Count > 0
                        ? $" Skipped tables not installed: {string.Join(", ", r.Skipped)}."
                        : ""
                );
        }
        catch (Exception e)
        {
            BackupStatus = "Restore failed: " + e.Message;
        }
    }

    [RelayCommand]
    void OpenBackupsFolder() => App.OpenFolder(AppFolders.Backups);

    internal async Task ShowLogSummaryAsync(string title, string log, VpxLog.Session session)
    {
        var lines = new List<string>();
        if (session.Table != null)
        {
            lines.Add($"Table: {Path.GetFileName(session.Table)}");
        }

        if (session.Started is { } t)
        {
            lines.Add(
                $"Started {t:g} · {(session.ClosedCleanly ? "closed normally" : "did not close normally")} · {(session.UsedVr ? "VR started" : "VR did not start")}"
            );
        }

        lines.AddRange(session.Hints.Select(h => "• " + h));
        var details =
            session.Problems.Count == 0
                ? "No errors in the log."
                : string.Join("\n", session.Problems.Take(30));
        if (session.Harmless.Count > 0)
        {
            details +=
                "\n\nAlso logged, usually harmless (a PuP pack missing some of its files, and the like):\n"
                + string.Join("\n", session.Harmless.Take(15));
        }

        var pick = await Ui.ChoiceAsync(
            title,
            string.Join("\n", lines),
            ["OPEN FULL LOG", "CLOSE"],
            details
        );
        if (pick == 0)
        {
            Ui.OpenExternal(log);
        }
    }

    [RelayCommand]
    async Task ViewLastVpxLogAsync()
    {
        if (VpxLog.Find(Settings) is not { } log)
        {
            await Ui.ChoiceAsync(
                "No VPX log yet",
                "VPX writes its log once a table has been started.",
                ["OK"]
            );
            return;
        }
        if (VpxLog.LastSession(log) is not { } session)
        {
            Ui.OpenExternal(log);
            return;
        }
        await ShowLogSummaryAsync("Last VPX run", log, session);
    }
}
