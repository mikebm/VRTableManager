using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;

/// <summary>
/// A tiny local stand-in for an Invision-style table site (VPUniverse/VPForums), used to test the
/// browser downloader end to end. It reproduces the two reported problems:
///  - the login page is slow to fill in (a "user" types for 5 s), so navigating away mid-login breaks it;
///  - the file starts downloading only after 4 s, so impatient re-clicking shows up as extra requests.
/// </summary>
sealed class MockTableSite : IDisposable
{
    readonly HttpListener _http = new();
    readonly CancellationTokenSource _stop = new();
    public string Root { get; }
    public string FileUrl => Root + "files/file/42-test-table-author-edition/";
    /// <summary>A second table with the same layout at a different address.</summary>
    public string FileUrl2 => Root + "files/file/43-test-table-author-edition/";
    public string RomPageUrl => Root + "files/file/721-attack-from-mars-roms/";
    public ConcurrentQueue<string> Log { get; } = new();

    public MockTableSite()
    {
        var port = 20000 + Random.Shared.Next(20000);
        Root = $"http://localhost:{port}/";
        _http.Prefixes.Add(Root);
        _http.Start();
        _ = Task.Run(LoopAsync);
    }

    public int Count(string entry) => Log.Count(l => l == entry);

    async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var path = req.Url!.AbsolutePath;
        var q = req.QueryString;
        var signedIn = req.Cookies["sess"]?.Value == "ok";
        try
        {
            if (path == "/favicon.ico") { res.StatusCode = 404; return; }

            if (path == "/login/" && req.HttpMethod == "POST")
            {
                Log.Enqueue("login-post");
                // Persistent, like a real site's "remember me": survives the browser closing.
                res.AddHeader("Set-Cookie", "sess=ok; Path=/; Max-Age=86400");
                res.Redirect(Root); // like Invision: back to the home page, not the file
                return;
            }
            if (path == "/login/")
            {
                Log.Enqueue("login-page");
                // The "user" takes 5 s to type, then submits. Navigating away before then loses it.
                await Html(res, """
                    <h1>Sign in</h1>
                    <form id=f method=post action="/login/">
                      <input name=user><input name=pass type=password><button>Sign In</button>
                    </form>
                    <script>setTimeout(() => { f.user.value='me'; f.pass.value='x'; f.submit(); }, 5000);</script>
                    """);
                return;
            }
            if (path == "/") { Log.Enqueue("home"); await Html(res, Header(signedIn) + "<p>Welcome back</p>"); return; }
            if (path == "/files/") { Log.Enqueue("nav-downloads"); await Html(res, "<p>All downloads</p>"); return; }

            // A PinMAME ROM page offering every ROM version of a machine (like VPUniverse's).
            if (path == "/files/file/721-attack-from-mars-roms/")
            {
                string[] roms = ["afm_11", "afm_113", "afm_113b"];
                if (!signedIn) { Log.Enqueue("rom-page-signed-out"); await Html(res, Header(false) + "<p>Sign in to download</p>"); return; }
                if (q["do"] == "download" && int.TryParse(q["r"], out var r) && r >= 1 && r <= roms.Length)
                {
                    Log.Enqueue("rom-" + roms[r - 1]);
                    using var ms = new MemoryStream();
                    using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) z.CreateEntry(roms[r - 1] + ".rom");
                    res.ContentType = "application/zip";
                    res.AddHeader("Content-Disposition", $"attachment; filename=\"{roms[r - 1]}.zip\"");
                    await res.OutputStream.WriteAsync(ms.ToArray());
                    return;
                }
                if (q["do"] == "download")
                {
                    Log.Enqueue("rom-picker");
                    var rows = string.Join("", roms.Select((n, i) =>
                        $"<li><span>{n}.zip</span> <a href=\"{Root}files/file/721-attack-from-mars-roms/?do=download&csrfKey=abc&r={i + 1}\">Download</a></li>"));
                    await Html(res, Header(true) + $"<ul>{rows}</ul>");
                    return;
                }
                Log.Enqueue("rom-page");
                await Html(res, Header(true) + $"<a class=\"ipsButton\" href=\"{Root}files/file/721-attack-from-mars-roms/?do=download\">Download this file</a>");
                return;
            }

            if (IsTablePath(path) && q["do"] == "download" && q["r"] != null)
            {
                Log.Enqueue("file-" + q["r"]);
                await Task.Delay(4000); // slow to start, like the real site
                var name = q["r"] == "2" ? "Test Table VR.zip" : "Test Table desktop.zip";
                using var ms = new MemoryStream();
                using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) z.CreateEntry("Test Table.vpx");
                res.ContentType = "application/zip";
                res.AddHeader("Content-Disposition", $"attachment; filename=\"{name}\"");
                await res.OutputStream.WriteAsync(ms.ToArray());
                return;
            }
            if (IsTablePath(path) && q["do"] == "download")
            {
                Log.Enqueue("picker");
                var link = Root + path.TrimStart('/') + "?do=download&csrfKey=abc&r=";
                await Html(res, Header(signedIn) + $"""
                    <h2>Choose a file</h2>
                    <ul>
                      <li><span>Test Table desktop.zip</span> <a href="{link}1">Download</a></li>
                      <li><span>Test Table VR.zip</span> <a href="{link}2">Download</a></li>
                    </ul>
                    """);
                return;
            }
            if (IsTablePath(path))
            {
                Log.Enqueue("file-page");
                // Signed out: the "user" clicks Sign In after 2 s.
                var autoSignIn = signedIn ? "" : "<script>setTimeout(() => location.href='/login/', 2000);</script>";
                await Html(res, Header(signedIn) + $"""
                    <div style="height:2500px">Test Table by Someone</div>
                    <a class="ipsButton" href="{Root}{path.TrimStart('/')}?do=download">Download this file</a>
                    {autoSignIn}
                    """);
                return;
            }
            res.StatusCode = 404;
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    static bool IsTablePath(string path) => path is "/files/file/42-test-table-author-edition/" or "/files/file/43-test-table-author-edition/";

    static string Header(bool signedIn) => signedIn
        ? "<nav><a href=\"/files/\">Downloads</a> <a id=elUserLink href=\"/profile/\">me</a></nav>"
        : "<nav><a href=\"/files/\">Downloads</a> <a id=elUserSignIn href=\"/login/\">Sign In</a></nav>";

    static async Task Html(HttpListenerResponse res, string body)
    {
        var bytes = Encoding.UTF8.GetBytes("<!doctype html><html><body>" + body + "</body></html>");
        res.ContentType = "text/html; charset=utf-8";
        await res.OutputStream.WriteAsync(bytes);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _http.Stop(); } catch { }
    }
}
