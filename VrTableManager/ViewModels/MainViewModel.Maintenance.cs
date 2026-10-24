using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Setup → Storage, Backup and Troubleshooting, plus the folder shortcuts on cards and Setup.
public sealed partial class MainViewModel
{
    // ---- Folder shortcuts ------------------------------------------------------------------

    void OpenFolder(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { /* open what's there */ }
        _ui.OpenExternal(dir);
    }

    [RelayCommand] void OpenVpxFolder() => OpenFolder(Settings.VpxDir);
    [RelayCommand] void OpenTablesFolder() => OpenFolder(Settings.TablesDir);
    [RelayCommand] void OpenDataFolder() => OpenFolder(AppSettings.DataDir);
    [RelayCommand] void OpenVpxPrefsFolder() => OpenFolder(AppFolders.VpxPrefs);
    [RelayCommand] void OpenUevrFolder() { if (!string.IsNullOrWhiteSpace(Settings.UevrDir)) OpenFolder(Settings.UevrDir); }
    [RelayCommand] void OpenFpFolder() { if (!string.IsNullOrWhiteSpace(Settings.FpDir)) OpenFolder(Settings.FpDir); }

    [RelayCommand]
    void OpenVrIni()
    {
        var ini = VpxSetup.VrIniPath(Settings);
        if (!File.Exists(ini)) VpxSetup.EnsureVrIni(Settings);
        _ui.OpenExternal(ini);
    }

    [RelayCommand]
    void OpenPlayLog()
    {
        if (File.Exists(PlaySession.LogPath)) _ui.OpenExternal(PlaySession.LogPath);
        else OpenFolder(AppSettings.DataDir);
    }

    [RelayCommand] void OpenRomFolder(InstalledItem item) => OpenFolder(Path.Combine(item.Table.Folder, "pinmame", "roms"));
    [RelayCommand] void OpenNvramFolder(InstalledItem item) => OpenFolder(Path.Combine(item.Table.Folder, "pinmame", "nvram"));

    [RelayCommand]
    void OpenTableIni(InstalledItem item)
    {
        var ini = VpxGraphics.TableIniPath(item.Table.TablePath);
        if (!File.Exists(ini)) File.WriteAllText(ini, "; Settings for this table only. They override VPX's own settings.\r\n");
        _ui.OpenExternal(ini);
    }

