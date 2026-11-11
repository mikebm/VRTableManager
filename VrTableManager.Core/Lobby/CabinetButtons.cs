using VrTableManager.Services;

namespace VrTableManager.Lobby;

/// <summary>What a cabinet button does in the lobby.</summary>
public enum CabinetAction
{
    None,
    PrevTable,
    NextTable,
    PrevLetter,
    NextLetter,
    Play,
    Filter,
    Random,
    Leave,
    Favourite,

    /// <summary>VPX's "Align VR view" (keypad 5): face the selected cabinet from where you stand.</summary>
    Recenter,
}

/// <summary>
/// Cabinet buttons (flippers, magna-saves, Start, Launch…) in the lobby, for players on a cabinet controller
/// (PinOne, Pinscape, Zeb's, …) rather than the Quest controllers. The buttons are whatever the player set up in
/// VPX, read from the settings VR play uses (<see cref="VpxIniLayers.Effective"/>), so a board that works in VPX
/// works here with nothing to set up:
/// <list type="bullet">
/// <item>VPX 10.8.1+: <c>[Input] Mapping.*</c>, e.g. <c>Key;225 | SDLJoy_0300f…_1;4</c>. Keys are SDL scancodes;
/// a joystick is <c>SDLJoy_&lt;SDL GUID&gt;_&lt;n-th of that GUID&gt;</c> and its buttons SDL's raw joystick
/// button numbers (from 0), d-pad (hat) directions <c>0x100 + hat × 4 + direction</c>, axes <c>0x200 + axis</c>.
/// Empty means VPX's default key.</item>
/// <item>Older VPX: <c>[Player] LFlipKey</c> etc. (DirectInput scancodes, empty = VPX's default key) and
/// <c>JoyLFlipKey</c> etc. (buttons from 1).</item>
/// </list>
/// Keys are kept as SDL scancodes; joystick buttons from 0, as Godot numbers a joystick it doesn't know as a game
/// controller (its d-pad as Godot's d-pad buttons, 11–14).
/// </summary>
public sealed class CabinetButtons
{
    /// <summary>
    /// The VPX control behind each lobby action: its 10.8.1 mapping, its older key / joystick setting, and VPX's
    /// default key (SDL) in 10.8.1 and before it.
    /// </summary>
    static readonly (
        CabinetAction Action,
        string Mapping,
        string? LegacyKey,
        string? LegacyJoy,
        int DefaultKey,
        int LegacyDefaultKey
    )[] Controls =
    [
        (CabinetAction.PrevTable, "LeftFlipper", "LFlipKey", "JoyLFlipKey", Sdl.LShift, Sdl.LShift),
        (
            CabinetAction.NextTable,
            "RightFlipper",
            "RFlipKey",
            "JoyRFlipKey",
            Sdl.RShift,
            Sdl.RShift
        ),
        (
            CabinetAction.PrevLetter,
            "LeftMagna",
            "LMagnaSave",
            "JoyLMagnaSave",
            Sdl.LCtrl,
            Sdl.LCtrl
        ),
        (
            CabinetAction.NextLetter,
            "RightMagna",
            "RMagnaSave",
            "JoyRMagnaSave",
            Sdl.RCtrl,
            Sdl.RCtrl
        ),
        (CabinetAction.Play, "Start", "StartGameKey", "JoyStartGameKey", Sdl.Digit1, Sdl.Digit1),
        (CabinetAction.Play, "LaunchBall", "PlungerKey", "JoyPlungerKey", Sdl.Enter, Sdl.Enter),
        (CabinetAction.Play, "Lockbar", "LockbarKey", "JoyLockbarKey", Sdl.LAlt, Sdl.LAlt),
        (
            CabinetAction.Filter,
            "Credit1",
            "AddCreditKey",
            "JoyAddCreditKey",
            Sdl.Digit5,
            Sdl.Digit5
        ),
        (CabinetAction.Random, "ExtraBall", null, null, Sdl.B, Sdl.B),
        (CabinetAction.Leave, "ExitGame", "ExitGameKey", "JoyExitGameKey", Sdl.Escape, Sdl.Q),
        // 10.8.1 calls it "Align VR view"; older VPX "TableRecenterKey". Keypad 5 in both.
        (
            CabinetAction.Recenter,
            "VRCenter",
            "TableRecenterKey",
            "JoyTableRecenterKey",
            Sdl.Keypad5,
            Sdl.Keypad5
        ),
    ];

    /// <summary>Godot's d-pad buttons, which a joystick's first hat arrives as.</summary>
    static readonly int[] GodotDpad = [13, 14, 11, 12]; // VPX's hat directions: left, right, up, down

    readonly Dictionary<int, CabinetAction> _keys = [];
    readonly Dictionary<int, CabinetAction> _joy = [];
    readonly Dictionary<(string Guid, int Button), CabinetAction> _joyByDevice = [];

    public static readonly CabinetButtons None = new();

    public IReadOnlyDictionary<int, CabinetAction> Keys => _keys;

