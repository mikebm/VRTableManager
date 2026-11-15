using Godot;
using VrTableManager.Services;
using Parsed = VrLobby.RealCabinetLoader.Parsed;
using SurfaceData = VrLobby.RealCabinetLoader.SurfaceData;

namespace VrLobby;

/// <summary>
/// A table's own 3D toys (<see cref="VpxCabinet.Toys"/>), ready to draw on a cabinet's playfield
/// (<see cref="Cabinet.ShowToys"/>): one mesh, a surface per look, in playfield space (x across from its centre, y up
/// from it, z along it towards the player), for a playfield of <see cref="PlayfieldSize"/>.
/// </summary>
public sealed class TableToys
{
    public required ArrayMesh Mesh { get; init; }
    public required Vector2 PlayfieldSize { get; init; }
    public int Vertices { get; init; }
    public int Textures { get; init; }
}

/// <summary>
/// Reads tables' toys in the background (Setup: Playfield depth, "3D toys"; parallax by default), keeps them on
/// disk beside the cabinets (<see cref="ToyCache"/>) and the last few in memory. A table whose toys come to more than
/// <see cref="Budget"/> vertices is left without, and the log says so.
/// </summary>
public partial class TableToysLoader : Node
{
    /// <summary>Most vertices a table's toys may have (all toys together). Fish Tales has ≈ 0.6 M, Die Hard Trilogy ≈ 0.9 M.</summary>
    public const int DefaultBudget = 1_000_000;

    /// <summary>Toys' pictures: VPW's baked toys share a few big sheets (4-8k), which 1024 (the cabinets') blurs.</summary>
    public const int TextureMaxSide = 2048;

    public int Budget { get; set; } = DefaultBudget;

    /// <summary>
    /// Tables kept in memory when only the selected one is on show: it and the last one or two (scrolling back is
    /// quick). With more on show (<see cref="Want"/>), every one of them and two more.
    /// </summary>
    public int Keep { get; set; } = 3;

    /// <summary>Tables kept in memory now (<see cref="Keep"/>, or more while more are on show).</summary>
    int _keep = 3;

    public string? CacheDir { get; set; } = ToyCache.DefaultDir;

    /// <summary>Most the saved toys may take on disk (Setup, AppSettings.LobbyToyCacheGb); 0: no limit.</summary>
    public long CacheLimitBytes { get; set; }

    /// <summary>Table path, and its toys (null: none, over budget, or unreadable).</summary>
    public event Action<string, TableToys?>? ToysReady;

    readonly Dictionary<string, TableToys?> _done = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<string> _recent = [];
    readonly SemaphoreSlim _oneAtATime = new(1);

    public bool IsKnown(string vpx) => _done.ContainsKey(vpx);

    public TableToys? Get(string vpx) => _done.GetValueOrDefault(vpx);

