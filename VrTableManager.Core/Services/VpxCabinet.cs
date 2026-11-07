using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using OpenMcdf;

namespace VrTableManager.Services;

/// <summary>A material from the table (VPX "basic" or "metal").</summary>
public sealed record VpxMaterial(string Name, uint BaseColour, bool IsMetal, float Roughness, float Opacity, bool OpacityActive)
{
    /// <summary>VPX COLORREF is 0x00BBGGRR.</summary>
    public (float R, float G, float B) Rgb => ((BaseColour & 0xFF) / 255f, ((BaseColour >> 8) & 0xFF) / 255f, ((BaseColour >> 16) & 0xFF) / 255f);
}

/// <summary>
/// One cabinet part as a ready-to-draw triangle mesh in lobby space: metres, x right, y up, +z towards
/// the player, triangles wound so their front faces (Godot's) are the side the normals point to.
/// </summary>
public sealed class VpxCabinetPart
{
    public string Name { get; init; } = "";
    public float[] Positions { get; init; } = [];   // x,y,z per vertex
    public float[] Normals { get; init; } = [];
    public float[] Uvs { get; init; } = [];         // u,v per vertex
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
    /// <summary>The playfield's slope, back end up (radians), once the cabinet stands level.</summary>
    public float PlayfieldSlope => PlayfieldCorners.Length == 4
        ? MathF.Atan2(PlayfieldCorners[0].Y - PlayfieldCorners[3].Y, PlayfieldCorners[3].Z - PlayfieldCorners[0].Z) : 0;
    public float FrontZ { get; init; }
    public Vector3 Min { get; init; }
    public Vector3 Max { get; init; }
    /// <summary>Height of the playfield surface at its front edge (for the lockbar-height sanity check).</summary>
    public float PlayfieldFrontHeight => PlayfieldCorners.Length == 4 ? PlayfieldCorners[3].Y : 0;
    public IEnumerable<string> ImageNames => Parts.Select(p => p.Image).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A playfield's heights seen from above (see <see cref="VpxCabinet.HeightMap"/>): <see cref="Heights"/> row by row from the
/// back, 0 = playfield surface, 1 = <see cref="TopMetres"/> above it.
/// </summary>
public sealed record PlayfieldHeightMap(int Width, int Height, float[] Heights, float[] Undersides, int Primitives, Vector2 SizeMetres, float TopMetres)
{
    /// <summary>Share of the playfield with something standing on it (0..1); near 0 for tables built from walls and ramps.</summary>
    public float Coverage => Heights.Count(h => h > 0.02f) / (float)Heights.Length;
}

/// <summary>A primitive as read from the table, before any filtering (also used for diagnostics).</summary>
public sealed record VpxPrimitiveInfo(string Name, bool Visible, bool Use3DMesh, int Vertices, string? Image, string? Material, string? Group,
    Vector3 Min, Vector3 Max);

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

    const int ItemPrimitive = 19, ItemPartGroup = 24;

    // Names that cabinet builders use (PinCab template, VPW, g5k…), and room/lighting parts to leave out.
    static readonly Regex CabinetName = new(
        @"pincab|cabinet|^cab|cab$|_cab|cab_|backbox|backglass|lockdown|lockbar|side_?rail|siderail|^rails?$|_rails?\b|rails?_|leg|coin|grill|speaker|dmd|button|blade|bottom|metals?\b|metal_|fitting|apron|plunger|shooter|housing|bolt|feet|foot|leveler|trim",
        RegexOptions.IgnoreCase);
    static readonly Regex RoomName = new(
        @"room|floor|wall|ceiling|poster|window|carpet|chair|shelf|clock|plant|sign|sky|tree|cloud|curtain|beam|lamp|light|neon|topper|stool|rug|desk|tv\b|screen|sofa|door(?!.*coin)(?<!coin.*)",
        RegexOptions.IgnoreCase);
    // Lit/unlit duplicates ("cab_off", "APRON_GIoff", "siderails_off"): keep one version only.
    static readonly Regex OffVariant = new(@"(_|\b)(gi_?)?off\d*$|_off_|gioff", RegexOptions.IgnoreCase);
    // VLM lightmaps ("LM_GIBottom_RailsLockdown") are additive light layers over the baked meshes ("BM_…"),
    // not geometry to draw on their own; plain "Bolt12" parts are playfield post bolts.
    static readonly Regex NotGeometry = new(@"^lm_|^bolt\d*$", RegexOptions.IgnoreCase);

