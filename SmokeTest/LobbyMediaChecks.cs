using System.IO.Compression;
using System.Text.Json;
using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>Get all VR lobby media (LobbyMedia): what a table lacks, which download goes first, and one pack filling every gap.</summary>
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

    public static IEnumerable<(bool, string)> Run(string root)
    {
        MediaPackAudio.Source S(string label, bool direct = false) =>
            new("https://example.com/" + label, direct, label);
        var pack = S("pack");
        var wheel = S("wheel");
        var sound = S("sound", true);

        var all = new LobbyMedia.Needs
        {
            Video = true,
            Loading = true,
            Audio = true,
            Wheel = true,
        };
        var order = LobbyMedia.Order([pack], [wheel], [sound], all);
        yield return (
            order
                .Select(o => o.Kind)
                .SequenceEqual([
                    LobbyMedia.Kind.Pack,
                    LobbyMedia.Kind.WheelFiles,
                    LobbyMedia.Kind.Sound,
                ]),
            "when a video is missing, the media pack is downloaded first (it may make the small downloads unnecessary)"
        );
        var small = new LobbyMedia.Needs { Audio = true, Wheel = true };
        order = LobbyMedia.Order([pack], [wheel], [sound], small);
        yield return (
            order
                .Select(o => o.Kind)
                .SequenceEqual([
                    LobbyMedia.Kind.Sound,
                    LobbyMedia.Kind.WheelFiles,
                    LobbyMedia.Kind.Pack,
                ]),
            "when only the audio and wheel are missing, the small downloads go first and the big pack last"
        );
        yield return (
            LobbyMedia.Helps(LobbyMedia.Kind.Pack, small)
                && !LobbyMedia.Helps(LobbyMedia.Kind.WheelFiles, new() { Audio = true })
                && !LobbyMedia.Helps(LobbyMedia.Kind.Sound, new() { Wheel = true })
                && !LobbyMedia.Helps(LobbyMedia.Kind.Pack, new()),
            "a download is skipped when it can't fill anything still missing"
        );
        yield return (
            LobbyMedia.JoinAnd(["a", "b", "c"]) == "a, b and c"
                && LobbyMedia.JoinOr(["a"]) == "a"
                && LobbyMedia.JoinOr(["a", "b"]) == "a or b",
            "the job's summary lists pieces in plain English"
        );

        var folder = Path.Combine(root, "lobby-media", "Fish Tales (Williams 1992)");
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
        var table = new InstalledTable(m, vpx, folder);
        var needs = LobbyMedia.Needs.Of(table);
        yield return (
            needs is { Video: true, Loading: true, Audio: true, Wheel: true },
            "a new table needs all four"
        );
        yield return (
            !LobbyMedia.Needs.Of(new InstalledTable(m, vpx, folder) { SharedFolder = true }).Wheel
                && !LobbyMedia
                    .Needs.Of(new InstalledTable(m, vpx, folder) { SharedFolder = true })
                    .Video,
            "a loose table (shared folder) isn't given folder media"
        );

        var mine = Path.Combine(root, "lobby-media-wheel.png");
        File.WriteAllText(mine, "png");
        UserArtwork.Set(table, UserArtwork.Kind.Wheel, mine);
        yield return (
            !LobbyMedia.Needs.Of(table).Wheel,
            "your own wheel picture counts: no wheel is downloaded over it"
        );
        UserArtwork.Remove(table, UserArtwork.Kind.Wheel);

        // One fake media pack holding a real video (a PuP pack's .mp4 on this PC) as both its playfield and its
        // loading video, plus a wheel: one TakeFrom fills all of them.
        var real = RealVideo();
        if (real == null || !OperatingSystem.IsWindows())
        {
            yield break;
        }

        var zip = Path.Combine(root, "lobby-media-pack.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            z.CreateEntryFromFile(
                real,
                "POPMedia/Visual Pinball X/PlayField/Fish Tales (Williams 1992).mp4",
                CompressionLevel.NoCompression
            );
            z.CreateEntryFromFile(
                real,
                "POPMedia/Visual Pinball X/Loading/Fish Tales (Williams 1992).mp4",
                CompressionLevel.NoCompression
            );
            using var w = new StreamWriter(
                z.CreateEntry("POPMedia/Visual Pinball X/Wheel/Fish Tales (Williams 1992).png")
                    .Open()
            );
            w.Write("wheel");
        }

        var problems = new List<string>();
        var got = LobbyMedia.TakeFrom(
            zip,
            LobbyMedia.Kind.Pack,
            table,
            needs,
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
                && LobbyVideo.Existing(folder) != null
                && LoadingVideo.Own(table) != null
                && WheelArt.Saved(folder) != null,
            $"one media pack fills the playfield video, loading video and wheel at once (got {LobbyMedia.JoinAnd(got)}"
                + (problems.Count > 0 ? "; " + string.Join("; ", problems) : "")
                + ")"
        );
        yield return (
            got.Contains(LobbyMedia.AudioName) == !needs.Audio
                && got.Contains(LobbyMedia.AudioName) == (MediaPackAudio.Existing(folder) != null),
            $"the attract audio comes from the same pack when its videos have sound ({(needs.Audio ? "this one had none" : "it did")})"
        );

        var wheelTime = File.GetLastWriteTimeUtc(WheelArt.Saved(folder)!);
        var videoInfo = LobbyVideo.Existing(folder);
        var again = LobbyMedia.Needs.Of(table);
        var second = LobbyMedia.TakeFrom(zip, LobbyMedia.Kind.Pack, table, again, [], () => null);
        yield return (
            second.All(x => x == LobbyMedia.AudioName)
                && !second.Contains(LobbyMedia.VideoName)
                && !second.Contains(LobbyMedia.LoadingName)
                && !second.Contains(LobbyMedia.WheelName)
                && File.GetLastWriteTimeUtc(WheelArt.Saved(folder)!) == wheelTime
                && LobbyVideo.Existing(folder) == videoInfo,
            "running it again replaces nothing the table already has"
        );
    }
}