    /// <summary>
    /// The tables on show, the one you're on first (it's read and built first). Each one not known yet is read when
    /// <paramref name="read"/> (the ring has nearly stopped); a read still waiting for its turn is dropped once its
    /// table is no longer wanted (scrolled on, or turned away from). Tables on show are kept in memory whatever else is
    /// read: without that a table shown all along dropped out as the ones scrolled past finished, its toys vanished,
    /// and it was read again.
    /// </summary>
    public void Want(IReadOnlyList<string> tables, bool read)
    {
        _keep = Math.Max(Keep, tables.Count + 2);
        _first = read && tables.Count > 0 ? tables[0] : null;
        if (!read)
        {
            if (_wanted.Count > 0)
            {
                _wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        else if (!_wanted.SetEquals(tables))
        {
            _wanted = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
        }

        // Most recent first: the one you're on ends up at the front.
        for (var i = tables.Count - 1; i >= 0; i--)
        {
            var vpx = tables[i];
            if (read || _done.ContainsKey(vpx))
            {
                _recent.Remove(vpx);
                _recent.AddFirst(vpx);
            }
        }

        if (read)
        {
            foreach (var vpx in tables) // in order: the one you're on queues first
            {
                Request(vpx);
            }
        }
    }

    /// <summary>Tables wanted now (read on any thread: replaced, never changed), and the one you're on.</summary>
    volatile HashSet<string> _wanted = new(StringComparer.OrdinalIgnoreCase);
    volatile string? _first;

    void Request(string vpx)
    {
        if (_done.ContainsKey(vpx) || !_pending.Add(vpx))
        {
            return;
        }

        var dir = CacheDir;
        var budget = Budget;
        var limit = CacheLimitBytes;
        _ = LobbyWork.Run(async () =>
        {
            await _oneAtATime.WaitAsync(LobbyWork.Closing);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                LobbyWork.Closing.ThrowIfCancellationRequested();
                // Scrolled on (or turned away) while it waited: don't spend a second or two reading a table you've
                // left. It's asked for again if you come back.
                if (!_wanted.Contains(vpx))
                {
                    Callable.From(() => _pending.Remove(vpx)).CallDeferred();
                    return;
                }

                var from = "cache";
                if (dir == null || !ToyCache.TryLoad(vpx, dir, budget, out var read))
                {
                    from = "table";
                    read = Parse(vpx, budget);
                    if (dir != null)
                    {
                        // A table that can't be read is remembered too (as having none), so it isn't read every time.
                        ToyCache.Save(vpx, dir, read ?? ToyCache.None);
                        // Within the size chosen on Setup: the tables used longest ago go, never this one.
                        var pruned = VrTableManager.Lobby.ToyFiles.Prune(
                            limit,
                            VrTableManager.Lobby.ToyFiles.FileFor(vpx, dir),
                            dir
                        );
                        if (pruned > 0)
                        {
                            GD.Print(
                                $"Toys: {pruned} table(s) used longest ago dropped from the cache, to keep it within its size"
                            );
                        }
                    }
                }
                else
                {
                    VrTableManager.Lobby.ToyFiles.MarkUsed(
                        VrTableManager.Lobby.ToyFiles.FileFor(vpx, dir)
                    );
                }
                var ms = sw.ElapsedMilliseconds;
                Callable.From(() => Finish(vpx, read, from, ms)).CallDeferred();
            }
            catch (Exception e)
            {
                if (e is not OperationCanceledException)
                {
                    GD.PushWarning($"Couldn't read the toys in {vpx}: {e}");
                }
                Callable
                    .From(() => Finish(vpx, null, "error", sw.ElapsedMilliseconds))
                    .CallDeferred();
            }
            finally
            {
                _oneAtATime.Release();
            }
        });
    }

    /// <summary>What <see cref="Parse"/> makes: the toys (null when over budget) and how many vertices they came to.</summary>
    internal sealed record Read(Parsed? Toys, int Vertices, int Primitives, bool OverBudget);

    /// <summary>Most vertices in one surface: about one frame's upload (Fish Tales's 464k-vertex "BM_Parts" was 25 ms).</summary>
    const int ChunkVertices = 60_000;

    /// <summary>Pictures past this many are drawn at <see cref="TextureMaxSideMany"/>.</summary>
    const int ManyPictures = 8;
    public const int TextureMaxSideMany = 1024;

    /// <summary>
    /// A merged mesh in pieces of whole triangles, at most <see cref="ChunkVertices"/> vertices each, each with just the
    /// vertices it uses. One piece when it's small enough already.
    /// </summary>
    static IEnumerable<(Vector3[], Vector3[], Vector2[], int[])> Chunks(
        List<Vector3> verts,
        List<Vector3> norms,
        List<Vector2> uvs,
        List<int> idx
    )
    {
        if (verts.Count <= ChunkVertices)
        {
            yield return (verts.ToArray(), norms.ToArray(), uvs.ToArray(), idx.ToArray());
            yield break;
        }

        var map = new Dictionary<int, int>();
        List<Vector3> v = [],
            n = [];
        List<Vector2> u = [];
        List<int> ix = [];
        for (var t = 0; t + 2 < idx.Count; t += 3)
        {
            if (map.Count + 3 > ChunkVertices)
            {
                yield return (v.ToArray(), n.ToArray(), u.ToArray(), ix.ToArray());
                map.Clear();
                v = [];
                n = [];
                u = [];
                ix = [];
            }
            for (var k = 0; k < 3; k++)
            {
                var old = idx[t + k];
                if (!map.TryGetValue(old, out var nu))
                {
                    nu = map[old] = v.Count;
                    v.Add(verts[old]);
                    n.Add(norms[old]);
                    u.Add(uvs[old]);
                }
                ix.Add(nu);
            }
        }
        if (ix.Count > 0)
        {
            yield return (v.ToArray(), n.ToArray(), u.ToArray(), ix.ToArray());
        }
    }

    /// <summary>Background part: read the toys, decode their pictures and merge them into a surface per look.</summary>
    internal static Read? Parse(string vpx, int budget)
    {
        var model = VpxCabinet.Toys(vpx, budget);
        if (model == null)
        {
            return null;
        }

        if (model.OverBudget || model.Parts.Count == 0)
        {
            return new Read(null, model.Vertices, model.Primitives, model.OverBudget);
        }

        var images = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        var alpha = new Dictionary<string, Image.AlphaMode>(StringComparer.OrdinalIgnoreCase);
        var catalogue = VpxImages.ListImages(vpx);
        var names = model
            .Parts.Select(p => p.Image)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // A few big shared sheets (VPW's) keep their detail at 2048; a picture per toy (Futurama's 102) is 1024, or
        // they took 100+ MB of video memory and as much again on disk.
        var maxSide = names.Count > ManyPictures ? TextureMaxSideMany : TextureMaxSide;
        foreach (var name in names)
        {
            var info = catalogue.FirstOrDefault(i =>
                string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)
            );
            if (info == null)
            {
                continue;
            }

            LobbyWork.Closing.ThrowIfCancellationRequested();
            if (VpxImages.ExtractImage(vpx, info.Index) is not { } bytes)
            {
                continue;
            }

            if (ArtLoader.Decode(bytes) is not { } img)
            {
                continue;
            }

            if (img.IsCompressed())
            {
                img.Decompress();
            }

            ArtLoader.Shrink(img, maxSide);
            img.GenerateMipmaps();
            var a = RealCabinetLoader.AlphaOf(img);
            if (DxtEncoder.GodotCan)
            {
                _ = img.Compress(Image.CompressMode.S3Tc, Image.CompressSource.Generic);
            }
            else
            {
                img = DxtEncoder.Compress(img, a != Image.AlphaMode.None);
            }
            images[name] = img;
            alpha[name] = a;
        }

        var surfaces = new List<SurfaceData>();
        foreach (
            var group in model.Parts.GroupBy(p =>
                (
                    Image: p.Image?.ToLowerInvariant(),
                    Mat: p.Material?.Name,
                    Lit: MathF.Round(p.SelfLit, 1)
                )
            )
        )
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
            var first = group.First();
            // In pieces of at most ChunkVertices: each piece is one frame's upload on the main thread (BuildStep).
            foreach (var (cv, cn, cu, ci) in Chunks(verts, norms, uvs, idx))
            {
                surfaces.Add(
                    new SurfaceData(
                        first.Name,
                        cv,
                        cn,
                        cu,
                        ci,
                        first.Image,
                        first.Material,
                        group.Key.Lit,
                        false
                    )
                );
            }
        }

