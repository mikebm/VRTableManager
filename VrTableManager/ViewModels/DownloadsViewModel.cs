using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Downloads: the jobs list, the download queue and the browsers that fetch table, ROM and add-on files.
// Setup → Downloads binds here too.
public sealed partial class DownloadsViewModel : ChildViewModel, IAsyncDisposable
{
    internal BrowserDownloader Browser { get; } = new();

    /// <summary>Table sites: you download in your own browser, the app takes it from there (see SitePolicy).</summary>
    internal AssistedDownloader Assisted { get; }

    /// <summary>Saved queue: downloads not finished when the app closed are offered again at the next launch.</summary>
    internal PendingDownloads Pending { get; } = new();

    /// <summary>The saved queue as it was at launch: downloads started since (while the startup checks ran) aren't resumed.</summary>
    internal IReadOnlyList<PendingDownload> LeftFromLastTime { get; }

    /// <summary>The catalog's machine names, which tell the assisted downloader which download is which.</summary>
    internal IEnumerable<string> KnownGames
    {
        set => Assisted.KnownGames = value;
    }

    public DownloadsViewModel(MainViewModel app)
        : base(app)
    {
        LeftFromLastTime = Pending.Items;
        Assisted = new AssistedDownloader(app.Ui.OpenExternal);
        Jobs.CollectionChanged += (_, e) =>
        {
            foreach (JobItem j in e.NewItems ?? Array.Empty<JobItem>())
            {
                j.Succeeded += OnJobSucceeded;
            }
        };
        // Partial files from downloads cut off when the app last closed. (Their queue entries are
        // kept and offered again once the catalog has loaded.)
        PendingDownloads.CleanDownloadsFolder();
    }

    // ---- Setup → Downloads ----

    [ObservableProperty]
    public partial bool DeleteDownloadsAfterInstall { get; set; }

    partial void OnDeleteDownloadsAfterInstallChanged(bool value)
    {
        Settings.DeleteDownloadsAfterInstall = value;
        Settings.Save();
    }

    internal override void LoadSettings(AppSettings s)
    {
        MaxParallelDownloads = Math.Clamp(s.MaxParallelDownloads, 1, 4);
        DeleteDownloadsAfterInstall = s.DeleteDownloadsAfterInstall;
    }

    // ---- Jobs ----

    public ObservableCollection<JobItem> Jobs { get; } = [];

    public string DownloadsFolderText =>
        $"Files are picked up from {Assisted.DownloadsDir}. Saved somewhere else? Use I SAVED IT on the download.";

    /// <summary>A job waiting on you in the browser: you saved the file somewhere other than the Downloads folder.</summary>
    [RelayCommand]
    async Task PickDownloadedFileAsync(JobItem job)
    {
        if (
            await Ui.PickFileAsync(
                "The file you downloaded",
                "*.*",
                startFolder: Assisted.DownloadsDir
            ) is
            { } file
        )
        {
            job.HandOff(file);
        }
    }

    /// <summary>
    /// Downloads a file straight (GitHub etc.); from a table site, opens its page in your browser and
    /// takes the file you download there (or drives the sign-in browser, for a site that allows it).
    /// Downloads in your browser don't take a download slot: the browser does the downloading, and you may
    /// have several pages counting down at once.
    /// </summary>
    internal Task<string> FetchAsync(string url, JobItem job, string[] hint, string? what = null)
    {
        var downloads = JobDownloadDir(job);
        if (!BrowserDownloader.IsDirectFileUrl(url) && !SitePolicy.AllowsAutomation(url))
        {
            return Assisted.DownloadAsync(url, downloads, job, what);
        }

        return WithDownloadSlotAsync(
            job,
            async () =>
            {
                if (!BrowserDownloader.IsDirectFileUrl(url))
                {
                    return await Browser.DownloadAsync(
                        url,
                        downloads,
                        hint,
                        job.Report,
                        job.Cts.Token
                    );
                }

                var file = Path.Combine(
                    downloads,
                    TableInstaller.SafeName(Path.GetFileName(new Uri(url).LocalPath))
                );
                job.Report("Downloading…");
                await Http.DownloadFileAsync(url, file, job.ProgressReporter, job.Cts.Token);
                return file;
            }
        );
    }

