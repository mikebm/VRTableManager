using SkiaSharp;

// VR Table Manager art, in the app's "DMD & lamps" style. Drawn as vectors at every size so
// small icons stay crisp.
//  - Icon: "VR" in orange dot-matrix plasma on a black tile with a chrome edge (Concepts.cs).
//  - dmd-dots.png: one cell of a dot-matrix display's dot grid, tiled behind DMD panels.
//
// Usage: dotnet run --project tools/IconGen [-- <output folder>] [--preview=<png>] [--concepts=<png>]
//        (default output: VrTableManager/Assets)

var preview = args.FirstOrDefault(a => a.StartsWith("--preview="))?["--preview=".Length..];
var outDir = args.FirstOrDefault(a => !a.StartsWith("--")) ?? FindAssets();
Directory.CreateDirectory(outDir);

// --concepts=<png>: draw every concept in Concepts.cs side by side (256/64/32/16 px) and stop.
var conceptsSheet = args.FirstOrDefault(a => a.StartsWith("--concepts="))?["--concepts=".Length..];
if (conceptsSheet != null)
{
    WriteConceptSheet(conceptsSheet);
    Console.WriteLine($"Wrote concept sheet to {conceptsSheet}");
    return;
}

int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var pngs = icoSizes.ToDictionary(s => s, Render);
if (preview != null) WritePreview(preview, pngs);
WriteIco(Path.Combine(outDir, "app.ico"), pngs);
File.WriteAllBytes(Path.Combine(outDir, "app-icon.png"), Render(512));
File.WriteAllBytes(Path.Combine(outDir, "dmd-dots.png"), DmdDot(6));
Console.WriteLine($"Wrote app.ico ({string.Join(", ", icoSizes)} px), app-icon.png (512 px) and dmd-dots.png to {outDir}");

static string FindAssets()
{
    var dir = AppContext.BaseDirectory;
    while (dir != null && !Directory.Exists(Path.Combine(dir, "VrTableManager", "Assets"))) dir = Path.GetDirectoryName(dir);
    return dir != null ? Path.Combine(dir, "VrTableManager", "Assets") : Directory.GetCurrentDirectory();
}

// One unlit DMD dot, centred in a cell. Tiled, it shows the display's grid through the glass.
static byte[] DmdDot(int cell)
{
    using var surface = SKSurface.Create(new SKImageInfo(cell, cell, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    using var dot = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#FF7A1F").WithAlpha(34) };
    c.DrawCircle(cell / 2f, cell / 2f, cell * 0.3f, dot);
    using var img = surface.Snapshot();
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// The app icon: concept 1 in Concepts.cs ("VR" in orange dot-matrix plasma, like a DMD).
static byte[] Render(int size) => RenderConcept(size, 0);

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

static byte[] RenderConcept(int size, int concept)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    c.Scale(size / 512f);
    Concepts.Draw(c, concept, size <= 64); // up to 64 px: no faint unlit dots or blur, which smear at that scale
    using var img = surface.Snapshot();
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

static void WriteConceptSheet(string path)
{
    int[] sizes = [256, 64, 32, 16];
    const int pad = 28, label = 44;
    var rowW = sizes.Sum() + pad * (sizes.Length + 1);
    var rowH = 256 + label + pad;
    var n = Concepts.Names.Length;
    using var surface = SKSurface.Create(new SKImageInfo(rowW * 2, rowH * ((n + 1) / 2) + pad));
    var c = surface.Canvas;
    c.Clear(SKColor.Parse("#2B2B2E"));
    using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 26);
    using var text = new SKPaint { IsAntialias = true, Color = SKColors.White };
    for (var i = 0; i < n; i++)
    {
        var ox = (i % 2) * rowW;
        var oy = (i / 2) * rowH + pad;
        c.DrawText($"{i + 1}. {Concepts.Names[i]}", ox + pad, oy + 28, font, text);
        var x = ox + pad;
        foreach (var s in sizes)
        {
            using var bmp = SKBitmap.Decode(RenderConcept(s, i));
            c.DrawBitmap(bmp, x, oy + label + (256 - s) / 2f);
            x += s + pad;
        }
    }
    using var img = surface.Snapshot();
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, data.ToArray());
}
