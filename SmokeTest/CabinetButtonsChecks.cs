using VrTableManager.Lobby;

/// <summary>Cabinet buttons in the VR lobby, read from the player's VPX controls (CabinetButtons).</summary>
static class CabinetButtonsChecks
{
    static Dictionary<(string, string), string> Ini(
        params (string Section, string Key, string Value)[] values
    ) => values.ToDictionary(v => (v.Section, v.Key), v => v.Value);

    public static IEnumerable<(bool, string)> Run()
    {
        // Nothing set: VPX's default keys (10.8.1 writes [Input] Devices even then; Exit Game is Escape there).
        var d = CabinetButtons.From(
            Ini(("Input", "Devices", "Key"), ("Input", "Mapping.LeftFlipper", ""))
        );
        yield return (
            d.ForKey(CabinetButtons.Sdl.LShift) == CabinetAction.PrevTable
                && d.ForKey(CabinetButtons.Sdl.RShift) == CabinetAction.NextTable
                && d.ForKey(CabinetButtons.Sdl.LCtrl) == CabinetAction.PrevLetter
                && d.ForKey(CabinetButtons.Sdl.RCtrl) == CabinetAction.NextLetter
                && d.ForKey(CabinetButtons.Sdl.Digit1) == CabinetAction.Play
                && d.ForKey(CabinetButtons.Sdl.Enter) == CabinetAction.Play
                && d.ForKey(CabinetButtons.Sdl.Digit5) == CabinetAction.Filter
                && d.ForKey(CabinetButtons.Sdl.Escape) == CabinetAction.Leave
                && d.ForKey(CabinetButtons.Sdl.Keypad5) == CabinetAction.Recenter
                && d.ForKey(CabinetButtons.Sdl.Q) == CabinetAction.None
                && d.JoyButtons.Count == 0,
            "cabinet buttons: VPX 10.8.1's default keys when nothing is set"
        );
        yield return (
            CabinetButtons.From(Ini(("Player", "LFlipKey", ""))).ForKey(CabinetButtons.Sdl.Q)
                == CabinetAction.Leave,
            "cabinet buttons: older VPX's Exit Game default is Q"
        );

        // VPX 10.8.1, as it writes them: a Pinscape board's buttons (SDLJoy_<GUID>_<n>;button), a d-pad direction,
        // an axis, the VR controllers and a combination; only keys, buttons and the d-pad count.
        const string pinscape = "SDLJoy_03008a6d091200004aea000000000000_1";
        const string guid = "03008a6d091200004aea000000000000";
        var n = CabinetButtons.From(
            Ini(
                ("Input", "Devices", $"Key;{pinscape}"),
                ("Input", "Mapping.LeftFlipper", $"Key;4 | {pinscape};0 | OpenXR;16;o;0.300000"),
                ("Input", "Mapping.RightFlipper", $"{pinscape};1"),
                ("Input", "Mapping.LeftMagna", $"{pinscape};259"), // hat 0 down
                ("Input", "Mapping.RightMagna", $"{pinscape};513;o;0.5"), // axis 1
                ("Input", "Mapping.Start", "Key;30 & Key;225")
            )
        );
        yield return (
            n.ForKey(4) == CabinetAction.PrevTable
                && n.ForKey(CabinetButtons.Sdl.LShift) == CabinetAction.None
                && n.ForJoyButton(0) == CabinetAction.PrevTable
                && n.ForJoyButton(1) == CabinetAction.NextTable
                && n.ForJoyButton(16) == CabinetAction.None
                && n.ForKey(CabinetButtons.Sdl.RShift) == CabinetAction.None,
            "cabinet buttons: 10.8.1 key and SDLJoy joystick mappings replace the defaults"
        );
        yield return (
            n.ForJoyButton(guid.ToUpperInvariant(), 1) == CabinetAction.NextTable
                && n.ForJoyButton("030000005e0400008e02000000000000", 1) == CabinetAction.None
                && n.JoyDevices.SetEquals([guid]),
            "cabinet buttons: a joystick binding belongs to the board VPX names (by SDL GUID)"
        );
        yield return (
            n.ForJoyButton(12) == CabinetAction.PrevLetter
                && !n.JoyButtons.Values.Contains(CabinetAction.NextLetter),
            "cabinet buttons: a d-pad direction is Godot's d-pad button; an axis isn't a button"
        );
        yield return (
            n.ForKey(30) == CabinetAction.None
                && n.ForKey(CabinetButtons.Sdl.Enter) == CabinetAction.Play,
            "cabinet buttons: a key combination isn't a lobby button"
        );

        // Older VPX: DirectInput keys and joystick buttons from 1.
        var o = CabinetButtons.From(
            Ini(
                ("Player", "LFlipKey", "44"), // DIK_Z
                ("Player", "RFlipKey", ""),
                ("Player", "JoyLFlipKey", "3"),
                ("Player", "JoyStartGameKey", "0"),
                ("Player", "StartGameKey", "28") // DIK_RETURN
            )
        );
        yield return (
            o.ForKey(4 + ('Z' - 'A')) == CabinetAction.PrevTable
                && o.ForKey(CabinetButtons.Sdl.RShift) == CabinetAction.NextTable
                && o.ForJoyButton(2) == CabinetAction.PrevTable
                && o.JoyButtons.Count == 1
                && o.ForKey(CabinetButtons.Sdl.Enter) == CabinetAction.Play,
            "cabinet buttons: older VPX keys and joystick buttons"
        );
        // Recentering follows VPX's own control: 10.8.1's VRCenter ("Align VR view"), or older VPX's
        // TableRecenterKey / JoyTableRecenterKey; keypad 5 when nothing is set.
        var r = CabinetButtons.From(
            Ini(
                ("Input", "Devices", $"Key;{pinscape}"),
                ("Input", "Mapping.VRCenter", $"Key;10 | {pinscape};7")
            )
        );
        yield return (
            r.ForKey(10) == CabinetAction.Recenter
                && r.ForJoyButton(guid, 7) == CabinetAction.Recenter
                && r.ForKey(CabinetButtons.Sdl.Keypad5) == CabinetAction.None,
            "cabinet buttons: VPX 10.8.1's VRCenter mapping recenters"
        );
        var ro = CabinetButtons.From(
            Ini(("Player", "TableRecenterKey", "34"), ("Player", "JoyTableRecenterKey", "9")) // DIK_G
        );
        var rd = CabinetButtons.From(Ini(("Player", "TableRecenterKey", "")));
        yield return (
            ro.ForKey(4 + ('G' - 'A')) == CabinetAction.Recenter
                && ro.ForJoyButton(8) == CabinetAction.Recenter
                && rd.ForKey(CabinetButtons.Sdl.Keypad5) == CabinetAction.Recenter,
            "cabinet buttons: older VPX's TableRecenterKey recenters (keypad 5 by default)"
        );
        yield return (
            CabinetButtons.ForGamepadButton(0) == CabinetAction.Play // A / Cross
                && CabinetButtons.ForGamepadButton(9) == CabinetAction.PrevTable // L1
                && CabinetButtons.ForGamepadButton(14) == CabinetAction.NextTable // d-pad right
                && CabinetButtons.ForGamepadButton(4) == CabinetAction.Leave // Back / Share
                && CabinetButtons.ForGamepadButton(5) == CabinetAction.None, // Guide / PS button
            "cabinet buttons: game controllers have their own layout"
        );
        yield return (
            CabinetButtons.Sdl.FromDirectInput(0x10) == 4 + ('Q' - 'A')
                && CabinetButtons.Sdl.FromDirectInput(0x02) == CabinetButtons.Sdl.Digit1
                && CabinetButtons.Sdl.FromDirectInput(0x9D) == CabinetButtons.Sdl.RCtrl,
            "cabinet buttons: DirectInput codes convert to SDL"
        );
    }
}
