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
        && AutomationAllowed.Any(h =>
            u.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)
        );
}

/// <summary>
/// Downloads from table sites the way their terms ask: the file page opens in your own browser, you
/// sign in, accept the terms and click Download there, and the app picks the finished file up from your
/// Downloads folder (or you point it at the file). Several pages can wait at once (VPUniverse makes you sit
/// through a timer on each): a new file goes to the waiting job that takes its type and whose game or ROM
/// its name carries (<see cref="Score"/>); when that can't be told, you hand it over with I SAVED IT.
/// </summary>
public sealed class AssistedDownloader
{
    readonly Action<string> _openInBrowser;
    readonly object _lock = new();

    /// <summary>Jobs waiting on you, with when each began (a file saved before that isn't theirs).</summary>
    readonly List<(JobItem job, DateTime started)> _waiting = [];

    /// <summary>Files a job has taken (path and write time), so another waiting job doesn't take them too.</summary>
    readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File types a table, ROM, add-on or media pack comes as. Anything else that lands is ignored.</summary>
    static readonly string[] Wanted =
    [
        ".zip",
        ".7z",
        ".rar",
        ".tar",
        ".gz",
        ".vpx",
        ".fpt",
        ".directb2s",
        ".mp3",
        ".ogg",
        ".wav",
        ".crz",
        ".crom",
        ".cromc",
        ".pac",
        ".vni",
        ".pal",
    ];

    /// <summary>Words too common in file names to tell games apart.</summary>
    static readonly string[] Common =
    [
        "the",
        "and",
        "for",
        "of",
        "in",
        "a",
        "pinball",
        "table",
        "edition",
        "vr",
        "vpx",
        "original",
        "mod",
    ];

