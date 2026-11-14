using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using SkiaSharp;

namespace VrTableManager.Services;

/// <summary>
/// The manager's half of <see cref="LobbyVideo"/>: decodes VPX recordings (QOI) and media pack videos (Windows'
/// Media Foundation, so no codec is installed) and writes the lobby's portrait JPEG frames with SkiaSharp.
/// </summary>
public sealed class VideoFrames : LobbyVideo.IFrameSink
{
    public static readonly VideoFrames Instance = new();

    public void Write(int width, int height, byte[] rgba, int quarterTurns, string destJpg) =>
        WriteFit(width, height, rgba, quarterTurns, destJpg, LobbyVideo.Width, LobbyVideo.Height);

    /// <summary>One frame, turned, fitted inside <paramref name="outW"/>×<paramref name="outH"/> (padded with black), as JPEG.</summary>
    static void WriteFit(
        int width,
        int height,
        byte[] rgba,
        int quarterTurns,
        string destJpg,
        int outW,
        int outH
    )
    {
        using var src = ToBitmap(width, height, rgba);
        WriteFit(src, quarterTurns, destJpg, outW, outH);
    }

    static void WriteFit(SKBitmap src, int quarterTurns, string destJpg, int outW, int outH)
    {
        int width = src.Width,
            height = src.Height;
        quarterTurns = LoadingVideo.Turns(quarterTurns);
        var (w, h) = quarterTurns % 2 == 1 ? (height, width) : (width, height);
        // Fit inside the frame, keeping the shape; pad with black.
        var scale = Math.Min((float)outW / w, (float)outH / h);
        using var dst = new SKBitmap(
            new SKImageInfo(outW, outH, SKColorType.Rgba8888, SKAlphaType.Opaque)
        );
        using (var canvas = new SKCanvas(dst))
        {
            canvas.Clear(SKColors.Black);
            canvas.Translate(outW / 2f, outH / 2f);
            canvas.Scale(scale);
            canvas.RotateDegrees(90 * quarterTurns);
            using var image = SKImage.FromBitmap(src);
            canvas.DrawImage(
                image,
                new SKRect(-width / 2f, -height / 2f, width / 2f, height / 2f),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            );
        }
        using var data = SKImage.FromBitmap(dst).Encode(SKEncodedImageFormat.Jpeg, 78);
        File.WriteAllBytes(destJpg, data.ToArray());
    }

