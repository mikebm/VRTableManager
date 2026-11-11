using Godot;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>
/// The loading screen's video (<see cref="LoadingVideo"/>): a big screen standing where the picked cabinet was,
/// playing the table's loading video in a loop with its sound, until the table takes the headset over. Frames are
/// decoded a few ahead on a worker thread, as for the playfield video, so the headset's frame rate isn't touched.
/// </summary>
public partial class LoadingScreenVideo : Node3D
{
    /// <summary>The screen's largest size (m): about a 90-inch TV at 2.7 m, filling a comfortable part of the view.</summary>
    public const float MaxWidth = 2.0f,
        MaxHeight = 1.4f;

    readonly FrameStream _frames = new();
    readonly MeshInstance3D _screen = new();
    readonly MeshInstance3D _frame = new();
    readonly StandardMaterial3D _mat = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = Colors.Black,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
    };
    readonly AudioStreamPlayer3D _sound = new() { VolumeDb = -8f, UnitSize = 4f };
    ImageTexture? _tex;
    double _frameTime,
        _untilNext;

    /// <summary>Folder of the video playing now, or null.</summary>
    public string? Playing { get; private set; }
    public int FramesShown { get; private set; }
    public bool SoundPlaying => _sound.Playing;

    /// <summary>The screen's size (m) for the video playing.</summary>
    public Vector2 Size { get; private set; }

    public override void _Ready()
    {
        _screen.Mesh = new QuadMesh();
        _screen.MaterialOverride = _mat;
        _screen.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(_screen);
        // A chrome-edged black bezel just behind the picture, in the room's style.
        _frame.Mesh = new QuadMesh();
        _frame.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.05f, 0.05f, 0.055f),
            Metallic = 0.9f,
            Roughness = 0.25f,
        };
        _frame.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _frame.Position = new Vector3(0, 0, -0.01f);
        AddChild(_frame);
        AddChild(_sound);
        Visible = false;
    }

    /// <summary>The screen size for a <paramref name="w"/>×<paramref name="h"/> video: its shape, as big as fits.</summary>
    public static Vector2 FitSize(int w, int h)
    {
        var aspect = w > 0 && h > 0 ? (float)w / h : 16f / 9f;
        var width = Math.Min(MaxWidth, MaxHeight * aspect);
        return new Vector2(width, width / aspect);
    }

    public void Play(LoadingVideo.Video video)
    {
        Stop();
        Playing = video.Folder;
        FramesShown = 0;
        _frameTime = 1.0 / video.Info.Fps;
        _untilNext = 0;
        Size = FitSize(video.Info.Width, video.Info.Height);
        ((QuadMesh)_screen.Mesh).Size = Size;
        ((QuadMesh)_frame.Mesh).Size = Size + new Vector2(0.06f, 0.06f);
        _mat.AlbedoTexture = null;
        _mat.AlbedoColor = Colors.Black; // black until the first frame, never a white flash
        _sound.Stream = video.Audio != null ? LoadSound(video.Audio) : null;
        Visible = true;
        _frames.Start(video.Folder, video.Info.Frames, mipmaps: true); // seen from across the room: smaller than its pixels
    }

    static AudioStream? LoadSound(string file)
    {
        try
        {
            return Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".mp3" => new AudioStreamMP3 { Data = File.ReadAllBytes(file) },
                ".wav" => AudioStreamWav.LoadFromFile(file),
                _ => null,
            };
        }
        catch (Exception e)
        {
            GD.PushWarning($"Loading video sound {file}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Silences it, for good, while the picture plays on: the table has the headset's focus (and soon its sound),
    /// though the lobby may still be shown for a while (LobbyHandover).
    /// </summary>
    public void Mute()
    {
        _sound.Stop();
        _sound.Stream = null; // or the next loop would start it again
    }

    public void Stop()
    {
        _frames.Stop();
        _sound.Stop();
        _sound.Stream = null;
        _mat.AlbedoTexture = null;
        _tex = null;
        Playing = null;
        Visible = false;
    }

    public override void _Process(double delta)
    {
        if (Playing == null)
        {
            return;
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
            _mat.AlbedoTexture = _tex;
            _mat.AlbedoColor = Colors.White;
        }
        else
        {
            _tex.Update(img);
        }

        // The sound starts with the first picture and again each time the video loops (the frame number goes
        // back), so they stay together, even when frame 0 itself is missing.
        if (_sound.Stream != null && (FramesShown == 0 || index < _lastIndex))
        {
            _sound.Play(index * (float)_frameTime);
        }

        _lastIndex = index;
        FramesShown++;
    }

    int _lastIndex;

    /// <summary>Not one frame could be read: the loading screen goes back to the plain one.</summary>
    public bool Failed => Playing != null && _frames.Failed;

    public override void _ExitTree()
    {
        Stop();
        _frames.Shutdown(); // the worker uses Godot's Image: let it finish before the engine shuts down under it
    }
}
