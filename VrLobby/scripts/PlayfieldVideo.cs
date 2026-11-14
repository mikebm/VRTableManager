using Godot;
using VrTableManager.Lobby;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>
/// Plays a table's lobby video (<see cref="LobbyVideo"/>: numbered JPEG frames) on the selected cabinet's
/// playfield, looping. Frames are decoded a few ahead on a worker thread, so the headset's frame rate isn't
/// touched; the main thread only uploads the next one into the same texture when it's due.
/// </summary>
public partial class PlayfieldVideo : Node
{
    readonly FrameStream _frames = new();
    Image? _heights; // the table's height map, handed over from the worker (PlayfieldDepth)
    ImageTexture? _tex;
    Cabinet? _cab;
    double _frameTime,
        _untilNext;

    /// <summary>Folder of the video playing now, or null.</summary>
    public string? Playing { get; private set; }

    /// <summary>Frames put on the playfield since it started (tests).</summary>
    public int FramesShown { get; private set; }
    public event Action? FirstFrameShown;

    /// <summary>The cabinet it plays on, and its table's video (for a handover, <see cref="Release"/>).</summary>
    public Cabinet? Cabinet => _cab;
    public LobbyVideo.Info? Info { get; private set; }

    /// <summary>The frame on show now.</summary>
    public int Frame { get; private set; }

    /// <summary>
    /// Plays on <paramref name="cab"/>. Handed over from another player (<see cref="Release"/>), it carries on from
    /// <paramref name="startAt"/> on the picture already showing (<paramref name="showing"/>), so nothing restarts or
    /// flashes back to the still picture as a cabinet becomes, or stops being, the selected one.
    /// <paramref name="halfSize"/>: a cabinet beside the selected one (decoded and uploaded at a quarter of the pixels).
    /// <paramref name="depth"/>: give the playfield the table's height map (<see cref="PlayfieldDepth"/>); <paramref name="vpxPath"/>
    /// is still used without it, to cut a recorded video's back-board band off.
    /// </summary>
    public void Play(
        Cabinet cab,
        string folder,
        LobbyVideo.Info info,
        string? vpxPath = null,
        int startAt = 0,
        ImageTexture? showing = null,
        bool halfSize = false,
        bool depth = true
    )
    {
        Stop();
        _cab = cab;
        _tex = showing;
        Info = info;
        Playing = folder;
        _frameTime = 1.0 / info.Fps;
        _untilNext = 0;
        FramesShown = 0;
        Frame = startAt;
        _frames.Start(
            folder,
            info.Frames,
            mipmaps: true, // the playfield material samples mipmaps (it's seen at a low angle)
            before: ct =>
            {
                if (
                    depth
                    && vpxPath != null
                    && PlayfieldDepth.Load(folder, vpxPath, LobbyWork.Closing) is { } h
                    && !ct.IsCancellationRequested
                )
                {
                    Volatile.Write(ref _heights, h);
                }
            },
            // A recording made with VPX shows the back board's art above the playfield at the top of each frame:
            // cut off here, while playing, so recordings made before this need nothing redone.
            topTrim: info.Source == "recorded" && vpxPath != null
                ? img => TopTrim(folder, img, vpxPath)
                : null,
            startAt: startAt,
            halfSize: halfSize
        );
    }

    /// <summary>The share of a recorded frame's height above the playfield (<see cref="PlayfieldMatch"/>), or 0.</summary>
    static double TopTrim(string folder, Image frame, string vpxPath) =>
        // Worked out once per video: several play at once now, and each start would read the table's playfield again.
        TopTrims.GetOrAdd(folder, _ => TopTrim(frame, vpxPath));

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> TopTrims =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The table's playfield picture: from the cache Prepare (or an earlier lobby run) filled, else the table file.</summary>
    static byte[]? PlayfieldBytes(string vpxPath) =>
        VpxImages.CachedPlayfield(vpxPath, ArtLoader.ExtractDir) is { } cached
            ? File.ReadAllBytes(cached)
            : VpxImages.ExtractPlayfield(vpxPath);

