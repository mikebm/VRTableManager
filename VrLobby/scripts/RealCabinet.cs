using Godot;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>What a cabinet surface shows: kept as is, or this table's art / grille / display / backglass.</summary>
public enum SurfaceKind
{
    Keep,
    Art,
    Grille,
    Dmd,
    Backglass,
}

/// <summary>
/// A table's own VR cabinet, ready to draw: one mesh with a surface per material/texture, plus where
/// the playfield sits on it. Built once per table and shared by whichever ring slot shows that table.
/// </summary>
public sealed class RealCabinet
{
    public required ArrayMesh Mesh { get; init; }

    /// <summary>Playfield rectangle on this cabinet: centre (lobby space) and size (x across, z along).</summary>
    public required Vector3 PlayfieldCentre { get; init; }
    public required Vector2 PlayfieldSize { get; init; }

    /// <summary>The playfield's slope, back end up (radians), now that the cabinet stands level on its legs.</summary>
    public float PlayfieldSlope { get; init; }
    public required Aabb Bounds { get; init; }

    /// <summary>The surface holding the backglass part (-1: the cabinet has none), and whether it brought its own picture.</summary>
    public int BackglassSurface { get; init; } = -1;

    /// <summary>What each surface is, so another table can borrow this cabinet with its own art (see Cabinet.ShowReal).</summary>
    public IReadOnlyList<SurfaceKind> Kinds { get; init; } = [];
    public bool BackglassTextured { get; init; }

    /// <summary>Bounds of the backbox parts, for placing a backglass when the cabinet has none.</summary>
    public Aabb? Backbox { get; init; }
    public int Surfaces => Mesh.GetSurfaceCount();

    /// <summary>
    /// How far forward a backglass panel goes in this cabinet's backbox (lobby z), when it has a backbox. Worked out
    /// off the main thread by <see cref="ComputeGlassZ"/> as the cabinet is loaded.
    /// </summary>
    public float? GlassZ { get; init; }

    /// <summary>Height of the backbox's glass opening: the top 60% of the backbox (the speaker panel is below).</summary>
    public static float GlassHeight(Aabb box) => Mathf.Min(box.Size.Y * 0.6f, 0.6f);

    /// <summary>Centre height of the glass opening, with a margin inside the frame's top.</summary>
    public static float GlassCentreY(Aabb box) => box.End.Y - 0.045f - GlassHeight(box) / 2;

    /// <summary>
    /// A ray from the front into the middle of the backbox's opening finds the first surface there: a closed front
    /// (the backbox's own printed glass) and the panel sits just in front of it; an open frame and it sits on whatever
    /// is behind, at most 4 cm behind the frame's front edge, so the frame overlaps it like a real backglass. It used
    /// to sit a few millimetres in front of the frame, wider than the opening, and stood proud of it (The Addams Family).
    /// Walks every triangle: call it off the main thread.
    /// </summary>
    internal static float ComputeGlassZ(
        IEnumerable<(Vector3[] Verts, int[] Indices)> surfaces,
        Aabb box
    )
    {
        var front = box.End.Z;
        var cx = box.GetCenter().X;
        var cy = GlassCentreY(box);
        float? hit = null;
        foreach (var (v, idx) in surfaces)
        {
            for (var i = 0; i + 2 < idx.Length; i += 3)
            {
                Vector3 a = v[idx[i]],
                    b = v[idx[i + 1]],
                    c = v[idx[i + 2]];
                if (Mathf.Min(a.Z, Mathf.Min(b.Z, c.Z)) > front + 0.02f)
                {
                    continue; // in front of the box (a topper, a lamp)
                }
                // Cheap reject: seen from the front, the triangle's outline doesn't reach (cx, cy).
                if (
                    cx < Mathf.Min(a.X, Mathf.Min(b.X, c.X))
                    || cx > Mathf.Max(a.X, Mathf.Max(b.X, c.X))
                    || cy < Mathf.Min(a.Y, Mathf.Min(b.Y, c.Y))
                    || cy > Mathf.Max(a.Y, Mathf.Max(b.Y, c.Y))
                )
                {
                    continue;
                }
                // Does the triangle cover (cx, cy)? Then where along z.
                var d = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (Mathf.Abs(d) < 1e-9f)
                {
                    continue;
                }

                var w1 = ((cx - a.X) * (c.Y - a.Y) - (cy - a.Y) * (c.X - a.X)) / d;
                var w2 = ((b.X - a.X) * (cy - a.Y) - (b.Y - a.Y) * (cx - a.X)) / d;
                if (w1 < 0 || w2 < 0 || w1 + w2 > 1)
                {
                    continue;
                }

                var z = a.Z + w1 * (b.Z - a.Z) + w2 * (c.Z - a.Z);
                if (hit == null || z > hit)
                {
                    hit = z;
                }
            }
        }

        return hit is { } h ? Mathf.Max(h + 0.003f, front - 0.04f) : front - 0.02f;
    }

    (ArrayMesh Mesh, IReadOnlyList<SurfaceKind> Kinds, IReadOnlyList<int> Sources)? _withoutBackbox,
        _backboxOnly;

    /// <summary>
    /// This cabinet with its backbox taken out (every triangle inside <see cref="Backbox"/>), for a table borrowing it:
    /// the borrowing table gets the generic backbox (<see cref="CabinetKit.BuildBackbox"/>) in the same place, shaped
    /// to its own backglass. Kept with each surface's kind and its index in <see cref="Mesh"/>; surfaces left with
    /// nothing are dropped. Made the first time it's asked for, then kept. Null when the cabinet has no backbox parts.
    /// </summary>
    public (
        ArrayMesh Mesh,
        IReadOnlyList<SurfaceKind> Kinds,
        IReadOnlyList<int> Sources
    )? WithoutBackbox()
    {
        Split();
        return _withoutBackbox;
    }

    /// <summary>
    /// The other half of <see cref="WithoutBackbox"/>: just the backbox, so it can be drawn on its own and shaped
    /// (Cabinet: a shorter backbox for a wide picture over its whole front).
    /// </summary>
    public (
        ArrayMesh Mesh,
        IReadOnlyList<SurfaceKind> Kinds,
        IReadOnlyList<int> Sources
    )? BackboxOnly()
    {
        Split();
        return _backboxOnly;
    }

