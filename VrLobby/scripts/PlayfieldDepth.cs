using Godot;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>
/// Gives the flat playfield picture real depth: the quad is lifted to where the playfield glass would be, and a
/// shader walks each eye's view ray down into the table's height map (<see cref="VpxCabinet.HeightMap"/>) to find
/// what it hits first, ramps and toys before the playfield under them. So the top-down video shows ramps
/// standing up, with true stereo depth and parallax as your head moves (parallax occlusion mapping).
/// </summary>
public static class PlayfieldDepth
{
    /// <summary>Cached next to the lobby video's frames, so re-recording the video drops it too.</summary>
    public const string FileName = "heights-v2.png"; // v2: recorded up to 22 cm

    const string Code = """
        shader_type spatial;
        render_mode unshaded, cull_back;

        uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic, repeat_disable;
        uniform sampler2D height_tex : filter_linear, repeat_disable;  // r: top of what's there, g: its underside
        uniform vec4 tint : source_color = vec4(1.0);
        uniform vec2 size = vec2(0.5, 1.1);  // the quad, metres
        uniform float depth = 0.1;           // from glass level (where rays start) down to the playfield, metres
        uniform float top = 0.13;            // height a full height-map value stands for, metres
        uniform float side_shade = 0.22;     // the sides of things (linear: ≈ 50% on screen)
        uniform float under_shade = 0.3;     // the playfield seen under a ramp or plastic (≈ 58% on screen)
        const int STEPS = 64;

        bool inside(vec2 uv) { return all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0))); }

        // Is the point d metres below glass level, over picture point uv, inside something (or the playfield)?
        // Nothing stands outside the picture: rays that come in over the back or the sides only meet the playfield.
        bool solid(vec2 uv, float d) {
            if (d >= depth - 0.0005) return true;
            if (!inside(uv)) return false;
            vec2 s = max(vec2(depth) - textureLod(height_tex, uv, 0.0).rg * top, vec2(0.0));
            return d >= s.x && d <= s.y + 0.0005;
        }

        // Follows one eye ray to the playfield point p (picture coordinates) and returns what it sees.
        vec3 trace(vec2 p, vec2 per_metre, vec2 gx, vec2 gy) {
            // The quad lies on the playfield itself: p is where this ray reaches the playfield. It came in through
            // glass level, depth metres higher, at `start`, and the first thing it met on the way down is what shows.
            vec2 start = p - per_metre * depth;
            float step_d = depth / float(STEPS);
            float d = 0.0, prev_d = 0.0;
            for (int i = 0; i <= STEPS; i++) {
                if (solid(start + per_metre * d, d)) break;
                prev_d = d;
                d = min(d + step_d, depth);
            }
            // Narrow down where between the last two steps the ray went in.
            float lo = prev_d, hi = d;
            for (int i = 0; i < 6; i++) {
                float mid = 0.5 * (lo + hi);
                if (solid(start + per_metre * mid, mid)) hi = mid; else lo = mid;
            }
            d = hi;
            vec2 uv = d >= depth - 0.0005 ? p : start + per_metre * d;
            vec3 c = textureGrad(albedo_tex, clamp(uv, 0.0, 1.0), gx, gy).rgb;

            // The video only saw things from above, so it has no picture of their sides or of what's under them.
            if (d >= depth - 0.001) {
                // On the playfield: in the open it's the video as it is; under a ramp or plastic, the video only
                // has that ramp's top there, so it's drawn in its shadow.
                return textureLod(height_tex, uv, 0.0).g > 0.01 ? c * under_shade : c;
            }
            // On something. A ray that landed on its top lands right at that top; one that met its side (a plastic's
            // edge, a ramp wall) lands below the tallest thing around: drawn in shadow rather than smearing the
            // top's edge down it. Steep slopes count as sides too.
            vec2 texel = 1.0 / vec2(textureSize(height_tex, 0));
            float h = 0.0;
            for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                    h = max(h, textureLod(height_tex, uv + vec2(float(x), float(y)) * texel, 0.0).r);
            float below = d - max(depth - h * top, 0.0);
            vec2 e = 2.0 * texel;
            float sx = (textureLod(height_tex, uv + vec2(e.x, 0.0), 0.0).r - textureLod(height_tex, uv - vec2(e.x, 0.0), 0.0).r) * top / (2.0 * e.x * size.x);
            float sy = (textureLod(height_tex, uv + vec2(0.0, e.y), 0.0).r - textureLod(height_tex, uv - vec2(0.0, e.y), 0.0).r) * top / (2.0 * e.y * size.y);
            float upright = 1.0 / sqrt(1.0 + sx * sx + sy * sy);  // 1 = flat, 0 = vertical
            float side = max(smoothstep(0.002, 0.012, below), 1.0 - smoothstep(0.25, 0.6, upright));
            return c * mix(1.0, side_shade, side);
        }

        void fragment() {
            // From this point towards the eye being drawn. In the headset both eyes are drawn in one pass and VERTEX
            // is relative to the point between them; EYE_OFFSET is this eye's offset from it (0 on a flat screen).
            // Without it both eyes would get the same picture and it would look flat.
            vec3 v = normalize(-VERTEX + EYE_OFFSET);
            // View direction in the quad's own terms: x along UV.x, y along UV.y, z out of the surface.
            vec3 t = vec3(dot(v, TANGENT), dot(v, -BINORMAL), max(dot(v, NORMAL), 0.12));
            vec2 per_metre = -(t.xy / t.z) / size;  // how far the ray drifts across the picture per metre down
            // Four rays per pixel, spread over it, averaged: edges of ramps and posts come out smooth rather than
            // stair-stepped (the hits are all-or-nothing, so one ray per pixel can't blend them).
            vec2 gx = dFdx(UV), gy = dFdy(UV);
            vec3 c = vec3(0.0);
            c += trace(UV + (-0.25 * gx - 0.25 * gy), per_metre, gx, gy);
            c += trace(UV + ( 0.25 * gx - 0.25 * gy), per_metre, gx, gy);
            c += trace(UV + (-0.25 * gx + 0.25 * gy), per_metre, gx, gy);
            c += trace(UV + ( 0.25 * gx + 0.25 * gy), per_metre, gx, gy);
            ALBEDO = c * 0.25 * tint.rgb;
        }
        """;

