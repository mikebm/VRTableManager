using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Setup → VPX: where VPX and the tables are, installing VPX builds, and its settings files.
public sealed partial class VpxViewModel(MainViewModel app) : ChildViewModel(app)
{
    // ---- Folders and which builds to offer ----

    [ObservableProperty]
    public partial string VpxDir { get; set; } = "";

    /// <summary>Every table folder, the install one first (<see cref="AppSettings.AllTablesDirs"/>).</summary>
    public ObservableCollection<TableFolderRow> TableFolders { get; } = [];

    [ObservableProperty]
    public partial bool VpxUseDoNotUseBuilds { get; set; }

    [ObservableProperty]
    public partial bool VpxUseNightlyBuilds { get; set; }

    [ObservableProperty]
    public partial string VpxStatus { get; set; } = "";

    partial void OnVpxDirChanged(string value)
    {
        SaveSetting(s => s.VpxDir = value);
        App.Graphics.LoadGraphics();
        App.RefreshSetupStatus();
        App.Library.RefreshInstalled();
    }

    /// <summary>Saves the table folders (<paramref name="install"/> first, the one new tables go into) and lists them again.</summary>
    void SaveTableFolders(string install, IEnumerable<string> others)
    {
        SaveSetting(s =>
        {
            s.TablesDir = install;
            s.OtherTablesDirs = [.. others];
        });
        ShowTableFolders(Settings);
        App.RefreshSetupStatus();
        App.Library.RefreshInstalled();
    }

    void ShowTableFolders(AppSettings s)
    {
        TableFolders.Clear();
        foreach (var d in s.AllTablesDirs)
        {
            TableFolders.Add(
                new TableFolderRow(
                    d,
                    IsInstallFolder: TableFolders.Count == 0,
                    Missing: !Directory.Exists(d),
                    CanRemove: s.AllTablesDirs.Count > 1
                )
            );
        }
    }

    partial void OnVpxUseDoNotUseBuildsChanged(bool value) =>
        SaveSetting(s => s.VpxUseDoNotUseBuilds = value);

    partial void OnVpxUseNightlyBuildsChanged(bool value) =>
        SaveSetting(s => s.VpxUseNightlyBuilds = value);

    internal override void LoadSettings(AppSettings s)
    {
        VpxDir = s.VpxDir;
        ShowTableFolders(s);
        VpxUseDoNotUseBuilds = s.VpxUseDoNotUseBuilds;
        VpxUseNightlyBuilds = s.VpxUseNightlyBuilds;
    }

    internal void RefreshStatus() =>
        VpxStatus = Engines.For(TableManifest.Vpx).DescribeSetup(Settings);

    // ---- Installing VPX ----

    [ObservableProperty]
    public partial string LatestVpx { get; set; } = "";

    [ObservableProperty]
    public partial bool IsInstallingVpx { get; set; }

    [ObservableProperty]
    public partial double VpxProgress { get; set; }

    [RelayCommand]
    public async Task CheckVpxUpdateAsync()
    {
        try
        {
            var releases = await VpxSetup.FindReleasesAsync(
                CancellationToken.None,
                Settings.VpxUseDoNotUseBuilds,
                Settings.VpxUseNightlyBuilds
            );
            var (text, hasUpdate) = VpxSetup.DescribeUpdate(
                releases,
                VpxSetup.InstalledVersion(Settings),
                Settings.VpxKeptBuild
            );
            LatestVpx = hasUpdate ? $"{text} ({releases.Latest!.Size / 1_048_576} MB)" : text;
        }
        catch (Exception e)
        {
            LatestVpx = "Couldn't check GitHub: " + e.Message;
        }
    }

    CancellationTokenSource? _vpxInstallCts;

    /// <summary>The bar can't show progress (checking GitHub, extracting): it just shows activity.</summary>
    [ObservableProperty]
    public partial bool VpxProgressUnknown { get; set; }

    [RelayCommand]
    void CancelVpxInstall() => _vpxInstallCts?.Cancel();

