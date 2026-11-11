using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using OpenMcdf;

namespace VrTableManager.Services;

/// <summary>A material from the table (VPX "basic" or "metal").</summary>
public sealed record VpxMaterial(
    string Name,
    uint BaseColour,
    bool IsMetal,
    float Roughness,
    float Opacity,
    bool OpacityActive
)
{
    /// <summary>VPX COLORREF is 0x00BBGGRR.</summary>
    public (float R, float G, float B) Rgb =>
        (
            (BaseColour & 0xFF) / 255f,
            ((BaseColour >> 8) & 0xFF) / 255f,
            ((BaseColour >> 16) & 0xFF) / 255f
        );
}

/// <summary>
/// One cabinet part as a ready-to-draw triangle mesh in lobby space: metres, x right, y up, +z towards
/// the player, triangles wound so their front faces (Godot's) are the side the normals point to.
/// </summary>
public sealed class VpxCabinetPart
{
    public string Name { get; init; } = "";
    public float[] Positions { get; init; } = []; // x,y,z per vertex
    public float[] Normals { get; init; } = [];
    public float[] Uvs { get; init; } = []; // u,v per vertex
    public int[] Indices { get; init; } = [];
    public string? Image { get; init; }
    public VpxMaterial? Material { get; init; }

    /// <summary>VPX "disable lighting" (0..1): how much the part lights itself (backglasses, lit art) regardless of the room.</summary>
    public float SelfLit { get; init; }
    public int VertexCount => Positions.Length / 3;
}

/// <summary>A table's own 3D cabinet, in lobby space (floor at y = 0, centred on x = 0, front at z = <see cref="FrontZ"/>).</summary>
public sealed class VpxCabinetModel
{
    public List<VpxCabinetPart> Parts { get; init; } = [];

    /// <summary>Playfield corners in lobby space: back-left, back-right, front-right, front-left.</summary>
    public Vector3[] PlayfieldCorners { get; init; } = [];

    /// <summary>How far the cabinet was tipped forward to stand level on its legs (radians; see VpxCabinet.LevelingTilt).</summary>
    public float Tilt { get; init; }

    /// <summary>The playfield's slope, back end up (radians): the table's own slope, as VPX shows it (the legs' <see cref="Tilt"/> when the table names none).</summary>
    public float PlayfieldSlope =>
        PlayfieldCorners.Length == 4
            ? MathF.Atan2(
                PlayfieldCorners[0].Y - PlayfieldCorners[3].Y,
                PlayfieldCorners[3].Z - PlayfieldCorners[0].Z
            )
            : 0;
    public float FrontZ { get; init; }
    public Vector3 Min { get; init; }
    public Vector3 Max { get; init; }

    /// <summary>Height of the playfield surface at its front edge (for the lockbar-height sanity check).</summary>
    public float PlayfieldFrontHeight => PlayfieldCorners.Length == 4 ? PlayfieldCorners[3].Y : 0;
    public IEnumerable<string> ImageNames =>
        Parts.Select(p => p.Image).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A playfield's heights seen from above (see <see cref="VpxCabinet.HeightMap"/>): <see cref="Heights"/> row by row from the
/// back, 0 = playfield surface, 1 = <see cref="TopMetres"/> above it.
/// </summary>
public sealed record PlayfieldHeightMap(
    int Width,
    int Height,
    float[] Heights,
    float[] Undersides,
    int Primitives,
    Vector2 SizeMetres,
    float TopMetres
)
{
    /// <summary>Share of the playfield with something standing on it (0..1); near 0 for tables built from walls and ramps.</summary>
    public float Coverage => Heights.Count(h => h > 0.02f) / (float)Heights.Length;
}

/// <summary>A primitive as read from the table, before any filtering (also used for diagnostics).</summary>
public sealed record VpxPrimitiveInfo(
    string Name,
    bool Visible,
    bool Use3DMesh,
    int Vertices,
    string? Image,
    string? Material,
    string? Group,
    Vector3 Min,
    Vector3 Max
);

/// <summary>
/// Reads the VR cabinet a table's author built into its .vpx: the 3D "primitives" (cabinet body, side
/// rails, lockdown bar, legs, backbox, speaker grilles, coin door…), their materials and texture names.
/// Follows VPX's own formats (src/parts/primitive.cpp, Material.h): BIFF records per primitive, zlib-
/// compressed vertex/index arrays, and the transform scale → rotate/translate → position.
/// </summary>
public static class VpxCabinet
{
    /// <summary>One VPX unit in metres: 50 units are 1 1/16 inch.</summary>
    public const float VpuToMetres = 1.0625f * 0.0254f / 50f;

    const int ItemSurface = 0,
        ItemPrimitive = 19,
        ItemPartGroup = 24;

    // Names that cabinet builders use (PinCab template, VPW, g5k…), and room/lighting parts to leave out.
    static readonly Regex CabinetName = new(
        @"pincab|cabinet|^cab|cab$|_cab|cab_|backbox|backglass|lockdown|lockbar|side_?rail|siderail|^rails?$|_rails?\b|rails?_|leg|coin|grill|speaker|dmd|button|blade|bottom|metals?\b|metal_|fitting|apron|plunger|shooter|housing|bolt|feet|foot|leveler|trim",
        RegexOptions.IgnoreCase
    );

    // "Cab" as its own word inside a camel-case name: AC/DC's outer panels are "TAFCabLeft", "TAFCabRight" and
    // "TAFCabFront", which CabinetName (cab at a word edge) misses; without them the cabinet stood open, its inside
    // showing through the sides. Case-sensitive, so "vocabulary" or "Cable" don't count.
    static readonly Regex CamelCab = new(@"Cab(?=[A-Z0-9_]|$)");
    static readonly Regex RoomName = new(
        @"room|floor|wall|ceiling|poster|window|carpet|chair|shelf|clock|plant|sign|sky|tree|cloud|curtain|beam|lamp|light|neon|topper|stool|rug|desk|tv\b|screen|sofa|door(?!.*coin)(?<!coin.*)",
        RegexOptions.IgnoreCase
    );

    // Lit/unlit duplicates ("cab_off", "APRON_GIoff", "siderails_off"): keep one version only.
    static readonly Regex OffVariant = new(
        @"(_|\b)(gi_?)?off\d*$|_off_|gioff",
        RegexOptions.IgnoreCase
    );

    // VLM lightmaps ("LM_GIBottom_RailsLockdown") are additive light layers over the baked meshes ("BM_…"),
    // not geometry to draw on their own; plain "Bolt12" parts are playfield post bolts.
    static readonly Regex NotGeometry = new(@"^lm_|^bolt\d*$", RegexOptions.IgnoreCase);