    void Split()
    {
        if (_withoutBackbox != null || Backbox is not { } box)
        {
            return;
        }

        var room = box.Grow(0.01f);
        var outside = (
            Mesh: new ArrayMesh(),
            Kinds: new List<SurfaceKind>(),
            Sources: new List<int>()
        );
        var inside = (
            Mesh: new ArrayMesh(),
            Kinds: new List<SurfaceKind>(),
            Sources: new List<int>()
        );
        for (var s = 0; s < Mesh.GetSurfaceCount(); s++)
        {
            var arrays = Mesh.SurfaceGetArrays(s);
            var verts = arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
            var idx = arrays[(int)Godot.Mesh.ArrayType.Index].AsInt32Array();
            List<int> outIdx = new(idx.Length),
                inIdx = [];
            for (var i = 0; i + 2 < idx.Length; i += 3)
            {
                var inBox =
                    room.HasPoint(verts[idx[i]])
                    && room.HasPoint(verts[idx[i + 1]])
                    && room.HasPoint(verts[idx[i + 2]]);
                var to = inBox ? inIdx : outIdx;
                to.Add(idx[i]);
                to.Add(idx[i + 1]);
                to.Add(idx[i + 2]);
            }
            foreach (var (part, kept) in new[] { (outside, outIdx), (inside, inIdx) })
            {
                if (kept.Count == 0)
                {
                    continue;
                }

                var copy = (Godot.Collections.Array)arrays.Duplicate();
                copy[(int)Godot.Mesh.ArrayType.Index] = kept.ToArray();
                part.Mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, copy);
                part.Mesh.SurfaceSetMaterial(
                    part.Mesh.GetSurfaceCount() - 1,
                    Mesh.SurfaceGetMaterial(s)
                );
                part.Kinds.Add(s < Kinds.Count ? Kinds[s] : SurfaceKind.Keep);
                part.Sources.Add(s);
            }
        }
        _withoutBackbox = (outside.Mesh, outside.Kinds, outside.Sources);
        _backboxOnly = (inside.Mesh, inside.Kinds, inside.Sources);
    }

    /// <summary>
    /// The surfaces made from the community VR cabinet kit's two art parts, PinCab_Cabinet (its sides and front on one
    /// square sheet) and PinCab_Backbox (the backbox's sides, front and speaker panel on one 1:2 sheet), or -1. Most VR
    /// tables (and the default cabinet, Medieval Madness's) are built from that kit, so one table's sheets fit another
    /// table's parts: a table on the default cabinet wears its own cabinet's art (Cabinet.ShowReal).
    /// </summary>
    public int KitCabinetSurface { get; init; } = -1;
    public int KitBackboxSurface { get; init; } = -1;

    /// <summary>This cabinet's kit art sheets (see <see cref="KitCabinetSurface"/>), when it has them in the kit's shape.</summary>
    public Texture2D? KitCabinetArt { get; init; }
    public Texture2D? KitBackboxArt { get; init; }

    /// <summary>The kit's inner walls above the playfield (PinCab_Blades, a square sheet), like the other kit parts.</summary>
    public int KitBladesSurface { get; init; } = -1;
    public Texture2D? KitBladesArt { get; init; }

    /// <summary>
    /// The apron (the instruction cards' panel at the front of the playfield) and the back panel (the board behind the
    /// playfield, under the backbox), or -1. On the default cabinet they're the template table's own: the apron is
    /// hidden, and the back panel wears the borrowing table's picture when it has one (Cabinet.DressBorrowed).
    /// </summary>
    public int ApronSurface { get; init; } = -1;
    public int BackPanelSurface { get; init; } = -1;

    /// <summary>
    /// The backbox's front opening (x across, y up): its glass part and the speaker panel under it, where a picture of
    /// a whole backbox front goes when the speakers are hidden (Cabinet: an "empty" backbox); and how far forward
    /// (lobby z), just in front of the speaker grilles. Null without a backglass part in a backbox.
    /// </summary>
    public Rect2? FrontOpening { get; init; }
    public float FrontOpeningZ { get; init; }

    /// <summary>Is this surface (named by its first part, or by its picture) one of the kit's two art parts?</summary>
    internal static bool IsKitPart(string name, string? image, string part) =>
        string.Equals(name, part, StringComparison.OrdinalIgnoreCase)
        || image?.StartsWith(part, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>A kit sheet has the kit's shape: square for the cabinet, twice as tall as wide for the backbox.</summary>
    internal static bool KitShaped(Texture2D tex, float aspect) =>
        tex.GetHeight() > 0
        && Mathf.Abs((float)tex.GetWidth() / tex.GetHeight() - aspect) < aspect * 0.08f;

    readonly Dictionary<RealCabinet, (ArrayMesh? Sides, ArrayMesh? Back)> _wallsFor = [];

    /// <summary>Height of this cabinet's playfield at <paramref name="z"/> (it rises towards the back).</summary>
    float PlayfieldYAt(float z) =>
        PlayfieldCentre.Y + (PlayfieldCentre.Z - z) * Mathf.Tan(PlayfieldSlope);

    /// <summary>
    /// This cabinet's own inner walls round its playfield (the side walls above it and the board behind it, whatever
    /// they're called and however their art is laid out: Big Trouble in Little China's "backwall" wraps all three),
    /// fitted onto <paramref name="into"/>'s playfield, for a table shown on the default cabinet. Picked by place: the
    /// textured faces that stand up (not floors or tops) within its playfield's outline (or up to 20 cm behind it, under
    /// the backbox) and up to 30 cm above it,
    /// facing inwards. Sides: both walls (null unless both have art along most of their length); Back: the back board
    /// (null when it has none, or only black). Each face keeps its own material and texture coordinates. Made once per
    /// default cabinet, then kept.
    /// </summary>
    public (ArrayMesh? Sides, ArrayMesh? Back) OwnWallsOn(RealCabinet into)
    {
        if (_wallsFor.TryGetValue(into, out var made))
        {
            return made;
        }

        var c = PlayfieldCentre;
        var half = new Vector2(PlayfieldSize.X / 2, PlayfieldSize.Y / 2);
        var dirO = new Vector3(0, Mathf.Sin(PlayfieldSlope), -Mathf.Cos(PlayfieldSlope)); // towards the back, along it
        var dirT = new Vector3(0, Mathf.Sin(into.PlayfieldSlope), -Mathf.Cos(into.PlayfieldSlope));
        var sx = into.PlayfieldSize.X / Mathf.Max(PlayfieldSize.X, 1e-3f);
        var sz = into.PlayfieldSize.Y / Mathf.Max(PlayfieldSize.Y, 1e-3f);
        // Own lobby space -> along / across / up from its playfield -> the same from the default cabinet's.
        Vector3 Map(Vector3 p)
        {
            var d = p - c;
            var along = d.Dot(dirO) * sz;
            var up = p.Y - PlayfieldYAt(p.Z);
            var onPlayfield = into.PlayfieldCentre + dirT * along;
            return new Vector3(
                into.PlayfieldCentre.X + d.X * sx,
                onPlayfield.Y + up,
                onPlayfield.Z
            );
        }

        var sides = new ArrayMesh();
        var back = new ArrayMesh();
        float left = 0,
            right = 0,
            backArea = 0,
            backLight = 0,
            backSamples = 0;
        // How far along each wall (z) and across the back (x) the faces reach: (min, max).
        Vector2 leftZ = new(float.MaxValue, float.MinValue),
            rightZ = leftZ,
            backX = leftZ;
        static Vector2 Extend(Vector2 r, float p, float q, float s) =>
            new(
                Mathf.Min(r.X, Mathf.Min(p, Mathf.Min(q, s))),
                Mathf.Max(r.Y, Mathf.Max(p, Mathf.Max(q, s)))
            );
        // Its cabinet's art parts, then its playfield-edge parts (EdgeMesh): Elvira's walls are only there.
        var sources = Enumerable
            .Range(0, Mesh.GetSurfaceCount())
            .Select(s => (Src: Mesh, S: s, Edge: false))
            .Concat(
                Enumerable
                    .Range(0, EdgeMesh?.GetSurfaceCount() ?? 0)
                    .Select(s => (Src: EdgeMesh!, S: s, Edge: true))
            );
        foreach (var (src, s, edge) in sources)
        {
            if (
                !edge
                && (
                    s == BackglassSurface
                    || s >= Kinds.Count
                    || Kinds[s]
                        is SurfaceKind.Keep
                            or SurfaceKind.Backglass
                            or SurfaceKind.Grille
                            or SurfaceKind.Dmd
                )
            )
            {
                continue; // walls with art only: plain parts and the backbox's stay the default cabinet's
            }

            var arrays = src.SurfaceGetArrays(s);
            var v = arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
            var n = arrays[(int)Godot.Mesh.ArrayType.Normal].AsVector3Array();
            var uv = arrays[(int)Godot.Mesh.ArrayType.TexUV].AsVector2Array();
            var idx = arrays[(int)Godot.Mesh.ArrayType.Index].AsInt32Array();
            var pv = new[] { new List<Vector3>(), new List<Vector3>() };
            var pn = new[] { new List<Vector3>(), new List<Vector3>() };
            var pu = new[] { new List<Vector2>(), new List<Vector2>() };
            var backUvs = new List<Vector2>();
            for (var i = 0; i + 2 < idx.Length; i += 3)
            {
                Vector3 a = v[idx[i]],
                    b = v[idx[i + 1]],
                    d = v[idx[i + 2]];
                var normal = (b - a).Cross(d - a);
                if (normal.LengthSquared() < 1e-12f)
                {
                    continue;
                }

                normal = normal.Normalized();
                if ((n[idx[i]] + n[idx[i + 1]] + n[idx[i + 2]]).Dot(normal) < 0)
                {
                    normal = -normal;
                }

                if (Mathf.Abs(normal.Y) > 0.5f)
                {
                    continue; // floors and tops
                }

                var inside = true;
                foreach (var p in new[] { a, b, d })
                {
                    var above = p.Y - PlayfieldYAt(p.Z);
                    inside &=
                        Mathf.Abs(p.X - c.X) <= half.X + 0.035f
                        && p.Z >= c.Z - half.Y - 0.20f // walls often run on under the backbox
                        && p.Z <= c.Z + half.Y + 0.02f
                        && above >= -0.06f
                        && above <= 0.30f;
                }
                if (!inside)
                {
                    continue;
                }

                var mid = (a + b + d) / 3;
                var area = (b - a).Cross(d - a).Length() / 2;
                int to;
                // Facing inwards: a side wall towards the middle, the back board forwards.
                // At the playfield's edge only: the outer eighth each side, the back stretch. Ramps and toys over the
                // playfield face inwards too (Elvira's).
                if (
                    Mathf.Abs(normal.X) > 0.6f
                    && normal.X * (mid.X - c.X) < 0
                    && Mathf.Abs(mid.X - c.X) >= half.X * 0.75f
                )
                {
                    to = 0;
                    if (mid.X < c.X)
                    {
                        left += area;
                        leftZ = Extend(leftZ, a.Z, b.Z, d.Z);
                    }
                    else
                    {
                        right += area;
                        rightZ = Extend(rightZ, a.Z, b.Z, d.Z);
                    }
                }
                else if (normal.Z > 0.6f && mid.Z < c.Z - half.Y * 0.85f)
                {
                    to = 1;
                    backArea += area;
                    backX = Extend(backX, a.X, b.X, d.X);
                    backUvs.Add((uv[idx[i]] + uv[idx[i + 1]] + uv[idx[i + 2]]) / 3);
                }
                else
                {
                    continue;
                }

                foreach (var k in new[] { idx[i], idx[i + 1], idx[i + 2] })
                {
                    pv[to].Add(Map(v[k]) + normal * 0.002f);
                    pn[to].Add(n[k]);
                    pu[to].Add(uv[k]);
                }
            }
            var material = src.SurfaceGetMaterial(s);
            for (var p = 0; p < 2; p++)
            {
                if (pv[p].Count == 0)
                {
                    continue;
                }

                var face = new Godot.Collections.Array();
                face.Resize((int)Godot.Mesh.ArrayType.Max);
                face[(int)Godot.Mesh.ArrayType.Vertex] = pv[p].ToArray();
                face[(int)Godot.Mesh.ArrayType.Normal] = pn[p].ToArray();
                face[(int)Godot.Mesh.ArrayType.TexUV] = pu[p].ToArray();
                var target = p == 0 ? sides : back;
                target.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, face);
                target.SurfaceSetMaterial(target.GetSurfaceCount() - 1, material);
            }
            // How light the back board's art is where its faces take it from (a kit wall sheet often leaves it black).
            if (
                backUvs.Count > 0
                && (material as StandardMaterial3D)?.AlbedoTexture?.GetImage() is { } img
            )
            {
                if (img.IsCompressed())
                {
                    img.Decompress();
                }

                foreach (var u in backUvs.Take(64))
                {
                    var px = img.GetPixel(
                        Math.Clamp((int)(u.X * img.GetWidth()), 0, img.GetWidth() - 1),
                        Math.Clamp((int)(u.Y * img.GetHeight()), 0, img.GetHeight() - 1)
                    );
                    backLight += px.R * 0.3f + px.G * 0.59f + px.B * 0.11f;
                    backSamples++;
                }
            }
        }
        // Enough to count: art along most of each wall's length, a few centimetres high; most of the back board's
        // width, and not black.
        // Enough to count: art along most of each wall's length (bits of other parts that only happen to stand there
        // don't make a wall: Elvira's), a few centimetres high; most of the back board's width, and not black.
        var wallArea = PlayfieldSize.Y * 0.04f;
        made = (
            left >= wallArea
            && right >= wallArea
            && leftZ.Y - leftZ.X >= PlayfieldSize.Y * 0.7f
            && rightZ.Y - rightZ.X >= PlayfieldSize.Y * 0.7f
                ? sides
                : null,
            backArea >= PlayfieldSize.X * 0.04f
            && backX.Y - backX.X >= PlayfieldSize.X * 0.7f
            && backSamples > 0
            && backLight / backSamples > 0.025f
                ? back
                : null
        );
        _wallsFor[into] = made;
        return made;
    }

    readonly Dictionary<int, Dictionary<int, Rect2>> _uvLayouts = [];

    /// <summary>
    /// Where each side of a surface takes its art from on its sheet: for the faces pointing left, right, forward, back
    /// and up, the rectangle of texture coordinates they use. Made once per surface, then kept.
    /// </summary>
    Dictionary<int, Rect2> UvLayout(int surface)
    {
        if (_uvLayouts.TryGetValue(surface, out var known))
        {
            return known;
        }

        var layout = new Dictionary<int, Rect2>();
        if (surface >= 0 && surface < Mesh.GetSurfaceCount())
        {
            var arrays = Mesh.SurfaceGetArrays(surface);
            var n = arrays[(int)Godot.Mesh.ArrayType.Normal].AsVector3Array();
            var uv = arrays[(int)Godot.Mesh.ArrayType.TexUV].AsVector2Array();
            var idx = arrays[(int)Godot.Mesh.ArrayType.Index].AsInt32Array();
            for (var i = 0; i + 2 < idx.Length; i += 3)
            {
                var normal = n[idx[i]] + n[idx[i + 1]] + n[idx[i + 2]];
                if (normal.LengthSquared() < 1e-6f)
                {
                    continue;
                }

                normal = normal.Normalized();
                var side =
                    normal.X < -0.7f ? 0
                    : normal.X > 0.7f ? 1
                    : normal.Z > 0.7f ? 2
                    : normal.Z < -0.7f ? 3
                    : normal.Y > 0.7f ? 4
                    : -1;
                if (side < 0)
                {
                    continue;
                }

                foreach (var k in new[] { idx[i], idx[i + 1], idx[i + 2] })
                {
                    layout[side] = layout.TryGetValue(side, out var r)
                        ? r.Expand(uv[k])
                        : new Rect2(uv[k], Vector2.Zero);
                }
            }
        }
        _uvLayouts[surface] = layout;
        return layout;
    }

    /// <summary>
    /// Where on its sheet the face of a surface pointing <paramref name="side"/> (0 left, 1 right, 2 forward, 3 back,
    /// 4 up) takes its art, in texture coordinates; null when it has no such face.
    /// </summary>
    public Rect2? FaceUv(int surface, int side) =>
        surface >= 0 && UvLayout(surface).TryGetValue(side, out var r) ? r : null;

    /// <summary>
    /// Do two cabinets' parts take their art from the same places on their sheets? Most VR cabinets are built from the
    /// same community kit, so one table's sheet fits another's parts; but some only borrow the kit's names and lay
    /// their own sheet out differently (Fish Tales'), which put the wrong bits of art on the wrong faces. Compared side
    /// by side (left, right, front, back, top): every side both have must overlap mostly (more than half).
    /// </summary>
    public static bool SameSheetLayout(RealCabinet a, int surfaceA, RealCabinet b, int surfaceB)
    {
        if (surfaceA < 0 || surfaceB < 0)
        {
            return false;
        }

        var la = a.UvLayout(surfaceA);
        var lb = b.UvLayout(surfaceB);
        var shared = la.Keys.Intersect(lb.Keys).ToList();
        if (shared.Count == 0)
        {
            return false;
        }

        foreach (var side in shared)
        {
            var (ra, rb) = (la[side], lb[side]);
            var overlap = ra.Intersection(rb);
            var union = ra.Merge(rb);
            var areaU = union.Size.X * union.Size.Y;
            var areaI = overlap.Size.X * overlap.Size.Y;
            if (areaU > 1e-6f && areaI / areaU < 0.5f)
            {
                return false;
            }
        }
        return true;
    }

    (ArrayMesh Mesh, int Left, int Right, int Back)? _artFaces;

    /// <summary>
    /// The faces a borrowing table's own pictures go on, picked out of the kit's inner side walls (the left one's
    /// inward face, the right one's) and the back panel (its front face), each a surface of its own with texture
    /// coordinates laid along it: a picture covers just that face, left to right and top to bottom as you look at it,
    /// rather than following the template's own art sheet (which wrapped a back panel picture over its top edge). Set a
    /// millimetre in front of the face they copy. -1 for a face the cabinet doesn't have. Made once, then kept.
    /// </summary>
    public (ArrayMesh Mesh, int Left, int Right, int Back) ArtFaces()
    {
        if (_artFaces is { } made)
        {
            return made;
        }

        var mesh = new ArrayMesh();
        int Add(int surface, Func<Vector3, bool> facing, Func<Vector3, float> along, bool flip)
        {
            if (surface < 0 || surface >= Mesh.GetSurfaceCount())
            {
                return -1;
            }

            var arrays = Mesh.SurfaceGetArrays(surface);
            var v = arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
            var n = arrays[(int)Godot.Mesh.ArrayType.Normal].AsVector3Array();
            var idx = arrays[(int)Godot.Mesh.ArrayType.Index].AsInt32Array();
            var tris = new List<int>();
            for (var i = 0; i + 2 < idx.Length; i += 3)
            {
                var normal = (n[idx[i]] + n[idx[i + 1]] + n[idx[i + 2]]).Normalized();
                if (facing(normal))
                {
                    tris.AddRange([idx[i], idx[i + 1], idx[i + 2]]);
                }
            }
            if (tris.Count == 0)
            {
                return -1;
            }
            // Along the face (0 at one end, 1 at the other), and up it between its bottom and top edges, which may
            // slope (the side walls follow the playfield): fitted from the corners at each end.
            var pts = tris.Distinct().Select(i => v[i]).ToList();
            var a0 = pts.Min(along);
            var a1 = pts.Max(along);
            var span = Mathf.Max(a1 - a0, 1e-4f);
            (float Bottom, float Top) EdgesAt(float end)
            {
                var near = pts.Where(p => Mathf.Abs(along(p) - end) < span * 0.1f).ToList();
                return near.Count == 0
                    ? (pts.Min(p => p.Y), pts.Max(p => p.Y))
                    : (near.Min(p => p.Y), near.Max(p => p.Y));
            }
            var (b0, t0) = EdgesAt(a0);
            var (b1, t1) = EdgesAt(a1);
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            foreach (var i in tris)
            {
                var p = v[i];
                var t = (along(p) - a0) / span;
                var bottom = Mathf.Lerp(b0, b1, t);
                var top = Mathf.Lerp(t0, t1, t);
                var u = flip ? 1 - t : t;
                var vv = (top - p.Y) / Mathf.Max(top - bottom, 1e-4f);
                verts.Add(p + n[i] * 0.001f);
                norms.Add(n[i]);
                uvs.Add(new Vector2(u, Mathf.Clamp(vv, 0, 1)));
            }
            var face = new Godot.Collections.Array();
            face.Resize((int)Godot.Mesh.ArrayType.Max);
            face[(int)Godot.Mesh.ArrayType.Vertex] = verts.ToArray();
            face[(int)Godot.Mesh.ArrayType.Normal] = norms.ToArray();
            face[(int)Godot.Mesh.ArrayType.TexUV] = uvs.ToArray();
            mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, face);
            return mesh.GetSurfaceCount() - 1;
        }

        // Seen from inside the cabinet: on the left wall (facing right) the front end is on your left as you face it,
        // on the right wall (facing left) the back end is; the back panel faces you, left to right.
        var left = Add(KitBladesSurface, nm => nm.X > 0.7f, p => p.Z, flip: true);
        var right = Add(KitBladesSurface, nm => nm.X < -0.7f, p => p.Z, flip: false);
        var back = Add(BackPanelSurface, nm => nm.Z > 0.7f, p => p.X, flip: false);
        _artFaces = (mesh, left, right, back);
        return _artFaces.Value;
    }

    /// <summary>
    /// Top of the side rails at the playfield's front and back ends (lobby z, y each), where the glass lies: the rails
    /// usually rise more steeply than the playfield, so glass parallel to the playfield sank into the back panel.
    /// Null when the cabinet has no part named for its rails.
    /// </summary>
    public (Vector2 Front, Vector2 Back)? RailTops { get; init; }

    /// <summary>
    /// The table's own parts along its playfield's sides and back that aren't cabinet parts (VpxCabinetModel.EdgeParts:
    /// Elvira's walls), one surface each with its material. Never drawn with the cabinet; only <see cref="OwnWallsOn"/>
    /// looks at them. Empty when there are none (the bundled default cabinet has none).
    /// </summary>
    public ArrayMesh? EdgeMesh { get; init; }

    public int Textures { get; init; }

    /// <summary>How many textures ended up GPU-compressed (DXT); the rest stayed uncompressed.</summary>
    public int CompressedTextures { get; init; }
}

