using System.Numerics;
using System.Text.RegularExpressions;

namespace VrTableManager.Services;

/// <summary>
/// A table's own toys (ramps, plastics, toys and the rest standing on its playfield, as 3D primitives), ready to draw
/// over the lobby's playfield video (see <see cref="VpxCabinet.Toys"/>). Parts are in playfield space, metres: x across
/// from the playfield's centre (right), y up from its surface, z along it from its centre (towards the player), the
/// playfield lying flat. The lobby tips and scales them onto whichever cabinet's playfield shows the table.
/// </summary>
public sealed class VpxToyModel
{
    public List<VpxCabinetPart> Parts { get; init; } = [];

    /// <summary>The table's playfield, metres: x across, y along.</summary>
    public Vector2 PlayfieldSize { get; init; }

    /// <summary>Vertices in the toys found, whether kept or not (over the budget, <see cref="Parts"/> is empty).</summary>
    public int Vertices { get; init; }
    public bool OverBudget { get; init; }

    /// <summary>Primitives taken as toys.</summary>
    public int Primitives { get; init; }
}

public static partial class VpxCabinet
{
    /// <summary>Lightest top a toy may have above the playfield (VPX units, ≈ 5 mm): flatter parts are in the video already.</summary>
    const float ToyMinTop = 0.005f / VpuToMetres;

    // The playfield itself (VPW's "BM_Playfield", a desktop "Playfield_Mesh"), the parts that are the cabinet, the
    // room or light rather than something standing on the playfield, a flipper's raised pose (VPW's "BM_LeftFlipperUp",
    // Fish Tales's "BM_FlipperLUp", shown by the script in place of the resting one) and an option's other look
    // ("BM_RampCoversAlt"). Words, not letters inside one: each starts the name, follows "_" or a digit, or starts a
    // capitalised word ("BallShadow"), so "Mushroom", "Hourglass" and "Cobalt" stay toys.
    static readonly Regex NotToy = new(
        @"(?:^|(?<=[^A-Za-z])|(?<=[a-z])(?=[A-Z]))(?i:playfield|pincab|cabinet|cab(?-i:(?![a-z]))|blade|siderail|side_?rail|lockdown|lockbar|backbox|backglass|room|floor|ceiling|glass|reflect|shadow|vr_)"
            + @"|(?i:flipper[lr]?\d*up)$|(?:_(?i:alt)|Alt)\d*$"
    );

