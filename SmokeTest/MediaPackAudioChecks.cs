using System.IO.Compression;
using VrTableManager.Models;
using VrTableManager.Services;

/// <summary>
/// Picking a table's attract audio out of community media packs (MediaPackAudio), with synthetic
/// zips laid out like PinUP Popper and PinballX packs. Downloading the packs is the browser's job.
/// </summary>
public static class MediaPackAudioChecks
{
    public static IEnumerable<(bool ok, string name)> Run(string root)
    {
        // Sources: sound files named after the game first (Dropbox links made downloadable), then media packs.
        var game = new VpsGame
        {
            Name = "Fish Tales",
            SoundFiles =
            [
                new VpsFile { Urls = [new VpsUrl { Url = "https://www.dropbox.com/s/abc/Fathom%20%28Bally%201981%29.mp3?dl=0" }] },
                new VpsFile { Urls = [new VpsUrl { Url = "https://www.dropbox.com/s/def/Fish%20Tales%20%28Williams%201992%29.mp3?dl=0" }] },
                new VpsFile { Urls = [new VpsUrl { Url = "https://pinsound-community.org/forum/index.php?/files/file/1-fish-tales/" }] },
            ],
            MediaPackFiles =
            [
                new VpsFile { Authors = ["Bitupset"], Urls = [new VpsUrl { Url = "https://www.vpforums.org/index.php?app=downloads&showfile=4372" }] },
                new VpsFile { Urls = [new VpsUrl { Url = "https://vpuniverse.com/files/file/1-dead/", Broken = true }] },
            ],
        };
        var sources = MediaPackAudio.SourcesFor(game);
        yield return (sources.Count == 2
                && sources[0] is { IsDirect: true, Url: "https://www.dropbox.com/s/def/Fish%20Tales%20%28Williams%201992%29.mp3?dl=1" }
                && sources[1] is { IsDirect: false, Label: "media pack by Bitupset" },
            "sources: the matching sound file (as a download link), then the media pack; other machines' songs, sound packs and broken links left out");

        static VpsGame WithSound(string name, int year, string file) =>
            new() { Name = name, Year = year, SoundFiles = [new VpsFile { Urls = [new VpsUrl { Url = "https://www.dropbox.com/s/x/" + Uri.EscapeDataString(file) + "?dl=0" }] }] };
        yield return (MediaPackAudio.SourcesFor(WithSound("James Cameron's Avatar", 2010, "Avatar (Stern 2010) ver 2.mp3")).Count == 1
                && MediaPackAudio.SourcesFor(WithSound("Avatar", 2010, "Avatar (Stern 2009).mp3")).Count == 0,
            "sound file names match a shorter title (Avatar for James Cameron's Avatar) but not another year");

        // Picking: PinUP's Audio\ beats AudioLaunch\, videos and a stray wav; nothing over the lobby's size cap.
        (string, long)[] pinup =
        [
            ("PinUPSystem/POPMedia/Visual Pinball X/AudioLaunch/Fish Tales (Williams 1992).mp3", 300_000),
            ("PinUPSystem/POPMedia/Visual Pinball X/Audio/Fish Tales (Williams 1992).mp3", 3_000_000),
            ("PinUPSystem/POPMedia/Visual Pinball X/PlayField/Fish Tales (Williams 1992).mp4", 90_000_000),
            ("PinUPSystem/POPMedia/Visual Pinball X/Other2/callout.wav", 50_000),
        ];
        yield return (MediaPackAudio.Pick(pinup, "Fish Tales") == pinup[1].Item1, "PinUP pack: Audio\\ chosen over AudioLaunch\\ and call-outs");
        (string, long)[] pinballx =
        [
            ("Media/Visual Pinball/Launch Audio/Fish Tales.mp3", 200_000),
            ("Media/Visual Pinball/Table Audio/Fish Tales.mp3", 2_500_000),
            ("Media/Visual Pinball/Table Videos/Fish Tales.f4v", 70_000_000),
        ];
        yield return (MediaPackAudio.Pick(pinballx, "Fish Tales") == pinballx[1].Item1, "PinballX pack: Table Audio\\ chosen over Launch Audio\\");
        yield return (MediaPackAudio.Pick([("Audio/huge.mp3", MediaPackAudio.MaxBytes + 1), ("Audio/x.flac", 1000), ("Videos/a.mp4", 1000)], "X") == null,
            "no pick when the only audio is too big for the lobby or a format it can't play");
        yield return (MediaPackAudio.Pick([("Launch/a.ogg", 1000), ("misc/b.ogg", 1000)], "X") == "misc/b.ogg",
            "launch sounds only when nothing better is there");

        // End to end on zips: only the audio lands in music\attract.<ext>; an older attract.* is replaced.
        var dir = Directory.CreateDirectory(Path.Combine(root, "media-pack-audio")).FullName;
        var table = Directory.CreateDirectory(Path.Combine(dir, "Fish Tales (Williams 1992)")).FullName;
        Directory.CreateDirectory(Path.Combine(table, "music"));
        File.WriteAllText(Path.Combine(table, "music", "attract.wav"), "old");
        var pack = Path.Combine(dir, "pack.zip");
        using (var z = ZipFile.Open(pack, ZipArchiveMode.Create))
        {
            foreach (var (path, _) in pinup)
                using (var w = new StreamWriter(z.CreateEntry(path).Open())) w.Write(path.Contains("/Audio/") ? "THEME" : "other");
        }
        var saved = MediaPackAudio.Save(pack, table, "Fish Tales");
        var music = Directory.GetFiles(Path.Combine(table, "music")).Select(Path.GetFileName).ToList();
        yield return (saved == Path.Combine(table, "music", "attract.mp3") && File.ReadAllText(saved) == "THEME"
                && music.SequenceEqual(["attract.mp3"]) && MediaPackAudio.Existing(table) == saved,
            "media pack zip: only its Audio\\ mp3 is kept, as music\\attract.mp3 (old attract.wav removed)");

        // Packs that wrap each front end's media in an inner archive.
        var outer = Path.Combine(dir, "outer.zip");
        var inner = Path.Combine(dir, "PinballX.zip");
        using (var z = ZipFile.Open(inner, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("Table Audio/Fish Tales.ogg").Open())) w.Write("INNER");
        using (var z = ZipFile.Open(outer, ZipArchiveMode.Create))
        {
            z.CreateEntryFromFile(inner, "PinballX.zip");
            z.CreateEntry("Wheel/Fish Tales.png");
        }
        saved = MediaPackAudio.Save(outer, table, "Fish Tales");
        yield return (saved == Path.Combine(table, "music", "attract.ogg") && File.ReadAllText(saved) == "INNER"
                && !File.Exists(Path.Combine(table, "music", "attract.mp3")) && Directory.GetFiles(dir, "inner-*").Length == 0,
            "audio found one archive deep; the inner archive's temp copy is cleaned up");

        // A pack with no audio leaves the table alone; a downloaded .mp3 is used as is.
        var videos = Path.Combine(dir, "videos.zip");
        using (var z = ZipFile.Open(videos, ZipArchiveMode.Create)) z.CreateEntry("PlayField/a.mp4");
        yield return (MediaPackAudio.Save(videos, table, "Fish Tales") == null && File.Exists(Path.Combine(table, "music", "attract.ogg")),
            "a pack without audio gives nothing and keeps the current attract audio");
        var video = Path.Combine(dir, "MonsterBashAttract.mp4");
        File.WriteAllText(video, "not an archive");
        yield return (MediaPackAudio.Save(video, table, "Fish Tales") == null, "a \"media pack\" that is a single video gives nothing (no error)");
        var mp3 = Path.Combine(dir, "Fish Tales (Williams 1992).mp3");
        File.WriteAllText(mp3, "DIRECT");
        saved = MediaPackAudio.Save(mp3, table, "Fish Tales");
        yield return (saved == Path.Combine(table, "music", "attract.mp3") && File.ReadAllText(saved) == "DIRECT"
                && !File.Exists(Path.Combine(table, "music", "attract.ogg")), "a downloaded sound file is saved as music\\attract.mp3");
        MediaPackAudio.Remove(table);
        yield return (MediaPackAudio.Existing(table) == null, "remove deletes the attract audio");

        foreach (var check in CompressedChecks(dir, table)) yield return check;
    }

