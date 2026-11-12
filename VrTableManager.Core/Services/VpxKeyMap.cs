using System.Text.RegularExpressions;

namespace VrTableManager.Services;

/// <summary>Which settings a key set on the Setup tab goes into (<see cref="VpxKeyMap.Set"/>).</summary>
public enum ControlsTarget
{
    /// <summary>The player's own VPinballX.ini, so desktop and VR play (and VPX started on its own) all use it.</summary>
    Everywhere,

    /// <summary>The VR overrides only (<see cref="VpxSetup.VrIniPath"/>).</summary>
    VrOnly,

    /// <summary>The desktop overrides only (<see cref="VpxSetup.DesktopIniPath"/>).</summary>
    DesktopOnly,
}

/// <summary>
/// Keyboard mappings for VPX 10.8.1+ (<c>[Input] Mapping.*</c>), set from the Setup tab rather than VPX's in-game menu.
/// A mapping is alternatives joined by <c> | </c> (<c>Key;225 | OpenXR;16;o;0.3 | SDLJoy_…;4</c>); setting a key
/// replaces only the single-key alternatives, so VR controller, gamepad and cabinet-board bindings and key
/// combinations (<c>&amp;</c>) stay. An empty mapping means VPX's default key.
/// </summary>
public static class VpxKeyMap
{
    /// <summary>One VPX action: its <c>Mapping.</c> name, a label, the group it's listed in, and VPX's default key (SDL).</summary>
    public sealed record Control(string Mapping, string Label, string Group, int DefaultKey);

    /// <summary>The actions listed on the Setup tab, in order. VPX's default keys are those VPX 10.8.1 writes.</summary>
    public static readonly IReadOnlyList<Control> Controls =
    [
        new("LeftFlipper", "Left flipper", "Flippers & buttons", 225),
        new("RightFlipper", "Right flipper", "Flippers & buttons", 229),
        new("LeftStagedFlipper", "Left staged flipper", "Flippers & buttons", 225),
        new("RightStagedFlipper", "Right staged flipper", "Flippers & buttons", 229),
        new("LeftMagna", "Left magna-save", "Flippers & buttons", 224),
        new("RightMagna", "Right magna-save", "Flippers & buttons", 228),
        new("LaunchBall", "Launch ball (plunger)", "Flippers & buttons", 40),
        new("Lockbar", "Lockbar / fire button", "Flippers & buttons", 226),
        new("Start", "Start", "Flippers & buttons", 30),
        new("Credit1", "Coin 1", "Flippers & buttons", 34),
        new("Credit2", "Coin 2", "Flippers & buttons", 33),
        new("ExtraBall", "Extra ball / buy-in", "Flippers & buttons", 5),
        new("LeftNudge", "Nudge left", "Nudge & tilt", 29),
        new("RightNudge", "Nudge right", "Nudge & tilt", 56),
        new("CenterNudge", "Nudge forward", "Nudge & tilt", 44),
        new("Tilt", "Tilt bob", "Nudge & tilt", 23),
        new("SlamTilt", "Slam tilt", "Nudge & tilt", 74),
        new("ExitGame", "Exit table", "Game", 41),
        new("InGameUI", "In-game menu", "Game", 69),
        new("Pause", "Pause", "Game", 19),
        new("VolumeDown", "Volume down", "Game", 45),
        new("VolumeUp", "Volume up", "Game", 46),
        new("CoinDoor", "Coin door", "Game", 77),
        new("Reset", "Reset", "Game", 60),
        new("VRCenter", "Align VR view", "VR", 93),
    ];

