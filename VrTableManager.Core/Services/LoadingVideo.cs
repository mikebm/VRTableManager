namespace VrTableManager.Services;

/// <summary>
/// A video the VR lobby plays on a big screen while the table you picked starts, like the "loading" videos
/// PinUP Popper shows on a cabinet. Kept as numbered JPEG frames plus <c>video.json</c> (the lobby video's format,
/// <see cref="LobbyVideo"/>) at the video's own shape, and its sound as <c>audio.mp3</c> (or .wav) beside them:
/// <list type="bullet">
/// <item>per table, in <c>&lt;table folder&gt;\medias\loading-video</c>: from the catalog's media pack (Popper's
/// <c>Loading\</c> video) or a video you chose;</item>
/// <item>for every other table, one you chose for all of them, in <c>%APPDATA%\VrTableManager\loading-video</c>.</item>
/// </list>
/// The frames are made by the manager (it has the video decoder); the lobby only reads them.
/// </summary>
public static class LoadingVideo
{
    public const string FolderName = "loading-video";
    public const string AudioName = "audio";
    public static readonly string[] AudioExts = [".mp3", ".wav"];

    /// <summary>Frame rate: the same as the lobby video's, smooth enough for a screen across the room.</summary>
    public const int Fps = LobbyVideo.Fps;

    /// <summary>At most this long (it loops when a table takes longer to start). 30 s at 1280 px is about 25 MB.</summary>
    public const int MaxSeconds = 30;
    public const int MaxFrames = Fps * MaxSeconds;

    /// <summary>Frames are this many pixels on their longest side, at the video's own shape.</summary>
    public const int LongSide = 1280;

    /// <summary>A loading video ready to play: its frames' folder, their info, and its sound (or null).</summary>
    public sealed record Video(string Folder, LobbyVideo.Info Info, string? Audio);

    /// <summary>A table's own loading video folder.</summary>
    public static string TableFolder(string tableFolder) =>
        Path.Combine(tableFolder, "medias", FolderName);

    /// <summary>The loading video for every table without its own.</summary>
    public static string DefaultFolder => Path.Combine(AppSettings.DataDir, FolderName);

    /// <summary>The video in <paramref name="folder"/>, or null (none, or incomplete).</summary>
    public static Video? In(string folder) =>
        LobbyVideo.ExistingIn(folder) is { } info ? new Video(folder, info, AudioIn(folder)) : null;

    public static string? AudioIn(string folder) =>
        AudioExts
            .Select(e => Path.Combine(folder, AudioName + e))
            .FirstOrDefault(f => File.Exists(f) && new FileInfo(f).Length > 0);

    /// <summary>The table's own loading video, or null.</summary>
    public static Video? Own(InstalledTable t) => t.HasOwnFolder ? In(TableFolder(t.Folder)) : null;

    /// <summary>What plays while <paramref name="t"/> starts: its own loading video, else the one for every table, else null.</summary>
    public static Video? For(InstalledTable t) => Own(t) ?? In(DefaultFolder);

    /// <summary>
    /// Where a loading video's frames came from, kept beside them in <see cref="OriginFile"/> so it can be turned
    /// again from the video itself: the video you chose (null for a media pack's, whose copy is deleted) and the
    /// clockwise quarter turns its frames were given.
    /// </summary>
    public sealed record Origin(string? Video, int QuarterTurns);

    public const string OriginFile = "source.json";

    /// <summary>The <see cref="Origin"/> saved in <paramref name="folder"/>, or null (none: made before it was kept).</summary>
    public static Origin? OriginIn(string folder)
    {
        try
        {
            var path = Path.Combine(folder, OriginFile);
            return File.Exists(path)
                ? System.Text.Json.JsonSerializer.Deserialize<Origin>(File.ReadAllText(path))
                : null;
        }
        catch (Exception e)
            when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void WriteOrigin(string folder, Origin origin) =>
        File.WriteAllText(
            Path.Combine(folder, OriginFile),
            System.Text.Json.JsonSerializer.Serialize(origin)
        );

    /// <summary>Clockwise quarter turns as 0–3 (−1, anticlockwise, is 3).</summary>
    public static int Turns(int quarterTurns) => ((quarterTurns % 4) + 4) % 4;

    public static void Remove(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>The frame size for a <paramref name="w"/>×<paramref name="h"/> video: its shape, <see cref="LongSide"/> long (never larger), even sides.</summary>
    public static (int W, int H) FrameSize(int w, int h)
    {
        if (w <= 0 || h <= 0)
        {
            return (LongSide, LongSide * 9 / 16);
        }

        var scale = Math.Min(1.0, (double)LongSide / Math.Max(w, h));
        static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);
        return (Even(w * scale), Even(h * scale));
    }

    /// <summary><see cref="FrameSize(int, int)"/> after <paramref name="quarterTurns"/> clockwise quarter turns (odd turns swap the sides).</summary>
    public static (int W, int H) FrameSize(int w, int h, int quarterTurns) =>
        Turns(quarterTurns) % 2 == 1 ? FrameSize(h, w) : FrameSize(w, h);

    /// <summary>
    /// How good a media pack entry is as the table's loading video, or null when it isn't one. PinUP Popper keeps
    /// it in <c>Loading\</c>; some packs name it "… loading …" or put it with the launch media.
    /// </summary>
    public static int? Score(string entryPath, long size, string gameName)
    {
        if (
            size <= 0
            || !LobbyVideo.VideoExts.Contains(Path.GetExtension(entryPath).ToLowerInvariant())
        )
        {
            return null;
        }

        var (folders, file) = MediaPackFiles.Parts(entryPath);
        if (
            folders
                .Concat([file])
                .Any(p => p.Contains("tutorial") || p.Contains("help") || p.Contains("instruction"))
        )
        {
            return null;
        }

        int score;
        if (folders.Any(f => f.Contains("loading")))
        {
            score = 50;
        }
        else if (file.Contains("loading"))
        {
            score = 40;
        }
        else if (folders.Any(f => f.Contains("launch")) || file.Contains("launch"))
        {
            score = 25;
        }
        else
        {
            return null; // some other screen's video
        }

        if (MediaPackFiles.Normalize(gameName) is { Length: > 2 } name && file.Contains(name))
        {
            score += 10;
        }

        if (Path.GetExtension(entryPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            score += 5; // plays as is; .f4v needs a copy
        }

        return score;
    }

    /// <summary>
    /// The pack's loading video taken out of a downloaded media pack (or a downloaded video used as it is), or null
    /// when it has none. Dispose it to delete the scratch copy.
    /// </summary>
    public static MediaPackFiles.Extracted? FromPack(
        string downloaded,
        string gameName,
        CancellationToken ct = default
    ) =>
        MediaPackFiles.ExtractToFile(
            downloaded,
            MediaPackFiles.Best((p, size) => Score(p, size, gameName)),
            [],
            ct
        );
}