    /// <summary>AAC (video sound tracks, .m4a) converted to MP3 through Windows' Media Foundation, with real encoded files.</summary>
    static IEnumerable<(bool ok, string name)> CompressedChecks(string dir, string table)
    {
        string[] conv = [.. CompressedAudio.Exts];
        var order = MediaPackAudio.PickConvertible(
            ["Tutorial Videos/X.mp4", "Promo Videos/X.mp4", "Media/Visual Pinball/Table Videos/X.f4v", "Videos/X attract.mp4", "Audio/X.m4a", "Wheel/X.png"], conv, "X");
        yield return (order.SequenceEqual(["Audio/X.m4a", "Videos/X attract.mp4", "Media/Visual Pinball/Table Videos/X.f4v"]),
            "convertible entries: .m4a, then attract videos, then table videos (at most three tried)");
        yield return (MediaPackAudio.PickConvertible(["Tutorial Videos/X.mp4", "Promo Videos/X.mp4"], conv, "X").SequenceEqual(["Promo Videos/X.mp4"]),
            "promo videos only as a last resort; tutorials (narration) never");

        // 2 s of silence then 5 s of tone; a silent clip; 90 s of tone.
        var tone = Path.Combine(dir, "tone.mp4");
        var silent = Path.Combine(dir, "silent.mp4");
        var longTone = Path.Combine(dir, "long.m4a");
        string? error = null;
        try
        {
            EncodeAac(tone, 2, 5);
            EncodeAac(silent, 4, 0);
            EncodeAac(longTone, 0, 90);
        }
        catch (Exception e) { error = e.Message; }
        if (error != null)
        {
            yield return (false, "Windows' AAC encoder made test files: " + error);
            yield break;
        }

        var mp3 = CompressedAudio.ToLobbyAudio(tone, dir);
        var length = mp3 == null ? TimeSpan.Zero : Duration(mp3);
        yield return (mp3?.EndsWith(".mp3") == true && length.TotalSeconds is > 4.5 and < 6,
            $"AAC in an .mp4 becomes an MP3 without the leading silence ({length.TotalSeconds:F1} s)");
        yield return (CompressedAudio.ToLobbyAudio(silent, dir) == null, "a silent video gives nothing");
        var clipped = CompressedAudio.ToLobbyAudio(longTone, dir);
        yield return (clipped != null && Duration(clipped) <= CompressedAudio.MaxLength + TimeSpan.FromSeconds(1)
                && new FileInfo(clipped).Length < MediaPackAudio.MaxBytes,
            $"a long track is cut to {CompressedAudio.MaxLength.TotalSeconds:0} s and stays under the lobby's size limit");
        yield return (CompressedAudio.ToLobbyAudio(Path.Combine(dir, "pack.zip"), dir) == null, "a file that isn't audio or video gives nothing");
        foreach (var f in Directory.GetFiles(dir, "converted-*")) File.Delete(f); // the direct conversions above

        // A HyperPin-style pack with videos only: the table video's sound (an .f4v, really MP4) is used, the silent tutorial isn't.
        var videoPack = Path.Combine(dir, "hyperpin.zip");
        using (var z = ZipFile.Open(videoPack, ZipArchiveMode.Create))
        {
            z.CreateEntryFromFile(silent, "Media/Visual Pinball/Tutorial Videos/Fish Tales.mp4");
            z.CreateEntryFromFile(tone, "Media/Visual Pinball/Table Videos/Fish Tales (Williams 1992).f4v");
            z.CreateEntry("Media/Visual Pinball/Wheel Images/Fish Tales.png");
        }
        var saved = MediaPackAudio.Save(videoPack, table, "Fish Tales", CompressedAudio.Converter);
        yield return (saved == Path.Combine(table, "music", "attract.mp3") && Duration(saved).TotalSeconds > 4.5
                && Directory.GetFiles(dir, "inner-*").Length == 0 && Directory.GetFiles(dir, "converted-*").Length == 0,
            "video-only pack: the table video's sound saved as music\\attract.mp3, scratch files removed");
        yield return (MediaPackAudio.Save(videoPack, table, "Fish Tales") == null, "without a converter a video-only pack still gives nothing");

        // A download that is a single video (Monster Bash's attract .mp4).
        MediaPackAudio.Remove(table);
        saved = MediaPackAudio.Save(tone, table, "Fish Tales", CompressedAudio.Converter);
        yield return (saved == Path.Combine(table, "music", "attract.mp3"), "a single downloaded video gives its sound as music\\attract.mp3");
        foreach (var f in Directory.GetFiles(dir, "converted-*")) File.Delete(f);
    }

    static void EncodeAac(string path, double silenceSeconds, double toneSeconds)
    {
        const int rate = 44100;
        var frames = (int)((silenceSeconds + toneSeconds) * rate);
        var pcm = new byte[frames * 4];
        for (var i = (int)(silenceSeconds * rate); i < frames; i++)
        {
            var s = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4), s);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4 + 2), s);
        }
        NAudio.MediaFoundation.MediaFoundationApi.Startup();
        using var raw = new NAudio.Wave.RawSourceWaveStream(new MemoryStream(pcm), new NAudio.Wave.WaveFormat(rate, 16, 2));
        NAudio.Wave.MediaFoundationEncoder.EncodeToAac(raw, path, 128_000);
    }

    static TimeSpan Duration(string file)
    {
        using var r = new NAudio.Wave.MediaFoundationReader(file);
        return r.TotalTime;
    }
}
