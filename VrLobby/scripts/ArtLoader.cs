using System.Security.Cryptography;
using System.Text;
using Godot;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>
/// Loads table artwork. Uses VR Table Manager's image cache (same file names: SHA1 of the url + ".img"),
/// downloading into it when missing, a few at a time. Decoding happens off the main thread; only the
/// GPU upload happens on it. Textures are capped at 1024 px on the longest side, and only the most
/// recently used ones are kept (cabinets still showing a texture keep it alive themselves).
/// </summary>
public partial class ArtLoader : Node
{
    public const int MaxSide = 1024;
    public const int PlayfieldMaxSide = 2048;
    public const int KeepTextures = 40;

    /// <summary>url, texture, average colour of the picture.</summary>
    public event Action<string, Texture2D, Color>? Loaded;

    readonly Dictionary<string, (Texture2D? Tex, Color Avg)> _textures = [];
    readonly LinkedList<string> _recent = [];
    readonly SemaphoreSlim _throttle = new(4);

    /// <summary>Decoded pictures waiting for their GPU upload: one per frame, so a burst can't stall the headset.</summary>
    readonly System.Collections.Concurrent.ConcurrentQueue<(
        string Url,
        Image Img,
        Color Avg
    )> _decoded = new();

    public int CachedCount => _textures.Count(kv => kv.Value.Tex != null);

    public static string CacheDir =>
        Directory.CreateDirectory(Path.Combine(AppSettings.DataDir, "img-cache")).FullName;

    /// <summary>Same key as the manager's ImageLoader: Convert.ToHexString(SHA1(url)).</summary>
    public static string CachePath(string url) =>
        Path.Combine(
            CacheDir,
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".img"
        );

    /// <param name="url">A web picture, or "vpx:&lt;table path&gt;" for the playfield inside a table file.</param>
    /// <param name="fallback">Used when <paramref name="url"/> gives nothing usable (delivered under <paramref name="url"/>).</param>
    /// <param name="maxSide">Longest side after shrinking: playfields get 2048 px, they fill your view.</param>
    public void Request(string? url, string? fallback = null, int maxSide = MaxSide)
    {
        if (string.IsNullOrEmpty(url))
            return;
        Touch(url);
        if (_textures.TryGetValue(url, out var known))
        {
            if (known.Tex != null)
                Loaded?.Invoke(url, known.Tex, known.Avg);
            return; // loaded, or still on its way
        }
        _textures[url] = (null, default);
        _ = Task.Run(async () =>
        {
            try
            {
                var img = Decode(await FetchAsync(url) ?? []);
                if (img == null && !string.IsNullOrEmpty(fallback))
                    img = Decode(await FetchAsync(fallback) ?? []);
                if (img == null)
                    return;
                if (img.IsCompressed())
                    img.Decompress();
                Shrink(img, maxSide);
                FillHoles(img);
                var avg = Average(img);
                img.GenerateMipmaps();
                _decoded.Enqueue((url, img, avg)); // the GPU upload happens on the main thread (_Process)
            }
            catch
            { /* no art: the cabinet keeps its plain look */
            }
        });
    }

    /// <summary>Encoded picture bytes: from the table file, or the web picture via the manager's cache.</summary>
    async Task<byte[]?> FetchAsync(string url)
    {
        try
        {
            if (UserArtwork.PathOf(url) is { } mine)
                return await File.ReadAllBytesAsync(mine); // a picture you chose
            if (url.StartsWith(VrTableManager.Lobby.LobbyArt.VpxScheme, StringComparison.Ordinal))
            {
                // Extracted once per table version into the same cache folder, then read from there.
                var extracted = VpxImages.CachedPlayfield(
                    url[VrTableManager.Lobby.LobbyArt.VpxScheme.Length..],
                    CacheDir
                );
                return extracted == null ? null : await File.ReadAllBytesAsync(extracted);
            }
            var file = CachePath(url);
            if (!File.Exists(file))
            {
                await _throttle.WaitAsync();
                try
                {
                    await File.WriteAllBytesAsync(file, await Http.Client.GetByteArrayAsync(url));
                }
                finally
                {
                    _throttle.Release();
                }
            }
            return await File.ReadAllBytesAsync(file);
        }
        catch
        {
            return null;
        }
    }

    public override void _Process(double delta)
    {
        if (_decoded.TryDequeue(out var d))
            Finish(d.Url, d.Img, d.Avg);
    }

    void Finish(string url, Image img, Color avg)
    {
        var tex = ImageTexture.CreateFromImage(img);
        _textures[url] = (tex, avg);
        Touch(url); // (may have been pushed out of the recent list while it was loading)
        Evict();
        Loaded?.Invoke(url, tex, avg);
    }

    void Touch(string url)
    {
        _recent.Remove(url);
        _recent.AddFirst(url);
    }

    void Evict()
    {
        while (_recent.Count > KeepTextures)
        {
            var old = _recent.Last!.Value;
            _recent.RemoveLast();
            if (_textures.TryGetValue(old, out var e) && e.Tex != null)
                _textures.Remove(old);
        }
    }