/// <summary>
/// Loads tables' own cabinets in the background (parse + texture decode off the main thread, GPU upload
/// on it), keeps the most recent ones, and remembers tables that have none so they stay generic.
/// What it reads is also kept on disk (<see cref="CabinetCache"/>), so later lobby runs skip the reading.
/// </summary>
public partial class RealCabinetLoader : Node
{
    public const int TextureMaxSide = 1024;

    /// <summary>Names the surfaces made from a table's playfield-edge parts (VpxCabinetModel.EdgeParts), kept out of its cabinet.</summary>
    internal const string EdgePrefix = "edge:";

    /// <summary>
    /// How many tables' cabinets are kept: the ring's cabinets plus a few just scrolled past. The lobby sets it from
    /// the ring's size. It must never be fewer than the cabinets on show: reading one would forget another still
    /// standing in the ring, which then hides until it's read again and forgets the next, round and round.
    /// </summary>
    public int Keep { get; set; } = KeepFor(VrTableManager.Lobby.LobbyRing.Default.Count);

    public static int KeepFor(int ringCount) => ringCount + 3;

    /// <summary>Table path, and its cabinet (null: the table has no VR cabinet, use the generic one).</summary>
    public event Action<string, RealCabinet?>? CabinetReady;

    /// <summary>Where read cabinets are kept between lobby runs; null: no disk cache.</summary>
    public string? CacheDir { get; set; } = CabinetCache.DefaultDir;

