using VrTableManager.ViewModels;

namespace VrTableManager.Services;

/// <summary>Which sites the app may download from by itself.</summary>
public static class SitePolicy
{
    /// <summary>
    /// Sites that have agreed in writing to automated downloads. Everything else (VPUniverse, VPForums,
    /// Pinball Nirvana …) is downloaded by you in your own browser, so you see the site, its ads and its
    /// terms. Add a host here only with that site's permission.
    /// </summary>
    static readonly HashSet<string> AutomationAllowed = new(StringComparer.OrdinalIgnoreCase);

    public static bool AllowsAutomation(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && AutomationAllowed.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Downloads from table sites the way their terms ask: the file page opens in your own browser, you
/// sign in, accept the terms and click Download there, and the app picks the finished file up from your
/// Downloads folder (or you point it at the file). One page is open at a time, so each new file belongs
/// to the job that's waiting.
/// </summary>
public sealed class AssistedDownloader
{
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Action<string> _openInBrowser;

    /// <summary>File types a table, ROM, add-on or media pack comes as. Anything else that lands is ignored.</summary>
    static readonly string[] Wanted =
        [".zip", ".7z", ".rar", ".tar", ".gz", ".vpx", ".fpt", ".directb2s", ".mp3", ".ogg", ".wav", ".crz", ".cromc", ".pac", ".vni", ".pal"];
    /// <summary>A multi-file page: stop waiting this long after the last file arrived (or when you press DONE).</summary>
    public TimeSpan QuietAfterLast { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public AssistedDownloader(Action<string> openInBrowser, string? downloadsDir = null)
    {
        _openInBrowser = openInBrowser;
        DownloadsDir = downloadsDir ?? FindDownloadsDir();
    }

    public string DownloadsDir { get; }

    /// <summary>The Windows Downloads folder (wherever it was moved to), else %USERPROFILE%\Downloads.</summary>
    static string FindDownloadsDir()
    {
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!OperatingSystem.IsWindows()) return fallback;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string p && Directory.Exists(Environment.ExpandEnvironmentVariables(p)))
                return Environment.ExpandEnvironmentVariables(p);
        }
        catch { }
        return fallback;
    }

    /// <summary>Opens the page and waits for the file you download there; copies it into <paramref name="destDir"/>.</summary>
    public async Task<string> DownloadAsync(string pageUrl, string destDir, JobItem job) =>
        (await DownloadCoreAsync(pageUrl, destDir, many: false, job))[0];

    /// <summary>Like <see cref="DownloadAsync"/>, for a page with several files you want (a ROM page): takes each one you download until you press DONE.</summary>
    public Task<List<string>> DownloadAllAsync(string pageUrl, string destDir, JobItem job) =>
        DownloadCoreAsync(pageUrl, destDir, many: true, job);

    async Task<List<string>> DownloadCoreAsync(string pageUrl, string destDir, bool many, JobItem job)
    {
        var ct = job.Cts.Token;
        if (_gate.CurrentCount == 0) job.Report("Queued: another download is waiting for you in the browser first.");
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(destDir);
            var host = Uri.TryCreate(pageUrl, UriKind.Absolute, out var u) ? u.Host : "the site";
            var started = DateTime.UtcNow.AddSeconds(-2);
            var seen = Snapshot();
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var got = new List<string>();
            var lastGot = DateTime.UtcNow;

            job.BeginWaiting(pageUrl, many);
            _openInBrowser(pageUrl);
            job.Report($"Download it from {host} in your browser: sign in and accept the site's terms if it asks, then click Download. "
                       + $"It's picked up from {DownloadsDir} as soon as it's saved.");

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // You pointed at a file yourself, or pressed DONE.
                if (job.TakeHandoff() is { } handoff)
                {
                    if (handoff.File is { } picked) { got.Add(CopyIn(picked, destDir)); lastGot = DateTime.UtcNow; }
                    if (handoff.Done || !many) break;
                }
                foreach (var file in FinishedNewFiles(seen, sizes, started))
                {
                    seen[file] = File.GetLastWriteTimeUtc(file);
                    got.Add(CopyIn(file, destDir));
                    lastGot = DateTime.UtcNow;
                    if (!many) break;
                }
                if (got.Count > 0)
                {
                    if (!many) break;
                    job.Report($"Got {got.Count} file{(got.Count == 1 ? "" : "s")}. Download any others you want from the page, then press DONE.");
                    if (DateTime.UtcNow - lastGot > QuietAfterLast) break;
                }
                await Task.WhenAny(Task.Delay(PollInterval, ct), job.HandoffArrived);
            }
            if (got.Count == 0) throw new InvalidOperationException("No file was downloaded.");
            job.Report(got.Count == 1 ? $"Got {Path.GetFileName(got[0])}." : $"Got {got.Count} files.");
            return got;
        }
        finally
        {
            job.EndWaiting();
            _gate.Release();
        }
    }

    Dictionary<string, DateTime> Snapshot()
    {
        var d = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(DownloadsDir)) return d;
        foreach (var f in Directory.EnumerateFiles(DownloadsDir)) d[f] = File.GetLastWriteTimeUtc(f);
        return d;
    }

    /// <summary>Files saved since the wait began whose size has stopped changing and that nothing is still writing.</summary>
    IEnumerable<string> FinishedNewFiles(Dictionary<string, DateTime> seen, Dictionary<string, long> sizes, DateTime started)
    {
        if (!Directory.Exists(DownloadsDir)) yield break;
        foreach (var f in Directory.EnumerateFiles(DownloadsDir).ToList())
        {
            if (!Wanted.Contains(Path.GetExtension(f).ToLowerInvariant())) continue; // also skips .crdownload / .part / .tmp
            if (File.Exists(f + ".part")) continue;                                // Firefox: still downloading
            var written = File.GetLastWriteTimeUtc(f);
            // New since the wait began, or saved over an older copy with the same name.
            if (seen.TryGetValue(f, out var before) && (written <= before || written < started)) continue;
            long size;
            try { size = new FileInfo(f).Length; } catch { continue; }
            // Same size as the last look, and nothing has it open: the browser is done with it.
            if (size == 0 || !sizes.TryGetValue(f, out var last) || last != size) { sizes[f] = size; continue; }
            if (!IsClosed(f)) continue;
            yield return f;
        }
    }

    static bool IsClosed(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Copies the file in (your copy in Downloads stays where it is).</summary>
    static string CopyIn(string file, string destDir)
    {
        var dest = Path.Combine(destDir, Path.GetFileName(file));
        File.Copy(file, dest, overwrite: true);
        return dest;
    }
}