    /// <summary>Scales down so the longest side is at most <paramref name="maxSide"/>.</summary>
    public static void Shrink(Image img, int maxSide)
    {
        var (w, h) = (img.GetWidth(), img.GetHeight());
        var longest = Math.Max(w, h);
        if (longest <= maxSide)
            return;
        img.Resize(
            Math.Max(1, w * maxSide / longest),
            Math.Max(1, h * maxSide / longest),
            Image.Interpolation.Bilinear
        );
    }

    /// <summary>
    /// Fills a picture's transparent parts from the art around them and makes it opaque. Many tables' playfield
    /// pictures have holes (10-26% of 24, Batman, Big Trouble, The Addams Family…) where, in VPX, lit inserts,
    /// kickers and cut-outs show through from underneath; drawn as they are, the holes came out black. Pull-push:
    /// the picture is averaged down level by level (only the opaque pixels counting), then each hole takes the
    /// colour of the level above, so a hole becomes a soft blend of what surrounds it.
    /// </summary>
    public static void FillHoles(Image img)
    {
        if (img.DetectAlpha() == Image.AlphaMode.None)
            return;
        if (img.GetFormat() != Image.Format.Rgba8)
            img.Convert(Image.Format.Rgba8);
        int w = img.GetWidth(),
            h = img.GetHeight();
        var data = img.GetData();
        // Level 0: colour weighted by alpha (r, g, b, weight).
        var levels = new List<(int W, int H, float[] P)>();
        var p0 = new float[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var a = data[i * 4 + 3] / 255f;
            p0[i * 4] = data[i * 4] * a;
            p0[i * 4 + 1] = data[i * 4 + 1] * a;
            p0[i * 4 + 2] = data[i * 4 + 2] * a;
            p0[i * 4 + 3] = a;
        }
        levels.Add((w, h, p0));
        // Pull: halve until 1×1, summing 2×2 blocks.
        while (levels[^1].W > 1 || levels[^1].H > 1)
        {
            var (pw, ph, pp) = levels[^1];
            int nw = Math.Max(1, (pw + 1) / 2),
                nh = Math.Max(1, (ph + 1) / 2);
            var np = new float[nw * nh * 4];
            for (var y = 0; y < ph; y++)
            for (var x = 0; x < pw; x++)
            {
                int s = (y * pw + x) * 4,
                    d = (y / 2 * nw + x / 2) * 4;
                for (var k = 0; k < 4; k++)
                    np[d + k] += pp[s + k];
            }
            for (var i = 0; i < nw * nh; i++)
                if (np[i * 4 + 3] > 1)
                {
                    var f = 1 / np[i * 4 + 3];
                    for (var k = 0; k < 4; k++)
                        np[i * 4 + k] *= f;
                } // keep weight ≤ 1
            levels.Add((nw, nh, np));
        }
        // Push: from coarse to fine, a pixel short of full weight takes the rest from its parent.
        for (var l = levels.Count - 2; l >= 0; l--)
        {
            var (cw, ch, cp) = levels[l];
            var (uw, _, up) = levels[l + 1];
            for (var y = 0; y < ch; y++)
            for (var x = 0; x < cw; x++)
            {
                int i = (y * cw + x) * 4,
                    u = (y / 2 * uw + x / 2) * 4;
                var missing = 1 - cp[i + 3];
                if (missing <= 0)
                    continue;
                var uw8 = up[u + 3] > 0 ? 1 / up[u + 3] : 0;
                for (var k = 0; k < 3; k++)
                    cp[i + k] += up[u + k] * uw8 * missing;
                cp[i + 3] = 1;
            }
        }
        var out0 = levels[0].P;
        for (var i = 0; i < w * h; i++)
        {
            var a = Math.Max(out0[i * 4 + 3], 1e-6f);
            for (var k = 0; k < 3; k++)
                data[i * 4 + k] = (byte)Math.Clamp(out0[i * 4 + k] / a, 0, 255);
            data[i * 4 + 3] = 255;
        }
        img.SetData(w, h, false, Image.Format.Rgba8, data);
    }

    /// <summary>Average colour, from a tiny copy (cheap and good enough to tint a cabinet).</summary>
    public static Color Average(Image img)
    {
        var small = (Image)img.Duplicate();
        if (small.IsCompressed())
            small.Decompress();
        small.Resize(4, 4, Image.Interpolation.Bilinear);
        float r = 0,
            g = 0,
            b = 0;
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
        {
            var c = small.GetPixel(x, y);
            r += c.R;
            g += c.G;
            b += c.B;
        }
        return new Color(r / 16, g / 16, b / 16);
    }

    /// <summary>Detects PNG / JPEG / WebP from the header bytes (cache files have no extension).</summary>
    public static Image? Decode(byte[] bytes)
    {
        if (bytes.Length < 12)
            return null;
        var img = new Image();
        var err = Error.FileUnrecognized;
        if (bytes[0] == 0x89 && bytes[1] == 0x50)
            err = img.LoadPngFromBuffer(bytes);
        else if (bytes[0] == 0xFF && bytes[1] == 0xD8)
            err = img.LoadJpgFromBuffer(bytes);
        else if (Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP")
            err = img.LoadWebpFromBuffer(bytes);
        return err == Error.Ok ? img : null;
    }
}