    /// <summary>Requests answered from the disk cache instead of reading the table, and how long the last request took.</summary>
    public int CacheHits => _cacheHits;
    public long LastLoadMs { get; private set; }
    int _cacheHits;

    readonly Dictionary<string, RealCabinet?> _done = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<string> _recent = [];
    readonly SemaphoreSlim _oneAtATime = new(1); // big tables: don't decode several at once

    public bool IsKnown(string vpx) => _done.ContainsKey(vpx);

    public RealCabinet? Get(string vpx) => _done.GetValueOrDefault(vpx);

    /// <summary>
    /// Marks a table's cabinet as just used (it's on show). Without it the order was when each was first read, so
    /// stepping back the way the ring had loaded forgot a cabinet still standing next to the selected one.
    /// </summary>
    public void Touch(string vpx)
    {
        if (_recent.First?.Value == vpx)
        {
            return;
        }

        if (_recent.Remove(vpx))
        {
            _recent.AddFirst(vpx);
        }
    }

    public void Request(string vpx)
    {
        _recent.Remove(vpx);
        _recent.AddFirst(vpx);
        if (_done.TryGetValue(vpx, out var known))
        {
            CabinetReady?.Invoke(vpx, known);
            return;
        }
        if (!_pending.Add(vpx))
        {
            return;
        }

        var dir = CacheDir;
        _ = LobbyWork.Run(async () =>
        {
            await _oneAtATime.WaitAsync(LobbyWork.Closing); // closing: the ones still waiting never start
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                LobbyWork.Closing.ThrowIfCancellationRequested();
                Parsed? parsed;
                if (dir != null && CabinetCache.TryLoad(vpx, dir, out var cached))
                {
                    parsed = cached;
                    Interlocked.Increment(ref _cacheHits);
                }
                else
                {
                    parsed = Parse(vpx);
                    if (dir != null)
                    {
                        CabinetCache.Save(vpx, dir, parsed);
                    }
                }
                var glass = GlassZOf(parsed); // walks every triangle: here, not on the main thread
                Callable.From(() => Finish(vpx, parsed, glass, sw)).CallDeferred();
            }
            catch
            {
                Callable.From(() => Finish(vpx, null, null, sw)).CallDeferred();
            }
            finally
            {
                _oneAtATime.Release();
            }
        });
    }

    /// <summary>
    /// The lobby's default cabinet, worn by every table without its own: a cabinet dumped once from a
    /// table (<see cref="DumpDefault"/>) and shipped with the lobby, so it's there at once, table installed or not.
    /// Read through Godot (<paramref name="resPath"/> is a res:// path): in an exported lobby the file is inside
    /// the .pck, where plain .NET file access can't reach it.
    /// </summary>
    public static RealCabinet? LoadDefault(string resPath)
    {
        var bytes = Godot.FileAccess.GetFileAsBytes(resPath);
        if (bytes == null || bytes.Length == 0)
        {
            return null;
        }

        using var stream = new MemoryStream(bytes, writable: false);
        return CabinetCache.TryLoadDump(stream, out var parsed) && parsed != null
            ? Build(parsed, GlassZOf(parsed))
            : null;
    }

    static float? GlassZOf(Parsed? p) =>
        p?.Backbox is { } box
            ? RealCabinet.ComputeGlassZ(p.Surfaces.Select(s => (s.Verts, s.Indices)), box)
            : null;

    /// <summary>
    /// Reads this table's VR cabinet and writes it as the default cabinet file; false if it has none.
    /// Its pictures (the table's printed art) are left out: tables borrowing the cabinet cover every
    /// textured surface with their own look (Cabinet.DressBorrowed), so each becomes a plain grey stand-in
    /// that only keeps the surface counted as textured.
    /// </summary>
    public static bool DumpDefault(string vpx, string file)
    {
        var parsed = Parse(vpx);
        if (parsed == null)
        {
            return false;
        }

        foreach (var name in parsed.Images.Keys.ToList())
        {
            var blank = Image.CreateEmpty(4, 4, false, Image.Format.Rgb8);
            blank.Fill(new Color(0.5f, 0.5f, 0.5f));
            parsed.Images[name] = blank;
            parsed.Alpha[name] = Image.AlphaMode.None;
        }
        return CabinetCache.SaveDump(parsed, file);
    }

    /// <summary>One surface to be: the parts sharing a look, merged into plain arrays.</summary>
    internal sealed record SurfaceData(
        string Name,
        Vector3[] Verts,
        Vector3[] Norms,
        Vector2[] Uvs,
        int[] Indices,
        string? Image,
        VpxMaterial? Material,
        float Lit,
        bool Backglass
    );

    /// <summary>A cabinet ready for the GPU: what <see cref="Parse"/> makes and <see cref="CabinetCache"/> keeps.</summary>
    internal sealed class Parsed
    {
        public required List<SurfaceData> Surfaces { get; init; }
        public required Dictionary<string, Image> Images { get; init; }
        public required Dictionary<string, Image.AlphaMode> Alpha { get; init; }
        public required Vector3 PlayfieldCentre { get; init; }
        public required Vector2 PlayfieldSize { get; init; }
        public float PlayfieldSlope { get; init; }
        public required Aabb Bounds { get; init; }
        public Aabb? Backbox { get; init; }
    }

    /// <summary>Background part: read the cabinet, decode its pictures (Godot Images are fine off the main thread) and merge its parts.</summary>
    internal static Parsed? Parse(string vpx)
    {
        var model = VpxCabinet.Load(vpx, CabinetKit.Front);
        if (model == null)
        {
            return null;
        }

        var images = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        var alphaModes = new Dictionary<string, Image.AlphaMode>(StringComparer.OrdinalIgnoreCase);
        var catalogue = VpxImages.ListImages(vpx);
        var backglassImages = model
            .Parts.Where(p => IsBackglass(p.Name) && p.Image != null)
            .Select(p => p.Image!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in model.ImageNames)
        {
            var info = catalogue.FirstOrDefault(i =>
                string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)
            );
            if (info == null)
            {
                continue;
            }

            // The lobby is closing: stop between pictures (thrown, so nothing half-read goes in the disk cache).
            LobbyWork.Closing.ThrowIfCancellationRequested();
            var bytes = VpxImages.ExtractImage(vpx, info.Index);
            if (bytes == null)
            {
                continue;
            }

            var img = ArtLoader.Decode(bytes);
            if (img == null)
            {
                continue;
            }

            if (img.IsCompressed())
            {
                img.Decompress();
            }
            // A backglass picture that's one flat colour (Harry Potter's is plain black) is a placeholder the
            // table's script or PuP pack draws over while playing: leave it out, so the VPS backglass shows.
            if (backglassImages.Contains(name) && IsBlank(img))
            {
                continue;
            }

            ArtLoader.Shrink(img, TextureMaxSide);
            img.GenerateMipmaps();
            // GPU block compression (DXT): ~4-8x less video memory for a ring of cabinets. Godot's own encoder
            // where it has one (the editor), else ours (an exported lobby). DetectAlpha first, as compression hides it.
            var alpha = AlphaOf(img);
            if (DxtEncoder.GodotCan)
            {
                _ = img.Compress(Image.CompressMode.S3Tc, Image.CompressSource.Generic);
            }
            else
            {
                img = DxtEncoder.Compress(img, alpha != Image.AlphaMode.None);
            }
            images[name] = img;
            alphaModes[name] = alpha;
        }

        // The backglass gets a surface of its own, so the lobby can swap in the VPS picture if it has none.
        var surfaces = new List<SurfaceData>();
        foreach (
            var group in model
                .Parts.Select(x => (Part: x, Edge: false))
                .Concat(model.EdgeParts.Select(x => (Part: x, Edge: true)))
                .GroupBy(e =>
                    (
                        Image: e.Part.Image?.ToLowerInvariant(),
                        Mat: e.Part.Material?.Name,
                        Lit: MathF.Round(e.Part.SelfLit, 1),
                        Bg: IsBackglass(e.Part.Name),
                        e.Edge
                    )
                )
        )
        {
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var idx = new List<int>();
            foreach (var part in group.Select(e => e.Part))
            {
                var start = verts.Count;
                for (var i = 0; i < part.VertexCount; i++)
                {
                    verts.Add(
                        new Vector3(
                            part.Positions[i * 3],
                            part.Positions[i * 3 + 1],
                            part.Positions[i * 3 + 2]
                        )
                    );
                    norms.Add(
                        new Vector3(
                            part.Normals[i * 3],
                            part.Normals[i * 3 + 1],
                            part.Normals[i * 3 + 2]
                        )
                    );
                    uvs.Add(new Vector2(part.Uvs[i * 2], part.Uvs[i * 2 + 1]));
                }
                foreach (var k in part.Indices)
                {
                    idx.Add(start + k);
                }
            }
            var first = group.First().Part;
            surfaces.Add(
                new SurfaceData(
                    // Edge parts (VpxCabinetModel.EdgeParts) are named for it: kept apart from the cabinet in Build.
                    group.Key.Edge
                        ? EdgePrefix + first.Name
                        : first.Name,
                    verts.ToArray(),
                    norms.ToArray(),
                    uvs.ToArray(),
                    idx.ToArray(),
                    first.Image,
                    first.Material,
                    group.Key.Lit,
                    group.Key.Bg
                )
            );
        }

        var c = model.PlayfieldCorners;
        return new Parsed
        {
            Surfaces = surfaces,
            Images = images,
            Alpha = alphaModes,
            PlayfieldCentre = (ToGodot(c[0]) + ToGodot(c[2])) / 2,
            PlayfieldSize = new Vector2(Mathf.Abs(c[1].X - c[0].X), (c[3] - c[0]).Length()),
            PlayfieldSlope = model.PlayfieldSlope,
            Bounds = new Aabb(ToGodot(model.Min), ToGodot(model.Max - model.Min)),
            Backbox = BoundsOf(
                model.Parts.Where(x =>
                    x.Name.Contains("backbox", StringComparison.OrdinalIgnoreCase)
                )
            ),
        };
    }

    void Finish(string vpx, Parsed? parsed, float? glassZ, System.Diagnostics.Stopwatch sw)
    {
        _pending.Remove(vpx);
        // Forgotten while it was read (scrolled far past): keeping it would hold it for good, as nothing would ever
        // forget it again. It's in the disk cache now, so showing it later is quick.
        if (!_recent.Contains(vpx) && _recent.Count >= Keep)
        {
            return;
        }

        if (!_recent.Contains(vpx))
        {
            _recent.AddLast(vpx);
        }

        RealCabinet? cab = null;
        try
        {
            if (parsed != null)
            {
                cab = Build(parsed, glassZ);
            }
        }
        catch (Exception e)
        {
            GD.PushWarning($"Cabinet for {vpx}: {e.Message}");
        }
        LastLoadMs = sw.ElapsedMilliseconds;
        _done[vpx] = cab;
        while (_recent.Count > Keep)
        {
            _done.Remove(_recent.Last!.Value);
            _recent.RemoveLast();
        }
        CabinetReady?.Invoke(vpx, cab);
    }

    /// <summary>Main-thread part: textures and one mesh, a surface per look.</summary>
    static RealCabinet Build(Parsed p, float? glassZ)
    {
        var textures = p.Images.ToDictionary(
            kv => kv.Key,
            kv => (Texture2D)ImageTexture.CreateFromImage(kv.Value),
            StringComparer.OrdinalIgnoreCase
        );
        var mesh = new ArrayMesh();
        int bgSurface = -1;
        var kinds = new List<SurfaceKind>();
        var bgTextured = false;
        int kitCab = -1,
            kitBox = -1,
            kitBlades = -1,
            apron = -1,
            backPanel = -1;
        Texture2D? kitCabArt = null,
            kitBoxArt = null,
            kitBladesArt = null;
        var edgeMesh = new ArrayMesh();
        // The cabinet's own surfaces, numbered as the mesh's are; the playfield-edge parts go to edgeMesh.
        var cabinet = p
            .Surfaces.Where(s => !s.Name.StartsWith(EdgePrefix, StringComparison.Ordinal))
            .ToList();
        foreach (var s in p.Surfaces)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Godot.Mesh.ArrayType.Max);
            arrays[(int)Godot.Mesh.ArrayType.Vertex] = s.Verts;
            arrays[(int)Godot.Mesh.ArrayType.Normal] = s.Norms;
            arrays[(int)Godot.Mesh.ArrayType.TexUV] = s.Uvs;
            arrays[(int)Godot.Mesh.ArrayType.Index] = s.Indices;
            Texture2D? tex = s.Image != null && textures.TryGetValue(s.Image, out var t) ? t : null;
            // The table's playfield-edge parts: not drawn with its cabinet, only looked at for its wall art.
            if (s.Name.StartsWith(EdgePrefix, StringComparison.Ordinal))
            {
                edgeMesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
                edgeMesh.SurfaceSetMaterial(
                    edgeMesh.GetSurfaceCount() - 1,
                    MaterialFor(
                        s.Material,
                        tex,
                        s.Image != null && p.Alpha.TryGetValue(s.Image, out var ea)
                            ? ea
                            : Image.AlphaMode.None,
                        s.Lit
                    )
                );
                continue;
            }

            mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
            if (s.Backglass && (bgSurface < 0 || tex != null))
            {
                bgSurface = mesh.GetSurfaceCount() - 1;
                bgTextured = tex != null;
            }
            kinds.Add(KindOf(s.Name, tex != null, s.Backglass));
            // The kit's art parts (the default cabinet's have blank stand-in sheets, but the parts are what count there).
            if (
                kitCab < 0
                && tex != null
                && RealCabinet.IsKitPart(s.Name, s.Image, "PinCab_Cabinet")
            )
            {
                kitCab = mesh.GetSurfaceCount() - 1;
                kitCabArt = RealCabinet.KitShaped(tex, 1f) ? tex : null;
            }
            else if (
                kitBox < 0
                && tex != null
                && RealCabinet.IsKitPart(s.Name, s.Image, "PinCab_Backbox")
            )
            {
                kitBox = mesh.GetSurfaceCount() - 1;
                kitBoxArt = RealCabinet.KitShaped(tex, 0.5f) ? tex : null;
            }
            else if (
                kitBlades < 0
                && tex != null
                && RealCabinet.IsKitPart(s.Name, s.Image, "PinCab_Blades")
            )
            {
                kitBlades = mesh.GetSurfaceCount() - 1;
                kitBladesArt = RealCabinet.KitShaped(tex, 1f) ? tex : null;
            }
            if (apron < 0 && tex != null && NameHas(s, "apron"))
            {
                apron = mesh.GetSurfaceCount() - 1;
            }
            else if (
                backPanel < 0
                && tex != null
                && (
                    NameHas(s, "back_wall")
                    || NameHas(s, "backwall")
                    || NameHas(s, "backpanel")
                    || NameHas(s, "backboard")
                )
            )
            {
                backPanel = mesh.GetSurfaceCount() - 1;
            }
            var alpha =
                s.Image != null && p.Alpha.TryGetValue(s.Image, out var a)
                    ? a
                    : Image.AlphaMode.None;
            mesh.SurfaceSetMaterial(
                mesh.GetSurfaceCount() - 1,
                MaterialFor(s.Material, tex, alpha, s.Lit)
            );
        }

        // The backbox's front opening, for a picture that takes the whole front (Cabinet: an "empty" backbox): across
        // and down to the top of its glass part, down to the bottom of its speaker grilles, just in front of those.
        Rect2? opening = null;
        var openingZ = 0f;
        if (bgSurface >= 0 && p.Backbox is { } bbox)
        {
            var glass = BoundsOf(cabinet[bgSurface].Verts);
            var bottom = glass.Position.Y;
            var front = glass.End.Z;
            for (var i = 0; i < cabinet.Count; i++)
            {
                if (
                    kinds[i] is SurfaceKind.Grille or SurfaceKind.Dmd
                    && BoundsOf(cabinet[i].Verts) is var g
                    && bbox.Grow(0.02f).Encloses(g)
                )
                {
                    bottom = Mathf.Min(bottom, g.Position.Y);
                    front = Mathf.Max(front, g.End.Z);
                }
            }
            opening = new Rect2(glass.Position.X, bottom, glass.Size.X, glass.End.Y - bottom);
            openingZ = front + 0.004f;
        }

        return new RealCabinet
        {
            Mesh = mesh,
            PlayfieldCentre = p.PlayfieldCentre,
            PlayfieldSize = p.PlayfieldSize,
            PlayfieldSlope = p.PlayfieldSlope,
            Bounds = p.Bounds,
            BackglassSurface = bgSurface,
            BackglassTextured = bgTextured,
            Kinds = kinds,
            KitCabinetSurface = kitCab,
            KitBackboxSurface = kitBox,
            KitCabinetArt = kitCabArt,
            KitBackboxArt = kitBoxArt,
            KitBladesSurface = kitBlades,
            ApronSurface = apron,
            BackPanelSurface = backPanel,
            KitBladesArt = kitBladesArt,
            Backbox = p.Backbox,
            FrontOpening = opening,
            RailTops = RailTopsOf(p),
            EdgeMesh = edgeMesh,
            FrontOpeningZ = openingZ,
            GlassZ = glassZ,
            Textures = p.Images.Count,
            CompressedTextures = p.Images.Values.Count(i => i.IsCompressed()),
        };
    }

    static bool NameHas(SurfaceData s, string word) =>
        s.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
        || s.Image?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The highest point of the side rails over the front and back quarters of the playfield (see RealCabinet.RailTops),
    /// from the parts named for rails (not the lockdown bar). Null without them, or when they don't reach both ends.
    /// </summary>
    static (Vector2 Front, Vector2 Back)? RailTopsOf(Parsed p)
    {
        var c = p.PlayfieldCentre;
        var half = p.PlayfieldSize.Y / 2;
        Vector2? front = null,
            back = null;
        foreach (var s in p.Surfaces)
        {
            if (
                s.Name.StartsWith(EdgePrefix, StringComparison.Ordinal)
                || !s.Name.Contains("rail", StringComparison.OrdinalIgnoreCase)
                || s.Name.Contains("lockdown", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            foreach (var v in s.Verts)
            {
                if (Mathf.Abs(v.X - c.X) < p.PlayfieldSize.X * 0.35f)
                {
                    continue; // over the middle of the playfield: not a side rail
                }

                if (v.Z > c.Z + half * 0.5f && (front == null || v.Y > front.Value.Y))
                {
                    front = new Vector2(v.Z, v.Y);
                }
                else if (v.Z < c.Z - half * 0.5f && (back == null || v.Y > back.Value.Y))
                {
                    back = new Vector2(v.Z, v.Y);
                }
            }
        }
        return front is { } f && back is { } b && f.X - b.X > half ? (f, b) : null;
    }

    static Vector3 ToGodot(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    static bool IsBackglass(string name) =>
        name.Contains("backglass", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How a picture's alpha is drawn. VPX only blends a part whose material has opacity on (handled in MaterialFor);
    /// a texture's alpha just cuts pixels away. So a picture is drawn see-through (blended) only when a real share
    /// of it is partly transparent, and otherwise as a cut-out. Godot's DetectAlpha calls a picture "blend" over a
    /// handful of soft edge pixels, which put Big Trouble in Little China's whole cabinet on the blended path:
    /// there, the cabinet's inside (drawn after its front, in the same mesh) showed through the front panel.
    /// </summary>
    internal static Image.AlphaMode AlphaOf(Image img)
    {
        if (img.GetFormat() != Image.Format.Rgba8)
        {
            return img.DetectAlpha(); // no alpha channel (or one we don't read): as Godot says
        }

        var data = img.GetData();
        var pixels = img.GetWidth() * img.GetHeight(); // the full-size level; any mipmaps follow it
        int clear = 0,
            partial = 0,
            seen = 0;
        for (var i = 0; i < pixels; i += 3, seen++)
        {
            var a = data[i * 4 + 3];
            if (a < 8)
            {
                clear++;
            }
            else if (a < 248)
            {
                partial++;
            }
        }
        if (clear == 0 && partial == 0)
        {
            return Image.AlphaMode.None;
        }

        return partial > seen / 100 ? Image.AlphaMode.Blend : Image.AlphaMode.Bit;
    }

    /// <summary>One flat colour (every sampled pixel within a few levels of the first).</summary>
    internal static bool IsBlank(Image img)
    {
        int w = img.GetWidth(),
            h = img.GetHeight();
        var first = img.GetPixel(0, 0);
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var c = img.GetPixel(
                    Math.Min(w - 1, x * w / 16 + w / 32),
                    Math.Min(h - 1, y * h / 16 + h / 32)
                );
                if (
                    Math.Abs(c.R - first.R) + Math.Abs(c.G - first.G) + Math.Abs(c.B - first.B)
                    > 0.06f
                )
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Untextured parts (metal, chrome, black panels) belong to any table; textured ones carry this
    /// table's art and get replaced when another table borrows the cabinet.
    /// </summary>
    static SurfaceKind KindOf(string name, bool textured, bool backglass)
    {
        if (backglass)
        {
            return SurfaceKind.Backglass;
        }

        if (!textured)
        {
            return SurfaceKind.Keep;
        }

        if (
            name.Contains("grill", StringComparison.OrdinalIgnoreCase)
            || name.Contains("speaker", StringComparison.OrdinalIgnoreCase)
        )
        {
            return SurfaceKind.Grille;
        }

        if (name.Contains("dmd", StringComparison.OrdinalIgnoreCase))
        {
            return SurfaceKind.Dmd;
        }

        return SurfaceKind.Art;
    }

    static Aabb BoundsOf(Vector3[] verts)
    {
        var box = new Aabb(verts.Length > 0 ? verts[0] : Vector3.Zero, Vector3.Zero);
        foreach (var v in verts)
        {
            box = box.Expand(v);
        }
        return box;
    }

    static Aabb? BoundsOf(IEnumerable<VpxCabinetPart> parts)
    {
        Aabb? box = null;
        foreach (var part in parts)
        {
            for (var i = 0; i < part.Positions.Length; i += 3)
            {
                var v = new Vector3(
                    part.Positions[i],
                    part.Positions[i + 1],
                    part.Positions[i + 2]
                );
                box = box is { } b ? b.Expand(v) : new Aabb(v, Vector3.Zero);
            }
        }

        return box;
    }

    /// <summary>
    /// VPX material → Godot: a texture replaces the base colour (as in VPX); "metal" materials are metallic;
    /// VPX roughness runs the other way (0 = diffuse, 1 = mirror-like). Both faces are drawn, since VR
    /// cabinets mix closed meshes and single-sided panels.
    /// </summary>
    static StandardMaterial3D MaterialFor(
        VpxMaterial? m,
        Texture2D? tex,
        Image.AlphaMode alpha,
        float selfLit
    )
    {
        var mat = new StandardMaterial3D
        {
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        if (tex != null)
        {
            mat.AlbedoTexture = tex;
            // Cut-outs (grilles, bolts, decals) use alpha; scissor avoids sorting trouble.

            if (alpha == Image.AlphaMode.Bit)
            {
                mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                mat.AlphaScissorThreshold = 0.5f;
            }
            else if (alpha == Image.AlphaMode.Blend)
            {
                mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass;
            }
        }
        else if (m != null)
        {
            var (r, g, b) = m.Rgb;
            mat.AlbedoColor = new Color(r, g, b);
        }
        if (m != null)
        {
            mat.Metallic = m.IsMetal ? 1f : 0f;
            mat.Roughness = Mathf.Clamp(1f - m.Roughness, 0.06f, 1f);
            if (m.OpacityActive && m.Opacity < 0.99f)
            {
                mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                mat.AlbedoColor = new Color(mat.AlbedoColor, Mathf.Clamp(m.Opacity, 0.05f, 1f));
            }
        }
        else
        {
            mat.Roughness = 0.6f;
        }
        // "Disable lighting" in VPX means: show the texture as it is, whatever the room's lights do
        // (backglasses, and many VR cabinets' art, which has its lighting baked in). These used to be drawn
        // unshaded, which ignored the room: cabinets around the ring were as bright as the one in front of you,
        // and lifting the selected one pushed its art past white into the bloom. Now they're plain PBR like the rest,
        // with no glow of their own: the cabinet in front of you is brighter only because it stands in the spotlight.
        // See-through self-lit parts (the glass over a backglass) stay unshaded: shaded, they catch the room's
        // reflections and haze the picture behind them over in milky white (Medieval Madness, Monster Bash).
        if (
            selfLit >= 0.5f
            && mat.Transparency
                is BaseMaterial3D.TransparencyEnum.Alpha
                    or BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass
        )
        {
            mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            mat.Metallic = 0;
        }
        else if (selfLit >= 0.5f)
        {
            mat.Metallic = 0;
            mat.Roughness = Mathf.Max(mat.Roughness, 0.55f); // printed art: no mirror-like highlights to blow out
        }
        return mat;
    }
}