        var size = new Vector2(model.PlayfieldSize.X, model.PlayfieldSize.Y);
        return new Read(
            new Parsed
            {
                Surfaces = surfaces,
                Images = images,
                Alpha = alpha,
                PlayfieldCentre = Vector3.Zero,
                PlayfieldSize = size,
                Bounds = new Aabb(),
            },
            model.Vertices,
            model.Primitives,
            false
        );
    }

    void Finish(string vpx, Read? read, string from, long ms)
    {
        if (!_recent.Contains(vpx))
        {
            _pending.Remove(vpx);
            return; // forgotten while it was read: not built either (that's main-thread work)
        }

        var name = Path.GetFileNameWithoutExtension(vpx);
        TableToys? toys = null;
        if (read == null)
        {
            GD.Print($"Toys {name}: couldn't read the table ({ms} ms)");
        }
        else if (read.OverBudget)
        {
            GD.Print(
                $"Toys {name}: none shown, {read.Vertices:N0} vertices in {read.Primitives} parts is over the budget of {Budget:N0} ({ms} ms)"
            );
        }
        else if (read.Toys == null)
        {
            GD.Print($"Toys {name}: none found ({ms} ms)");
        }
        else
        {
            // Built a piece a frame (BuildStep), shown once whole: built at once, a table's pictures and meshes held
            // the frame for 60-95 ms just as you arrived at it.
            _building.Add(new Building(vpx, name, read, from, ms));
            return;
        }
        Done(vpx, toys);
    }

    void Done(string vpx, TableToys? toys)
    {
        _pending.Remove(vpx); // pending until here, built or not, so it isn't asked for again meanwhile
        if (!_recent.Contains(vpx))
        {
            return; // forgotten meanwhile
        }

        _done[vpx] = toys;
        while (_recent.Count > _keep)
        {
            _done.Remove(_recent.Last!.Value);
            _recent.RemoveLast();
        }
        ToysReady?.Invoke(vpx, toys);
    }

    /// <summary>A table's toys being put together on the main thread, a piece at a time.</summary>
    sealed class Building(string vpx, string name, Read read, string from, long readMs)
    {
        public readonly string Vpx = vpx,
            Name = name,
            From = from;
        public readonly Read Read = read;
        public readonly long ReadMs = readMs;
        public readonly Dictionary<string, Texture2D> Textures = new(
            StringComparer.OrdinalIgnoreCase
        );
        public readonly ArrayMesh Mesh = new();
        public int Next;
        public double BuildMs;
        public int Frames;
    }

    readonly List<Building> _building = [];

    /// <summary>Main-thread time a frame may spend building toys (a 90 Hz frame is 11 ms).</summary>
    const double BuildSliceMs = 4;

    public override void _Process(double delta)
    {
        if (_building.Count > 0)
        {
            // The table you're on first: one you moved on from may still be queued ahead of it.
            BuildStep(_building.Find(b => b.Vpx == _first) ?? _building[0]);
        }
    }

    /// <summary>
    /// Adds pieces to the toys being built, a picture or a surface at a time, until this frame's slice is used (at least
    /// one piece a frame). Done: they're handed over; forgotten meanwhile: dropped.
    /// </summary>
    void BuildStep(Building b)
    {
        if (!_recent.Contains(b.Vpx))
        {
            _building.Remove(b);
            _pending.Remove(b.Vpx);
            return;
        }

        var p = b.Read.Toys!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        b.Frames++;
        while (b.Next < p.Surfaces.Count)
        {
            var s = p.Surfaces[b.Next];
            Texture2D? tex = null;
            if (s.Image != null && !b.Textures.TryGetValue(s.Image, out tex))
            {
                if (p.Images.TryGetValue(s.Image, out var img))
                {
                    tex = b.Textures[s.Image] = ImageTexture.CreateFromImage(img);
                    if (sw.Elapsed.TotalMilliseconds >= BuildSliceMs)
                    {
                        break; // the picture is up; its surface next frame
                    }
                }
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Godot.Mesh.ArrayType.Max);
            arrays[(int)Godot.Mesh.ArrayType.Vertex] = s.Verts;
            arrays[(int)Godot.Mesh.ArrayType.Normal] = s.Norms;
            arrays[(int)Godot.Mesh.ArrayType.TexUV] = s.Uvs;
            arrays[(int)Godot.Mesh.ArrayType.Index] = s.Indices;
            b.Mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
            var a =
                s.Image != null && p.Alpha.TryGetValue(s.Image, out var am)
                    ? am
                    : Image.AlphaMode.None;
            b.Mesh.SurfaceSetMaterial(
                b.Mesh.GetSurfaceCount() - 1,
                MaterialFor(s.Material, tex, a, s.Lit)
            );
            b.Next++;
            if (sw.Elapsed.TotalMilliseconds >= BuildSliceMs)
            {
                break;
            }
        }
        b.BuildMs += sw.Elapsed.TotalMilliseconds;
        if (b.Next < p.Surfaces.Count)
        {
            return;
        }

        _building.Remove(b);
        GD.Print(
            $"Toys {b.Name}: {b.Read.Primitives} parts, {b.Read.Vertices:N0} vertices, {b.Mesh.GetSurfaceCount()} surfaces, "
                + $"{b.Textures.Count} pictures; read from the {b.From} in {b.ReadMs} ms, built in {b.BuildMs:0} ms over {b.Frames} frames"
        );
        Done(
            b.Vpx,
            new TableToys
            {
                Mesh = b.Mesh,
                PlayfieldSize = p.PlayfieldSize,
                Vertices = b.Read.Vertices,
                Textures = b.Textures.Count,
            }
        );
    }

    /// <summary>
    /// A toy's look. VPX's "disable lighting" toys (VPW's baked "BM_" meshes: their light is in the picture) are drawn
    /// unshaded, as the playfield video under them is, so they match it; the rest are lit by the room like the cabinets.
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
            AlbedoTexture = tex,
            Roughness = m != null ? Mathf.Clamp(1f - m.Roughness, 0.06f, 1f) : 0.6f,
            Metallic = m is { IsMetal: true } ? 1f : 0f,
        };
        // VPX uses a picture's alpha only on a material with opacity on: VPW's solid bakes ("VLM.Bake.Solid") share
        // sheets whose alpha is junk where they're solid, and cut out they left holes in Fish Tales's apron cards.
        if (m is { OpacityActive: false })
        {
            alpha = Image.AlphaMode.None;
        }

        if (alpha == Image.AlphaMode.Bit)
        {
            mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            mat.AlphaScissorThreshold = 0.5f;
        }
        else if (alpha == Image.AlphaMode.Blend)
        {
            mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass;
        }

        // Clear plastic: VPX also shows its edges more opaque (the material's edge opacity), which a flat alpha can't, so
        // a nearly clear ramp (VPW's ramps are 10%) is drawn at a third, or it all but vanished.
        if (m is { OpacityActive: true, Opacity: < 0.99f })
        {
            mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            mat.AlbedoColor = new Color(1, 1, 1, Mathf.Clamp(m.Opacity, 0.35f, 1f));
        }
        if (selfLit >= 0.5f)
        {
            mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            mat.Metallic = 0;
        }
        return mat;
    }
}

