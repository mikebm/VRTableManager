using Godot;

namespace VrLobby;

/// <summary>
/// The low-poly pinball cabinet, built once in code (no model files) and shared by every cabinet
/// in the ring. Parts are grouped by material so a cabinet costs about ten draw calls.
///
/// Cabinet space: x across, y up from the floor, +z towards the player. Real-machine proportions:
/// a 0.74 m wide, 1.3 m long body on 0.58 m legs, the playfield rising ~6.6° towards the back,
/// a chrome lockdown bar at ~0.92 m and chrome side rails, and a backbox with the backglass on top
/// and a speaker panel (two grilles and the dot-matrix display) underneath.
/// </summary>
public static class CabinetKit
{
    public const float Width = 0.74f, Length = 1.40f;
    public const float Front = Length / 2, Back = -Length / 2;
    public const float LegTop = 0.58f;
    public const float TopFront = 0.88f, TopBack = 1.03f;           // playfield surface height at each end
    public static readonly float Slope = Mathf.Atan((TopBack - TopFront) / Length);
    public const float BoxBottom = TopBack, BoxHeight = 0.92f, BoxDepth = 0.26f;
    public const float BoxFront = Back + BoxDepth;                   // z of the backbox's front face
    /// <summary>The playfield fills the well from side board to side board and front wall to backbox.</summary>
    public static Vector2 PlayfieldSize => new(Width - 2 * WallThick + 0.004f, WellLength);
    public static readonly Vector2 BackglassSize = new(0.68f, 0.51f); // 4:3, like VPS backglass images
    const float GlassTopMargin = 0.045f;
    public static float BackglassCentreY => BoxBottom + BoxHeight - GlassTopMargin - BackglassSize.Y / 2;
    // Speaker panel under the backglass: two grilles with the dot-matrix display between them.
    // The panel starts above the trim strip in front of the backbox's base, so the speakers clear it.
    const float PanelBottomMargin = 0.10f, PanelGap = 0.02f;
    static float PanelTop => BackglassCentreY - BackglassSize.Y / 2 - PanelGap;
    static float PanelHeight => PanelTop - (BoxBottom + PanelBottomMargin);
    public static float PanelCentreY => BoxBottom + PanelBottomMargin + PanelHeight / 2;
    public const float SpeakerX = 0.235f, SpeakerRadius = 0.085f;
    public static readonly Vector2 DmdSize = new(0.30f, 0.085f);
    public static float Height => BoxBottom + BoxHeight;
    // The playfield sits recessed inside the cabinet: walls rise above it on both sides and at the
    // front, capped by the side rails and the lockdown bar; the glass lies just under the rail tops.
    // Side boards are thick (like a real cabinet's), so the playfield meets them with no gap.
    public const float WallRise = 0.075f, WallThick = 0.075f, FrontWall = 0.03f;
    public const float RailHalfW = 0.042f, RailHalfH = 0.013f;
    public static float GlassLift => WallRise - 0.012f;
    /// <summary>The visible well between the front wall and the backbox: where the playfield and glass go.</summary>
    public static float WellFront => Front - FrontWall;
    public static float WellLength => WellFront - BoxFront;
    public static float WellCentreZ => (WellFront + BoxFront) / 2;

    // Legs: square, ~8 cm at the top tapering to ~5.5 cm, splayed a little outwards, standing on levelers.
    const float LegTopHalf = 0.04f, LegBottomHalf = 0.028f, LegFloorGap = 0.035f, LegOverlap = 0.16f;
    static readonly (int X, int Z)[] LegCorners = [(-1, -1), (1, -1), (-1, 1), (1, 1)];

    /// <summary>Centre of a leg's top (beside the cabinet, overlapping its side) and bottom (on the leveler).</summary>
    static (Vector3 Top, Vector3 Bottom) LegEnds(int sx, int sz)
    {
        var top = new Vector3(sx * (Width / 2 + LegTopHalf), LegTop + LegOverlap, sz * (Length / 2 - 0.07f));
        var bottom = new Vector3(top.X + sx * 0.05f, LegFloorGap, top.Z + sz * 0.03f);
        return (top, bottom);
    }

