using Godot;

namespace VrLobby;

/// <summary>
/// GPU block compression (DXT1 for opaque pictures, DXT5 with alpha) in plain C#. Godot's own compressor is part of
/// the editor only, so an exported lobby (a release) can't use <c>Image.Compress</c>; this keeps its cabinets at the
/// same ~4-8x less video memory. Each 4×4 block takes its colour endpoints from the block's principal axis, then
/// one least-squares refit, keeping whichever is closer. A little below the editor's encoder in quality, which
/// suits cabinet art seen from a metre or more away.
/// </summary>
static class DxtEncoder
{
    /// <summary>True when Godot can compress at runtime (the editor running the project), so it's used instead.</summary>
    public static bool GodotCan => !OS.HasFeature("template");

    /// <summary>
    /// <paramref name="src"/> (any uncompressed format, with or without mipmaps) as DXT5 when
    /// <paramref name="alpha"/>, else DXT1, mipmaps kept. Thread-safe; <paramref name="src"/> isn't changed.
    /// </summary>
    public static Image Compress(Image src, bool alpha)
    {
        var img = src;
        if (img.GetFormat() != Image.Format.Rgba8)
        {
            img = (Image)src.Duplicate();
            img.Convert(Image.Format.Rgba8);
        }

        int w = img.GetWidth(),
            h = img.GetHeight();
        var mips = img.HasMipmaps();
        var format = alpha ? Image.Format.Dxt5 : Image.Format.Dxt1;
        var blockBytes = alpha ? 16 : 8;
        // Godot's layout for this format (its mipmap sizes and offsets), filled in below.
        var target = Image.CreateEmpty(w, h, mips, format);
        var output = target.GetData();
        var input = img.GetData();
        var srcLevels = img.GetMipmapCount();

        for (var level = 0; level <= target.GetMipmapCount(); level++)
        {
            // Fewer DXT levels than RGBA ones (DXT stops at 4×4), but never more: clamp just in case.
            var s = Math.Min(level, srcLevels);
            int sw = Math.Max(1, w >> s),
                sh = Math.Max(1, h >> s);
            var srcOff = s == 0 ? 0 : (int)img.GetMipmapOffset(s);
            var dstOff = level == 0 ? 0 : (int)target.GetMipmapOffset(level);
            int lw = Math.Max(1, w >> level),
                lh = Math.Max(1, h >> level);
            int bw = (lw + 3) / 4,
                bh = (lh + 3) / 4;

            Parallel.For(
                0,
                bh,
                by =>
                {
                    Span<byte> px = stackalloc byte[64];
                    for (var bx = 0; bx < bw; bx++)
                    {
                        // The block's 16 pixels; past the picture's edge, its last row or column again.
                        for (var y = 0; y < 4; y++)
                        {
                            var yy = Math.Min(by * 4 + y, sh - 1);
                            for (var x = 0; x < 4; x++)
                            {
                                var xx = Math.Min(bx * 4 + x, sw - 1);
                                input
                                    .AsSpan(srcOff + (yy * sw + xx) * 4, 4)
                                    .CopyTo(px[(((y * 4) + x) * 4)..]);
                            }
                        }

                        var dst = output.AsSpan(dstOff + ((by * bw) + bx) * blockBytes, blockBytes);
                        if (alpha)
                        {
                            EncodeAlpha(px, dst[..8]);
                            EncodeColor(px, dst[8..]);
                        }
                        else
                        {
                            EncodeColor(px, dst);
                        }
                    }
                }
            );
        }

        return Image.CreateFromData(w, h, mips, format, output);
    }

    // ---- Colour (the DXT1 block, also the second half of a DXT5 one) ----

    static readonly float[] WeightA = [1f, 0f, 2f / 3f, 1f / 3f];
    static readonly float[] WeightB = [0f, 1f, 1f / 3f, 2f / 3f];

