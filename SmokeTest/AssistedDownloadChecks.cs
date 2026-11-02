using VrTableManager.Services;
using VrTableManager.ViewModels;

/// <summary>
/// Downloads from table sites (AssistedDownloader): the page opens in your browser and the file you save
/// is picked up from the Downloads folder. A fake "browser" writes into a temporary Downloads folder.
/// </summary>
public static class AssistedDownloadChecks
{
    public static async Task<List<(bool ok, string name)>> RunAsync(string root)
    {
        var results = new List<(bool, string)>();
        var downloads = Path.Combine(root, "assisted", "Downloads");
        Directory.CreateDirectory(downloads);
        File.WriteAllText(Path.Combine(downloads, "old table.zip"), "already here"); // must not be taken
        var opened = new List<string>();

        // Chrome-style: a .crdownload grows, then is renamed to the real name.
        void SaveLikeChrome(string url)
        {
            opened.Add(url);
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                var part = Path.Combine(downloads, "Unconfirmed 1234.crdownload");
                File.WriteAllText(part, "PK partial");
                await Task.Delay(1500);
                File.AppendAllText(part, " rest");
                File.Move(part, Path.Combine(downloads, "Test Table VR.zip"));
            });
        }

        var dl = new AssistedDownloader(SaveLikeChrome, downloads) { PollInterval = TimeSpan.FromMilliseconds(200) };
        var job = new JobItem("test");
        using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            limit.Token.Register(job.Cts.Cancel);
            string? got = null;
            try { got = await dl.DownloadAsync("https://vpuniverse.com/files/file/42-test/", Path.Combine(root, "assisted", "job"), job); }
            catch (OperationCanceledException) { }
            results.Add((opened.SequenceEqual(["https://vpuniverse.com/files/file/42-test/"]), "assisted: opens the file page in your browser (once)"));
            results.Add((got != null && Path.GetFileName(got) == "Test Table VR.zip" && File.ReadAllText(got) == "PK partial rest",
                "assisted: takes the finished file, not the partial one or an older download"));
            results.Add((File.Exists(Path.Combine(downloads, "Test Table VR.zip")), "assisted: your copy stays in Downloads"));
        }

        // Saved elsewhere: you point at the file.
        var elsewhere = Path.Combine(root, "assisted", "Desktop", "afm_113b.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        File.WriteAllText(elsewhere, "rom");
        var job2 = new JobItem("test 2");
        var dl2 = new AssistedDownloader(_ => { }, downloads) { PollInterval = TimeSpan.FromMilliseconds(200) };
        var wait = dl2.DownloadAsync("https://www.vpforums.org/index.php?app=downloads&showfile=99", Path.Combine(root, "assisted", "job2"), job2);
        await Task.Delay(500);
        job2.HandOff(elsewhere);
        var picked = await wait.WaitAsync(TimeSpan.FromSeconds(10));
        results.Add((Path.GetFileName(picked) == "afm_113b.zip", "assisted: \"I saved it\" takes a file from anywhere"));

        // A ROM page: every file you download until DONE.
        var job3 = new JobItem("test 3");
        var dl3 = new AssistedDownloader(url =>
        {
            _ = Task.Run(async () =>
            {
                foreach (var n in new[] { "afm_11.zip", "afm_113.zip" })
                {
                    await Task.Delay(300);
                    File.WriteAllText(Path.Combine(downloads, n), n);
                }
                await Task.Delay(1500);
                job3.DoneWaitingCommand.Execute(null);
            });
        }, downloads) { PollInterval = TimeSpan.FromMilliseconds(200) };
        var all = await dl3.DownloadAllAsync("https://vpuniverse.com/files/file/1-afm-roms/", Path.Combine(root, "assisted", "job3"), job3)
            .WaitAsync(TimeSpan.FromSeconds(15));
        results.Add((all.Select(Path.GetFileName).Order().SequenceEqual(["afm_11.zip", "afm_113.zip"]), "assisted: a ROM page takes each file until DONE"));

        results.Add((!SitePolicy.AllowsAutomation("https://vpuniverse.com/files/file/42-test/") && !SitePolicy.AllowsAutomation("https://www.vpforums.org/index.php"),
            "table sites aren't automated without their permission"));
        return results;
    }
}
