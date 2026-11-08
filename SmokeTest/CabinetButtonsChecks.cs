using VrTableManager.Lobby;

/// <summary>Cabinet buttons in the VR lobby, read from the player's VPX controls (CabinetButtons).</summary>
static class CabinetButtonsChecks
{
    static Dictionary<(string, string), string> Ini(
        params (string Section, string Key, string Value)[] values
    ) => values.ToDictionary(v => (v.Section, v.Key), v => v.Value);

    public static IEnumerable<(bool, string)> Run()
    {
        // Nothing set: VPX's default keys.
        var d = CabinetButtons.From(Ini(("Input", "Mapping.LeftFlipper", "")));
        yield return (
            d.ForKey(CabinetButtons.Sdl.LShift) == CabinetAction.PrevTable
                && d.ForKey(CabinetButtons.Sdl.RShift) == CabinetAction.NextTable
                && d.ForKey(CabinetButtons.Sdl.LCtrl) == CabinetAction.PrevLetter
                && d.ForKey(CabinetButtons.Sdl.RCtrl) == CabinetAction.NextLetter
                && d.ForKey(CabinetButtons.Sdl.Digit1) == CabinetAction.Play
                && d.ForKey(CabinetButtons.Sdl.Enter) == CabinetAction.Play
                && d.ForKey(CabinetButtons.Sdl.Digit5) == CabinetAction.Filter
                && d.ForKey(CabinetButtons.Sdl.Q) == CabinetAction.Leave
                && d.JoyButtons.Count == 0,
            "cabinet buttons: VPX's default keys when nothing is set"
        );

        // VPX 10.8.1: keys and joystick buttons, VR controller and combination bindings left out.
        var n = CabinetButtons.From(
            Ini(
                ("Input", "Mapping.LeftFlipper", "Key;4 | SDL.PinOne;0 | OpenXR;16;o;0.300000"),
                ("Input", "Mapping.RightFlipper", "SDL.PinOne;1"),
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
            "cabinet buttons: 10.8.1 key and joystick mappings replace the defaults"
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
        yield return (
            CabinetButtons.Sdl.FromDirectInput(0x10) == 4 + ('Q' - 'A')
                && CabinetButtons.Sdl.FromDirectInput(0x02) == CabinetButtons.Sdl.Digit1
                && CabinetButtons.Sdl.FromDirectInput(0x9D) == CabinetButtons.Sdl.RCtrl,
            "cabinet buttons: DirectInput codes convert to SDL"
        );
    }
}