    /// <summary>Height of the playfield surface at z (it rises towards the back).</summary>
    public static float TopAt(float z) => TopFront + (Front - z) / Length * (TopBack - TopFront);

    static ArrayMesh? _body, _metal, _chrome, _grille, _glow, _shadow;
    static StandardMaterial3D? _metalMat, _chromeMat, _grilleMat, _shadowMat, _glassMat;

    /// <summary>Cabinet sides, backbox shell and the frame round the backglass: tinted per cabinet.</summary>
    public static ArrayMesh Body => _body ??= BuildBody();
    /// <summary>Legs, coin door, speaker panel and display bezel: dark metal.</summary>
    public static ArrayMesh Metal => _metal ??= BuildMetal();
    /// <summary>Lockdown bar, side rails and speaker rings: polished chrome.</summary>
    public static ArrayMesh Chrome => _chrome ??= BuildChrome();
    /// <summary>The two speaker grilles.</summary>
    public static ArrayMesh Grille => _grille ??= BuildGrille();
    /// <summary>Coin-slot lights, flipper buttons and the neon strip on the backbox: emissive, per cabinet.</summary>
    public static ArrayMesh Glow => _glow ??= BuildGlow();
    /// <summary>Soft blob shadow on the floor.</summary>
    public static ArrayMesh Shadow => _shadow ??= BuildShadow();

