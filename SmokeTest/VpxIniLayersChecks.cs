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