    /// <summary>
    /// Mappings that share a key with another by default and follow it when it's remapped, as long as they still have
    /// the same keys: the staged flippers, and the in-game menu's navigation (flippers left/right, magna-saves up/down).
    /// </summary>
    static readonly Dictionary<string, (string Mapping, int DefaultKey)[]> Followers = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["LeftFlipper"] = [("LeftStagedFlipper", 225), ("UILeft", 225)],
        ["RightFlipper"] = [("RightStagedFlipper", 229), ("UIRight", 229)],
        ["LeftMagna"] = [("UIUp", 224)],
        ["RightMagna"] = [("UIDown", 228)],
    };

    /// <summary>
    /// Whether this VPX keeps its controls in the 10.8.1 format: its settings already have the <c>[Input]</c> section,
    /// or the installed build is 10.8.1 or later. Older builds use <c>[Player] LFlipKey</c> etc., which this doesn't write.
    /// </summary>
    public static bool IsSupported(AppSettings s)
    {
        if (VpxIniLayers.Effective(s, vr: false).ContainsKey((VpxInput.Section, "Devices")))
        {
            return true;
        }

        return VpxSetup.InstalledVersion(s) is { } tag
            && Regex.Match(tag, @"(\d+)\.(\d+)\.(\d+)") is { Success: true } m
            && new Version(
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value)
            ) >= new Version(10, 8, 1);
    }

    /// <summary>The keys (SDL scancodes) of a mapping's single-key alternatives, in order.</summary>
    public static List<int> KeysIn(string? mapping) =>
        Alternatives(mapping)
            .Select(KeyOf)
            .Where(k => k is not null)
            .Select(k => k!.Value)
            .ToList();

    /// <summary>The keys that work for a mapping: its own, or VPX's default when it's empty.</summary>
    public static List<int> EffectiveKeys(string? mapping, int defaultKey) =>
        string.IsNullOrWhiteSpace(mapping) ? [defaultKey] : KeysIn(mapping);

    /// <summary>
    /// The mapping with <paramref name="sdl"/> as its only key. The other alternatives (VR controllers, gamepads,
    /// cabinet boards, key combinations) are kept after it.
    /// </summary>
    public static string WithKey(string? mapping, int sdl) =>
        string.Join(
            " | ",
            new[] { $"Key;{sdl}" }.Concat(Alternatives(mapping).Where(a => KeyOf(a) is null))
        );

    /// <summary>"Left Shift", "Left Shift (default)", "Z + VR controller", "Left Alt+1"… for the Setup tab.</summary>
    public static string Describe(string? mapping, int defaultKey)
    {
        if (string.IsNullOrWhiteSpace(mapping))
        {
            return KeyName(defaultKey) + " (default)";
        }

        var keys = new List<string>();
        bool vrController = false,
            controller = false;
        foreach (var alternative in Alternatives(mapping))
        {
            // A combination ("Key;226 & Key;30") is pressed together: "Left Alt+1".
            var parts = alternative.Split('&').Select(p => p.Trim()).ToList();
            var partKeys = parts.Select(KeyOf).ToList();
            if (partKeys.All(k => k is not null))
            {
                keys.Add(string.Join("+", partKeys.Select(k => KeyName(k!.Value))));
                continue;
            }

            foreach (var device in parts.Select(DeviceOf))
            {
                if (device.Equals("OpenXR", StringComparison.OrdinalIgnoreCase))
                {
                    vrController = true;
                }
                else if (device.StartsWith("SDL", StringComparison.OrdinalIgnoreCase))
                {
                    controller = true;
                }
            }
        }

        var extras = new List<string>();
        if (vrController)
        {
            extras.Add("VR controller");
        }

        if (controller)
        {
            extras.Add("controller button");
        }

        var text = keys.Count > 0 ? string.Join(" / ", keys) : "No key";
        return extras.Count > 0 ? $"{text} + {string.Join(" + ", extras)}" : text;
    }

    /// <summary>
    /// Sets the key for one action in the settings <paramref name="target"/> names, and moves the mappings that follow
    /// it (<see cref="Followers"/>) along when they had the same keys. Every file is worked out before any is written,
    /// and if one can't be written the ones already written are put back. Returns a line for the Setup tab.
    /// </summary>
    /// <exception cref="InvalidOperationException">There's no VPinballX.ini of the player's to write into.</exception>
    public static string Set(AppSettings s, string mapping, int sdl, ControlsTarget target)
    {
        var control = Controls.First(c =>
            c.Mapping.Equals(mapping, StringComparison.OrdinalIgnoreCase)
        );
        var writes = new List<(string File, List<(string Key, string Value)> Changes)>();
        string done;
        if (target is ControlsTarget.VrOnly or ControlsTarget.DesktopOnly)
        {
            // The mode's whole mapping (the player's own, or the override's) goes into the override.
            var vr = target == ControlsTarget.VrOnly;
            var effective = VpxIniLayers.Effective(s, vr);
            string Mode(string m) => Value(effective, m);
            var changes = Changes(Mode, control, sdl);
            if (WithKeyboard(Devices(effective)) is { } devices)
            {
                changes.Add(("Devices", devices));
            }

            writes.Add((VpxIniLayers.OverridePath(s, vr), changes));
            done = $"✓ {control.Label} is {KeyName(sdl)} in {(vr ? "VR" : "desktop")} play.";
        }
        else
        {
            if (VpxIniLayers.BasePath(s) is not { } own)
            {
                throw new InvalidOperationException(
                    "VPX hasn't saved settings of its own yet. Start VPinballX.exe on its own once (not from here) and it creates VPinballX.ini, or save to VR or desktop only."
                );
            }

            var ownValues = IniFile.Read(own);
            string Own(string m) => Value(ownValues, m);
            var ownChanges = Changes(Own, control, sdl);
            var ownDevices = WithKeyboard(Devices(ownValues));
            if (ownDevices != null)
            {
                ownChanges.Add(("Devices", ownDevices));
            }

            writes.Add((own, ownChanges));
            foreach (var vr in new[] { true, false })
            {
                writes.Add(
                    (
                        VpxIniLayers.OverridePath(s, vr),
                        ModeChanges(s, vr, ownValues, ownChanges, ownDevices, control, sdl)
                    )
                );
            }
            done = $"✓ {control.Label} is {KeyName(sdl)} in desktop and VR play (saved in {own}).";
        }

        WriteAll(writes);
        return done;
    }

    /// <summary>
    /// "Desktop and VR": what one mode's override needs so that, with the player's own file changed, the mode ends up
    /// as if the key were set in it: the leader gets the key even where the override has its own mapping, followers
    /// that followed in this mode (by what it used before) get it too, and followers that didn't keep what they had.
    /// </summary>
    static List<(string Key, string Value)> ModeChanges(
        AppSettings s,
        bool vr,
        Dictionary<(string Section, string Key), string> ownValues,
        List<(string Key, string Value)> ownChanges,
        string? ownDevicesFixed,
        Control control,
        int sdl
    )
    {
        var values = IniFile.Read(VpxIniLayers.OverridePath(s, vr));
        // Put back to VPX's default in this mode during play: the mode uses the default, not the player's file.
        var setToDefault = VpxIniLayers.SetToDefault(VpxIniLayers.OverridePath(s, vr));
        bool Pinned(string m) => setToDefault.Contains((VpxInput.Section, "Mapping." + m));
        var before = VpxIniLayers.Effective(s, vr);
        var changes = new List<(string Key, string Value)>();
        var leader = Value(values, control.Mapping);
        if (leader != "" || Pinned(control.Mapping))
        {
            changes.Add(("Mapping." + control.Mapping, WithKey(leader, sdl)));
        }

        // What the mode uses once both files are written: the override's value (empty: VPX's default, when it was
        // put back to it), else the player's new or old one.
        string After(string m)
        {
            var key = "Mapping." + m;
            var own = Value(values, m);
            return own != "" || Pinned(m) ? own
                : ownChanges.FirstOrDefault(c =>
                    c.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                )
                    is { Key: not null } c
                    ? c.Value
                : Value(ownValues, m);
        }

        var leaderBefore = EffectiveKeys(Value(before, control.Mapping), control.DefaultKey);
        foreach (var (m, def) in Followers.GetValueOrDefault(control.Mapping) ?? [])
        {
            var was = Value(before, m);
            var followed =
                leaderBefore.Count > 0
                && EffectiveKeys(was, def).ToHashSet().SetEquals(leaderBefore);
            var wantKeys = followed ? [sdl] : EffectiveKeys(was, def);
            if (!EffectiveKeys(After(m), def).ToHashSet().SetEquals(wantKeys))
            {
                // Following: the key, keeping the mode's other bindings. Not following: pin what it had.
                changes.Add(
                    (
                        "Mapping." + m,
                        followed ? WithKey(was, sdl)
                        : was != "" ? was
                        : $"Key;{def}"
                    )
                );
            }
        }

        // The mode's device list once the player's file is written, which the override's own list replaces.
        var modeDevices = Devices(values) is { Length: > 0 } d
            ? d
            : ownDevicesFixed ?? Devices(ownValues);
        if (WithKeyboard(modeDevices) is { } fixedDevices)
        {
            changes.Add(("Devices", fixedDevices));
        }

        return changes;
    }

    static string Devices(IReadOnlyDictionary<(string Section, string Key), string> ini) =>
        (ini.GetValueOrDefault((VpxInput.Section, "Devices")) ?? "").Trim();

    /// <summary>
    /// A device list that leaves the keyboard out, with it added (else null): VPX reads keys only from devices in the
    /// list. An empty list is left alone (VPX uses its own).
    /// </summary>
    static string? WithKeyboard(string devices) =>
        devices != ""
        && !devices.Split(';').Any(d => d.Trim().Equals("Key", StringComparison.OrdinalIgnoreCase))
            ? "Key;" + devices
            : null;

    /// <summary>The other actions that already use this key in the mode, for a warning (VPX allows sharing one).</summary>
    public static List<string> SharedWith(
        IReadOnlyDictionary<(string Section, string Key), string> effective,
        string mapping,
        int sdl
    ) =>
        Controls
            .Where(c =>
                !c.Mapping.Equals(mapping, StringComparison.OrdinalIgnoreCase)
                && !IsFollower(mapping, c.Mapping)
                && EffectiveKeys(Value(effective, c.Mapping), c.DefaultKey).Contains(sdl)
            )
            .Select(c => c.Label)
            .ToList();

    static bool IsFollower(string leader, string mapping) =>
        Followers.TryGetValue(leader, out var f)
        && f.Any(x => x.Mapping.Equals(mapping, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The new values for <paramref name="control"/> and its followers in one file whose mappings start from
    /// <paramref name="value"/>. A follower moves when it had the same keys as the leader; a leader with no key of its
    /// own (joystick or VR controller only) moves none.
    /// </summary>
    internal static List<(string Key, string Value)> Changes(
        Func<string, string> value,
        Control control,
        int sdl
    )
    {
        var leader = value(control.Mapping);
        var result = new List<(string, string)>
        {
            ("Mapping." + control.Mapping, WithKey(leader, sdl)),
        };
        var leaderKeys = EffectiveKeys(leader, control.DefaultKey);
        if (leaderKeys.Count == 0)
        {
            return result;
        }

        foreach (var (m, def) in Followers.GetValueOrDefault(control.Mapping) ?? [])
        {
            var current = value(m);
            if (EffectiveKeys(current, def).ToHashSet().SetEquals(leaderKeys))
            {
                result.Add(("Mapping." + m, WithKey(current, sdl)));
            }
        }
        return result;
    }

    /// <summary>Writes each file's changes; if one fails, the files already written are put back as they were.</summary>
    static void WriteAll(List<(string File, List<(string Key, string Value)> Changes)> writes)
    {
        var written = new List<(string File, byte[]? Before)>();
        try
        {
            foreach (var (file, changes) in writes.Where(w => w.Changes.Count > 0))
            {
                var before = File.Exists(file) ? File.ReadAllBytes(file) : null;
                VpxIniLayers.SetOverrides(file, VpxInput.Section, changes); // the player's own file has no notes
                written.Add((file, before));
            }
        }
        catch
        {
            foreach (var (file, before) in written)
            {
                try
                {
                    if (before is null)
                    {
                        File.Delete(file);
                    }
                    else
                    {
                        File.WriteAllBytes(file, before);
                    }
                }
                catch { }
            }
            throw;
        }
    }

    public static string Value(
        IReadOnlyDictionary<(string Section, string Key), string> ini,
        string mapping
    ) => ini.GetValueOrDefault((VpxInput.Section, "Mapping." + mapping)) ?? "";

    static List<string> Alternatives(string? mapping) =>
        (mapping ?? "").Split('|').Select(a => a.Trim()).Where(a => a != "").ToList();

    /// <summary>The key of a single-key alternative (<c>Key;225</c>); null for anything else.</summary>
    static int? KeyOf(string alternative)
    {
        if (alternative.Contains('&'))
        {
            return null;
        }

        var parts = alternative.Split(';').Select(p => p.Trim()).ToArray();
        return
            parts.Length == 2
            && parts[0].Equals("Key", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], out var k)
            ? k
            : null;
    }

    static string DeviceOf(string alternative) => alternative.Split(';')[0].Trim();

    // ---- Key names -----------------------------------------------------------------------------

    /// <summary>A key's name for the Setup tab, from its SDL scancode.</summary>
    public static string KeyName(int sdl) =>
        Keys.FirstOrDefault(k => k.Sdl == sdl) is { Name: { } name } ? name : $"Key {sdl}";

    /// <summary>
    /// The SDL scancode for a key by its W3C code name (what Avalonia's <c>PhysicalKey</c> is called: "ShiftLeft",
    /// "A", "Digit1", "NumPad5"…), or null for a key VPX can't map.
    /// </summary>
    public static int? FromCode(string code) =>
        Keys.FirstOrDefault(k => k.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
            is { Code: not null } key
            ? key.Sdl
            : null;

    static readonly (string Code, int Sdl, string Name)[] Keys = BuildKeys();

    static (string, int, string)[] BuildKeys()
    {
        var k = new List<(string, int, string)>();
        for (var c = 'A'; c <= 'Z'; c++)
        {
            k.Add((c.ToString(), 4 + (c - 'A'), c.ToString()));
        }

        for (var d = 1; d <= 9; d++)
        {
            k.Add(($"Digit{d}", 29 + d, d.ToString()));
            k.Add(($"NumPad{d}", 88 + d, $"Keypad {d}"));
        }
        k.Add(("Digit0", 39, "0"));
        k.Add(("NumPad0", 98, "Keypad 0"));
        for (var f = 1; f <= 12; f++)
        {
            k.Add(($"F{f}", 57 + f, $"F{f}"));
        }

        for (var f = 13; f <= 24; f++)
        {
            k.Add(($"F{f}", 91 + f, $"F{f}"));
        }

        k.AddRange([
            ("Enter", 40, "Enter"),
            ("Escape", 41, "Esc"),
            ("Backspace", 42, "Backspace"),
            ("Tab", 43, "Tab"),
            ("Space", 44, "Space"),
            ("Minus", 45, "-"),
            ("Equal", 46, "="),
            ("BracketLeft", 47, "["),
            ("BracketRight", 48, "]"),
            ("Backslash", 49, "\\"),
            ("Semicolon", 51, ";"),
            ("Quote", 52, "'"),
            ("Backquote", 53, "`"),
            ("Comma", 54, ","),
            ("Period", 55, "."),
            ("Slash", 56, "/"),
            ("CapsLock", 57, "Caps Lock"),
            ("PrintScreen", 70, "Print Screen"),
            ("ScrollLock", 71, "Scroll Lock"),
            ("Pause", 72, "Pause/Break"),
            ("Insert", 73, "Insert"),
            ("Home", 74, "Home"),
            ("PageUp", 75, "Page Up"),
            ("Delete", 76, "Delete"),
            ("End", 77, "End"),
            ("PageDown", 78, "Page Down"),
            ("ArrowRight", 79, "Right arrow"),
            ("ArrowLeft", 80, "Left arrow"),
            ("ArrowDown", 81, "Down arrow"),
            ("ArrowUp", 82, "Up arrow"),
            ("NumLock", 83, "Num Lock"),
            ("NumPadDivide", 84, "Keypad /"),
            ("NumPadMultiply", 85, "Keypad *"),
            ("NumPadSubtract", 86, "Keypad -"),
            ("NumPadAdd", 87, "Keypad +"),
            ("NumPadEnter", 88, "Keypad Enter"),
            ("NumPadDecimal", 99, "Keypad ."),
            ("IntlBackslash", 100, "\\ (left of Z)"),
            ("ContextMenu", 101, "Menu"),
            ("NumPadEqual", 103, "Keypad ="),
            ("ControlLeft", 224, "Left Ctrl"),
            ("ShiftLeft", 225, "Left Shift"),
            ("AltLeft", 226, "Left Alt"),
            ("MetaLeft", 227, "Left Windows"),
            ("ControlRight", 228, "Right Ctrl"),
            ("ShiftRight", 229, "Right Shift"),
            ("AltRight", 230, "Right Alt"),
            ("MetaRight", 231, "Right Windows"),
        ]);
        return [.. k];
    }
}
