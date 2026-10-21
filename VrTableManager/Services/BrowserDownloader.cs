using Microsoft.Playwright;

namespace VrTableManager.Services;

/// <summary>
/// Drives a real browser window (Edge/Chrome via Playwright) to fetch files from
/// sites that need a login, such as VPUniverse, VPForums and Pinball Nirvana.
/// The user signs in themselves in that window; the app never sees credentials.
/// Once signed in, it clicks through the download buttons and captures the file.
/// The browser profile is persistent, so sign-ins are remembered between runs.
/// </summary>
public sealed class BrowserDownloader : IAsyncDisposable
{
    IPlaywright? _pw;
    IBrowserContext? _ctx;
    // Pages share one browser (one profile = one window), each download in its own tab. The app's
    // download queue decides how many run at once; this is only an upper bound.
    readonly SemaphoreSlim _gate = new(4, 4);
    readonly string _profileDir;
    readonly bool _headless;

    /// <param name="profileDir">Browser profile folder (sign-ins live here). Defaults to the app's data folder.</param>
    /// <param name="headless">Only for tests: run without a window.</param>
    public BrowserDownloader(string? profileDir = null, bool headless = false)
    {
        _profileDir = profileDir ?? Path.Combine(AppSettings.DataDir, "browser-profile");
        _headless = headless;
    }

    /// <summary>How long to give a site after a click before trying something else.</summary>
    public TimeSpan ClickPatience { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>True if the URL can be fetched without a browser (GitHub release assets, direct file links).</summary>
    public static bool IsDirectFileUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var path = u.AbsolutePath.ToLowerInvariant();
        if (u.Host.EndsWith("github.com") && path.Contains("/releases/download/")) return true;
        if (u.Host == "raw.githubusercontent.com" || u.Host == "objects.githubusercontent.com") return true;
        return path.EndsWith(".zip") || path.EndsWith(".7z") || path.EndsWith(".rar") || path.EndsWith(".vpx") || path.EndsWith(".fpt");
    }

    async Task<IBrowserContext> EnsureContextAsync(Action<string> status)
    {
        if (_ctx != null) return _ctx;
        _pw ??= await Playwright.CreateAsync();

        var opts = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = _headless,
            AcceptDownloads = true,
            ViewportSize = ViewportSize.NoViewport,
            // Don't announce "controlled by automated software" (navigator.webdriver, infobar):
            // bot checks on the table sites can loop forever on login when they see it.
            IgnoreDefaultArgs = ["--enable-automation"],
            Args = ["--disable-blink-features=AutomationControlled"],
        };

        // Prefer a browser that's already on the machine (Edge ships with Windows),
        // and only fall back to downloading Playwright's Chromium if neither exists.
        PlaywrightException? firstError = null;
        foreach (var channel in new[] { "msedge", "chrome" })
        {
            try
            {
                opts.Channel = channel;
                _ctx = await _pw.Chromium.LaunchPersistentContextAsync(_profileDir, opts);
                break;
            }
            catch (PlaywrightException e) { firstError ??= e; }
        }

        // A browser profile can only be open in one browser at a time. If it's in use (the download
        // window from another copy of this app is still open), say so rather than fetch a new browser.
        if (_ctx == null && ProfileInUse(_profileDir))
            throw new InvalidOperationException(
                "The download browser is already open, probably from another copy of VR Table Manager. Close that window (or the other app) and try again.",
                firstError);

        if (_ctx == null)
        {
            status("Installing a browser for downloads (one-time)…");
            await Task.Run(() => Microsoft.Playwright.Program.Main(["install", "chromium"]));
            opts.Channel = null;
            _ctx = await _pw.Chromium.LaunchPersistentContextAsync(_profileDir, opts);
        }