    static Shader? _shader;
    public static Shader Shader => _shader ??= new Shader { Code = Code };

    /// <summary>
    /// The table's height map as an RGB image, red the tops and green their undersides (cached in
    /// <paramref name="videoFolder"/>), or null when the table has nothing standing on its playfield worth showing.
    /// Slow the first time (reads the .vpx): call off the main thread. Never throws.
    /// </summary>
    public static Image? Load(string videoFolder, string vpxPath)
    {
        var cache = Path.Combine(videoFolder, FileName);
        try
        {
            if (File.Exists(cache))
            {
                var img = new Image();
                if (img.LoadPngFromBuffer(File.ReadAllBytes(cache)) == Error.Ok) return img;
                // Unreadable (left over from a crash): made again below.
            }
            var map = VpxCabinet.HeightMap(vpxPath);
            if (map == null || map.Coverage < 0.03f) return null;
            var bytes = new byte[map.Heights.Length * 3];
            for (var i = 0; i < map.Heights.Length; i++)
            {
                bytes[i * 3] = (byte)Math.Round(map.Heights[i] * 255);
                bytes[i * 3 + 1] = (byte)Math.Round(map.Undersides[i] * 255);
            }
            var made = Image.CreateFromData(map.Width, map.Height, false, Image.Format.Rgb8, bytes);
            // Written aside and renamed into place: the lobby's background warm-up and the selected table's video can
            // build the same map at once, and neither may read the other's half-written file.
            var tmp = cache + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, made.SavePngToBuffer());
                File.Move(tmp, cache, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { try { File.Delete(tmp); } catch { } }
            return made;
        }
        catch (Exception e)
        {
            // Depth is an extra: a table the height map can't be read from just plays its video flat.
            GD.PushWarning($"Playfield depth {vpxPath}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// How deep the playfield sits under the lifted quad, metres: the height almost everything on it stays under
    /// (a stray tall part such as a backboard shouldn't push the whole table down).
    /// </summary>
    public static float DepthOf(Image heights)
    {
        var data = heights.GetData();
        var px = heights.GetFormat() == Image.Format.Rgb8 ? 3 : heights.GetFormat() == Image.Format.Rgba8 ? 4 : 1;
        var counts = new int[256];
        for (var i = 0; i < data.Length; i += px) counts[data[i]]++;
        var keep = data.Length / px * 0.9995;
        var level = 255;
        for (int i = 0, sum = 0; i < 256; i++) { sum += counts[i]; if (sum >= keep) { level = i; break; } }
        return Math.Max(level, 1) / 255f * VpxCabinet.HeightMapTop * VpxCabinet.VpuToMetres;
    }
}
