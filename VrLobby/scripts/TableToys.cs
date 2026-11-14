using Godot;
using VrTableManager.Services;
using Parsed = VrLobby.RealCabinetLoader.Parsed;
using SurfaceData = VrLobby.RealCabinetLoader.SurfaceData;

namespace VrLobby;

/// <summary>
/// A table's own 3D toys (<see cref="VpxCabinet.Toys"/>), ready to draw on the selected cabinet's playfield
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

    /// <summary>Tables kept in memory: the selected one and the last one or two (scrolling back is quick).</summary>
    public int Keep { get; set; } = 3;

    public string? CacheDir { get; set; } = ToyCache.DefaultDir;

    /// <summary>Table path, and its toys (null: none, over budget, or unreadable).</summary>
    public event Action<string, TableToys?>? ToysReady;

    readonly Dictionary<string, TableToys?> _done = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<string> _recent = [];
    readonly SemaphoreSlim _oneAtATime = new(1);

    public bool IsKnown(string vpx) => _done.ContainsKey(vpx);

    public TableToys? Get(string vpx) => _done.GetValueOrDefault(vpx);

    /// <summary>
    /// Marks a table as just used (it's on show), so reading others never forgets it: without this a table shown all along
    /// dropped out as the ones scrolled past finished, its toys vanished, and it was read again.
    /// </summary>
    public void Touch(string vpx)
    {
        if (_recent.First?.Value != vpx && _recent.Remove(vpx))
        {
            _recent.AddFirst(vpx);
        }
    }

    /// <summary>The table last asked for: a read still waiting for its turn is dropped once another is wanted.</summary>
    volatile string? _wanted;

    public void Request(string vpx)
    {
        _recent.Remove(vpx);
        _recent.AddFirst(vpx);
        _wanted = vpx;
        if (_done.ContainsKey(vpx) || !_pending.Add(vpx))
        {
            return;
        }

        var dir = CacheDir;
        var budget = Budget;
        _ = LobbyWork.Run(async () =>
        {
            await _oneAtATime.WaitAsync(LobbyWork.Closing);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                LobbyWork.Closing.ThrowIfCancellationRequested();
                // Scrolled on while it waited: don't spend a second or two reading a table you've left. It's asked
                // for again if you come back.
                if (_wanted != vpx)
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
                    }
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
        foreach (
            var name in model
                .Parts.Select(p => p.Image)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
        )
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

            ArtLoader.Shrink(img, TextureMaxSide);
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
            surfaces.Add(
                new SurfaceData(
                    first.Name,
                    verts.ToArray(),
                    norms.ToArray(),
                    uvs.ToArray(),
                    idx.ToArray(),
                    first.Image,
                    first.Material,
                    group.Key.Lit,
                    false
                )
            );
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
        _pending.Remove(vpx);
        if (!_recent.Contains(vpx))
        {
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
            var sw = System.Diagnostics.Stopwatch.StartNew();
            toys = Build(read.Toys, read.Vertices);
            GD.Print(
                $"Toys {name}: {read.Primitives} parts, {read.Vertices:N0} vertices, {toys.Mesh.GetSurfaceCount()} surfaces, "
                    + $"{toys.Textures} pictures; read from the {from} in {ms} ms, built in {sw.ElapsedMilliseconds} ms"
            );
        }
        _done[vpx] = toys;
        while (_recent.Count > Keep)
        {
            _done.Remove(_recent.Last!.Value);
            _recent.RemoveLast();
        }
        ToysReady?.Invoke(vpx, toys);
    }

    static TableToys Build(Parsed p, int vertices)
    {
        var textures = p.Images.ToDictionary(
            kv => kv.Key,
            kv => (Texture2D)ImageTexture.CreateFromImage(kv.Value),
            StringComparer.OrdinalIgnoreCase
        );
        var mesh = new ArrayMesh();
        foreach (var s in p.Surfaces)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Godot.Mesh.ArrayType.Max);
            arrays[(int)Godot.Mesh.ArrayType.Vertex] = s.Verts;
            arrays[(int)Godot.Mesh.ArrayType.Normal] = s.Norms;
            arrays[(int)Godot.Mesh.ArrayType.TexUV] = s.Uvs;
            arrays[(int)Godot.Mesh.ArrayType.Index] = s.Indices;
            mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
            var tex = s.Image != null && textures.TryGetValue(s.Image, out var t) ? t : null;
            var a =
                s.Image != null && p.Alpha.TryGetValue(s.Image, out var am)
                    ? am
                    : Image.AlphaMode.None;
            mesh.SurfaceSetMaterial(
                mesh.GetSurfaceCount() - 1,
                MaterialFor(s.Material, tex, a, s.Lit)
            );
        }
        return new TableToys
        {
            Mesh = mesh,
            PlayfieldSize = p.PlayfieldSize,
            Vertices = vertices,
            Textures = textures.Count,
        };
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
    const int Version = 6; // 6: toy names matched as words (not "Mushroom", "Hourglass"), the lowest-numbered pose kept; 5: lit "_on" copies dropped; 4: the back board the cabinet takes out of a baked mesh left out; 3: broken normals made finite; 2: animation frames, flipper-up poses and Alt options dropped
    static readonly byte[] Magic = "VTMTOY"u8.ToArray();

    public static string DefaultDir =>
        Path.Combine(Path.GetDirectoryName(CabinetCache.DefaultDir.TrimEnd('\\', '/'))!, "toys");

    /// <summary>What's kept for a table that couldn't be read: no toys, so it isn't read again until it changes.</summary>
    public static readonly TableToysLoader.Read None = new(null, 0, 0, false);

    static string FileFor(string vpx, string dir) =>
        Path.ChangeExtension(CabinetCache.FileFor(vpx, dir), ".toys");

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