    /// <summary>The build list: releases and (with nightly builds on) recent nightlies, to go back to an older one.</summary>
    public ObservableCollection<VpxBuildChoice> VpxBuilds { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallChosenVpxCommand))]
    public partial VpxBuildChoice? ChosenVpxBuild { get; set; }

    [ObservableProperty]
    public partial bool HasVpxBuilds { get; set; }

    [RelayCommand]
    async Task LoadVpxBuildsAsync()
    {
        LatestVpx = "Getting the list of VPX builds from GitHub…";
        try
        {
            var builds = await VpxSetup.FindBuildsAsync(
                CancellationToken.None,
                Settings.VpxUseDoNotUseBuilds,
                Settings.VpxUseNightlyBuilds
            );
            VpxBuilds.Clear();
            foreach (var b in builds)
            {
                VpxBuilds.Add(b);
            }
            HasVpxBuilds = VpxBuilds.Count > 0;
            MarkInstalledVpxBuild();
            ChosenVpxBuild =
                VpxBuilds.FirstOrDefault(b => b.Installed) ?? VpxBuilds.FirstOrDefault();
            LatestVpx = HasVpxBuilds
                ? $"{VpxBuilds.Count} builds. Pick one and click INSTALL THIS BUILD."
                    + (
                        Settings.VpxUseNightlyBuilds
                            ? ""
                            : " Turn on nightly builds below to list those too."
                    )
                : "No VPX builds found for this OS.";
        }
        catch (Exception e)
        {
            LatestVpx = "Couldn't check GitHub: " + e.Message;
        }
    }

    /// <summary>Marks which build in the list is the installed one (after the list loads, and after an install).</summary>
    void MarkInstalledVpxBuild()
    {
        var installed = VpxSetup.InstalledVersion(Settings);
        var chosen = ChosenVpxBuild;
        for (var i = 0; i < VpxBuilds.Count; i++)
        {
            var b = VpxBuilds[i];
            var now = VpxSetup.IsInstalled(b, installed);
            if (b.Installed != now)
            {
                VpxBuilds[i] = b with { Installed = now };
                if (ReferenceEquals(b, chosen))
                {
                    ChosenVpxBuild = VpxBuilds[i];
                }
            }
        }
    }

    bool CanInstallChosenVpx() => ChosenVpxBuild != null;

    /// <summary>The installed build can be picked too: installing it again repairs a damaged VPX folder.</summary>
    public string ChosenVpxButtonText =>
        ChosenVpxBuild is { Installed: true } ? "REINSTALL THIS BUILD" : "INSTALL THIS BUILD";

    partial void OnChosenVpxBuildChanged(VpxBuildChoice? value) =>
        OnPropertyChanged(nameof(ChosenVpxButtonText));

    /// <summary>
    /// Installs the build picked from the list, older or newer, and keeps it: newer builds aren't offered as
    /// updates until INSTALL / UPDATE VPX is clicked.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallChosenVpx))]
    Task InstallChosenVpxAsync() =>
        RunVpxInstallAsync(async ct =>
        {
            if (ChosenVpxBuild is not { } build)
            {
                return null;
            }
            LatestVpx = $"Finding the download for {build.Label}…";
            if (await VpxSetup.ResolveAsync(build, ct) is not { } rel)
            {
                LatestVpx =
                    "GitHub no longer has that nightly's download (they expire after a while). Pick a newer one.";
                return null;
            }
            Settings.VpxKeptBuild = rel.Tag;
            Settings.Save();
            return rel;
        });

    [RelayCommand]
    internal Task InstallVpxAsync() =>
        RunVpxInstallAsync(async ct =>
        {
            // The newest build again: a build picked from the list stops being kept.
            if (Settings.VpxKeptBuild != null)
            {
                Settings.VpxKeptBuild = null;
                Settings.Save();
            }
            LatestVpx = "Checking GitHub for the newest VPX build…";
            var releases = await VpxSetup.FindReleasesAsync(
                ct,
                Settings.VpxUseDoNotUseBuilds,
                Settings.VpxUseNightlyBuilds
            );
            var installed = VpxSetup.InstalledVersion(Settings);
            var (text, hasUpdate) = VpxSetup.DescribeUpdate(releases, installed);
            // Only install what's offered: no "DO NOT USE" build unless allowed, never an older one over a newer install.
            if (releases.Latest is not { } rel || (installed != null && !hasUpdate))
            {
                LatestVpx = text;
                return null;
            }
            return rel;
        });

    /// <summary>Installs the build <paramref name="pick"/> chooses (null: nothing to install; it says why).</summary>
    async Task RunVpxInstallAsync(Func<CancellationToken, Task<VpxRelease?>> pick)
    {
        if (IsInstallingVpx)
        {
            return; // UPDATE ALL while an install from the VPX section runs: one at a time into the VPX folder
        }

        IsInstallingVpx = true;
        VpxProgress = 0;
        VpxProgressUnknown = true;
        using var cts = _vpxInstallCts = new CancellationTokenSource();
        try
        {
            if (await pick(cts.Token) is not { } rel)
            {
                return;
            }
            var progress = new Progress<double>(p =>
            {
                VpxProgressUnknown = false;
                VpxProgress = p * 100;
            });
            await VpxSetup.InstallAsync(
                Settings,
                rel,
                progress,
                s =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        LatestVpx = s;
                        if (s.StartsWith("Extracting"))
                        {
                            VpxProgressUnknown = true;
                        }
                    }),
                cts.Token
            );
            LatestVpx =
                $"Installed {rel.Tag}."
                + (
                    Settings.VpxKeptBuild == rel.Tag
                        ? " Newer builds won't be offered as updates until you click INSTALL / UPDATE VPX."
                        : ""
                );
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            LatestVpx = "Install cancelled.";
        }
        catch (Exception e)
        {
            LatestVpx =
                "Install failed: "
                + e.Message
                + e switch
                {
                    UnauthorizedAccessException =>
                        $" Windows didn't allow writing to {Settings.VpxDir}. Pick a folder you own (Browse), or allow VR Table Manager in Windows Security → Ransomware protection → Controlled folder access.",
                    IOException
                        when e.HResult
                            is unchecked((int)0x80070020)
                                or unchecked((int)0x80070021) =>
                        " A VPX file is in use: close VPX (and the VR lobby) and try again.",
                    HttpRequestException or TimeoutException =>
                        " If it keeps failing, download the Windows BGFX zip yourself from github.com/vpinball/vpinball/releases, unzip it into a folder and choose that folder above.",
                    _ => "",
                };
        }
        finally
        {
            _vpxInstallCts = null;
            VpxProgressUnknown = false;
            IsInstallingVpx = false;
            MarkInstalledVpxBuild();
            App.RefreshSetupStatus();
            await App.Updates.CheckUpdatesAsync();
        }
    }

    [RelayCommand]
    async Task BrowseVpxDirAsync()
    {
        if (await Ui.PickFolderAsync("VPX install folder") is { } d)
        {
            VpxDir = d;
        }
    }

    [ObservableProperty]
    public partial string TableFoldersStatus { get; set; } = "";

    /// <summary>Adds a table folder. Its tables show up in My Tables and the VR lobby; new installs still go where they did.</summary>
    [RelayCommand]
    async Task AddTableFolderAsync()
    {
        if (await Ui.PickFolderAsync("Another folder for VPX tables") is not { } d)
        {
            return;
        }

        var s = Settings;
        if (s.IsTablesRoot(d))
        {
            TableFoldersStatus = "That folder is already in the list.";
            return;
        }
        // A folder inside another (or around one) would list its tables twice, or a table folder as a table.
        if (s.AllTablesDirs.FirstOrDefault(t => Inside(d, t) || Inside(t, d)) is { } clash)
        {
            TableFoldersStatus =
                $"That folder is inside {clash}, or holds it. Pick a folder of its own, beside the others.";
            return;
        }
        TableFoldersStatus = "";
        SaveTableFolders(s.TablesDir, [.. s.OtherTablesDirs, d]);

        static bool Inside(string folder, string root) =>
            Path.GetFullPath(folder)
                .TrimEnd('\\', '/')
                .StartsWith(
                    Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                );
    }

    /// <summary>New tables (downloads and copies from another install) go into this folder from now on.</summary>
    [RelayCommand]
    void MakeInstallFolder(TableFolderRow row)
    {
        var s = Settings;
        TableFoldersStatus = "";
        SaveTableFolders(
            row.Path,
            s.AllTablesDirs.Where(d =>
                !string.Equals(d, row.Path, StringComparison.OrdinalIgnoreCase)
            )
        );
    }

    /// <summary>Takes a folder off the list. Nothing is deleted: its tables just stop showing until it's added again.</summary>
    [RelayCommand]
    void RemoveTableFolder(TableFolderRow row)
    {
        var rest = Settings
            .AllTablesDirs.Where(d =>
                !string.Equals(d, row.Path, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        if (rest.Count == 0)
        {
            return; // there's always one to install into
        }
        // Taking the install folder off: new tables go into the first other folder that's there right now.
        var install = row.IsInstallFolder
            ? rest.FirstOrDefault(Directory.Exists)
            : Settings.TablesDir;
        if (install == null)
        {
            TableFoldersStatus =
                "None of the other folders are there right now (a drive that isn't plugged in?), so new tables would have nowhere to go. Plug it in or add another folder first.";
            return;
        }
        TableFoldersStatus =
            $"Took {row.Path} off the list. Its tables are still on disk; ADD TABLE FOLDER brings them back.";
        SaveTableFolders(
            install,
            rest.Where(d => !string.Equals(d, install, StringComparison.OrdinalIgnoreCase))
        );
    }

    [RelayCommand]
    void OpenTableFolder(TableFolderRow row) => App.OpenFolder(row.Path);

    [ObservableProperty]
    public partial string ImportStatus { get; set; } = "";

    /// <summary>Copies the tables of another VPX install (its folder or its Tables folder) into the tables folder.</summary>
    [RelayCommand]
    async Task ImportTablesAsync()
    {
        if (await Ui.PickFolderAsync("Another VPX install, or its Tables folder") is not { } src)
        {
            return;
        }

        if (TableImport.FindTablesDir(src) == null)
        {
            ImportStatus = "No .vpx tables in that folder or its Tables folder.";
            return;
        }
        var job = new JobItem(
            "Copy tables from " + Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar))
        );
        App.Downloads.Jobs.Insert(0, job);
        ImportStatus = "Copying tables…";
        try
        {
            var r = await Task.Run(() =>
                TableImport.Import(src, Settings, job.Report, job.Cts.Token)
            );
            var text =
                $"✓ Copied {r.Copied.Count} table{(r.Copied.Count == 1 ? "" : "s")}."
                + (r.Skipped.Count > 0 ? $" {r.Skipped.Count} already here, left as they are." : "")
                + (r.Failed.Count > 0 ? $" Couldn't copy: {string.Join("; ", r.Failed)}" : "");
            job.Finish(text, error: r.Copied.Count == 0 && r.Failed.Count > 0);
            ImportStatus = text;
        }
        catch (OperationCanceledException)
        {
            job.Finish("Cancelled", error: true);
            ImportStatus = "Cancelled.";
        }
        catch (Exception e)
        {
            job.Finish("Couldn't copy tables: " + e.Message, error: true);
            ImportStatus = "Couldn't copy tables: " + e.Message;
        }
        finally
        {
            App.Library.RefreshInstalled();
        }
    }

    [RelayCommand]
    void OpenVpxFolder() => App.OpenFolder(Settings.VpxDir);

    /// <summary>What happened when opening a settings file (shown under the buttons).</summary>
    [ObservableProperty]
    public partial string SettingsFilesStatus { get; set; } = "";

    /// <summary>The player's own VPinballX.ini: the bottom layer for both VR and desktop play.</summary>
    [RelayCommand]
    void OpenOwnVpxIni()
    {
        if (VpxIniLayers.BasePath(Settings) is { } ini)
        {
            SettingsFilesStatus = "";
            Ui.OpenExternal(ini);
        }
        else
        {
            SettingsFilesStatus =
                "VPX hasn't saved settings of its own yet. Start VPinballX.exe on its own once (not from here) and it creates VPinballX.ini.";
        }
    }

    [RelayCommand]
    void OpenVrIni()
    {
        var ini = VpxSetup.VrIniPath(Settings);
        if (!File.Exists(ini))
        {
            VpxSetup.EnsureVrIni(Settings);
        }

        Ui.OpenExternal(ini);
    }

    [RelayCommand]
    void OpenDesktopIni()
    {
        var ini = VpxSetup.DesktopIniPath(Settings);
        if (!File.Exists(ini))
        {
            VpxSetup.EnsureDesktopIni(Settings);
        }

        Ui.OpenExternal(ini);
    }
}

/// <summary>
/// A row in Setup's table folder list: where it is, whether new tables install into it, whether it's there right now
/// (a drive that isn't plugged in), and whether it can be taken off the list (not the only one).
/// </summary>
public sealed record TableFolderRow(string Path, bool IsInstallFolder, bool Missing, bool CanRemove)
{
    public bool CanMakeInstallFolder => !IsInstallFolder && !Missing;

    /// <summary>The install folder is made on the first install, so only another missing folder says NOT FOUND.</summary>
    public bool ShowNotFound => !IsInstallFolder && Missing;
}
