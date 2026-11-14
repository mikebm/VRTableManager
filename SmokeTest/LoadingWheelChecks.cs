using System.IO.Compression;
using System.Text.Json;
using VrTableManager.Lobby;
using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>The VR lobby's loading video (LoadingVideo) and wheel art (WheelArt): which pack files they are, where they're kept, which wins.</summary>
static class LoadingWheelChecks
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
        // ---- Loading video ----
        var pack = new (string, long)[]
        {
            ("POPMedia/Visual Pinball X/BackGlass/Fish Tales (Williams 1992).mp4", 9_000_000),
            ("POPMedia/Visual Pinball X/Loading/Fish Tales (Williams 1992).mp4", 5_000_000),
            ("POPMedia/Visual Pinball X/PlayField/Fish Tales (Williams 1992).mp4", 30_000_000),
            ("POPMedia/Visual Pinball X/GameHelp/Fish Tales loading help.mp4", 1_000_000),
            ("POPMedia/Visual Pinball X/Wheel/Fish Tales (Williams 1992).png", 200_000),
        };
        MediaPackFiles.Scorer loading = (p, s) => LoadingVideo.Score(p, s, "Fish Tales");
        yield return (
            MediaPackFiles.Pick(pack, loading) == pack[1].Item1,
            "a media pack's loading video is Popper's Loading\\ video"
        );
        yield return (
            MediaPackFiles.Pick([pack[0], pack[2], pack[3], pack[4]], loading) == null,
            "playfield, backglass and help videos are never the loading video"
        );
        yield return (
            LoadingVideo.FrameSize(1920, 1080) == (1280, 720)
                && LoadingVideo.FrameSize(1080, 1920) == (720, 1280)
                && LoadingVideo.FrameSize(640, 360) == (640, 360),
            "loading frames keep the video's shape, 1280 px at most"
        );

        var folder = Path.Combine(root, "loading-tables", "Fish Tales (Williams 1992)");
        Directory.CreateDirectory(folder);
        var vpx = Path.Combine(folder, "Fish Tales.vpx");
        File.WriteAllText(vpx, "x");
        var m = new TableManifest { GameName = "Fish Tales", VpxFile = "Fish Tales.vpx" };
        File.WriteAllText(Path.Combine(folder, "vrtm.json"), JsonSerializer.Serialize(m));
        var table = new InstalledTable(m, vpx, folder);
        var lobbyTable = LobbyLibrary.From(table);

        yield return (
            LoadingVideo.Own(table) == null
                && LoadingVideo.For(table)?.Folder
                    == LoadingVideo.In(LoadingVideo.DefaultFolder)?.Folder,
            "without its own, a table gets the loading video for every table (if there is one)"
        );
        var lv = LoadingVideo.TableFolder(folder);
        using (var b = new LobbyVideo.Builder(lv, "test", LoadingVideo.Fps))
        {
            (b.FrameWidth, b.FrameHeight) = (64, 36);
            File.WriteAllText(b.NextFramePath(), "jpg");
            File.WriteAllText(b.NextFramePath(), "jpg");
            File.WriteAllText(Path.Combine(b.Staging, "audio.mp3"), "mp3");
            b.Commit();
        }
        var own = LoadingVideo.For(table);
        yield return (
            own is { Info: { Frames: 2, Width: 64, Height: 36 } }
                && own.Folder == lv
                && own.Audio == Path.Combine(lv, "audio.mp3"),
            "a table's own loading video (frames, size and sound) wins, from medias\\loading-video"
        );
        yield return (
            LobbyArt.LoadingVideoFor(lobbyTable, new AppSettings())?.Folder == lv
                && LobbyArt.LoadingVideoFor(
                    lobbyTable,
                    new AppSettings { LobbyLoadingVideos = false }
                ) == null,
            "the lobby plays it unless loading videos are turned off"
        );
        var shared = new InstalledTable(m, vpx, folder) { SharedFolder = true };
        yield return (
            LoadingVideo.Own(shared) == null,
            "a loose table (shared folder) never takes a folder's loading video as its own"
        );
        LoadingVideo.Remove(lv);
        yield return (LoadingVideo.Own(table) == null, "removing it deletes it");

        // A real video on this PC (a PuP pack's .mp4, only read), turned into a loading video in the test folder.
        var real = RealVideo();
        if (real != null && OperatingSystem.IsWindows())
        {
            var dest = Path.Combine(root, "loading-real");
            var made = VideoFrames.LoadingFromVideo(real, dest, CancellationToken.None);
            var got = LoadingVideo.In(dest);
            yield return (
                made is { Frames: > 0 } info
                    && got?.Info == made
                    && info.Width % 2 == 0
                    && info.Height % 2 == 0
                    && Math.Max(info.Width, info.Height) <= LoadingVideo.LongSide
                    && info.Frames <= LoadingVideo.MaxFrames,
                $"a real .mp4 becomes a loading video ({made?.Frames} frames, {made?.Width}×{made?.Height}, "
                    + $"{(got?.Audio != null ? "sound " + new FileInfo(got.Audio).Length / 1024 + " KB" : "no sound")}; {Path.GetFileName(real)})"
            );
            // At 15 fps the frames must last as long as the video (and its sound), whatever its own frame rate.
            double seconds;
            using (var reader = new NAudio.Wave.MediaFoundationReader(real))
            {
                seconds = Math.Min(reader.TotalTime.TotalSeconds, LoadingVideo.MaxSeconds);
            }

            var played = (made?.Frames ?? 0) / (double)LoadingVideo.Fps;
            yield return (
                Math.Abs(played - seconds) < 0.5,
                $"the frames play as long as the video lasts ({played:0.0} s of frames for {seconds:0.0} s of video)"
            );

            // The same video turned anticlockwise: the frames take the turned shape, and it remembers its video.
            var turnedDest = Path.Combine(root, "loading-real-turned");
            var turned = VideoFrames.LoadingFromVideo(
                real,
                turnedDest,
                CancellationToken.None,
                -1,
                rememberSource: true
            );
            yield return (
                turned != null
                    && made != null
                    && turned.Width == made.Height
                    && turned.Height == made.Width
                    && LoadingVideo.OriginIn(turnedDest) is { QuarterTurns: 3 } o
                    && o.Video == Path.GetFullPath(real),
                $"a real .mp4 turned 90° anticlockwise swaps its shape ({made?.Width}×{made?.Height} → {turned?.Width}×{turned?.Height}) and remembers its video"
            );
        }

        // A launch video made sideways (for a cabinet screen) turned a quarter either way: the frames take the turned
        // shape, and the picture turns the right way. A 64×32 frame, red on the left, blue on the right: clockwise
        // puts the left side at the top, anticlockwise at the bottom.
        var wide = new byte[64 * 32 * 4];
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var p = (y * 64 + x) * 4;
                (wide[p], wide[p + 2], wide[p + 3]) =
                    x < 32 ? ((byte)255, (byte)0, (byte)255) : ((byte)0, (byte)255, (byte)255);
            }
        }
        static string TopBottom(string folder)
        {
            using var bmp = SkiaSharp.SKBitmap.Decode(LobbyVideo.FramePath(folder, 0));
            static string C(SkiaSharp.SKColor c) =>
                c.Red > 180 && c.Blue < 80 ? "red"
                : c.Blue > 180 && c.Red < 80 ? "blue"
                : "?";
            return $"{bmp.Width}×{bmp.Height} {C(bmp.GetPixel(bmp.Width / 2, bmp.Height / 4))}/{C(bmp.GetPixel(bmp.Width / 2, bmp.Height * 3 / 4))}";
        }
        foreach (
            var (turns, expect, way) in new[]
            {
                (1, "32×64 red/blue", "clockwise"),
                (-1, "32×64 blue/red", "anticlockwise"),
            }
        )
        {
            var turned = Path.Combine(root, "loading-turned-" + way);
            using (var b = new LobbyVideo.Builder(turned, "loading", LoadingVideo.Fps))
            {
                VideoFrames.WriteLoadingFrames(b, [(64, 32, wide), (64, 32, wide)], turns);
                b.Commit();
            }
            var info = LoadingVideo.In(turned)?.Info;
            yield return (
                info is { Width: 32, Height: 64, Frames: 2 } && TopBottom(turned) == expect,
                $"a sideways video turned 90° {way} comes out tall and upright ({info?.Width}×{info?.Height}, frame {TopBottom(turned)})"
            );
        }

        // Turning an already-made loading video (a media pack's, whose video is gone) turns its frames, keeps its sound.
        var made2 = Path.Combine(root, "loading-turned-clockwise");
        File.WriteAllBytes(Path.Combine(made2, LoadingVideo.AudioName + ".mp3"), [1, 2, 3]);
        LoadingVideo.WriteOrigin(made2, new(null, 1));
        var back = VideoFrames.RotateLoading(made2, -1, CancellationToken.None);
        yield return (
            back is { Width: 64, Height: 32, Frames: 2 }
                && LoadingVideo.AudioIn(made2) != null
                && LoadingVideo.OriginIn(made2) is { Video: null, QuarterTurns: 0 },
            $"rotating a made loading video back anticlockwise turns its frames back ({back?.Width}×{back?.Height}), keeps its sound and counts the turn"
        );
        using (var left = SkiaSharp.SKBitmap.Decode(LobbyVideo.FramePath(made2, 0)))
        {
            var l = left.GetPixel(8, 16);
            var r = left.GetPixel(56, 16);
            yield return (
                l.Red > 180 && l.Blue < 80 && r.Blue > 180 && r.Red < 80,
                "…with red back on the left and blue on the right"
            );
        }

        // Replacing a video swaps whole folders, so it's always the old one or the new one, never a mix.
        var swap = Path.Combine(root, "loading-swap");
        foreach (var source in new[] { "first", "second" })
        {
            using var b = new LobbyVideo.Builder(swap, source, LoadingVideo.Fps);
            File.WriteAllText(b.NextFramePath(), source);
            File.WriteAllText(b.NextFramePath(), source);
            b.Commit();
        }
        yield return (
            LobbyVideo.ExistingIn(swap)?.Source == "second"
                && File.ReadAllText(LobbyVideo.FramePath(swap, 0)) == "second"
                && !Directory.Exists(swap + ".old")
                && !Directory.Exists(swap + ".new"),
            "a new video replaces the old one whole, leaving nothing behind"
        );
        File.Delete(LobbyVideo.FramePath(swap, 0));
        yield return (
            LobbyVideo.ExistingIn(swap) == null,
            "a video missing its first frame doesn't count"
        );

        // ---- Wheel art ----
        var ft = new WheelArt.Game("Fish Tales", "Williams", 1992);
        yield return (
            WheelArt.Score(
                "POPMedia/Visual Pinball X/Wheel/Fish Tales (Williams 1992).png",
                10,
                ft,
                false
            ) != null
                && WheelArt.Score(
                    "PinballX/Media/Visual Pinball/Wheel Images/Fish Tales.png",
                    10,
                    ft,
                    false
                ) != null
                && WheelArt.Score(
                    "POPMedia/Visual Pinball X/BackGlass/Fish Tales.png",
                    10,
                    ft,
                    false
                ) == null
                && WheelArt.Score("POPMedia/Visual Pinball X/Wheel/Fish Tales.mp4", 10, ft, false)
                    == null,
            @"a media pack's wheel is in Popper's Wheel\ or PinballX's Wheel Images\, never its other pictures"
        );
        string? PickFrom(WheelArt.Game g, params (string, long)[] entries) =>
            WheelArt.Pick(entries, g, wheelPack: true);
        yield return (
            PickFrom(
                ft,
                ("Wheels/Funhouse.png", 300),
                ("Wheels/Fish Tales (Williams 1992).png", 100),
                ("Wheels/Getaway.png", 300)
            ) == "Wheels/Fish Tales (Williams 1992).png"
                && PickFrom(ft, ("Wheels/Funhouse.png", 300), ("Wheels/Getaway.png", 300)) == null
                && PickFrom(ft, ("wheel.png", 300), ("readme.txt", 10)) == "wheel.png",
            "from a wheel pack only this game's wheel (or a plainly named one), never another game's"
        );
        yield return (
            PickFrom(ft, ("Wheel/Fish Tales.jpg", 900), ("Wheel/Fish Tales.png", 100))
                == "Wheel/Fish Tales.png",
            "a transparent .png wins over a .jpg"
        );
        yield return (
            PickFrom(ft, ("AFM_wheel_v2.png", 300), ("readme.txt", 10)) == "AFM_wheel_v2.png"
                && PickFrom(
                    ft,
                    ("(Wheel) Fish Tales (Williams 1992).png", 300),
                    ("(Wheel) Funhouse.png", 300)
                ) == "(Wheel) Fish Tales (Williams 1992).png",
            "a single-game pack's only picture is taken, whatever its name; VPX's \"(Wheel) <game>\" naming is understood"
        );
        var flash = new WheelArt.Game("Flash", "Williams", 1979);
        yield return (
            PickFrom(
                flash,
                ("Flash Gordon (Bally 1981).png", 900),
                ("Flash (Williams 1979).png", 100)
            ) == "Flash (Williams 1979).png"
                && PickFrom(flash, ("Flash Gordon (Bally 1981).png", 900), ("Funhouse.png", 100))
                    == null
                && PickFrom(
                    new WheelArt.Game("Star Trek", "Bally", 1979),
                    ("Star Trek (Data East 1991).png", 900),
                    ("Star Trek (Bally 1979).png", 100),
                    ("Star Trek (Stern 2013).png", 900)
                ) == "Star Trek (Bally 1979).png",
            "namesakes are told apart: the exact name and year win, another year never counts"
        );

        yield return (WheelArt.Key(table) == null, "no wheel art yet: none");
        var zip = Path.Combine(root, "wheel-pack.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (path, _) in pack)
            {
                using var w = new StreamWriter(z.CreateEntry(path).Open());
                w.Write(path);
            }
        }
        var saved = WheelArt.Save(zip, folder, ft, wheelPack: false);
        yield return (
            saved != null
                && Path.GetFileName(saved) == "(Wheel) Fish Tales (Williams 1992).png"
                && WheelArt.Saved(folder) == saved
                && File.ReadAllText(saved).EndsWith("Wheel/Fish Tales (Williams 1992).png"),
            $"a pack's wheel is kept as medias\\(Wheel) <table folder>.png ({Path.GetFileName(saved ?? "none")})"
        );
        var savedKey = WheelArt.Key(table);
        yield return (
            savedKey != null
                && UserArtwork.PathOf(savedKey) == saved
                && LobbyArt.WheelFor(lobbyTable) == savedKey,
            "the lobby shows the saved wheel"
        );
        var mine = Path.Combine(root, "my-wheel.png");
        File.WriteAllText(mine, "png");
        UserArtwork.Set(table, UserArtwork.Kind.Wheel, mine);
        yield return (
            UserArtwork.PathOf(WheelArt.Key(table)!)
                == UserArtwork.Find(table, UserArtwork.Kind.Wheel)
                && UserArtwork
                    .PathOf(WheelArt.Key(table)!)!
                    .Contains(Path.Combine("artwork", "wheel")),
            "your own wheel picture wins over the saved one"
        );
        UserArtwork.Remove(table, UserArtwork.Kind.Wheel);
        WheelArt.RemoveSaved(folder);
        yield return (WheelArt.Key(table) == null, "removing them leaves no wheel art");

        var game = new VpsGame
        {
            Id = "ft",
            Name = "Fish Tales",
            WheelArtFiles =
            [
                new VpsFile
                {
                    Urls = [new VpsUrl { Url = "https://vpuniverse.com/files/file/1-ft-wheel/" }],
                },
                new VpsFile
                {
                    Urls = [new VpsUrl { Url = "https://example.com/broken", Broken = true }],
                },
            ],
            MediaPackFiles =
            [
                new VpsFile
                {
                    Urls = [new VpsUrl { Url = "https://vpuniverse.com/files/file/2-ft-media/" }],
                },
            ],
        };
        var sources = WheelArt.SourcesFor(game);
        yield return (
            sources.Count == 2
                && sources[0].WheelPack
                && sources[0].Source.Url.Contains("ft-wheel")
                && !sources[1].WheelPack
                && sources[1].Source.Url.Contains("ft-media"),
            "wheel art comes from the catalog's wheel art first, then its media pack"
        );
    }
}