        _ctx.Close += (_, _) => _ctx = null;
        return _ctx;
    }

    /// <summary>Chromium keeps a "lockfile" open in a profile that's in use; it can't be opened while that's so.</summary>
    static bool ProfileInUse(string profileDir)
    {
        var lockFile = Path.Combine(profileDir, "lockfile");
        if (!File.Exists(lockFile)) return false;
        try
        {
            using var _ = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>Open the browser at a site so the user can sign in ahead of time.</summary>
    public async Task OpenForSignInAsync(string url)
    {
        var ctx = await EnsureContextAsync(_ => { });
        var page = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();
        await page.BringToFrontAsync();
        await page.GotoAsync(url);
    }

    /// <param name="preferHint">Words to prefer when a page offers several files (e.g. "VR").</param>
    public async Task<string> DownloadAsync(string pageUrl, string destDir, string[] preferHint,
        Action<string> status, CancellationToken ct) =>
        (await DownloadCoreAsync(pageUrl, destDir, preferHint, all: false, status, ct))[0];

    /// <summary>
    /// Like <see cref="DownloadAsync"/>, but when the page offers several files (e.g. a PinMAME ROM
    /// page with every ROM version of a machine) it downloads all of them, one after another.
    /// </summary>
    public Task<List<string>> DownloadAllAsync(string pageUrl, string destDir, Action<string> status, CancellationToken ct) =>
        DownloadCoreAsync(pageUrl, destDir, [], all: true, status, ct);

    /// <summary>Most files to take from one multi-file page (a safety limit).</summary>
    const int MaxFilesPerPage = 30;

    async Task<List<string>> DownloadCoreAsync(string pageUrl, string destDir, string[] preferHint, bool all,
        Action<string> status, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var ctx = await EnsureContextAsync(status);
            var page = await ctx.NewPageAsync();
            await page.BringToFrontAsync();

            var got = new TaskCompletionSource<IDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
            var picker = all ? new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously) : null;
            void OnDownload(object? _, IDownload d) => got.TrySetResult(d);
            // Only popups opened by *this* tab: another download may be running in a different tab.
            void OnPage(object? _, IPage p) => p.Download += OnDownload;
            page.Download += OnDownload;
            page.Popup += OnPage;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(20));
            Directory.CreateDirectory(destDir);
            try
            {
                status($"Opening {new Uri(pageUrl).Host}…");
                await GotoAsync(page, pageUrl);
                var automation = AutomateAsync(page, pageUrl, preferHint, status, got.Task, picker, cts.Token);

                var never = Task.Delay(Timeout.Infinite, cts.Token);
                var finished = await Task.WhenAny(got.Task, picker?.Task ?? never, never);
                ct.ThrowIfCancellationRequested();

                if (picker != null && finished == picker.Task)
                {
                    // Several files on offer: fetch each one in turn on this page.
                    cts.Cancel();
                    try { await automation; } catch { /* cancelled */ }
                    page.Download -= OnDownload;
                    page.Popup -= OnPage;
                    var links = picker.Task.Result.Take(MaxFilesPerPage).ToList();
                    var saved = new List<string>();
                    for (var i = 0; i < links.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        status($"Downloading file {i + 1} of {links.Count}…");
                        var d = await page.RunAndWaitForDownloadAsync(() => GotoAsync(page, links[i]), new() { Timeout = 120_000 });
                        saved.Add(await SaveAsync(d, destDir));
                    }
                    return saved;
                }

                if (finished != got.Task || !got.Task.IsCompletedSuccessfully)
                    throw new TimeoutException("No download started. You can click the download button yourself in the browser window and try again.");

                var download = got.Task.Result;
                status($"Downloading {download.SuggestedFilename}…");
                var dest = await SaveAsync(download, destDir);
                cts.Cancel(); // stop the automation loop
                try { await automation; } catch { /* cancelled */ }
                return [dest];
            }
            finally
            {
                page.Popup -= OnPage;
                page.Download -= OnDownload;
                try { await page.CloseAsync(); } catch { }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    static async Task<string> SaveAsync(IDownload download, string destDir)
    {
        var dest = Path.Combine(destDir, MakeSafe(download.SuggestedFilename));
        await download.SaveAsAsync(dest);
        var failure = await download.FailureAsync();
        if (failure != null) throw new IOException($"Download of {download.SuggestedFilename} failed: {failure}");
        return dest;
    }

    /// <summary>The individual file links on an Invision multi-file picker (…?do=download&amp;r=N), same site only.</summary>
    static async Task<List<string>> PickerLinksAsync(IPage page)
    {
        var host = new Uri(page.Url).Host;
        var links = page.Locator("a[href*='do=download'][href*='r=']");
        var n = await links.CountAsync();
        var result = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var href = await links.Nth(i).GetAttributeAsync("href");
            if (string.IsNullOrEmpty(href) || !Uri.TryCreate(new Uri(page.Url), href, out var u) || u.Host != host) continue;
            if (!result.Contains(u.ToString())) result.Add(u.ToString());
        }
        return result;
    }

    static async Task GotoAsync(IPage page, string url)
    {
        try { await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded }); }
        catch (PlaywrightException e) when (e.Message.Contains("Download is starting")) { /* direct file link */ }
    }

    // ---- Site automation -------------------------------------------------

    // Selectors for "you are signed out" across Invision (VPUniverse, VPForums) and XenForo (Pinball Nirvana).
    // Sign-in links. Sign-OUT links are excluded: IP.Board 3 (VPForums) signs out through
    // "…section=login&do=logout", which would otherwise look like a sign-in link forever.
    static readonly string[] SignInSelectors =
    [
        "#elUserSignIn",                                              // Invision 4 (VPUniverse)
        "#sign_in", "#user_navigation.not_logged_in a",               // IP.Board 3 (VPForums)
        "a.p-navgroup-link--logIn",                                   // XenForo (Pinball Nirvana)
        "a[href*='/login/']:not([href*='logout'])",
        "a[href*='section=login']:not([href*='logout'])",
        "a[href*='/login']:not([href*='logout'])",
    ];

    // Things only a signed-in visitor sees. Checked first: if any is on the page, you're signed in.
    static readonly string[] SignedInSelectors =
    [
        "#elUserLink",                                                // Invision 4 (VPUniverse)
        "#user_navigation.logged_in", "#user_link",                   // IP.Board 3 (VPForums)
        "a.p-navgroup-link--user",                                    // XenForo (Pinball Nirvana)
        "a[href*='do=logout']", "a[href*='/logout']",                 // any sign-out link
    ];

    // Ordered from most to least specific; each is tried until one leads to a download.
    static readonly string[] DownloadSelectors =
    [
        "a[href*='confirm=1']",                       // Invision "agree to terms" step
        "a[href*='do=download'][href*='r=']",         // Invision multi-file picker rows
        "a[href*='do=download']",                     // Invision "Download this file"
        "a[href*='do=confirm_download']",             // IP.Board 3 (VPForums)
        "a[href*='section=download']",
        "a.button--cta[href*='/download']",           // XenForo resources (Pinball Nirvana)
        "a[href$='/download']",
        // Text fallbacks are exact-ish so they never hit a "Downloads" nav menu.
        "a:text-matches('^\\s*download( this file| now)?\\s*$', 'i')",
        "button:text-matches('^\\s*download( this file| now)?\\s*$', 'i')",
    ];

    /// <summary>Give up auto-clicking after this many tries and let the user click.</summary>
    const int MaxClicks = 4;

    /// <param name="picker">When set ("download all" mode), a page offering several files completes this with their links instead of picking one.</param>
    async Task AutomateAsync(IPage page, string pageUrl, string[] preferHint, Action<string> status,
        Task downloadStarted, TaskCompletionSource<List<string>>? picker, CancellationToken ct)
    {
        var clicked = new HashSet<string>();
        var clicks = 0;
        var waitingForSignIn = false;
        var broughtToFront = false;
        DateTime? waitUntil = null;  // set after a click: give the site time to respond
        string? urlAtClick = null;

        while (!ct.IsCancellationRequested && !downloadStarted.IsCompleted)
        {
            try
            {
                // 1. The user is signing in (login page, or a password box on screen):
                //    never navigate or click while they type; just wait.
                if (await IsSigningInAsync(page) || await IsSignedOutAsync(page))
                {
                    if (!waitingForSignIn)
                    {
                        status($"Sign in to {new Uri(pageUrl).Host} in the browser window. Take your time; I'll carry on once you're in.");
                        waitingForSignIn = true;
                        if (!broughtToFront) { await page.BringToFrontAsync(); broughtToFront = true; }
                    }
                    await Task.Delay(1500, ct);
                    continue;
                }

                // 2. Signed in now. Sites usually redirect home, so go back to the file once.
                if (waitingForSignIn)
                {
                    waitingForSignIn = false;
                    clicked.Clear();
                    clicks = 0;
                    waitUntil = null;
                    status("Signed in. Finding the download…");
                    if (!SameFilePage(page.Url, pageUrl)) await GotoAsync(page, pageUrl);
                    continue;
                }

                // 3. After a click, wait for the download, a new page (file picker, terms), or the timeout.
                //    Clicking again sooner is what caused the repeated scrolling.
                if (waitUntil is { } until)
                {
                    if (page.Url != urlAtClick)
                    {
                        waitUntil = null;
                        try { await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 15_000 }); } catch (TimeoutException) { }
                        continue;
                    }
                    if (DateTime.UtcNow < until) { await Task.Delay(500, ct); continue; }
                    waitUntil = null;
                }

                if (picker != null && await PickerLinksAsync(page) is { Count: > 1 } files)
                {
                    status($"Found {files.Count} files on the page.");
                    picker.TrySetResult(files);
                    return;
                }

                if (clicks >= MaxClicks)
                {
                    status("I couldn't start the download. Click the download button in the browser window and I'll catch the file.");
                    await Task.Delay(3000, ct);
                    continue;
                }

                var link = await FindDownloadLinkAsync(page, clicked, preferHint);
                if (link == null)
                {
                    status("Couldn't find the download button. Click it in the browser window and I'll catch the file.");
                    await Task.Delay(2000, ct);
                    continue;
                }

                status("Starting download…");
                clicks++;
                urlAtClick = page.Url;
                waitUntil = DateTime.UtcNow + ClickPatience;
                await ActivateAsync(page, link);
            }
            catch (OperationCanceledException) { return; }
            catch (PlaywrightException)
            {
                // Page navigated mid-query or the element went stale; look again.
                await Task.Delay(1000, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Follows a link by going to its address (no scrolling the page), or clicks it when it's
    /// a script button without a real address.
    /// </summary>
    static async Task ActivateAsync(IPage page, ILocator link)
    {
        var href = await link.GetAttributeAsync("href");
        if (!string.IsNullOrWhiteSpace(href) && !href.StartsWith('#') && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(new Uri(page.Url), href, out var target))
        {
            await GotoAsync(page, target.ToString());
            return;
        }
        await link.ClickAsync(new() { Timeout = 10_000 });
    }

    /// <summary>
    /// True for login/registration addresses such as /login/, ?section=login, /sign-in, /two-factor.
    /// Matches whole words only, so table pages like "/files/file/123-author-name/" don't count.
    /// </summary>
    public static bool IsAuthUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var words = System.Text.RegularExpressions.Regex.Split((u.AbsolutePath + " " + u.Query).ToLowerInvariant(), "[^a-z0-9]+");
        var joined = new HashSet<string>(words);
        for (var i = 0; i + 1 < words.Length; i++) joined.Add(words[i] + words[i + 1]); // "sign-in" → "signin"
        return new[] { "login", "signin", "logon", "register", "lostpassword", "twofactor", "oauth" }.Any(joined.Contains);
    }

    /// <summary>True on a login/registration page, or whenever a password box is showing (e.g. a sign-in pop-up).</summary>
    public static async Task<bool> IsSigningInAsync(IPage page)
    {
        if (IsAuthUrl(page.Url)) return true;
        var passwords = page.Locator("input[type=password]");
        var n = await passwords.CountAsync();
        for (var i = 0; i < Math.Min(n, 5); i++)
            if (await passwords.Nth(i).IsVisibleAsync()) return true;
        return false;
    }

    static async Task<bool> IsSignedOutAsync(IPage page)
    {
        // A sign-out link or user menu means signed in, whatever else is on the page.
        foreach (var sel in SignedInSelectors)
            if (await page.Locator(sel).CountAsync() > 0) return false;

        foreach (var sel in SignInSelectors)
            if (await AnyVisibleAsync(page.Locator(sel))) return true;
        return false;
    }

    /// <summary>Any match visible (not just the first: a hidden duplicate in a menu shouldn't hide a visible one).</summary>
    static async Task<bool> AnyVisibleAsync(ILocator matches)
    {
        var n = Math.Min(await matches.CountAsync(), 10);
        for (var i = 0; i < n; i++)
            if (await matches.Nth(i).IsVisibleAsync()) return true;
        return false;
    }

    static async Task<ILocator?> FindDownloadLinkAsync(IPage page, HashSet<string> clicked, string[] preferHint)
    {
        foreach (var sel in DownloadSelectors)
        {
            var all = page.Locator(sel);
            var count = await all.CountAsync();
            if (count == 0) continue;

            var candidates = new List<(ILocator el, string key, string text)>();
            for (var i = 0; i < Math.Min(count, 30); i++)
            {
                var el = all.Nth(i);
                if (!await el.IsVisibleAsync()) continue;
                var href = await el.GetAttributeAsync("href") ?? "";
                var text = (await el.InnerTextAsync()).Trim();
                var key = sel + "|" + href + "|" + text;
                if (clicked.Contains(key)) continue;
                // Only click download links on the page's own site, never off-site ads.
                if (href.StartsWith("http") && Uri.TryCreate(href, UriKind.Absolute, out var u) &&
                    !u.Host.EndsWith(new Uri(page.Url).Host.Replace("www.", ""))) continue;
                candidates.Add((el, key, text));
            }
            if (candidates.Count == 0) continue;

            // Several files on offer (e.g. desktop and VR builds): prefer the one matching the hint.
            // When nothing matches and there's more than one choice, let the user pick.
            var row = await PickAsync(candidates, preferHint);
            if (row == null) return null;
            clicked.Add(row.Value.key);
            return row.Value.el;
        }
        return null;
    }

    static async Task<(ILocator el, string key, string text)?> PickAsync(
        List<(ILocator el, string key, string text)> candidates, string[] preferHint)
    {
        if (candidates.Count == 1 || !candidates[0].key.Contains("r=")) return candidates[0];

        foreach (var c in candidates)
        {
            // Invision rows put the file name in the surrounding row, not the button.
            var rowText = await c.el.Locator("xpath=ancestor::*[self::li or self::tr or self::div][1]").InnerTextAsync();
            if (preferHint.Any(h => rowText.Contains(h, StringComparison.OrdinalIgnoreCase))) return c;
        }
        return null;
    }

    /// <summary>
    /// Same page? Compares the path and the query: on VPForums every page is /index.php and only
    /// the query differs (?showfile=123 vs the board index). Session/security parameters that
    /// sites add (s, csrfKey, sid) are ignored, and so is "www.".
    /// </summary>
    public static bool SameFilePage(string current, string target)
    {
        if (!Uri.TryCreate(current, UriKind.Absolute, out var a) || !Uri.TryCreate(target, UriKind.Absolute, out var b)) return false;
        static string Host(Uri u) => u.Host.StartsWith("www.") ? u.Host[4..] : u.Host;
        static string Query(Uri u) => string.Join("&", u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Split('=')[0].ToLowerInvariant() is not ("s" or "csrfkey" or "sid"))
            .Order(StringComparer.OrdinalIgnoreCase));
        return Host(a).Equals(Host(b), StringComparison.OrdinalIgnoreCase)
            && a.AbsolutePath.TrimEnd('/').Equals(b.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            && Query(a).Equals(Query(b), StringComparison.OrdinalIgnoreCase);
    }

    static string MakeSafe(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ctx != null) await _ctx.CloseAsync();
        _pw?.Dispose();
    }
}
