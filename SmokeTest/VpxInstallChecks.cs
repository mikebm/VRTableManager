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
