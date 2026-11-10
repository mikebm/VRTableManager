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
    public void Start(
        string folder,
        int frames,
        bool mipmaps,
        Action<CancellationToken>? before = null
    )
    {
        Stop();
        Failed = false;
        var run = ++_run;
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        _workers.RemoveAll(w => w.IsCompleted);
        _workers.Add(
            Task.Run(
                () =>
                {
                    before?.Invoke(ct);
                    if (!Decode(run, folder, frames, mipmaps, ct) && !ct.IsCancellationRequested)
                    {
                        Failed = true;
                    }
                },
                ct
            )
        );
    }

    /// <summary>False when a whole loop gave nothing.</summary>
    bool Decode(int run, string folder, int frames, bool mipmaps, CancellationToken ct)
    {
        var misses = 0;
        for (var i = 0; !ct.IsCancellationRequested; i = (i + 1) % frames)
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