/// <summary>
/// Tables' toys as <see cref="TableToysLoader"/> read them, one file per table under vr-lobby\toys (beside the
/// cabinets), stamped with the table's size and date and the budget's verdict: an updated table is read again, and
/// so is one over the budget once the budget is bigger. Reading and writing never throw.
/// </summary>
static class ToyCache
{
    /// <summary>Bump when what TableToysLoader.Parse makes changes.</summary>
    const int Version = 7; // 7: big meshes in 60k-vertex pieces, pictures at 1024 past 8 of them; 6: toy names matched as words (not "Mushroom", "Hourglass"), the lowest-numbered pose kept; 5: lit "_on" copies dropped; 4: the back board the cabinet takes out of a baked mesh left out; 3: broken normals made finite; 2: animation frames, flipper-up poses and Alt options dropped
    static readonly byte[] Magic = "VTMTOY"u8.ToArray();

    public static string DefaultDir => VrTableManager.Lobby.ToyFiles.Dir;

    /// <summary>What's kept for a table that couldn't be read: no toys, so it isn't read again until it changes.</summary>
    public static readonly TableToysLoader.Read None = new(null, 0, 0, false);

    static string FileFor(string vpx, string dir) =>
        VrTableManager.Lobby.ToyFiles.FileFor(vpx, dir);

