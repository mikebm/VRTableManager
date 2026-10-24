using SkiaSharp;

/// <summary>
/// Alternative app-icon designs, all on the same neutral black tile with a chrome edge.
/// Draw at 512 units; callers scale. <c>small</c> = icon under 48 px: no blur, bolder shapes.
/// </summary>
static class Concepts
{
    public static readonly string[] Names = ["Dot-matrix VR", "Flipper & ball", "Headset DMD", "Cabinet"];

    static readonly SKColor Dmd = SKColor.Parse("#FF7A1F");
    static readonly SKColor Amber = SKColor.Parse("#FFB23E");

    public static void Draw(SKCanvas c, int concept, bool small)
    {
        Tile(c, small);
        switch (concept)
        {
            case 0: DotMatrixVr(c, small); break;
            case 1: FlipperAndBall(c, small); break;
            case 2: HeadsetDmd(c, small); break;
            default: Cabinet(c, small); break;
        }
    }

    static SKRoundRect TileRect(bool small) =>
        new(new SKRect(small ? 8 : 18, small ? 8 : 18, small ? 504 : 494, small ? 504 : 494), small ? 92 : 104);

    static void Tile(SKCanvas c, bool small)
    {
        var tile = TileRect(small);
        using (var bg = new SKPaint { IsAntialias = true })
        {
            bg.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 512),
                [SKColor.Parse("#26262A"), SKColor.Parse("#111113"), SKColor.Parse("#070708")], [0f, 0.55f, 1f], SKShaderTileMode.Clamp);
            c.DrawRoundRect(tile, bg);
        }
        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = small ? 14 : 8 };
        edge.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 512),
            [SKColor.Parse("#F4F6F8"), SKColor.Parse("#8E969F"), SKColor.Parse("#D8DDE3"), SKColor.Parse("#5A6068")],
            [0f, 0.45f, 0.55f, 1f], SKShaderTileMode.Clamp);
        c.DrawRoundRect(tile, edge);
    }

    static void Glow(SKCanvas c, SKPoint at, float r, SKColor color, byte alpha, bool small)
    {
        if (small) return;
        using var g = new SKPaint { IsAntialias = true };
        g.Shader = SKShader.CreateRadialGradient(at, r, [color.WithAlpha(alpha), color.WithAlpha((byte)(alpha / 4)), SKColors.Transparent],
            [0f, 0.6f, 1f], SKShaderTileMode.Clamp);
        c.DrawCircle(at, r, g);
    }

    static void LitDot(SKCanvas c, float x, float y, float r, bool small)
    {
        if (!small)
        {
            using var halo = new SKPaint { IsAntialias = true, Color = Dmd.WithAlpha(120) };
            halo.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, r * 0.7f);
            c.DrawCircle(x, y, r * 1.15f, halo);
        }
        using var dot = new SKPaint { IsAntialias = true };
        dot.Shader = SKShader.CreateRadialGradient(new SKPoint(x - r * 0.25f, y - r * 0.25f), r * 1.2f,
            [SKColor.Parse("#FFE0B8"), Dmd, SKColor.Parse("#C24A08")], [0f, 0.45f, 1f], SKShaderTileMode.Clamp);
        c.DrawCircle(x, y, r, dot);
    }

    // ---- 1. "VR" in 5x7 dot-matrix characters ----
    static readonly string[] V = ["X...X", "X...X", "X...X", "X...X", ".X.X.", ".X.X.", "..X.."];
    static readonly string[] R = ["XXXX.", "X...X", "X...X", "XXXX.", "X.X..", "X..X.", "X...X"];

    static void DotMatrixVr(SKCanvas c, bool small)
    {
        const int cols = 11, rows = 7;
        var pitch = small ? 40f : 36f;
        var r = small ? 17f : 14f;
        var x0 = 256 - pitch * (cols - 1) / 2;
        var y0 = 256 - pitch * (rows - 1) / 2;
        using var unlit = new SKPaint { IsAntialias = true, Color = Dmd.WithAlpha(16) };
        for (var row = 0; row < rows; row++)
            for (var col = 0; col < cols; col++)
            {
                var lit = col < 5 ? V[row][col] == 'X' : col > 5 && R[row][col - 6] == 'X';
                var x = x0 + col * pitch;
                var y = y0 + row * pitch;
                if (lit) LitDot(c, x, y, r, small);
                else if (!small) c.DrawCircle(x, y, r * 0.8f, unlit);
            }
    }

    // ---- 2. A classic flipper (white bat, red rubber) and a chrome ball ----
    static void FlipperAndBall(SKCanvas c, bool small)
    {
        Glow(c, new SKPoint(330, 190), 190, Amber, 70, small);
        Ball(c, new SKPoint(332, 178), small ? 78 : 70, small);

        // Flipper: pivot circle bottom-left, tapering to a rounded tip, tilted up to the right.
        var pivot = new SKPoint(150, 360);
        float pr = small ? 58 : 52, tr = small ? 30 : 26, len = 250;
        var ang = -24 * Math.PI / 180;
        var tip = new SKPoint(pivot.X + (float)(Math.Cos(ang) * len), pivot.Y + (float)(Math.Sin(ang) * len));
        using var bat = FlipperPath(pivot, pr, tip, tr);
        using var rubber = FlipperPath(pivot, pr + (small ? 14 : 12), tip, tr + (small ? 14 : 12));
        if (!small)
        {
            using var shadow = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(160) };
            shadow.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14);
            c.Save(); c.Translate(8, 16); c.DrawPath(rubber, shadow); c.Restore();
        }
        using (var red = new SKPaint { IsAntialias = true })
        {
            red.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 250), new SKPoint(0, 440),
                [SKColor.Parse("#F06A5E"), SKColor.Parse("#C7261C"), SKColor.Parse("#8E1610")], [0f, 0.5f, 1f], SKShaderTileMode.Clamp);
            c.DrawPath(rubber, red);
        }
        using (var white = new SKPaint { IsAntialias = true })
        {
            white.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 250), new SKPoint(0, 420),
                [SKColors.White, SKColor.Parse("#E4E6EA"), SKColor.Parse("#A9AEB6")], [0f, 0.55f, 1f], SKShaderTileMode.Clamp);
            c.DrawPath(bat, white);
        }
        using var pin = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#5A6068") };
        c.DrawCircle(pivot, small ? 16 : 14, pin);
    }

    static SKPath FlipperPath(SKPoint a, float ra, SKPoint b, float rb)
    {
        // Convex hull of two circles: tangent lines plus the arcs around each end.
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        var d = (float)Math.Sqrt(dx * dx + dy * dy);
        var baseAng = Math.Atan2(dy, dx);
        var off = Math.Acos((ra - rb) / d);
        SKPoint P(SKPoint o, float r, double t) => new(o.X + (float)(Math.Cos(t) * r), o.Y + (float)(Math.Sin(t) * r));
        var path = new SKPath();
        path.MoveTo(P(a, ra, baseAng + off));
        path.LineTo(P(b, rb, baseAng + off));
        path.ArcTo(new SKRect(b.X - rb, b.Y - rb, b.X + rb, b.Y + rb), (float)((baseAng + off) * 180 / Math.PI), (float)(-2 * off * 180 / Math.PI), false);
        path.LineTo(P(a, ra, baseAng - off));
        path.ArcTo(new SKRect(a.X - ra, a.Y - ra, a.X + ra, a.Y + ra), (float)((baseAng - off) * 180 / Math.PI), (float)(-(2 * Math.PI - 2 * off) * 180 / Math.PI), false);
        path.Close();
        return path;
    }

    static void Ball(SKCanvas c, SKPoint at, float r, bool small)
    {
        if (!small)
        {
            using var shadow = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(150) };
            shadow.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12);
            c.DrawCircle(at.X + 8, at.Y + 12, r, shadow);
        }
        using (var chrome = new SKPaint { IsAntialias = true })
        {
            chrome.Shader = SKShader.CreateRadialGradient(new SKPoint(at.X - r * 0.37f, at.Y - r * 0.4f), r * 1.55f,
                [SKColors.White, SKColor.Parse("#E4E8EE"), SKColor.Parse("#8E97A6"), SKColor.Parse("#3B3F48"), SKColor.Parse("#15161A")],
                [0f, 0.18f, 0.5f, 0.78f, 1f], SKShaderTileMode.Clamp);
            c.DrawCircle(at, r, chrome);
        }
        using var spec = new SKPaint { IsAntialias = true, Color = SKColors.White };
        if (!small) spec.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4);
        c.DrawOval(new SKRect(at.X - r * 0.62f, at.Y - r * 0.7f, at.X - r * 0.22f, at.Y - r * 0.4f), spec);
    }

    // ---- 3. A VR headset whose visor is a DMD showing a ball ----
    static void HeadsetDmd(SKCanvas c, bool small)
    {
        // Strap
        using (var strap = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#2E2E33") })
            c.DrawRoundRect(new SKRect(40, 230, 472, 290), 30, 30, strap);
        // Visor body with chrome rim
        var visor = new SKRoundRect(new SKRect(86, 150, 426, 370), 90);
        using (var rim = new SKPaint { IsAntialias = true })
        {
            rim.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 150), new SKPoint(0, 370),
                [SKColor.Parse("#F4F6F8"), SKColor.Parse("#9AA2AB"), SKColor.Parse("#D8DDE3"), SKColor.Parse("#5A6068")],
                [0f, 0.45f, 0.55f, 1f], SKShaderTileMode.Clamp);
            c.DrawRoundRect(visor, rim);
        }
        var glass = new SKRoundRect(new SKRect(104, 168, 408, 352), 74);
        using (var g = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#120803") })
            c.DrawRoundRect(glass, g);
        // DMD dots inside the glass: a ball (filled circle of lit dots)
        c.Save();
        c.ClipRoundRect(glass, antialias: true);
        var pitch = small ? 34f : 22f;
        var r = small ? 13f : 7.5f;
        var centre = new SKPoint(256, 260);
        using var unlit = new SKPaint { IsAntialias = true, Color = Dmd.WithAlpha(40) };
        for (var y = 168 + pitch / 2; y < 352; y += pitch)
            for (var x = 104 + pitch / 2; x < 408; x += pitch)
            {
                var dx = x - centre.X; var dy = y - centre.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < (small ? 62 : 66)) LitDot(c, x, y, r, small);
                else if (!small) c.DrawCircle(x, y, r * 0.75f, unlit);
            }
        c.Restore();
    }

    // ---- 4. Pinball cabinet, side on, in lamp-amber lines, with a lit display ----
    static void Cabinet(SKCanvas c, bool small)
    {
        Glow(c, new SKPoint(330, 150), 170, Dmd, 70, small);
        var w = small ? 30f : 18f;
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w, Color = Amber, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        if (!small) line.ImageFilter = SKImageFilter.CreateDropShadow(0, 0, 6, 6, Amber.WithAlpha(150));
        // Body (sloped top: low at the front/left, high at the back/right)
        using var body = new SKPath();
        body.MoveTo(96, 300); body.LineTo(360, 262); body.LineTo(360, 330); body.LineTo(96, 352); body.Close();
        c.DrawPath(body, line);
        // Legs
        c.DrawLine(118, 352, 108, 440, line);
        c.DrawLine(338, 332, 348, 440, line);
        // Backbox
        using var box = new SKPath();
        box.AddRoundRect(new SKRoundRect(new SKRect(330, 90, 430, 262), 10));
        c.DrawPath(box, line);
        // Lit display in the backbox
        using var display = new SKPaint { IsAntialias = true };
        display.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 150), new SKPoint(0, 210),
            [SKColor.Parse("#FFC07A"), Dmd], [0f, 1f], SKShaderTileMode.Clamp);
        c.DrawRoundRect(new SKRect(352, 150, 408, 206), 6, 6, display);
    }
}