    public static StandardMaterial3D MetalMaterial => _metalMat ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.07f, 0.075f, 0.09f), Metallic = 0.6f, Roughness = 0.4f,
    };

    /// <summary>Mirror-like chrome. It reflects the room's reflection map (see Main.ReflectionSky), so it reads as chrome even in a dark room.</summary>
    public static StandardMaterial3D ChromeMaterial => _chromeMat ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.92f, 0.93f, 0.96f), Metallic = 1f, Roughness = 0.1f,
    };

    public static StandardMaterial3D GrilleMaterial => _grilleMat ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.32f, 0.32f, 0.35f), AlbedoTexture = GrilleTexture(),
        Metallic = 0.5f, Roughness = 0.55f,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
    };

    public static StandardMaterial3D GlassMaterial => _glassMat ??= new StandardMaterial3D
    {
        // Barely-there glass: just enough sheen to read as a lid over the playfield.
        AlbedoColor = new Color(0.8f, 0.9f, 1f, 0.025f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        Metallic = 0.15f, Roughness = 0.08f,
    };

    public static StandardMaterial3D ShadowMaterial => _shadowMat ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = Colors.Black,
        AlbedoTexture = BlobTexture(),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
    };

    // ---- Parts ---------------------------------------------------------------------

    static ArrayMesh BuildBody()
    {
        var m = new MeshKit();
        float x = Width / 2;
        // Main body: a box whose top follows the playfield slope.
        m.AddHex(
            new Vector3(-x, LegTop, Front), new Vector3(x, LegTop, Front), new Vector3(x, LegTop, Back), new Vector3(-x, LegTop, Back),
            new Vector3(-x, TopFront, Front), new Vector3(x, TopFront, Front), new Vector3(x, TopBack, Back), new Vector3(-x, TopBack, Back));
        // Side walls rising above the playfield (the cabinet sides continue up), and the front wall
        // under the lockdown bar, so the playfield sits inside the cabinet rather than on top of it.
        var wallBasis = new Basis(Vector3.Right, Slope); // follows the playfield slope (back end higher)
        var wallMidZ = (Front + BoxFront) / 2;
        foreach (var sx in new[] { -1f, 1f })
            m.AddBox(new Vector3(sx * (x - WallThick / 2), TopAt(wallMidZ) + WallRise / 2, wallMidZ),
                new Vector3(WallThick, WallRise, Front - BoxFront), wallBasis);
        m.AddBox(new Vector3(0, TopFront + WallRise / 2, Front - FrontWall / 2), new Vector3(Width, WallRise, FrontWall));
        // Backbox shell, with its front face left open for the backglass and speaker panel.
        m.AddBox(new Vector3(0, BoxBottom + BoxHeight / 2, Back + BoxDepth / 2), new Vector3(Width, BoxHeight, BoxDepth), skipFront: true);
        // Frame strips on the backbox front: either side (full height), above the glass,
        // between the glass and the speaker panel, and under the panel.
        var gw = BackglassSize.X;
        var side = (Width - gw) / 2;
        var z = BoxFront - 0.005f;
        var yMid = BoxBottom + BoxHeight / 2;
        m.AddBox(new Vector3(-(gw / 2 + side / 2), yMid, z), new Vector3(side, BoxHeight, 0.01f));
        m.AddBox(new Vector3(gw / 2 + side / 2, yMid, z), new Vector3(side, BoxHeight, 0.01f));
        m.AddBox(new Vector3(0, BoxBottom + BoxHeight - GlassTopMargin / 2, z), new Vector3(gw, GlassTopMargin, 0.01f));
        m.AddBox(new Vector3(0, PanelTop + PanelGap / 2, z), new Vector3(gw, PanelGap, 0.01f));
        m.AddBox(new Vector3(0, BoxBottom + PanelBottomMargin / 2, z), new Vector3(gw, PanelBottomMargin, 0.01f));
        return m.Commit();
    }

    static ArrayMesh BuildMetal()
    {
        var m = new MeshKit();
        // Legs: bolted on outside the cabinet corners, running up its side, tapering towards the floor
        // and splaying slightly outwards, like a real machine's.
        foreach (var (sx, sz) in LegCorners)
        {
            var (top, bottom) = LegEnds(sx, sz);
            float ht = LegTopHalf, hb = LegBottomHalf;
            Vector3 T(float dx, float dz) => top + new Vector3(dx * ht, 0, dz * ht);
            Vector3 B(float dx, float dz) => bottom + new Vector3(dx * hb, 0, dz * hb);
            m.AddHex(B(-1, 1), B(1, 1), B(1, -1), B(-1, -1), T(-1, 1), T(1, 1), T(1, -1), T(-1, -1));
        }
        // Trim strip along the back of the playfield, under the backbox.
        m.AddBox(new Vector3(0, TopBack + 0.03f, BoxFront + 0.02f), new Vector3(Width - 2 * WallThick, 0.06f, 0.04f));
        // Coin door.
        m.AddBox(new Vector3(0, LegTop + 0.14f, Front + 0.006f), new Vector3(0.30f, 0.22f, 0.012f));
        // Speaker panel (black), and the display bezel standing slightly proud of it.
        m.AddBox(new Vector3(0, PanelCentreY, BoxFront - 0.005f), new Vector3(BackglassSize.X, PanelHeight, 0.01f));
        m.AddBox(new Vector3(0, PanelCentreY, BoxFront + 0.003f), new Vector3(DmdSize.X + 0.02f, DmdSize.Y + 0.02f, 0.006f));
        return m.Commit();
    }

    static ArrayMesh BuildChrome()
    {
        var m = new MeshKit();
        const float square = 6f; // flat-topped with rounded edges, like real rails and lockbars
        // Lockdown bar: a wide flat bar capping the front wall, a little wider than the cabinet.
        var lockHalfH = 0.016f;
        var lockY = TopFront + WallRise + lockHalfH;
        var lockZ = Front - 0.05f;
        m.AddBar(new Vector3(-Width / 2 - 0.01f, lockY, lockZ), new Vector3(Width / 2 + 0.01f, lockY, lockZ),
            halfWidth: 0.058f, halfHeight: lockHalfH, up: Vector3.Up, sides: 32, squareness: square);
        // Side rails capping the side walls, flush with the cabinet's outside and overhanging the glass
        // edge a little, following the slope from behind the lockbar to the backbox.
        var zFront = lockZ - 0.058f;
        var zBack = BoxFront;
        foreach (var sx in new[] { -1f, 1f })
        {
            var rx = sx * (Width / 2 - RailHalfW + 0.002f);
            m.AddBar(new Vector3(rx, TopAt(zFront) + WallRise + RailHalfH, zFront), new Vector3(rx, TopAt(zBack) + WallRise + RailHalfH, zBack),
                halfWidth: RailHalfW, halfHeight: RailHalfH, up: Vector3.Up, sides: 32, squareness: square);
        }
        foreach (var (sx, sz) in LegCorners)
        {
            var (top, bottom) = LegEnds(sx, sz);
            // Leveler under each leg: a threaded stem and a round foot pad.
            var c = new Vector3(bottom.X, 0, bottom.Z);
            m.AddBar(c + new Vector3(0, 0.008f, 0), c + new Vector3(0, LegFloorGap + 0.004f, 0), 0.008f, 0.008f, Vector3.Forward, 10);
            m.AddBar(c, c + new Vector3(0, 0.01f, 0), 0.025f, 0.025f, Vector3.Forward, 16);
            // Mounting plate on the leg's outer face where it meets the cabinet, with two bolt heads.
            var plateX = top.X + sx * (LegTopHalf + 0.002f);
            m.AddBox(new Vector3(plateX, LegTop + 0.05f, top.Z), new Vector3(0.004f, 0.15f, 2 * LegTopHalf - 0.012f));
            foreach (var by in new[] { LegTop + 0.0f, LegTop + 0.1f })
                m.AddBar(new Vector3(plateX, by, top.Z), new Vector3(plateX + sx * 0.008f, by, top.Z), 0.011f, 0.011f, Vector3.Up, 12);
        }
        // Chrome rings round the speakers.
        foreach (var sx in new[] { -1f, 1f })
            m.AddRing(new Vector3(sx * SpeakerX, PanelCentreY, BoxFront + 0.006f), SpeakerRadius + 0.006f, 0.006f, 24);
        return m.Commit();
    }

    static ArrayMesh BuildGrille()
    {
        var m = new MeshKit();
        foreach (var sx in new[] { -1f, 1f })
            m.AddDisc(new Vector3(sx * SpeakerX, PanelCentreY, BoxFront + 0.002f), SpeakerRadius, 32);
        return m.Commit();
    }

    static ArrayMesh BuildGlow()
    {
        var m = new MeshKit();
        // Two coin-slot lights on the door.
        m.AddBox(new Vector3(-0.06f, LegTop + 0.19f, Front + 0.014f), new Vector3(0.03f, 0.05f, 0.008f));
        m.AddBox(new Vector3(0.06f, LegTop + 0.19f, Front + 0.014f), new Vector3(0.03f, 0.05f, 0.008f));
        // Flipper buttons on each side, near the front.
        m.AddBox(new Vector3(-Width / 2 - 0.008f, TopFront - 0.05f, Front - 0.12f), new Vector3(0.016f, 0.035f, 0.035f));
        m.AddBox(new Vector3(Width / 2 + 0.008f, TopFront - 0.05f, Front - 0.12f), new Vector3(0.016f, 0.035f, 0.035f));
        // Neon strip along the top front edge of the backbox.
        m.AddBox(new Vector3(0, BoxBottom + BoxHeight + 0.01f, BoxFront - 0.01f), new Vector3(Width, 0.02f, 0.02f));
        return m.Commit();
    }

    static ArrayMesh BuildShadow()
    {
        var m = new MeshKit();
        // Slightly larger than the footprint and lifted off the floor to avoid z-fighting.
        float x = Width / 2 + 0.25f, z = Length / 2 + 0.3f;
        m.AddQuad(new Vector3(-x, 0.004f, z), new Vector3(x, 0.004f, z), new Vector3(x, 0.004f, -z), new Vector3(-x, 0.004f, -z), Vector3.Up);
        return m.Commit();
    }

    /// <summary>A radial falloff for the blob shadow, made in code (no texture file).</summary>
    static ImageTexture BlobTexture()
    {
        const int n = 64;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var dx = (x + 0.5f) / n * 2 - 1;
            var dy = (y + 0.5f) / n * 2 - 1;
            var d = Mathf.Sqrt(dx * dx + dy * dy);
            var a = Mathf.Clamp(1f - Mathf.SmoothStep(0.45f, 1f, d), 0f, 1f) * 0.75f;
            img.SetPixel(x, y, new Color(0, 0, 0, a));
        }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>Perforated speaker grille: a grid of round holes, made in code.</summary>
    static ImageTexture GrilleTexture()
    {
        const int n = 128, pitch = 8;
        var img = Image.CreateEmpty(n, n, true, Image.Format.Rgb8);
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            // Staggered rows of holes, like punched metal.
            var row = y / pitch;
            var ox = (row % 2) * pitch / 2;
            var cx = ((x + ox) % pitch) - pitch / 2f + 0.5f;
            var cy = (y % pitch) - pitch / 2f + 0.5f;
            var hole = cx * cx + cy * cy < 7.5f;
            var v = hole ? 0.05f : 0.75f;
            img.SetPixel(x, y, new Color(v, v, v));
        }
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }
}