    static SKBitmap ToBitmap(int width, int height, byte[] rgba)
    {
        var bmp = new SKBitmap(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul)
        );
        Marshal.Copy(rgba, 0, bmp.GetPixels(), Math.Min(rgba.Length, width * height * 4));
        return bmp;
    }

    // ---- VPX recording -------------------------------------------------------------------

    /// <summary>
    /// Turns VPX's recorded frames into the lobby video. Cabinet view in a landscape window draws the table with
    /// its top to the left, so each frame is turned a quarter clockwise.
    /// </summary>
    public static LobbyVideo.Info FromCapture(
        IReadOnlyList<string> qoiFrames,
        string tableFolder,
        CancellationToken ct
    )
    {
        using var builder = new LobbyVideo.Builder(tableFolder, "recorded");
        foreach (var f in qoiFrames.Take(LobbyVideo.MaxFrames))
        {
            ct.ThrowIfCancellationRequested();
            var img = Qoi.Decode(File.ReadAllBytes(f));
            Instance.Write(
                img.Width,
                img.Height,
                img.Rgba,
                img.Width > img.Height ? 1 : 0,
                builder.NextFramePath()
            );
        }
        return builder.Commit();
    }

    // ---- Media pack video ------------------------------------------------------------------

    static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new(
        "fb394f3d-ccf1-42ee-bbb3-f9b845d5681d"
    );
    const int FirstVideoStream = unchecked((int)0xFFFFFFFC),
        AllStreams = unchecked((int)0xFFFFFFFE);

    /// <summary>
    /// Decodes up to <paramref name="maxFrames"/> frames of a video, one every 1/<paramref name="fps"/> s, as RGBA.
    /// Empty when Windows can't decode it.
    /// </summary>
    public static IEnumerable<(int Width, int Height, byte[] Rgba)> DecodeVideo(
        string path,
        int fps,
        int maxFrames,
        CancellationToken ct
    )
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        string? copy = null;
        if (Path.GetExtension(path).Equals(".f4v", StringComparison.OrdinalIgnoreCase)) // MP4 inside; MF goes by the extension
        {
            File.Copy(
                path,
                copy = Path.Combine(
                    Path.GetTempPath(),
                    "vrtm-" + Guid.NewGuid().ToString("N")[..8] + ".mp4"
                )
            );
        }

        IMFSourceReader? reader = null;
        try
        {
            MediaFoundationApi.Startup();
            var attrs = MediaFoundationApi.CreateAttributes(1);
            attrs.SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1); // lets the reader convert to RGB32
            MediaFoundationInterop.MFCreateSourceReaderFromURL(copy ?? path, attrs, out reader);
            reader.SetStreamSelection(AllStreams, false);
            reader.SetStreamSelection(FirstVideoStream, true);
            var want = MediaFoundationApi.CreateMediaType();
            want.SetGUID(MediaFoundationAttributes.MF_MT_MAJOR_TYPE, MediaTypes.MFMediaType_Video);
            want.SetGUID(MediaFoundationAttributes.MF_MT_SUBTYPE, MFVideoFormat_RGB32);
            reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, want);
            reader.GetCurrentMediaType(FirstVideoStream, out var actual);
            actual.GetUINT64(MF_MT_FRAME_SIZE, out var size);
            int width = (int)(size >> 32),
                height = (int)(size & 0xFFFFFFFF);
            var stride = width * 4;
            try
            {
                actual.GetUINT32(MF_MT_DEFAULT_STRIDE, out stride);
            }
            catch (COMException) { }
            Marshal.ReleaseComObject(actual);
            if (width <= 0 || height <= 0)
            {
                yield break;
            }

            var step = 10_000_000L / fps; // 100 ns units
            long next = 0;
            var count = 0;
            while (count < maxFrames)
            {
                ct.ThrowIfCancellationRequested();
                reader.ReadSample(
                    FirstVideoStream,
                    0,
                    out _,
                    out var flags,
                    out var time,
                    out var sample
                );
                if (sample == null)
                {
                    if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                    {
                        break;
                    }

                    continue;
                }
                byte[]? rgba = null;
                var copies = 0;
                try
                {
                    // One output frame every `step` from the start (time 0, where the sound starts too): a faster
                    // source (30 or 60 fps) has frames dropped, a slower one has frames repeated, so the result
                    // lasts as long as the source and its sound track stays in time with it.
                    while (next <= (long)time + step / 4)
                    {
                        copies++;
                        next += step;
                    }

                    if (copies == 0)
                    {
                        continue; // between the frames we keep
                    }

                    sample.ConvertToContiguousBuffer(out var buffer);
                    try
                    {
                        buffer.Lock(out var ptr, out _, out var length);
                        try
                        {
                            rgba = ToRgba(ptr, length, width, height, stride);
                        }
                        finally
                        {
                            buffer.Unlock();
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(buffer);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(sample);
                }
                if (rgba == null)
                {
                    break;
                }

                for (var c = 0; c < copies && count < maxFrames; c++)
                {
                    count++;
                    yield return (width, height, rgba);
                }
            }
        }
        finally
        {
            if (reader != null)
            {
                Marshal.ReleaseComObject(reader);
            }

            if (copy != null)
            {
                try
                {
                    File.Delete(copy);
                }
                catch { }
            }
        }
    }

    /// <summary>BGRX rows (bottom-up when the stride is negative) to top-down RGBA.</summary>
    static byte[]? ToRgba(IntPtr ptr, int length, int width, int height, int stride)
    {
        var pitch = Math.Abs(stride);
        if (pitch < width * 4 || (long)pitch * height > length)
        {
            return null;
        }

        var row = new byte[pitch];
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var srcRow = stride < 0 ? height - 1 - y : y;
            Marshal.Copy(ptr + srcRow * pitch, row, 0, pitch);
            var o = y * width * 4;
            for (var x = 0; x < width; x++, o += 4)
            {
                rgba[o] = row[x * 4 + 2];
                rgba[o + 1] = row[x * 4 + 1];
                rgba[o + 2] = row[x * 4];
                rgba[o + 3] = 255;
            }
        }
        return rgba;
    }

    /// <summary>
    /// Turns a media pack's playfield video into the lobby video. A landscape video is a playfield turned for a
    /// cabinet screen; which way it's turned varies, so both ways are compared with the table's own playfield
    /// picture (<paramref name="playfieldImage"/>) when there is one. Null when the video can't be decoded.
    /// </summary>
    public static LobbyVideo.Info? FromVideo(
        string video,
        string tableFolder,
        byte[]? playfieldImage,
        CancellationToken ct
    )
    {
        using var builder = new LobbyVideo.Builder(tableFolder, "media pack");
        int? turns = null;
        foreach (var (w, h, rgba) in DecodeVideo(video, LobbyVideo.Fps, LobbyVideo.MaxFrames, ct))
        {
            turns ??= w <= h ? 0 : BestTurn(w, h, rgba, playfieldImage);
            Instance.Write(w, h, rgba, turns.Value, builder.NextFramePath());
        }
        return builder.Count == 0 ? null : builder.Commit();
    }

    /// <summary>
    /// Turns a video into a loading video in <paramref name="folder"/> (<see cref="LoadingVideo"/>): its first
    /// <see cref="LoadingVideo.MaxSeconds"/> s at its own shape, plus its sound track from the very start (so it
    /// stays in time with the pictures). <paramref name="quarterTurns"/> turns each frame clockwise first (−1 is
    /// anticlockwise), for videos made sideways for a cabinet screen; the frames take the turned shape. With
    /// <paramref name="rememberSource"/> the video's path is kept so it can be turned again from the video itself.
    /// Null when Windows can't decode the video.
    /// </summary>
    public static LobbyVideo.Info? LoadingFromVideo(
        string video,
        string folder,
        CancellationToken ct,
        int quarterTurns = 0,
        bool rememberSource = false
    )
    {
        using var builder = new LobbyVideo.Builder(folder, "loading", LoadingVideo.Fps);
        WriteLoadingFrames(
            builder,
            DecodeVideo(video, LoadingVideo.Fps, LoadingVideo.MaxFrames, ct),
            quarterTurns
        );
        if (builder.Count == 0)
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();
        if (
            CompressedAudio.ToLobbyAudio(
                video,
                builder.Staging,
                TimeSpan.FromSeconds(LoadingVideo.MaxSeconds),
                trimLeadIn: false
            ) is
            { } sound
        )
        {
            File.Move(
                sound,
                Path.Combine(builder.Staging, LoadingVideo.AudioName + Path.GetExtension(sound))
            );
        }

        LoadingVideo.WriteOrigin(
            builder.Staging,
            new(rememberSource ? Path.GetFullPath(video) : null, LoadingVideo.Turns(quarterTurns))
        );
        return builder.Commit();
    }

    /// <summary>
    /// Writes decoded frames as loading video frames: turned <paramref name="quarterTurns"/> clockwise, at the
    /// turned shape (<see cref="LoadingVideo.FrameSize(int, int, int)"/> of the first frame).
    /// </summary>
    public static void WriteLoadingFrames(
        LobbyVideo.Builder builder,
        IEnumerable<(int Width, int Height, byte[] Rgba)> frames,
        int quarterTurns
    )
    {
        foreach (var (w, h, rgba) in frames)
        {
            if (builder.Count == 0)
            {
                (builder.FrameWidth, builder.FrameHeight) = LoadingVideo.FrameSize(
                    w,
                    h,
                    quarterTurns
                );
            }

            WriteFit(
                w,
                h,
                rgba,
                quarterTurns,
                builder.NextFramePath(),
                builder.FrameWidth,
                builder.FrameHeight
            );
        }
    }

    /// <summary>
    /// Turns the loading video already in <paramref name="folder"/> a further <paramref name="quarterTurns"/>
    /// clockwise (−1 is anticlockwise), frame by frame, keeping its sound. For one whose video isn't at hand any
    /// more (a media pack's); one made from a video you still have is better made again from that video. Null when
    /// there's no loading video there.
    /// </summary>
    public static LobbyVideo.Info? RotateLoading(
        string folder,
        int quarterTurns,
        CancellationToken ct
    )
    {
        if (LoadingVideo.In(folder) is not { } video)
        {
            return null;
        }

        var info = video.Info;
        var turns = LoadingVideo.Turns(quarterTurns);
        var origin = LoadingVideo.OriginIn(folder);
        using var builder = new LobbyVideo.Builder(folder, info.Source, info.Fps);
        (builder.FrameWidth, builder.FrameHeight) =
            turns % 2 == 1 ? (info.Height, info.Width) : (info.Width, info.Height);
        for (var i = 0; i < info.Frames; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var frame =
                SKBitmap.Decode(LobbyVideo.FramePath(folder, i))
                ?? throw new InvalidDataException($"Frame {i} can't be read.");
            WriteFit(
                frame,
                turns,
                builder.NextFramePath(),
                builder.FrameWidth,
                builder.FrameHeight
            );
        }

        if (video.Audio != null)
        {
            File.Copy(video.Audio, Path.Combine(builder.Staging, Path.GetFileName(video.Audio)));
        }

        LoadingVideo.WriteOrigin(
            builder.Staging,
            new(origin?.Video, LoadingVideo.Turns((origin?.QuarterTurns ?? 0) + turns))
        );
        return builder.Commit();
    }

    /// <summary>1 (clockwise) or 3 (anticlockwise): whichever makes the frame look more like the playfield picture.</summary>
    static int BestTurn(int w, int h, byte[] rgba, byte[]? playfieldImage)
    {
        const int Clockwise = 1,
            Anticlockwise = 3;
        if (playfieldImage == null)
        {
            return Clockwise; // VPX's cabinet view (and most packs made from it): top on the left
        }

        try
        {
            using var reference = SKBitmap.Decode(playfieldImage);
            if (reference == null)
            {
                return Clockwise;
            }

            var target = Thumb(reference, 0);
            using var frame = ToBitmap(w, h, rgba);
            return
                Similarity(Thumb(frame, Clockwise), target)
                >= Similarity(Thumb(frame, Anticlockwise), target)
                ? Clockwise
                : Anticlockwise;
        }
        catch (Exception)
        {
            return Clockwise;
        }
    }

    const int ThumbW = 24,
        ThumbH = 48;

    /// <summary>A tiny grey picture (turned <paramref name="turns"/> quarters clockwise) for comparing layouts.</summary>
    static float[] Thumb(SKBitmap src, int turns)
    {
        using var small = new SKBitmap(
            new SKImageInfo(ThumbW, ThumbH, SKColorType.Rgba8888, SKAlphaType.Opaque)
        );
        using (var canvas = new SKCanvas(small))
        {
            canvas.Translate(ThumbW / 2f, ThumbH / 2f);
            var (w, h) = turns % 2 == 1 ? (src.Height, src.Width) : (src.Width, src.Height);
            canvas.Scale((float)ThumbW / w, (float)ThumbH / h);
            canvas.RotateDegrees(90 * turns);
            using var image = SKImage.FromBitmap(src);
            canvas.DrawImage(
                image,
                new SKRect(-src.Width / 2f, -src.Height / 2f, src.Width / 2f, src.Height / 2f),
                new SKSamplingOptions(SKFilterMode.Linear)
            );
        }
        var px = new float[ThumbW * ThumbH];
        for (var y = 0; y < ThumbH; y++)
        {
            for (var x = 0; x < ThumbW; x++)
            {
                var c = small.GetPixel(x, y);
                px[y * ThumbW + x] = 0.3f * c.Red + 0.59f * c.Green + 0.11f * c.Blue;
            }
        }

        return px;
    }

    /// <summary>Correlation of two pictures' brightness (−1…1).</summary>
    static float Similarity(float[] a, float[] b)
    {
        float ma = a.Average(),
            mb = b.Average(),
            num = 0,
            da = 0,
            db = 0;
        for (var i = 0; i < a.Length; i++)
        {
            num += (a[i] - ma) * (b[i] - mb);
            da += (a[i] - ma) * (a[i] - ma);
            db += (b[i] - mb) * (b[i] - mb);
        }
        return da == 0 || db == 0 ? 0 : num / MathF.Sqrt(da * db);
    }
}
