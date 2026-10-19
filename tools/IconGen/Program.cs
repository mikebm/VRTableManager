using SkiaSharp;

// VR Table Manager icon: a chrome pinball inside a neon ring (cyan, with a magenta arc),
// on the app's dark navy tile. Drawn as vectors at every size so small icons stay crisp.
//
// Usage: dotnet run --project tools/IconGen [-- <output folder>]   (default: VrTableManager/Assets)

var preview = args.FirstOrDefault(a => a.StartsWith("--preview="))?["--preview=".Length..];
var outDir = args.FirstOrDefault(a => !a.StartsWith("--")) ?? FindAssets();
Directory.CreateDirectory(outDir);

int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var pngs = icoSizes.ToDictionary(s => s, Render);
if (preview != null) WritePreview(preview, pngs);
WriteIco(Path.Combine(outDir, "app.ico"), pngs);
File.WriteAllBytes(Path.Combine(outDir, "app-icon.png"), Render(512));
Console.WriteLine($"Wrote app.ico ({string.Join(", ", icoSizes)} px) and app-icon.png (512 px) to {outDir}");

static string FindAssets()
{
    var dir = AppContext.BaseDirectory;
    while (dir != null && !Directory.Exists(Path.Combine(dir, "VrTableManager", "Assets"))) dir = Path.GetDirectoryName(dir);
    return dir != null ? Path.Combine(dir, "VrTableManager", "Assets") : Directory.GetCurrentDirectory();
}