/// <summary>Tiny mesh builder: flat boxes, quads and hex solids, plus smooth bars, rings and discs.</summary>
public sealed class MeshKit
{
    readonly SurfaceTool _st = new();

    public MeshKit() => _st.Begin(Mesh.PrimitiveType.Triangles);

    public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
    {
        // a-b-c-d go counter-clockwise seen from the front (the side the normal points to);
        // Godot's front faces are clockwise, so each triangle is emitted reversed.
        Vertex(a, normal, new Vector2(0, 1)); Vertex(c, normal, new Vector2(1, 0)); Vertex(b, normal, new Vector2(1, 1));
        Vertex(a, normal, new Vector2(0, 1)); Vertex(d, normal, new Vector2(0, 0)); Vertex(c, normal, new Vector2(1, 0));
    }

    void Vertex(Vector3 p, Vector3 n, Vector2 uv)
    {
        _st.SetNormal(n);
        _st.SetUV(uv);
        _st.AddVertex(p);
    }

    /// <summary>A triangle with its own (smooth) normals, wound so it faces the way those normals point.</summary>
    void SmoothTri(Vector3 a, Vector3 na, Vector2 ua, Vector3 b, Vector3 nb, Vector2 ub, Vector3 c, Vector3 nc, Vector2 uc)
    {
        var faces = (b - a).Cross(c - a).Dot(na + nb + nc) > 0; // counter-clockwise seen from outside
        if (faces) { Vertex(a, na, ua); Vertex(c, nc, uc); Vertex(b, nb, ub); }
        else { Vertex(a, na, ua); Vertex(b, nb, ub); Vertex(c, nc, uc); }
    }