    /// <summary>
    /// The playfield glass ("PinCab_Glass"): the lobby shows the playfield itself, and a glass part often has no
    /// material, which draws as an opaque grey sheet over the playfield (Big Trouble in Little China).
    /// </summary>
    static bool IsPlayfieldGlass(string name) =>
        name.Contains("glass", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("backglass", StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeCabinetPart(string name) =>
        (CabinetName.IsMatch(name) || CamelCab.IsMatch(name))
        && !RoomName.IsMatch(name)
        && !OffVariant.IsMatch(name)
        && !NotGeometry.IsMatch(name)
        && !IsPlayfieldGlass(name);

    // ---- Playfield height map -------------------------------------------------------------

    /// <summary>Tallest thing a height map records above the playfield (VPX units, ≈ 22 cm, about glass height): ramps, toys, back decorations.</summary>
    public const float HeightMapTop = 400f;

    /// <summary>
    /// How deep below its top the topmost thing at a spot is taken to reach, at most (VPX units, ≈ 1.6 cm). A ramp
    /// floor or a plastic is a few millimetres thick, so the playfield shows under it rather than a solid block
    /// running down to the playfield; a solid toy becomes a shell, dark underneath.
    /// </summary>
    public const float HeightMapLayer = 30f;

    /// <summary>
    /// The table's playfield seen from straight above: for each pixel, how high the topmost visible thing on the
    /// playfield is there (<see cref="PlayfieldHeightMap.Heights"/>) and where its underside is
    /// (<see cref="PlayfieldHeightMap.Undersides"/>), so ramps and plastics can be looked under. 0 = the playfield,
    /// 1 = <see cref="HeightMapTop"/>; rows run from the back of the playfield (top of the picture) to the front,
    /// like the lobby video. Built from the table's visible 3D primitives, which is how modern tables (VPW's baked
    /// "BM_" meshes) draw their ramps, plastics and toys; older tables built from VPX walls and ramps come out
    /// mostly flat. Null when the table can't be read. <paramref name="include"/> picks primitives by name (diagnostics).
    /// </summary>
    public static PlayfieldHeightMap? HeightMap(
        string vpxPath,
        int width = 384,
        int height = 768,
        Func<string, bool>? include = null
    )
    {
        try
        {
            var t = Read(vpxPath, meshes: true);
            var pw = t.Right - t.Left;
            var ph = t.Bottom - t.Top;
            if (pw <= 0 || ph <= 0)
            {
                return null;
            }

            var meshes = new List<(Vector3[] Px, int[] Idx)>();
            foreach (var p in t.Prims)
            {
                if (
                    !p.Visible
                    || !p.Use3DMesh
                    || p.Verts == null
                    || p.Indices == null
                    || p.NumVerts == 0
                )
                {
                    continue;
                }

                if (
                    NotGeometry.IsMatch(p.Name)
                    || IsPlayfieldGlass(p.Name)
                    || HeightMapSkip.IsMatch(p.Name)
                )
                {
                    continue;
                }

                if (SpaceOf(p.Group, t) != 0)
                {
                    continue; // cabinet / room space
                }

                if (include != null && !include(p.Name))
                {
                    continue;
                }

                var m = FullMatrix(p, t);
                var px = new Vector3[p.NumVerts];
                for (var i = 0; i < p.NumVerts; i++)
                {
                    var v = Vector3.Transform(
                        new Vector3(p.Verts[i * 8], p.Verts[i * 8 + 1], p.Verts[i * 8 + 2]),
                        m
                    );
                    px[i] = new Vector3(
                        (v.X - t.Left) / pw * width,
                        (v.Y - t.Top) / ph * height,
                        v.Z
                    );
                }
                meshes.Add((px, p.Indices));
            }
            // First the tops, then (knowing them) the undersides of whatever is on top.
            var top = new float[width * height];
            var used = 0;
            foreach (var (px, idx) in meshes)
            {
                var drew = false;
                for (var i = 0; i + 2 < idx.Length; i += 3)
                {
                    drew |= Raster(
                        top,
                        null,
                        width,
                        height,
                        px[idx[i]],
                        px[idx[i + 1]],
                        px[idx[i + 2]]
                    );
                }

                if (drew)
                {
                    used++;
                }
            }
            var under = (float[])top.Clone();
            foreach (var (px, idx) in meshes)
            {
                for (var i = 0; i + 2 < idx.Length; i += 3)
                {
                    Raster(top, under, width, height, px[idx[i]], px[idx[i + 1]], px[idx[i + 2]]);
                }
            }

            for (var i = 0; i < top.Length; i++)
            {
                top[i] = Math.Clamp(top[i] / HeightMapTop, 0f, 1f);
                under[i] = Math.Clamp(under[i] / HeightMapTop, 0f, top[i]);
            }
            return new PlayfieldHeightMap(
                width,
                height,
                top,
                under,
                used,
                new Vector2(pw * VpuToMetres, ph * VpuToMetres),
                HeightMapTop * VpuToMetres
            );
        }
        catch
        {
            return null;
        }
    }

    // Cabinet body, glass and the room never stand on the playfield; the apron does, and stays.
    static readonly Regex HeightMapSkip = new(
        @"pincab|cabinet|^cab|blade|siderail|side_?rail|lockdown|lockbar|backbox|backglass|room|floor|ceiling|glass|reflect|shadow",
        RegexOptions.IgnoreCase
    );

    /// <summary>
    /// Draws one triangle into the height map. With <paramref name="under"/> null it keeps the highest point per pixel
    /// in <paramref name="top"/>; otherwise it keeps in <paramref name="under"/> the lowest point that still belongs
    /// to the top layer (within <see cref="HeightMapLayer"/> of the top). True when it changed any pixel.
    /// </summary>
    static bool Raster(float[] top, float[]? under, int w, int h, Vector3 a, Vector3 b, Vector3 c)
    {
        // Entirely above what's recorded (a backbox, a topper): not playfield.
        if (a.Z > HeightMapTop * 1.5f && b.Z > HeightMapTop * 1.5f && c.Z > HeightMapTop * 1.5f)
        {
            return false;
        }

        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        var maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        var maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        if (minX > maxX || minY > maxY)
        {
            return false;
        }

        var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        var any = false;

        bool Put(int i, float zHigh, float zLow)
        {
            if (under == null)
            {
                if (zHigh <= top[i])
                {
                    return false;
                }

                top[i] = zHigh;
                return true;
            }
            var layer = top[i] - HeightMapLayer;
            if (zHigh < layer)
            {
                return false;
            }

            var z = MathF.Max(zLow, layer);
            if (z >= under[i])
            {
                return false;
            }

            under[i] = z;
            return true;
        }

        if (MathF.Abs(area) < 1e-6f)
        {
            // Seen edge-on (a wall's side): it marks the pixels it passes over, from its top edge down.
            if (maxX - minX > 2 && maxY - minY > 2)
            {
                return false;
            }

            var zHigh = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
            var zLow = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    any |= Put(y * w + x, zHigh, zLow);
                }
            }

            return any;
        }
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                float sx = x + 0.5f,
                    sy = y + 0.5f;
                var w0 = ((b.X - sx) * (c.Y - sy) - (b.Y - sy) * (c.X - sx)) / area;
                var w1 = ((c.X - sx) * (a.Y - sy) - (c.Y - sy) * (a.X - sx)) / area;
                var w2 = 1 - w0 - w1;
                if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f)
                {
                    continue;
                }