    /// <summary>One 4×4 block (16 RGBA pixels) as 8 bytes: two RGB565 endpoints, then 2-bit indices.</summary>
    internal static void EncodeColor(ReadOnlySpan<byte> px, Span<byte> dst)
    {
        Span<float> p = stackalloc float[48];
        float mr = 0,
            mg = 0,
            mb = 0;
        for (var i = 0; i < 16; i++)
        {
            p[i * 3] = px[i * 4];
            p[i * 3 + 1] = px[i * 4 + 1];
            p[i * 3 + 2] = px[i * 4 + 2];
            mr += p[i * 3];
            mg += p[i * 3 + 1];
            mb += p[i * 3 + 2];
        }
        mr /= 16;
        mg /= 16;
        mb /= 16;

        // The direction the block's colours spread along most (power iteration on their covariance).
        float crr = 0,
            crg = 0,
            crb = 0,
            cgg = 0,
            cgb = 0,
            cbb = 0;
        for (var i = 0; i < 16; i++)
        {
            float r = p[i * 3] - mr,
                g = p[i * 3 + 1] - mg,
                b = p[i * 3 + 2] - mb;
            crr += r * r;
            crg += r * g;
            crb += r * b;
            cgg += g * g;
            cgb += g * b;
            cbb += b * b;
        }

        Span<ushort> best = stackalloc ushort[2];
        Span<byte> bestIdx = stackalloc byte[16];
        Span<byte> idx = stackalloc byte[16];
        if (crr + cgg + cbb < 1e-3f)
        {
            // One colour: both endpoints on it.
            var c = To565(mr, mg, mb);
            Write(dst, c, c, idx[..16]);
            return;
        }

        // Start from the covariance row with the largest diagonal, so the start isn't at right angles to the axis.
        float vr,
            vg,
            vb;
        if (crr >= cgg && crr >= cbb)
        {
            (vr, vg, vb) = (crr, crg, crb);
        }
        else if (cgg >= cbb)
        {
            (vr, vg, vb) = (crg, cgg, cgb);
        }
        else
        {
            (vr, vg, vb) = (crb, cgb, cbb);
        }
        for (var it = 0; it < 8; it++)
        {
            var nr = crr * vr + crg * vg + crb * vb;
            var ng = crg * vr + cgg * vg + cgb * vb;
            var nb = crb * vr + cgb * vg + cbb * vb;
            var len = MathF.Sqrt(nr * nr + ng * ng + nb * nb);
            if (len < 1e-6f)
            {
                break;
            }
            (vr, vg, vb) = (nr / len, ng / len, nb / len);
        }

        // Endpoints: the block's two outermost pixels along that axis, pulled in a little (as most encoders do,
        // since the ends are rarely hit exactly).
        int lo = 0,
            hi = 0;
        float tLo = float.MaxValue,
            tHi = float.MinValue;
        for (var i = 0; i < 16; i++)
        {
            var t = (p[i * 3] - mr) * vr + (p[i * 3 + 1] - mg) * vg + (p[i * 3 + 2] - mb) * vb;
            if (t < tLo)
            {
                (tLo, lo) = (t, i);
            }
            if (t > tHi)
            {
                (tHi, hi) = (t, i);
            }
        }
        float ar = p[hi * 3],
            ag = p[hi * 3 + 1],
            ab = p[hi * 3 + 2],
            br = p[lo * 3],
            bg = p[lo * 3 + 1],
            bb = p[lo * 3 + 2];
        float ir = (ar - br) / 16,
            ig = (ag - bg) / 16,
            ib = (ab - bb) / 16;
        var bestErr = Evaluate(
            p,
            To565(ar - ir, ag - ig, ab - ib),
            To565(br + ir, bg + ig, bb + ib),
            best,
            bestIdx
        );

        // One least-squares refit of both endpoints to the indices just picked.
        float saa = 0,
            sab = 0,
            sbb = 0,
            sar = 0,
            sag = 0,
            sab2 = 0,
            sbr = 0,
            sbg = 0,
            sbb2 = 0;
        for (var i = 0; i < 16; i++)
        {
            float a = WeightA[bestIdx[i]],
                b = WeightB[bestIdx[i]];
            saa += a * a;
            sab += a * b;
            sbb += b * b;
            sar += a * p[i * 3];
            sag += a * p[i * 3 + 1];
            sab2 += a * p[i * 3 + 2];
            sbr += b * p[i * 3];
            sbg += b * p[i * 3 + 1];
            sbb2 += b * p[i * 3 + 2];
        }
        var det = saa * sbb - sab * sab;
        if (MathF.Abs(det) > 1e-6f)
        {
            Span<ushort> refit = stackalloc ushort[2];
            var err = Evaluate(
                p,
                To565(
                    (sbb * sar - sab * sbr) / det,
                    (sbb * sag - sab * sbg) / det,
                    (sbb * sab2 - sab * sbb2) / det
                ),
                To565(
                    (saa * sbr - sab * sar) / det,
                    (saa * sbg - sab * sag) / det,
                    (saa * sbb2 - sab * sab2) / det
                ),
                refit,
                idx
            );
            if (err < bestErr)
            {
                refit.CopyTo(best);
                idx.CopyTo(bestIdx);
            }
        }

        Write(dst, best[0], best[1], bestIdx);
    }

