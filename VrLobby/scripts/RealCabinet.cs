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
    public required Aabb Bounds { get; init; }
    /// <summary>The surface holding the backglass part (-1: the cabinet has none), and whether it brought its own picture.</summary>
    public int BackglassSurface { get; init; } = -1;
    /// <summary>What each surface is, so another table can borrow this cabinet with its own art (see Cabinet.ShowReal).</summary>
    public IReadOnlyList<SurfaceKind> Kinds { get; init; } = [];
    public bool BackglassTextured { get; init; }
    /// <summary>Bounds of the backbox parts, for placing a backglass when the cabinet has none.</summary>
    public Aabb? Backbox { get; init; }
    public int Surfaces => Mesh.GetSurfaceCount();
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
                Callable.From(() => Finish(vpx, parsed, sw)).CallDeferred();
            }
            catch
            {
                Callable.From(() => Finish(vpx, null, sw)).CallDeferred();
            }
            finally { _oneAtATime.Release(); }
        });
    }

    /// <summary>
    /// The lobby's default cabinet, worn by every table without its own: a cabinet dumped once from a
    /// table (<see cref="DumpDefault"/>) and shipped with the lobby, so it's there at once, table installed or not.
    /// </summary>
    public static RealCabinet? LoadDefault(string file) =>
        CabinetCache.TryLoadDump(file, out var parsed) && parsed != null ? Build(parsed) : null;

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
        foreach (var name in model.ImageNames)
        {
            var info = catalogue.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (info == null) continue;
            var bytes = VpxImages.ExtractImage(vpx, info.Index);
            if (bytes == null) continue;
            var img = ArtLoader.Decode(bytes);
            if (img == null) continue;
            if (img.IsCompressed()) img.Decompress();
            ArtLoader.Shrink(img, TextureMaxSide);
            img.GenerateMipmaps();
            // GPU block compression (DXT): ~4-8x less video memory for a ring of cabinets. Uncompressed if
            // this Godot build can't compress at runtime. DetectAlpha first, as compression hides it.
            var alpha = img.DetectAlpha();
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
            PlayfieldSize = new Vector2(Mathf.Abs(c[1].X - c[0].X), Mathf.Abs(c[3].Z - c[0].Z)),
            Bounds = new Aabb(ToGodot(model.Min), ToGodot(model.Max - model.Min)),
            Backbox = BoundsOf(model.Parts.Where(x => x.Name.Contains("backbox", StringComparison.OrdinalIgnoreCase))),
        };
    }

    void Finish(string vpx, Parsed? parsed, System.Diagnostics.Stopwatch sw)
    {
        _pending.Remove(vpx);
        RealCabinet? cab = null;
        try { if (parsed != null) cab = Build(parsed); }
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
    static RealCabinet Build(Parsed p)
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
            Bounds = p.Bounds,
            BackglassSurface = bgSurface,
            BackglassTextured = bgTextured,
            Kinds = kinds,
            Backbox = p.Backbox,
            Textures = p.Images.Count,
            CompressedTextures = p.Images.Values.Count(i => i.IsCompressed()),
        };
    }

    static Vector3 ToGodot(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    static bool IsBackglass(string name) => name.Contains("backglass", StringComparison.OrdinalIgnoreCase);

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
        // (backglasses, and many VR cabinets' art, which has its lighting baked in). That's Godot's
        // unshaded mode; partly self-lit parts stay shaded.
        if (selfLit >= 0.5f)
        {
            mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            mat.Metallic = 0;
        }
        return mat;
    }
}
