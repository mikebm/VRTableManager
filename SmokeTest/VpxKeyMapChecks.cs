using VrTableManager.Services;

/// <summary>Keys set on Setup → Controls (VpxKeyMap), into the file the player picked.</summary>
static class VpxKeyMapChecks
{
    public static IEnumerable<(bool, string)> Run(string root)
    {
        yield return (
            VpxKeyMap.WithKey("Key;225 | OpenXR;16;o;0.3 | Key;226 & Key;30 | Key;1", 29)
                == "Key;29 | OpenXR;16;o;0.3 | Key;226 & Key;30",
            "keymap: a new key replaces the keys and keeps VR controller bindings and combinations"
        );
        yield return (
            VpxKeyMap.WithKey("", 29) == "Key;29",
            "keymap: an empty (default) mapping gets just the key"
        );
        yield return (
            VpxKeyMap.Describe("", 225) == "Left Shift (default)"
                && VpxKeyMap.Describe("Key;29 | OpenXR;16", 225) == "Z + VR controller"
                && VpxKeyMap.Describe("SDLJoy_03000000_1;4", 225) == "No key + controller button",
            "keymap: mappings are described by key name"
        );
        yield return (
            VpxKeyMap.FromCode("ShiftLeft") == 225
                && VpxKeyMap.FromCode("A") == 4
                && VpxKeyMap.FromCode("Digit1") == 30
                && VpxKeyMap.FromCode("NumPad5") == 93
                && VpxKeyMap.FromCode("F12") == 69
                && VpxKeyMap.FromCode("Escape") == 41
                && VpxKeyMap.FromCode("LaunchMail") == null,
            "keymap: Avalonia's physical keys become VPX's SDL scancodes"
        );

        var dir = Path.Combine(root, "vpx-keymap");
        var s = new AppSettings { VpxDir = Path.Combine(dir, "vpx") };
        Directory.CreateDirectory(s.VpxDir);
        var own = Path.Combine(s.VpxDir, "VPinballX.ini");
        var vrIni = VpxSetup.VrIniPath(s);
        var desktopIni = VpxSetup.DesktopIniPath(s);
        var oldPrefs = Environment.GetEnvironmentVariable("VRTM_VPX_PREFS");
        Environment.SetEnvironmentVariable("VRTM_VPX_PREFS", Path.Combine(dir, "no prefs")); // the base is the ini beside VPX
        try
        {
            string Get(string file, string mapping) => VpxKeyMap.Value(IniFile.Read(file), mapping);

            // No VPinballX.ini of the player's yet: "desktop and VR" can't be saved.
            var threw = false;
            try
            {
                VpxKeyMap.Set(s, "Start", 5, ControlsTarget.Everywhere);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            yield return (
                threw && !File.Exists(own),
                "keymap: desktop and VR without a VPinballX.ini says so and creates nothing"
            );

            File.WriteAllText(
                own,
                "[Player]\nX = 1\n\n[Input]\nDevices = Key\nMapping.LeftFlipper = \nMapping.RightFlipper = \nMapping.UIRight = Key;80\n"
            );
            File.WriteAllText(
                vrIni,
                "[Input]\nDevices = Key;OpenXR\nMapping.LeftFlipper = Key;225 | OpenXR;16;o;0.3\nMapping.UILeft = Key;225 | OpenXR;22;x;-0.9\n"
            );
            yield return (
                VpxKeyMap.IsSupported(s),
                "keymap: VPX 10.8.1 settings ([Input] Devices) are supported"
            );

            VpxKeyMap.Set(s, "LeftFlipper", 29, ControlsTarget.Everywhere);
            yield return (
                Get(own, "LeftFlipper") == "Key;29"
                    && Get(own, "LeftStagedFlipper") == "Key;29"
                    && Get(own, "UILeft") == "Key;29"
                    && IniFile.Read(own).GetValueOrDefault(("Player", "X")) == "1",
                "keymap: desktop and VR writes the player's VPinballX.ini, staged flipper and menu-left following"
            );
            yield return (
                Get(vrIni, "LeftFlipper") == "Key;29 | OpenXR;16;o;0.3"
                    && Get(vrIni, "UILeft") == "Key;29 | OpenXR;22;x;-0.9"
                    && Get(vrIni, "LeftStagedFlipper") == ""
                    && !File.Exists(desktopIni),
                "keymap: desktop and VR also updates a VR mapping that would hide it, keeping the VR controller"
            );
            yield return (
                VpxKeyMap
                    .Value(VpxIniLayers.Effective(s, vr: true), "LeftFlipper")
                    .StartsWith("Key;29")
                    && VpxKeyMap.Value(VpxIniLayers.Effective(s, vr: false), "LeftFlipper")
                        == "Key;29",
                "keymap: desktop and VR reaches both modes"
            );

            VpxKeyMap.Set(s, "RightFlipper", 56, ControlsTarget.Everywhere);
            yield return (
                Get(own, "RightFlipper") == "Key;56" && Get(own, "UIRight") == "Key;80",
                "keymap: a menu key the player moved elsewhere doesn't follow the flipper"
            );

            var ownBefore = File.ReadAllText(own);
            VpxKeyMap.Set(s, "RightMagna", 4, ControlsTarget.VrOnly);
            yield return (
                Get(vrIni, "RightMagna") == "Key;4"
                    && Get(vrIni, "UIDown") == "Key;4"
                    && File.ReadAllText(own) == ownBefore,
                "keymap: VR only writes the VR file and leaves VPinballX.ini alone"
            );

            VpxKeyMap.Set(s, "Start", 5, ControlsTarget.DesktopOnly);
            yield return (
                Get(desktopIni, "Start") == "Key;5"
                    && File.ReadAllText(own) == ownBefore
                    && Get(vrIni, "Start") == "",
                "keymap: desktop only writes the desktop file"
            );

            IniFile.Set(vrIni, VpxInput.Section, "Devices", "OpenXR");
            VpxKeyMap.Set(s, "Tilt", 23, ControlsTarget.VrOnly);
            yield return (
                IniFile.Read(vrIni).GetValueOrDefault((VpxInput.Section, "Devices"))
                    == "Key;OpenXR",
                "keymap: a device list without the keyboard gets it"
            );

            var effective = VpxIniLayers.Effective(s, vr: false);
            yield return (
                VpxKeyMap.SharedWith(effective, "Tilt", 29).Contains("Left flipper")
                    && !VpxKeyMap
                        .SharedWith(effective, "LeftFlipper", 29)
                        .Contains("Left staged flipper"),
                "keymap: a key another action uses is reported, but not the staged flipper that follows it"
            );

            // An override that sets only the follower: it follows what the mode used (the player's own leader key).
            File.WriteAllText(
                own,
                "[Input]\nDevices = Key\nMapping.LeftFlipper = Key;4\nMapping.UILeft = Key;4\n"
            );
            File.WriteAllText(
                vrIni,
                "[Input]\nDevices = Key;OpenXR\nMapping.UILeft = Key;4 | OpenXR;22;x;-0.9\nMapping.LeftStagedFlipper = Key;225 | OpenXR;16;o;0.6\n"
            );
            File.Delete(desktopIni);
            VpxKeyMap.Set(s, "LeftFlipper", 29, ControlsTarget.Everywhere);
            yield return (
                Get(vrIni, "UILeft") == "Key;29 | OpenXR;22;x;-0.9"
                    && Get(vrIni, "LeftStagedFlipper") == "Key;225 | OpenXR;16;o;0.6"
                    && Get(vrIni, "LeftFlipper") == "",
                "keymap: an override's follower moves with the key the mode used, and one set to another key stays"
            );

            // A leader with no key (joystick only) doesn't drag a joystick-only follower onto the key.
            File.WriteAllText(
                own,
                "[Input]\nDevices = Key;SDLJoy_x_1\nMapping.LeftFlipper = SDLJoy_x_1;0\nMapping.LeftStagedFlipper = SDLJoy_x_1;5\n"
            );
            File.Delete(vrIni);
            VpxKeyMap.Set(s, "LeftFlipper", 29, ControlsTarget.Everywhere);
            yield return (
                Get(own, "LeftFlipper") == "Key;29 | SDLJoy_x_1;0"
                    && Get(own, "LeftStagedFlipper") == "SDLJoy_x_1;5",
                "keymap: a leader without a key moves no followers"
            );

            yield return (
                VpxKeyMap.Describe("Key;226 & Key;30 | OpenXR;9", 0)
                    == "Left Alt+1 + VR controller",
                "keymap: a key combination is shown, not \"No key\""
            );

            // One file can't be written: the files already written are put back.
            var ownBeforeFail = File.ReadAllBytes(own);
            File.WriteAllText(vrIni, "[Input]\nMapping.Start = Key;30 | OpenXR;10\n");
            File.SetAttributes(vrIni, FileAttributes.ReadOnly);
            var failed = false;
            try
            {
                VpxKeyMap.Set(s, "Start", 5, ControlsTarget.Everywhere);
            }
            catch (Exception)
            {
                failed = true;
            }
            finally
            {
                File.SetAttributes(vrIni, FileAttributes.Normal);
            }
            yield return (
                failed && File.ReadAllBytes(own).SequenceEqual(ownBeforeFail),
                "keymap: if the VR file can't be written, VPinballX.ini is put back as it was"
            );

            // USE MY DESKTOP VPX CONTROLS reads the same VPinballX.ini as everything else (the preferences one first).
            var prefs = Path.Combine(dir, "prefs");
            var prefsIni = Path.Combine(prefs, "10.8", "VPinballX.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(prefsIni)!);
            File.WriteAllText(prefsIni, "[Input]\nDevices = Key\nMapping.Start = Key;31\n");
            Environment.SetEnvironmentVariable("VRTM_VPX_PREFS", prefs);
            yield return (
                VpxInput.FindDesktopIni(s) == prefsIni && VpxIniLayers.BasePath(s) == prefsIni,
                "keymap: the import uses the VPinballX.ini VPX loads, not a stale one beside the exe"
            );
            Environment.SetEnvironmentVariable("VRTM_VPX_PREFS", Path.Combine(dir, "no prefs"));
            File.Delete(desktopIni);

            // The override sets only the leader: in that mode the followers go by the override's leader.
            File.WriteAllText(
                own,
                "[Input]\nDevices = Key\nMapping.LeftFlipper = Key;4\nMapping.LeftStagedFlipper = Key;4\nMapping.UILeft = \n"
            );
            File.WriteAllText(
                vrIni,
                "[Input]\nDevices = Key;OpenXR\nMapping.LeftFlipper = Key;225 | OpenXR;16;o;0.3\n"
            );
            VpxKeyMap.Set(s, "LeftFlipper", 29, ControlsTarget.Everywhere);
            var vrAfter = VpxIniLayers.Effective(s, vr: true);
            var deskAfter = VpxIniLayers.Effective(s, vr: false);
            yield return (
                VpxKeyMap.Value(vrAfter, "LeftFlipper") == "Key;29 | OpenXR;16;o;0.3"
                    && VpxKeyMap.KeysIn(VpxKeyMap.Value(vrAfter, "UILeft")).SequenceEqual([29])
                    && VpxKeyMap
                        .KeysIn(VpxKeyMap.Value(vrAfter, "LeftStagedFlipper"))
                        .SequenceEqual([4]),
                "keymap: in VR, menu-left follows the VR flipper key and the staged flipper (on another key there) stays"
            );
            yield return (
                VpxKeyMap.Value(deskAfter, "LeftFlipper") == "Key;29"
                    && VpxKeyMap.Value(deskAfter, "LeftStagedFlipper") == "Key;29"
                    && VpxKeyMap
                        .EffectiveKeys(VpxKeyMap.Value(deskAfter, "UILeft"), 225)
                        .SequenceEqual([225]),
                "keymap: on the desktop, the staged flipper follows and menu-left (on another key) stays"
            );

            // The device list VR play ends up with (the player's, here) gets the keyboard, not just the file's own.
            File.WriteAllText(own, "[Input]\nDevices = SDLJoy_x_1\n");
            File.Delete(vrIni);
            VpxKeyMap.Set(s, "Start", 5, ControlsTarget.VrOnly);
            yield return (
                VpxIniLayers.Effective(s, vr: true).GetValueOrDefault((VpxInput.Section, "Devices"))
                    == "Key;SDLJoy_x_1",
                "keymap: VR only adds the keyboard to the device list VR play uses"
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("VRTM_VPX_PREFS", oldPrefs);
        }
    }
}