    /// <summary>
    /// The playfield glass ("PinCab_Glass"): the lobby shows the playfield itself, and a glass part often has no
    /// material, which draws as an opaque grey sheet over the playfield (Big Trouble in Little China).
    /// </summary>
    static bool IsPlayfieldGlass(string name) =>
        name.Contains("glass", StringComparison.OrdinalIgnoreCase) && !name.Contains("backglass", StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeCabinetPart(string name) =>
        CabinetName.IsMatch(name) && !RoomName.IsMatch(name) && !OffVariant.IsMatch(name) && !NotGeometry.IsMatch(name) && !IsPlayfieldGlass(name);

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
    public static PlayfieldHeightMap? HeightMap(string vpxPath, int width = 384, int height = 768, Func<string, bool>? include = null)
    {
        try
        {
            var t = Read(vpxPath, meshes: true);
            var pw = t.Right - t.Left;
            var ph = t.Bottom - t.Top;
            if (pw <= 0 || ph <= 0) return null;
            var meshes = new List<(Vector3[] Px, int[] Idx)>();
            foreach (var p in t.Prims)
            {
                if (!p.Visible || !p.Use3DMesh || p.Verts == null || p.Indices == null || p.NumVerts == 0) continue;
                if (NotGeometry.IsMatch(p.Name) || IsPlayfieldGlass(p.Name) || HeightMapSkip.IsMatch(p.Name)) continue;
                if (SpaceOf(p.Group, t) != 0) continue; // cabinet / room space
                if (include != null && !include(p.Name)) continue;
                var m = FullMatrix(p, t);
                var px = new Vector3[p.NumVerts];
                for (var i = 0; i < p.NumVerts; i++)
                {
                    var v = Vector3.Transform(new Vector3(p.Verts[i * 8], p.Verts[i * 8 + 1], p.Verts[i * 8 + 2]), m);
                    px[i] = new Vector3((v.X - t.Left) / pw * width, (v.Y - t.Top) / ph * height, v.Z);
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
                    drew |= Raster(top, null, width, height, px[idx[i]], px[idx[i + 1]], px[idx[i + 2]]);
                if (drew) used++;
            }
            var under = (float[])top.Clone();
            foreach (var (px, idx) in meshes)
                for (var i = 0; i + 2 < idx.Length; i += 3)
                    Raster(top, under, width, height, px[idx[i]], px[idx[i + 1]], px[idx[i + 2]]);
            for (var i = 0; i < top.Length; i++)
            {
                top[i] = Math.Clamp(top[i] / HeightMapTop, 0f, 1f);
                under[i] = Math.Clamp(under[i] / HeightMapTop, 0f, top[i]);
            }
            return new PlayfieldHeightMap(width, height, top, under, used,
                new Vector2(pw * VpuToMetres, ph * VpuToMetres), HeightMapTop * VpuToMetres);
        }
        catch
        {
            return null;
        }
    }

    // Cabinet body, glass and the room never stand on the playfield; the apron does, and stays.
    static readonly Regex HeightMapSkip = new(@"pincab|cabinet|^cab|blade|siderail|side_?rail|lockdown|lockbar|backbox|backglass|room|floor|ceiling|glass|reflect|shadow", RegexOptions.IgnoreCase);

    /// <summary>
    /// Draws one triangle into the height map. With <paramref name="under"/> null it keeps the highest point per pixel
    /// in <paramref name="top"/>; otherwise it keeps in <paramref name="under"/> the lowest point that still belongs
    /// to the top layer (within <see cref="HeightMapLayer"/> of the top). True when it changed any pixel.
    /// </summary>
    static bool Raster(float[] top, float[]? under, int w, int h, Vector3 a, Vector3 b, Vector3 c)
    {
        // Entirely above what's recorded (a backbox, a topper): not playfield.
        if (a.Z > HeightMapTop * 1.5f && b.Z > HeightMapTop * 1.5f && c.Z > HeightMapTop * 1.5f) return false;
        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        var maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        var maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        if (minX > maxX || minY > maxY) return false;
        var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        var any = false;

        bool Put(int i, float zHigh, float zLow)
        {
            if (under == null)
            {
                if (zHigh <= top[i]) return false;
                top[i] = zHigh;
                return true;
            }
            var layer = top[i] - HeightMapLayer;
            if (zHigh < layer) return false;
            var z = MathF.Max(zLow, layer);
            if (z >= under[i]) return false;
            under[i] = z;
            return true;
        }

        if (MathF.Abs(area) < 1e-6f)
        {
            // Seen edge-on (a wall's side): it marks the pixels it passes over, from its top edge down.
            if (maxX - minX > 2 && maxY - minY > 2) return false;
            var zHigh = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
            var zLow = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
            for (var y = minY; y <= maxY; y++)
                for (var x = minX; x <= maxX; x++) any |= Put(y * w + x, zHigh, zLow);
            return any;
        }
        for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                float sx = x + 0.5f, sy = y + 0.5f;
                var w0 = ((b.X - sx) * (c.Y - sy) - (b.Y - sy) * (c.X - sx)) / area;
                var w1 = ((c.X - sx) * (a.Y - sy) - (c.Y - sy) * (a.X - sx)) / area;
                var w2 = 1 - w0 - w1;
                if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                var z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                any |= Put(y * w + x, z, z);
            }
        return any;
    }

    // ---- Reading ---------------------------------------------------------------------

    sealed class Prim
    {
        public string Name = "";
        public Vector3 Pos, Size = Vector3.One;
        public readonly float[] RotTra = new float[9];
        public string? Image, Material, Group;
        public bool Visible = true, Use3DMesh;
        public float SelfLit;
        public int NumVerts, NumIndices, CompressedVerts, CompressedIndices;
        public float[]? Verts;   // 8 floats per vertex: x,y,z,nx,ny,nz,u,v
        public int[]? Indices;
    }

    sealed class TableData
    {
        public float Left, Top, Right, Bottom;
        public float GlassBottom, GroundToLockbar = 91f / 100f / VpuToMetres;
        public Dictionary<string, VpxMaterial> Materials = new(StringComparer.OrdinalIgnoreCase);
        public List<Prim> Prims = [];
        public Dictionary<string, (int Space, string? Parent)> Groups = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every primitive in the table with its name, visibility and bounds (VPX units), for diagnostics and tests.</summary>
    public static List<VpxPrimitiveInfo> ListPrimitives(string vpxPath)
    {
        var t = Read(vpxPath, meshes: true);
        return t.Prims.Select(p =>
        {
            var (min, max) = Bounds(p, t);
            return new VpxPrimitiveInfo(p.Name, p.Visible, p.Use3DMesh, p.NumVerts, p.Image, p.Material, p.Group, min, max);
        }).ToList();
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
            if (picked.Count == 0) return null;

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
                for (var i = 0; i < n; i++)
                {
                    var o = i * 8;
                    var v = Vector3.Transform(new Vector3(p.Verts![o], p.Verts[o + 1], p.Verts[o + 2]), m);
                    var nn = Vector3.TransformNormal(new Vector3(p.Verts[o + 3], p.Verts[o + 4], p.Verts[o + 5]), normalMatrix);
                    nn = nn.LengthSquared() > 1e-12f ? Vector3.Normalize(nn) : Vector3.UnitZ;
                    // VPX (x right, y towards the player, z up) → lobby (x right, y up, z towards the player).
                    pos[i * 3] = v.X * VpuToMetres; pos[i * 3 + 1] = v.Z * VpuToMetres; pos[i * 3 + 2] = v.Y * VpuToMetres;
                    nrm[i * 3] = nn.X; nrm[i * 3 + 1] = nn.Z; nrm[i * 3 + 2] = nn.Y;
                    uv[i * 2] = p.Verts[o + 6]; uv[i * 2 + 1] = p.Verts[o + 7];
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
                foreach (var (_, pos, nrm, _, _) in parts)
                    for (var i = 0; i < pos.Length; i += 3)
                    {
                        var v = Level(new Vector3(pos[i], pos[i + 1], pos[i + 2]));
                        pos[i] = v.X; pos[i + 1] = v.Y; pos[i + 2] = v.Z;
                        var n = Level(new Vector3(nrm[i], nrm[i + 1], nrm[i + 2]));
                        nrm[i] = n.X; nrm[i + 1] = n.Y; nrm[i + 2] = n.Z;
                    }

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var (_, pos, _, _, _) in parts)
                for (var i = 0; i < pos.Length; i += 3)
                {
                    var v = new Vector3(pos[i], pos[i + 1], pos[i + 2]);
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }
            // Floor at y = 0 (the bottom of the legs), centred across, front edge at frontZ.
            var shift = new Vector3(-(min.X + max.X) / 2, -min.Y, frontZ - max.Z);
            var outParts = parts.Select(x =>
            {
                for (var i = 0; i < x.Pos.Length; i += 3) { x.Pos[i] += shift.X; x.Pos[i + 1] += shift.Y; x.Pos[i + 2] += shift.Z; }
                return new VpxCabinetPart
                {
                    Name = x.P.Name, Positions = x.Pos, Normals = x.Nrm, Uvs = x.Uv, Indices = x.Idx,
                    Image = string.IsNullOrWhiteSpace(x.P.Image) ? null : x.P.Image,
                    Material = x.P.Material != null && t.Materials.TryGetValue(x.P.Material, out var mat) ? mat : null,
                    SelfLit = Math.Clamp(x.P.SelfLit, 0f, 1f),
                };
            }).ToList();

            // Playfield rectangle at playfield height (z = 0 in playfield space), same mapping.
            Vector3 Pf(float x, float y) => Level(new Vector3(x * VpuToMetres, 0, y * VpuToMetres)) + shift;
            return new VpxCabinetModel
            {
                Parts = outParts,
                PlayfieldCorners = [Pf(t.Left, t.Top), Pf(t.Right, t.Top), Pf(t.Right, t.Bottom), Pf(t.Left, t.Bottom)],
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
        float zMin = float.MaxValue, zMax = float.MinValue;
        foreach (var pos in positions)
            for (var i = 2; i < pos.Length; i += 3) { zMin = Math.Min(zMin, pos[i]); zMax = Math.Max(zMax, pos[i]); }
        if (zMax <= zMin) return 0;
        var band = (zMax - zMin) * 0.2f;
        (float Y, float Z) back = (float.MaxValue, 0), front = (float.MaxValue, 0);
        foreach (var pos in positions)
            for (var i = 0; i < pos.Length; i += 3)
            {
                var (y, z) = (pos[i + 1], pos[i + 2]);
                if (z <= zMin + band && y < back.Y) back = (y, z);
                if (z >= zMax - band && y < front.Y) front = (y, z);
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
        var candidates = t.Prims.Where(p => p.Use3DMesh && p.Verts != null && p.Indices != null && p.NumVerts > 0
                                            && LooksLikeCabinetPart(p.Name)).ToList();
        // The body anchors the selection: "PinCab_Cabinet", "CabinetMain", "cab"… (the biggest such part).
        var body = candidates.Where(p => Regex.IsMatch(p.Name, @"cabinet|^cab$|cabmain|cab_?body", RegexOptions.IgnoreCase))
            .OrderByDescending(p => { var (a, b) = Bounds(p, t); return (b - a).Length(); }).FirstOrDefault();
        if (body == null) return [];
        var (bmin, bmax) = Bounds(body, t);
        var margin = 0.6f / VpuToMetres; // 60 cm
        bmin -= new Vector3(margin, margin, margin * 2);
        bmax += new Vector3(margin, margin, margin * 3);
        // The playfield area (slightly inset), from just under the surface to well above it: toys, posts
        // and bolts live there. Only clearly-cabinet parts (apron, blades, PinCab_*) may sit inside it.
        var inset = 20f;
        var picked = candidates.Where(p =>
        {
            var (a, b) = Bounds(p, t);
            var nearBody = a.X >= bmin.X && b.X <= bmax.X && a.Y >= bmin.Y && b.Y <= bmax.Y && a.Z >= bmin.Z && b.Z <= bmax.Z;
            if (!nearBody) return false;
            var onPlayfield = a.X >= t.Left + inset && b.X <= t.Right - inset && a.Y >= t.Top + inset && b.Y <= t.Bottom - inset
                              && a.Z >= -60f && b.Z <= 400f;
            return !onPlayfield || StrongCabinetName.IsMatch(p.Name);
        }).ToList();
        picked.AddRange(PlayfieldWalls(t).Where(w => !picked.Contains(w)));
        return DropUnlitCopies(picked);
    }

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
        picked.Where(p =>
        {
            if (TrailingOff.Match(p.Name) is not { Success: true } m) return true;
            var stem = m.Groups["stem"].Value;
            if (stem.EndsWith("DT", StringComparison.OrdinalIgnoreCase)) stem = stem[..^2]; // "…DTOff" / "…DTON": desktop variants
            return !picked.Any(o => o != p && !TrailingOff.IsMatch(o.Name) && o.Name.StartsWith(stem, StringComparison.OrdinalIgnoreCase));
        }).ToList();

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
        foreach (var p in t.Prims)
        {
            if (!p.Visible || !p.Use3DMesh || p.Verts == null || p.Indices == null || p.NumVerts is 0 or > 2000) continue;
            if (NotGeometry.IsMatch(p.Name) || IsPlayfieldGlass(p.Name) || SpaceOf(p.Group, t) != 0) continue;
            var (a, b) = Bounds(p, t);
            var along = b.Y - a.Y >= (t.Bottom - t.Top) * 0.8f;
            var bothSides = a.X <= t.Left + 40 && b.X >= t.Right - 40 && a.X >= t.Left - 150 && b.X <= t.Right + 150;
            var railHigh = a.Z >= -60 && b.Z is >= 120 and <= 500;
            if (!along || !bothSides || !railHigh) continue;
            // Walls keep to the edges; metal guide walls and the like run across the middle of the playfield too.
            // Judged by what the part covers seen from above (its triangles; a mesh may carry unused vertices).
            const int W = 48, H = 96;
            var m = FullMatrix(p, t);
            var px = new Vector3[p.NumVerts];
            for (var i = 0; i < p.NumVerts; i++)
            {
                var v = Vector3.Transform(new Vector3(p.Verts[i * 8], p.Verts[i * 8 + 1], p.Verts[i * 8 + 2]), m);
                px[i] = new Vector3((v.X - t.Left) / (t.Right - t.Left) * W, (v.Y - t.Top) / (t.Bottom - t.Top) * H, MathF.Max(v.Z, 1));
            }
            var cover = new float[W * H];
            for (var i = 0; i + 2 < p.Indices.Length; i += 3)
                Raster(cover, null, W, H, px[p.Indices[i]], px[p.Indices[i + 1]], px[p.Indices[i + 2]]);
            int covered = 0, middle = 0;
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                    if (cover[y * W + x] > 0)
                    {
                        covered++;
                        if (x >= W / 10 && x < W - W / 10 && y >= H / 10 && y < H - H / 10) middle++; // inside the outer 10%
                    }
            if (covered == 0 || middle > covered / 10) continue;
            if (seen.Add((p.NumVerts, (int)a.Y, (int)b.Z))) walls.Add(p);
        }
        return walls;
    }

    static readonly Regex StrongCabinetName = new(@"pincab|cabinet|^cab|apron|blade|siderail|side_?rail|lockdown|lockbar", RegexOptions.IgnoreCase);

    static (Vector3 Min, Vector3 Max) Bounds(Prim p, TableData t)
    {
        if (p.Verts == null || p.NumVerts == 0) return (p.Pos, p.Pos);
        var m = FullMatrix(p, t);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        // Every vertex for small meshes; a sample for big ones (bounds only need to be close).
        var step = Math.Max(1, p.NumVerts / 4000);
        for (var i = 0; i < p.NumVerts; i += step)
        {
            var v = Vector3.Transform(new Vector3(p.Verts[i * 8], p.Verts[i * 8 + 1], p.Verts[i * 8 + 2]), m);
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
        var rt = Matrix4x4.CreateTranslation(r[3], r[4], r[5])
               * Matrix4x4.CreateRotationZ(Rad(r[2])) * Matrix4x4.CreateRotationY(Rad(r[1])) * Matrix4x4.CreateRotationX(Rad(r[0]))
               * Matrix4x4.CreateRotationZ(Rad(r[8])) * Matrix4x4.CreateRotationY(Rad(r[7])) * Matrix4x4.CreateRotationX(Rad(r[6]));
        var m = Matrix4x4.CreateScale(p.Size) * rt * Matrix4x4.CreateTranslation(p.Pos);
        if (SpaceOf(p.Group, t) != 0)
            m *= Matrix4x4.CreateTranslation(0, 0, -(t.GroundToLockbar - t.GlassBottom));
        return m;
    }

    /// <summary>The part group's space reference (0 = playfield), following SR_INHERIT (4) up the parents.</summary>
    static int SpaceOf(string? group, TableData t)
    {
        for (var depth = 0; group != null && depth < 32; depth++)
        {
            if (!t.Groups.TryGetValue(group, out var g)) return 0;
            if (g.Space != 4) return g.Space;
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
            using (gd) ReadGameData(ReadAll(gd), t);

        for (var i = 0; i < 100_000; i++)
        {
            if (!game.TryOpenStream("GameItem" + i, out var s) || s == null) break;
            using (s)
            {
                var head = new byte[4];
                if (s.Read(head, 0, 4) < 4) continue;
                var type = BitConverter.ToInt32(head, 0);
                if (type != ItemPrimitive && type != ItemPartGroup) continue;
                s.Position = 0;
                var buf = ReadAll(s);
                if (type == ItemPrimitive) t.Prims.Add(ReadPrimitive(buf, meshes));
                else ReadPartGroup(buf, t);
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
                case "LEFT": t.Left = F(d); break;
                case "TOPX": t.Top = F(d); break;
                case "RGHT": t.Right = F(d); break;
                case "BOTM": t.Bottom = F(d); break;
                case "GLAB": t.GlassBottom = F(d); break;
                case "CLBH": t.GroundToLockbar = F(d); break;
                case "MASI": count = I(d); break;
                case "MATE": legacy = d; break;
                case "MATR": // 10.8+: one nested object per material
                    if (ReadMaterial(d) is { } m) t.Materials[m.Name] = m;
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
                t.Materials[name] = new VpxMaterial(name,
                    BaseColour: BitConverter.ToUInt32(legacy, o + 32),
                    IsMetal: legacy[o + 48] != 0,
                    Roughness: BitConverter.ToSingle(legacy, o + 52),
                    Opacity: BitConverter.ToSingle(legacy, o + 68),
                    OpacityActive: (legacy[o + 72] & 1) != 0);
            }
        }
    }

    static VpxMaterial? ReadMaterial(byte[] d)
    {
        string? name = null;
        uint col = 0xFFFFFF;
        int type = 0;
        float rough = 0.5f, opac = 1f;
        var opActive = false;
        foreach (var (tag, x) in Records(d, 0))
        {
            switch (tag)
            {
                case "NAME": name = Str(x); break;
                case "TYPE": type = I(x); break;
                case "ROUG": rough = F(x); break;
                case "OPAC": opac = F(x); break;
                case "BASE": col = (uint)I(x); break;
                case "EOPA": opActive = I(x) != 0; break;
            }
        }
        return name == null ? null : new VpxMaterial(name, col, type == 1, rough, opac, opActive);
    }

    static void ReadPartGroup(byte[] buf, TableData t)
    {
        string? name = null, parent = null;
        var space = 0;
        foreach (var (tag, d) in Records(buf, 4))
        {
            switch (tag)
            {
                case "NAME": name = WStr(d); break;
                case "SPRF": space = I(d); break;
                case "GRUP": parent = Str(d); break;
            }
        }
        if (name != null) t.Groups[name] = (space, string.IsNullOrEmpty(parent) ? null : parent);
    }

    static Prim ReadPrimitive(byte[] buf, bool meshes)
    {
        var p = new Prim();
        byte[]? cv = null, ci = null, rv = null, ri = null;
        foreach (var (tag, d) in Records(buf, 4))
        {
            switch (tag)
            {
                case "NAME": p.Name = WStr(d) ?? ""; break;
                case "VPOS": p.Pos = V3(d); break;
                case "VSIZ": p.Size = V3(d); break;
                case "IMAG": p.Image = Str(d); break;
                case "MATR": p.Material = Str(d); break;
                case "GRUP": p.Group = Str(d); break;
                case "TVIS": p.Visible = I(d) != 0; break;
                case "U3DM": p.Use3DMesh = I(d) != 0; break;
                case "DILI": { var v = I(d); p.SelfLit = v == 1 ? 1f : Math.Clamp(v / 255f, 0f, 1f); break; } // pre-10.8
                case "DILT": p.SelfLit = F(d); break;
                case "M3VN": p.NumVerts = I(d); break;
                case "M3FN": p.NumIndices = I(d); break;
                case "M3CY": p.CompressedVerts = I(d); break;
                case "M3CJ": p.CompressedIndices = I(d); break;
                case "M3CX": cv = d; break;
                case "M3CI": ci = d; break;
                case "M3DX": rv = d; break;
                case "M3DI": ri = d; break;
                default:
                    if (tag.Length == 4 && tag.StartsWith("RTV") && tag[3] is >= '0' and <= '8') p.RotTra[tag[3] - '0'] = F(d);
                    break;
            }
        }
        if (!meshes || !p.Use3DMesh || p.NumVerts <= 0 || p.NumIndices <= 0) return p;
        try
        {
            var vbytes = cv != null ? Inflate(cv, p.NumVerts * 32) : rv;
            var big = p.NumVerts > 65535;
            var ibytes = ci != null ? Inflate(ci, p.NumIndices * (big ? 4 : 2)) : ri;
            if (vbytes == null || ibytes == null || vbytes.Length < p.NumVerts * 32) return p;
            p.Verts = new float[p.NumVerts * 8];
            Buffer.BlockCopy(vbytes, 0, p.Verts, 0, p.NumVerts * 32);
            p.Indices = new int[p.NumIndices];
            for (var i = 0; i < p.NumIndices; i++)
                p.Indices[i] = big ? BitConverter.ToInt32(ibytes, i * 4) : BitConverter.ToUInt16(ibytes, i * 2);
            if (p.Indices.Any(i => i < 0 || i >= p.NumVerts)) { p.Verts = null; p.Indices = null; }
        }
        catch { p.Verts = null; p.Indices = null; }
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
            if (n <= 0) break;
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
            if (len < 4 || pos + 4 + len > buf.Length) yield break;
            var tag = Encoding.ASCII.GetString(buf, pos + 4, 4);
            var data = new byte[len - 4];
            Buffer.BlockCopy(buf, pos + 8, data, 0, data.Length);
            yield return (tag, data);
            if (tag == "ENDB") yield break;
            pos += 4 + len;
        }
    }

    static float F(byte[] d) => d.Length >= 4 ? BitConverter.ToSingle(d, 0) : 0;
    static int I(byte[] d) => d.Length >= 4 ? BitConverter.ToInt32(d, 0) : 0;
    static Vector3 V3(byte[] d) => d.Length >= 12 ? new Vector3(BitConverter.ToSingle(d, 0), BitConverter.ToSingle(d, 4), BitConverter.ToSingle(d, 8)) : Vector3.Zero;

    static string? Str(byte[] d)
    {
        if (d.Length < 4) return null;
        var n = BitConverter.ToInt32(d, 0);
        return n < 0 || 4 + n > d.Length ? null : Encoding.Latin1.GetString(d, 4, n).TrimEnd('\0');
    }

    static string? WStr(byte[] d)
    {
        if (d.Length < 4) return null;
        var n = BitConverter.ToInt32(d, 0);
        // Wide strings store their length in bytes in older files and in characters in newer ones; accept both.
        if (n >= 0 && 4 + n * 2 <= d.Length) return Encoding.Unicode.GetString(d, 4, n * 2).TrimEnd('\0');
        if (n >= 0 && 4 + n <= d.Length) return Encoding.Unicode.GetString(d, 4, n & ~1).TrimEnd('\0');
        return null;
    }

    static byte[] ReadAll(Stream s)
    {
        var buf = new byte[s.Length];
        var read = 0;
        while (read < buf.Length)
        {
            var n = s.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return buf;
    }
}
