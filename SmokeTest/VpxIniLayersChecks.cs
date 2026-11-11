using VrTableManager.Services;

/// <summary>Layered VPX settings: the player's own VPinballX.ini, the manager's VR/desktop overrides on top, and keeping in-game changes.</summary>
static class VpxIniLayersChecks
{
    public static List<(bool, string)> Run(string root)
    {
        var results = new List<(bool, string)>();
        var dir = Path.Combine(root, "ini layers");
        var prefs = Path.Combine(dir, "prefs");
        var oldPrefs = Environment.GetEnvironmentVariable("VRTM_VPX_PREFS");
        Environment.SetEnvironmentVariable("VRTM_VPX_PREFS", prefs);
        try
        {
            var s = new AppSettings
            {
                VpxDir = Path.Combine(dir, "vpx"),
                TablesDir = Path.Combine(dir, "tables"),
            };
            Directory.CreateDirectory(s.VpxDir);
            File.WriteAllText(Path.Combine(s.VpxDir, "vrtm-version.txt"), "v10.8.1-5436-af26b2d93");
            File.WriteAllText(
                Path.Combine(s.VpxDir, "VPinballX.ini"),
                "[Player]\nAAFactor = 0.5\n"
            ); // VPX prefers the one in its preferences folder
            var own = Path.Combine(prefs, "10.8", "VPinballX.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(own)!);
            File.WriteAllText(
                own,
                "; my settings\n[Player]\nAAFactor = 2\nMSAASamples = \n\n[Input]\nMapping.LeftFlipper = SDL.Pad;4\n\n[Plugin.PinMAME]\nEnable = \n"
            );
            results.Add(
                (
                    VpxIniLayers.BasePath(s) == own,
                    "layers: the base is the VPinballX.ini VPX itself loads (preferences folder for its version, before the one beside the exe)"
                )
            );

            VpxSetup.EnsureVrIni(s);
            IniFile.SetMany(
                VpxSetup.VrIniPath(s),
                "Player",
                [("MSAASamples", "1"), ("AAFactor", "")]
            );
            var vr = VpxIniLayers.Build(s, vr: true);
            var v = IniFile.Read(vr.Path);
            results.Add(
                (
                    v.GetValueOrDefault(("Player", "AAFactor")) == "2"
                        && v.GetValueOrDefault(("Player", "MSAASamples")) == "1"
                        && v.GetValueOrDefault(("PlayerVR", "AskToTurnOn")) == "0"
                        && v.GetValueOrDefault(("Plugin.PinMAME", "Enable")) == "1"
                        && v.GetValueOrDefault(("Input", "Mapping.LeftFlipper")) == "SDL.Pad;4"
                        && File.ReadAllText(vr.Path).StartsWith("; my settings"),
                    "layers: VR play gets the player's settings and controls, with the VR overrides on top (an empty override is no override)"
                )
            );

            File.WriteAllText(own, File.ReadAllText(own).Replace("AAFactor = 2", "AAFactor = 1.5"));
            results.Add(
                (
                    VpxGraphics.Read(VpxIniLayers.Effective(s, vr: true)).RenderScale == 1.5
                        && IniFile
                            .Read(VpxIniLayers.Build(s, vr: true).Path)
                            .GetValueOrDefault(("Player", "AAFactor")) == "1.5",
                    "layers: a change to the player's own VPinballX.ini reaches the next VR launch"
                )
            );

            // VR graphics switched off: the panel's settings stay in the file but VPX's own decide.
            s.UseVrGraphics = false;
            var offPlay = VpxIniLayers.Build(s, vr: true);
            var off = IniFile.Read(offPlay.Path);
            var offRead = VpxGraphics.Read(VpxIniLayers.Effective(s, vr: true));
            // Changed in VPX's menu while off: saved into the player's own VPinballX.ini, as outside the manager, and
            // not over the panel's settings. Other changes still go to the VR overrides.
            var ownBeforeOff = File.ReadAllBytes(own);
            var ownTextBeforeOff = File.ReadAllText(own);
            // One [Player] section with a graphics and a non-graphics change: each goes to its own file.
            IniFile.SetMany(offPlay.Path, "Player", [("MSAASamples", "3"), ("SoundVolume", "40")]);
            IniFile.SetMany(offPlay.Path, "PlayerVR", [("TableY", "7.0")]);
            var offKept = offPlay.SaveBack();
            var offNotes = offPlay.TakeNotes();
            var ownAfterOff = IniFile.Read(own);
            var ownTextAfterOff = File.ReadAllText(own);
            var vrAfterOff = IniFile.Read(VpxSetup.VrIniPath(s));
            File.WriteAllBytes(own, ownBeforeOff);
            s.UseVrGraphics = true;
            results.Add(
                (
                    off.GetValueOrDefault(("Player", "MSAASamples")) == ""
                        && off.GetValueOrDefault(("PlayerVR", "AskToTurnOn")) == "0"
                        && offRead.Msaa == 0
                        && offKept == 2
                        && offNotes.Any(n =>
                            n.StartsWith("saved 1 graphics setting changed during play into")
                        )
                        && ownTextAfterOff
                            == ownTextBeforeOff.Replace("MSAASamples = \n", "MSAASamples = 3\n")
                        && !ownAfterOff.ContainsKey(("Player", "SoundVolume"))
                        && vrAfterOff.GetValueOrDefault(("Player", "SoundVolume")) == "40"
                        && vrAfterOff.GetValueOrDefault(("PlayerVR", "TableY")) == "7.0"
                        && IniFile
                            .Read(VpxSetup.VrIniPath(s))
                            .GetValueOrDefault(("PlayerVR", "TableY")) == "7.0"
                        && IniFile
                            .Read(VpxSetup.VrIniPath(s))
                            .GetValueOrDefault(("Player", "MSAASamples")) == "1"
                        && IniFile
                            .Read(VpxIniLayers.Build(s, vr: true).Path)
                            .GetValueOrDefault(("Player", "MSAASamples")) == "1",
                    "layers: VR graphics off leaves VPX's own graphics (other VR settings stay), saves in-game graphics changes into the player's VPinballX.ini (only the changed line), not the panel's; on again brings the panel's back"
                )
            );

            // Off, but the player's file can't be written: the other changes are still kept, and the log says why.
            s.UseVrGraphics = false;
            var lockedPlay = VpxIniLayers.Build(s, vr: true);
            IniFile.SetMany(lockedPlay.Path, "Player", [("MSAASamples", "2")]);
            IniFile.SetMany(lockedPlay.Path, "PlayerVR", [("TableY", "8.0")]);
            File.SetAttributes(own, FileAttributes.ReadOnly);
            int lockedKept;
            List<string> lockedNotes;
            try
            {
                lockedKept = lockedPlay.SaveBack();
                lockedNotes = lockedPlay.TakeNotes();
            }
            finally
            {
                File.SetAttributes(own, FileAttributes.Normal);
            }
            results.Add(
                (
                    lockedKept == 1
                        && lockedNotes.Any(n => n.StartsWith("couldn't save 1 graphics setting"))
                        && File.ReadAllBytes(own).SequenceEqual(ownBeforeOff)
                        && !Directory
                            .EnumerateFiles(Path.GetDirectoryName(own)!, "*.vrtm-new")
                            .Any()
                        && IniFile
                            .Read(VpxSetup.VrIniPath(s))
                            .GetValueOrDefault(("PlayerVR", "TableY")) == "8.0",
                    "layers: VR graphics off with the player's VPinballX.ini read-only: other changes are still kept, the file is left as it was, and the log says why"
                )
            );

            // Off, and the table's own .ini sets its render scale: VPX writing that back isn't saved as the player's.
            var ownTable = Path.Combine(dir, "tables", "Own Scale", "Own Scale.vpx");
            Directory.CreateDirectory(Path.GetDirectoryName(ownTable)!);
            VpxGraphics.WriteTableScale(ownTable, 0.8);
            var tablePlay = VpxIniLayers.Build(
                s,
                vr: true,
                afterPlay: new VpxIniLayers.AfterPlay(NotKept: () =>
                    VpxGraphics.OwnTableKeys(ownTable)
                )
            );
            IniFile.SetMany(tablePlay.Path, "Player", [("AAFactor", "0.8")]);
            tablePlay.SaveBack();
            results.Add(
                (
                    File.ReadAllBytes(own).SequenceEqual(ownBeforeOff)
                        && tablePlay.TakeNotes().Count == 0
                        && IniFile
                            .Read(VpxSetup.VrIniPath(s))
                            .GetValueOrDefault(("Player", "AAFactor")) == "",
                    "layers: a graphics setting the table's own .ini sets (its render scale) is never saved as the player's or everyone's"
                )
            );

            // Off, with no VPinballX.ini of the player's anywhere: a graphics change lasts for that game, and the log says so.
            var besideExe = Path.Combine(s.VpxDir, "VPinballX.ini");
            var besideExeBytes = File.ReadAllBytes(besideExe);
            File.Delete(own);
            File.Delete(besideExe);
            var nonePlay = VpxIniLayers.Build(s, vr: true);
            IniFile.SetMany(nonePlay.Path, "Player", [("MSAASamples", "2")]);
            var noneKept = nonePlay.SaveBack();
            var noneNotes = nonePlay.TakeNotes();
            var noneCreated = File.Exists(own) || File.Exists(besideExe);
            File.WriteAllBytes(own, ownBeforeOff);
            File.WriteAllBytes(besideExe, besideExeBytes);
            s.UseVrGraphics = true;
            results.Add(
                (
                    noneKept == 0
                        && !noneCreated
                        && noneNotes.Any(n => n.Contains("not kept: there's no VPinballX.ini"))
                        && IniFile
                            .Read(VpxSetup.VrIniPath(s))
                            .GetValueOrDefault(("Player", "MSAASamples")) == "1",
                    "layers: VR graphics off without a VPinballX.ini: a graphics change isn't kept (nor counted as kept), and the log says so"
                )
            );

            // On, and in desktop play: a graphics change goes to that mode's overrides, never the player's file.
            var vrOnPlay = VpxIniLayers.Build(s, vr: true);
            IniFile.SetMany(vrOnPlay.Path, "Player", [("FXAA", "4")]);
            var vrOnKept = vrOnPlay.SaveBack();
            VpxSetup.EnsureDesktopIni(s);
            var desktopBefore = File.ReadAllBytes(VpxSetup.DesktopIniPath(s));
            var desktopPlay = VpxIniLayers.Build(s, vr: false);
            IniFile.SetMany(desktopPlay.Path, "Player", [("FXAA", "5")]);
            var desktopKept = desktopPlay.SaveBack();
            var desktopFxaa = IniFile
                .Read(VpxSetup.DesktopIniPath(s))
                .GetValueOrDefault(("Player", "FXAA"));
            File.WriteAllBytes(VpxSetup.DesktopIniPath(s), desktopBefore);
            var vrFxaa = IniFile.Read(VpxSetup.VrIniPath(s)).GetValueOrDefault(("Player", "FXAA"));
            IniFile.SetMany(VpxSetup.VrIniPath(s), "Player", [("FXAA", "")]);
            results.Add(
                (
                    vrOnKept == 1
                        && desktopKept == 1
                        && vrFxaa == "4"
                        && desktopFxaa == "5"
                        && File.ReadAllBytes(own).SequenceEqual(ownBeforeOff),
                    "layers: with VR graphics on, and in desktop play, a graphics change is kept as that mode's override and the player's file is untouched"
                )
            );

            // Rewriting a file keeps how it was written: byte-order mark, line endings, and text in the PC's code page.
            var styled = Path.Combine(dir, "styled.ini");
            byte[] Styled(string value) =>
                [
                    0xEF,
                    0xBB,
                    0xBF,
                    .. System.Text.Encoding.UTF8.GetBytes(
                        $"; café\n[Player]\nMSAASamples = {value}\nPath = C:\\Tables\\Café\n"
                    ),
                ];
            File.WriteAllBytes(styled, Styled(""));
            IniFile.SetMany(styled, "Player", [("MSAASamples", "2")]);
            var bomKept = File.ReadAllBytes(styled).SequenceEqual(Styled("2"));
            var ansi = Path.Combine(dir, "ansi.ini");
            byte[] Ansi(string value) =>
                System.Text.Encoding.Latin1.GetBytes(
                    $"[Player]\r\nMSAASamples = {value}\r\nPath = C:\\Tables\\Café\r\n"
                );
            File.WriteAllBytes(ansi, Ansi(""));
            IniFile.SetMany(ansi, "Player", [("MSAASamples", "2")]);
            results.Add(
                (
                    bomKept
                        && File.ReadAllBytes(ansi).SequenceEqual(Ansi("2"))
                        && IniFile.Read(ansi).GetValueOrDefault(("Player", "Path"))
                            == @"C:\Tables\Café",
                    "ini: rewriting a file keeps its byte-order mark, line endings and non-UTF-8 text as they were"
                )
            );

            VpxSetup.EnsureDesktopIni(s);
            var d = IniFile.Read(VpxIniLayers.Build(s, vr: false).Path);
            results.Add(
                (
                    d.GetValueOrDefault(("PlayerVR", "AskToTurnOn")) == "2"
                        && d.GetValueOrDefault(("Plugin.PinMAME", "Enable")) == "1"
                        && d.GetValueOrDefault(("Player", "MSAASamples")) == ""
                        && d.GetValueOrDefault(("Player", "AAFactor")) == "1.5",
                    "layers: desktop play has VR off and the built-in PinMAME on (ROMs from the table's folder), without the VR overrides"
                )
            );

            // Bloom off for desktop play: written into each desktop launch, never into VR's or an override file.
            var bloomBefore = d.GetValueOrDefault(("Player", "ForceBloomOff"));
            s.DesktopBloomOff = true;
            var bloomOff = VpxIniLayers.Build(s, vr: false);
            var bloomForced = IniFile
                .Read(bloomOff.Path)
                .GetValueOrDefault(("Player", "ForceBloomOff"));
            var bloomVr = IniFile.Read(VpxIniLayers.Build(s, vr: true).Path);
            var bloomKept = bloomOff.SaveBack();
            s.DesktopBloomOff = false;
            var bloomLeft = IniFile.Read(VpxIniLayers.Build(s, vr: false).Path);
            results.Add(
                (
                    bloomBefore is null or ""
                        && bloomForced == "1"
                        && bloomVr.GetValueOrDefault(("Player", "ForceBloomOff")) is null or ""
                        && bloomKept == 0
                        && !IniFile
                            .Read(VpxSetup.DesktopIniPath(s))
                            .ContainsKey(("Player", "ForceBloomOff"))
                        && bloomLeft.GetValueOrDefault(("Player", "ForceBloomOff")) is null or "",
                    "layers: 'bloom off in desktop play' sets [Player] ForceBloomOff = 1 for desktop launches only, and turning it off leaves bloom to VPX"
                )
            );

            // VPX rewrites the play file when the table closes: keep real changes as VR overrides.
            var ownBefore = File.ReadAllBytes(own);
            var play = VpxIniLayers.Build(s, vr: true);
            IniFile.SetMany(play.Path, "PlayerVR", [("TableX", "5.0")]);
            IniFile.SetMany(play.Path, "Player", [("AAFactor", "1.500000")]); // same value, VPX's formatting
            IniFile.SetMany(play.Path, "Version", [("VPinballX", "10.8.1")]);
            var kept = play.SaveBack();
            var o = IniFile.Read(VpxSetup.VrIniPath(s));
            results.Add(
                (
                    kept == 1
                        && o.GetValueOrDefault(("PlayerVR", "TableX")) == "5.0"
                        && o.GetValueOrDefault(("Player", "AAFactor")) == ""
                        && !o.ContainsKey(("Version", "VPinballX"))
                        && File.ReadAllBytes(own).SequenceEqual(ownBefore),
                    "layers: a setting changed during play is kept for that mode; reformatted values and VPX bookkeeping aren't; the player's file is untouched"
                )
            );
            results.Add(
                (
                    play.SaveBack() == 0 && VrView.Read(VpxIniLayers.Effective(s, vr: true)).X == 5,
                    "layers: saving back happens once per launch, and the kept setting is used next time"
                )
            );

            // A table's PinUP Player folder is for that launch only, even if VPX writes it back changed.
            var pupPlay = VpxIniLayers.Build(
                s,
                vr: true,
                extra: [("Plugin.PUP", "PUPFolder", @"C:\Tables\One")]
            );
            IniFile.SetMany(pupPlay.Path, "Plugin.PUP", [("PUPFolder", @"C:\Tables\One\")]);
            results.Add(
                (
                    pupPlay.SaveBack() == 0
                        && !IniFile
                            .Read(VpxSetup.VrIniPath(s))
                            .ContainsKey(("Plugin.PUP", "PUPFolder")),
                    "layers: a table's PinUP Player folder is never kept for later launches"
                )
            );

            // A real-size table's lockbar width is for that launch only, even if VPX writes it back changed.
            var widthPlay = VpxIniLayers.Build(
                s,
                vr: true,
                extra: [("Player", "LockbarWidth", "57.1")],
                notKept: [("Player", "LockbarWidth")]
            );
            IniFile.SetMany(widthPlay.Path, "Player", [("LockbarWidth", "62.000000")]);
            results.Add(
                (
                    widthPlay.SaveBack() == 0
                        && IniFile
                            .Read(VpxSetup.VrIniPath(s))
                            .GetValueOrDefault(("Player", "LockbarWidth"), "") == "",
                    "layers: a table's own lockbar width is never kept for later launches"
                )
            );

            // No VPinballX.ini in the preferences folder: VPX uses the one beside the exe.
            File.Delete(own);
            results.Add(
                (
                    VpxIniLayers.BasePath(s) == Path.Combine(s.VpxDir, "VPinballX.ini"),
                    "layers: without a preferences-folder ini, the one beside VPX is the base"
                )
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("VRTM_VPX_PREFS", oldPrefs);
        }
        return results;
    }
}
