using System.Net;
using System.Net.Sockets;
using System.Text;
using VrTableManager.Services;

/// <summary>VPX install robustness: stuck downloads fail instead of hanging; hand-unzipped VPX is recognised.</summary>
static class VpxInstallChecks
{
    public static async Task<List<(bool, string)>> RunAsync(string root)
    {
        var results = new List<(bool, string)>();
        results.Add(
            (
                VpxSetup.TagFromProductVersion("10.8.1.5436.af26b2d93") == "v10.8.1-5436-af26b2d93"
                    && VpxSetup.TagFromProductVersion("10.8.1.5436") == "v10.8.1-5436"
                    && VpxSetup.TagFromProductVersion("") == null,
                "a hand-unzipped VPX's version is read from the program"
            )
        );
        var rel = new VpxReleases(
            new VpxRelease("v10.8.1-5436-af26b2d93", "x.zip", "http://x", 1),
            null
        );
        results.Add(
            (
                !VpxSetup.DescribeUpdate(rel, "v10.8.1-5436").HasUpdate,
                "the same build without its hash counts as up to date"
            )
        );

        // The build list: releases and nightly runs, to go back to an older build.
        var suffix =
            OperatingSystem.IsWindows() ? "-windows-x64-Release.zip"
            : OperatingSystem.IsMacOS()
                ? (
                    System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                    == System.Runtime.InteropServices.Architecture.Arm64
                        ? "-macos-arm64-Release.dmg"
                        : "-macos-x64-Release.dmg"
                )
            : "-linux-x64-Release.tar.gz";
        static string Rel(string tag, string name, string date, string suffix) =>
            $$"""{"draft":false,"name":"{{name}}","tag_name":"{{tag}}","published_at":"{{date}}","assets":[{"name":"VPinballX_BGFX-{{tag.TrimStart('v')}}{{suffix}}","browser_download_url":"https://x/{{tag}}.zip","size":1}]}""";
        var releasesJson =
            $"[{Rel("v10.8.1-5436-af26b2d93", "DO NOT USE - v10.8.1-5436-af26b2d93", "2026-08-21T05:45:31Z", suffix)},{Rel("v10.8.0-2051-28dd6c3", "Visual Pinball X 10.8.0", "2025-01-29T21:46:54Z", suffix)}]";
        var withDnu = VpxSetup.ParseReleaseList(releasesJson, useDoNotUse: true);
        var withoutDnu = VpxSetup.ParseReleaseList(releasesJson, useDoNotUse: false);
        results.Add(
            (
                withDnu.Count == 2
                    && withDnu[0].Label.Contains("DO NOT USE")
                    && withDnu[0].Release?.Tag == "v10.8.1-5436-af26b2d93"
                    && withoutDnu.Count == 1
                    && withoutDnu[0].Release?.Tag == "v10.8.0-2051-28dd6c3",
                "build list: every release is listed, DO NOT USE ones only when allowed"
            )
        );
        var runs = VpxSetup.ParseNightlyRuns(
            """{"workflow_runs":[{"head_sha":"934be537e0000000000000000000000000000000","created_at":"2026-11-08T19:06:27Z","artifacts_url":"https://api/runs/2/artifacts"},{"head_sha":"2ad1916b2fffffffffffffffffffffffffffffff","created_at":"2026-11-06T21:26:14Z","artifacts_url":"https://api/runs/1/artifacts"}]}"""
        );
        results.Add(
            (
                runs.Count == 2
                    && runs[1].Label == "Nightly · 2026-11-06 21:26 UTC · 2ad1916b2"
                    && runs[1].ArtifactsUrl == "https://api/runs/1/artifacts?per_page=100"
                    && runs[1].Release == null,
                "build list: nightly runs are listed with their date and commit"
            )
        );
        results.Add(
            (
                VpxSetup.IsInstalled(runs[1], "v10.8.1-6625-2ad1916b2")
                    && !VpxSetup.IsInstalled(runs[0], "v10.8.1-6625-2ad1916b2")
                    && VpxSetup.IsInstalled(withDnu[0], "v10.8.1-5436")
                    && !VpxSetup.IsInstalled(withDnu[1], "v10.8.1-5436-af26b2d93")
                    && !VpxSetup.IsInstalled(runs[1], null),
                "build list: the installed build is recognised (a nightly by its commit)"
            )
        );
        var resolved = await VpxSetup.ResolveAsync(withDnu[0], CancellationToken.None);
        results.Add(
            (
                resolved?.Tag == "v10.8.1-5436-af26b2d93",
                "build list: a release resolves to its own download without asking GitHub"
            )
        );

        // A picked build is kept: newer builds are mentioned, not offered.
        var newer = new VpxReleases(
            new VpxRelease("v10.8.1-6625-2ad1916b2", "x.zip", "http://x", 1, Nightly: true),
            null
        );
        var kept = VpxSetup.DescribeUpdate(
            newer,
            "v10.8.1-5436-af26b2d93",
            "v10.8.1-5436-af26b2d93"
        );
        results.Add(
            (
                !kept.HasUpdate
                    && kept.Text.Contains("the build you picked")
                    && kept.Text.Contains("v10.8.1-6625-2ad1916b2"),
                "a build picked from the list isn't replaced by an update"
            )
        );
        results.Add(
            (
                VpxSetup
                    .DescribeUpdate(newer, "v10.8.1-5436-af26b2d93", "v10.8.1-3788-2151290")
                    .HasUpdate
                    && VpxSetup.DescribeUpdate(newer, "v10.8.1-5436-af26b2d93").HasUpdate,
                "updates are offered again once the picked build isn't the installed one"
            )
        );

        // A server that sends the headers and a little data, then nothing.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buf = new byte[4096];
            await stream.ReadAsync(buf);
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 1000\r\n\r\n0123456789")
            );
            await Task.Delay(TimeSpan.FromSeconds(30));
        });
        var old = Http.StallTimeout;
        Http.StallTimeout = TimeSpan.FromSeconds(2);
        var started = DateTime.UtcNow;
        string? error = null;
        try
        {
            await Http.DownloadFileAsync(
                $"http://127.0.0.1:{port}/vpx.zip",
                Path.Combine(root, "stalled.zip"),
                null,
                CancellationToken.None
            );
        }
        catch (TimeoutException e)
        {
            error = e.Message;
        }
        finally
        {
            Http.StallTimeout = old;
            listener.Stop();
        }
        results.Add(
            (
                error?.Contains("nothing arrived") == true
                    && DateTime.UtcNow - started < TimeSpan.FromSeconds(15),
                "a download that stops sending fails with a clear message instead of hanging"
            )
        );
        return results;
    }
}