    /// <summary>A multi-file page: stop waiting this long after the last file arrived (or when you press DONE).</summary>
    public TimeSpan QuietAfterLast { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>After DONE: wait at least this long for a file that just finished saving to be picked up…</summary>
    public TimeSpan DoneSettle { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>…and at most this long for one the browser is still writing.</summary>
    public TimeSpan DoneGraceMax { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The browsers' names for a file still downloading (Chrome/Edge, Firefox, Safari-style, generic).</summary>
    static readonly string[] Partial = [".crdownload", ".part", ".download", ".partial"];

    public AssistedDownloader(Action<string> openInBrowser, string? downloadsDir = null)
    {
        _openInBrowser = openInBrowser;
        DownloadsDir = downloadsDir ?? FindDownloadsDir();
    }

    public string DownloadsDir { get; }

    /// <summary>The Windows Downloads folder (wherever it was moved to), else %USERPROFILE%\Downloads.</summary>
    static string FindDownloadsDir()
    {
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads"
        );
        if (!OperatingSystem.IsWindows())
        {
            return fallback;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"
            );
            if (
                key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string p
                && Directory.Exists(Environment.ExpandEnvironmentVariables(p))
            )
            {
                return Environment.ExpandEnvironmentVariables(p);
            }
        }
        catch { }
        return fallback;
    }

    /// <summary>Opens the page and waits for the file you download there; copies it into <paramref name="destDir"/>.</summary>
    public async Task<string> DownloadAsync(
        string pageUrl,
        string destDir,
        JobItem job,
        string? what = null
    ) => (await DownloadCoreAsync(pageUrl, destDir, many: false, job, what))[0];

    /// <summary>Like <see cref="DownloadAsync"/>, for a page with several files you want (a ROM page): takes each one you download until you press DONE.</summary>
    /// <param name="complete">
    /// Says when the files so far are all that's needed (a ROM set PinMAME can load), so the wait ends by
    /// itself like a single-file download; DONE and the quiet timeout still end it otherwise.
    /// </param>
    public Task<List<string>> DownloadAllAsync(
        string pageUrl,
        string destDir,
        JobItem job,
        Func<IReadOnlyList<string>, bool>? complete = null
    ) => DownloadCoreAsync(pageUrl, destDir, many: true, job, complete: complete);

    async Task<List<string>> DownloadCoreAsync(
        string pageUrl,
        string destDir,
        bool many,
        JobItem job,
        string? what = null,
        Func<IReadOnlyList<string>, bool>? complete = null
    )
    {
        var ct = job.Cts.Token;
        var started = DateTime.UtcNow.AddSeconds(-2);
        lock (_lock)
        {
            _waiting.Add((job, started));
        }

        try
        {
            Directory.CreateDirectory(destDir);
            var host = Uri.TryCreate(pageUrl, UriKind.Absolute, out var u) ? u.Host : "the site";
            var seen = Snapshot();
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var got = new List<string>();
            var lastGot = DateTime.UtcNow;
            DateTime? doneAt = null;
            var lastBusy = DateTime.MinValue; // last time the browser was seen still writing a file
            var checkedAt = 0; // files there were when `complete` was last asked

            job.BeginWaiting(pageUrl, many);
            _openInBrowser(pageUrl);
            job.Report(
                $"Download {what ?? "it"} from {host} in your browser: sign in and accept the site's terms if it asks, then click Download. "
                    + $"It's picked up from {DownloadsDir} as soon as it's saved."
            );

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // You pointed at a file yourself, or pressed DONE.
                if (job.TakeHandoff() is { } handoff)
                {
                    if (handoff.File is { } picked)
                    {
                        // A file you already had before this download started is yours to keep: only one saved
                        // for this job is cleaned up afterwards.
                        // Off the UI thread (this loop runs on it): a PuP pack can be gigabytes.
                        var mayDelete =
                            File.GetCreationTimeUtc(picked) >= started
                            || File.GetLastWriteTimeUtc(picked) >= started;
                        got.Add(await Task.Run(() => CopyIn(picked, destDir, job, mayDelete)));
                        lastGot = DateTime.UtcNow;
                        if (!many)
                        {
                            break;
                        }
                    }
                    if (handoff.Done)
                    {
                        doneAt ??= DateTime.UtcNow;
                    }
                }
                foreach (var file in FinishedNewFiles(seen, sizes, started))
                {
                    var owner = Claim(file, job);
                    if (owner == Owner.Other)
                    {
                        continue; // another waiting job's: it takes it
                    }

                    seen[file] = File.GetLastWriteTimeUtc(file);
                    if (owner == Owner.Unsure)
                    {
                        job.Report(
                            $"Got {Path.GetFileName(file)}, but several downloads are waiting and it isn't clear which one it's for. "
                                + "If it's this one, use I SAVED IT and pick it."
                        );
                        continue;
                    }
                    if (owner == Owner.NotThis)
                    {
                        job.Report(
                            $"Got {Path.GetFileName(file)}, but that name doesn't match {string.Join(" / ", job.FileNames.Take(1))}, so it wasn't used. "
                                + "If it is the right file, use I SAVED IT and pick it."
                        );
                        continue;
                    }
                    got.Add(await Task.Run(() => CopyIn(file, destDir, job)));
                    lastGot = DateTime.UtcNow;
                    if (!many)
                    {
                        break;
                    }
                }
                if (got.Count > 0 && !many)
                {
                    break;
                }
                // Everything that's needed has arrived: no DONE to press.
                if (got.Count > checkedAt && complete != null)
                {
                    checkedAt = got.Count;
                    bool enough;
                    try
                    {
                        enough = complete(got);
                    }
                    catch
                    {
                        enough = false;
                    }
                    if (enough)
                    {
                        break;
                    }
                }
                if (doneAt is { } done)
                {
                    // DONE was pressed: a download that's still being saved (or just finished) is still yours.
                    // Give the browser a moment, and longer while it's still writing a file.
                    if (PartialDownloads(started))
                    {
                        lastBusy = DateTime.UtcNow;
                    }

                    var quiet = DateTime.UtcNow - (lastBusy > done ? lastBusy : done);
                    if (quiet > DoneSettle || DateTime.UtcNow - done > DoneGraceMax)
                    {
                        break;
                    }

                    job.Report("Waiting for the download to finish saving…");
                }
                else if (got.Count > 0)
                {
                    job.Report(
                        complete != null
                            ? $"Got {got.Count} file{(got.Count == 1 ? "" : "s")}, but the set isn't complete yet: download the rest from the page (a parent set, if it lists one), or press DONE."
                            : $"Got {got.Count} file{(got.Count == 1 ? "" : "s")}. Download any others you want from the page, then press DONE."
                    );
                    if (DateTime.UtcNow - lastGot > QuietAfterLast)
                    {
                        break;
                    }
                }
                await Task.WhenAny(Task.Delay(PollInterval, ct), job.HandoffArrived);
            }
            if (got.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No file arrived in {DownloadsDir}. Try again; if your browser saves somewhere else, use I SAVED IT while it waits."
                );
            }

            job.Report(
                got.Count == 1 ? $"Got {Path.GetFileName(got[0])}." : $"Got {got.Count} files."
            );
            return got;
        }
        finally
        {
            job.EndWaiting();
            lock (_lock)
            {
                _waiting.RemoveAll(w => w.job == job);
            }
        }
    }

    /// <summary>A file the browser started saving since the wait began and hasn't finished.</summary>
    bool PartialDownloads(DateTime started)
    {
        try
        {
            return Directory.Exists(DownloadsDir)
                && Directory
                    .EnumerateFiles(DownloadsDir)
                    .Any(f =>
                        Partial.Contains(Path.GetExtension(f).ToLowerInvariant())
                        && File.GetLastWriteTimeUtc(f) >= started
                    );
        }
        catch
        {
            return false;
        }
    }

    enum Owner
    {
        Mine,
        Other,
        Unsure,
        NotThis,
    }

    /// <summary>The catalog's game names, as <see cref="GameKey"/>s: a file named for one of them that isn't the job's game isn't the job's.</summary>
    volatile string[] _knownGameKeys = [];

    /// <summary>
    /// Every game in the catalog. A job waiting alone takes any new file of its type (table files are often
    /// named "SPP_VPW_1.2.zip", nothing like the game), but not one that spells out a different game:
    /// "Elvira and the Party Monsters … .zip" saved after Elvira's own job ended is not The Simpsons Pinball Party's.
    /// </summary>
    public IEnumerable<string> KnownGames
    {
        set =>
            _knownGameKeys = value.Select(GameKey).Where(k => k.Length >= 8).Distinct().ToArray();
    }

    /// <summary>A game name's distinctive words run together ("Elvira and the Party Monsters" → "elvirapartymonsters").</summary>
    static string GameKey(string name) =>
        string.Concat(Words(name.ToLowerInvariant()).Where(w => !Common.Contains(w)));

    /// <summary>
    /// The catalog game other than the job's own that <paramref name="fileName"/> is named for, when the name
    /// doesn't also carry the job's own game (whole, or as initials); null when it could be the job's.
    /// </summary>
    string? NamedForOtherGame(string fileName, IReadOnlyList<string> myNames)
    {
        if (myNames.Count == 0 || Score(fileName, myNames) >= 50)
        {
            return null;
        }

        var fileKey = GameKey(Path.GetFileNameWithoutExtension(fileName));
        var myKeys = myNames.Select(GameKey).Where(k => k.Length > 0).ToList();
        if (myKeys.Any(k => k.Length >= 3 && fileKey.Contains(k)))
        {
            return null;
        }
        // "The Simpsons" inside "The Simpsons Pinball Party" is the job's own game, not another one.
        return _knownGameKeys.FirstOrDefault(k =>
            fileKey.Contains(k) && !myKeys.Any(m => m.Contains(k))
        );
    }

    /// <summary>
    /// Whose a finished file is among the jobs waiting: the only one that takes its type, else the one whose
    /// names it carries best. Taking it marks it so no other job does. A job that asks for
    /// <see cref="JobItem.NamesMustMatch"/> (a ROM) doesn't take a file that carries none of its names just
    /// because nothing else is waiting: dungdrag.zip saved after its own job ended is not beav_butt.zip. Any
    /// other job alone doesn't take a file named for a different game of the catalog (<see cref="KnownGames"/>).
    /// </summary>
    Owner Claim(string file, JobItem me)
    {
        var key = file + "|" + File.GetLastWriteTimeUtc(file).Ticks;
        lock (_lock)
        {
            if (_taken.Contains(key))
            {
                return Owner.Other;
            }

            var written = File.GetLastWriteTimeUtc(file);
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var candidates = _waiting
                .Where(w =>
                    w.started <= written
                    && (w.job.FileTypes.Count == 0 || w.job.FileTypes.Contains(ext))
                )
                .Select(w => w.job)
                .ToList();
            if (!candidates.Contains(me))
            {
                return Owner.Other;
            }

            if (
                candidates.Count == 1
                && me.NamesMustMatch
                && Score(Path.GetFileName(file), me.FileNames) == 0
            )
            {
                return Owner.NotThis;
            }

            if (
                candidates.Count == 1
                && !me.NamesMustMatch
                && NamedForOtherGame(Path.GetFileName(file), me.FileNames) != null
            )
            {
                return Owner.NotThis;
            }

            var owner = candidates.Count == 1 ? me : null;
            if (owner == null)
            {
                var scores = candidates
                    .Select(j => (j, score: Score(Path.GetFileName(file), j.FileNames)))
                    .OrderByDescending(s => s.score)
                    .ToList();
                if (scores[0].score > 0 && scores[0].score > scores[1].score)
                {
                    owner = scores[0].j;
                }
            }
            if (owner == null)
            {
                return Owner.Unsure;
            }

            if (owner != me)
            {
                return Owner.Other;
            }

            _taken.Add(key);
            return Owner.Mine;
        }
    }

    /// <summary>
    /// How well a file name carries a job's names (game, ROMs, "pup" …): a whole name ("mm_109c",
    /// "BigTroubleInLittleChina") counts most, then its initials as a word ("BTILC"), then each distinctive word.
    /// Summed over the names.
    /// </summary>
    public static int Score(string fileName, IEnumerable<string> names)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var compact = Compact(stem);
        var words = Words(stem);
        var total = 0;
        foreach (var name in names)
        {
            var lower = name.ToLowerInvariant();
            var nameWords = Words(lower);
            var whole = Compact(lower);
            var initials = string.Concat(nameWords.Select(w => w[0]));
            total +=
                whole.Length >= 3 && compact.Contains(whole) ? 100
                : initials.Length >= 3 && words.Contains(initials) ? 50
                : 10
                    * nameWords.Count(w =>
                        (w.Length >= 3 || nameWords.Length == 1)
                        && !Common.Contains(w)
                        && words.Contains(w)
                    );
        }
        return total;
    }

    static string Compact(string s) => string.Concat(s.Where(char.IsLetterOrDigit));

    static string[] Words(string s) =>
        s.Split(
            s.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray(),
            StringSplitOptions.RemoveEmptyEntries
        );

    Dictionary<string, DateTime> Snapshot()
    {
        var d = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(DownloadsDir))
        {
            return d;
        }

        foreach (var f in Directory.EnumerateFiles(DownloadsDir))
        {
            d[f] = File.GetLastWriteTimeUtc(f);
        }

        return d;
    }

    /// <summary>Files saved since the wait began whose size has stopped changing and that nothing is still writing.</summary>
    IEnumerable<string> FinishedNewFiles(
        Dictionary<string, DateTime> seen,
        Dictionary<string, long> sizes,
        DateTime started
    )
    {
        if (!Directory.Exists(DownloadsDir))
        {
            yield break;
        }

        foreach (var f in Directory.EnumerateFiles(DownloadsDir).ToList())
        {
            if (!Wanted.Contains(Path.GetExtension(f).ToLowerInvariant()))
            {
                continue; // also skips .crdownload / .part / .tmp
            }

            if (File.Exists(f + ".part"))
            {
                continue; // Firefox: still downloading
            }

            var written = File.GetLastWriteTimeUtc(f);
            // New since the wait began, or saved over an older copy with the same name.
            if (seen.TryGetValue(f, out var before) && (written <= before || written < started))
            {
                continue;
            }

            long size;
            try
            {
                size = new FileInfo(f).Length;
            }
            catch
            {
                continue;
            }
            // Same size as the last look, and nothing has it open: the browser is done with it.
            if (size == 0 || !sizes.TryGetValue(f, out var last) || last != size)
            {
                sizes[f] = size;
                continue;
            }
            if (!IsClosed(f))
            {
                continue;
            }

            yield return f;
        }
    }

    static bool IsClosed(string path)
    {
        // Fails while the browser still has it open to write; other waiting jobs may be reading it at the same time.
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies the file in. Your copy stays until the job has installed it; then, if <paramref name="mayDelete"/> (it was
    /// saved for this download), it may be deleted: Setup → delete downloads after installing.
    /// </summary>
    static string CopyIn(string file, string destDir, JobItem job, bool mayDelete = true)
    {
        var dest = Path.Combine(destDir, Path.GetFileName(file));
        File.Copy(file, dest, overwrite: true);
        if (mayDelete)
        {
            job.AddSourceFile(file);
        }
        return dest;
    }
}
