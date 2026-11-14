using System.Collections.Concurrent;
using Godot;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>
/// Decodes a folder of numbered JPEG frames (<see cref="LobbyVideo"/>) a few ahead on a worker thread, looping, for
/// the playfield video and the loading screen's video. The main thread only takes the next picture when it's due.
/// <list type="bullet">
/// <item>A frame that's missing or won't decode is skipped; when a whole loop gives nothing, the worker stops
/// (so a broken folder never keeps a core busy) and <see cref="Failed"/> says so.</item>
/// <item>Each <see cref="Start"/> is a new run: pictures a previous run's worker was still finishing are dropped,
/// never shown in the new video.</item>
/// </list>
/// </summary>
public sealed class FrameStream
{
    const int Ahead = 4;

    readonly ConcurrentQueue<(int Run, int Index, Image Img)> _ready = new();
    readonly List<Task> _workers = [];
    CancellationTokenSource? _cts;
    int _run;

    /// <summary>The worker gave up: not one frame of a whole loop could be read.</summary>
    public bool Failed { get; private set; }

    /// <param name="before">Runs on the worker before the first frame (the playfield's height map).</param>
    /// <param name="topTrim">
    /// Run once on the first frame read: the share of its height to cut off the top of every frame (a recorded
    /// video's band of back-board art above the playfield, <see cref="VrTableManager.Lobby.PlayfieldMatch"/>).
    /// </param>
    /// <param name="startAt">The frame to start from: a video handed over from another stream carries on where it was.</param>
    /// <param name="halfSize">Half the width and height: a cabinet beside the selected one, seen from further off.</param>
    public void Start(
        string folder,
        int frames,
        bool mipmaps,
        Action<CancellationToken>? before = null,
        Func<Image, double>? topTrim = null,
        int startAt = 0,
        bool halfSize = false
    )
    {
        Stop();
        Failed = false;
        var run = ++_run;
        // Stopped by Stop, or by the lobby closing (LobbyWork), whichever comes first.
        var cts = _cts = CancellationTokenSource.CreateLinkedTokenSource(LobbyWork.Closing);
        var ct = cts.Token;
        _workers.RemoveAll(w => w.IsCompleted);
        _workers.Add(
            LobbyWork.Run(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    return; // stopped (or the lobby closing) before it began: not even the height map
                }

                before?.Invoke(ct);
                if (
                    !Decode(run, folder, frames, mipmaps, topTrim, startAt, halfSize, ct)
                    && !ct.IsCancellationRequested
                )
                {
                    Failed = true;
                }
            })
        );
    }

    /// <summary>False when a whole loop gave nothing.</summary>
    bool Decode(
        int run,
        string folder,
        int frames,
        bool mipmaps,
        Func<Image, double>? topTrim,
        int startAt,
        bool halfSize,
        CancellationToken ct
    )
    {
        var misses = 0;
        double? trim = null; // worked out on the first frame read
        for (var i = Math.Clamp(startAt, 0, frames - 1); !ct.IsCancellationRequested; i = (i + 1) % frames)
        {
            while (_ready.Count >= Ahead && !ct.IsCancellationRequested)
            {
                Thread.Sleep(5);
            }

            var img = Load(LobbyVideo.FramePath(folder, i));
            if (img == null)
            {
                if (++misses >= frames)
                {
                    GD.PushWarning($"Video {folder}: no frame could be read");
                    return false;
                }

                continue;
            }

            misses = 0;
            trim ??= topTrim?.Invoke(img) ?? 0;
            if (trim > 0)
            {
                var cut = (int)(img.GetHeight() * trim.Value);
                img = img.GetRegion(new Rect2I(0, cut, img.GetWidth(), img.GetHeight() - cut));
            }

            if (halfSize && img.GetWidth() >= 64)
            {
                img.Resize(img.GetWidth() / 2, img.GetHeight() / 2, Image.Interpolation.Bilinear);
            }

            if (mipmaps)
            {
                img.GenerateMipmaps();
            }

            if (!ct.IsCancellationRequested)
            {
                _ready.Enqueue((run, i, img));
            }
        }
        return true;
    }

    static Image? Load(string file)
    {
        try
        {
            var img = new Image();
            return img.LoadJpgFromBuffer(File.ReadAllBytes(file)) == Error.Ok ? img : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // missing (a half-replaced folder) or locked: skip it
        }
    }

    /// <summary>The next picture of the current run, if one is ready.</summary>
    public bool TryNext(out int index, out Image img)
    {
        while (_ready.TryDequeue(out var next))
        {
            if (next.Run == _run)
            {
                (index, img) = (next.Index, next.Img);
                return true;
            }
        }
        (index, img) = (0, null!);
        return false;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose(); // drops its link to LobbyWork.Closing (the worker only reads the token, which stays readable)
        _cts = null;
        _run++; // anything still on its way belongs to no run
        _ready.Clear();
    }

    /// <summary>Stops, and waits briefly for the workers (they use Godot's Image) before the engine shuts down.</summary>
    public void Shutdown()
    {
        Stop();
        try
        {
            Task.WaitAll([.. _workers], 1000);
        }
        catch (AggregateException) { }
        _workers.Clear();
    }
}
