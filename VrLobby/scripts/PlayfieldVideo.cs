using System.Collections.Concurrent;
using Godot;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>
/// Plays a table's lobby video (<see cref="LobbyVideo"/>: numbered JPEG frames) on the selected cabinet's
/// playfield, looping. Frames are decoded a few ahead on a worker thread, so the headset's frame rate isn't
/// touched; the main thread only uploads the next one into the same texture when it's due.
/// </summary>
public partial class PlayfieldVideo : Node
{
    const int Ahead = 4;

    readonly ConcurrentQueue<Image> _ready = new();
    Image? _heights;  // the table's height map, handed over from the worker (PlayfieldDepth)
    CancellationTokenSource? _cts;
    Task? _worker;
    ImageTexture? _tex;
    Cabinet? _cab;
    double _frameTime, _untilNext;

    /// <summary>Folder of the video playing now, or null.</summary>
    public string? Playing { get; private set; }
    /// <summary>Frames put on the playfield since it started (tests).</summary>
    public int FramesShown { get; private set; }
    public event Action? FirstFrameShown;

    public void Play(Cabinet cab, string folder, LobbyVideo.Info info, string? vpxPath = null)
    {
        Stop();
        _cab = cab;
        Playing = folder;
        _frameTime = 1.0 / info.Fps;
        _untilNext = 0;
        FramesShown = 0;
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        _worker = Task.Run(() =>
        {
            if (vpxPath != null && PlayfieldDepth.Load(folder, vpxPath) is { } h && !ct.IsCancellationRequested) Volatile.Write(ref _heights, h);
            Decode(folder, info.Frames, ct);
        }, ct);
    }

    void Decode(string folder, int frames, CancellationToken ct)
    {
        try
        {
            for (var i = 0; !ct.IsCancellationRequested; i = (i + 1) % frames)
            {
                while (_ready.Count >= Ahead && !ct.IsCancellationRequested) Thread.Sleep(5);
                var img = new Image();
                if (img.LoadJpgFromBuffer(File.ReadAllBytes(LobbyVideo.FramePath(folder, i))) != Error.Ok) continue;
                img.GenerateMipmaps(); // the playfield material samples mipmaps (it's seen at a low angle)
                if (!ct.IsCancellationRequested) _ready.Enqueue(img);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"Lobby video {folder}: {e.Message}");
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _ready.Clear();
        _heights = null;
        _cab?.SetPlayfieldVideo(null);
        _cab?.SetPlayfieldDepth(null);
        _cab = null;
        _tex = null;
        Playing = null;
    }

    public override void _Process(double delta)
    {
        if (Playing == null || _cab == null) return;
        // The depth switches on in the same frame as the first video picture, so the still picture never shows
        // with it and the change is one swap, made while the table is still turning in (Main.VideoFrom).
        if (FramesShown > 0 && Interlocked.Exchange(ref _heights, null) is { } heights)
            _cab.SetPlayfieldDepth(ImageTexture.CreateFromImage(heights), PlayfieldDepth.DepthOf(heights));
        _untilNext -= delta;
        if (_untilNext > 0 || !_ready.TryDequeue(out var img)) return;
        _untilNext = Math.Max(_untilNext + _frameTime, 0); // keep the pace; don't rush to catch up after a stall
        if (_tex == null || _tex.GetWidth() != img.GetWidth() || _tex.GetHeight() != img.GetHeight())
        {
            _tex = ImageTexture.CreateFromImage(img);
            _cab.SetPlayfieldVideo(_tex);
            FirstFrameShown?.Invoke();
        }
        else _tex.Update(img);
        FramesShown++;
        if (Interlocked.Exchange(ref _heights, null) is { } ready) // usually here: built before the frames
            _cab.SetPlayfieldDepth(ImageTexture.CreateFromImage(ready), PlayfieldDepth.DepthOf(ready));
    }

    public override void _ExitTree()
    {
        Stop();
        // The worker uses Godot's Image: let it finish before the engine shuts down under it.
        try { _worker?.Wait(1000); } catch (AggregateException) { }
        _worker = null;
    }
}
