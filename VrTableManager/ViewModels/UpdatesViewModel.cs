using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Setup → Updates: VPX, UEVR and BAM, and UPDATE ALL.
public sealed partial class UpdatesViewModel(MainViewModel app) : ChildViewModel(app)
{
    [ObservableProperty]
    public partial string VpxUpdateText { get; set; } = "Not checked yet.";

    [ObservableProperty]
    public partial bool HasVpxUpdate { get; set; }

    [ObservableProperty]
    public partial string UevrUpdateText { get; set; } = "Not checked yet.";

    [ObservableProperty]
    public partial bool HasUevrUpdate { get; set; }

    [ObservableProperty]
    public partial string UevrButtonText { get; set; } = "INSTALL UEVR";

    [ObservableProperty]
    public partial string BamUpdateText { get; set; } = "Not checked yet.";

    [ObservableProperty]
    public partial bool IsCheckingUpdates { get; set; }

    [ObservableProperty]
    public partial bool IsUpdating { get; set; }

    [ObservableProperty]
    public partial int UpdateCount { get; set; }

    [ObservableProperty]
    public partial string LastChecked { get; set; } = "";

    ToolRelease? _uevrLatest;

    /// <summary>Checks GitHub (VPX, UEVR) and ravarcade.pl (BAM). Runs once at startup after the catalog loads.</summary>
    [RelayCommand]
    public async Task CheckUpdatesAsync()
    {
        IsCheckingUpdates = true;
        try
        {
            var ct = CancellationToken.None;
            var vpxTask = Try<VpxReleases>(async () =>
                await VpxSetup.FindReleasesAsync(
                    ct,
                    Settings.VpxUseDoNotUseBuilds,
                    Settings.VpxUseNightlyBuilds
                )
            );
            var uevrTask = Try(() => UevrSetup.FindLatestAsync(ct));
            var bamTask = Try(() => BamCheck.LatestAsync(ct));
            var appTask = CheckAppUpdateAsync();
            await Task.WhenAll(vpxTask, uevrTask, bamTask, appTask);

            // VPX
            var (vpx, vpxErr) = vpxTask.Result;
            var (vpxText, vpxUpdate) =
                vpx == null
                    ? ("", false)
                    : VpxSetup.DescribeUpdate(
                        vpx,
                        VpxSetup.InstalledVersion(Settings),
                        Settings.VpxKeptBuild
                    );
            HasVpxUpdate = vpxUpdate;
            VpxUpdateText = vpxErr != null ? "Couldn't check GitHub: " + vpxErr : vpxText;

            // UEVR
            var (uevr, uevrErr) = uevrTask.Result;
            _uevrLatest = uevr;
            var uevrHave = UevrSetup.InstalledVersion(Settings);
            HasUevrUpdate = uevr != null && uevrHave != null && uevrHave != uevr.Tag;
            UevrButtonText = uevrHave == null ? "INSTALL UEVR" : "UPDATE UEVR";
            UevrUpdateText =
                uevrErr != null ? "Couldn't check GitHub: " + uevrErr
                : uevr == null ? "No UEVR release found."
                : uevrHave == null
                    ? $"Not installed. Latest is {uevr.Tag} (only needed for Pinball FX)."
                : uevrHave == "unknown"
                    ? $"Installed (version unknown). Latest is {uevr.Tag}; updating makes it trackable."
                : HasUevrUpdate ? $"{uevrHave} → {uevr.Tag}"
                : $"Up to date ({uevrHave}).";

            // BAM: report only.
            var (bam, bamErr) = bamTask.Result;
            var bamHave = BamCheck.InstalledVersion(Settings);
            BamUpdateText =
                bamErr != null ? "Couldn't reach ravarcade.pl: " + bamErr
                : bam == null ? "Couldn't read the latest version from ravarcade.pl."
                : bamHave == null
                    ? $"Latest is v{bam}. BAM isn't installed (only needed for Future Pinball)."
                : $"Latest is v{bam}; yours: {bamHave}. Install from the BAM page if yours is older.";

            App.Library.ApplyTableUpdates();
            LastChecked = $"Checked {DateTime.Now:t}";
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    static async Task<(T? value, string? error)> Try<T>(Func<Task<T?>> f)
        where T : class
    {
        try
        {
            return (await f(), null);
        }
        catch (Exception e)
        {
            return (null, e.Message);
        }
    }

    bool _installingUevr;

    [RelayCommand]
    async Task InstallUevrAsync()
    {
        if (_installingUevr)
        {
            return; // UPDATE ALL while UPDATE UEVR runs
        }

        _installingUevr = true;
        var job = new JobItem("UEVR");
        App.Downloads.Jobs.Insert(0, job);
        try
        {
            var rel =
                _uevrLatest
                ?? await UevrSetup.FindLatestAsync(CancellationToken.None)
                ?? throw new InvalidOperationException("No UEVR release found.");
            job.Report($"Downloading UEVR {rel.Tag}…");
            await UevrSetup.InstallAsync(Settings, rel, job.ProgressReporter, job.Cts.Token);
            App.OtherGames.UevrDir = Settings.UevrDir;
            job.Finish(
                $"✓ UEVR {rel.Tag} installed. Your Pinball FX / Pinball M profiles are untouched."
            );
        }
        catch (Exception e)
        {
            job.Finish("Failed: " + e.Message, error: true);
        }
        finally
        {
            _installingUevr = false;
            App.RefreshSetupStatus();
            await CheckUpdatesAsync();
        }
    }

    [RelayCommand]
    async Task UpdateAllAsync()
    {
        IsUpdating = true;
        try
        {
            if (HasVpxUpdate)
            {
                await App.Vpx.InstallVpxAsync();
            }

            if (HasUevrUpdate)
            {
                await InstallUevrAsync();
            }

            await App.Library.UpdateAllTablesAsync();
            await App.Addons.UpdateAllAddonsAsync();
            await CheckUpdatesAsync();
        }
        finally
        {
            IsUpdating = false;
        }
    }

}
