using VrTableManager.Services;

/// <summary>Desktop VPX controls copied into the VR settings file (VpxInput).</summary>
static class VpxInputChecks
{
    public static IEnumerable<(bool, string)> Run(string root)
    {
        var desktop = new List<(string, string)>
        {
            ("Devices", "Key;SDL.Xbox"),
            ("Device.SDL.Xbox.Type", "2"),
            ("Mapping.LeftFlipper", "SDL.Xbox;4 | Key;225"),
            ("Mapping.Start", "SDL.Xbox;6"),
            ("Mapping.ShowRules", ""),
            ("Mapping.Nudge0.X", "SDL.Xbox;0;P;0;1;1"),
        };
        var vr = new List<(string, string)>
        {
            ("Devices", "Key;OpenXR"),
            ("Mapping.LeftFlipper", "Key;225 | OpenXR;16;o;0.300000"),
            ("Mapping.Start", "Key;30 | OpenXR;10"),
            ("Mapping.ShowRules", "Key;9"),
            ("Mapping.Nudge0.X", "OpenXR;20;P;0;1;1"),
        };
        var m = VpxInput.Merge(desktop, vr).ToDictionary(kv => kv.Key, kv => kv.Value);
        yield return (
            m["Devices"] == "Key;SDL.Xbox;OpenXR",
            "devices: desktop ones plus the VR controllers"
        );
        yield return (
            m["Mapping.LeftFlipper"] == "SDL.Xbox;4 | Key;225 | OpenXR;16;o;0.300000",
            "a desktop mapping keeps the VR controller binding as an alternative"
        );
        yield return (
            m["Mapping.Start"] == "SDL.Xbox;6 | OpenXR;10",
            "the desktop keyboard binding replaces the VR one"
        );
        yield return (
            !m.ContainsKey("Mapping.ShowRules"),
            "an empty desktop value (VPX default) leaves the VR mapping alone"
        );
        yield return (
            m["Mapping.Nudge0.X"] == "SDL.Xbox;0;P;0;1;1",
            "nudge/plunger sensors take the desktop source"
        );
        yield return (m["Device.SDL.Xbox.Type"] == "2", "device descriptions are copied");

        // End to end: VR play follows the desktop controls (not a copy of them) until VR has its own.
        var dir = Path.Combine(root, "vpx-input");
        var s = new AppSettings { VpxDir = Path.Combine(dir, "vpx") };
        Directory.CreateDirectory(s.VpxDir);
        var oldPrefs = Environment.GetEnvironmentVariable("VRTM_VPX_PREFS");
        Environment.SetEnvironmentVariable("VRTM_VPX_PREFS", Path.Combine(dir, "no prefs")); // the base is the ini beside VPX
        try
        {
            var desktopIni = Path.Combine(s.VpxDir, "VPinballX.ini");
            File.WriteAllText(
                desktopIni,
                "[Player]\nX = 1\n\n[Input]\nDevices = Key;SDL.Pad\nMapping.LeftFlipper = SDL.Pad;4\n"
            );
            VpxSetup.EnsureVrIni(s);
            string? VrLeftFlipper() =>
                VpxIniLayers
                    .Effective(s, vr: true)
                    .GetValueOrDefault((VpxInput.Section, "Mapping.LeftFlipper"));
            yield return (
                VrLeftFlipper() == "SDL.Pad;4" && !VpxInput.HasMappings(VpxSetup.VrIniPath(s)),
                "VR play uses the desktop controls without copying them"
            );
            IniFile.Set(desktopIni, VpxInput.Section, "Mapping.LeftFlipper", "SDL.Pad;5");
            yield return (VrLeftFlipper() == "SDL.Pad;5", "a desktop remap reaches VR play");

            // Remaps made in VR are VR's own and aren't overwritten on the next launch.
            IniFile.Set(
                VpxSetup.VrIniPath(s),
                VpxInput.Section,
                "Mapping.LeftFlipper",
                "SDL.Pad;9"
            );
            VpxSetup.EnsureVrIni(s);
            yield return (VrLeftFlipper() == "SDL.Pad;9", "VR remaps survive later launches");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VRTM_VPX_PREFS", oldPrefs);
        }
    }
}

/// <summary>The VR table position VPX saves (VrView).</summary>
static class VrViewChecks
{
    public static IEnumerable<(bool, string)> Run(string root)
    {
        var ini = Path.Combine(root, "vrview", "vr.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        File.WriteAllText(
            ini,
            "[PlayerVR]\nAskToTurnOn = 0\nOrientation = 6.741632\nTableX = 0.708085\nTableY = 18.982412\nTableZ = 0.0\n"
        );
        var v = VrView.Read(ini);
        yield return (
            !v.IsDefault && v.Describe() == "offset X 0.7 cm, Y 19 cm, Z 0 cm, turned 6.7°",
            "the saved VR table position is read"
        );
        VrView.Reset(ini);
        yield return (
            VrView.Read(ini).IsDefault
                && IniFile.Read(ini).GetValueOrDefault(("PlayerVR", "AskToTurnOn")) == "0",
            "resetting it clears only the position"
        );
    }
}