    /// <summary>Every joystick binding, whichever joystick it's on.</summary>
    public IReadOnlyDictionary<int, CabinetAction> JoyButtons => _joy;

    /// <summary>The SDL GUIDs (lower case) of the joysticks VPX 10.8.1 has bindings for.</summary>
    public IReadOnlySet<string> JoyDevices => _joyByDevice.Keys.Select(k => k.Guid).ToHashSet();

    /// <summary>The action for a key (SDL scancode), or <see cref="CabinetAction.None"/>.</summary>
    public CabinetAction ForKey(int sdlScancode) => _keys.GetValueOrDefault(sdlScancode);

    /// <summary>The action for a button (from 0) on any joystick, or <see cref="CabinetAction.None"/>.</summary>
    public CabinetAction ForJoyButton(int button) => _joy.GetValueOrDefault(button);

    /// <summary>The action for a button (from 0) on the joystick with this SDL GUID, or <see cref="CabinetAction.None"/>.</summary>
    public CabinetAction ForJoyButton(string guid, int button) =>
        _joyByDevice.GetValueOrDefault((guid.ToLowerInvariant(), button));

    /// <summary>
    /// A game controller (Xbox, PlayStation, Switch…) in the lobby, whatever VPX has: Godot numbers its buttons in
    /// SDL's game controller layout, not the raw joystick numbers VPX maps, and most players never map one in VPX.
    /// Face buttons follow the Quest controllers (A play, B filter, Y random); the shoulders are the flippers.
    /// PlayStation: Cross = A, Circle = B, Square = X, Triangle = Y, Options = Start, Share = Back.
    /// </summary>
    public static CabinetAction ForGamepadButton(int button) =>
        button switch
        {
            0 or 6 => CabinetAction.Play, // A, Start
            1 => CabinetAction.Filter, // B
            2 => CabinetAction.Favourite, // X
            3 => CabinetAction.Random, // Y
            4 => CabinetAction.Leave, // Back (held)
            9 or 13 => CabinetAction.PrevTable, // left shoulder, d-pad left
            10 or 14 => CabinetAction.NextTable, // right shoulder, d-pad right
            11 => CabinetAction.PrevLetter, // d-pad up
            12 => CabinetAction.NextLetter, // d-pad down
            _ => CabinetAction.None,
        };

    /// <summary>The cabinet controls VR play would use with these settings.</summary>
    public static CabinetButtons Load(AppSettings s) => From(VpxIniLayers.Effective(s, vr: true));

    public static CabinetButtons From(IReadOnlyDictionary<(string Section, string Key), string> ini)
    {
        var b = new CabinetButtons();
        // 10.8.1 writes its [Input] section (Devices = Key…) even when nothing is remapped; older VPX never does.
        var v1081 = ini.Keys.Any(k =>
            k.Section.Equals(VpxInput.Section, StringComparison.OrdinalIgnoreCase)
            && k.Key.Equals("Devices", StringComparison.OrdinalIgnoreCase)
        );
        foreach (var (action, mapping, legacyKey, legacyJoy, defaultKey, legacyDefault) in Controls)
        {
            var mapped = ini.GetValueOrDefault((VpxInput.Section, "Mapping." + mapping)) ?? "";
            if (mapped.Trim() != "")
            {
                foreach (var alternative in mapped.Split('|'))
                {
                    b.AddBinding(action, alternative);
                }

                continue;
            }

            if (v1081)
            {
                b._keys.TryAdd(defaultKey, action);
                continue;
            }

            // Older VPX: a DirectInput key, and a joystick button (from 1; 0 = none).
            var key =
                legacyKey != null
                && int.TryParse(ini.GetValueOrDefault(("Player", legacyKey)), out var dik)
                && Sdl.FromDirectInput(dik) is { } sdl
                    ? sdl
                    : legacyDefault;
            b._keys.TryAdd(key, action);
            if (
                legacyJoy != null
                && int.TryParse(ini.GetValueOrDefault(("Player", legacyJoy)), out var joy)
                && joy > 0
            )
            {
                b._joy.TryAdd(joy - 1, action);
            }
        }
        return b;
    }

