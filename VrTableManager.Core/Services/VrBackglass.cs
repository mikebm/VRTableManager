using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using OpenMcdf;

namespace VrTableManager.Services;

/// <summary>
/// Some VR tables made for VPX 10.8.0 (Darkest Dungeon) have a blank backglass panel in their cabinet and leave the
/// picture to their PuP pack. Under VPX 10.8.1 the PinUP Player plugin's backglass only appears on the backglass VPX
/// adds to the VR scene itself (AddBackglass), which VPX puts low over the playfield, sized from the table's width.
/// <para>
/// For such a table, a VR launch turns VPX's backglass on and moves it onto the cabinet's panel from the table's
/// script, through the script copy <see cref="VrRoomSwitch"/> plays beside the table: VPX creates it before the
/// script starts (as <c>vr_backglass</c>, or <c>vr_backglass001</c> when the name is taken), and the script can
/// reach it through <c>GetElements</c>. It has to stay in view: hidden, or moved inside the backbox, it stops
/// getting the picture, and a second display set to VPX's Backglass window shows nothing while it's there. The
/// table file isn't changed.
/// </para>
/// <para>
/// VPX's backglass keeps its own size (4:3, 1.2 × the table's width), so on a 16:9 panel its black edges stand a
/// little above and below the panel; the 16:9 picture inside fits the panel's width closely.
/// </para>
/// </summary>
public static partial class VrBackglass
{
    const int PrimitiveType = 19;
    const int FlasherType = 20;
    const int ExtRender = 4;
    const int BackglassWindow = 1; // VPXWINDOW_Backglass (VPX clamps External Renderer windows to Backglass..Topper)

    /// <summary>
    /// In front of the panel, toward the player: at 2 (about a millimetre) the headset's depth buffer let the panel
    /// hide it. Tables' own PuP DMD displays sit about 14 in front of their panel.
    /// </summary>
    const float InFront = 15f;

    /// <summary>
    /// Bytes per pixel below which the panel's picture is a flat colour: a solid black 1000×714 JPEG is 0.03,
    /// real backglass art several times more than 0.1 (Guardians of the Galaxy's is 2.6).
    /// </summary>
    const double BlankBytesPerPixel = 0.1;

    const string BlockStart = "' VR Table Manager: VPX's VR backglass onto the cabinet (begin)";
    const string BlockEnd = "' VR Table Manager: VPX's VR backglass onto the cabinet (end)";

    /// <summary>Where VPX's backglass goes: its centre in table space and its tilt (rotation about X, degrees).</summary>
    /// <param name="Panel">The cabinet's backglass panel it goes over.</param>
    public sealed record Placement(string Panel, float X, float Y, float Height, float RotX);

    /// <summary>
    /// Where to put VPX's backglass for this table, or null when the table doesn't need it: no PuP pack showing a
    /// backglass, a display of its own already showing VPX's Backglass window, or no one blank, upright backglass
    /// panel in its cabinet. Never throws.
    /// </summary>
    public static Placement? Find(string vpx)
    {
        try
        {
            if (!ShowsPupBackglass(Path.GetDirectoryName(vpx)!))
            {
                return null;
            }

            using var root = RootStorage.OpenRead(vpx);
            return Read(root);
        }
        catch (Exception)
        {
            return null; // only advice for the launch: a table it can't read just plays as it is
        }
    }

