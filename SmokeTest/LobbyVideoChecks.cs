using System.IO.Compression;
using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>Lobby videos (LobbyVideo): which media pack file is the playfield video, the frame folder, VPX recording settings.</summary>
static class LobbyVideoChecks
{
    public static IEnumerable<(bool, string)> Run(string root)
    {
        // PinUP Popper and PinballX layouts; other screens' videos are never the playfield.
        var pack = new (string, long)[]
        {
            ("POPMedia/Visual Pinball X/BackGlass/Fish Tales (Williams 1992).mp4", 9_000_000),
            ("POPMedia/Visual Pinball X/DMD/Fish Tales (Williams 1992).mp4", 2_000_000),
            ("POPMedia/Visual Pinball X/Loading/Fish Tales (Williams 1992).mp4", 5_000_000),
            ("POPMedia/Visual Pinball X/PlayField/Fish Tales (Williams 1992).mp4", 30_000_000),
            ("PinballX/Media/Visual Pinball/Table Videos/Fish Tales (Williams 1992).f4v", 40_000_000),
        };
        yield return (LobbyVideo.PickPlayfield(pack, "Fish Tales") == pack[3].Item1 && LobbyVideo.PickPlayfield([pack[0], pack[4]], "Fish Tales") == pack[4].Item1,
            "a media pack's playfield video is picked (PinUP PlayField\\ or PinballX Table Videos\\)");
        yield return (LobbyVideo.PickPlayfield(pack[..3], "Fish Tales") == null, "backglass, DMD and loading videos are never the playfield");
        yield return (LobbyVideo.PickPlayfield([("medias/(Playfield) Fish Tales (Williams 1992).mp4", 10)], "Fish Tales") != null,
            "VPX's own medias\\(Playfield) naming is recognised");

        // Saved from an archive as medias\(Playfield) <folder>.mp4.
        var folder = Path.Combine(root, "video-tables", "Fish Tales (Williams 1992)");
        Directory.CreateDirectory(folder);
        var zip = Path.Combine(root, "media-pack.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var (path, _) in pack)
                using (var w = new StreamWriter(z.CreateEntry(path).Open())) w.Write(path);
        var saved = LobbyVideo.SavePlayfieldVideo(zip, folder, "Fish Tales");
        yield return (saved != null && Path.GetFileName(saved).StartsWith("(Playfield) Fish Tales (Williams 1992).") && LobbyVideo.ExistingPlayfieldVideo(folder) == saved,
            $"the playfield video is kept as medias\\(Playfield) <table folder> ({Path.GetFileName(saved ?? "none")})");

        // Frames are staged and only replace the old video once complete.
        using (var b = new LobbyVideo.Builder(folder, "test"))
        {
            File.WriteAllText(b.NextFramePath(), "jpg");
            File.WriteAllText(b.NextFramePath(), "jpg");
            b.Commit();
        }
        var info = LobbyVideo.Existing(folder);
        yield return (info is { Frames: 2, Fps: LobbyVideo.Fps } && File.Exists(LobbyVideo.FramePath(LobbyVideo.Folder(folder), 1)), "frames and video.json land in medias\\lobby-video");
        using (var b = new LobbyVideo.Builder(folder, "cancelled"))
            File.WriteAllText(b.NextFramePath(), "jpg"); // disposed without Commit
        yield return (LobbyVideo.Existing(folder)?.Source == "test" && !Directory.Exists(LobbyVideo.Folder(folder) + ".new"),
            "a recording that doesn't finish leaves the previous video as it was");
        LobbyVideo.Remove(folder);
        yield return (LobbyVideo.Existing(folder) == null, "removing deletes it");

        // VPX recording: VR off, cabinet view (the playfield from above) in a small landscape window, muted.
        var s = new AppSettings { VpxDir = Path.Combine(root, "video-vpx") };
        var ini = LobbyVideo.WriteCaptureIni(s);
        var v = IniFile.Read(ini);
        yield return (v[("PlayerVR", "AskToTurnOn")] == "2" && v[("Player", "BGSet")] == "1" && v[("Player", "PlayfieldWidth")] == "1280"
                      && v[("Player", "PlayfieldHeight")] == "720" && v[("Player", "SoundVolume")] == "0" && v[("Plugin.PUP", "Enable")] == "1",
            "recording settings: VR off, cabinet view, 1280x720 window, sound off, plugins as for VR play");
        yield return (LobbyVideo.CaptureArgs(ini, @"C:\t\x.vpx").SequenceEqual(["-Ini", ini, "-CaptureAttract", "180", "15", @"C:\t\x.vpx"]),
            "VPX is asked for 180 frames at 15 fps of the table");

        // QOI, as VPX writes its frames: a 2x1 image (one red pixel written in full, one repeated as a run).
        byte[] qoi = [(byte)'q', (byte)'o', (byte)'i', (byte)'f', 0, 0, 0, 2, 0, 0, 0, 1, 4, 0, 0xFE, 255, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0, 1];
        var img = Qoi.Decode(qoi);
        yield return (img.Width == 2 && img.Height == 1 && img.Rgba.SequenceEqual(new byte[] { 255, 0, 0, 255, 255, 0, 0, 255 }), "QOI frames decode");
    }
}
