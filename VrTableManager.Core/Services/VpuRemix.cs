using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using OpenMcdf;
using VrTableManager.Models;

namespace VrTableManager.Services;

/// <summary>
/// VPU Remixes (VPUniverse's "VPURemix Patching System"): a mod published as a .dif patch, not a table. Applied to
/// the exact original table it was made from, it gives the modded .vpx. The patch is JojoDiff's format (VPPatch
/// is a front end for JojoDiff); it carries no checksum of the original, so a patch applied to another version
/// of the table gives a broken file without saying so. <see cref="Apply"/> checks the result is a readable VPX
/// table instead.
/// </summary>
/// <remarks>
/// The patch reader follows the format as implemented in jojodiff-rs (MIT, https://github.com/francisdb/jojodiff-rs),
/// which vpxtool uses to apply VPU patches.
/// </remarks>
public static partial class VpuRemix
{
    /// <summary>The VPS feature tag on remix table files.</summary>
    public const string Feature = "VPU Patch";

    public const string PatchExt = ".dif";

    // Untagged remixes say so in their comment ("Use VPURemix System to patch", "VPU Remix Patching tool and
    // original table needed", "Requires VPU Patcher System and original table!").
    [GeneratedRegex(
        @"VPU\s*Remix|VPU\s*Patch|Patch(?:ing|er)\s+(?:System|Tool)",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex Mention();

    /// <summary>A VPU Remix: tagged "VPU Patch" on VPS, or its comment says it needs the patching tool.</summary>
    public static bool IsRemix(VpsTableFile tf) =>
        VpsCatalog.HasFeature(tf, Feature) || Mention().IsMatch(tf.Comment ?? "");

    /// <summary>The parent id when it names a table file ("root" and empty mean none).</summary>
    static string? ParentOf(VpsTableFile tf) =>
        string.IsNullOrWhiteSpace(tf.ParentId) || tf.ParentId == "root" ? null : tf.ParentId;

    /// <summary>
    /// The base table a remix patches: its VPS parent, looked up in its own game first (a few point to another
    /// game's table). Null when VPS doesn't say or the parent isn't in the catalog.
    /// </summary>
    public static (VpsGame Game, VpsTableFile File)? BaseOf(
        VpsTableFile remix,
        IEnumerable<VpsGame> games,
        VpsGame? remixGame = null
    )
    {
        if (ParentOf(remix) is not { } parent)
        {
            return null;
        }

        if (remixGame?.TableFiles?.FirstOrDefault(f => f.Id == parent) is { } own)
        {
            return (remixGame, own);
        }

        foreach (var g in games)
        {
            if (g.TableFiles?.FirstOrDefault(f => f.Id == parent) is { } f)
            {
                return (g, f);
            }
        }
        return null;
    }

    /// <summary>An installed table the patch may apply to, and the file to read (the original, not a VR DMD fix).</summary>
    public sealed record BaseCandidate(InstalledTable Table, string SourcePath, bool Exact);

    /// <summary>
    /// Installed VPX tables to try the remix's patch on, best first: the base VPS names (installed from that exact
    /// catalog entry), then the game's other tables (added by hand, or linked to another version). The remix itself
    /// (an earlier install of it) and other remixes are never a base. A table the VR DMD fix changed is read from
    /// the original it kept.
    /// </summary>
    public static List<BaseCandidate> BaseCandidates(
        VpsTableFile remix,
        string? remixGameId,
        IEnumerable<InstalledTable> installed,
        string? baseGameId = null
    )
    {
        var parent = ParentOf(remix);
        var result = new List<BaseCandidate>();
        foreach (var t in installed)
        {
            if (!t.IsVpx)
            {
                continue;
            }

            // A remix in another table's place is read as the original it kept (for its own updates, or another
            // remix of that original).
            var kept = KeptOriginal(t);
            var m = kept != null ? t.Manifest.RemixOriginal!.Manifest : t.Manifest;
            // An earlier install of the remix isn't its base (when a .dif was added as another catalog entry, that
            // entry may well be the base).
            if (kept == null && m.TableFileId == remix.Id && IsRemix(remix))
            {
                continue;
            }

            var exact = parent != null && m.TableFileId == parent;
            var sameGame =
                !string.IsNullOrEmpty(m.GameId)
                && (m.GameId == remixGameId || m.GameId == baseGameId);
            // The file is looked for last: this runs on the UI thread for every remix shown.
            if ((exact || (sameGame && !IsRemix(m))) && (kept != null || File.Exists(t.TablePath)))
            {
                result.Add(new BaseCandidate(t, PatchSource(kept ?? t.TablePath), exact));
            }
        }
        return result.OrderByDescending(c => c.Exact).ToList();
    }

    /// <summary>
    /// A table you chose as the base yourself (My Tables → VPU Remixes): read as <see cref="BaseCandidates"/> would,
    /// but whatever its game. Null when its file is gone.
    /// </summary>
    public static BaseCandidate? CandidateFor(InstalledTable t, VpsTableFile remix)
    {
        var kept = KeptOriginal(t);
        // A remix in place whose kept original is gone has nothing to patch: never the remix itself.
        if (kept == null && (t.Manifest.RemixOriginal != null || !File.Exists(t.TablePath)))
        {
            return null;
        }

        var m = kept != null ? t.Manifest.RemixOriginal!.Manifest : t.Manifest;
        var parent = ParentOf(remix);
        return new BaseCandidate(
            t,
            PatchSource(kept ?? t.TablePath),
            parent != null && m.TableFileId == parent
        );
    }

    /// <summary>An installed table (as its manifest says) that is a remix.</summary>
    public static bool IsRemix(TableManifest m) =>
        m.Features.Any(f => string.Equals(f, Feature, StringComparison.OrdinalIgnoreCase));

    /// <summary>The file a patch reads for this table file: the original a VR DMD fix kept, else the file itself.</summary>
    public static string PatchSource(string vpx) =>
        VrDmdFix.HasBackup(vpx) ? VrDmdFix.BackupPath(vpx) : vpx;

    /// <summary>What a table a remix replaced is kept as, in its folder: "Name.vpx.original" (not seen as a table).</summary>
    public const string KeptSuffix = ".original";

    public static string KeptPath(string vpx) => vpx + KeptSuffix;

    /// <summary>The kept original of a remix that replaced it, when its file is there; else null.</summary>
    public static string? KeptOriginal(InstalledTable t) =>
        t.Manifest.RemixOriginal is { File: { Length: > 0 } f }
        && Path.Combine(t.Folder, f) is var p
        && File.Exists(p)
            ? p
            : null;

    /// <summary>
    /// The remix came out before this version of its base did (by the release dates VPS gives), so it was made from
    /// an earlier version and its patch can't fit this one. VPS keeps one entry per table and moves its version on,
    /// so a remix's parent often names a newer version than the one the patch needs (its page says which, under
    /// "Original Table Version"). Only the release dates the uploaders gave count (not the last-edit date, which any
    /// new tag or link moves on): false when either is missing.
    /// </summary>
    public static bool Predates(VpsTableFile remix, VpsTableFile? baseFile) =>
        baseFile?.CreatedAt is > 0
        && remix.CreatedAt is > 0
        && baseFile.CreatedAt - remix.CreatedAt > TimeSpan.FromDays(1).TotalMilliseconds;

    /// <summary>
    /// <see cref="Predates"/> for the version you have: the catalog entry's date is only that version's when the
    /// entry still lists it (VPS moves an entry on to newer versions; an older copy you have isn't dated by it).
    /// </summary>
    public static bool PredatesYours(
        VpsTableFile remix,
        VpsTableFile? baseFile,
        string? yourVersion
    ) =>
        baseFile != null
        && !string.IsNullOrWhiteSpace(yourVersion)
        && TableUpdates.SameVersion(baseFile.Version, yourVersion)
        && Predates(remix, baseFile);

    /// <summary>A VPS release date for messages ("10 Feb 2025"), or null.</summary>
    public static string? ReleaseDate(VpsFile f) =>
        f.CreatedAt is > 0 and var ms
            ? DateTimeOffset
                .FromUnixTimeMilliseconds(ms)
                .ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
            : null;

    /// <summary>The parent id when it names a table file other than the remix itself; else null.</summary>
    public static string? ParentId(VpsTableFile tf) =>
        ParentOf(tf) is { } p && p != tf.Id ? p : null;

    /// <summary>
    /// Whether a file you picked is a remix's patch rather than a table: a .dif, or an archive with a .dif and no
    /// .vpx. Null when an archive can't be read (decide from the catalog entry instead).
    /// </summary>
    public static bool? IsPatchOnly(string file)
    {
        var ext = Path.GetExtension(file);
        if (ext.Equals(PatchExt, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ext.Equals(".vpx", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(file);
            var names = archive
                .Entries.Where(e => !e.IsDirectory && !string.IsNullOrEmpty(e.Key))
                .Select(e => e.Key!)
                .ToList();
            bool Has(string x) => names.Any(n => n.EndsWith(x, StringComparison.OrdinalIgnoreCase));
            return Has(PatchExt) && !Has(".vpx");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The features to save with an installed table: a remix always carries <see cref="Feature"/>, even one VPS only
    /// marks in its comment, so it's never taken for another remix's base.
    /// </summary>
    public static List<string> FeaturesOf(VpsTableFile tf)
    {
        var f = tf.Features?.ToList() ?? [];
        if (
            IsRemix(tf)
            && !f.Any(x => string.Equals(x, Feature, StringComparison.OrdinalIgnoreCase))
        )
        {
            f.Add(Feature);
        }
        return f;
    }

    /// <summary>The patch doesn't fit the table it was applied to (another version, or a changed copy).</summary>
    public sealed class MismatchException(string message) : IOException(message);

    /// <summary>
    /// Applies <paramref name="difPath"/> to <paramref name="basePath"/> and writes <paramref name="outPath"/>.
    /// Throws <see cref="MismatchException"/> (and leaves no output) when the patch doesn't fit the base or the
    /// result isn't a readable VPX table.
    /// </summary>
    public static void Apply(
        string basePath,
        string difPath,
        string outPath,
        CancellationToken ct = default
    )
    {
        try
        {
            using (
                var src = new FileStream(
                    basePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1 << 16
                )
            )
            using (
                var pat = new FileStream(
                    difPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1 << 16
                )
            )
            using (
                var dst = new FileStream(
                    outPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    1 << 16
                )
            )
            {
                Patch(src, pat, dst, ct);
            }

            if (!LooksLikeVpx(outPath, ct))
            {
                throw new MismatchException(
                    "the patched file isn't a readable VPX table, so the patch was made from another version of it"
                );
            }
        }
        catch
        {
            try
            {
                File.Delete(outPath);
            }
            catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// Tries each base in turn; returns the one the patch fitted, or null when it fitted none (the output is then
    /// absent). Files that can't be read are skipped like a mismatch; a broken .dif throws (it's broken on any base).
    /// </summary>
    public static string? ApplyToFirstFitting(
        IEnumerable<string> bases,
        string difPath,
        string outPath,
        CancellationToken ct = default
    )
    {
        foreach (var b in bases)
        {
            try
            {
                Apply(b, difPath, outPath, ct);
                return b;
            }
            // Only "this base doesn't fit" moves on: a broken .dif (InvalidDataException) is broken on any base, and a
            // failure writing the output (disk full) isn't the base's fault.
            catch (Exception e)
                when (e
                        is MismatchException
                            or FileNotFoundException
                            or DirectoryNotFoundException
                            or UnauthorizedAccessException
                ) { }
        }
        return null;
    }

    // ---- JojoDiff patch format -----------------------------------------------------------------

    const byte Esc = 0xA7; // escape: the next byte is an opcode (or a literal ESC)
    const byte Mod = 0xA6; // overwrite: bytes from the patch replace the original's
    const byte Ins = 0xA5; // insert: bytes from the patch, the original stays where it is
    const byte Del = 0xA4; // skip <len> bytes of the original
    const byte Eql = 0xA3; // copy <len> bytes of the original
    const byte Bkt = 0xA2; // seek back <len> bytes in the original
    const int None = -1;

    static bool IsOpcode(int b) => b is Mod or Ins or Del or Eql or Bkt;

    /// <summary>
    /// Applies a JojoDiff patch: <paramref name="source"/> (seekable) + <paramref name="patch"/> → <paramref name="output"/>.
    /// Throws <see cref="MismatchException"/> when the patch reads outside the original, and
    /// <see cref="InvalidDataException"/> when the patch itself is cut short. Returns how far into the original it
    /// read (see <see cref="OriginalSize"/>).
    /// </summary>
    public static long Patch(
        Stream source,
        Stream patch,
        Stream output,
        CancellationToken ct = default
    )
    {
        var pat = patch is BufferedStream ? patch : new BufferedStream(patch, 1 << 16);
        var dst = new BufferedStream(output, 1 << 16);
        var buf = new byte[1 << 16];
        long posOrg = 0,
            maxOrg = 0; // how far into the original it reads: at least the original's size
        var op = None;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int p1 = None,
                p2 = None;
            if (op == None)
            {
                var b = pat.ReadByte();
                if (b < 0)
                {
                    break;
                }

                if (b == Esc)
                {
                    var e = pat.ReadByte();
                    if (e < 0)
                    {
                        throw new InvalidDataException(
                            "The .dif ends in the middle of an instruction."
                        );
                    }

                    if (IsOpcode(e))
                    {
                        op = e;
                    }
                    else
                    {
                        // ESC xxx (or ESC ESC) at the start of a run: data, overwriting.
                        op = Mod;
                        p1 = Esc;
                        p2 = e;
                    }
                }
                else
                {
                    op = Mod; // a run without its opcode overwrites
                    p1 = b;
                }
            }

            switch (op)
            {
                case Mod:
                case Ins:
                    var (next, n) = CopyData(pat, dst, op, p1, p2, ct);
                    if (op == Mod)
                    {
                        posOrg += n;
                    }

                    if (next == None)
                    {
                        dst.Flush();
                        return Math.Max(maxOrg, posOrg);
                    }
                    op = next;
                    break;
                case Del:
                    var skip = ReadLength(pat);
                    if (skip > source.Length - posOrg)
                    {
                        throw new MismatchException(
                            "the patch skips past the end of the original table, so it was made from another version of it"
                        );
                    }
                    posOrg += skip;
                    op = None;
                    break;
                case Eql:
                    var len = ReadLength(pat);
                    if (len > source.Length - posOrg) // not posOrg + len: a huge length mustn't overflow
                    {
                        throw new MismatchException(
                            "the patch copies past the end of the original table, so it was made from another version of it"
                        );
                    }

                    source.Position = posOrg;
                    for (var left = len; left > 0; )
                    {
                        ct.ThrowIfCancellationRequested();
                        var got = source.Read(buf, 0, (int)Math.Min(buf.Length, left));
                        if (got <= 0)
                        {
                            throw new MismatchException("the original table ended early");
                        }
                        dst.Write(buf, 0, got);
                        left -= got;
                    }
                    posOrg += len;
                    op = None;
                    break;
                case Bkt:
                    var back = ReadLength(pat);
                    if (back > posOrg)
                    {
                        throw new MismatchException(
                            "the patch seeks before the start of the original table"
                        );
                    }
                    posOrg -= back;
                    op = None;
                    break;
            }
            maxOrg = Math.Max(maxOrg, posOrg);
        }
        dst.Flush();
        return maxOrg;
    }

    /// <summary>
    /// About how big the table a patch was made from is: how far into it the patch reads, which is its whole length
    /// but for any last bytes it leaves alone. The patch can't fit a smaller file, and rarely one of another size.
    /// Null when the .dif can't be read.
    /// </summary>
    public static long? OriginalSize(string difPath, CancellationToken ct = default)
    {
        try
        {
            using var pat = new FileStream(
                difPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1 << 16
            );
            return Patch(new Zeros(), pat, Stream.Null, ct);
        }
        catch (Exception e)
            when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="table"/> can be the original a patch made from a table of about
    /// <paramref name="originalSize"/> bytes: no smaller, and within a small margin (a patch may leave the
    /// original's last bytes alone).
    /// </summary>
    public static bool SizeFits(long table, long originalSize) =>
        table >= originalSize && table - originalSize <= Math.Max(4096, originalSize / 200);

    /// <summary>"89.9 MB" for messages.</summary>
    public static string SizeText(long bytes) =>
        (bytes / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
        + " MB";

    /// <summary>A stand-in original for measuring a patch: endless zeros.</summary>
    sealed class Zeros : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => long.MaxValue / 2;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            Position += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                _ => Length + offset,
            };

        public override void Flush() { }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Copies a MOD/INS run of data bytes to the output, first the pending byte(s) read with its opcode. Returns the
    /// opcode that ends it (None at the end of the patch) and how many bytes it wrote.
    /// </summary>
    static (int Next, long Written) CopyData(
        Stream pat,
        Stream dst,
        int op,
        int p1,
        int p2,
        CancellationToken ct
    )
    {
        long n = 0;
        if (p1 != None)
        {
            dst.WriteByte((byte)p1);
            n++;
            if (p1 == Esc && p2 != None)
            {
                dst.WriteByte((byte)p2);
                n++;
            }
        }

        int b;
        while ((b = pat.ReadByte()) >= 0)
        {
            if ((n & 0xFFFF) == 0)
            {
                ct.ThrowIfCancellationRequested(); // a big inserted image is one long run
            }

            if (b == Esc)
            {
                var nx = pat.ReadByte();
                if (nx < 0)
                {
                    throw new InvalidDataException(
                        "The .dif ends in the middle of an instruction."
                    );
                }

                if (nx == Esc)
                {
                    dst.WriteByte(Esc); // ESC ESC: one literal ESC
                    n++;
                    continue;
                }

                if (!IsOpcode(nx))
                {
                    dst.WriteByte(Esc); // ESC xxx: both are data
                    dst.WriteByte((byte)nx);
                    n += 2;
                    continue;
                }

                if (nx != op)
                {
                    return (nx, n);
                }

                // ESC MOD inside a MOD run (or INS in INS) means nothing: both are data.
                dst.WriteByte(Esc);
                n++;
                b = nx;
            }

            dst.WriteByte((byte)b);
            n++;
        }
        return (None, n);
    }

    /// <summary>
    /// A length: one byte 0–251 is that plus one; 252 is 253 plus the next byte; 253, 254 and 255 are followed by a
    /// big-endian 16, 32 or 64-bit value.
    /// </summary>
    static long ReadLength(Stream pat)
    {
        var b = pat.ReadByte();
        Span<byte> v = stackalloc byte[8];
        switch (b)
        {
            case < 0:
                throw new InvalidDataException("The .dif ends in the middle of an instruction.");
            case < 252:
                return b + 1;
            case 252:
                ReadExact(pat, v[..1]);
                return 253 + v[0];
            case 253:
                ReadExact(pat, v[..2]);
                return BinaryPrimitives.ReadUInt16BigEndian(v);
            case 254:
                ReadExact(pat, v[..4]);
                return BinaryPrimitives.ReadUInt32BigEndian(v);
            default:
                ReadExact(pat, v);
                var l = BinaryPrimitives.ReadInt64BigEndian(v);
                return l >= 0
                    ? l
                    : throw new InvalidDataException("The .dif has a length out of range.");
        }
    }

    static void ReadExact(Stream s, Span<byte> into)
    {
        try
        {
            s.ReadExactly(into);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("The .dif ends in the middle of an instruction.");
        }
    }

    // ---- Checking the result -----------------------------------------------------------------

    /// <summary>
    /// The file is a VPX table that reads through: a compound file whose GameStg has GameData, and whose table
    /// settings, parts and collections are whole records up to their end marker. A patch applied to the wrong
    /// original copies the wrong bytes into these, which breaks the record chain; every other stream is read
    /// through too, which checks the file's sector chains.
    /// </summary>
    public static bool LooksLikeVpx(string path, CancellationToken ct = default)
    {
        try
        {
            using var root = RootStorage.OpenRead(path);
            var game = root.OpenStorage("GameStg");
            if (!game.TryOpenStream("GameData", out var gd) || gd == null)
            {
                return false;
            }

            using (gd)
            {
                if (!RecordsEnd(ReadAll(gd), 0))
                {
                    return false;
                }
            }

            foreach (var e in game.EnumerateEntries().ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (e.Type != EntryType.Stream || e.Name == "GameData")
                {
                    continue;
                }

                using var s = game.OpenStream(e.Name);
                // A part starts with its type (int32), then its records; a collection is records from the start.
                var start =
                    e.Name.StartsWith("GameItem", StringComparison.Ordinal) ? 4
                    : e.Name.StartsWith("Collection", StringComparison.Ordinal) ? 0
                    : -1;
                if (start < 0)
                {
                    s.CopyTo(Stream.Null);
                }
                else if (!RecordsEnd(ReadAll(s), start))
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or OperationCanceledException))
        {
            return false;
        }
    }

    static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// [int32 length][4-char tag][length-4 bytes] records from <paramref name="start"/> reach an ENDB without
    /// running off the end or meeting a tag that isn't text. CODE's length covers only its tag; the script's own
    /// length follows it.
    /// </summary>
    static bool RecordsEnd(byte[] d, int start)
    {
        long p = start;
        while (p + 8 <= d.Length)
        {
            var len = BitConverter.ToInt32(d, (int)p);
            for (var i = 4; i < 8; i++)
            {
                // Text, or NUL padding a three-letter tag ("ZPD\0").
                if (d[p + i] is not 0 and (< 0x20 or > 0x7E))
                {
                    return false;
                }
            }

            var tag = Encoding.ASCII.GetString(d, (int)p + 4, 4);
            // FONT (decals, text boxes) is followed by a saved font rather than records, and DPNT by drag points
            // with records (and an ENDB) of their own: the records up to them read through, which is enough.
            if (tag is "ENDB" or "FONT" or "DPNT")
            {
                return true;
            }

            if (tag == "CODE")
            {
                if (p + 12 > d.Length)
                {
                    return false;
                }

                var code = BitConverter.ToInt32(d, (int)p + 8);
                if (code < 0)
                {
                    return false;
                }

                p += 12 + (long)code;
                continue;
            }

            if (len < 4 || p + 4 + len > d.Length)
            {
                return false;
            }
            p += 4 + len;
        }
        return false;
    }

    /// <summary>
    /// The first web link in a text file that came with the patch (its ReadMe should link the original table),
    /// to point you at when the patch doesn't fit.
    /// </summary>
    public static string? ReadmeLink(string folder)
    {
        try
        {
            foreach (
                var f in Directory
                    .EnumerateFiles(folder, "*.txt", SearchOption.AllDirectories)
                    .OrderByDescending(f =>
                        Path.GetFileName(f).Contains("readme", StringComparison.OrdinalIgnoreCase)
                    )
            )
            {
                if (Link().Match(File.ReadAllText(f)) is { Success: true } m)
                {
                    return m.Value.TrimEnd('.', ',', ')', ';');
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Link();

    /// <summary>"v1.43 by Mikcab, Mfuegemann +4": a base table for messages.</summary>
    public static string Describe(VpsTableFile f)
    {
        var a = f.Authors ?? [];
        var by =
            a.Count == 0
                ? ""
                : " by " + string.Join(", ", a.Take(2)) + (a.Count > 2 ? $" +{a.Count - 2}" : "");
        var v = string.IsNullOrWhiteSpace(f.Version)
            ? ""
            : "v" + f.Version.Trim().TrimStart('v', 'V');
        return (v + by).Trim();
    }

    /// <summary>
    /// "Cabinet MOD — v1.0.0 by LoadedWeapon, Skitso +3": a remix for a menu. Its name is the start of its VPS
    /// comment, unless that's only the usual "needs the patching tool and the original table".
    /// </summary>
    public static string Title(VpsTableFile f)
    {
        var first = (f.Comment ?? "")
            .Split(['.', '!', '\n', '(', '|'], 2)[0]
            .Split(" - ", 2)[0]
            .Trim();
        var boilerplate =
            first.Length == 0
            || Mention().IsMatch(first)
            || first.Contains("original table", StringComparison.OrdinalIgnoreCase)
            || first.StartsWith("Use ", StringComparison.OrdinalIgnoreCase)
            || first.StartsWith("Read ", StringComparison.OrdinalIgnoreCase);
        if (first.Length > 40)
        {
            first = first[..39].TrimEnd() + "…";
        }

        var d = Describe(f);
        return boilerplate ? (d.Length > 0 ? d : "VPU Remix")
            : d.Length > 0 ? $"{first} — {d}"
            : first;
    }
}
