using System.Security.Cryptography;
using System.Text;
using Godot;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>
/// Loads table artwork. Uses VR Table Manager's image cache (same file names: SHA1 of the url + ".img"),
/// downloading into it when missing, a few at a time.
/// </summary>
public partial class ArtLoader : Node
{
    public const int MaxWidth = 640;
    public event Action<string, Texture2D>? Loaded;

    readonly Dictionary<string, Texture2D?> _textures = [];
    readonly SemaphoreSlim _throttle = new(4);

    public static string CacheDir => Directory.CreateDirectory(Path.Combine(AppSettings.DataDir, "img-cache")).FullName;

    /// <summary>Same key as the manager's ImageLoader: Convert.ToHexString(SHA1(url)).</summary>
    public static string CachePath(string url) =>
        Path.Combine(CacheDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".img");

    public void Request(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        if (_textures.TryGetValue(url, out var tex))
        {
            if (tex != null) Loaded?.Invoke(url, tex);
            return;
        }
        _textures[url] = null;
        _ = Task.Run(async () =>
        {
            try
            {
                var file = CachePath(url);
                if (!File.Exists(file))
                {
                    await _throttle.WaitAsync();
                    try { await File.WriteAllBytesAsync(file, await Http.Client.GetByteArrayAsync(url)); }
                    finally { _throttle.Release(); }
                }
                var bytes = await File.ReadAllBytesAsync(file);
                Callable.From(() => Finish(url, bytes)).CallDeferred(); // textures are made on the main thread
            }
            catch { /* no art: the card keeps its placeholder colour */ }
        });
    }

    void Finish(string url, byte[] bytes)
    {
        var img = Decode(bytes);
        if (img == null) return;
        if (img.GetWidth() > MaxWidth)
            img.Resize(MaxWidth, img.GetHeight() * MaxWidth / img.GetWidth(), Image.Interpolation.Bilinear);
        img.GenerateMipmaps();
        var tex = ImageTexture.CreateFromImage(img);
        _textures[url] = tex;
        Loaded?.Invoke(url, tex);
    }

    /// <summary>Detects PNG / JPEG / WebP from the header bytes (cache files have no extension).</summary>
    public static Image? Decode(byte[] bytes)
    {
        if (bytes.Length < 12) return null;
        var img = new Image();
        var err = Error.FileUnrecognized;
        if (bytes[0] == 0x89 && bytes[1] == 0x50) err = img.LoadPngFromBuffer(bytes);
        else if (bytes[0] == 0xFF && bytes[1] == 0xD8) err = img.LoadJpgFromBuffer(bytes);
        else if (Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP") err = img.LoadWebpFromBuffer(bytes);
        return err == Error.Ok ? img : null;
    }
}
