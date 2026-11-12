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
                && VpxKeyMap.Describe(
                    "SDLJoy_03000000_1;4 | SDLJoy_03000000_1;258 | SDLJoy_03000000_1;512;+;0.5",
                    225,
                    d => d == "SDLJoy_03000000_1" ? "PinOne" : null
                ) == "PinOne button 5 / PinOne d-pad up + controller axis",
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

            // ADD: one more key, the ones it had still working.
            yield return (
                VpxKeyMap.WithAddedKey("", 225, 29) == "Key;225 | Key;29"
                    && VpxKeyMap.WithAddedKey("Key;4 | OpenXR;16", 225, 29)
                        == "Key;4 | OpenXR;16 | Key;29"
                    && VpxKeyMap.WithAddedKey("Key;4", 225, 4) == "Key;4",
                "keymap: an added key keeps the keys (VPX's default one too) and the VR controller"
            );
            yield return (
                VpxKeyMap.WithDefaultKey("Key;4 | OpenXR;16", 225) == "Key;225 | OpenXR;16"
                    && VpxKeyMap.WithDefaultKey("Key;4 | Key;5", 225) == "",
                "keymap: a reset keeps other bindings, and with none it's VPX's default (empty)"
            );

            File.WriteAllText(
                own,
                "[Input]\nDevices = Key\nMapping.LeftFlipper = Key;4\nMapping.LeftStagedFlipper = Key;4\nMapping.UILeft = Key;4\n"
            );
            File.Delete(vrIni);
            VpxKeyMap.Add(s, "LeftFlipper", 29, ControlsTarget.Everywhere);
            yield return (
                Get(own, "LeftFlipper") == "Key;4 | Key;29"
                    && Get(own, "LeftStagedFlipper") == "Key;4 | Key;29"
                    && Get(own, "UILeft") == "Key;4 | Key;29",
                "keymap: ADD for desktop and VR gives the flipper and its followers the second key"
            );

            VpxKeyMap.Reset(s, "LeftFlipper", ControlsTarget.Everywhere);
            yield return (
                Get(own, "LeftFlipper") == ""
                    && Get(own, "LeftStagedFlipper") == ""
                    && Get(own, "UILeft") == "",
                "keymap: RESET for desktop and VR puts the flipper and its followers back to VPX's default"
            );

            // VR only, with the player's own key: the VR file empties it and notes VPX's default, so it doesn't come back.
            File.WriteAllText(own, "[Input]\nDevices = Key\nMapping.Start = Key;31\n");
            VpxKeyMap.Set(s, "Start", 5, ControlsTarget.VrOnly);
            VpxKeyMap.Reset(s, "Start", ControlsTarget.VrOnly);
            yield return (
                Get(vrIni, "Start") == ""
                    && VpxKeyMap
                        .EffectiveKeys(
                            VpxKeyMap.Value(VpxIniLayers.Effective(s, vr: true), "Start"),
                            30
                        )
                        .SequenceEqual([30])
                    && VpxKeyMap.Value(VpxIniLayers.Effective(s, vr: false), "Start") == "Key;31",
                "keymap: RESET for VR only gives VR play VPX's default key, and desktop keeps the player's"
            );
            VpxKeyMap.Set(s, "Start", 6, ControlsTarget.VrOnly);
            yield return (
                VpxKeyMap.Value(VpxIniLayers.Effective(s, vr: true), "Start") == "Key;6",
                "keymap: setting a key after a reset takes the default note away"
            );

            // RESET everywhere, with a VR follower that followed there but not in the player's file: VR play gets the
            // follower's default (noted, as the player's own key would come back), desktop keeps the player's.
            File.WriteAllText(
                own,
                "[Input]\nDevices = Key\nMapping.LeftFlipper = Key;4\nMapping.LeftStagedFlipper = Key;7\n"
            );
            File.WriteAllText(
                vrIni,
                "[Input]\nDevices = Key;OpenXR\nMapping.LeftStagedFlipper = Key;4\n"
            );
            VpxKeyMap.Reset(s, "LeftFlipper", ControlsTarget.Everywhere);
            var vrReset = VpxIniLayers.Effective(s, vr: true);
            var deskReset = VpxIniLayers.Effective(s, vr: false);
            yield return (
                Get(vrIni, "LeftStagedFlipper") == ""
                    && VpxKeyMap
                        .EffectiveKeys(VpxKeyMap.Value(vrReset, "LeftStagedFlipper"), 225)
                        .SequenceEqual([225])
                    && VpxKeyMap.Value(deskReset, "LeftStagedFlipper") == "Key;7"
                    && VpxKeyMap.Value(deskReset, "LeftFlipper") == "",
                "keymap: RESET everywhere resets a VR follower that followed there, and a desktop one on another key stays"
            );

            // ADD of a key the mode already uses (its VPX default, here) writes nothing.
            File.WriteAllText(own, "[Input]\nDevices = Key\nMapping.Start = Key;31\n");
            File.WriteAllText(vrIni, "[Input]\nDevices = Key;OpenXR\n");
            var vrBeforeAdd = File.ReadAllText(vrIni);
            var addStatus = VpxKeyMap.Add(s, "Start", 31, ControlsTarget.VrOnly);
            yield return (
                File.ReadAllText(vrIni) == vrBeforeAdd && addStatus.Contains("already"),
                "keymap: ADD of a key the action already has changes no file"
            );

            // RESET keeps a controller binding next to the default key, and the device list gets the keyboard.
            File.WriteAllText(
                own,
                "[Input]\nDevices = SDLJoy_x_1\nMapping.LeftFlipper = Key;4 | SDLJoy_x_1;3 | OpenXR;16;o;0.3\n"
            );
            File.Delete(vrIni);
            VpxKeyMap.Reset(s, "LeftFlipper", ControlsTarget.Everywhere);
            yield return (
                Get(own, "LeftFlipper") == "Key;225 | OpenXR;16;o;0.3"
                    && IniFile.Read(own).GetValueOrDefault((VpxInput.Section, "Devices"))
                        == "Key;SDLJoy_x_1",
                "keymap: RESET drops controller buttons, keeps the VR controller, and makes sure the keyboard is in the device list"
            );

            // Controller buttons, as VPX writes them.
            const string pad = "SDLJoy_03008fe54c050000c405000000016800_1";
            const string board = "SDLJoy_03008a6d091200004aea000000000000_1";
            yield return (
                VpxKeyMap.WithInput("", 225, $"{pad};4") == $"Key;225 | {pad};4"
                    && VpxKeyMap.WithInput(
                        $"Key;4 | {pad};1 | {board};2 | OpenXR;16",
                        225,
                        $"{pad};4"
                    ) == $"Key;4 | {pad};4 | {board};2 | OpenXR;16"
                    && VpxKeyMap.WithAddedInput($"Key;4 | {pad};1", 225, $"{pad};4")
                        == $"Key;4 | {pad};1 | {pad};4"
                    && VpxKeyMap.WithKey($"Key;4 | {pad};1", 29) == $"Key;29 | {pad};1",
                "keymap: a button replaces that controller's buttons only; a key keeps the buttons; the default key stays"
            );

            File.WriteAllText(
                own,
                "[Input]\nDevices = Key\nMapping.LeftFlipper = \nMapping.LeftStagedFlipper = \nMapping.UILeft = Key;80\n"
            );
            File.Delete(vrIni);
            VpxKeyMap.Set(
                s,
                "LeftFlipper",
                $"{pad.ToUpperInvariant()};4",
                ControlsTarget.Everywhere
            );
            yield return (
                Get(own, "LeftFlipper") == $"Key;225 | {pad};4"
                    && Get(own, "LeftStagedFlipper") == $"Key;225 | {pad};4"
                    && Get(own, "UILeft") == "Key;80"
                    && IniFile.Read(own).GetValueOrDefault((VpxInput.Section, "Devices"))
                        == $"Key;{pad}",
                "keymap: SET with a button keeps the default key, moves the staged flipper, and lists the controller"
            );
            yield return (
                VpxKeyMap
                    .SharedWith(VpxIniLayers.Effective(s, vr: false), "Start", $"{pad};4")
                    .Contains("Left flipper"),
                "keymap: a button another action uses is reported"
            );
            var addAgain = VpxKeyMap.Add(s, "LeftFlipper", $"{pad};4", ControlsTarget.Everywhere);
            yield return (
                addAgain.Contains("already") && Get(own, "LeftFlipper") == $"Key;225 | {pad};4",
                "keymap: ADD of a button the action has changes nothing"
            );
            VpxKeyMap.Reset(s, "LeftFlipper", ControlsTarget.Everywhere);
            yield return (
                Get(own, "LeftFlipper") == "" && Get(own, "LeftStagedFlipper") == "",
                "keymap: RESET takes the button off the flipper and the staged flipper"
            );

            // A button VPX's menu gave only the flipper doesn't stop a new key moving the staged flipper too.
            File.WriteAllText(
                own,
                $"[Input]\nDevices = Key;{pad}\nMapping.LeftFlipper = Key;225 | {pad};4\nMapping.LeftStagedFlipper = Key;225\n"
            );
            VpxKeyMap.Set(s, "LeftFlipper", 29, ControlsTarget.Everywhere);
            yield return (
                Get(own, "LeftFlipper") == $"Key;29 | {pad};4"
                    && Get(own, "LeftStagedFlipper") == "Key;29",
                "keymap: a key moves a follower with the same keys, whatever buttons the flipper has"
            );

            // ADD of a button the action has, from a controller VPX's device list leaves out: the list gets it.
            File.WriteAllText(own, $"[Input]\nDevices = Key\nMapping.Start = Key;30 | {pad};9\n");
            var addListed = VpxKeyMap.Add(s, "Start", $"{pad};9", ControlsTarget.Everywhere);
            yield return (
                !addListed.Contains("already")
                    && IniFile.Read(own).GetValueOrDefault((VpxInput.Section, "Devices"))
                        == $"Key;{pad}",
                "keymap: ADD of a button from an unlisted controller adds the controller to the device list"
            );

            // No VPX installed yet: controllers can't be read, and it says so (the keyboard still works).
            var noVpx = JoystickCapture.TryOpen(
                new AppSettings { VpxDir = Path.Combine(dir, "no vpx") },
                out var why
            );
            yield return (
                noVpx == null && why?.Contains("VPX installed first") == true,
                "keymap: controller buttons without VPX say it's needed, without failing"
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("VRTM_VPX_PREFS", oldPrefs);
        }
    }
}
