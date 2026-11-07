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

        // Several pages waiting at once (VPUniverse's countdown): each file goes to the job it's for.
        results.Add((AssistedDownloader.Score("BTILC_2026_Original_1.0.1.vpx", ["Big Trouble in Little China"]) > 0
                && AssistedDownloader.Score("MedievalMadness_VR.zip", ["Medieval Madness"]) > AssistedDownloader.Score("MedievalMadness_VR.zip", ["Attack from Mars"])
                && AssistedDownloader.Score("BTILC-PuP-v1.01.zip", ["Big Trouble in Little China", "pup"]) > AssistedDownloader.Score("BTILC-PuP-v1.01.zip", ["Big Trouble in Little China"]),
            "assisted: file names are matched by game name, initials and kind"));
        var multiDir = Path.Combine(root, "assisted", "Downloads-multi");
        Directory.CreateDirectory(multiDir);
        var multi = new AssistedDownloader(_ => { }, multiDir) { PollInterval = TimeSpan.FromMilliseconds(200) };
        var tableJob = new JobItem("BTILC") { FileNames = ["Big Trouble in Little China"], FileTypes = [".vpx", ".zip"] };
        var pupJob = new JobItem("BTILC PuP") { FileNames = ["Big Trouble in Little China", "pup"], FileTypes = [".zip"] };
        var mmJob = new JobItem("MM") { FileNames = ["Medieval Madness", "mm_109c"], FileTypes = [".vpx", ".zip"] };
        var waits = new[] { (tableJob, "t"), (pupJob, "p"), (mmJob, "m") }
            .Select(x => multi.DownloadAsync("https://vpuniverse.com/files/file/1/", Path.Combine(root, "assisted", "multi-" + x.Item2), x.Item1)).ToList();
        await Task.Delay(500);
        // Saved in another order than the pages were opened.
        foreach (var n in new[] { "Medieval_Madness_2.0.zip", "BTILC-PuP-v1.01.zip", "BTILC_2026_Original_1.0.1.vpx" })
            File.WriteAllText(Path.Combine(multiDir, n), n);
        var names = (await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(15))).Select(Path.GetFileName).ToList();
        results.Add((names.SequenceEqual(["BTILC_2026_Original_1.0.1.vpx", "BTILC-PuP-v1.01.zip", "Medieval_Madness_2.0.zip"]),
            "assisted: with several pages waiting, each file goes to its own job"));

        // A ROM page ends by itself once the set is complete (here: the clone and then its parent), no DONE.
        var autoDir = Path.Combine(root, "assisted", "Downloads-auto");
        Directory.CreateDirectory(autoDir);
        var autoJob = new JobItem("AFM ROM") { FileNames = ["afm_113b", "afm_113"], FileTypes = [".zip"], NamesMustMatch = true };
        var auto = new AssistedDownloader(url =>
        {
            _ = Task.Run(async () =>
            {
                foreach (var n in new[] { "afm_113b.zip", "afm_113.zip" })
                {
                    await Task.Delay(300);
                    File.WriteAllText(Path.Combine(autoDir, n), n);
                }
            });
        }, autoDir) { PollInterval = TimeSpan.FromMilliseconds(200) };
        var autoGot = await auto.DownloadAllAsync("https://vpuniverse.com/files/file/4-afm-roms/", Path.Combine(root, "assisted", "auto"), autoJob,
                got => got.Select(Path.GetFileName).Order().SequenceEqual(["afm_113.zip", "afm_113b.zip"]))
            .WaitAsync(TimeSpan.FromSeconds(10)); // the quiet timeout is 60 s: only the completeness check ends it this soon
        results.Add((autoGot.Count == 2, "assisted: a ROM page finishes by itself once the set is complete"));

        // DONE pressed while the file is still being saved: it's still taken.
        var earlyDir = Path.Combine(root, "assisted", "Downloads-early");
        Directory.CreateDirectory(earlyDir);
        var earlyJob = new JobItem("D&D ROM") { FileNames = ["dungdrag", "Dungeons & Dragons"], FileTypes = [".zip"], NamesMustMatch = true };
        var early = new AssistedDownloader(url =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                var part = Path.Combine(earlyDir, "Unconfirmed 99.crdownload");
                File.WriteAllText(part, "PK");
                earlyJob.DoneWaitingCommand.Execute(null);
                await Task.Delay(1500);
                File.Move(part, Path.Combine(earlyDir, "dungdrag.zip"));
            });
        }, earlyDir) { PollInterval = TimeSpan.FromMilliseconds(200), DoneSettle = TimeSpan.FromMilliseconds(800) };
        var earlyGot = await early.DownloadAllAsync("https://vpuniverse.com/files/file/2-dd-rom/", Path.Combine(root, "assisted", "early"), earlyJob)
            .WaitAsync(TimeSpan.FromSeconds(15));
        results.Add((earlyGot.Select(Path.GetFileName).SequenceEqual(["dungdrag.zip"]), "assisted: DONE pressed mid-download still takes the file"));

        // A ROM job alone doesn't take another ROM's file.
        var strictDir = Path.Combine(root, "assisted", "Downloads-strict");
        Directory.CreateDirectory(strictDir);
        var bbJob = new JobItem("B&B ROM") { FileNames = ["beav_butt", "clas1812", "Beavis and Butt-Head Pinballed"], FileTypes = [".zip"], NamesMustMatch = true };
        var strict = new AssistedDownloader(url =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                File.WriteAllText(Path.Combine(strictDir, "dungdrag.zip"), "dd");
                await Task.Delay(1200);
                File.WriteAllText(Path.Combine(strictDir, "beav_butt.zip"), "bb");
            });
        }, strictDir) { PollInterval = TimeSpan.FromMilliseconds(200) };
        var bbGot = await strict.DownloadAsync("https://vpuniverse.com/files/file/3-bb-rom/", Path.Combine(root, "assisted", "strict"), bbJob)
            .WaitAsync(TimeSpan.FromSeconds(15));
        results.Add((Path.GetFileName(bbGot) == "beav_butt.zip", "assisted: a ROM job skips a file named for another ROM"));

        results.Add((!SitePolicy.AllowsAutomation("https://vpuniverse.com/files/file/42-test/") && !SitePolicy.AllowsAutomation("https://www.vpforums.org/index.php"),
            "table sites aren't automated without their permission"));
        return results;
    }
}
