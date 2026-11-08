using System.Text;
using OpenMcdf;

namespace VrTableManager.Services;

/// <summary>
/// Fixes VR tables whose cabinet DMD showed the PuP pack's FullDMD under VPX 10.8.0 and shows nothing under
/// 10.8.1 (vpinball/vpinball#4226). In 10.8.0 such a flasher was just marked "DMD" (IDMD) and VPX captured the
/// PuP window onto it; 10.8.1 dropped the capture and doesn't convert old tables. The fix is the editor's own
/// one: render mode External Renderer (RDMD = 4) showing the Score View window (RSTL = 2), which is where
/// the PinUP Player plugin draws the FullDMD (screen 5).
/// <para>
/// Only flashers with the old DMD flag that show the PuP pack are touched (see <see cref="Find"/>), so a
/// table's PinMAME/FlexDMD display is left alone. The original file is kept next to the table as <c>&lt;table&gt;.vpx.dmdfix.bak</c>
/// (not .vpx, so it isn't mistaken for a second table). From file version 10.0 on, VPX's integrity hash
/// doesn't cover GameItem streams, so the patched table loads without a warning.
/// </para>
/// </summary>
public static class VrDmdFix
{
    const int FlasherType = 20;
    const int ExtRender = 4; // FlasherData::EXT_RENDER
    const int ScoreView = 2; // VPXWINDOW_ScoreView
    public const string BackupSuffix = ".dmdfix.bak";

    public record Flasher(int Item, string Name);

    public static string BackupPath(string vpx) => vpx + BackupSuffix;

    public static bool HasBackup(string vpx) => File.Exists(BackupPath(vpx));

    /// <summary>
    /// The flashers that need the fix (empty when none, or the file can't be read). A flasher named like
    /// "...PUP..." always does. One with another name ("VR_DMD", as in Total Nuclear Annihilation) does only when
    /// the script shows PuP is the only thing that could have drawn on it: it uses PinUP Player, no PinMAME, and
    /// no FlexDMD/UltraDMD that's switched on. Otherwise it's the table's own DMD, which 10.8.1 still shows.
    /// </summary>
    public static IReadOnlyList<Flasher> Find(string vpx)
    {
        List<Flasher> named = [],
            other = [];
        try
        {
            using var root = RootStorage.OpenRead(vpx);
            var game = root.OpenStorage("GameStg");
            for (int i = 0; game.TryOpenStream("GameItem" + i, out var s) && s != null; i++)
                using (s)
                {
                    var d = ReadAll(s);
                    if (IsOldDmd(d, out var name))
                        (
                            name.Contains("pup", StringComparison.OrdinalIgnoreCase) ? named : other
                        ).Add(new(i, name));
                }
        }
        catch (Exception)
        {
            return [];
        }
        if (other.Count == 0)
            return named;
        // Reading the script means reading the whole file, so only when there's a flasher it would decide.
        var script = RomDetector.Inspect(vpx);
        return script is { UsesPup: true, UsesPinMame: false, UsesOwnDmd: false }
            ? [.. named, .. other.OrderBy(f => f.Item)]
            : named;
    }

    /// <summary>
    /// Backs the table up and patches the flashers. A file that still needs the fix is unpatched, so it replaces
    /// any older backup (a table update brings a new original). Works on a copy that replaces the table only
    /// when every write went through.
    /// </summary>
    public static IReadOnlyList<Flasher> Apply(string vpx)
    {
        var flashers = Find(vpx);
        if (flashers.Count == 0)
            return flashers;
        File.Copy(vpx, BackupPath(vpx), overwrite: true);
        var tmp = vpx + ".dmdfix.tmp";
        File.Copy(vpx, tmp, overwrite: true);
        try
        {
            using (var root = RootStorage.Open(tmp, FileMode.Open, FileAccess.ReadWrite))
            {
                var game = root.OpenStorage("GameStg");
                foreach (var f in flashers)
                {
                    using var s = game.OpenStream("GameItem" + f.Item);
                    var patched = Patch(ReadAll(s));
                    s.Position = 0;
                    s.SetLength(patched.Length);
                    s.Write(patched, 0, patched.Length);
                }
                root.Flush();
            }
            if (Find(tmp).Count != 0)
                throw new InvalidOperationException(
                    "the patched table still has the old DMD setting"
                );
            File.Move(tmp, vpx, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
        return flashers;
    }

    /// <summary>Puts the backed-up original back.</summary>
    public static void Undo(string vpx) => File.Move(BackupPath(vpx), vpx, overwrite: true);

    static byte[] ReadAll(Stream s)
    {
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    record struct Record(int Offset, int Size, string Tag);

    /// <summary>
    /// The item's own records, up to the first drag point: a flasher's DPNT records are followed by nested
    /// point records (and their own ENDB), which aren't the flasher's settings.
    /// </summary>
    static List<Record> TopRecords(byte[] d)
    {
        var list = new List<Record>();
        for (int p = 4; p + 8 <= d.Length; )
        {
            int len = BitConverter.ToInt32(d, p);
            if (len < 4 || p + 4 + len > d.Length)
                break;
            var tag = Encoding.ASCII.GetString(d, p + 4, 4);
            if (tag is "DPNT" or "ENDB")
                break;
            list.Add(new(p, 4 + len, tag));
            p += 4 + len;
        }
        return list;
    }

    /// <summary>A flasher marked "DMD" the 10.8.0 way (IDMD), with no 10.8.1 render mode yet.</summary>
    static bool IsOldDmd(byte[] d, out string name)
    {
        name = "";
        if (d.Length < 4 || BitConverter.ToInt32(d, 0) != FlasherType)
            return false;
        bool dmd = false,
            modern = false;
        foreach (var r in TopRecords(d))
        {
            var data = r.Offset + 8;
            switch (r.Tag)
            {
                case "NAME" when r.Size >= 12:
                    var n = BitConverter.ToInt32(d, data);
                    name = Encoding.Unicode.GetString(
                        d,
                        data + 4,
                        Math.Clamp(n * 2, 0, r.Size - 12)
                    );
                    break;
                case "IDMD" when r.Size == 12:
                    dmd = BitConverter.ToInt32(d, data) != 0;
                    break;
                case "RDMD":
                    modern = true;
                    break;
            }
        }
        return dmd && !modern;
    }

    /// <summary>IDMD → RDMD = External Renderer, RSTL = Score View; everything else byte for byte.</summary>
    static byte[] Patch(byte[] d)
    {
        var records = TopRecords(d);
        var idmd = records.First(r => r.Tag == "IDMD");
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int p = 0;
        foreach (var r in records.Where(r => r.Tag is "IDMD" or "RSTL"))
        {
            w.Write(d, p, r.Offset - p);
            if (r == idmd)
            {
                WriteInt(w, "RDMD", ExtRender);
                WriteInt(w, "RSTL", ScoreView);
            }
            p = r.Offset + r.Size;
        }
        w.Write(d, p, d.Length - p);
        return ms.ToArray();
    }

    static void WriteInt(BinaryWriter w, string tag, int value)
    {
        w.Write(8);
        w.Write(Encoding.ASCII.GetBytes(tag));
        w.Write(value);
    }

    /// <summary>VPX builds before 10.8.1 don't know RDMD: the patch would turn the DMD into a plain flasher there.</summary>
    public static bool EngineSupportsFix(string? installedTag) =>
        installedTag != null
        && System.Version.TryParse(installedTag.TrimStart('v').Split('-')[0], out var v)
        && v >= new System.Version(10, 8, 1);
}
