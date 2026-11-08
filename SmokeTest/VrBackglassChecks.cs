using System.Text;
using VrTableManager.Services;

/// <summary>
/// VPX's VR backglass moved onto a VR cabinet's blank backglass panel: which tables get it (a blank upright panel
/// and a PuP pack that shows a backglass), where it goes, and the block the script copy carries in VR only.
/// </summary>
static class VrBackglassChecks
{
    static byte[] Rec(string tag, byte[] data) =>
        [.. BitConverter.GetBytes(data.Length + 4), .. Encoding.ASCII.GetBytes(tag), .. data];

    static byte[] I32(int v) => BitConverter.GetBytes(v);

    static byte[] F32(params float[] v) => [.. v.SelectMany(BitConverter.GetBytes)];

    static byte[] WideName(string s) => [.. I32(s.Length * 2), .. Encoding.Unicode.GetBytes(s)];

    // An upright quad 1000 wide and 600 tall at y = -200, its bottom at z = 1000; no scale or rotation.
    static byte[] Panel(string name, string image) =>
        [
            .. I32(19),
            .. Rec("VPOS", F32(0, 0, 0, 0)),
            .. Rec("VSIZ", F32(1, 1, 1, 0)),
            .. Enumerable.Range(0, 9).SelectMany(i => Rec("RTV" + i, F32(0))),
            .. Rec("IMAG", [.. I32(image.Length), .. Encoding.Latin1.GetBytes(image)]),
            .. Rec("NAME", WideName(name)),
            .. Rec("M3VN", I32(4)),
            .. Rec(
                "M3DX",
                F32(
                    0,
                    -200,
                    1600,
                    0,
                    1,
                    0,
                    0,
                    0,
                    1000,
                    -200,
                    1600,
                    0,
                    1,
                    0,
                    1,
                    0,
                    1000,
                    -200,
                    1000,
                    0,
                    1,
                    0,
                    1,
                    1,
                    0,
                    -200,
                    1000,
                    0,
                    1,
                    0,
                    0,
                    1
                )
            ),
            .. Rec("ENDB", []),
        ];

    // A 1000×600 picture: a 20 KB flat colour, or 2 MB of artwork.
    static byte[] Image(string name, int bytes) =>
        [
            .. Rec("NAME", [.. I32(name.Length), .. Encoding.Latin1.GetBytes(name)]),
            .. Rec("WDTH", I32(1000)),
            .. Rec("HGHT", I32(600)),
            .. Rec("JPEG", new byte[bytes]),
            .. Rec("ENDB", []),
        ];

    static string Table(
        string root,
        string name,
        byte[][] items,
        byte[][] images,
        bool pupBackglass
    )
    {
        var dir = Path.Combine(root, "vr backglass", name);
        var pack = Path.Combine(dir, "pupvideos", "pack");
        Directory.CreateDirectory(pack);
        File.WriteAllText(
            Path.Combine(pack, "screens.pup"),
            "ScreenNum,ScreenDes,PlayList,PlayFile,Loopit,Active,Priority,CustomPos\r\n2,Backglass,,,0,"
                + (pupBackglass ? "ForcePopBack" : "off")
                + ",,\r\n"
        );
        var path = Path.Combine(dir, name + ".vpx");
        using var cfb = OpenMcdf.RootStorage.Create(path);
        var gs = cfb.CreateStorage("GameStg");
        void Put(string n, byte[] bytes)
        {
            using var s = gs.CreateStream(n);
            s.Write(bytes, 0, bytes.Length);
        }
        var code = Encoding.Latin1.GetBytes("Option Explicit\r\n");
        Put("GameData", [.. I32(4), .. "CODE"u8, .. I32(code.Length), .. code, .. Rec("ENDB", [])]);
        for (var i = 0; i < items.Length; i++)
        {
            Put("GameItem" + i, items[i]);
        }
        for (var i = 0; i < images.Length; i++)
        {
            Put("Image" + i, images[i]);
        }
        return path;
    }

    public static List<(bool, string)> Run(string root)
    {
        var results = new List<(bool, string)>();
        var blank = Table(
            root,
            "blank",
            [Panel("PinCab_Backglass", "backglassimage")],
            [Image("backglassimage", 20_000)],
            pupBackglass: true
        );
        var p = VrBackglass.Find(blank);
        results.Add(
            (
                p is { Panel: "PinCab_Backglass" }
                    && Math.Abs(p.X - 500) < 0.01
                    && Math.Abs(p.Y - (-185)) < 0.01
                    && Math.Abs(p.Height - 1300) < 0.01
                    && Math.Abs(p.RotX + 90) < 0.01,
                "VR backglass: a blank upright cabinet panel gets VPX's backglass centred on it, 15 in front, upright"
            )
        );

        var art = Table(
            root,
            "art",
            [Panel("PinCab_Backglass", "backglassimage")],
            [Image("backglassimage", 2_000_000)],
            pupBackglass: true
        );
        var noPup = Table(root, "no pup", [Panel("PinCab_Backglass", "")], [], pupBackglass: false);
        results.Add(
            (
                VrBackglass.Find(art) == null && VrBackglass.Find(noPup) == null,
                "VR backglass: not over real backglass art, nor without a PuP pack that shows a backglass"
            )
        );

        // Fish Tales names its panel VR_Backglass, the name VPX gives its own: the script couldn't tell them apart.
        var sameName = Table(
            root,
            "same name",
            [Panel("VR_Backglass", "")],
            [],
            pupBackglass: true
        );
        var block = VrBackglass.WithScript("", p);
        results.Add(
            (
                VrBackglass.Find(sameName) == null
                    && block.Contains("vrtmName = \"vr_backglass\" Or (Len(vrtmName) = 15")
                    && !block.Contains("Left(vrtmItem.Name"),
                "VR backglass: only VPX's own vr_backglass / vr_backglass### is moved; a table with an item of that name is left alone"
            )
        );

        // The script copy: the block in VR, gone again on the desktop; never twice.
        var withBlock = VrBackglass.WithScript("Option Explicit\r\n", p);
        var again = VrBackglass.WithScript(withBlock, p);
        results.Add(
            (
                withBlock.Contains(
                    "vrtmItem.X = 500 : vrtmItem.Y = -185 : vrtmItem.Height = 1300 : vrtmItem.RotX = -90"
                )
                    && again == withBlock
                    && VrBackglass.WithScript(withBlock, null) == "Option Explicit\r\n",
                "VR backglass: the script block moves vr_backglass*, isn't added twice, and comes out again"
            )
        );

        VrRoomSwitch.Prepare(blank, vr: true, enabled: true, backglass: p);
        var vrScript = File.ReadAllText(VrRoomSwitch.ScriptPath(blank));
        VrRoomSwitch.Prepare(blank, vr: false, enabled: true, backglass: p);
        results.Add(
            (
                vrScript.Contains("VrtmPlaceVrBackglass")
                    && !File.Exists(VrRoomSwitch.ScriptPath(blank))
                    && !File.Exists(VrRoomSwitch.NotePath(blank)),
                "VR backglass: a VR launch plays a script copy with the block; the desktop plays the table's own script"
            )
        );
        return results;
    }
}