    [RelayCommand]
    void ClearTableCache(InstalledItem item)
    {
        var dir = Path.Combine(item.Table.Folder, "cache");
        var freed = Directory.Exists(dir) ? DiskUsage.SizeOf(dir) : 0;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception e) { PlayStatus = "Couldn't clear the cache (is the table running?): " + e.Message; return; }
        PlayStatus = $"Cleared {item.Title}'s texture cache ({DiskUsage.Format(freed)}). Its next start rebuilds it.";
        _ = RefreshSizesAsync();
    }

    // ---- Disk use on the cards ---------------------------------------------------------------

    [ObservableProperty] public partial string InstalledSizeText { get; set; } = "";
    int _sizeGeneration;

    /// <summary>Works out each table's size and high scores in the background (big folders take a moment).</summary>
    async Task RefreshSizesAsync()
    {
        var gen = ++_sizeGeneration;
        var items = _allInstalled.ToList();
        var sizes = await Task.Run(() => items.Select(i => i.Table.Manifest.Engine is Models.TableManifest.Vpx or Models.TableManifest.FuturePinball
            ? DiskUsage.SizeOf(i.Table, Settings) : 0).ToList());
        var scores = await Task.Run(() => items.Select(i => i.Table.IsVpx ? Lobby.LobbyText.HighScoreLines(HighScores.Read(i.Table)) : []).ToList());
        if (gen != _sizeGeneration) return; // a newer scan replaced these items
        for (var i = 0; i < items.Count; i++)
        {
            items[i].SizeBytes = sizes[i];
            items[i].TopScore = scores[i].FirstOrDefault() ?? "";
            items[i].MoreScores = scores[i].Count > 1 ? scores[i][1].Replace("  ·  ", "\n") : "";
        }
        InstalledSizeText = DiskUsage.Format(sizes.Sum());
        if (InstalledSort == "Largest first") ShowInstalled();
    }

    /// <summary>Downloads the NVRAM maps the first time (and monthly), then shows the scores they unlock.</summary>
    async Task RefreshHighScoreMapsAsync()
    {
        var hadMaps = Directory.Exists(HighScores.MapsDir);
        if (await HighScores.EnsureMapsAsync(CancellationToken.None) && !hadMaps) await RefreshSizesAsync();
    }

    // ---- Storage panel -----------------------------------------------------------------------

    public ObservableCollection<StorageEntry> Storage { get; } = [];
    [ObservableProperty] public partial bool IsSurveyingStorage { get; set; }
    [ObservableProperty] public partial string StorageStatus { get; set; } = "";

    [RelayCommand]
    async Task RefreshStorageAsync()
    {
        IsSurveyingStorage = true;
        try
        {
            var entries = await Task.Run(() => AppFolders.Survey(Settings));
            Storage.Clear();
            foreach (var e in entries) Storage.Add(e);
            StorageStatus = $"{DiskUsage.Format(entries.Sum(e => e.Bytes))} in total";
        }
        finally { IsSurveyingStorage = false; }
    }

    [RelayCommand]
    void OpenStorage(StorageEntry entry)
    {
        if (entry.Paths.Count == 1) OpenFolder(entry.Paths[0]);
        else if (entry.Paths.Count > 1) OpenFolder(Path.GetDirectoryName(entry.Paths[0])!);
    }

    [RelayCommand]
    async Task ClearStorageAsync(StorageEntry entry)
    {
        if (!entry.CanClear) return;
        var pick = await _ui.ChoiceAsync($"Clear {entry.Name.ToLowerInvariant()}?", $"{entry.Description}\n\nFrees about {entry.SizeText}.", ["CLEAR", "CANCEL"]);
        if (pick != 0) return;
        var freed = await Task.Run(() => AppFolders.Clear(entry));
        await RefreshStorageAsync();
        StorageStatus = $"✓ Freed {DiskUsage.Format(freed)}. " + StorageStatus;
    }

    // ---- Backup ------------------------------------------------------------------------------

    [ObservableProperty] public partial string BackupStatus { get; set; } = "";

    [RelayCommand]
    async Task CreateBackupAsync()
    {
        Directory.CreateDirectory(AppFolders.Backups);
        var path = await _ui.PickSaveFileAsync("Save backup", Backup.DefaultFileName, "*.zip", AppFolders.Backups);
        if (path == null) return;
        try
        {
            var r = await Task.Run(() => Backup.Create(path, Settings));
            BackupStatus = $"✓ Backed up {r.Files} files to {path}";
        }
        catch (Exception e) { BackupStatus = "Backup failed: " + e.Message; }
    }

    [RelayCommand]
    async Task RestoreBackupAsync()
    {
        var path = await _ui.PickFileAsync("Choose a VR Table Manager backup", "*.zip");
        if (path == null) return;
        var pick = await _ui.ChoiceAsync("Restore this backup?",
            "Favourites, play stats, app and VPX settings, table settings, high scores and UEVR profiles are put back as they were in the backup, " +
            "replacing the current ones. Tables that aren't installed any more are skipped.\n\nClose any running table first.",
            ["RESTORE", "CANCEL"]);
        if (pick != 0) return;
        try
        {
            var r = await Task.Run(() => Backup.Restore(path, Settings));
            ReloadSettings();
            BackupStatus = $"✓ Restored {r.Files} files." + (r.Skipped.Count > 0 ? $" Skipped tables not installed: {string.Join(", ", r.Skipped)}." : "");
        }
        catch (Exception e) { BackupStatus = "Restore failed: " + e.Message; }
    }

    [RelayCommand] void OpenBackupsFolder() => OpenFolder(AppFolders.Backups);

    /// <summary>After a restore: show the restored settings (the view model's copies would otherwise overwrite them).</summary>
    void ReloadSettings()
    {
        var s = AppSettings.Load();
        s.ManagerExe = Settings.ManagerExe; // this install's path, not the backed-up one
        VpxDir = s.VpxDir;
        TablesDir = s.TablesDir;
        FpDir = s.FpDir;
        UevrDir = s.UevrDir;
        HideAdult = s.HideAdult;
        ReturnAfterPlay = s.ReturnAfterPlay;
        FpRunAsAdmin = s.FpRunAsAdmin;
        ApplyPinballUevrProfile = s.ApplyPinballUevrProfile;
        ShowZenReminder = s.ShowZenReminder;
        MaxParallelDownloads = Math.Clamp(s.MaxParallelDownloads, 1, 4);
        RefreshRate = RefreshRates.FirstOrDefault(r => r.StartsWith(s.VrRefreshRate.ToString())) ?? RefreshRates[0];
        AswMode = s.MetaAswMode;
        WarnSteamVrRuntime = s.WarnSteamVrRuntime;
        ExplainFailedLaunches = s.ExplainFailedLaunches;
        LoadGraphics();
        RefreshInstalled();
        RefreshSetupStatus();
    }
}
