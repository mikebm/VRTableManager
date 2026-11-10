using System.IO.Compression;
using System.Text.Json;
using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>Getting the VR lobby media (LobbyMedia): what a table lacks, which download goes next, and one pack filling every gap.</summary>
static class LobbyMediaChecks
{
    /// <summary>The first PuP pack video in this PC's tables folder, or null (none installed).</summary>
    static string? RealVideo()
    {
        try
        {
            var tables = AppSettings.Load().TablesDir;
            return Directory.Exists(tables)
                ? Directory
                    .EnumerateDirectories(tables, "pupvideos", SearchOption.AllDirectories)
                    .SelectMany(d =>
                        Directory.EnumerateFiles(d, "*.mp4", SearchOption.AllDirectories)
                    )
                    .FirstOrDefault(f => new FileInfo(f).Length > 100_000)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static InstalledTable NewTable(string root, string dir)
    {
        var folder = Path.Combine(root, "lobby-media", dir);
        Directory.CreateDirectory(folder);
        var vpx = Path.Combine(folder, "Fish Tales.vpx");
        File.WriteAllText(vpx, "x");
        var m = new TableManifest
        {
            GameName = "Fish Tales",
            VpxFile = "Fish Tales.vpx",
            Manufacturer = "Williams",
            Year = 1992,
        };
        File.WriteAllText(Path.Combine(folder, "vrtm.json"), JsonSerializer.Serialize(m));
        return new InstalledTable(m, vpx, folder);
    }

    const string PlayField = "POPMedia/Visual Pinball X/PlayField/Fish Tales (Williams 1992).mp4",
        Loading = "POPMedia/Visual Pinball X/Loading/Fish Tales (Williams 1992).mp4",
        Wheel = "POPMedia/Visual Pinball X/Wheel/Fish Tales (Williams 1992).png";

    public static IEnumerable<(bool, string)> Run(string root)
    {
        // ---- Which download next ----
        (MediaPackAudio.Source, LobbyMedia.Kind) S(string label, LobbyMedia.Kind kind) =>
            (new("https://example.com/" + label, kind == LobbyMedia.Kind.Sound, label), kind);
        var pack1 = S("pack 1", LobbyMedia.Kind.Pack);
        var pack2 = S("pack 2", LobbyMedia.Kind.Pack);
        var wheel = S("wheel", LobbyMedia.Kind.WheelFiles);
        var sound = S("sound", LobbyMedia.Kind.Sound);
        List<(MediaPackAudio.Source, LobbyMedia.Kind)> all = [pack1, pack2, wheel, sound];

        var everything = new LobbyMedia.Needs
        {
            Video = true,
            Loading = true,
            Audio = true,
            Wheel = true,
        };
        yield return (
            LobbyMedia.Next(all, everything) == pack1,
            "when a video is missing, a media pack is downloaded first (it may make the small downloads unnecessary)"
        );
        yield return (
            LobbyMedia.Next(all, new() { Audio = true, Wheel = true }) == sound
                && LobbyMedia.Next([pack1, pack2, wheel], new() { Audio = true, Wheel = true })
                    == wheel,
            "when only the audio and wheel are missing, the small downloads go first"
        );
        yield return (
            LobbyMedia.Next([pack2, wheel, sound], new() { Wheel = true }) == wheel,
            "after a pack filled everything but the wheel, the small wheel download comes before a second pack"
        );
        yield return (
            LobbyMedia.Next([pack2, wheel], new() { Audio = true }) == pack2
                && LobbyMedia.Next([wheel], new() { Audio = true }) == null
                && LobbyMedia.Next([sound], new() { Wheel = true }) == null
                && LobbyMedia.Next(all, new()) == null,
            "a download that can't fill anything still missing is never made"
        );
        yield return (
            LobbyMedia.JoinAnd(["a", "b", "c"]) == "a, b and c"
                && LobbyMedia.JoinOr(["a"]) == "a"
                && LobbyMedia.JoinOr(["a", "b"]) == "a or b",
            "the job's summary lists pieces in plain English"
        );
        var mask = everything.And(new() { Audio = true, Wheel = true });
        yield return (
            mask is { Video: false, Loading: false, Audio: true, Wheel: true }
                && new LobbyMedia.Needs { Video = true }.Or(new() { Wheel = true })
                    is { Video: true, Wheel: true, Audio: false, Loading: false },
            "a bonus piece is only the ones the table lacks (And), added to what's asked for (Or)"
        );

        // ---- What to say ----
        List<LobbyMedia.Problem> unplayable =
        [
            new(LobbyMedia.WheelName, "the media pack's wheel art failed (x)"),
            new(
                LobbyMedia.VideoName,
                "Windows couldn't play the media pack's playfield video (.f4v)"
            ),
        ];
        yield return (
            LobbyMedia.WhyNoVideo(unplayable, hadPack: true)
                == "No playfield video: Windows couldn't play the media pack's playfield video (.f4v)."
                && LobbyMedia.WhyNoVideo([new(null, "the media pack failed (timeout)")], true)
                    == "No playfield video: the media pack failed (timeout)."
                && LobbyMedia.WhyNoVideo([], hadPack: true).Contains("has no playfield video")
                && LobbyMedia.WhyNoVideo([], hadPack: false).Contains("no media pack"),
            "before offering to record, it says why there's no video: unplayable, a failed download, none in the pack, or no pack"
        );
        var summary = LobbyMedia.BatchSummary(3, 1, 2, 1, 0, 1, stopped: false);
        yield return (
            summary.StartsWith("Added VR lobby media to 3 tables (1 recorded in VPX).")
                && summary.Contains(
                    "2 tables still miss something (1 need a ROM before VPX can record them)"
                )
                && summary.Contains("Skipped 1 table.")
                && !summary.Contains("catalog")
                && LobbyMedia.BatchSummary(1, 0, 0, 0, 0, 0, true)
                    == "Added VR lobby media to 1 table. Stopped before the end.",
            "GET LOBBY MEDIA's summary counts what was added, recorded, still missing (and why) and skipped"
        );

        // ---- What a table lacks ----
        var table = NewTable(root, "Fish Tales (Williams 1992)");
        var needs = LobbyMedia.Needs.Of(table);
        yield return (
            needs is { Video: true, Loading: true, Audio: true, Wheel: true },
            "a new table needs all four"
        );
        var loose = new InstalledTable(table.Manifest, table.TablePath, table.Folder)
        {
            SharedFolder = true,
        };
        yield return (
            !LobbyMedia.Needs.Of(loose).Any,
            "a loose table (shared folder) is given no lobby media: it can't keep any"
        );
        var mine = Path.Combine(root, "lobby-media-wheel.png");
        File.WriteAllText(mine, "png");
        UserArtwork.Set(table, UserArtwork.Kind.Wheel, mine);
        yield return (
            !LobbyMedia.Needs.Of(table).Wheel,
            "your own wheel picture counts: no wheel is downloaded over it"
        );
        UserArtwork.Remove(table, UserArtwork.Kind.Wheel);

        // ---- A pack whose playfield video Windows can't play ----
        var broken = NewTable(root, "Broken (Williams 1992)");
        var brokenZip = Path.Combine(root, "lobby-media-broken.zip");
        using (var z = ZipFile.Open(brokenZip, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(z.CreateEntry(PlayField).Open());
            w.Write(new string('x', 5000));
        }
        var brokenTake = new LobbyMedia.Needs { Video = true };
        var brokenGot = new List<string>();
        var brokenProblems = new List<LobbyMedia.Problem>();
        string? crash = null;
        try
        {
            LobbyMedia.TakeFrom(
                brokenZip,
                LobbyMedia.Kind.Pack,
                "media pack",
                broken,
                brokenTake,
                brokenGot,
                brokenProblems,
                () => null
            );
        }
        catch (Exception e)
        {
            crash = e.Message;
        }
        yield return (
            crash == null
                && brokenTake.Video
                && brokenGot.Count == 0
                && brokenProblems.Any(p => p.Piece == LobbyMedia.VideoName),
            "a playfield video Windows can't play is a problem with the video, not \"there's none\" ("
                + (crash ?? string.Join("; ", brokenProblems.Select(p => p.Text)))
                + ")"
        );

        // ---- One fake media pack holding a real video: one TakeFrom fills every piece ----
        var real = RealVideo();
        if (real == null || !OperatingSystem.IsWindows())
        {
            yield return (
                true,
                "SKIPPED: one media pack filling every piece (no PuP .mp4 on this PC to build the pack from)"
            );
            yield break;
        }

        var zip = Path.Combine(root, "lobby-media-pack.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            z.CreateEntryFromFile(real, PlayField, CompressionLevel.NoCompression);
            z.CreateEntryFromFile(real, Loading, CompressionLevel.NoCompression);
            using var w = new StreamWriter(z.CreateEntry(Wheel).Open());
            w.Write("wheel");
        }

        var got = new List<string>();
        var problems = new List<LobbyMedia.Problem>();
        LobbyMedia.TakeFrom(
            zip,
            LobbyMedia.Kind.Pack,
            "media pack",
            table,
            needs,
            got,
            problems,
            () => null
        );
        yield return (
            got.Contains(LobbyMedia.VideoName)
                && got.Contains(LobbyMedia.LoadingName)
                && got.Contains(LobbyMedia.WheelName)
                && !needs.Video
                && !needs.Loading
                && !needs.Wheel
                && LobbyVideo.Existing(table.Folder) != null
                && LoadingVideo.Own(table) != null
                && WheelArt.Saved(table.Folder) != null,
            $"one media pack fills the playfield video, loading video and wheel at once (got {LobbyMedia.JoinAnd(got)}"
                + (problems.Count > 0 ? "; " + string.Join("; ", problems.Select(p => p.Text)) : "")
                + ")"
        );
        yield return (
            got.Contains(LobbyMedia.AudioName) == !needs.Audio
                && got.Contains(LobbyMedia.AudioName)
                    == (MediaPackAudio.Existing(table.Folder) != null),
            $"the attract audio comes from the same pack when its videos have sound ({(needs.Audio ? "this one had none" : "it did")})"
        );

        // Running it again: the table lacks nothing the pack has, so nothing is rewritten.
        string[] files =
        [
            Path.Combine(LobbyVideo.Folder(table.Folder), "video.json"),
            Path.Combine(LoadingVideo.TableFolder(table.Folder), "video.json"),
            WheelArt.Saved(table.Folder)!,
        ];
        var times = files.Select(File.GetLastWriteTimeUtc).ToList();
        var again = LobbyMedia.Needs.Of(table);
        var second = new List<string>();
        LobbyMedia.TakeFrom(
            zip,
            LobbyMedia.Kind.Pack,
            "media pack",
            table,
            again,
            second,
            [],
            () => null
        );
        yield return (
            !again.Video
                && !again.Loading
                && !again.Wheel
                && second.All(x => x == LobbyMedia.AudioName)
                && files.Select(File.GetLastWriteTimeUtc).SequenceEqual(times),
            "running it again rewrites nothing the table already has (the frames' and wheel's files are untouched)"
        );
    }
}