static byte[] Render(int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    c.Scale(size / 512f);

    var small = size < 48;          // tiny icons: no blur, bolder shapes
    var cyan = SKColor.Parse("#4DE3FF");
    var magenta = SKColor.Parse("#F0287A");
    var center = new SKPoint(256, 256);

    // ---- Tile ----
    var tile = new SKRoundRect(new SKRect(small ? 8 : 18, small ? 8 : 18, small ? 504 : 494, small ? 504 : 494), small ? 96 : 110);
    using (var bg = new SKPaint { IsAntialias = true })
    {
        bg.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(512, 512),
            [SKColor.Parse("#18223D"), SKColor.Parse("#0B1020"), SKColor.Parse("#1A0C22")], [0f, 0.6f, 1f], SKShaderTileMode.Clamp);
        c.DrawRoundRect(tile, bg);
    }
    if (!small)
    {
        // Soft neon bloom behind the ring.
        using var bloom = new SKPaint { IsAntialias = true };
        bloom.Shader = SKShader.CreateRadialGradient(center, 230,
            [cyan.WithAlpha(70), cyan.WithAlpha(18), SKColors.Transparent], [0.45f, 0.75f, 1f], SKShaderTileMode.Clamp);
        c.Save();
        c.ClipRoundRect(tile, antialias: true);
        c.DrawCircle(center, 230, bloom);
        c.Restore();

        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5 };
        edge.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(512, 512),
            [cyan.WithAlpha(150), SKColor.Parse("#2C3956").WithAlpha(120), magenta.WithAlpha(150)], [0f, 0.5f, 1f], SKShaderTileMode.Clamp);
        c.DrawRoundRect(tile, edge);
    }

    // ---- Neon ring: cyan, with a magenta arc on the lower left ----
    const float ringR = 172;
    var ringRect = new SKRect(center.X - ringR, center.Y - ringR, center.X + ringR, center.Y + ringR);
    var ringW = small ? 34f : 20f;
    void Arc(float start, float sweep, SKColor color)
    {
        if (!small)
        {
            using var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = ringW * 2.2f, Color = color.WithAlpha(150), StrokeCap = SKStrokeCap.Round };
            glow.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 16);
            c.DrawArc(ringRect, start, sweep, false, glow);
        }
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = ringW, Color = color, StrokeCap = SKStrokeCap.Round };
        c.DrawArc(ringRect, start, sweep, false, line);
        if (!small)
        {
            // Hot white core, like a neon tube.
            using var core = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = ringW * 0.32f, Color = SKColors.White.WithAlpha(200), StrokeCap = SKStrokeCap.Round };
            c.DrawArc(ringRect, start, sweep, false, core);
        }
    }
    var gap = small ? 0f : 14f;     // small gaps between the colours read as separate tubes
    Arc(-60 + gap / 2, 220 - gap, cyan);   // top, right and bottom
    Arc(160 + gap / 2, 140 - gap, magenta); // lower left round to upper left

    // ---- Chrome pinball ----
    const float ballR = 112;
    if (!small)
    {
        using var shadow = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(150) };
        shadow.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14);
        c.DrawCircle(center.X + 8, center.Y + 14, ballR, shadow);
    }
    using (var ball = new SKPaint { IsAntialias = true })
    {
        ball.Shader = SKShader.CreateRadialGradient(new SKPoint(center.X - 40, center.Y - 46), ballR * 1.55f,
            [SKColors.White, SKColor.Parse("#DCE4F0"), SKColor.Parse("#8A96AE"), SKColor.Parse("#39435C"), SKColor.Parse("#141A2C")],
            [0f, 0.18f, 0.5f, 0.78f, 1f], SKShaderTileMode.Clamp);
        c.DrawCircle(center, ballR, ball);
    }
    if (!small)
    {
        // Soft reflection of the cyan ring along the ball's lower-right edge (clipped to the ball).
        c.Save();
        using (var clip = new SKPath()) { clip.AddCircle(center.X, center.Y, ballR); c.ClipPath(clip, antialias: true); }
        using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 26, Color = cyan.WithAlpha(110) })
        {
            rim.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12);
            c.DrawArc(new SKRect(center.X - ballR, center.Y - ballR, center.X + ballR, center.Y + ballR), -10, 120, false, rim);
        }
        c.Restore();

        // Specular highlight.
        using var spec = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(235) };
        spec.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6);
        c.DrawOval(new SKRect(center.X - 74, center.Y - 82, center.X - 26, center.Y - 50), spec);
        using var spark = new SKPaint { IsAntialias = true, Color = SKColors.White };
        c.DrawCircle(center.X - 46, center.Y - 64, 9, spark);
    }
    else
    {
        using var spec = new SKPaint { IsAntialias = true, Color = SKColors.White };
        c.DrawCircle(center.X - 44, center.Y - 50, 26, spec);
    }

    using var img = surface.Snapshot();
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// Every size side by side at 1:1 on the app's background, then scaled 4x (to judge the small ones).
static void WritePreview(string path, Dictionary<int, byte[]> pngs)
{
    var w = pngs.Keys.Sum() + 12 * (pngs.Count + 1);
    var h = pngs.Keys.Max() + 24;
    using var surface = SKSurface.Create(new SKImageInfo(w * 2, h * 2));
    var c = surface.Canvas;
    c.Clear(SKColor.Parse("#202020"));
    c.Scale(2);
    var x = 12f;
    foreach (var (size, png) in pngs)
    {
        using var bmp = SKBitmap.Decode(png);
        c.DrawBitmap(bmp, x, 12, new SKPaint { FilterQuality = SKFilterQuality.None });
        x += size + 12;
    }
    using var img = surface.Snapshot();
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, data.ToArray());
}

// ICO with PNG-compressed images (supported since Windows Vista).
static void WriteIco(string path, Dictionary<int, byte[]> pngs)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((ushort)0);            // reserved
    w.Write((ushort)1);            // type: icon
    w.Write((ushort)pngs.Count);
    var offset = 6 + 16 * pngs.Count;
    foreach (var (size, png) in pngs)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);          // palette
        w.Write((byte)0);          // reserved
        w.Write((ushort)1);        // planes
        w.Write((ushort)32);       // bits per pixel
        w.Write(png.Length);
        w.Write(offset);
        offset += png.Length;
    }
    foreach (var png in pngs.Values) w.Write(png);
}
