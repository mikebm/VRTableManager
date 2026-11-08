using Godot;

namespace VrLobby;

/// <summary>
/// The arcade's floor and ceiling. The floor is blacklight arcade carpet: neon squiggles, rings, stars and
/// zigzags on near-black, drawn here at start-up from a fixed seed (an original pattern, not a copy of
/// any real carpet) and tiled seamlessly. It glows faintly the way UV-reactive carpet does under blacklight.
/// The ceiling is black drop-ceiling tiles on a metal grid, with two neon tube rings hanging under it.
/// </summary>
public static class ArcadeRoom
{
    public const float CeilingY = 3.6f;

    /// <summary>One carpet tile covers this many metres.</summary>
    public const float CarpetTile = 1.5f;

    /// <summary>One ceiling tile (a 2 ft drop-ceiling tile).</summary>
    public const float CeilingTile = 0.61f;

    public static readonly Color[] Neon =
    [
        new("#FF2BD6"),
        new("#22E5FF"),
        new("#FFE23A"),
        new("#6BFF3A"),
        new("#FF7A1F"),
        new("#9B5CFF"),
    ];

    public static MeshInstance3D Floor(float size)
    {
        var tex = ImageTexture.CreateFromImage(Carpet(1024, 1977));
        return new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(size, size) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoTexture = tex,
                Roughness = 1f,
                MetallicSpecular = 0f, // carpet has no sheen: without this it mirrors the room's reflection map as a grey haze
                // Blacklight glow: the neon fibres light up a little on their own; the black stays black.
                EmissionEnabled = true,
                EmissionTexture = tex,
                EmissionEnergyMultiplier = 0.2f,
                Uv1Scale = new Vector3(size / CarpetTile, size / CarpetTile, 1),
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            },
        };
    }

    public static Node3D Ceiling(float radius)
    {
        var root = new Node3D { Name = "Ceiling" };
        var size = radius * 2;
        root.AddChild(
            new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(size, size), FlipFaces = true }, // faces down
                Position = new Vector3(0, CeilingY, 0),
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoTexture = ImageTexture.CreateFromImage(CeilingTiles(256)),
                    Roughness = 0.9f,
                    Uv1Scale = new Vector3(size / CeilingTile, size / CeilingTile, 1),
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            }
        );
        // Neon tubes: magenta over the cabinets, cyan further out. Glow (the environment's bloom) makes them halo.
        root.AddChild(NeonRing(Main.RingFront + 0.9f, CeilingY - 0.12f, Neon[0]));
        root.AddChild(NeonRing(5.6f, CeilingY - 0.12f, Neon[1]));
        return root;
    }

    static MeshInstance3D NeonRing(float radius, float y, Color colour) =>
        new()
        {
            Mesh = new TorusMesh
            {
                InnerRadius = radius - 0.018f,
                OuterRadius = radius + 0.018f,
                Rings = 96,
                RingSegments = 8,
            },
            Position = new Vector3(0, y, 0),
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = colour,
                EmissionEnabled = true,
                Emission = colour,
                EmissionEnergyMultiplier = 2.2f,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };

    // ---- Carpet --------------------------------------------------------------------------

    /// <summary>
    /// A seamless square of arcade carpet, size × size pixels. Every shape is drawn with wrap-around, so the
    /// edges meet. Same seed, same carpet.
    /// </summary>
    public static Image Carpet(int size, int seed)
    {
        var rng = new Random(seed);
        var px = new float[size * size * 3];
        // Near-black base with a fibre speckle, so it reads as carpet rather than paint.
        for (var i = 0; i < size * size; i++)
        {
            var n = (float)rng.NextDouble();
            var v = 0.004f + n * n * 0.012f;
            px[i * 3] = v;
            px[i * 3 + 1] = v * 0.9f;
            px[i * 3 + 2] = v * 1.35f;
        }

        var s = size / 1024f; // shapes are designed for a 1024 px tile
        for (var k = 0; k < 70; k++)
        {
            var colour = Neon[rng.Next(Neon.Length)];
            var c = new Vector2((float)rng.NextDouble() * size, (float)rng.NextDouble() * size);
            var angle = (float)(rng.NextDouble() * Math.Tau);
            var scale = (34 + (float)rng.NextDouble() * 46) * s;
            var width = (4.5f + (float)rng.NextDouble() * 3f) * s;
            switch (k % 6)
            {
                case 0 or 1:
                    Stroke(px, size, Squiggle(c, angle, scale * 2.2f), width, colour);
                    break;
                case 2:
                    Ring(px, size, c, scale * 0.55f, width, colour);
                    break;
                case 3:
                    Stroke(px, size, Star(c, angle, scale * 0.6f), width, colour, closed: true);
                    break;
                case 4:
                    Stroke(px, size, Zigzag(c, angle, scale * 1.8f), width, colour);
                    break;
                case 5:
                    Stroke(
                        px,
                        size,
                        Triangle(c, angle, scale * 0.65f),
                        width,
                        colour,
                        closed: true
                    );
                    break;
            }
        }
        // Confetti: small dots and plus-sparkles in between.
        for (var k = 0; k < 160; k++)
        {
            var colour = Neon[rng.Next(Neon.Length)];
            var c = new Vector2((float)rng.NextDouble() * size, (float)rng.NextDouble() * size);
            if (k % 3 == 0)
            {
                var r = (7 + (float)rng.NextDouble() * 5) * s;
                Stroke(px, size, [c - new Vector2(r, 0), c + new Vector2(r, 0)], 3f * s, colour);
                Stroke(px, size, [c - new Vector2(0, r), c + new Vector2(0, r)], 3f * s, colour);
            }
            else
            {
                Dot(px, size, c, (2.5f + (float)rng.NextDouble() * 3.5f) * s, colour);
            }
        }

        var bytes = new byte[size * size * 3];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)Math.Clamp(ToSrgb(px[i]) * 255f + 0.5f, 0, 255);
        }

        var img = Image.CreateFromData(size, size, false, Image.Format.Rgb8, bytes);
        img.GenerateMipmaps();
        return img;
    }

    static List<Vector2> Squiggle(Vector2 c, float angle, float length)
    {
        var pts = new List<Vector2>();
        var dir = Vector2.FromAngle(angle);
        var side = dir.Orthogonal();
        for (var i = 0; i <= 28; i++)
        {
            var t = i / 28f - 0.5f;
            pts.Add(
                c + dir * (t * length) + side * (Mathf.Sin(t * Mathf.Tau * 1.6f) * length * 0.12f)
            );
        }
        return pts;
    }

    static List<Vector2> Zigzag(Vector2 c, float angle, float length)
    {
        var pts = new List<Vector2>();
        var dir = Vector2.FromAngle(angle);
        var side = dir.Orthogonal();
        for (var i = 0; i <= 5; i++)
        {
            pts.Add(
                c
                    + dir * ((i / 5f - 0.5f) * length)
                    + side * ((i % 2 == 0 ? -1 : 1) * length * 0.1f)
            );
        }

        return pts;
    }

    static List<Vector2> Star(Vector2 c, float angle, float r)
    {
        var pts = new List<Vector2>();
        for (var i = 0; i < 10; i++)
        {
            pts.Add(c + Vector2.FromAngle(angle + i * Mathf.Pi / 5) * (i % 2 == 0 ? r : r * 0.45f));
        }

        return pts;
    }

    static List<Vector2> Triangle(Vector2 c, float angle, float r) =>
        [
            c + Vector2.FromAngle(angle) * r,
            c + Vector2.FromAngle(angle + Mathf.Tau / 3) * r,
            c + Vector2.FromAngle(angle + 2 * Mathf.Tau / 3) * r,
        ];

    /// <summary>Draws a polyline (or closed polygon outline) with soft edges, wrapping around the tile.</summary>
    static void Stroke(
        float[] px,
        int size,
        List<Vector2> pts,
        float width,
        Color colour,
        bool closed = false
    )
    {
        var min = pts[0];
        var max = pts[0];
        foreach (var p in pts)
        {
            min = new Vector2(Mathf.Min(min.X, p.X), Mathf.Min(min.Y, p.Y));
            max = new Vector2(Mathf.Max(max.X, p.X), Mathf.Max(max.Y, p.Y));
        }
        var pad = width + 2;
        var count = closed ? pts.Count : pts.Count - 1;
        Paint(
            px,
            size,
            min - Vector2.One * pad,
            max + Vector2.One * pad,
            colour,
            p =>
            {
                var d = float.MaxValue;
                for (var i = 0; i < count; i++)
                {
                    d = Mathf.Min(d, SegmentDistance(p, pts[i], pts[(i + 1) % pts.Count]));
                }

                return d - width / 2;
            }
        );
    }

    static void Ring(float[] px, int size, Vector2 c, float r, float width, Color colour) =>
        Paint(
            px,
            size,
            c - Vector2.One * (r + width + 2),
            c + Vector2.One * (r + width + 2),
            colour,
            p => Mathf.Abs(p.DistanceTo(c) - r) - width / 2
        );

    static void Dot(float[] px, int size, Vector2 c, float r, Color colour) =>
        Paint(
            px,
            size,
            c - Vector2.One * (r + 2),
            c + Vector2.One * (r + 2),
            colour,
            p => p.DistanceTo(c) - r
        );

    /// <summary>
    /// Fills the pixels in [from, to] by a signed distance (negative inside), anti-aliased over a pixel. Pixel
    /// coordinates outside the tile wrap round, so a shape over an edge carries on at the other side.
    /// </summary>
    static void Paint(
        float[] px,
        int size,
        Vector2 from,
        Vector2 to,
        Color colour,
        Func<Vector2, float> distance
    )
    {
        var lin = colour.SrgbToLinear() * 0.6f; // fibres, not paint: a little short of full neon
        for (var y = (int)Mathf.Floor(from.Y); y <= (int)Mathf.Ceil(to.Y); y++)
        {
            for (var x = (int)Mathf.Floor(from.X); x <= (int)Mathf.Ceil(to.X); x++)
            {
                var cover = Mathf.Clamp(0.5f - distance(new Vector2(x + 0.5f, y + 0.5f)), 0f, 1f);
                if (cover <= 0)
                {
                    continue;
                }

                var i = (((y % size) + size) % size * size + ((x % size) + size) % size) * 3;
                px[i] = Mathf.Lerp(px[i], lin.R, cover);
                px[i + 1] = Mathf.Lerp(px[i + 1], lin.G, cover);
                px[i + 2] = Mathf.Lerp(px[i + 2], lin.B, cover);
            }
        }
    }

    static float ToSrgb(float v) =>
        v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;

    static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t = Mathf.Clamp((p - a).Dot(ab) / Mathf.Max(ab.LengthSquared(), 1e-6f), 0f, 1f);
        return p.DistanceTo(a + ab * t);
    }

    // ---- Ceiling -------------------------------------------------------------------------

    /// <summary>One drop-ceiling tile: speckled black acoustic board inside a thin grey metal grid (half on each edge, so tiles meet).</summary>
    public static Image CeilingTiles(int size)
    {
        var rng = new Random(7);
        var bytes = new byte[size * size * 3];
        var grid = Mathf.Max(2, size / 64);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var edge = Math.Min(Math.Min(x, size - 1 - x), Math.Min(y, size - 1 - y));
                float v;
                if (edge < grid)
                {
                    v = 0.20f; // the metal T-bar
                }
                else if (edge < grid + 2)
                {
                    v = 0.035f; // its shadow on the tile
                }
                else
                {
                    var n = (float)rng.NextDouble();
                    v = n > 0.985f ? 0.015f : 0.05f + n * 0.018f; // fissured board with pinholes
                }
                var b = (byte)Math.Clamp(ToSrgb(v) * 255f + 0.5f, 0, 255);
                var i = (y * size + x) * 3;
                bytes[i] = b;
                bytes[i + 1] = b;
                bytes[i + 2] = (byte)Math.Min(255, b + 2);
            }
        }

        var img = Image.CreateFromData(size, size, false, Image.Format.Rgb8, bytes);
        img.GenerateMipmaps();
        return img;
    }
}