    /// <summary>
    /// Deletes saved toys of an older <see cref="Version"/> (or unreadable): nothing can use them, and they'd count against
    /// the space chosen on Setup (PREPARE TABLES, before it works out the room left). Reads only each file's header.
    /// </summary>
    public static int DropStale(string dir)
    {
        var dropped = 0;
        try
        {
            foreach (var file in Directory.Exists(dir) ? Directory.GetFiles(dir, "*.toys") : [])
            {
                bool current;
                try
                {
                    using var r = new BinaryReader(File.OpenRead(file));
                    current =
                        r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)
                        && r.ReadInt32() == Version;
                }
                catch (EndOfStreamException)
                {
                    current = false;
                }
                catch (IOException)
                {
                    continue; // in use: leave it
                }

                if (!current)
                {
                    try
                    {
                        File.Delete(file);
                        dropped++;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return dropped;
    }

    /// <summary>True if the cache holds this version of the table's toys, judged against this budget (reads only the header).</summary>
    public static bool IsCurrent(string vpx, string dir, int budget)
    {
        try
        {
            var fi = new FileInfo(vpx);
            var file = FileFor(vpx, dir);
            if (!fi.Exists || !File.Exists(file))
            {
                return false;
            }

            using var r = new BinaryReader(File.OpenRead(file));
            if (
                !r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)
                || r.ReadInt32() != Version
                || r.ReadInt64() != fi.Length
                || r.ReadInt64() != fi.LastWriteTimeUtc.Ticks
            )
            {
                return false;
            }

            var vertices = r.ReadInt32();
            r.ReadInt32();
            return r.ReadBoolean() ? vertices > budget : vertices <= budget;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryLoad(string vpx, string dir, int budget, out TableToysLoader.Read? read)
    {
        read = null;
        try
        {
            var fi = new FileInfo(vpx);
            var file = FileFor(vpx, dir);
            if (!fi.Exists || !File.Exists(file))
            {
                return false;
            }

            using var r = new BinaryReader(new BufferedStream(File.OpenRead(file), 1 << 16));
            if (
                !r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)
                || r.ReadInt32() != Version
                || r.ReadInt64() != fi.Length
                || r.ReadInt64() != fi.LastWriteTimeUtc.Ticks
            )
            {
                return false;
            }

            int vertices = r.ReadInt32(),
                primitives = r.ReadInt32();
            var overBudget = r.ReadBoolean();
            if (overBudget ? vertices <= budget : vertices > budget)
            {
                return false; // the budget changed across it: read again
            }

            read = new TableToysLoader.Read(
                r.ReadBoolean() ? CabinetCache.ReadBody(r) : null,
                vertices,
                primitives,
                overBudget
            );
            return true;
        }
        catch
        {
            read = null;
            return false;
        }
    }

    public static void Save(string vpx, string dir, TableToysLoader.Read read)
    {
        var file = FileFor(vpx, dir);
        var tmp = file + "." + System.Environment.ProcessId + ".tmp";
        try
        {
            var fi = new FileInfo(vpx);
            if (!fi.Exists)
            {
                return;
            }

            Directory.CreateDirectory(dir);
            using (var w = new BinaryWriter(new BufferedStream(File.Create(tmp), 1 << 16)))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(fi.Length);
                w.Write(fi.LastWriteTimeUtc.Ticks);
                w.Write(read.Vertices);
                w.Write(read.Primitives);
                w.Write(read.OverBudget);
                w.Write(read.Toys != null);
                if (read.Toys != null)
                {
                    CabinetCache.WriteBody(w, read.Toys);
                }
            }
            File.Move(tmp, file, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tmp);
            }
            catch { } // full disk or read-only folder: just no cache this time
        }
    }
}
