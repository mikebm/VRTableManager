using System.Text;
using VrTableManager.Services;

/// <summary>A table's hand-set VR room switch: which scripts have one, and setting it per launch through a .vbs beside the table.</summary>
static class VrRoomSwitchChecks
{
    static byte[] Rec(string tag, byte[] data) =>
        [.. BitConverter.GetBytes(data.Length + 4), .. Encoding.ASCII.GetBytes(tag), .. data];

    // CODE's length field doesn't cover its data: a second length follows (as RomAudio.Script reads it).
    static string Table(string root, string name, string script)
    {
        var dir = Path.Combine(root, "vr room", name);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".vpx");
        var code = Encoding.Latin1.GetBytes(script);
        using var cfb = OpenMcdf.RootStorage.Create(path);
        using var s = cfb.CreateStorage("GameStg").CreateStream("GameData");
        byte[] bytes =
        [
            .. BitConverter.GetBytes(4),
            .. "CODE"u8,
            .. BitConverter.GetBytes(code.Length),
            .. code,
            .. Rec("ENDB", []),
        ];
        s.Write(bytes, 0, bytes.Length);
        return path;
    }

    static string? Sidecar(string vpx) =>
        File.Exists(VrRoomSwitch.ScriptPath(vpx))
            ? Encoding.Latin1.GetString(File.ReadAllBytes(VrRoomSwitch.ScriptPath(vpx)))
            : null;

    public static List<(bool, string)> Run(string root)
    {
        var results = new List<(bool, string)>();
        const string getaway =
            "Option Explicit\r\n'** VR OPTIONS **\r\nVRRoom = 0        '0 = Desktop/FS    1 = VR Room\r\nScratches = 1\r\nif VRRoom = 1 Then\r\n\tVRRoom = 0\r\nend If\r\n";
        const string batman =
            "Const VRRoom\t\t\t\t\t= 0\t\t'0=VR Room off, 1=360 Room, 2=Minimal Room, 3=Ultra Minimal\r\n";
        const string bk2000 = "const VR_Room = 1 ' 1 = VR Room; 0 = desktop or cab mode, \r\n";
        const string mfDoom =
            "Const VRRoom = 1\t\t' 0 = big room, 1 = small room\r\nIf RenderingMode = 2 Then\r\n\tVRRoom = 1\r\nEnd If\r\n";
        const string btlc =
            "If RenderingMode = 2 Then\r\n\tVRRoom = 1\r\nElse\r\n\tVRRoom = 0\r\nEnd If\r\n";
        const string noComment = "VRRoom = 0\r\n";

        results.Add(
            (
                VrRoomSwitch.Find(getaway) is { Name: "VRRoom", Value: 0 } g
                    && getaway.Substring(g.ValueIndex - 9, 10) == "VRRoom = 0"
                    && VrRoomSwitch.Find(batman) is { Value: 0 }
                    && VrRoomSwitch.Find(bk2000) is { Name: "VR_Room", Value: 1 },
                "VR room switch: found in The Getaway, Batman 66 (Const, tabs) and Black Knight 2000 (VR_Room = 1); an indented one inside If isn't the switch"
            )
        );
        results.Add(
            (
                VrRoomSwitch.Find(mfDoom) == null
                    && VrRoomSwitch.Find(btlc) == null
                    && VrRoomSwitch.Find(noComment) == null,
                "VR room switch: left alone when the script checks RenderingMode itself, or the comment doesn't say 0 is off"
            )
        );

        // The Getaway: VR turns it on, desktop puts it back, and the .vpx is never written.
        var vpx = Table(root, "getaway", getaway);
        var vpxBytes = File.ReadAllBytes(vpx);
        var vrLine = VrRoomSwitch.Prepare(vpx, vr: true, enabled: true);
        var vrScript = Sidecar(vpx);
        results.Add(
            (
                vrScript == getaway.Replace("VRRoom = 0        '", "VRRoom = 1        '")
                    && File.Exists(VrRoomSwitch.NotePath(vpx))
                    && vrLine != null,
                "VR room switch: playing in VR writes <table>.vbs with VRRoom = 1 (only the switch line changes)"
            )
        );
        results.Add(
            (
                VrRoomSwitch.Prepare(vpx, vr: true, enabled: true)
                    == $"VRRoom = 1 for VR (the table has 0)"
                    && Sidecar(vpx) == vrScript,
                "VR room switch: playing in VR again keeps it as it is"
            )
        );
        VrRoomSwitch.Prepare(vpx, vr: false, enabled: true);
        results.Add(
            (
                Sidecar(vpx) == null
                    && !File.Exists(VrRoomSwitch.NotePath(vpx))
                    && File.ReadAllBytes(vpx).SequenceEqual(vpxBytes),
                "VR room switch: DESKTOP removes the copy again; the .vpx was never changed"
            )
        );

        // Turning the option off puts the table back at its next launch.
        VrRoomSwitch.Prepare(vpx, vr: true, enabled: true);
        VrRoomSwitch.Prepare(vpx, vr: true, enabled: false);
        results.Add(
            (
                Sidecar(vpx) == null && !File.Exists(VrRoomSwitch.NotePath(vpx)),
                "VR room switch: with the option off, the next launch puts the table's own script back"
            )
        );

        // Black Knight 2000 ships with the room on: desktop turns it off, VR leaves it as the table has it.
        var bk = Table(root, "bk2000", bk2000);
        results.Add(
            (
                VrRoomSwitch.Prepare(bk, vr: true, enabled: true) == null && Sidecar(bk) == null,
                "VR room switch: a table already set for VR is left alone in VR"
            )
        );
        VrRoomSwitch.Prepare(bk, vr: false, enabled: true);
        var bkDesktop = Sidecar(bk);
        VrRoomSwitch.Prepare(bk, vr: true, enabled: true);
        results.Add(
            (
                bkDesktop == bk2000.Replace("VR_Room = 1", "VR_Room = 0") && Sidecar(bk) == null,
                "VR room switch: DESKTOP turns a table's VR room off; VR play puts its own setting back"
            )
        );

        // A .vbs of the user's own: changed in place and put back to its value, their other edits kept.
        var own = Table(root, "own script", batman);
        var mine = batman + "' my tweak\r\n";
        File.WriteAllBytes(VrRoomSwitch.ScriptPath(own), Encoding.Latin1.GetBytes(mine));
        VrRoomSwitch.Prepare(own, vr: true, enabled: true);
        var ownVr = Sidecar(own);
        VrRoomSwitch.Prepare(own, vr: false, enabled: true);
        results.Add(
            (
                ownVr == mine.Replace("= 0\t\t'", "= 1\t\t'")
                    && Sidecar(own) == mine
                    && !File.Exists(VrRoomSwitch.NotePath(own)),
                "VR room switch: a .vbs of the user's own is switched in place and put back, keeping their edits"
            )
        );

        // Guardians of the Galaxy: a True/False switch with a comment that doesn't mention 0.
        const string gotg =
            "Option Explicit\r\nConst VRRoom \t\t\t\t\t= False\t' Turns on VR Room and Cabinet.   VPVR must also be set for desktop play.\r\nIf VRRoom = True Then 'Turns on VR Room and Cabinet\r\nEnd If\r\n";
        var gg = Table(root, "gotg", gotg);
        var ggLine = VrRoomSwitch.Prepare(gg, vr: true, enabled: true);
        var ggVr = Sidecar(gg);
        VrRoomSwitch.Prepare(gg, vr: false, enabled: true);
        results.Add(
            (
                VrRoomSwitch.Find(gotg) is { Value: 0, IsBool: true }
                    && ggVr == gotg.Replace("= False\t'", "= True\t'")
                    && ggLine == "VRRoom = 1 for VR (the table has 0)"
                    && Sidecar(gg) == null,
                "VR room switch: a True/False switch (Guardians of the Galaxy) is set to True in VR and put back for desktop"
            )
        );

        // PuPlayer.GetRoot has no trailing backslash under VPX's plugin: scripts that leave it out get one.
        const string ddRoot =
            "Dim pupPackScreenFile: pupPackScreenFile = PuPlayer.GetRoot & \"DarkestDungeon\\ScreenType.txt\"\r\n";
        const string scorbit =
            "Set f = fso.OpenTextFile(puplayer.getroot & pgamename & \"\\sToken.dat\",1)\r\n";
        const string fine =
            "Set f = fso.OpenTextFile(puplayer.getroot&\"\\\" & cGameName & \"\\sToken.dat\",1)\r\nset pFolder = fso.GetFolder(PuPlayer.getroot)\r\n";
        results.Add(
            (
                PupRootPaths.Fix(ddRoot)
                    == ddRoot.Replace("& \"DarkestDungeon", "& \"\\DarkestDungeon")
                    && PupRootPaths.Fix(scorbit)
                        == scorbit.Replace("getroot & pgamename", "getroot & \"\\\" & pgamename")
                    && PupRootPaths.Fix(fine) == fine,
                "PuP GetRoot: a backslash goes after GetRoot when a string or variable is joined on without one; joins that have it are left alone"
            )
        );

        // Darkest Dungeon picks its own VR room (RenderingMode): the copy carries only the GetRoot fix, for VR and desktop.
        const string dd = "If RenderingMode = 2 Then\r\nEnd If\r\n" + ddRoot;
        var ddVpx = Table(root, "darkest", dd);
        var ddLine = VrRoomSwitch.Prepare(ddVpx, vr: true, enabled: true);
        var ddVr = Sidecar(ddVpx);
        var ddAgain = VrRoomSwitch.Prepare(ddVpx, vr: false, enabled: true);
        results.Add(
            (
                ddVr == PupRootPaths.Fix(dd)
                    && ddVr != dd
                    && ddLine != null
                    && ddLine.Contains("GetRoot")
                    && ddAgain == null
                    && Sidecar(ddVpx) == ddVr,
                "PuP GetRoot: Darkest Dungeon gets a fixed copy of its script on its first launch, kept for VR and desktop alike"
            )
        );
        File.Delete(ddVpx);
        Table(root, "darkest", dd + "' v2\r\n");
        VrRoomSwitch.Prepare(ddVpx, vr: true, enabled: true);
        var plain = Table(root, "plain", getaway.Replace("VRRoom = 0", "Dim x"));
        results.Add(
            (
                Sidecar(ddVpx) == PupRootPaths.Fix(dd + "' v2\r\n")
                    && VrRoomSwitch.Prepare(plain, vr: true, enabled: true) == null
                    && Sidecar(plain) == null,
                "PuP GetRoot: the fixed copy follows a table update; a table that needs neither fix gets no copy"
            )
        );

        // Edits to the copy this app made are the user's from then on: kept, and still switched and fixed.
        var edited = Table(root, "edited", dd);
        VrRoomSwitch.Prepare(edited, vr: true, enabled: true);
        var mineDd = Sidecar(edited) + "' my fix\r\n";
        File.WriteAllBytes(VrRoomSwitch.ScriptPath(edited), Encoding.Latin1.GetBytes(mineDd));
        VrRoomSwitch.Prepare(edited, vr: true, enabled: true);
        VrRoomSwitch.Prepare(edited, vr: false, enabled: true);
        results.Add(
            (
                Sidecar(edited) == mineDd,
                "script copy: a copy this app made and the user then edited keeps their edit"
            )
        );

        // A lowercase switch at its own value isn't rewritten as True/False (no copy when nothing changes).
        var lower = Table(root, "lower", "Const VRRoom = false ' Turns on VR Room and Cabinet\r\n");
        results.Add(
            (
                VrRoomSwitch.Prepare(lower, vr: false, enabled: true) == null
                    && VrRoomSwitch.Prepare(lower, vr: true, enabled: false) == null
                    && Sidecar(lower) == null
                    && !File.Exists(VrRoomSwitch.NotePath(lower)),
                "VR room switch: a lowercase false left at its own value gets no copy"
            )
        );

        // True/False inside a Sub isn't the setting; a numbered switch with its comment wins over it.
        const string subBool =
            "VRRoom = 0 '0 = Desktop/FS 1 = VR Room\r\nSub X\r\nVRRoom = True\r\nEnd Sub\r\n";
        const string boolInSub = "Sub X\r\nVRRoom = True\r\nEnd Sub\r\n";
        results.Add(
            (
                VrRoomSwitch.Find(subBool) is { Value: 0, IsBool: false }
                    && VrRoomSwitch.Find(boolInSub) == null,
                "VR room switch: a True/False line inside a Sub doesn't count"
            )
        );

        // GetRoot inside a string literal or a comment, vbNullString and "/x" are left alone; without the PuP
        // plugin, no fix.
        const string literals =
            "ExecuteGlobal \"r = PuPlayer.GetRoot & cGameName\"\r\nx = PuPlayer.GetRoot & vbNullString\r\n' PuPlayer.GetRoot & \"a\"\r\ny = PuPlayer.GetRoot & \"/a\"\r\n";
        var noPup = Table(root, "no pup", dd);
        results.Add(
            (
                PupRootPaths.Fix(literals) == literals
                    && VrRoomSwitch.Prepare(noPup, vr: true, enabled: true, fixPupRoot: false)
                        == null
                    && Sidecar(noPup) == null,
                "PuP GetRoot: string literals, comments, vb constants and / paths are left alone; nothing without the PuP plugin"
            )
        );

        // A copy an older version made (its note has no WrittenHash): the user's edits in it are kept; one that is
        // exactly what this app makes is still the app's (removed again for the desktop).
        string LegacyNote(int own) => $"{{\"Own\":{own},\"Created\":true,\"ScriptHash\":null}}";
        var legacyEdited = Table(root, "legacy edited", getaway);
        var legacyMine =
            getaway.Replace("VRRoom = 0        '", "VRRoom = 1        '") + "' my option\r\n";
        File.WriteAllBytes(
            VrRoomSwitch.ScriptPath(legacyEdited),
            Encoding.Latin1.GetBytes(legacyMine)
        );
        var legacyHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.Latin1.GetBytes(getaway))
        );
        File.WriteAllText(
            VrRoomSwitch.NotePath(legacyEdited),
            LegacyNote(0).Replace("null", $"\"{legacyHash}\"")
        );
        VrRoomSwitch.Prepare(legacyEdited, vr: false, enabled: true);
        var legacyPlain = Table(root, "legacy plain", getaway);
        File.WriteAllBytes(
            VrRoomSwitch.ScriptPath(legacyPlain),
            Encoding.Latin1.GetBytes(getaway.Replace("VRRoom = 0        '", "VRRoom = 1        '"))
        );
        File.WriteAllText(
            VrRoomSwitch.NotePath(legacyPlain),
            LegacyNote(0).Replace("null", $"\"{legacyHash}\"")
        );
        VrRoomSwitch.Prepare(legacyPlain, vr: false, enabled: true);
        results.Add(
            (
                Sidecar(legacyEdited)
                    == legacyMine.Replace("VRRoom = 1        '", "VRRoom = 0        '")
                    && Sidecar(legacyPlain) == null,
                "script copy: an older version's copy keeps the user's edits; one exactly as the app made it is still the app's"
            )
        );

        // The table is updated after the user edited the app's copy: their copy stays.
        var updEdited = Table(root, "update edited", dd);
        VrRoomSwitch.Prepare(updEdited, vr: true, enabled: true);
        var updMine = Sidecar(updEdited) + "' my patch\r\n";
        File.WriteAllBytes(VrRoomSwitch.ScriptPath(updEdited), Encoding.Latin1.GetBytes(updMine));
        File.Delete(updEdited);
        Table(root, "update edited", dd + "' v2\r\n");
        VrRoomSwitch.Prepare(updEdited, vr: true, enabled: true);
        results.Add(
            (
                Sidecar(updEdited) == updMine,
                "script copy: a table update keeps a copy the user edited"
            )
        );

        // The table was updated while the copy was there: the copy is of the old script, so it goes.
        var upd = Table(root, "updated", getaway);
        VrRoomSwitch.Prepare(upd, vr: true, enabled: true);
        File.Delete(upd);
        Table(root, "updated", getaway + "' v2\r\n");
        VrRoomSwitch.Prepare(upd, vr: true, enabled: true);
        results.Add(
            (
                Sidecar(upd)
                    == (getaway + "' v2\r\n").Replace("VRRoom = 0        '", "VRRoom = 1        '"),
                "VR room switch: after a table update the copy is made again from the new script"
            )
        );

        results.Add(
            (
                VrRoomSwitch.Prepare(
                    Path.Combine(root, "vr room", "missing.vpx"),
                    vr: true,
                    enabled: true
                )
                    is { } skipped
                    && skipped.StartsWith("VR room switch skipped"),
                "VR room switch: a table it can't read still launches (never throws)"
            )
        );
        return results;
    }
}
