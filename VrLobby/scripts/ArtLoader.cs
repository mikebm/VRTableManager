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

    /// <summary>
    /// Pictures kept: a playfield and a backglass for each cabinet in the ring, the selected one's wheel, and a few
    /// just scrolled past (40 for the default 11). The lobby sets it from the ring's size.
    /// </summary>
    public int KeepTextures { get; set; } = KeepFor(VrTableManager.Lobby.LobbyRing.Default.Count);

    public static int KeepFor(int ringCount) => ringCount * 3 + 7;

    /// <summary>url, texture, average colour of the picture.</summary>
    public event Action<string, Texture2D, Color>? Loaded;

    readonly Dictionary<string, (Texture2D? Tex, Color Avg)> _textures = [];
    readonly LinkedList<string> _recent = [];
    readonly SemaphoreSlim _throttle = new(4);

    /// <summary>
    /// Decoded pictures waiting for their GPU upload: one per frame, so a burst can't stall the headset. A null
    /// picture is one that couldn't be had (see <see cref="Failed"/>).
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentQueue<(
        string Url,
        Image? Img,
        Color Avg
    )> _decoded = new();

    /// <summary>
    /// Pictures that couldn't be had, and when to try again: a minute after the first miss, doubling up to half an
    /// hour, so a dropped connection doesn't cost a table its art for the whole run, nor hammer the server.
    /// </summary>
    readonly Dictionary<string, (int Misses, DateTime Retry)> _failed = [];

    /// <summary>The fallback each request last went out with (see Request).</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _triedFallback = new();

    /// <summary>The fallback each request's work actually used ("" for none).</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _usedFallback = new();

    /// <summary>How long a download may take before it counts as failed (and is tried again later).</summary>
    static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    public int CachedCount => _textures.Count(kv => kv.Value.Tex != null);

    public static string CacheDir =>
        Directory.CreateDirectory(Path.Combine(AppSettings.DataDir, "img-cache")).FullName;

    /// <summary>
    /// Where pictures read out of table files and .directb2s files go (the playfield, the table's own backglass and
    /// back panel, the .directb2s's backglass): the lobby's own folder (VrTableManager.Lobby.LobbyFiles), not the
    /// img-cache it shares with the manager for pictures from the web.
    /// </summary>
    public static string ExtractDir => Directory.CreateDirectory(VrTableManager.Lobby.LobbyFiles.Pictures).FullName;

    /// <summary>Same key as the manager's ImageLoader: Convert.ToHexString(SHA1(url)).</summary>
    public static string CachePath(string url) =>
        Path.Combine(
            CacheDir,
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".img"
        );

    /// <param name="url">A web picture, or "vpx:&lt;table path&gt;" for the playfield inside a table file.</param>
    /// <param name="fallback">Used when <paramref name="url"/> gives nothing usable (delivered under <paramref name="url"/>).</param>
    /// <param name="maxSide">Longest side after shrinking: playfields get 2048 px, they fill your view.</param>
    /// <param name="keepAlpha">Leave transparent parts transparent (a wheel logo) instead of filling them in.</param>
    public void Request(
        string? url,
        string? fallback = null,
        int maxSide = MaxSide,
        bool keepAlpha = false
    )
    {
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        // A fallback it didn't have before (the catalog's picture, once the catalog has loaded): kept for a request
        // still on its way, and a reason to try again one that missed (a table file's backglass turned down).
        var newFallback =
            !string.IsNullOrEmpty(fallback) && _triedFallback.GetValueOrDefault(url) != fallback;
        if (newFallback)
        {
            _triedFallback[url] = fallback;
        }

        Touch(url);
        if (_textures.TryGetValue(url, out var known))
        {
            if (known.Tex != null)
            {
                Loaded?.Invoke(url, known.Tex, known.Avg);
            }

            return; // loaded, or still on its way
        }

        // Missed a moment ago: not again yet, unless it now brings a fallback it didn't have (the catalog's picture,
        // once the catalog has loaded: a table file whose backglass was turned down has nothing else to show).
        if (
            _failed.TryGetValue(url, out var miss)
            && DateTime.UtcNow < miss.Retry
            && !newFallback
        )
        {
            return;
        }

        _textures[url] = (null, default);
        _ = LobbyWork.Run(async () =>
        {
            try
            {
                var bytes = await FetchAsync(url);
                LobbyWork.Closing.ThrowIfCancellationRequested(); // closing: no decoding now
                var img = Decode(bytes ?? []);
                if (img == null)
                {
                    DropIfCorrupt(url, bytes);
                }
                else if (
                    url.StartsWith(VpxBackglassScheme, StringComparison.Ordinal)
                    && (ClearShare(img) > 0.05f || HoleInMiddle(img))
                )
                {
                    // A table file's backglass with a see-through window (X-Men's, for a video behind it): only part
                    // of a picture, so the catalog's instead.
                    img = null;
                }
                else
                {
                    img = TrimGrille(url, img);
                    // A blade drawn standing up (Attack from Mars', The Lord of the Rings'): turned to lie along the wall.
                    if (
                        (
                            url.StartsWith(VpxBladeLeftScheme, StringComparison.Ordinal)
                            || url.StartsWith(VpxBladeRightScheme, StringComparison.Ordinal)
                        )
                        && img.GetHeight() > img.GetWidth() * 2
                    )
                    {
                        if (img.IsCompressed())
                        {
                            img.Decompress();
                        }
                        img.Rotate90(ClockDirection.Counterclockwise);
                    }
                }

                // The latest fallback asked for: one that arrived while this was on its way (the catalog loaded) counts.
                var fb = string.IsNullOrEmpty(fallback) ? _triedFallback.GetValueOrDefault(url) : fallback;
                _usedFallback[url] = fb ?? "";
                if (img == null && !string.IsNullOrEmpty(fb))
                {
                    FellBack[url] = true; // what arrives under url is the fallback's picture
                    bytes = await FetchAsync(fb);
                    LobbyWork.Closing.ThrowIfCancellationRequested();
                    img = Decode(bytes ?? []);
                    if (img == null)
                    {
                        DropIfCorrupt(fb, bytes);
                    }
                }

                if (img == null || LobbyWork.Closing.IsCancellationRequested)
                {
                    _decoded.Enqueue((url, null, default));
                    return;
                }

                if (img.IsCompressed())
                {
                    img.Decompress();
                }

                Shrink(img, maxSide);
                if (!keepAlpha)
                {
                    FillHoles(img);
                }

                var avg = Average(img);
                img.GenerateMipmaps();
                _decoded.Enqueue((url, img, avg)); // the GPU upload happens on the main thread (_Process)
            }
            catch
            {
                _decoded.Enqueue((url, null, default)); // no art for now: the cabinet keeps its plain look
            }
        });
    }

    /// <summary>
    /// A .directb2s backglass whose picture runs on into the speaker grille below the glass (its GrillHeight): the
    /// picture is kept whole, and the grille's share of its height is noted (<see cref="GrilleShare"/>). The generic
    /// backbox shows all of it (speakers included: it has none of its own); a table's own backbox, which has its own
    /// speaker panel, shows only the glass above it (Cabinet.FitBackglass crops it with the material's UVs).
    /// </summary>
    static Image TrimGrille(string url, Image img)
    {
        if (
            !url.StartsWith(VrTableManager.Lobby.LobbyArt.B2sScheme, StringComparison.Ordinal)
            || DirectB2s.CachedBackglass(
                url[VrTableManager.Lobby.LobbyArt.B2sScheme.Length..],
                ExtractDir
            )
                is not { } b2s
        )
        {
            return img;
        }

        var h = img.GetHeight();
        var trim = DirectB2s.TrimFor(h, b2s.GrillHeight);
        if (trim > 0)
        {
            GrilleShares[url] = (float)trim / h;
        }

        return img;
    }

    /// <summary>Share of a picture (sampled on a 64 × 64 grid) that is more than half see-through.</summary>
    internal static float ClearShare(Image img)
    {
        if (img.IsCompressed())
        {
            img.Decompress();
        }

        if (img.DetectAlpha() == Image.AlphaMode.None)
        {
            return 0;
        }

        int w = img.GetWidth(),
            h = img.GetHeight(),
            clear = 0;
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                if (img.GetPixel(Math.Min(w - 1, x * w / 64), Math.Min(h - 1, y * h / 64)).A < 0.5f)
                {
                    clear++;
                }
            }
        }

        return clear / 4096f;
    }

    /// <summary>
    /// A picture whose middle (a quarter of its width, half its height) is one block of black: a backglass frame left
    /// open for a video or display behind it (X-Men's), not a whole picture.
    /// </summary>
    internal static bool HoleInMiddle(Image img)
    {
        int w = img.GetWidth(),
            h = img.GetHeight(),
            black = 0;
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var c = img.GetPixel(
                    Math.Min(w - 1, w * 38 / 100 + x * w * 24 / 1600),
                    Math.Min(h - 1, h / 4 + y * h / 32)
                );
                if (c.A < 0.5f || c.R + c.G + c.B < 0.06f)
                {
                    black++;
                }
            }
        }

        return black > 256 * 0.9f;
    }

    /// <summary>Art key for the table's own backglass picture inside its .vpx (VpxImages.PickBackglass).</summary>
    public const string VpxBackglassScheme = "vpxbg:";

    /// <summary>Art key for the table's own back panel picture inside its .vpx (VpxImages.PickBackPanel).</summary>
    public const string VpxBackPanelScheme = "vpxbp:";

    /// <summary>Art keys for the table's own inner side wall pictures, left and right (VpxImages.PickBlade).</summary>
    public const string VpxBladeLeftScheme = "vpxbl:",
        VpxBladeRightScheme = "vpxbr:";

    /// <summary>Requests whose picture came from their fallback (the catalog's) instead.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> FellBack = new(
        StringComparer.Ordinal
    );

    /// <summary>
    /// Whether a loaded backglass picture is just the glass (a .directb2s's, the table file's own, or one you chose)
    /// rather than the catalog's, which is often a photo of the whole backbox front with its speakers and display.
    /// A backbox with a speaker panel of its own only takes glass (Cabinet.ShowReal).
    /// </summary>
    public static bool IsGlassOnly(string? url) =>
        url != null
        && !FellBack.ContainsKey(url)
        && (
            url.StartsWith(VrTableManager.Lobby.LobbyArt.B2sScheme, StringComparison.Ordinal)
            || url.StartsWith(VpxBackglassScheme, StringComparison.Ordinal)
            || UserArtwork.PathOf(url) != null
        );

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> GrilleShares =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Share (0–1) of a loaded backglass picture's height, at the bottom, that is its speaker grille rather than glass
    /// (a .directb2s's GrillHeight); 0 for anything else.
    /// </summary>
    public static float GrilleShare(string? url) =>
        url != null && GrilleShares.TryGetValue(url, out var s) ? s : 0f;

    /// <summary>Encoded picture bytes: from the table file, the backglass file, or the web picture via the manager's cache.</summary>
    async Task<byte[]?> FetchAsync(string url)
    {
        try
        {
            LobbyWork.Closing.ThrowIfCancellationRequested(); // closing: not even a table file's playfield
            if (UserArtwork.PathOf(url) is { } mine)
            {
                return await File.ReadAllBytesAsync(mine); // a picture you chose
            }

            if (url.StartsWith(VrTableManager.Lobby.LobbyArt.VpxScheme, StringComparison.Ordinal))
            {
                // Extracted once per table version into the lobby's pictures folder, then read from there.
                var extracted = VpxImages.CachedPlayfield(
                    url[VrTableManager.Lobby.LobbyArt.VpxScheme.Length..],
                    ExtractDir
                );
                return extracted == null ? null : await File.ReadAllBytesAsync(extracted);
            }

            if (
                url.StartsWith(VpxBladeLeftScheme, StringComparison.Ordinal)
                || url.StartsWith(VpxBladeRightScheme, StringComparison.Ordinal)
            )
            {
                // The table's own inner side wall art (VpxImages.PickBlade), extracted once per table version.
                var left = url.StartsWith(VpxBladeLeftScheme, StringComparison.Ordinal);
                var vpx = url[(left ? VpxBladeLeftScheme : VpxBladeRightScheme).Length..];
                var blade = left
                    ? VpxImages.CachedBladeLeft(vpx, ExtractDir)
                    : VpxImages.CachedBladeRight(vpx, ExtractDir);
                return blade == null ? null : await File.ReadAllBytesAsync(blade);
            }

            if (url.StartsWith(VpxBackPanelScheme, StringComparison.Ordinal))
            {
                // The table's own back panel picture (VpxImages.PickBackPanel), extracted once per table version.
                var panel = VpxImages.CachedBackPanel(url[VpxBackPanelScheme.Length..], ExtractDir);
                return panel == null ? null : await File.ReadAllBytesAsync(panel);
            }

            if (url.StartsWith(VpxBackglassScheme, StringComparison.Ordinal))
            {
                // The table's own backglass picture (VpxImages.PickBackglass), extracted once per table version.
                var own = VpxImages.CachedBackglass(url[VpxBackglassScheme.Length..], ExtractDir);
                return own == null ? null : await File.ReadAllBytesAsync(own);
            }

            if (url.StartsWith(VrTableManager.Lobby.LobbyArt.B2sScheme, StringComparison.Ordinal))
            {
                // The backglass inside the table's .directb2s, extracted once per version of that file.
                var b2s = DirectB2s.CachedBackglass(
                    url[VrTableManager.Lobby.LobbyArt.B2sScheme.Length..],
                    ExtractDir
                );
                return b2s == null ? null : await File.ReadAllBytesAsync(b2s.Value.File);
            }
            var file = CachePath(url);
            if (!File.Exists(file))
            {
                await _throttle.WaitAsync(LobbyWork.Closing);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                        LobbyWork.Closing
                    );
                    timeout.CancelAfter(FetchTimeout);
                    var bytes = await Http.Client.GetByteArrayAsync(url, timeout.Token);
                    // Written whole, then moved into place, as the manager's ImageLoader does (they share the
                    // cache): a download cut short (the lobby closing for a table) never sits there as the picture.
                    var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        await File.WriteAllBytesAsync(tmp, bytes);
                        File.Move(tmp, file, overwrite: true);
                    }
                    catch
                    {
                        try
                        {
                            File.Delete(tmp);
                        }
                        catch { }
                        if (!File.Exists(file))
                        {
                            return bytes; // couldn't keep it: use it this once
                        }
                    }
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

    /// <summary>
    /// A downloaded picture that's truly broken (cut short by an older lobby, or not a picture at all) is deleted, so
    /// the retry downloads it again instead of reading the same bad file. Only by what was read: nothing read (the
    /// file was busy) leaves it be, and so does a whole picture this loader can't decode (a GIF or BMP the manager
    /// shows). Table files and your own pictures are never touched.
    /// </summary>
    static void DropIfCorrupt(string url, byte[]? bytes)
    {
        if (
            bytes == null
            || !IsCorrupt(bytes)
            || UserArtwork.PathOf(url) != null
            || url.StartsWith(VrTableManager.Lobby.LobbyArt.VpxScheme, StringComparison.Ordinal)
            || url.StartsWith(VrTableManager.Lobby.LobbyArt.B2sScheme, StringComparison.Ordinal)
            || url.StartsWith(VpxBackglassScheme, StringComparison.Ordinal)
            || url.StartsWith(VpxBackPanelScheme, StringComparison.Ordinal)
            || url.StartsWith(VpxBladeLeftScheme, StringComparison.Ordinal)
            || url.StartsWith(VpxBladeRightScheme, StringComparison.Ordinal)
        )
        {
            return;
        }

        try
        {
            File.Delete(CachePath(url));
        }
        catch { }
    }

    /// <summary>No picture signature at all (PNG, JPEG, WebP, GIF, BMP), or a PNG / JPEG / WebP missing its end.</summary>
    internal static bool IsCorrupt(byte[] b)
    {
        bool Starts(params byte[] sig) =>
            b.Length >= sig.Length && b.AsSpan(0, sig.Length).SequenceEqual(sig);
        if (Starts(0x89, 0x50, 0x4E, 0x47))
        {
            // Ends with the IEND chunk: "IEND" then its 4-byte CRC.
            return b.Length < 12 || !b.AsSpan(b.Length - 8, 4).SequenceEqual("IEND"u8);
        }

        if (Starts(0xFF, 0xD8))
        {
            // The end-of-image marker, allowing for a little padding after it.
            var tail = b.AsSpan(Math.Max(0, b.Length - 32));
            return tail.IndexOf(new byte[] { 0xFF, 0xD9 }) < 0;
        }

        if (b.Length >= 12 && Starts("RIFF"u8.ToArray()) && b.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            // The RIFF header says how long the file is.
            return b.Length < 8L + BitConverter.ToUInt32(b, 4);
        }

        return !(Starts("GIF8"u8.ToArray()) || Starts((byte)'B', (byte)'M'));
    }

    public override void _Process(double delta)
    {
        if (!_decoded.TryDequeue(out var d))
        {
            return;
        }

        if (d.Img == null)
        {
            Failed(d.Url);
        }
        else
        {
            Finish(d.Url, d.Img, d.Avg);
        }
    }

    /// <summary>Nothing usable this time: forgotten, so a later request tries again once its wait is over.</summary>
    void Failed(string url)
    {
        if (_textures.TryGetValue(url, out var e) && e.Tex == null)
        {
            _textures.Remove(url);
        }

        // A fallback came in while it was on its way, too late for it: not a miss, it goes again with that one
        // (backglasses are the pictures that get a fallback; they take the usual size).
        if (
            _triedFallback.GetValueOrDefault(url) is { Length: > 0 } latest
            && _usedFallback.GetValueOrDefault(url) != latest
        )
        {
            Request(url, latest);
            return;
        }

        var misses = _failed.TryGetValue(url, out var f) ? f.Misses + 1 : 1;
        var wait = TimeSpan.FromMinutes(Math.Min(30, Math.Pow(2, misses - 1)));
        _failed[url] = (misses, DateTime.UtcNow + wait);
    }

    void Finish(string url, Image img, Color avg)
    {
        _failed.Remove(url);
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
            {
                _textures.Remove(old);
            }
        }
    }

    /// <summary>Scales down so the longest side is at most <paramref name="maxSide"/>.</summary>
    public static void Shrink(Image img, int maxSide)
    {
        var (w, h) = (img.GetWidth(), img.GetHeight());
        var longest = Math.Max(w, h);
        if (longest <= maxSide)
        {
            return;
        }

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
        {
            return;
        }

        if (img.GetFormat() != Image.Format.Rgba8)
        {
            img.Convert(Image.Format.Rgba8);
        }

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
            {
                for (var x = 0; x < pw; x++)
                {
                    int s = (y * pw + x) * 4,
                        d = (y / 2 * nw + x / 2) * 4;
                    for (var k = 0; k < 4; k++)
                    {
                        np[d + k] += pp[s + k];
                    }
                }
            }

            for (var i = 0; i < nw * nh; i++)
            {
                if (np[i * 4 + 3] > 1)
                {
                    var f = 1 / np[i * 4 + 3];
                    for (var k = 0; k < 4; k++)
                    {
                        np[i * 4 + k] *= f;
                    }
                } // keep weight ≤ 1
            }

            levels.Add((nw, nh, np));
        }
        // Push: from coarse to fine, a pixel short of full weight takes the rest from its parent.
        for (var l = levels.Count - 2; l >= 0; l--)
        {
            var (cw, ch, cp) = levels[l];
            var (uw, _, up) = levels[l + 1];
            for (var y = 0; y < ch; y++)
            {
                for (var x = 0; x < cw; x++)
                {
                    int i = (y * cw + x) * 4,
                        u = (y / 2 * uw + x / 2) * 4;
                    var missing = 1 - cp[i + 3];
                    if (missing <= 0)
                    {
                        continue;
                    }

                    var uw8 = up[u + 3] > 0 ? 1 / up[u + 3] : 0;
                    for (var k = 0; k < 3; k++)
                    {
                        cp[i + k] += up[u + k] * uw8 * missing;
                    }

                    cp[i + 3] = 1;
                }
            }
        }
        var out0 = levels[0].P;
        for (var i = 0; i < w * h; i++)
        {
            var a = Math.Max(out0[i * 4 + 3], 1e-6f);
            for (var k = 0; k < 3; k++)
            {
                data[i * 4 + k] = (byte)Math.Clamp(out0[i * 4 + k] / a, 0, 255);
            }

            data[i * 4 + 3] = 255;
        }
        img.SetData(w, h, false, Image.Format.Rgba8, data);
    }

    /// <summary>Average colour, from a tiny copy (cheap and good enough to tint a cabinet).</summary>
    public static Color Average(Image img)
    {
        var small = (Image)img.Duplicate();
        if (small.IsCompressed())
        {
            small.Decompress();
        }

        small.Resize(4, 4, Image.Interpolation.Bilinear);
        float r = 0,
            g = 0,
            b = 0;
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var c = small.GetPixel(x, y);
                r += c.R;
                g += c.G;
                b += c.B;
            }
        }

        return new Color(r / 16, g / 16, b / 16);
    }

    /// <summary>Detects PNG / JPEG / WebP from the header bytes (cache files have no extension).</summary>
    public static Image? Decode(byte[] bytes)
    {
        if (bytes.Length < 12)
        {
            return null;
        }

        var img = new Image();
        var err = Error.FileUnrecognized;
        if (bytes[0] == 0x89 && bytes[1] == 0x50)
        {
            err = img.LoadPngFromBuffer(bytes);
        }
        else if (bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            err = img.LoadJpgFromBuffer(bytes);
        }
        else if (Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP")
        {
            err = img.LoadWebpFromBuffer(bytes);
        }

        return err == Error.Ok ? img : null;
    }
}