    /// <summary>
    /// Orders two endpoints for 4-colour mode (first larger), picks each pixel's nearest of the four colours, and
    /// returns the total squared error. Equal endpoints: every index 0.
    /// </summary>
    static float Evaluate(
        ReadOnlySpan<float> p,
        ushort c0,
        ushort c1,
        Span<ushort> ends,
        Span<byte> idx
    )
    {
        if (c0 < c1)
        {
            (c0, c1) = (c1, c0);
        }
        ends[0] = c0;
        ends[1] = c1;

        Span<float> pal = stackalloc float[12];
        Expand(c0, pal[..3]);
        Expand(c1, pal[3..6]);
        for (var ch = 0; ch < 3; ch++)
        {
            pal[6 + ch] = (2 * pal[ch] + pal[3 + ch]) / 3;
            pal[9 + ch] = (pal[ch] + 2 * pal[3 + ch]) / 3;
        }

        float total = 0;
        for (var i = 0; i < 16; i++)
        {
            var bestD = float.MaxValue;
            byte bestK = 0;
            var colours = c0 == c1 ? 1 : 4;
            for (var k = 0; k < colours; k++)
            {
                float dr = p[i * 3] - pal[k * 3],
                    dg = p[i * 3 + 1] - pal[k * 3 + 1],
                    db = p[i * 3 + 2] - pal[k * 3 + 2];
                var d = dr * dr + dg * dg + db * db;
                if (d < bestD)
                {
                    (bestD, bestK) = (d, (byte)k);
                }
            }
            idx[i] = bestK;
            total += bestD;
        }
        return total;
    }

    static void Write(Span<byte> dst, ushort c0, ushort c1, ReadOnlySpan<byte> idx)
    {
        dst[0] = (byte)c0;
        dst[1] = (byte)(c0 >> 8);
        dst[2] = (byte)c1;
        dst[3] = (byte)(c1 >> 8);
        for (var row = 0; row < 4; row++)
        {
            dst[4 + row] = (byte)(
                idx[row * 4]
                | (idx[row * 4 + 1] << 2)
                | (idx[row * 4 + 2] << 4)
                | (idx[row * 4 + 3] << 6)
            );
        }
    }

    static ushort To565(float r, float g, float b)
    {
        int R = (int)MathF.Round(Math.Clamp(r, 0, 255) * 31 / 255),
            G = (int)MathF.Round(Math.Clamp(g, 0, 255) * 63 / 255),
            B = (int)MathF.Round(Math.Clamp(b, 0, 255) * 31 / 255);
        return (ushort)((R << 11) | (G << 5) | B);
    }

    /// <summary>An RGB565 endpoint as the 0-255 colour the GPU decodes it to.</summary>
    static void Expand(ushort c, Span<float> rgb)
    {
        int r = (c >> 11) & 31,
            g = (c >> 5) & 63,
            b = c & 31;
        rgb[0] = (r << 3) | (r >> 2);
        rgb[1] = (g << 2) | (g >> 4);
        rgb[2] = (b << 3) | (b >> 2);
    }

    // ---- Alpha (the first half of a DXT5 block) ----

    /// <summary>One 4×4 block's alpha as 8 bytes: the largest and smallest alpha, then a 3-bit index per pixel.</summary>
    internal static void EncodeAlpha(ReadOnlySpan<byte> px, Span<byte> dst)
    {
        byte max = 0,
            min = 255;
        for (var i = 0; i < 16; i++)
        {
            var a = px[i * 4 + 3];
            max = Math.Max(max, a);
            min = Math.Min(min, a);
        }
        dst[0] = max;
        dst[1] = min;
        ulong bits = 0;
        if (max > min)
        {
            // 8-value mode: max, min, then six steps from max to min.
            Span<int> pal = stackalloc int[8];
            pal[0] = max;
            pal[1] = min;
            for (var k = 1; k <= 6; k++)
            {
                pal[k + 1] = ((7 - k) * max + k * min + 3) / 7;
            }
            for (var i = 0; i < 16; i++)
            {
                var a = px[i * 4 + 3];
                int bestK = 0,
                    bestD = int.MaxValue;
                for (var k = 0; k < 8; k++)
                {
                    var d = Math.Abs(a - pal[k]);
                    if (d < bestD)
                    {
                        (bestD, bestK) = (d, k);
                    }
                }
                bits |= (ulong)bestK << (3 * i);
            }
        }
        for (var b = 0; b < 6; b++)
        {
            dst[2 + b] = (byte)(bits >> (8 * b));
        }
    }
}