    // ---- Download queue ----------------------------------------------------------
    // Downloads run a few at a time (Setup → "Downloads at once", default 2); the rest wait in the
    // queue. Unpacking and installing don't take a slot, so the next download starts meanwhile.

    public int[] ParallelDownloadChoices { get; } = [1, 2, 3, 4];

    [ObservableProperty]
    public partial int MaxParallelDownloads { get; set; }
    SemaphoreSlim _downloadSlots = new(2);

    partial void OnMaxParallelDownloadsChanged(int value)
    {
        value = Math.Clamp(value, 1, 4);
        Settings.MaxParallelDownloads = value;
        Settings.Save();
        // Jobs already holding a slot release it to the old gate; new jobs use the new size.
        _downloadSlots = new SemaphoreSlim(value);
    }

    internal async Task<T> WithDownloadSlotAsync<T>(JobItem job, Func<Task<T>> download)
    {
        var slots = _downloadSlots;
        if (slots.CurrentCount == 0)
        {
            job.Report("Queued: waiting for a free download slot…");
        }

        await slots.WaitAsync(job.Cts.Token);
        try
        {
            return await download();
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>Each job downloads into its own folder, so parallel downloads with the same file name can't collide.</summary>
    internal static string JobDownloadDir(JobItem job) =>
        Directory.CreateDirectory(Path.Combine(AppSettings.DataDir, "downloads", job.Id)).FullName;

    /// <summary>
    /// A job installed what it downloaded: delete the copy your browser saved (Setup → "Delete downloads after
    /// installing"). Only on success: a failed install keeps the file, so you can try again with it.
    /// </summary>
    void OnJobSucceeded(JobItem job)
    {
        if (!Settings.DeleteDownloadsAfterInstall)
        {
            return;
        }

        foreach (var f in job.TakeSourceFiles())
        {
            try
            {
                File.Delete(f);
            }
            catch
            { /* open elsewhere or already gone: leave it */
            }
        }
    }

    /// <summary>Takes a finished or failed job off the downloads list.</summary>
    [RelayCommand]
    void DismissJob(JobItem job)
    {
        if (job.IsDone || job.IsError)
        {
            Jobs.Remove(job);
        }
    }

    internal static void CleanJobDownloads(JobItem job)
    {
        try
        {
            Directory.Delete(
                Path.Combine(AppSettings.DataDir, "downloads", job.Id),
                recursive: true
            );
        }
        catch { }
    }

    [RelayCommand]
    void ClearFinishedJobs()
    {
        foreach (var j in Jobs.Where(j => j.IsDone || j.IsError).ToList())
        {
            Jobs.Remove(j);
        }
    }

    [RelayCommand]
    async Task OpenSiteAsync(string url)
    {
        try
        {
            if (SitePolicy.AllowsAutomation(url))
            {
                await Browser.OpenForSignInAsync(url);
            }
            else
            {
                Ui.OpenExternal(url);
            }
        }
        catch (Exception e)
        {
            App.Catalog.CatalogStatus = "Couldn't open the browser: " + e.Message;
        }
    }

    // ---- Resume after a restart / closing with downloads running ------------------------

    volatile bool _closing;

    /// <summary>The app is closing: a job cut off by it keeps its saved-queue entry (see <see cref="FinishPending"/>).</summary>
    internal bool IsClosing => _closing;

    internal void PrepareToClose() => _closing = true;

    /// <summary>A job is over (finished, failed or cancelled): nothing to resume, unless the app closing cut it off.</summary>
    internal void FinishPending(JobItem job)
    {
        if (!_closing)
        {
            Pending.Remove(job.Id);
        }
    }

    /// <summary>Downloads still running or waiting in the queue.</summary>
    public int RunningJobCount => Jobs.Count(j => !j.IsDone && !j.IsError);

    public ValueTask DisposeAsync() => Browser.DisposeAsync();
}