                var z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                any |= Put(y * w + x, z, z);
            }
        }

        return any;
    }

    // ---- Reading ---------------------------------------------------------------------

    sealed class Prim
    {
        public string Name = "";
        public Vector3 Pos,
            Size = Vector3.One;
        public readonly float[] RotTra = new float[9];
        public string? Image,
            Material,
            Group;
        public bool Visible = true,
            Use3DMesh;

        /// <summary>Built from a VPX wall (<see cref="ReadWall"/>): its script's ".Image" is the wall's top, not these sides.</summary>
        public bool IsWall;
        public float SelfLit;
        public int NumVerts,
            NumIndices,
            CompressedVerts,
            CompressedIndices;
        public float[]? Verts; // 8 floats per vertex: x,y,z,nx,ny,nz,u,v
        public int[]? Indices;
    }

    sealed class TableData
    {
        public float Left,
            Top,
            Right,
            Bottom;
        public float GlassBottom,
            GroundToLockbar = 91f / 100f / VpuToMetres;

        /// <summary>The table's playfield slope range in degrees (GameData's SLOP and SLPX; difficulty picks between them). 0 when missing.</summary>
        public float SlopeMin,
            SlopeMax;
        public Dictionary<string, VpxMaterial> Materials = new(StringComparer.OrdinalIgnoreCase);
        public List<Prim> Prims = [];

        /// <summary>The table's VPX walls with visible sides, as meshes (see <see cref="ReadWall"/>); only <see cref="PlayfieldWalls"/> looks at them.</summary>
        public List<Prim> Walls = [];
        public Dictionary<string, (int Space, string? Parent)> Groups = new(
            StringComparer.OrdinalIgnoreCase
        );

        /// <summary>The table's collections (named groups of parts its script switches together), by name.</summary>
        public Dictionary<string, List<string>> Collections = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The playfield's width in centimetres (GameData's LEFT and RGHT), reading nothing else of the table. Null when
    /// the file can't be read or names no width.
    /// </summary>
    public static double? PlayfieldWidthCm(string vpxPath)
    {
        try
        {
            using var root = RootStorage.OpenRead(vpxPath);
            if (!root.OpenStorage("GameStg").TryOpenStream("GameData", out var gd) || gd == null)
            {
                return null;
            }

            float? left = null,
                right = null;
            using (gd)
            {
                foreach (var (tag, d) in Records(ReadAll(gd), 0))
                {
                    if (tag == "LEFT")
                    {
                        left = F(d);
                    }
                    else if (tag == "RGHT")
                    {
                        right = F(d);
                    }

                    if (left != null && right != null)
                    {
                        break;
                    }
                }
            }
            return right is { } r && r - (left ?? 0) is var w and > 0
                ? w * VpuToMetres * 100
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Every primitive in the table with its name, visibility and bounds (VPX units), for diagnostics and tests.</summary>
    public static List<VpxPrimitiveInfo> ListPrimitives(string vpxPath)
    {
        var t = Read(vpxPath, meshes: true);
        return t
            .Prims.Select(p =>
            {
                var (min, max) = Bounds(p, t);
                return new VpxPrimitiveInfo(
                    p.Name,
                    p.Visible,
                    p.Use3DMesh,
                    p.NumVerts,
                    p.Image,
                    p.Material,
                    p.Group,
                    min,
                    max
                );
            })
            .ToList();
    }

    /// <summary>The table's VPX walls with visible sides (as <see cref="ReadWall"/> builds them), for diagnostics and tests.</summary>
    public static List<VpxPrimitiveInfo> ListWalls(string vpxPath)
    {
        var t = Read(vpxPath, meshes: true);
        return t
            .Walls.Select(p =>
            {
                var (min, max) = Bounds(p, t);
                return new VpxPrimitiveInfo(
                    p.Name,
                    p.Visible,
                    p.Use3DMesh,
                    p.NumVerts,
                    p.Image,
                    p.Material,
                    p.Group,
                    min,
                    max
                );
            })
            .ToList();
    }

    /// <summary>
    /// The table's cabinet in lobby space, or null when the table has no VR cabinet (a desktop-only table),
    /// so the lobby can fall back to its generic cabinet. Never throws.
    /// </summary>
    public static VpxCabinetModel? Load(string vpxPath, float frontZ = 0.7f)
    {
        try
        {
            var t = Read(vpxPath, meshes: true);
            var picked = Pick(t);
            if (picked.Count == 0)
            {
                return null;
            }

            // Pictures the script sets, before deciding which back boards are hidden (that looks at which have one).
            if (picked.Any(p => !p.IsWall && string.IsNullOrWhiteSpace(p.Image)))
            {
                var fromScript = ScriptImages(vpxPath);
                foreach (var p in picked)
                {
                    if (
                        !p.IsWall
                        && string.IsNullOrWhiteSpace(p.Image)
                        && fromScript.TryGetValue(p.Name, out var img)
                    )
                    {
                        p.Image = img;
                    }
                }
            }
            picked = DropHiddenBackBoards(picked, t);

            // Build every part in VPX space first (in metres, axes swapped), then anchor the whole cabinet.
            var parts = new List<(Prim P, float[] Pos, float[] Nrm, float[] Uv, int[] Idx)>();
            foreach (var p in picked)
            {
                var m = FullMatrix(p, t);
                var normalMatrix = Matrix4x4.Invert(m, out var inv) ? Matrix4x4.Transpose(inv) : m;
                var n = p.NumVerts;
                var pos = new float[n * 3];
                var nrm = new float[n * 3];
                var uv = new float[n * 2];
                // A back board whose art starts just above the playfield is stretched down to it (StretchesDown):
                // its heights map bottom..top onto 0..top, the texture following.
                (float Bottom, float Top)? stretch = null;
                if (StretchesDown(p, t))
                {
                    var (sa, sb) = Bounds(p, t);
                    stretch = (sa.Z, sb.Z);
                }
                for (var i = 0; i < n; i++)
                {
                    var o = i * 8;
                    var v = Vector3.Transform(
                        new Vector3(p.Verts![o], p.Verts[o + 1], p.Verts[o + 2]),
                        m
                    );
                    if (stretch is var (bottom, top))
                    {
                        v.Z = top - (top - v.Z) * top / (top - bottom);
                    }
                    var nn = Vector3.TransformNormal(
                        new Vector3(p.Verts[o + 3], p.Verts[o + 4], p.Verts[o + 5]),
                        normalMatrix
                    );
                    nn = nn.LengthSquared() > 1e-12f ? Vector3.Normalize(nn) : Vector3.UnitZ;
                    // VPX (x right, y towards the player, z up) → lobby (x right, y up, z towards the player).
                    pos[i * 3] = v.X * VpuToMetres;
                    pos[i * 3 + 1] = v.Z * VpuToMetres;
                    pos[i * 3 + 2] = v.Y * VpuToMetres;
                    nrm[i * 3] = nn.X;
                    nrm[i * 3 + 1] = nn.Z;
                    nrm[i * 3 + 2] = nn.Y;
                    uv[i * 2] = p.Verts[o + 6];
                    uv[i * 2 + 1] = p.Verts[o + 7];
                }
                // VPX's index order, as it is: after the axis swap it already gives Godot's front faces on the
                // outside (checked against the parts' own normals in SmokeTest). Flipping it, as this used to,
                // turned every cabinet inside out: the outer art was lit as if it faced inward (dark, hardly
                // touched by the lobby's lights) and the inner walls shaded oddly.
                var idx = (int[])p.Indices!.Clone();
                parts.Add((p, pos, nrm, uv, idx));
            }

            // VR cabinets are built in VPX's playfield space, where the playfield is level, so the cabinet as
            // read leans back by the playfield's slope: its back legs reach the floor and its front legs stop
            // 14-21 cm above it. Tip it forward until both pairs stand on the same floor.
            var tilt = LevelingTilt(parts.Select(x => x.Pos));
            var (sin, cos) = MathF.SinCos(tilt);
            Vector3 Level(Vector3 v) => new(v.X, v.Y * cos - v.Z * sin, v.Y * sin + v.Z * cos);
            if (tilt != 0)
            {
                foreach (var (_, pos, nrm, _, _) in parts)
                {
                    for (var i = 0; i < pos.Length; i += 3)
                    {
                        var v = Level(new Vector3(pos[i], pos[i + 1], pos[i + 2]));
                        pos[i] = v.X;
                        pos[i + 1] = v.Y;
                        pos[i + 2] = v.Z;
                        var n = Level(new Vector3(nrm[i], nrm[i + 1], nrm[i + 2]));
                        nrm[i] = n.X;
                        nrm[i + 1] = n.Y;
                        nrm[i + 2] = n.Z;
                    }
                }
            }

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var (_, pos, _, _, _) in parts)
            {
                for (var i = 0; i < pos.Length; i += 3)
                {
                    var v = new Vector3(pos[i], pos[i + 1], pos[i + 2]);
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }
            }
            // Floor at y = 0 (the bottom of the legs), centred across, front edge at frontZ.
            var shift = new Vector3(-(min.X + max.X) / 2, -min.Y, frontZ - max.Z);
            var outParts = parts
                .Select(x =>
                {
                    for (var i = 0; i < x.Pos.Length; i += 3)
                    {
                        x.Pos[i] += shift.X;
                        x.Pos[i + 1] += shift.Y;
                        x.Pos[i + 2] += shift.Z;
                    }
                    return new VpxCabinetPart
                    {
                        Name = x.P.Name,
                        Positions = x.Pos,
                        Normals = x.Nrm,
                        Uvs = x.Uv,
                        Indices = x.Idx,
                        Image = string.IsNullOrWhiteSpace(x.P.Image) ? null : x.P.Image,
                        Material =
                            x.P.Material != null
                            && t.Materials.TryGetValue(x.P.Material, out var mat)
                                ? mat
                                : null,
                        SelfLit = Math.Clamp(x.P.SelfLit, 0f, 1f),
                    };
                })
                .ToList();

            // Playfield rectangle at playfield height (z = 0 in playfield space), same mapping, except for its slope.
            // VPX shows a VR table inclined by the table's own playfield slope (its SLOP..SLPX range, which the
            // difficulty picks from), not by its legs: about two tables in three have legs drawn for only 3-4°, so a
            // playfield tipped with its cabinet sloped half as much as in VPX and looked sunk at the back. The
            // cabinet still stands on its legs; the playfield turns about its own centre to the table's slope.
            var tableSlope = (t.SlopeMin + t.SlopeMax) / 2 * MathF.PI / 180;
            var slope = tableSlope is > 1f * MathF.PI / 180 and < 15f * MathF.PI / 180
                ? tableSlope
                : tilt;
            var pfCentre = new Vector3(
                (t.Left + t.Right) / 2 * VpuToMetres,
                0,
                (t.Top + t.Bottom) / 2 * VpuToMetres
            );
            var (sinS, cosS) = MathF.SinCos(slope);
            Vector3 Pf(float x, float y)
            {
                var v = new Vector3(x * VpuToMetres, 0, y * VpuToMetres) - pfCentre;
                return Level(pfCentre)
                    + new Vector3(v.X, v.Y * cosS - v.Z * sinS, v.Y * sinS + v.Z * cosS)
                    + shift;
            }
            return new VpxCabinetModel
            {
                Parts = outParts,
                PlayfieldCorners =
                [
                    Pf(t.Left, t.Top),
                    Pf(t.Right, t.Top),
                    Pf(t.Right, t.Bottom),
                    Pf(t.Left, t.Bottom),
                ],
                Tilt = tilt,
                FrontZ = frontZ,
                Min = min + shift,
                Max = max + shift,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// How far to tip a cabinet forward (radians, about the x axis) so the lowest point near its front (the
    /// front legs) and the lowest near its back (the back legs) are at the same height. Zero when it already
    /// stands level, or when the answer isn't a plausible playfield slope (a cabinet without front legs, say).
    /// </summary>
    public static float LevelingTilt(IEnumerable<float[]> positions)
    {
        float zMin = float.MaxValue,
            zMax = float.MinValue;
        foreach (var pos in positions)
        {
            for (var i = 2; i < pos.Length; i += 3)
            {
                zMin = Math.Min(zMin, pos[i]);
                zMax = Math.Max(zMax, pos[i]);
            }
        }

        if (zMax <= zMin)
        {
            return 0;
        }

        var band = (zMax - zMin) * 0.2f;
        (float Y, float Z) back = (float.MaxValue, 0),
            front = (float.MaxValue, 0);
        foreach (var pos in positions)
        {
            for (var i = 0; i < pos.Length; i += 3)
            {
                var (y, z) = (pos[i + 1], pos[i + 2]);
                if (z <= zMin + band && y < back.Y)
                {
                    back = (y, z);
                }

                if (z >= zMax - band && y < front.Y)
                {
                    front = (y, z);
                }
            }
        }

        var tilt = MathF.Atan2(front.Y - back.Y, front.Z - back.Z);
        return tilt is > 0.5f * MathF.PI / 180 and < 12f * MathF.PI / 180 ? tilt : 0;
    }

    /// <summary>
    /// Cabinet parts: named like cabinet parts, with a mesh, and near the main cabinet body (so a stray
    /// "rail" on the far side of a VR room isn't pulled in). Hidden parts are included, since VR tables
    /// usually hide their cabinet in the file and show it from the script when VR starts.
    /// </summary>
    static List<Prim> Pick(TableData t)
    {
        var candidates = t
            .Prims.Where(p =>
                p.Use3DMesh
                && p.Verts != null
                && p.Indices != null
                && p.NumVerts > 0
                && LooksLikeCabinetPart(p.Name)
            )
            .ToList();
        // Many VR tables list their cabinet in a collection ("VRCab", "VR_Cabinet", "VRCabinet", "NewVRCab",
        // "VR_Table") that the script shows when VR starts. Then a hidden part outside it is for desktop or cabinet
        // mode, not VR (The Addams Family's speaker "Grill", wider than its backbox; Big Trouble's second cabinet
        // body). Parts the table shows anyway, and blades (some scripts show those by an option), always count.
        // Kept only if the cabinet body survives it, in case a collection lists part of a cabinet.
        var vrCab = t
            .Collections.Where(c => VrCabinetCollection.IsMatch(c.Key))
            .SelectMany(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (vrCab.Count > 0)
        {
            var inVr = candidates
                .Where(p =>
                    p.Visible
                    || vrCab.Contains(p.Name)
                    || p.Name.Contains("blade", StringComparison.OrdinalIgnoreCase)
                )
                .ToList();
            if (inVr.Any(p => CabinetBody.IsMatch(p.Name)))
            {
                candidates = inVr;
            }
        }
        // The body anchors the selection: "PinCab_Cabinet", "CabinetMain", "cab"… (the biggest such part).
        var body = candidates
            .Where(p => CabinetBody.IsMatch(p.Name))
            .OrderByDescending(p =>
            {
                var (a, b) = Bounds(p, t);
                return (b - a).Length();
            })
            .FirstOrDefault();
        if (body == null)
        {
            return [];
        }

        var (bmin, bmax) = Bounds(body, t);
        var margin = 0.6f / VpuToMetres; // 60 cm
        bmin -= new Vector3(margin, margin, margin * 2);
        bmax += new Vector3(margin, margin, margin * 3);
        // The playfield area (slightly inset), from just under the surface to well above it: toys, posts
        // and bolts live there. Only clearly-cabinet parts (apron, blades, PinCab_*) may sit inside it.
        var inset = 20f;
        var picked = candidates
            .Where(p =>
            {
                var (a, b) = Bounds(p, t);
                var nearBody =
                    a.X >= bmin.X
                    && b.X <= bmax.X
                    && a.Y >= bmin.Y
                    && b.Y <= bmax.Y
                    && a.Z >= bmin.Z
                    && b.Z <= bmax.Z;
                if (!nearBody)
                {
                    return false;
                }

                var onPlayfield =
                    a.X >= t.Left + inset
                    && b.X <= t.Right - inset
                    && a.Y >= t.Top + inset
                    && b.Y <= t.Bottom - inset
                    && a.Z >= -60f
                    && b.Z <= 400f;
                return !onPlayfield || StrongCabinetName.IsMatch(p.Name);
            })
            .ToList();
        var have = new HashSet<Prim>(picked);
        var byPlace = ByPlace(t, body, have, vrCab);
        have.UnionWith(byPlace);
        picked.AddRange(byPlace);
        var walls = PlayfieldWalls(t).Where(w => !have.Contains(w)).ToList();
        have.UnionWith(walls);
        picked.AddRange(walls);
        if (!picked.Any(p => IsBackBoard(p, t)) && BakedBackBoard(t, have) is { } baked)
        {
            picked.Add(baked);
        }
        return DropNearCopies(DropUnlitCopies(picked), t);
    }

    /// <summary>
    /// Near-identical copies of one part, however they were picked: the same mesh size, picture and material within
    /// a few units of each other (AC/DC's "TAFCabFront" and "TAFCabFront1", about 2 units apart). VPX draws both and
    /// one hides the other; drawn in the lobby they flicker through each other. The first is kept.
    /// </summary>
    static List<Prim> DropNearCopies(List<Prim> picked, TableData t)
    {
        var kept = new List<Prim>();
        var keptBounds = new List<(Vector3 A, Vector3 B)>();
        foreach (var p in picked)
        {
            var (a, b) = Bounds(p, t);
            var copy = false;
            for (var k = 0; k < kept.Count && !copy; k++)
            {
                var o = kept[k];
                copy =
                    o.NumVerts == p.NumVerts
                    && o.NumIndices == p.NumIndices
                    && string.Equals(o.Image, p.Image, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(o.Material, p.Material, StringComparison.OrdinalIgnoreCase)
                    && Vector3.Distance(keptBounds[k].A, a) < 5
                    && Vector3.Distance(keptBounds[k].B, b) < 5;
            }
            if (!copy)
            {
                kept.Add(p);
                keptBounds.Add((a, b));
            }
        }
        return kept;
    }

    /// <summary>
    /// Back boards drawn in the same place (run once the script's pictures are filled in, since it looks at which
    /// boards have one):
    /// - an untextured board that a textured one covers: within about 2 cm of depth, and no wider, lower or taller
    ///   (one that reaches past the art, like JAWS's black "Wall007" below "Wall15", fills a gap and stays);
    /// - a board shut wholly inside another one (inside its depth, and no wider or taller), which VPX hides and the
    ///   lobby would show flickering through it.
    /// A board that reaches out of the other one stays: Elvira's "background" stands just behind "Pincab_Backwall"
    /// but rises above it, and that top part shows in VPX too, so both are drawn.
    /// </summary>
    /// <summary>
    /// A textured back board whose art starts a little above the playfield (up to about 3 cm: JAWS's "Wall15" at
    /// 2.7 cm), drawn stretched down to it. In VPX the toys at the back of the playfield stand in front of that strip;
    /// the lobby's playfield is a flat picture that can't hide anything, so the strip showed as an inch-high band
    /// (a black board behind, or a gap). Stretched, the art is about a sixth taller and meets the playfield.
    /// </summary>
    static bool StretchesDown(Prim p, TableData t)
    {
        if (string.IsNullOrWhiteSpace(p.Image) || !IsBackBoard(p, t))
        {
            return false;
        }
        var (a, _) = Bounds(p, t);
        return a.Z is > 1 and <= 60;
    }

    static List<Prim> DropHiddenBackBoards(List<Prim> picked, TableData t)
    {
        var boards = picked.Where(p => IsBackBoard(p, t)).ToList();
        if (boards.Count < 2)
        {
            return picked;
        }
        var drop = new HashSet<Prim>();
        foreach (var p in boards)
        {
            var (a, b) = Bounds(p, t);
            if (
                string.IsNullOrWhiteSpace(p.Image)
                && boards.Any(o =>
                {
                    if (o == p || string.IsNullOrWhiteSpace(o.Image))
                    {
                        return false;
                    }
                    // Covered by it: as near (about 2 cm), and no wider, lower or taller (a board whose art is
                    // stretched down to the playfield, StretchesDown, counts from there). JAWS's black "Wall007"
                    // under "Wall15"'s art goes once that art reaches the playfield.
                    var (oa, ob) = Bounds(o, t);
                    if (StretchesDown(o, t))
                    {
                        oa.Z = 0;
                    }
                    return MathF.Abs((a.Y + b.Y) / 2 - (oa.Y + ob.Y) / 2) < 40
                        && a.X >= oa.X - 5
                        && b.X <= ob.X + 5
                        && a.Z >= oa.Z - 5
                        && b.Z <= ob.Z + 5;
                })
            )
            {
                drop.Add(p);
                continue;
            }
            foreach (var o in boards)
            {
                if (o == p || drop.Contains(o))
                {
                    continue;
                }
                var (oa, ob) = Bounds(o, t);
                // Strictly inside the other's depth, and no wider or taller than it.
                if (
                    a.Y > oa.Y + 0.5f
                    && b.Y < ob.Y - 0.5f
                    && a.X >= oa.X - 5
                    && b.X <= ob.X + 5
                    && a.Z >= oa.Z - 5
                    && b.Z <= ob.Z + 5
                )
                {
                    drop.Add(p);
                    break;
                }
            }
        }
        return drop.Count == 0 ? picked : picked.Where(p => !drop.Contains(p)).ToList();
    }

    /// <summary>
    /// Cabinet parts found by where they sit rather than by name: anything shown that lies within the cabinet body's
    /// own outline (10 cm round it) and isn't on the playfield. Cabinet builders name parts every way ("TAFBoxSupport",
    /// "Primitive71vr"), so a list of names keeps missing some, and the cabinet shows gaps. Names still leave things
    /// out (room parts, glass, lightmaps, unlit copies). A part counts as playfield, and stays out, when most of it
    /// lies over the playfield inside the rails, from 15 cm below the surface up: the lobby shows the playfield as
    /// video, and VPW's baked playfield meshes reach past the rails. Only parts shown in the file or listed in the
    /// table's VR cabinet collection count, so a table that hides its whole cabinet and shows it from the script gains
    /// nothing here. Very detailed parts (over 10,000 vertices, JAWS's rail screws) are left out, and of copies in the
    /// same place that a script swaps between (AFM's "VR_Key" and "VR_KeyShake"), only the first is kept.
    /// </summary>
    static List<Prim> ByPlace(TableData t, Prim body, HashSet<Prim> picked, HashSet<string> vrCab)
    {
        const int MaxVertices = 10_000;
        var found = new List<Prim>();
        var (bmin, bmax) = Bounds(body, t);
        var margin = 0.1f / VpuToMetres;
        bmin -= new Vector3(margin);
        bmax += new Vector3(margin);
        const float edge = 1f,
            below = -300f;
        foreach (var p in t.Prims)
        {
            if (
                picked.Contains(p)
                || !p.Use3DMesh
                || p.Verts == null
                || p.Indices == null
                || p.NumVerts is 0 or > MaxVertices
                || !(p.Visible || vrCab.Contains(p.Name))
                || RoomName.IsMatch(p.Name)
                || OffVariant.IsMatch(p.Name)
                || NotGeometry.IsMatch(p.Name)
                || IsPlayfieldGlass(p.Name)
                || NotCabinetByPlace.IsMatch(p.Name)
            )
            {
                continue;
            }

            var (a, b) = Bounds(p, t);
            if (
                a.X < bmin.X
                || b.X > bmax.X
                || a.Y < bmin.Y
                || b.Y > bmax.Y
                || a.Z < bmin.Z
                || b.Z > bmax.Z
            )
            {
                continue;
            }

            // On the playfield: within its rectangle (the rails' screws, gates and decals sit right on its edge), or
            // mostly over it (meshes that reach past it).
            if (a.X >= t.Left && b.X <= t.Right && a.Y >= t.Top && b.Y <= t.Bottom && a.Z > below)
            {
                continue;
            }

            var m = FullMatrix(p, t);
            var over = 0;
            for (var i = 0; i < p.NumVerts; i++)
            {
                var v = Vector3.Transform(
                    new Vector3(p.Verts[i * 8], p.Verts[i * 8 + 1], p.Verts[i * 8 + 2]),
                    m
                );
                if (
                    v.X >= t.Left - edge
                    && v.X <= t.Right + edge
                    && v.Y >= t.Top - edge
                    && v.Y <= t.Bottom + edge
                    && v.Z > below
                )
                {
                    over++;
                }
            }
            if (over * 2 >= p.NumVerts)
            {
                continue;
            }

            // A copy of one already found or picked: the same mesh size in the same place, or a variant named after it
            // in the same place ("VR_KeyShake" for "VR_Key"). Of a variant pair, the plain name stays.
            bool Overlaps(Prim o)
            {
                var (oa, ob) = Bounds(o, t);
                return a.X <= ob.X
                    && b.X >= oa.X
                    && a.Y <= ob.Y
                    && b.Y >= oa.Y
                    && a.Z <= ob.Z
                    && b.Z >= oa.Z;
            }
            bool SameMesh(Prim o)
            {
                var (oa, ob) = Bounds(o, t);
                return o.NumVerts == p.NumVerts
                    && Vector3.Distance(oa, a) < 5
                    && Vector3.Distance(ob, b) < 5;
            }
            static bool Extends(string longer, string shorter) =>
                longer.Length > shorter.Length
                && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
            if (
                found
                    .Concat(picked)
                    .Any(o => SameMesh(o) || (Extends(p.Name, o.Name) && Overlaps(o)))
            )
            {
                continue;
            }
            found.RemoveAll(o => Extends(o.Name, p.Name) && Overlaps(o));
            found.Add(p);
        }
        return found;
    }

    /// <summary>A board across the back of the playfield: reaching both sides, standing at its back edge, about rail height.</summary>
    static bool IsBackBoard(Prim p, TableData t)
    {
        var (a, b) = Bounds(p, t);
        return a.X <= t.Left + 40
            && b.X >= t.Right - 40
            && b.Y <= t.Top + (t.Bottom - t.Top) * 0.05f
            && a.Y >= t.Top - 150
            && a.Z <= 100 // standing on the playfield, not up in the backbox (a backglass)
            && b.Z >= 120;
    }

    /// <summary>
    /// The back board out of a VPW baked mesh: VPW tables (Metallica, Fish Tales) bake the board behind the playfield
    /// into "BM_Parts" with the whole playfield, so there's no part of its own to pick. Its triangles are the upright
    /// ones across the playfield (facing either way along it) at its back edge, from just under its surface up; they
    /// become a part of their own, in the baked mesh's texture. The flat playfield never qualifies, being level.
    /// Null when no baked mesh has them across the playfield's width.
    /// </summary>
    static Prim? BakedBackBoard(TableData t, HashSet<Prim> picked)
    {
        Prim? best = null;
        var bestCount = 0;
        foreach (var p in t.Prims)
        {
            if (
                !p.Name.StartsWith("BM_", StringComparison.OrdinalIgnoreCase)
                || !p.Visible
                || picked.Contains(p)
                || p.Verts == null
                || p.Indices == null
                || p.NumVerts == 0
                || SpaceOf(p.Group, t) != 0
            )
            {
                continue;
            }

            var m = FullMatrix(p, t);
            var w = new Vector3[p.NumVerts];
            for (var i = 0; i < p.NumVerts; i++)
            {
                w[i] = Vector3.Transform(
                    new Vector3(p.Verts[i * 8], p.Verts[i * 8 + 1], p.Verts[i * 8 + 2]),
                    m
                );
            }

            bool InBand(Vector3 v) =>
                v.Y >= t.Top - 60
                && v.Y <= t.Top + 40
                && v.Z is >= -10 and <= 500
                && v.X >= t.Left - 60
                && v.X <= t.Right + 60;
            var tris = new List<int>();
            float minX = float.MaxValue,
                maxX = float.MinValue;
            for (var i = 0; i + 2 < p.Indices.Length; i += 3)
            {
                int i0 = p.Indices[i],
                    i1 = p.Indices[i + 1],
                    i2 = p.Indices[i + 2];
                if (!InBand(w[i0]) || !InBand(w[i1]) || !InBand(w[i2]))
                {
                    continue;
                }
                var n = Vector3.Cross(w[i1] - w[i0], w[i2] - w[i0]);
                if (n.LengthSquared() < 1e-6f)
                {
                    continue;
                }
                n = Vector3.Normalize(n);
                if (MathF.Abs(n.Y) < 0.7f) // upright and facing along the playfield, not a side or a top
                {
                    continue;
                }
                tris.Add(i0);
                tris.Add(i1);
                tris.Add(i2);
                minX = MathF.Min(minX, MathF.Min(w[i0].X, MathF.Min(w[i1].X, w[i2].X)));
                maxX = MathF.Max(maxX, MathF.Max(w[i0].X, MathF.Max(w[i1].X, w[i2].X)));
            }
            if (tris.Count / 3 > bestCount && minX <= t.Left + 60 && maxX >= t.Right - 60)
            {
                // A part of its own: the chosen triangles and just the vertices they use, in the baked mesh's space.
                var map = new Dictionary<int, int>();
                var verts = new List<float>();
                var idx = new int[tris.Count];
                for (var k = 0; k < tris.Count; k++)
                {
                    if (!map.TryGetValue(tris[k], out var ni))
                    {
                        ni = map.Count;
                        map[tris[k]] = ni;
                        verts.AddRange(p.Verts.AsSpan(tris[k] * 8, 8));
                    }
                    idx[k] = ni;
                }
                var part = new Prim
                {
                    Name = p.Name + "_BackBoard",
                    Pos = p.Pos,
                    Size = p.Size,
                    Image = p.Image,
                    Material = p.Material,
                    Group = p.Group,
                    Visible = true,
                    Use3DMesh = true,
                    SelfLit = p.SelfLit,
                    NumVerts = map.Count,
                    NumIndices = idx.Length,
                    Verts = verts.ToArray(),
                    Indices = idx,
                };
                Array.Copy(p.RotTra, part.RotTra, p.RotTra.Length);
                best = part;
                bestCount = tris.Count / 3;
            }
        }
        return best;
    }

    // Found near the cabinet but not part of it: flat shadow decals ("BallShadow11", "IceManRampShadow") and the
    // flasher domes at the back of the playfield ("Flasherbase5", "Flasherlit17"), which the playfield video shows.
    static readonly Regex NotCabinetByPlace = new(@"shadow|flasher", RegexOptions.IgnoreCase);

    // "BladeLeftDTOff", "ApronRailOff", "sidewoodtrimOff": an unlit copy, named by a trailing Off.
    static readonly Regex TrailingOff = new(@"^(?<stem>.+?)_?(Off|OFF|off)\d*$");

    /// <summary>
    /// Tables that switch their GI keep each part twice, lit and unlit, in the same place. The names
    /// <see cref="OffVariant"/> knows are left out from the start; others ("BladeLeftDTOff" beside "BladeLeftDTON",
    /// "ApronRailOff" beside "ApronRailLeft" and "ApronRailRight") are dropped here, when a part whose name starts
    /// the same way was picked too. Drawn together, the two copies flicker through each other (The Addams Family).
    /// An unlit part with no lit sibling stays: it's the only one there is.
    /// </summary>
    static List<Prim> DropUnlitCopies(List<Prim> picked) =>
        picked
            .Where(p =>
            {
                if (TrailingOff.Match(p.Name) is not { Success: true } m)
                {
                    return true;
                }

                var stem = m.Groups["stem"].Value;
                if (stem.EndsWith("DT", StringComparison.OrdinalIgnoreCase))
                {
                    stem = stem[..^2]; // "…DTOff" / "…DTON": desktop variants
                }

                return !picked.Any(o =>
                    o != p
                    && !TrailingOff.IsMatch(o.Name)
                    && o.Name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                );
            })
            .ToList();

    /// <summary>
    /// The table's own walls around the playfield (the wooden or printed side boards and the back): in VPX they're
    /// part of the table, not the VR cabinet, and they go by any name ("backwall", "OutBox", "sidewallsgioff",
    /// "Primitive58"…), so they're found by shape: a simple visible part that runs along most of the playfield,
    /// reaching both its sides, about as high as a cabinet's rails. Without them the lobby showed open gaps around
    /// the playfield, into the inside of the cabinet's shell. Merged baked meshes (VPW's BM_Parts) are far too
    /// detailed to count; of look-alike copies (GI on / off), the first is kept.
    /// </summary>
    static List<Prim> PlayfieldWalls(TableData t)
    {
        var walls = new List<Prim>();
        var seen = new HashSet<(int, int, int)>();
        foreach (var p in t.Prims.Concat(t.Walls))
        {
            if (
                !p.Visible
                || !p.Use3DMesh
                || p.Verts == null
                || p.Indices == null
                || p.NumVerts is 0 or > 2000
            )
            {
                continue;
            }

            if (NotGeometry.IsMatch(p.Name) || IsPlayfieldGlass(p.Name) || SpaceOf(p.Group, t) != 0)
            {
                continue;
            }

            var (a, b) = Bounds(p, t);
            var along = b.Y - a.Y >= (t.Bottom - t.Top) * 0.8f;
            var bothSides =
                a.X <= t.Left + 40
                && b.X >= t.Right - 40
                && a.X >= t.Left - 150
                && b.X <= t.Right + 150;
            // From a little under the playfield (AFM's "REARWALL" starts 70 below it) to about rail height.
            var railHigh = a.Z >= -100 && b.Z is >= 120 and <= 500;
            // The board across the back of the playfield (AC/DC's "BackboardWallOff", a VPX wall): it doesn't run
            // along, it stands at the back edge.
            var atBack = b.Y <= t.Top + (t.Bottom - t.Top) * 0.05f && a.Y >= t.Top - 150;
            // The low wooden boards on the playfield's edges, about an inch tall (JAWS's "Wall98", Medieval Madness's
            // "Wall271"/"Wall272", AC/DC's "sidewood", AFM's "WOODRAILS"), often one per side: they cover the dark
            // edge of the playfield picture, which otherwise showed as an inch-wide gap round the playfield.
            var oneSide =
                (a.X <= t.Left + 40 || b.X >= t.Right - 40)
                && a.X >= t.Left - 150
                && b.X <= t.Right + 150;
            // Opaque only: the clear plastic guards along the sides (JAWS's "Primitive031"/"Primitive051", 70% opaque
            // "Plastic Ramps" with no picture) stand there too, and drew as solid white bars.
            var seeThrough =
                p.Material != null
                && t.Materials.TryGetValue(p.Material, out var pm)
                && pm.OpacityActive
                && pm.Opacity < 0.95f;
            var lowBoard = along && oneSide && a.Z >= -60 && b.Z is >= 30 and < 120 && !seeThrough;
            if (!lowBoard && (!(along || atBack) || !bothSides || !railHigh))
            {
                continue;
            }
            // Walls keep to the edges; metal guide walls and the like run across the middle of the playfield too.
            // Judged by what the part covers seen from above (its triangles; a mesh may carry unused vertices).
            const int W = 48,
                H = 96;
            var m = FullMatrix(p, t);
            var px = new Vector3[p.NumVerts];
            for (var i = 0; i < p.NumVerts; i++)
            {
                var v = Vector3.Transform(
                    new Vector3(p.Verts[i * 8], p.Verts[i * 8 + 1], p.Verts[i * 8 + 2]),
                    m
                );
                px[i] = new Vector3(
                    (v.X - t.Left) / (t.Right - t.Left) * W,
                    (v.Y - t.Top) / (t.Bottom - t.Top) * H,
                    MathF.Max(v.Z, 1)
                );
            }
            var cover = new float[W * H];
            for (var i = 0; i + 2 < p.Indices.Length; i += 3)
            {
                Raster(
                    cover,
                    null,
                    W,
                    H,
                    px[p.Indices[i]],
                    px[p.Indices[i + 1]],
                    px[p.Indices[i + 2]]
                );
            }

            int covered = 0,
                middle = 0;
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    if (cover[y * W + x] > 0)
                    {
                        covered++;
                        // Inside the outer 15% of the width (the plunger lane's divider, part of AC/DC's "sidewood",
                        // stands at 88-91%) and 10% of the length.
                        if (
                            x >= W * 15 / 100
                            && x < W - W * 15 / 100
                            && y >= H / 10
                            && y < H - H / 10
                        )
                        {
                            middle++;
                        }
                    }
                }
            }

            if (covered == 0 || middle > covered / 10)
            {
                continue;
            }

            if (seen.Add((p.NumVerts, (int)a.Y, (int)b.Z)))
            {
                walls.Add(p);
            }
        }
        return walls;
    }

    /// <summary>
    /// Pictures the table's script gives its parts ("LeftCab.image = "sidecabL""): some tables leave a part's image
    /// empty in the file and set it when the table starts, often by an option. AC/DC's inner side panels drew plain
    /// white without this. Only plain assignments of one quoted name count (not "Page" &amp; n), outside comments.
    /// The first one in the script counts; that's often the default option's, but it can be a light-show frame or
    /// another option's, so this is a best guess for parts that have no image of their own at all. Parts the script
    /// can hide anywhere (".visible = 0") are left out, which also skips some parts hidden only on desktop.
    /// </summary>
    static Dictionary<string, string> ScriptImages(string vpxPath)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var script = WithoutComments(RomAudio.Script(vpxPath));
            // A part the script can hide is optional dressing, its picture one option's look (24's DMD decal,
            // hidden by default): those keep the file's image.
            var hideable = ScriptHides
                .Matches(script)
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in ScriptImage.Matches(script))
            {
                if (!hideable.Contains(m.Groups[1].Value))
                {
                    found.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
                }
            }
        }
        catch
        {
            // no script to read: the parts keep the file's images
        }
        return found;
    }

    /// <summary>The script with its comments blanked: "'" outside a string to the end of the line, and Rem lines.</summary>
    static string WithoutComments(string script)
    {
        var sb = new StringBuilder(script.Length);
        foreach (var line in script.Split('\n'))
        {
            if (Regex.IsMatch(line, @"^\s*rem\b", RegexOptions.IgnoreCase))
            {
                sb.Append('\n');
                continue;
            }
            var inString = false;
            var end = line.Length;
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == '"')
                {
                    inString = !inString;
                }
                else if (line[i] == '\'' && !inString)
                {
                    end = i;
                    break;
                }
            }
            sb.Append(line, 0, end).Append('\n');
        }
        return sb.ToString();
    }

    // "Part.image = "name"" as a whole statement: the quoted name ends it (or a ":" starts the next one).
    static readonly Regex ScriptImage = new(
        @"\b([A-Za-z_]\w*)\.image\s*=\s*""([^""\r\n]+)""[ \t]*(?=:|\r?$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline
    );

    static readonly Regex ScriptHides = new(
        @"\b([A-Za-z_]\w*)\.(?:side)?visible\s*=\s*(0|false)\b", // a wall's sides hide with ".SideVisible"
        RegexOptions.IgnoreCase
    );

    static readonly Regex CabinetBody = new(
        @"cabinet|^cab$|cabmain|cab_?body",
        RegexOptions.IgnoreCase
    );
    static readonly Regex VrCabinetCollection = new(
        @"^(new)?_?vr_?(cab|cabinet|table)\d*$",
        RegexOptions.IgnoreCase
    );

    static readonly Regex StrongCabinetName = new(
        @"pincab|cabinet|^cab|apron|blade|siderail|side_?rail|lockdown|lockbar",
        RegexOptions.IgnoreCase
    );

    static (Vector3 Min, Vector3 Max) Bounds(Prim p, TableData t)
    {
        if (p.Verts == null || p.NumVerts == 0)
        {
            return (p.Pos, p.Pos);
        }

        var m = FullMatrix(p, t);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        // Every vertex for small meshes; a sample for big ones (bounds only need to be close).
        var step = Math.Max(1, p.NumVerts / 4000);
        for (var i = 0; i < p.NumVerts; i += step)
        {
            var v = Vector3.Transform(
                new Vector3(p.Verts[i * 8], p.Verts[i * 8 + 1], p.Verts[i * 8 + 2]),
                m
            );
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }
        return (min, max);
    }

    /// <summary>
    /// VPX's Primitive::RecalculateMatrices with row vectors (v' = v·M), exactly as VPX composes it:
    /// scale, then translate by RotAndTra[3..5], rotate Z/Y/X by [2],[1],[0], rotate Z/Y/X by [8],[7],[6],
    /// then translate to the position. Parts in a cabinet/room-space group get VPX's playfield→cabinet offset.
    /// </summary>
    static Matrix4x4 FullMatrix(Prim p, TableData t)
    {
        static float Rad(float deg) => deg * MathF.PI / 180f;
        var r = p.RotTra;
        var rt =
            Matrix4x4.CreateTranslation(r[3], r[4], r[5])
            * Matrix4x4.CreateRotationZ(Rad(r[2]))
            * Matrix4x4.CreateRotationY(Rad(r[1]))
            * Matrix4x4.CreateRotationX(Rad(r[0]))
            * Matrix4x4.CreateRotationZ(Rad(r[8]))
            * Matrix4x4.CreateRotationY(Rad(r[7]))
            * Matrix4x4.CreateRotationX(Rad(r[6]));
        var m = Matrix4x4.CreateScale(p.Size) * rt * Matrix4x4.CreateTranslation(p.Pos);
        if (SpaceOf(p.Group, t) != 0)
        {
            m *= Matrix4x4.CreateTranslation(0, 0, -(t.GroundToLockbar - t.GlassBottom));
        }

        return m;
    }

    /// <summary>The part group's space reference (0 = playfield), following SR_INHERIT (4) up the parents.</summary>
    static int SpaceOf(string? group, TableData t)
    {
        for (var depth = 0; group != null && depth < 32; depth++)
        {
            if (!t.Groups.TryGetValue(group, out var g))
            {
                return 0;
            }

            if (g.Space != 4)
            {
                return g.Space;
            }

            group = g.Parent;
        }
        return 0;
    }

    static TableData Read(string vpxPath, bool meshes)
    {
        var t = new TableData();
        using var root = RootStorage.OpenRead(vpxPath);
        var game = root.OpenStorage("GameStg");

        if (game.TryOpenStream("GameData", out var gd) && gd != null)
        {
            using (gd)
            {
                ReadGameData(ReadAll(gd), t);
            }
        }

        for (var i = 0; i < 100_000; i++)
        {
            if (!game.TryOpenStream("GameItem" + i, out var s) || s == null)
            {
                break;
            }

            using (s)
            {
                var head = new byte[4];
                if (s.Read(head, 0, 4) < 4)
                {
                    continue;
                }

                var type = BitConverter.ToInt32(head, 0);
                if (
                    type != ItemPrimitive
                    && type != ItemPartGroup
                    && !(type == ItemSurface && meshes)
                )
                {
                    continue;
                }

                s.Position = 0;
                var buf = ReadAll(s);
                if (type == ItemPrimitive)
                {
                    t.Prims.Add(ReadPrimitive(buf, meshes));
                }
                else if (type == ItemSurface)
                {
                    // One damaged wall mustn't cost the table its cabinet: it's left out on its own.
                    try
                    {
                        if (ReadWall(buf) is { } wall)
                        {
                            t.Walls.Add(wall);
                        }
                    }
                    catch (Exception)
                    {
                        // skip this wall
                    }
                }
                else
                {
                    ReadPartGroup(buf, t);
                }
            }
        }
        // Collections live in streams of their own: a NAME and one ITEM per member.
        for (var i = 0; i < 10_000; i++)
        {
            if (!game.TryOpenStream("Collection" + i, out var s) || s == null)
            {
                break;
            }

            using (s)
            {
                string? name = null;
                var items = new List<string>();
                foreach (var (tag, d) in Records(ReadAll(s), 0))
                {
                    if (tag == "NAME")
                    {
                        name = WStr(d);
                    }
                    else if (tag == "ITEM" && WStr(d) is { Length: > 0 } item)
                    {
                        items.Add(item);
                    }
                }
                if (name != null)
                {
                    t.Collections[name] = items;
                }
            }
        }
        return t;
    }

    static void ReadGameData(byte[] buf, TableData t)
    {
        var count = 0;
        byte[]? legacy = null;
        foreach (var (tag, d) in Records(buf, 0))
        {
            switch (tag)
            {
                case "LEFT":
                    t.Left = F(d);
                    break;
                case "TOPX":
                    t.Top = F(d);
                    break;
                case "RGHT":
                    t.Right = F(d);
                    break;
                case "BOTM":
                    t.Bottom = F(d);
                    break;
                case "GLAB":
                    t.GlassBottom = F(d);
                    break;
                case "CLBH":
                    t.GroundToLockbar = F(d);
                    break;
                case "SLOP":
                    t.SlopeMin = F(d);
                    break;
                case "SLPX":
                    t.SlopeMax = F(d);
                    break;
                case "MASI":
                    count = I(d);
                    break;
                case "MATE":
                    legacy = d;
                    break;
                case "MATR": // 10.8+: one nested object per material
                    if (ReadMaterial(d) is { } m)
                    {
                        t.Materials[m.Name] = m;
                    }

                    break;
            }
        }
        // Older tables: a packed array of 76-byte SaveMaterial structs (MSVC layout).
        if (t.Materials.Count == 0 && legacy != null && count > 0 && legacy.Length >= count * 76)
        {
            for (var i = 0; i < count; i++)
            {
                var o = i * 76;
                var name = Encoding.Latin1.GetString(legacy, o, 32).Split('\0')[0];
                t.Materials[name] = new VpxMaterial(
                    name,
                    BaseColour: BitConverter.ToUInt32(legacy, o + 32),
                    IsMetal: legacy[o + 48] != 0,
                    Roughness: BitConverter.ToSingle(legacy, o + 52),
                    Opacity: BitConverter.ToSingle(legacy, o + 68),
                    OpacityActive: (legacy[o + 72] & 1) != 0
                );
            }
        }
    }

    static VpxMaterial? ReadMaterial(byte[] d)
    {
        string? name = null;
        uint col = 0xFFFFFF;
        int type = 0;
        float rough = 0.5f,
            opac = 1f;
        var opActive = false;
        foreach (var (tag, x) in Records(d, 0))
        {
            switch (tag)
            {
                case "NAME":
                    name = Str(x);
                    break;
                case "TYPE":
                    type = I(x);
                    break;
                case "ROUG":
                    rough = F(x);
                    break;
                case "OPAC":
                    opac = F(x);
                    break;
                case "BASE":
                    col = (uint)I(x);
                    break;
                case "EOPA":
                    opActive = I(x) != 0;
                    break;
            }
        }
        return name == null ? null : new VpxMaterial(name, col, type == 1, rough, opac, opActive);
    }

    static void ReadPartGroup(byte[] buf, TableData t)
    {
        string? name = null,
            parent = null;
        var space = 0;
        foreach (var (tag, d) in Records(buf, 4))
        {
            switch (tag)
            {
                case "NAME":
                    name = WStr(d);
                    break;
                case "SPRF":
                    space = I(d);
                    break;
                case "GRUP":
                    parent = Str(d);
                    break;
            }
        }
        if (name != null)
        {
            t.Groups[name] = (space, string.IsNullOrEmpty(parent) ? null : parent);
        }
    }

    /// <summary>
    /// A VPX wall ("surface") as a mesh of its sides, or null when its sides aren't drawn or it can't be built. Walls
    /// are flat outlines (drag points) raised from a bottom to a top height; some tables build the boards around
    /// the playfield this way rather than as primitives (AC/DC's backboard behind the playfield). Only straight
    /// walls: a smoothed (curved) one is left out rather than drawn with corners. No top: the boards this is for
    /// are thin, and their tops are mostly hidden. Side texture coordinates follow VPX's
    /// IHaveDragPoints::GetTextureCoords (fixed coordinates at points without auto-texture, by length between).
    /// </summary>
    static Prim? ReadWall(byte[] buf)
    {
        string? name = null,
            image = null,
            material = null;
        float bottom = 0,
            top = 50;
        string? group = null;
        var sideVisible = true;
        var pts = new List<(Vector2 P, bool Smooth, bool Auto, float Tex)>();
        var inPoint = false;
        var pos = 4;
        while (pos + 8 <= buf.Length)
        {
            var len = BitConverter.ToInt32(buf, pos);
            if (len < 4 || len > buf.Length - pos - 4) // written so a huge length can't overflow
            {
                break;
            }

            var tag = Encoding.ASCII.GetString(buf, pos + 4, 4);
            var d = buf.AsSpan(pos + 8, len - 4).ToArray();
            pos += 4 + len;
            if (tag == "DPNT")
            {
                pts.Add((Vector2.Zero, false, true, 0));
                inPoint = true;
                continue;
            }
            if (inPoint)
            {
                var (p, smooth, auto, tex) = pts[^1];
                switch (tag)
                {
                    case "VCEN" when d.Length >= 8:
                        p = new Vector2(BitConverter.ToSingle(d, 0), BitConverter.ToSingle(d, 4));
                        break;
                    case "SMTH":
                        smooth = I(d) != 0;
                        break;
                    case "ATEX":
                        auto = I(d) != 0;
                        break;
                    case "TEXC":
                        tex = F(d);
                        break;
                    case "ENDB":
                        inPoint = false;
                        break;
                }
                pts[^1] = (p, smooth, auto, tex);
                continue;
            }
            switch (tag)
            {
                case "NAME":
                    name = WStr(d);
                    break;
                case "SIMG":
                    image = Str(d);
                    break;
                case "SIMA":
                    material = Str(d);
                    break;
                case "HTBT":
                    bottom = F(d);
                    break;
                case "HTTP":
                    top = F(d);
                    break;
                case "SVBL":
                    sideVisible = I(d) != 0;
                    break;
                case "GRUP":
                    group = Str(d);
                    break;
            }
        }

        var n = pts.Count;
        if (name == null || !sideVisible || n < 2 || top - bottom < 1 || pts.Any(x => x.Smooth))
        {
            return null;
        }

        // Texture coordinates along the outline (GetTextureCoords): spans between points with a fixed coordinate,
        // or 0..1 round the whole outline from the first point when none has one.
        var tc = new float[n];
        var fixedAt = Enumerable.Range(0, n).Where(i => !pts[i].Auto).ToList();
        var none = fixedAt.Count == 0;
        if (none)
        {
            fixedAt.Add(0);
        }
        for (var k = 0; k < fixedAt.Count; k++)
        {
            var start = fixedAt[k];
            var end = fixedAt[(k + 1) % fixedAt.Count];
            var t0 = none ? 0f : pts[start].Tex;
            var t1 = none ? 1f : pts[end].Tex;
            if (end <= start)
            {
                end += n;
            }
            var total = 0f;
            for (var l = start; l < end; l++)
            {
                total += Vector2.Distance(pts[l % n].P, pts[(l + 1) % n].P);
            }
            var part = 0f;
            for (var l = start; l < end; l++)
            {
                tc[l % n] = total > 0 ? t0 + (t1 - t0) * part / total : t0;
                part += Vector2.Distance(pts[l % n].P, pts[(l + 1) % n].P);
            }
        }

        // One quad per side (bottom, top, next top, next bottom, as VPX lays them out), facing out of the outline.
        // Which side is out follows from which way the outline runs (the sign of its area), which holds for concave
        // outlines too; walls running the other way get their triangles turned round to match, so Godot's front
        // faces are the outside, as for primitives.
        var area = 0f;
        for (var i = 0; i < n; i++)
        {
            var (p0, p1) = (pts[i].P, pts[(i + 1) % n].P);
            area += p0.X * p1.Y - p1.X * p0.Y;
        }
        var outward = area > 0 ? 1f : -1f;
        int[] quad = area > 0 ? [0, 2, 1, 0, 3, 2] : [0, 1, 2, 0, 2, 3];
        var verts = new float[n * 4 * 8];
        var idx = new int[n * 6];
        for (var i = 0; i < n; i++)
        {
            var a = pts[i].P;
            var b = pts[(i + 1) % n].P;
            var along = b - a;
            var normal =
                along.LengthSquared() > 1e-8f
                    ? outward * Vector2.Normalize(new Vector2(along.Y, -along.X))
                    : Vector2.UnitY;
            var corners = new[]
            {
                (a, bottom, tc[i], 1f),
                (a, top, tc[i], 0f),
                (b, top, tc[(i + 1) % n], 0f),
                (b, bottom, tc[(i + 1) % n], 1f),
            };
            for (var c = 0; c < 4; c++)
            {
                var (xy, z, u, v) = corners[c];
                var o = (i * 4 + c) * 8;
                verts[o] = xy.X;
                verts[o + 1] = xy.Y;
                verts[o + 2] = z;
                verts[o + 3] = normal.X;
                verts[o + 4] = normal.Y;
                verts[o + 5] = 0;
                verts[o + 6] = u;
                verts[o + 7] = v;
            }
            for (var q = 0; q < 6; q++)
            {
                idx[i * 6 + q] = i * 4 + quad[q];
            }
        }

        return new Prim
        {
            Name = name,
            Image = string.IsNullOrWhiteSpace(image) ? null : image,
            Material = string.IsNullOrWhiteSpace(material) ? null : material,
            Group = string.IsNullOrWhiteSpace(group) ? null : group,
            IsWall = true,
            Visible = true,
            Use3DMesh = true,
            NumVerts = n * 4,
            NumIndices = idx.Length,
            Verts = verts,
            Indices = idx,
        };
    }

    static Prim ReadPrimitive(byte[] buf, bool meshes)
    {
        var p = new Prim();
        byte[]? cv = null,
            ci = null,
            rv = null,
            ri = null;
        foreach (var (tag, d) in Records(buf, 4))
        {
            switch (tag)
            {
                case "NAME":
                    p.Name = WStr(d) ?? "";
                    break;
                case "VPOS":
                    p.Pos = V3(d);
                    break;
                case "VSIZ":
                    p.Size = V3(d);
                    break;
                case "IMAG":
                    p.Image = Str(d);
                    break;
                case "MATR":
                    p.Material = Str(d);
                    break;
                case "GRUP":
                    p.Group = Str(d);
                    break;
                case "TVIS":
                    p.Visible = I(d) != 0;
                    break;
                case "U3DM":
                    p.Use3DMesh = I(d) != 0;
                    break;
                case "DILI":
                {
                    var v = I(d);
                    p.SelfLit = v == 1 ? 1f : Math.Clamp(v / 255f, 0f, 1f);
                    break;
                } // pre-10.8
                case "DILT":
                    p.SelfLit = F(d);
                    break;
                case "M3VN":
                    p.NumVerts = I(d);
                    break;
                case "M3FN":
                    p.NumIndices = I(d);
                    break;
                case "M3CY":
                    p.CompressedVerts = I(d);
                    break;
                case "M3CJ":
                    p.CompressedIndices = I(d);
                    break;
                case "M3CX":
                    cv = d;
                    break;
                case "M3CI":
                    ci = d;
                    break;
                case "M3DX":
                    rv = d;
                    break;
                case "M3DI":
                    ri = d;
                    break;
                default:
                    if (tag.Length == 4 && tag.StartsWith("RTV") && tag[3] is >= '0' and <= '8')
                    {
                        p.RotTra[tag[3] - '0'] = F(d);
                    }

                    break;
            }
        }
        if (!meshes || !p.Use3DMesh || p.NumVerts <= 0 || p.NumIndices <= 0)
        {
            return p;
        }

        try
        {
            var vbytes = cv != null ? Inflate(cv, p.NumVerts * 32) : rv;
            var big = p.NumVerts > 65535;
            var ibytes = ci != null ? Inflate(ci, p.NumIndices * (big ? 4 : 2)) : ri;
            if (vbytes == null || ibytes == null || vbytes.Length < p.NumVerts * 32)
            {
                return p;
            }

            p.Verts = new float[p.NumVerts * 8];
            Buffer.BlockCopy(vbytes, 0, p.Verts, 0, p.NumVerts * 32);
            p.Indices = new int[p.NumIndices];
            for (var i = 0; i < p.NumIndices; i++)
            {
                p.Indices[i] = big
                    ? BitConverter.ToInt32(ibytes, i * 4)
                    : BitConverter.ToUInt16(ibytes, i * 2);
            }

            if (p.Indices.Any(i => i < 0 || i >= p.NumVerts))
            {
                p.Verts = null;
                p.Indices = null;
            }
        }
        catch
        {
            p.Verts = null;
            p.Indices = null;
        }
        return p;
    }

    static byte[] Inflate(byte[] data, int expected)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        var outBuf = new byte[expected];
        var read = 0;
        while (read < expected)
        {
            var n = z.Read(outBuf, read, expected - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }
        return read == expected ? outBuf : outBuf[..read];
    }

    // ---- BIFF helpers ---------------------------------------------------------------

    /// <summary>[int32 length][4-char tag][length-4 bytes] records, starting at <paramref name="start"/>.</summary>
    static IEnumerable<(string Tag, byte[] Data)> Records(byte[] buf, int start)
    {
        var pos = start;
        while (pos + 8 <= buf.Length)
        {
            var len = BitConverter.ToInt32(buf, pos);
            if (len < 4 || len > buf.Length - pos - 4) // written so a huge length can't overflow
            {
                yield break;
            }

            var tag = Encoding.ASCII.GetString(buf, pos + 4, 4);
            var data = new byte[len - 4];
            Buffer.BlockCopy(buf, pos + 8, data, 0, data.Length);
            yield return (tag, data);
            if (tag == "ENDB")
            {
                yield break;
            }

            pos += 4 + len;
        }
    }

    static float F(byte[] d) => d.Length >= 4 ? BitConverter.ToSingle(d, 0) : 0;

    static int I(byte[] d) => d.Length >= 4 ? BitConverter.ToInt32(d, 0) : 0;

    static Vector3 V3(byte[] d) =>
        d.Length >= 12
            ? new Vector3(
                BitConverter.ToSingle(d, 0),
                BitConverter.ToSingle(d, 4),
                BitConverter.ToSingle(d, 8)
            )
            : Vector3.Zero;

    static string? Str(byte[] d)
    {
        if (d.Length < 4)
        {
            return null;
        }

        var n = BitConverter.ToInt32(d, 0);
        return n < 0 || 4 + n > d.Length ? null : Encoding.Latin1.GetString(d, 4, n).TrimEnd('\0');
    }

    static string? WStr(byte[] d)
    {
        if (d.Length < 4)
        {
            return null;
        }

        var n = BitConverter.ToInt32(d, 0);
        // Wide strings store their length in bytes in older files and in characters in newer ones; accept both.
        if (n >= 0 && 4 + n * 2 <= d.Length)
        {
            return Encoding.Unicode.GetString(d, 4, n * 2).TrimEnd('\0');
        }

        if (n >= 0 && 4 + n <= d.Length)
        {
            return Encoding.Unicode.GetString(d, 4, n & ~1).TrimEnd('\0');
        }

        return null;
    }

    static byte[] ReadAll(Stream s)
    {
        var buf = new byte[s.Length];
        var read = 0;
        while (read < buf.Length)
        {
            var n = s.Read(buf, read, buf.Length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }
        return buf;
    }
}
