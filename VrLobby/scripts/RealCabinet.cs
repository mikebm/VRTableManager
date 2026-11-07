using Godot;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>What a cabinet surface shows: kept as is, or this table's art / grille / display / backglass.</summary>
public enum SurfaceKind { Keep, Art, Grille, Dmd, Backglass }

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
    internal static float ComputeGlassZ(IEnumerable<(Vector3[] Verts, int[] Indices)> surfaces, Aabb box)
    {
        var front = box.End.Z;
        var cx = box.GetCenter().X;
        var cy = GlassCentreY(box);
        float? hit = null;
        foreach (var (v, idx) in surfaces)
            for (var i = 0; i + 2 < idx.Length; i += 3)
            {
                Vector3 a = v[idx[i]], b = v[idx[i + 1]], c = v[idx[i + 2]];
                if (Mathf.Min(a.Z, Mathf.Min(b.Z, c.Z)) > front + 0.02f) continue; // in front of the box (a topper, a lamp)
                // Cheap reject: seen from the front, the triangle's outline doesn't reach (cx, cy).
                if (cx < Mathf.Min(a.X, Mathf.Min(b.X, c.X)) || cx > Mathf.Max(a.X, Mathf.Max(b.X, c.X))
                    || cy < Mathf.Min(a.Y, Mathf.Min(b.Y, c.Y)) || cy > Mathf.Max(a.Y, Mathf.Max(b.Y, c.Y))) continue;
                // Does the triangle cover (cx, cy)? Then where along z.
                var d = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (Mathf.Abs(d) < 1e-9f) continue;
                var w1 = ((cx - a.X) * (c.Y - a.Y) - (cy - a.Y) * (c.X - a.X)) / d;
                var w2 = ((b.X - a.X) * (cy - a.Y) - (b.Y - a.Y) * (cx - a.X)) / d;
                if (w1 < 0 || w2 < 0 || w1 + w2 > 1) continue;
                var z = a.Z + w1 * (b.Z - a.Z) + w2 * (c.Z - a.Z);
                if (hit == null || z > hit) hit = z;
            }
        return hit is { } h ? Mathf.Max(h + 0.003f, front - 0.04f) : front - 0.02f;
    }

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
    public const int Keep = 14;   // the 11 in the ring plus a few just scrolled past

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

    public void Request(string vpx)
    {
        _recent.Remove(vpx);
        _recent.AddFirst(vpx);
        if (_done.TryGetValue(vpx, out var known)) { CabinetReady?.Invoke(vpx, known); return; }
        if (!_pending.Add(vpx)) return;
        var dir = CacheDir;
        _ = Task.Run(async () =>
        {
            await _oneAtATime.WaitAsync();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Parsed? parsed;
                if (dir != null && CabinetCache.TryLoad(vpx, dir, out var cached)) { parsed = cached; Interlocked.Increment(ref _cacheHits); }
                else
                {
                    parsed = Parse(vpx);
                    if (dir != null) CabinetCache.Save(vpx, dir, parsed);
                }
                var glass = GlassZOf(parsed); // walks every triangle: here, not on the main thread
                Callable.From(() => Finish(vpx, parsed, glass, sw)).CallDeferred();
            }
            catch
            {
                Callable.From(() => Finish(vpx, null, null, sw)).CallDeferred();
            }
            finally { _oneAtATime.Release(); }
        });
    }

    /// <summary>
    /// The lobby's default cabinet, worn by every table without its own: a cabinet dumped once from a
    /// table (<see cref="DumpDefault"/>) and shipped with the lobby, so it's there at once, table installed or not.
    /// </summary>
    public static RealCabinet? LoadDefault(string file) =>
        CabinetCache.TryLoadDump(file, out var parsed) && parsed != null ? Build(parsed, GlassZOf(parsed)) : null;

    static float? GlassZOf(Parsed? p) =>
        p?.Backbox is { } box ? RealCabinet.ComputeGlassZ(p.Surfaces.Select(s => (s.Verts, s.Indices)), box) : null;

    /// <summary>
    /// Reads this table's VR cabinet and writes it as the default cabinet file; false if it has none.
    /// Its pictures (the table's printed art) are left out: tables borrowing the cabinet cover every
    /// textured surface with their own look (Cabinet.DressBorrowed), so each becomes a plain grey stand-in
    /// that only keeps the surface counted as textured.
    /// </summary>
    public static bool DumpDefault(string vpx, string file)
    {
        var parsed = Parse(vpx);
        if (parsed == null) return false;
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
    internal sealed record SurfaceData(string Name, Vector3[] Verts, Vector3[] Norms, Vector2[] Uvs, int[] Indices,
        string? Image, VpxMaterial? Material, float Lit, bool Backglass);

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
    static Parsed? Parse(string vpx)
    {
        var model = VpxCabinet.Load(vpx, CabinetKit.Front);
        if (model == null) return null;
        var images = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        var alphaModes = new Dictionary<string, Image.AlphaMode>(StringComparer.OrdinalIgnoreCase);
        var catalogue = VpxImages.ListImages(vpx);
        var backglassImages = model.Parts.Where(p => IsBackglass(p.Name) && p.Image != null).Select(p => p.Image!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in model.ImageNames)
        {
            var info = catalogue.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (info == null) continue;
            var bytes = VpxImages.ExtractImage(vpx, info.Index);
            if (bytes == null) continue;
            var img = ArtLoader.Decode(bytes);
            if (img == null) continue;
            if (img.IsCompressed()) img.Decompress();
            // A backglass picture that's one flat colour (Harry Potter's is plain black) is a placeholder the
            // table's script or PuP pack draws over while playing: leave it out, so the VPS backglass shows.
            if (backglassImages.Contains(name) && IsBlank(img)) continue;
            ArtLoader.Shrink(img, TextureMaxSide);
            img.GenerateMipmaps();
            // GPU block compression (DXT): ~4-8x less video memory for a ring of cabinets. Uncompressed if
            // this Godot build can't compress at runtime. DetectAlpha first, as compression hides it.
            var alpha = AlphaOf(img);
            _ = img.Compress(Image.CompressMode.S3Tc, Image.CompressSource.Generic);
            images[name] = img;
            alphaModes[name] = alpha;
        }

        // The backglass gets a surface of its own, so the lobby can swap in the VPS picture if it has none.
        var surfaces = new List<SurfaceData>();
        foreach (var group in model.Parts.GroupBy(x => (Image: x.Image?.ToLowerInvariant(), Mat: x.Material?.Name, Lit: MathF.Round(x.SelfLit, 1), Bg: IsBackglass(x.Name))))
        {
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var idx = new List<int>();
            foreach (var part in group)
            {
                var start = verts.Count;
                for (var i = 0; i < part.VertexCount; i++)
                {
                    verts.Add(new Vector3(part.Positions[i * 3], part.Positions[i * 3 + 1], part.Positions[i * 3 + 2]));
                    norms.Add(new Vector3(part.Normals[i * 3], part.Normals[i * 3 + 1], part.Normals[i * 3 + 2]));
                    uvs.Add(new Vector2(part.Uvs[i * 2], part.Uvs[i * 2 + 1]));
                }
                foreach (var k in part.Indices) idx.Add(start + k);
            }
            var first = group.First();
            surfaces.Add(new SurfaceData(first.Name, verts.ToArray(), norms.ToArray(), uvs.ToArray(), idx.ToArray(),
                first.Image, first.Material, group.Key.Lit, group.Key.Bg));
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
            Backbox = BoundsOf(model.Parts.Where(x => x.Name.Contains("backbox", StringComparison.OrdinalIgnoreCase))),
        };
    }

    void Finish(string vpx, Parsed? parsed, float? glassZ, System.Diagnostics.Stopwatch sw)
    {
        _pending.Remove(vpx);
        RealCabinet? cab = null;
        try { if (parsed != null) cab = Build(parsed, glassZ); }
        catch (Exception e) { GD.PushWarning($"Cabinet for {vpx}: {e.Message}"); }
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
        var textures = p.Images.ToDictionary(kv => kv.Key, kv => (Texture2D)ImageTexture.CreateFromImage(kv.Value), StringComparer.OrdinalIgnoreCase);
        var mesh = new ArrayMesh();
        int bgSurface = -1;
        var kinds = new List<SurfaceKind>();
        var bgTextured = false;
        foreach (var s in p.Surfaces)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Godot.Mesh.ArrayType.Max);
            arrays[(int)Godot.Mesh.ArrayType.Vertex] = s.Verts;
            arrays[(int)Godot.Mesh.ArrayType.Normal] = s.Norms;
            arrays[(int)Godot.Mesh.ArrayType.TexUV] = s.Uvs;
            arrays[(int)Godot.Mesh.ArrayType.Index] = s.Indices;
            mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
            Texture2D? tex = s.Image != null && textures.TryGetValue(s.Image, out var t) ? t : null;
            if (s.Backglass && (bgSurface < 0 || tex != null)) { bgSurface = mesh.GetSurfaceCount() - 1; bgTextured = tex != null; }
            kinds.Add(KindOf(s.Name, tex != null, s.Backglass));
            var alpha = s.Image != null && p.Alpha.TryGetValue(s.Image, out var a) ? a : Image.AlphaMode.None;
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, MaterialFor(s.Material, tex, alpha, s.Lit));
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
            Backbox = p.Backbox,
            GlassZ = glassZ,
            Textures = p.Images.Count,
            CompressedTextures = p.Images.Values.Count(i => i.IsCompressed()),
        };
    }

    static Vector3 ToGodot(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    static bool IsBackglass(string name) => name.Contains("backglass", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How a picture's alpha is drawn. VPX only blends a part whose material has opacity on (handled in MaterialFor);
    /// a texture's alpha just cuts pixels away. So a picture is drawn see-through (blended) only when a real share
    /// of it is partly transparent, and otherwise as a cut-out. Godot's DetectAlpha calls a picture "blend" over a
    /// handful of soft edge pixels, which put Big Trouble in Little China's whole cabinet on the blended path:
    /// there, the cabinet's inside (drawn after its front, in the same mesh) showed through the front panel.
    /// </summary>
    internal static Image.AlphaMode AlphaOf(Image img)
    {
        if (img.GetFormat() != Image.Format.Rgba8) return img.DetectAlpha(); // no alpha channel (or one we don't read): as Godot says
        var data = img.GetData();
        var pixels = img.GetWidth() * img.GetHeight(); // the full-size level; any mipmaps follow it
        int clear = 0, partial = 0, seen = 0;
        for (var i = 0; i < pixels; i += 3, seen++)
        {
            var a = data[i * 4 + 3];
            if (a < 8) clear++;
            else if (a < 248) partial++;
        }
        if (clear == 0 && partial == 0) return Image.AlphaMode.None;
        return partial > seen / 100 ? Image.AlphaMode.Blend : Image.AlphaMode.Bit;
    }

    /// <summary>One flat colour (every sampled pixel within a few levels of the first).</summary>
    internal static bool IsBlank(Image img)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        var first = img.GetPixel(0, 0);
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
            {
                var c = img.GetPixel(Math.Min(w - 1, x * w / 16 + w / 32), Math.Min(h - 1, y * h / 16 + h / 32));
                if (Math.Abs(c.R - first.R) + Math.Abs(c.G - first.G) + Math.Abs(c.B - first.B) > 0.06f) return false;
            }
        return true;
    }

    /// <summary>
    /// Untextured parts (metal, chrome, black panels) belong to any table; textured ones carry this
    /// table's art and get replaced when another table borrows the cabinet.
    /// </summary>
    static SurfaceKind KindOf(string name, bool textured, bool backglass)
    {
        if (backglass) return SurfaceKind.Backglass;
        if (!textured) return SurfaceKind.Keep;
        if (name.Contains("grill", StringComparison.OrdinalIgnoreCase) || name.Contains("speaker", StringComparison.OrdinalIgnoreCase)) return SurfaceKind.Grille;
        if (name.Contains("dmd", StringComparison.OrdinalIgnoreCase)) return SurfaceKind.Dmd;
        return SurfaceKind.Art;
    }

    static Aabb? BoundsOf(IEnumerable<VpxCabinetPart> parts)
    {
        Aabb? box = null;
        foreach (var part in parts)
            for (var i = 0; i < part.Positions.Length; i += 3)
            {
                var v = new Vector3(part.Positions[i], part.Positions[i + 1], part.Positions[i + 2]);
                box = box is { } b ? b.Expand(v) : new Aabb(v, Vector3.Zero);
            }
        return box;
    }

    /// <summary>
    /// VPX material → Godot: a texture replaces the base colour (as in VPX); "metal" materials are metallic;
    /// VPX roughness runs the other way (0 = diffuse, 1 = mirror-like). Both faces are drawn, since VR
    /// cabinets mix closed meshes and single-sided panels.
    /// </summary>
    static StandardMaterial3D MaterialFor(VpxMaterial? m, Texture2D? tex, Image.AlphaMode alpha, float selfLit)
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

            if (alpha == Image.AlphaMode.Bit) { mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor; mat.AlphaScissorThreshold = 0.5f; }
            else if (alpha == Image.AlphaMode.Blend) mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass;
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
        else mat.Roughness = 0.6f;
        // "Disable lighting" in VPX means: show the texture as it is, whatever the room's lights do
        // (backglasses, and many VR cabinets' art, which has its lighting baked in). These used to be drawn
        // unshaded, which ignored the room: cabinets around the ring were as bright as the one in front of you,
        // and lifting the selected one pushed its art past white into the bloom. Now they're plain PBR like the rest,
        // with no glow of their own: the cabinet in front of you is brighter only because it stands in the spotlight.
        // See-through self-lit parts (the glass over a backglass) stay unshaded: shaded, they catch the room's
        // reflections and haze the picture behind them over in milky white (Medieval Madness, Monster Bash).
        if (selfLit >= 0.5f && mat.Transparency is BaseMaterial3D.TransparencyEnum.Alpha or BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass)
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