    /// <summary>
    /// The table's toys: its visible 3D primitives with a picture or a material, standing on the playfield (up to 45 cm
    /// above it, within its outline), not cabinet parts (<see cref="Load"/>'s or <see cref="LooksLikeCabinetPart"/>-named
    /// ones on the playfield's edge), not the VR room, not VLM lightmaps ("LM_"), not the playfield's own mesh or anything
    /// else lying flat on it, and not the thin wall parts the lobby copies onto the default cabinet's walls. VPX ramps, walls and
    /// rubbers (built from curves) are left out: tables built from those (most older ones) have few toys this way.
    /// Over <paramref name="maxVertices"/> in all, no parts come back, only the count. Null when the table can't be read.
    /// </summary>
    public static VpxToyModel? Toys(string vpxPath, int maxVertices)
    {
        try
        {
            var t = Read(vpxPath, meshes: true);
            float w = t.Right - t.Left,
                h = t.Bottom - t.Top;
            if (w <= 0 || h <= 0)
            {
                return null;
            }

            ShowScriptShown(t, vpxPath);
            var picked = Pick(t);
            const float cm = 1f / 100f / VpuToMetres;
            // Lit and unlit copies of a part, "…gioff" and "…gion" (Tales of the Arabian Nights's ramp, plastics, screws
            // and walls): the file shows the unlit one and the script swaps in the lit one, or hides both until it
            // starts. Drawn once, lit, as the video shows the table: dropping the unlit copy and skipping the hidden lit
            // one left that table's back half bare.
            var byName = t
                .Prims.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var litOf = new Dictionary<Prim, Prim>();
            foreach (var p in t.Prims.Where(p => OffVariant.IsMatch(p.Name)))
            {
                var i = p.Name.LastIndexOf("off", StringComparison.OrdinalIgnoreCase);
                if (
                    i >= 0
                    && byName.TryGetValue(p.Name[..i] + "on" + p.Name[(i + 3)..], out var lit)
                    && lit != p
                )
                {
                    litOf[p] = lit;
                }
            }
            var unlitOf = litOf
                .GroupBy(kv => kv.Value)
                .ToDictionary(g => g.Key, g => g.First().Key);
            // Of the edge parts the lobby may copy onto the default cabinet's walls, only the walls themselves: all
            // but a few of their vertices in the outer bands, each band's no more than 4 cm deep. The rest (Medieval
            // Madness's wire ramps, Attack from Mars's chrome rails and clear ramps, Elvira's screws) only reach the
            // edge: the lobby leaves them off its walls, piece by piece, so they belong here. Skipping them all left
            // most tables' ramps and metal shown nowhere.
            var edge = EdgePrims(t, picked).ToHashSet();
            var skip = picked.ToHashSet();
            var toys = t
                .Prims.Where(p =>
                    !unlitOf.ContainsKey(p) // a lit copy: comes in through its unlit one
                    && (p.Visible || litOf.ContainsKey(p))
                )
                .Select(p => litOf.GetValueOrDefault(p) ?? p)
                .Where(p =>
                    !skip.Contains(p)
                    && !(
                        (edge.Contains(p) || unlitOf.TryGetValue(p, out var u) && edge.Contains(u))
                        && IsWallOnly(p, t)
                    )
                    && p.Use3DMesh
                    && p.Verts != null
                    && p.Indices != null
                    && p.NumVerts > 0
                    // A picture, or at least a material: metal rails, wire guides and brackets usually have only a
                    // metal material, and are drawn in its colour.
                    && (
                        !string.IsNullOrWhiteSpace(p.Image)
                        || p.Material != null && t.Materials.ContainsKey(p.Material)
                    )
                    && !NotGeometry.IsMatch(p.Name)
                    && !NotToy.IsMatch(p.Name)
                    && SpaceOf(p.Group, t) == 0
                )
                .Where(p =>
                {
                    var (a, b) = Bounds(p, t);
                    if (
                        b.Z < ToyMinTop
                        || b.Z > 45 * cm
                        || a.Z < -15 * cm
                        || a.X < t.Left - w * 0.05f
                        || b.X > t.Right + w * 0.05f
                        || a.Y < t.Top - h * 0.08f
                        || b.Y > t.Bottom + h * 0.05f
                    )
                    {
                        return false;
                    }
                    // Thousands of vertices on a few millimetres: Batman 66's posts are 25,000 each, 1.6 cm across, and
                    // thirty of them came to 750,000. A detailed figure is a few hundred to a cm² of floor.
                    var footprint =
                        MathF.Max(b.X - a.X, 0.5f * cm) * MathF.Max(b.Y - a.Y, 0.5f * cm);
                    if (p.NumVerts > 5_000 && p.NumVerts / (footprint / (cm * cm)) > 10_000)
                    {
                        return false;
                    }
                    // Playfield-sized and nearly flat: a second playfield (an insert or lighting layer), not a toy.
                    return !(b.X - a.X >= w * 0.8f && b.Y - a.Y >= h * 0.8f && b.Z < 1 * cm);
                })
                .ToList();
            toys = DropPoses(toys, t);
            // A lit copy beside the unlit one (24's "Safehouse_on" over "Safehouse"): the script shows one at a time.
            var names = toys.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // And an unlit copy beside the lit one (The Addams Family's "RGP9_GI1_5off" over "RGP9_GI1_5"): the video
            // shows the table lit.
            toys = toys.Where(p =>
                    !(
                        p.Name.EndsWith("_on", StringComparison.OrdinalIgnoreCase)
                        && names.Contains(p.Name[..^3])
                    )
                    && !(
                        p.Name.EndsWith("off", StringComparison.OrdinalIgnoreCase)
                        && names.Contains(p.Name[..^3].TrimEnd('_'))
                    )
                )
                .ToList();

            var vertices = toys.Sum(p => p.NumVerts);
            var size = new Vector2(w * VpuToMetres, h * VpuToMetres);
            if (vertices > maxVertices)
            {
                return new VpxToyModel
                {
                    PlayfieldSize = size,
                    Vertices = vertices,
                    Primitives = toys.Count,
                    OverBudget = true,
                };
            }

            float cx = (t.Left + t.Right) / 2,
                cy = (t.Top + t.Bottom) / 2;
            var parts = new List<VpxCabinetPart>();
            foreach (var p in toys)
            {
                var m = FullMatrix(p, t);
                var normalMatrix = Matrix4x4.Invert(m, out var inv) ? Matrix4x4.Transpose(inv) : m;
                var n = p.NumVerts;
                var pos = new float[n * 3];
                var nrm = new float[n * 3];
                var uv = new float[n * 2];
                var world = new Vector3[n];
                for (var i = 0; i < n; i++)
                {
                    var o = i * 8;
                    var v = Vector3.Transform(
                        new Vector3(p.Verts![o], p.Verts[o + 1], p.Verts[o + 2]),
                        m
                    );
                    world[i] = v;
                    var nn = Vector3.TransformNormal(
                        new Vector3(p.Verts[o + 3], p.Verts[o + 4], p.Verts[o + 5]),
                        normalMatrix
                    );
                    // Up where a normal is missing or broken: Die Hard Trilogy's "BM_BIPL" has infinite ones, and Godot
                    // warned (with a backtrace) for each of thousands of them, holding the lobby for over a second.
                    nn =
                        nn.LengthSquared() is var len && float.IsFinite(len) && len > 1e-12f
                            ? Vector3.Normalize(nn)
                            : Vector3.UnitZ;
                    // VPX (x right, y towards the player, z up) → playfield space (x right, y up, z towards the player),
                    // the same swap as Load, so VPX's index order gives Godot's front faces as it does there.
                    pos[i * 3] = (v.X - cx) * VpuToMetres;
                    pos[i * 3 + 1] = v.Z * VpuToMetres;
                    pos[i * 3 + 2] = (v.Y - cy) * VpuToMetres;
                    nrm[i * 3] = nn.X;
                    nrm[i * 3 + 1] = nn.Z;
                    nrm[i * 3 + 2] = nn.Y;
                    uv[i * 2] = p.Verts[o + 6];
                    uv[i * 2 + 1] = p.Verts[o + 7];
                }
                parts.Add(
                    new VpxCabinetPart
                    {
                        Name = p.Name,
                        Positions = pos,
                        Normals = nrm,
                        Uvs = uv,
                        Indices = skip.Any(s => s.Name == p.Name + "_BackBoard")
                            ? WithoutBackBoard(p.Indices!, world, t)
                            : (int[])p.Indices!.Clone(),
                        Image = p.Image,
                        Material =
                            p.Material != null && t.Materials.TryGetValue(p.Material, out var mat)
                                ? mat
                                : null,
                        SelfLit = Math.Clamp(p.SelfLit, 0f, 1f),
                    }
                );
            }
            return new VpxToyModel
            {
                Parts = parts,
                PlayfieldSize = size,
                Vertices = vertices,
                Primitives = toys.Count,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A baked mesh's triangles less the back board the cabinet took out of it (<see cref="BakedBackBoard"/>, Fish
    /// Tales's "BM_Parts_BackBoard"), by the same test: upright, facing along the playfield, in the band at its back.
    /// Left in, the board was drawn twice. <paramref name="world"/>: the mesh's vertices in VPX playfield space.
    /// </summary>
    static int[] WithoutBackBoard(int[] indices, Vector3[] world, TableData t)
    {
        bool InBand(Vector3 v) =>
            v.Y >= t.Top - 60
            && v.Y <= t.Top + 40
            && v.Z is >= -10 and <= 500
            && v.X >= t.Left - 60
            && v.X <= t.Right + 60;
        var kept = new List<int>(indices.Length);
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            Vector3 a = world[indices[i]],
                b = world[indices[i + 1]],
                c = world[indices[i + 2]];
            var n = Vector3.Cross(b - a, c - a);
            var board =
                InBand(a)
                && InBand(b)
                && InBand(c)
                && n.LengthSquared() >= 1e-6f
                && MathF.Abs(Vector3.Normalize(n).Y) >= 0.7f;
            if (!board)
            {
                kept.Add(indices[i]);
                kept.Add(indices[i + 1]);
                kept.Add(indices[i + 2]);
            }
        }
        return kept.ToArray();
    }

    /// <summary>
    /// <summary>
    /// True when a part is only wall the lobby keeps on the default cabinet (RealCabinet.OwnWallsOn), by its numbers:
    /// at least 90% of its vertices in the playfield's outer eighth either side or its back eighth; a side's no more
    /// than 3 cm deep across, at least 2 cm high and along at least 70% of the playfield; the back's at least 10 cm
    /// high, across at least a fifth of the playfield, and no deeper along it than 8 cm or 15% of its height (a board
    /// leaning back, or a sign on it; the lobby measures where most of its area is, finer than this). Tales of
    /// the Arabian Nights's "sidewallsgion" (both side walls in one mesh) and X-Men's Sentinel board are walls; a wire
    /// ramp down a side (Medieval Madness's catapult ramp, 8 cm across), Batman 66's 3 cm metal walls along half the
    /// sides and The Getaway's low guide wall at the back are not, and stay toys: the lobby would leave them off its
    /// walls too, and skipped here they'd show nowhere.
    /// </summary>
    static bool IsWallOnly(Prim p, TableData t)
    {
        if (p.Verts == null || p.NumVerts == 0)
        {
            return false;
        }

        const float cm = 1f / 100f / VpuToMetres;
        float w = t.Right - t.Left,
            h = t.Bottom - t.Top;
        var m = FullMatrix(p, t);
        // Per band (left, right, back): its vertices' extent in x, y and z.
        var lo = new Vector3[3];
        var hi = new Vector3[3];
        var any = new bool[3];
        var inner = 0;
        for (var i = 0; i < p.NumVerts; i++)
        {
            var o = i * 8;
            var v = Vector3.Transform(new Vector3(p.Verts[o], p.Verts[o + 1], p.Verts[o + 2]), m);
            bool left = v.X <= t.Left + w / 8,
                right = v.X >= t.Right - w / 8,
                back = v.Y <= t.Top + h / 8;
            // The back corners are both: they count as wall but measure neither, or a side wall's back end made it
            // look 14 cm deep along, and a back board's ends as deep across.
            var band =
                back && !left && !right ? 2
                : left && !back ? 0
                : right && !back ? 1
                : -1;
            if (band >= 0)
            {
                lo[band] = any[band] ? Vector3.Min(lo[band], v) : v;
                hi[band] = any[band] ? Vector3.Max(hi[band], v) : v;
                any[band] = true;
            }
            else if (!back && !left && !right)
            {
                inner++;
            }
        }

        if (inner > p.NumVerts * 0.1f || !any.Any(a => a))
        {
            return false;
        }

        for (var s = 0; s < 2; s++)
        {
            if (
                any[s]
                && (
                    hi[s].X - lo[s].X > 3 * cm
                    || hi[s].Z - MathF.Max(lo[s].Z, 0) < 2 * cm
                    || hi[s].Y - lo[s].Y < h * 0.7f
                )
            )
            {
                return false;
            }
        }

        var height = hi[2].Z - MathF.Max(lo[2].Z, 0);
        return !any[2]
            || height >= 10 * cm
                && hi[2].X - lo[2].X >= w * 0.2f
                && hi[2].Y - lo[2].Y <= MathF.Max(8 * cm, height * 0.15f);
    }

    static readonly Regex NumberedName = new(@"^(?<stem>.*?)(?<n>\d+)$");

    /// <summary>
    /// A toy's animation frames: parts named alike but for a number at the end ("BM_LeftSling1" to "BM_LeftSling4"),
    /// with the same picture, in the same place (within 1 cm). The script shows one at a time; drawn together they
    /// flicker through each other. The lowest-numbered is kept.
    /// </summary>
    static List<Prim> DropPoses(List<Prim> toys, TableData t)
    {
        const float cm = 1f / 100f / VpuToMetres;
        var kept = new List<(Prim P, string? Stem, Vector3 A, Vector3 B)>();
        // Lowest number first, whatever the file order: "Sling1" is the resting one, not "Sling3".
        foreach (
            var p in toys.OrderBy(p =>
                NumberedName.Match(p.Name) is { Success: true } m
                && int.TryParse(m.Groups["n"].Value, out var n)
                    ? n
                    : 0
            )
        )
        {
            var stem = NumberedName.Match(p.Name) is { Success: true } m
                ? m.Groups["stem"].Value
                : null;
            var (a, b) = Bounds(p, t);
            if (
                stem != null
                && kept.Any(k =>
                    string.Equals(k.Stem, stem, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(k.P.Image, p.Image, StringComparison.OrdinalIgnoreCase)
                    && Vector3.Distance(k.A, a) < cm
                    && Vector3.Distance(k.B, b) < cm
                )
            )
            {
                continue;
            }

            kept.Add((p, stem, a, b));
        }
        return kept.Select(k => k.P).ToList();
    }
}