    /// <summary>
    /// One 10.8.1 binding: <c>Key;225</c> or <c>SDLJoy_&lt;guid&gt;_1;4</c>. Combinations (<c>&amp;</c>), axes,
    /// the mouse and the VR controllers are left out.
    /// </summary>
    void AddBinding(CabinetAction action, string binding)
    {
        if (binding.Contains('&'))
        {
            return;
        }

        var parts = binding.Split(';').Select(p => p.Trim()).ToArray();
        // A third part is an axis's direction and threshold.
        if (parts.Length != 2 || !int.TryParse(parts[1], out var id) || id < 0)
        {
            return;
        }

        if (parts[0].Equals("Key", StringComparison.OrdinalIgnoreCase))
        {
            _keys.TryAdd(id, action);
            return;
        }

        if (!parts[0].StartsWith("SDLJoy_", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int button;
        if (id < 0x100)
        {
            button = id;
        }
        else if (id < 0x104)
        {
            button = GodotDpad[id - 0x100]; // the first hat; Godot has no others
        }
        else
        {
            return;
        }

        _joy.TryAdd(button, action);
        // SDLJoy_<GUID>_<n>: the GUID picks out the board; n tells identical boards apart, which Godot can't.
        var device = parts[0]["SDLJoy_".Length..];
        var cut = device.LastIndexOf('_');
        if (cut > 0)
        {
            _joyByDevice.TryAdd((device[..cut].ToLowerInvariant(), button), action);
        }
    }

    /// <summary>"PrevTable: key 225, button 3; …", for the lobby's log.</summary>
    public string Describe() =>
        string.Join(
            "; ",
            Enum.GetValues<CabinetAction>()
                .Where(a => a != CabinetAction.None)
                .Select(a =>
                    $"{a}: "
                    + string.Join(
                        ", ",
                        _keys
                            .Where(kv => kv.Value == a)
                            .Select(kv => $"key {kv.Key}")
                            .Concat(
                                _joy.Where(kv => kv.Value == a).Select(kv => $"button {kv.Key}")
                            )
                    )
                )
        ) + (_joyByDevice.Count > 0 ? "; joysticks: " + string.Join(", ", JoyDevices) : "");

    /// <summary>SDL scancodes (what VPX 10.8.1 stores for keys), and the DirectInput ones older VPX used.</summary>
    public static class Sdl
    {
        public const int B = 5;
        public const int Q = 20;
        public const int Escape = 41;
        public const int Digit1 = 30;
        public const int Digit5 = 34;
        public const int Enter = 40;
        public const int LCtrl = 224;
        public const int LShift = 225;
        public const int LAlt = 226;
        public const int RCtrl = 228;
        public const int RShift = 229;
        public const int Keypad5 = 93;

        /// <summary>True for A–Z and 0–9, the keys the desktop lobby uses to jump to a letter.</summary>
        public static bool IsLetterOrDigit(int sdl) => sdl is >= 4 and <= 39;

        static readonly Dictionary<int, int> FromDik = BuildDik();

        public static int? FromDirectInput(int dik) =>
            FromDik.TryGetValue(dik, out var s) ? s : null;

        static Dictionary<int, int> BuildDik()
        {
            var d = new Dictionary<int, int>();
            // Letters by their DirectInput row positions.
            void Row(int firstDik, string letters)
            {
                for (var i = 0; i < letters.Length; i++)
                {
                    d[firstDik + i] = 4 + (letters[i] - 'A');
                }
            }
            Row(0x10, "QWERTYUIOP");
            Row(0x1E, "ASDFGHJKL");
            Row(0x2C, "ZXCVBNM");
            for (var i = 0; i < 9; i++)
            {
                d[0x02 + i] = 30 + i; // 1–9
            }
            d[0x0B] = 39; // 0
            for (var i = 0; i < 10; i++)
            {
                d[0x3B + i] = 58 + i; // F1–F10
            }
            d[0x57] = 68; // F11
            d[0x58] = 69; // F12
            foreach (
                var (dik, sdl) in new[]
                {
                    (0x01, 41), // Esc
                    (0x0C, 45), // -
                    (0x0D, 46), // =
                    (0x0E, 42), // Backspace
                    (0x0F, 43), // Tab
                    (0x1A, 47), // [
                    (0x1B, 48), // ]
                    (0x1C, Enter),
                    (0x1D, LCtrl),
                    (0x27, 51), // ;
                    (0x28, 52), // '
                    (0x29, 53), // `
                    (0x2A, LShift),
                    (0x2B, 49), // \
                    (0x33, 54), // ,
                    (0x34, 55), // .
                    (0x35, 56), // /
                    (0x36, RShift),
                    (0x37, 85), // keypad *
                    (0x38, LAlt),
                    (0x39, 44), // Space
                    (0x3A, 57), // Caps Lock
                    (0x47, 95), // keypad 7
                    (0x48, 96),
                    (0x49, 97),
                    (0x4A, 86), // keypad -
                    (0x4B, 92), // keypad 4
                    (0x4C, 93),
                    (0x4D, 94),
                    (0x4E, 87), // keypad +
                    (0x4F, 89), // keypad 1
                    (0x50, 90),
                    (0x51, 91),
                    (0x52, 98), // keypad 0
                    (0x53, 99), // keypad .
                    (0x9C, 88), // keypad Enter
                    (0x9D, RCtrl),
                    (0xB5, 84), // keypad /
                    (0xB8, 230), // Right Alt
                    (0xC7, 74), // Home
                    (0xC8, 82), // Up
                    (0xC9, 75), // Page Up
                    (0xCB, 80), // Left
                    (0xCD, 79), // Right
                    (0xCF, 77), // End
                    (0xD0, 81), // Down
                    (0xD1, 78), // Page Down
                    (0xD2, 73), // Insert
                    (0xD3, 76), // Delete
                    (0xDB, 227), // Left Windows
                    (0xDC, 231), // Right Windows
                }
            )
            {
                d[dik] = sdl;
            }
            return d;
        }
    }
}