    /// <summary>
    /// The script with this app's block that moves VPX's backglass (none when <paramref name="placement"/> is
    /// null). A block from an earlier launch is taken out first, so a .vbs of the user's own doesn't collect them.
    /// The block does nothing on the desktop, where VPX adds no backglass.
    /// </summary>
    public static string WithScript(string script, Placement? placement)
    {
        var start = script.IndexOf(BlockStart, StringComparison.Ordinal);
        var end = start < 0 ? -1 : script.IndexOf(BlockEnd, start, StringComparison.Ordinal);
        if (start >= 0 && end >= 0)
        {
            var after = end + BlockEnd.Length;
            if (script.AsSpan(after).StartsWith("\r\n"))
            {
                after += 2;
            }
            // The line break this app put before its block goes too.
            var before =
                start >= 2 && script.AsSpan(start - 2, 2).SequenceEqual("\r\n") ? start - 2 : start;
            script = script[..before] + script[after..];
        }
        if (placement == null)
        {
            return script;
        }

        static string N(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var sub = "VrtmPlaceVrBackglass";
        return script
            + "\r\n"
            + BlockStart
            + "\r\n"
            + $"Sub {sub}\r\n"
            + "\tDim vrtmItem, vrtmName\r\n"
            + "\tOn Error Resume Next\r\n"
            + "\tFor Each vrtmItem In GetElements()\r\n"
            // Only VPX's own: "vr_backglass", or "vr_backglass" and three digits (the table has none, see Read).
            + "\t\tvrtmName = LCase(vrtmItem.Name)\r\n"
            + "\t\tIf vrtmName = \"vr_backglass\" Or (Len(vrtmName) = 15 And Left(vrtmName, 12) = \"vr_backglass\" And IsNumeric(Mid(vrtmName, 13))) Then\r\n"
            + $"\t\t\tvrtmItem.X = {N(placement.X)} : vrtmItem.Y = {N(placement.Y)} : vrtmItem.Height = {N(placement.Height)} : vrtmItem.RotX = {N(placement.RotX)}\r\n"
            + "\t\tEnd If\r\n"
            + "\tNext\r\n"
            + "\tOn Error Goto 0\r\n"
            + "End Sub\r\n"
            + $"{sub}\r\n"
            + BlockEnd
            + "\r\n";
    }

    /// <summary>A PuP pack in the table's pupvideos\ whose screens.pup shows the Backglass screen (2).</summary>
    public static bool ShowsPupBackglass(string tableDir)
    {
        var pupDir = Path.Combine(tableDir, PupPacks.FolderName);
        if (!Directory.Exists(pupDir))
        {
            return false;
        }

        foreach (var pack in PupPacks.PackRoots(pupDir))
        {
            var screens = Path.Combine(pack, "screens.pup");
            if (!File.Exists(screens))
            {
                continue;
            }

            foreach (var line in File.ReadLines(screens).Take(200))
            {
                var cols = PupPackOptions.CsvFields(line);
                if (cols.Count >= 6 && cols[0].Trim() == "2")
                {
                    if (cols[5].Trim().ToLowerInvariant() is not ("off" or "musiconly"))
                    {
                        return true;
                    }
                    break;
                }
            }
        }
        return false;
    }

    static Placement? Read(RootStorage root)
    {
        var game = root.OpenStorage("GameStg");
        var panels = new List<(string Name, Dictionary<string, byte[]> Fields)>();
        for (var i = 0; game.TryOpenStream("GameItem" + i, out var s) && s != null; i++)
        {
            var d = ReadAll(s);
            var type = BitConverter.ToInt32(d, 0);
            if (type != PrimitiveType && type != FlasherType)
            {
                continue;
            }

            var fields = TopFields(d);
            var name = Name(fields);
            if (
                type == FlasherType
                && fields.TryGetValue("RDMD", out var rdmd)
                && Int(rdmd) == ExtRender
                && fields.TryGetValue("RSTL", out var rstl)
                && Int(rstl) <= BackglassWindow
            )
            {
                return null; // the table shows VPX's Backglass window on a display of its own
            }
            if (AddedName().IsMatch(name))
            {
                // An item of the table's own named like the one VPX adds: the script couldn't tell them apart.
                return null;
            }
            if (type == PrimitiveType && PanelName().IsMatch(name))
            {
                panels.Add((name, fields));
            }
        }
        if (panels.Count != 1)
        {
            return null;
        }

        var panel = panels[0];
        if (!IsBlank(game, panel.Fields) || PanelCorners(panel.Fields) is not { } c)
        {
            return null;
        }

        // c: top-left, top-right, bottom-right, bottom-left. The centre, moved in front along the panel's normal,
        // and the X rotation that tilts an upright flasher (-90) back like the panel.
        var topY = (c[0].Y + c[1].Y) / 2;
        var botY = (c[2].Y + c[3].Y) / 2;
        var topZ = (c[0].Z + c[1].Z) / 2;
        var botZ = (c[2].Z + c[3].Z) / 2;
        var lean = MathF.Atan2(botY - topY, topZ - botZ);
        return new Placement(
            panel.Name,
            (c[0].X + c[1].X) / 2,
            (topY + botY) / 2 + InFront * MathF.Cos(lean),
            (topZ + botZ) / 2 + InFront * MathF.Sin(lean),
            -(90f - lean * 180f / MathF.PI)
        );
    }

    /// <summary>The panel shows no picture: none set, or one that compresses to almost nothing (a flat colour).</summary>
    static bool IsBlank(Storage game, Dictionary<string, byte[]> panel)
    {
        var image = panel.TryGetValue("IMAG", out var imag) ? Str(imag) : "";
        if (image.Length == 0)
        {
            return true;
        }

        for (var i = 0; game.TryOpenStream("Image" + i, out var s) && s != null; i++)
        {
            using (s)
            {
                var head = new byte[Math.Min(4096, (int)s.Length)];
                s.ReadExactly(head);
                var fields = TopFields([0, 0, 0, 0, .. head], stopAtData: true);
                if (!Name(fields).Equals(image, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var w = fields.TryGetValue("WDTH", out var wb) ? Int(wb) : 0;
                var h = fields.TryGetValue("HGHT", out var hb) ? Int(hb) : 0;
                return w > 0 && h > 0 && s.Length / ((double)w * h) < BlankBytesPerPixel;
            }
        }
        return false; // a picture this can't find: leave it alone
    }

    /// <summary>
    /// The panel's four corners in table space, as VPX draws it (scale, then its translation and rotations, then
    /// its position), ordered top-left, top-right, bottom-right, bottom-left (x left to right). Null unless it's
    /// a flat quad with level top and bottom edges, upright or leaning back by less than 45°.
    /// </summary>
    static Vector3[]? PanelCorners(Dictionary<string, byte[]> f)
    {
        if (!f.TryGetValue("M3VN", out var vn) || Int(vn) != 4)
        {
            return null;
        }

        byte[] raw;
        if (f.TryGetValue("M3CX", out var cx))
        {
            using var z = new ZLibStream(new MemoryStream(cx), CompressionMode.Decompress);
            var o = new MemoryStream();
            z.CopyTo(o);
            raw = o.ToArray();
        }
        else if (!f.TryGetValue("M3DX", out raw!))
        {
            return null;
        }
        if (raw.Length < 4 * 32 || !f.ContainsKey("VPOS") || !f.ContainsKey("VSIZ"))
        {
            return null;
        }

        float F(string tag, int at = 0) =>
            f.TryGetValue(tag, out var b) && b.Length >= at + 4 ? BitConverter.ToSingle(b, at) : 0f;
        Matrix4x4 Rx(string t) => Matrix4x4.CreateRotationX(F(t) * MathF.PI / 180);
        Matrix4x4 Ry(string t) => Matrix4x4.CreateRotationY(F(t) * MathF.PI / 180);
        Matrix4x4 Rz(string t) => Matrix4x4.CreateRotationZ(F(t) * MathF.PI / 180);
        var m =
            Matrix4x4.CreateScale(F("VSIZ"), F("VSIZ", 4), F("VSIZ", 8))
            * Matrix4x4.CreateTranslation(F("RTV3"), F("RTV4"), F("RTV5"))
            * Rz("RTV2")
            * Ry("RTV1")
            * Rx("RTV0")
            * Rz("RTV8")
            * Ry("RTV7")
            * Rx("RTV6")
            * Matrix4x4.CreateTranslation(F("VPOS"), F("VPOS", 4), F("VPOS", 8));
        var v = Enumerable
            .Range(0, 4)
            .Select(k =>
                Vector3.Transform(
                    new Vector3(
                        BitConverter.ToSingle(raw, k * 32),
                        BitConverter.ToSingle(raw, k * 32 + 4),
                        BitConverter.ToSingle(raw, k * 32 + 8)
                    ),
                    m
                )
            )
            .ToList();
        var top = v.OrderByDescending(p => p.Z).Take(2).OrderBy(p => p.X).ToArray();
        var bottom = v.OrderBy(p => p.Z).Take(2).OrderBy(p => p.X).ToArray();
        const float Tol = 1f;
        if (
            MathF.Abs(top[0].Z - top[1].Z) > Tol
            || MathF.Abs(bottom[0].Z - bottom[1].Z) > Tol
            || MathF.Abs(top[0].Y - top[1].Y) > Tol
            || MathF.Abs(bottom[0].Y - bottom[1].Y) > Tol
            || MathF.Abs(top[0].X - bottom[0].X) > Tol
            || MathF.Abs(top[1].X - bottom[1].X) > Tol
            || top[1].X - top[0].X < 100
            || top[0].Z - bottom[0].Z < 100
            || MathF.Abs(bottom[0].Y - top[0].Y) > top[0].Z - bottom[0].Z
        )
        {
            return null;
        }

        return [top[0], top[1], bottom[1], bottom[0]];
    }

    /// <summary>An item's own records (first of each tag), up to its first drag point or the end.</summary>
    static Dictionary<string, byte[]> TopFields(byte[] d, bool stopAtData = false)
    {
        var fields = new Dictionary<string, byte[]>();
        for (var p = 4; p + 8 <= d.Length; )
        {
            var len = BitConverter.ToInt32(d, p);
            if (len < 4 || p + 4 + len > d.Length)
            {
                break;
            }

            var tag = Encoding.ASCII.GetString(d, p + 4, 4);
            if (tag is "DPNT" or "ENDB" || (stopAtData && tag is "JPEG" or "BITS" or "DATA"))
            {
                break;
            }

            fields.TryAdd(tag, d[(p + 8)..(p + 4 + len)]);
            p += 4 + len;
        }
        return fields;
    }

    /// <summary>NAME: a game item's is UTF-16 (its length in bytes first), an image's is 8-bit.</summary>
    static string Name(Dictionary<string, byte[]> f)
    {
        if (!f.TryGetValue("NAME", out var b) || b.Length < 4)
        {
            return "";
        }

        var n = Math.Clamp(BitConverter.ToInt32(b, 0), 0, b.Length - 4);
        var bytes = b.AsSpan(4, n);
        return n >= 2 && bytes[1] == 0
            ? Encoding.Unicode.GetString(bytes)
            : Encoding.Latin1.GetString(bytes);
    }

    static string Str(byte[] b) =>
        b.Length < 4
            ? ""
            : Encoding.Latin1.GetString(
                b,
                4,
                Math.Clamp(BitConverter.ToInt32(b, 0), 0, b.Length - 4)
            );

    static int Int(byte[] b) => b.Length >= 4 ? BitConverter.ToInt32(b, 0) : 0;

    static byte[] ReadAll(Stream s)
    {
        using (s)
        {
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    /// <summary>The name VPX gives the backglass it adds: "vr_backglass", or with three digits ("vr_backglass001").</summary>
    [GeneratedRegex(@"^vr_backglass(?:\d{3})?$", RegexOptions.IgnoreCase)]
    private static partial Regex AddedName();

    /// <summary>"PinCab_Backglass", "VRCab_Backglass", "VR_Backglass", "Primary_Backglass".</summary>
    [GeneratedRegex(@"^(?:PinCab|VRCab|VR|Primary)_?Backglass$", RegexOptions.IgnoreCase)]
    private static partial Regex PanelName();
}