    /// <summary>Six faces from 8 corners: bottom (b0..b3) then top (t0..t3), each going front-left, front-right, back-right, back-left.</summary>
    public void AddHex(Vector3 b0, Vector3 b1, Vector3 b2, Vector3 b3, Vector3 t0, Vector3 t1, Vector3 t2, Vector3 t3, bool skipFront = false)
    {
        Face(t0, t1, t2, t3);             // top
        Face(b3, b2, b1, b0);             // bottom
        if (!skipFront) Face(b0, b1, t1, t0); // front (+z)
        Face(b2, b3, t3, t2);             // back
        Face(b3, b0, t0, t3);             // left
        Face(b1, b2, t2, t1);             // right
    }

    void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d) =>
        AddQuad(a, b, c, d, (b - a).Cross(d - a).Normalized());

    public void AddBox(Vector3 centre, Vector3 size, Basis? basis = null, bool skipFront = false)
    {
        var bs = basis ?? Basis.Identity;
        var h = size / 2;
        Vector3 P(float x, float y, float z) => centre + bs * new Vector3(x * h.X, y * h.Y, z * h.Z);
        AddHex(P(-1, -1, 1), P(1, -1, 1), P(1, -1, -1), P(-1, -1, -1),
               P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), P(-1, 1, -1), skipFront);
    }

    /// <summary>
    /// A smooth bar from a to b, with flat end caps. The cross-section is a superellipse:
    /// <paramref name="squareness"/> 2 = oval, ~6 = a flat-topped bar with rounded edges (like pinball
    /// side rails and lockdown bars). halfHeight runs along <paramref name="up"/>, halfWidth across.
    /// Smooth normals make highlights run along it, which is what makes chrome read as chrome.
    /// </summary>
    public void AddBar(Vector3 a, Vector3 b, float halfWidth, float halfHeight, Vector3 up, int sides = 16, float squareness = 2f)
    {
        var d = (b - a).Normalized();
        var v = (up - d * up.Dot(d)).Normalized();   // "up" across the bar
        var u = v.Cross(d).Normalized();             // sideways across the bar
        var p = squareness;
        Vector3 Off(int k, out Vector3 n)
        {
            var t = Mathf.Tau * k / sides;
            var (c, s) = (Mathf.Cos(t), Mathf.Sin(t));
            // Point on |x/a|^p + |y/b|^p = 1, and the outward normal from that curve's gradient.
            var x = Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), 2f / p);
            var y = Mathf.Sign(s) * Mathf.Pow(Mathf.Abs(s), 2f / p);
            var gx = Mathf.Sign(x) * Mathf.Pow(Mathf.Abs(x), p - 1) / halfWidth;
            var gy = Mathf.Sign(y) * Mathf.Pow(Mathf.Abs(y), p - 1) / halfHeight;
            n = (u * gx + v * gy).Normalized();
            return u * (x * halfWidth) + v * (y * halfHeight);
        }
        for (var k = 0; k < sides; k++)
        {
            var o0 = Off(k, out var n0);
            var o1 = Off(k + 1, out var n1);
            var uv0 = new Vector2((float)k / sides, 0);
            var uv1 = new Vector2((float)(k + 1) / sides, 1);
            SmoothTri(a + o0, n0, uv0, b + o0, n0, uv1, b + o1, n1, uv1);
            SmoothTri(a + o0, n0, uv0, b + o1, n1, uv1, a + o1, n1, uv0);
            // End caps.
            SmoothTri(a, -d, Vector2.Zero, a + o0, -d, Vector2.Zero, a + o1, -d, Vector2.Zero);
            SmoothTri(b, d, Vector2.Zero, b + o1, d, Vector2.Zero, b + o0, d, Vector2.Zero);
        }
    }

    /// <summary>A ring (torus-ish) facing +z, made of short round bars.</summary>
    public void AddRing(Vector3 centre, float radius, float tube, int segments)
    {
        for (var k = 0; k < segments; k++)
        {
            var t0 = Mathf.Tau * k / segments;
            var t1 = Mathf.Tau * (k + 1) / segments;
            var p0 = centre + new Vector3(Mathf.Cos(t0), Mathf.Sin(t0), 0) * radius;
            var p1 = centre + new Vector3(Mathf.Cos(t1), Mathf.Sin(t1), 0) * radius;
            AddBar(p0, p1, tube, tube, Vector3.Back, 8);
        }
    }

    /// <summary>A flat disc facing +z, UV-mapped across its square bounds.</summary>
    public void AddDisc(Vector3 centre, float radius, int segments)
    {
        var n = Vector3.Back; // +z
        Vector2 Uv(float x, float y) => new(0.5f + x / (2 * radius), 0.5f - y / (2 * radius));
        for (var k = 0; k < segments; k++)
        {
            var t0 = Mathf.Tau * k / segments;
            var t1 = Mathf.Tau * (k + 1) / segments;
            var (x0, y0) = (Mathf.Cos(t0) * radius, Mathf.Sin(t0) * radius);
            var (x1, y1) = (Mathf.Cos(t1) * radius, Mathf.Sin(t1) * radius);
            SmoothTri(centre, n, Uv(0, 0), centre + new Vector3(x0, y0, 0), n, Uv(x0, y0), centre + new Vector3(x1, y1, 0), n, Uv(x1, y1));
        }
    }

    public ArrayMesh Commit() => _st.Commit();
}