    static double TopTrim(Image frame, string vpxPath)
    {
        try
        {
            if (PlayfieldBytes(vpxPath) is not { } bytes || ArtLoader.Decode(bytes) is not { } pf)
            {
                return 0;
            }
            var trim = PlayfieldMatch.TopTrim(
                Grey(frame, PlayfieldMatch.FrameW, PlayfieldMatch.FrameH),
                Grey(pf, PlayfieldMatch.PlayfieldW, PlayfieldMatch.PlayfieldH)
            );
            if (trim > 0)
            {
                GD.Print(
                    $"Playfield video: top {trim:P0} of each frame is above the playfield, cut off"
                );
            }
            return trim;
        }
        catch (Exception e)
        {
            GD.PushWarning($"Couldn't match the playfield video to its playfield: {e.Message}");
            return 0;
        }
    }

    static float[] Grey(Image source, int w, int h)
    {
        var img = (Image)source.Duplicate();
        if (img.IsCompressed())
        {
            img.Decompress();
        }
        img.Convert(Image.Format.Rgb8);
        img.Resize(w, h, Image.Interpolation.Bilinear);
        var grey = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = img.GetPixel(x, y);
                grey[y * w + x] = c.R * 0.3f + c.G * 0.59f + c.B * 0.11f;
            }
        }
        return grey;
    }

    /// <summary>
    /// Stops decoding but leaves the picture showing now, and its depth, on the cabinet: for the launch, so the
    /// table doesn't drop from 3D to flat as it starts. <see cref="Stop"/> (the next selection) clears it as usual.
    /// </summary>
    public void Freeze()
    {
        _frames.Stop();
        // Depth that arrived after the first frame but before this frame's _Process: put it on now, or never.
        if (
            _cab != null
            && FramesShown > 0
            && Interlocked.Exchange(ref _heights, null) is { } heights
        )
        {
            _cab.SetPlayfieldDepth(
                ImageTexture.CreateFromImage(heights),
                PlayfieldDepth.DepthOf(heights)
            );
        }
        _heights = null;
        Playing = null; // _Process does nothing more; _cab stays so Stop can put the flat playfield back
    }

    /// <summary>
    /// Stops decoding but leaves the cabinet showing the picture it has (and its depth), and says where the video was,
    /// for another player to carry on from (<see cref="Play"/>'s startAt and showing). Null when nothing was playing.
    /// </summary>
    public (
        Cabinet Cab,
        string Folder,
        LobbyVideo.Info Info,
        int NextFrame,
        ImageTexture? Showing
    )? Release()
    {
        if (Playing is not { } folder || _cab is not { } cab || Info is not { } info)
        {
            return null;
        }

        var handover = (cab, folder, info, (Frame + 1) % Math.Max(1, info.Frames), _tex);
        _frames.Stop();
        _heights = null;
        _cab = null;
        _tex = null;
        Info = null;
        Playing = null;
        return handover;
    }

    public void Stop()
    {
        _frames.Stop();
        _heights = null;
        _cab?.SetPlayfieldVideo(null);
        _cab?.SetPlayfieldDepth(null);
        _cab = null;
        _tex = null;
        Info = null;
        Playing = null;
    }

    public override void _Process(double delta)
    {
        if (Playing == null || _cab == null)
        {
            return;
        }
        // The depth switches on in the same frame as the first video picture, so the still picture never shows
        // with it and the change is one swap, made while the table is still turning in (Main.VideoFrom).
        if (FramesShown > 0 && Interlocked.Exchange(ref _heights, null) is { } heights)
        {
            _cab.SetPlayfieldDepth(
                ImageTexture.CreateFromImage(heights),
                PlayfieldDepth.DepthOf(heights)
            );
        }

        _untilNext -= delta;
        if (_untilNext > 0 || !_frames.TryNext(out var index, out var img))
        {
            return;
        }

        _untilNext = Math.Max(_untilNext + _frameTime, 0); // keep the pace; don't rush to catch up after a stall
        if (
            _tex == null
            || _tex.GetWidth() != img.GetWidth()
            || _tex.GetHeight() != img.GetHeight()
        )
        {
            _tex = ImageTexture.CreateFromImage(img);
            _cab.SetPlayfieldVideo(_tex);
            FirstFrameShown?.Invoke();
        }
        else
        {
            _tex.Update(img);
        }

        FramesShown++;
        Frame = index;
        if (Interlocked.Exchange(ref _heights, null) is { } ready) // usually here: built before the frames
        {
            _cab.SetPlayfieldDepth(
                ImageTexture.CreateFromImage(ready),
                PlayfieldDepth.DepthOf(ready)
            );
        }
    }

    public override void _ExitTree()
    {
        Stop();
        _frames.Shutdown(); // the worker uses Godot's Image: let it finish before the engine shuts down under it
    }
}
