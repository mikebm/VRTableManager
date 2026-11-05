namespace VrTableManager.Services;

/// <summary>
/// Decoder for QOI ("Quite OK Image", qoiformat.org), the format VPX's <c>-CaptureAttract</c> writes its
/// frames in. Small and dependency-free; the output is 8-bit RGBA, row by row from the top.
/// </summary>
public static class Qoi
{
    public sealed record Image(int Width, int Height, byte[] Rgba);

    /// <summary>Width and height from the 14-byte header, or null when it isn't a QOI file.</summary>
    public static (int Width, int Height)? ReadSize(ReadOnlySpan<byte> header)
    {
        if (header.Length < 14 || header[0] != 'q' || header[1] != 'o' || header[2] != 'i' || header[3] != 'f') return null;
        var w = (header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7];
        var h = (header[8] << 24) | (header[9] << 16) | (header[10] << 8) | header[11];
        return w > 0 && h > 0 && (long)w * h <= 400_000_000 ? (w, h) : null;
    }

    public static Image Decode(ReadOnlySpan<byte> data)
    {
        if (ReadSize(data) is not var (w, h)) throw new InvalidDataException("Not a QOI image.");
        var px = new byte[w * h * 4];
        Span<byte> index = stackalloc byte[64 * 4];
        byte r = 0, g = 0, b = 0, a = 255;
        var p = 14;
        var run = 0;
        var end = data.Length - 8; // 8-byte end marker
        for (var o = 0; o < px.Length; o += 4)
        {
            if (run > 0) run--;
            else if (p < end)
            {
                var b1 = data[p++];
                if (b1 == 0xFE) { r = data[p++]; g = data[p++]; b = data[p++]; }
                else if (b1 == 0xFF) { r = data[p++]; g = data[p++]; b = data[p++]; a = data[p++]; }
                else
                {
                    switch (b1 >> 6)
                    {
                        case 0: // INDEX
                            var i = (b1 & 0x3F) * 4;
                            r = index[i]; g = index[i + 1]; b = index[i + 2]; a = index[i + 3];
                            break;
                        case 1: // DIFF
                            r += (byte)(((b1 >> 4) & 3) - 2);
                            g += (byte)(((b1 >> 2) & 3) - 2);
                            b += (byte)((b1 & 3) - 2);
                            break;
                        case 2: // LUMA
                            var b2 = data[p++];
                            var dg = (b1 & 0x3F) - 32;
                            r += (byte)(dg - 8 + ((b2 >> 4) & 0x0F));
                            g += (byte)dg;
                            b += (byte)(dg - 8 + (b2 & 0x0F));
                            break;
                        default: // RUN
                            run = b1 & 0x3F;
                            break;
                    }
                }
                var h6 = (r * 3 + g * 5 + b * 7 + a * 11) % 64 * 4;
                index[h6] = r; index[h6 + 1] = g; index[h6 + 2] = b; index[h6 + 3] = a;
            }
            px[o] = r; px[o + 1] = g; px[o + 2] = b; px[o + 3] = a;
        }
        return new Image(w, h, px);
    }
}
