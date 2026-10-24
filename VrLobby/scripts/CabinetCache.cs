using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Godot;
using VrTableManager.Services;
using Parsed = VrLobby.RealCabinetLoader.Parsed;
using SurfaceData = VrLobby.RealCabinetLoader.SurfaceData;

namespace VrLobby;

/// <summary>
/// Tables' VR cabinets as read by <see cref="RealCabinetLoader"/>, kept on disk so the lobby (which restarts
/// after every game) doesn't re-read and re-compress them: merged surfaces, materials, DXT-compressed
/// mipmapped pictures and the placement, one file per table under %APPDATA%\VrTableManager\cabinet-cache.
/// A file holds the table's size and date; an updated table (or a newer <see cref="Version"/>) is read again
/// and overwrites it. Tables without a cabinet are remembered too. Reading and writing never throw.
/// </summary>
static class CabinetCache
{
    /// <summary>Bump when what Parse makes changes (texture size, merging, materials), so old files are re-read.</summary>
    const int Version = 1;
    static readonly byte[] Magic = "VTMCAB"u8.ToArray();

    public static string DefaultDir => Path.Combine(AppSettings.DataDir, "cabinet-cache");

    public static string FileFor(string vpx, string dir) =>
        Path.Combine(dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(vpx).ToLowerInvariant()))) + ".cab");

    /// <summary>True if the cache knows this version of the table: its cabinet, or null when it has none.</summary>
    public static bool TryLoad(string vpx, string dir, out Parsed? parsed)
    {
        parsed = null;
        try
        {
            var fi = new FileInfo(vpx);
            var file = FileFor(vpx, dir);
            if (!fi.Exists || !File.Exists(file)) return false;
            using var r = new BinaryReader(new BufferedStream(File.OpenRead(file), 1 << 16));
            if (!r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || r.ReadInt32() != Version
                || r.ReadInt64() != fi.Length || r.ReadInt64() != fi.LastWriteTimeUtc.Ticks) return false;
            if (!r.ReadBoolean()) return true; // known: no VR cabinet in this table

            var centre = ReadVector3(r);
            var size = new Vector2(r.ReadSingle(), r.ReadSingle());
            var bounds = new Aabb(ReadVector3(r), ReadVector3(r));
            Aabb? backbox = r.ReadBoolean() ? new Aabb(ReadVector3(r), ReadVector3(r)) : null;

            var images = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
            var alpha = new Dictionary<string, Image.AlphaMode>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0, n = r.ReadInt32(); i < n; i++)
            {
                var name = r.ReadString();
                alpha[name] = (Image.AlphaMode)r.ReadInt32();
                int w = r.ReadInt32(), h = r.ReadInt32();
                var mips = r.ReadBoolean();
                var format = (Image.Format)r.ReadInt32();
                images[name] = Image.CreateFromData(w, h, mips, format, r.ReadBytes(r.ReadInt32()));
            }

            var surfaces = new List<SurfaceData>();
            for (int i = 0, n = r.ReadInt32(); i < n; i++)
            {
                var name = r.ReadString();
                var image = r.ReadBoolean() ? r.ReadString() : null;
                var material = r.ReadBoolean()
                    ? new VpxMaterial(r.ReadString(), r.ReadUInt32(), r.ReadBoolean(), r.ReadSingle(), r.ReadSingle(), r.ReadBoolean())
                    : null;
                var lit = r.ReadSingle();
                var bg = r.ReadBoolean();
                surfaces.Add(new SurfaceData(name, ReadArray<Vector3>(r), ReadArray<Vector3>(r), ReadArray<Vector2>(r), ReadArray<int>(r),
                    image, material, lit, bg));
            }

            parsed = new Parsed
            {
                Surfaces = surfaces, Images = images, Alpha = alpha,
                PlayfieldCentre = centre, PlayfieldSize = size, Bounds = bounds, Backbox = backbox,
            };
            return true;
        }
        catch
        {
            parsed = null;
            return false; // truncated or locked: read the table instead
        }
    }

    /// <summary>Keeps what was read from this version of the table (null: it has no VR cabinet).</summary>
    public static void Save(string vpx, string dir, Parsed? parsed)
    {
        var file = FileFor(vpx, dir);
        var tmp = file + "." + System.Environment.ProcessId + ".tmp";
        try
        {
            var fi = new FileInfo(vpx);
            if (!fi.Exists) return;
            Directory.CreateDirectory(dir);
            using (var w = new BinaryWriter(new BufferedStream(File.Create(tmp), 1 << 16)))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(fi.Length);
                w.Write(fi.LastWriteTimeUtc.Ticks);
                w.Write(parsed != null);
                if (parsed != null)
                {
                    WriteVector3(w, parsed.PlayfieldCentre);
                    w.Write(parsed.PlayfieldSize.X); w.Write(parsed.PlayfieldSize.Y);
                    WriteVector3(w, parsed.Bounds.Position); WriteVector3(w, parsed.Bounds.Size);
                    w.Write(parsed.Backbox.HasValue);
                    if (parsed.Backbox is { } box) { WriteVector3(w, box.Position); WriteVector3(w, box.Size); }

                    w.Write(parsed.Images.Count);
                    foreach (var (name, img) in parsed.Images)
                    {
                        w.Write(name);
                        w.Write((int)parsed.Alpha.GetValueOrDefault(name));
                        w.Write(img.GetWidth()); w.Write(img.GetHeight());
                        w.Write(img.HasMipmaps());
                        w.Write((int)img.GetFormat());
                        var data = img.GetData();
                        w.Write(data.Length);
                        w.Write(data);
                    }

                    w.Write(parsed.Surfaces.Count);
                    foreach (var s in parsed.Surfaces)
                    {
                        w.Write(s.Name);
                        w.Write(s.Image != null);
                        if (s.Image != null) w.Write(s.Image);
                        w.Write(s.Material != null);
                        if (s.Material is { } m)
                        {
                            w.Write(m.Name); w.Write(m.BaseColour); w.Write(m.IsMetal);
                            w.Write(m.Roughness); w.Write(m.Opacity); w.Write(m.OpacityActive);
                        }
                        w.Write(s.Lit);
                        w.Write(s.Backglass);
                        WriteArray<Vector3>(w, s.Verts); WriteArray<Vector3>(w, s.Norms); WriteArray<Vector2>(w, s.Uvs); WriteArray<int>(w, s.Indices);
                    }
                }
            }
            File.Move(tmp, file, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { } // full disk or read-only folder: just no cache this time
        }
    }

    static Vector3 ReadVector3(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    static void WriteVector3(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }

    /// <summary>Plain structs (vectors, ints) as one block of bytes.</summary>
    static T[] ReadArray<T>(BinaryReader r) where T : unmanaged
    {
        var items = new T[r.ReadInt32()];
        r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(items.AsSpan()));
        return items;
    }

    static void WriteArray<T>(BinaryWriter w, T[] items) where T : unmanaged
    {
        w.Write(items.Length);
        w.Write(MemoryMarshal.AsBytes(items.AsSpan()));
    }
}
