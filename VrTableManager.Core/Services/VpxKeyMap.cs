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

    // An "input" is one binding the Setup tab sets: a key (Key;225) or a button or d-pad direction on a game
    // controller or cabinet board (SDLJoy_<guid>_1;4), written as VPX writes it. VR controllers, axes and key
    // combinations are kept as they are, never set here.

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

    /// <summary>A key's input (<c>Key;225</c>).</summary>
    public static string KeyInput(int sdl) => $"Key;{sdl}";

    /// <summary>The inputs a mapping has (keys and controller buttons), as <see cref="InputOf"/> writes them.</summary>
    public static List<string> InputsIn(string? mapping) =>
        Alternatives(mapping).Select(InputOf).OfType<string>().ToList();

    /// <summary>The inputs that work for a mapping: its own, or VPX's default key when it's empty.</summary>
    public static List<string> EffectiveInputs(string? mapping, int defaultKey) =>
        string.IsNullOrWhiteSpace(mapping) ? [KeyInput(defaultKey)] : InputsIn(mapping);

    static bool SameInputs(string? a, int defaultA, string? b, int defaultB) =>
        EffectiveInputs(a, defaultA)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(EffectiveInputs(b, defaultB));

    /// <summary>
    /// Whether a follower moves with its leader's edit: it had the same inputs on the device being changed (its keys
    /// for a key, that controller's buttons for a button; everything for a reset). A leader with no key moves no
    /// follower's keys (it's on a controller or the VR controllers only).
    /// </summary>
    static bool Follows(
        string? follower,
        int followerDefault,
        string? leader,
        int leaderDefault,
        KeyEdit edit,
        string input
    )
    {
        // A button edit also compares the keys: a follower the player put on another key keeps to itself.
        var device = edit == KeyEdit.Reset ? null : DeviceOf(input);
        bool Counts(string i) =>
            device == null
            || IsKey(i)
            || DeviceOf(i).Equals(device, StringComparison.OrdinalIgnoreCase);
        List<string> On(string? m, int def) => EffectiveInputs(m, def).Where(Counts).ToList();
        var leaderInputs = On(leader, leaderDefault);
        if (device == "Key" ? !leaderInputs.Any(IsKey) : leaderInputs.Count == 0)
        {
            return false;
        }

        return On(follower, followerDefault)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(leaderInputs);
    }

    /// <summary>
    /// The mapping with <paramref name="sdl"/> as its only key. The other alternatives (VR controllers, gamepads,
    /// cabinet boards, key combinations) are kept after it.
    /// </summary>
    public static string WithKey(string? mapping, int sdl) => WithInput(mapping, 0, KeyInput(sdl));

    /// <summary>
    /// SET: the mapping with <paramref name="input"/> instead of the inputs it had on the same device. A key replaces
    /// the keys; a button replaces that controller's buttons, and the keys (VPX's default one, when the mapping was
    /// empty) stay. Everything else is kept.
    /// </summary>
    public static string WithInput(string? mapping, int defaultKey, string input)
    {
        var device = DeviceOf(input);
        var alternatives = string.IsNullOrWhiteSpace(mapping)
            ? IsKey(input)
                ? []
                : [KeyInput(defaultKey)]
            : Alternatives(mapping);
        var at = alternatives.FindIndex(a =>
            InputOf(a) is { } i && DeviceOf(i).Equals(device, StringComparison.OrdinalIgnoreCase)
        );
        alternatives.RemoveAll(a =>
            InputOf(a) is { } i && DeviceOf(i).Equals(device, StringComparison.OrdinalIgnoreCase)
        );
        alternatives.Insert(at < 0 ? (IsKey(input) ? 0 : alternatives.Count) : at, input);
        return string.Join(" | ", alternatives);
    }

    /// <summary>
    /// The mapping with <paramref name="sdl"/> added as one more key: its keys (VPX's default one, when it's empty)
    /// still work, as does everything else in it. Unchanged when it already has that key.
    /// </summary>
    public static string WithAddedKey(string? mapping, int defaultKey, int sdl) =>
        WithAddedInput(mapping, defaultKey, KeyInput(sdl));

    /// <summary>ADD: the mapping with one more input; unchanged when it already has it.</summary>
    public static string WithAddedInput(string? mapping, int defaultKey, string input)
    {
        if (EffectiveInputs(mapping, defaultKey).Contains(input, StringComparer.OrdinalIgnoreCase))
        {
            return mapping ?? "";
        }

        var alternatives = string.IsNullOrWhiteSpace(mapping)
            ? [KeyInput(defaultKey)]
            : Alternatives(mapping);
        return string.Join(" | ", alternatives.Append(input));
    }

    /// <summary>
    /// RESET: the mapping with VPX's default key as its only key or button. VR controller bindings, axes and key
    /// combinations stay; with none of those left it's empty (VPX's own default).
    /// </summary>
    public static string WithDefaultKey(string? mapping, int defaultKey)
    {
        var kept = Alternatives(mapping).Where(a => InputOf(a) is null);
        var m = string.Join(" | ", kept.Prepend(KeyInput(defaultKey)));
        return m == KeyInput(defaultKey) ? "" : m;
    }

    /// <summary>What a change on the Setup tab does to an action's keys and buttons.</summary>
    internal enum KeyEdit
    {
        /// <summary>The pressed key or button replaces the ones it had on that device.</summary>
        Replace,

        /// <summary>The pressed key or button works as well as the ones it has.</summary>
        Add,

        /// <summary>Back to VPX's default key.</summary>
        Reset,
    }

    static string Apply(KeyEdit edit, string? mapping, int defaultKey, string input) =>
        edit switch
        {
            KeyEdit.Add => WithAddedInput(mapping, defaultKey, input),
            KeyEdit.Reset => WithDefaultKey(mapping, defaultKey),
            _ => WithInput(mapping, defaultKey, input),
        };

    /// <summary>"Left Shift", "Left Shift (default)", "Z / PS4 Controller button 5 + VR controller"… for the Setup tab.</summary>
    /// <param name="deviceName">A controller's name by its device (<c>SDLJoy_…_1</c>), when known.</param>
    public static string Describe(
        string? mapping,
        int defaultKey,
        Func<string, string?>? deviceName = null
    )
    {
        if (string.IsNullOrWhiteSpace(mapping))
        {
            return KeyName(defaultKey) + " (default)";
        }

        var inputs = new List<string>();
        bool vrController = false,
            controller = false;
        foreach (var alternative in Alternatives(mapping))
        {
            if (InputOf(alternative) is { } input)
            {
                inputs.Add(InputName(input, deviceName));
                continue;
            }

            // A combination ("Key;226 & Key;30") is pressed together: "Left Alt+1".
            var parts = alternative.Split('&').Select(p => p.Trim()).ToList();
            var partKeys = parts.Select(KeyOf).ToList();
            if (partKeys.All(k => k is not null))
            {
                inputs.Add(string.Join("+", partKeys.Select(k => KeyName(k!.Value))));
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
                    controller = true; // an axis (plunger, trigger)
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
            extras.Add("controller axis");
        }

        var text = inputs.Count > 0 ? string.Join(" / ", inputs) : "No key";
        return extras.Count > 0 ? $"{text} + {string.Join(" + ", extras)}" : text;
    }

    /// <summary>"Left Shift", "PS4 Controller button 5", "Controller d-pad up"…</summary>
    public static string InputName(string input, Func<string, string?>? deviceName = null)
    {
        var parts = input.Split(';');
        var code = int.Parse(parts[1]);
        if (IsKey(input))
        {
            return KeyName(code);
        }

        var device = deviceName?.Invoke(parts[0]) is { Length: > 0 } n ? n : "Controller";
        if (code < 0x100)
        {
            return $"{device} button {code + 1}"; // numbered from 1, as Windows' game controller settings do
        }

        var hat = (code - 0x100) / 4;
        var direction = new[] { "left", "right", "up", "down" }[(code - 0x100) % 4];
        return hat == 0 ? $"{device} d-pad {direction}" : $"{device} hat {hat + 1} {direction}";
    }

    /// <summary>
    /// Sets a key for one action in the settings <paramref name="target"/> names: it becomes the action's only
    /// keyboard key. The mappings that follow it (<see cref="Followers"/>) move along when they had the same keys.
    /// Every file is worked out before any is written, and if one can't be written the ones already written are put
    /// back. Returns a line for the Setup tab.
    /// </summary>
    /// <exception cref="InvalidOperationException">There's no VPinballX.ini of the player's to write into.</exception>
    public static string Set(AppSettings s, string mapping, int sdl, ControlsTarget target) =>
        Set(s, mapping, KeyInput(sdl), target);

    /// <summary>
    /// As the key one, for any input: a button replaces the buttons the action had on that controller, and its keys
    /// and other controllers' buttons stay.
    /// </summary>
    /// <exception cref="InvalidOperationException">There's no VPinballX.ini of the player's to write into.</exception>
    public static string Set(AppSettings s, string mapping, string input, ControlsTarget target) =>
        Edit(s, mapping, input, target, KeyEdit.Replace, null);

    /// <summary>As <see cref="Set(AppSettings, string, int, ControlsTarget)"/>, but the key works as well as the ones the action has.</summary>
    /// <exception cref="InvalidOperationException">There's no VPinballX.ini of the player's to write into.</exception>
    public static string Add(AppSettings s, string mapping, int sdl, ControlsTarget target) =>
        Add(s, mapping, KeyInput(sdl), target);

    /// <summary>The key or button works as well as the ones the action has.</summary>
    /// <exception cref="InvalidOperationException">There's no VPinballX.ini of the player's to write into.</exception>
    public static string Add(
        AppSettings s,
        string mapping,
        string input,
        ControlsTarget target,
        Func<string, string?>? deviceName = null
    ) => Edit(s, mapping, input, target, KeyEdit.Add, deviceName);

    /// <summary>As <see cref="Set(AppSettings, string, string, ControlsTarget)"/>, naming the controller in the line it returns.</summary>
    /// <exception cref="InvalidOperationException">There's no VPinballX.ini of the player's to write into.</exception>
    public static string Set(
        AppSettings s,
        string mapping,
        string input,
        ControlsTarget target,
        Func<string, string?>? deviceName
    ) => Edit(s, mapping, input, target, KeyEdit.Replace, deviceName);

    /// <summary>
    /// Puts the action back to VPX's default key in the settings <paramref name="target"/> names (its followers too,
    /// when they had the same keys and buttons): its keys and controller buttons go. VR controller bindings, axes and
    /// key combinations stay; with none, the mapping is emptied, so it's VPX's own default. For VR or desktop only,
    /// that's noted in the override file, so the player's own mapping doesn't come back in that mode.
    /// </summary>
    /// <exception cref="InvalidOperationException">There's no VPinballX.ini of the player's to write into.</exception>
    public static string Reset(AppSettings s, string mapping, ControlsTarget target) =>
        Edit(s, mapping, "", target, KeyEdit.Reset, null);

    sealed record Write(string File, List<(string Key, string Value)> Changes, List<string> Pins);

    static string Edit(
        AppSettings s,
        string mapping,
        string input,
        ControlsTarget target,
        KeyEdit edit,
        Func<string, string?>? deviceName
    )
    {
        var control = Controls.First(c =>
            c.Mapping.Equals(mapping, StringComparison.OrdinalIgnoreCase)
        );
        if (edit != KeyEdit.Reset)
        {
            input =
                InputOf(input)
                ?? throw new ArgumentException($"Not a key or button: {input}", nameof(input));
        }

        // The device list gets what the edit leaves: the input's device, or the keyboard for VPX's default key.
        var device = edit == KeyEdit.Reset ? "Key" : DeviceOf(input);
        var writes = new List<Write>();
        // ADD of an input the action already has wherever it applies: nothing to write (writing the mode's mapping
        // into its override would stop later changes to the player's own file reaching that mode).
        if (edit == KeyEdit.Add)
        {
            bool[] modes = target switch
            {
                ControlsTarget.VrOnly => [true],
                ControlsTarget.DesktopOnly => [false],
                _ => [false, true],
            };
            if (
                modes.All(vr =>
                    VpxIniLayers.Effective(s, vr) is var e
                    && EffectiveInputs(Value(e, control.Mapping), control.DefaultKey)
                        .Contains(input, StringComparer.OrdinalIgnoreCase)
                    && WithDevice(Devices(e), device) is null // and VPX reads that device
                )
            )
            {
                return $"{control.Label} already works with {InputName(input, deviceName)}.";
            }
        }

        string where;
        if (target is ControlsTarget.VrOnly or ControlsTarget.DesktopOnly)
        {
            // The mode's whole mapping (the player's own, or the override's) goes into the override.
            var vr = target == ControlsTarget.VrOnly;
            var effective = VpxIniLayers.Effective(s, vr);
            string Mode(string m) => Value(effective, m);
            var changes = Changes(Mode, control, edit, input);
            if (WithDevice(Devices(effective), device) is { } devices)
            {
                changes.Add(("Devices", devices));
            }

            var ownValues = VpxIniLayers.BasePath(s) is { } b ? IniFile.Read(b) : IniFile.Read("");
            writes.Add(
                new(
                    VpxIniLayers.OverridePath(s, vr),
                    changes,
                    Pins(changes, key => ownValues.GetValueOrDefault((VpxInput.Section, key)) ?? "")
                )
            );
            where = vr ? "in VR play" : "in desktop play";
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
            var ownChanges = Changes(Own, control, edit, input);
            var ownDevices = WithDevice(Devices(ownValues), device);
            if (ownDevices != null)
            {
                ownChanges.Add(("Devices", ownDevices));
            }

            writes.Add(new(own, ownChanges, []));
            foreach (var vr in new[] { true, false })
            {
                writes.Add(
                    ModeChanges(
                        s,
                        vr,
                        ownValues,
                        ownChanges,
                        ownDevices,
                        control,
                        edit,
                        input,
                        device
                    )
                );
            }
            where = $"in desktop and VR play (saved in {own})";
        }

        WriteAll(writes);
        return edit switch
        {
            KeyEdit.Add =>
                $"✓ {control.Label} also works with {InputName(input, deviceName)} {where}.",
            KeyEdit.Reset =>
                $"✓ {control.Label} is back to VPX's default key ({KeyName(control.DefaultKey)}) {where}.",
            _ when IsKey(input) => $"✓ {control.Label} is {InputName(input, deviceName)} {where}.",
            _ => $"✓ {control.Label} is {InputName(input, deviceName)} on that controller {where}.",
        };
    }

    /// <summary>
    /// The mappings an override empties while the layer under it (<paramref name="below"/>) has a value: an empty
    /// value is no override, so they're noted as put back to VPX's default instead.
    /// </summary>
    static List<string> Pins(
        List<(string Key, string Value)> changes,
        Func<string, string> below
    ) =>
        changes
            .Where(c =>
                c.Value == ""
                && c.Key.StartsWith("Mapping.", StringComparison.OrdinalIgnoreCase)
                && below(c.Key) != ""
            )
            .Select(c => c.Key)
            .ToList();

    /// <summary>
    /// "Desktop and VR": what one mode's override needs so that, with the player's own file changed, the mode ends up
    /// as if the change were made in it: the leader gets it even where the override has its own mapping, followers
    /// that followed in this mode (by what it used before) get it too, and followers that didn't keep what they had.
    /// </summary>
    static Write ModeChanges(
        AppSettings s,
        bool vr,
        Dictionary<(string Section, string Key), string> ownValues,
        List<(string Key, string Value)> ownChanges,
        string? ownDevicesFixed,
        Control control,
        KeyEdit edit,
        string input,
        string device
    )
    {
        var path = VpxIniLayers.OverridePath(s, vr);
        var values = IniFile.Read(path);
        // Put back to VPX's default in this mode during play: the mode uses the default, not the player's file.
        var setToDefault = VpxIniLayers.SetToDefault(path);
        bool Pinned(string m) => setToDefault.Contains((VpxInput.Section, "Mapping." + m));
        var before = VpxIniLayers.Effective(s, vr);
        var changes = new List<(string Key, string Value)>();
        var leader = Value(values, control.Mapping);
        if (leader != "" || Pinned(control.Mapping))
        {
            changes.Add(
                ("Mapping." + control.Mapping, Apply(edit, leader, control.DefaultKey, input))
            );
        }

        // The player's own value once it's written.
        string OwnAfter(string key) =>
            ownChanges.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                is { Key: not null } c
                ? c.Value
                : ownValues.GetValueOrDefault((VpxInput.Section, key)) ?? "";

        // What the mode uses once both files are written: the override's value (empty: VPX's default, when it was
        // put back to it), else the player's new or old one.
        string After(string m)
        {
            var own = Value(values, m);
            return own != "" || Pinned(m) ? own : OwnAfter("Mapping." + m);
        }

        var leaderBefore = Value(before, control.Mapping);
        foreach (var (m, def) in Followers.GetValueOrDefault(control.Mapping) ?? [])
        {
            var was = Value(before, m);
            var followed = Follows(was, def, leaderBefore, control.DefaultKey, edit, input);
            var want = followed ? Apply(edit, was, def, input) : was;
            if (!SameInputs(After(m), def, want, def))
            {
                // Following: the change, keeping the mode's other bindings. Not following: pin what it had.
                changes.Add(
                    (
                        "Mapping." + m,
                        followed ? want
                        : was != "" ? was
                        : KeyInput(def)
                    )
                );
            }
        }

        // The mode's device list once the player's file is written, which the override's own list replaces.
        var modeDevices = Devices(values) is { Length: > 0 } d
            ? d
            : ownDevicesFixed ?? Devices(ownValues);
        if (WithDevice(modeDevices, device) is { } fixedDevices)
        {
            changes.Add(("Devices", fixedDevices));
        }

        return new(path, changes, Pins(changes, OwnAfter));
    }

    static string Devices(IReadOnlyDictionary<(string Section, string Key), string> ini) =>
        (ini.GetValueOrDefault((VpxInput.Section, "Devices")) ?? "").Trim();

    /// <summary>
    /// A device list that leaves <paramref name="device"/> out (the keyboard, <c>Key</c>, or a controller), with it
    /// added (else null): VPX reads inputs only from devices in the list. An empty list is left alone (VPX uses its own).
    /// </summary>
    static string? WithDevice(string devices, string device) =>
        devices != ""
        && !devices.Split(';').Any(d => d.Trim().Equals(device, StringComparison.OrdinalIgnoreCase))
            ? device == "Key"
                ? "Key;" + devices
                : devices + ";" + device
            : null;

    /// <summary>The other actions that already use this key in the mode, for a warning (VPX allows sharing one).</summary>
    public static List<string> SharedWith(
        IReadOnlyDictionary<(string Section, string Key), string> effective,
        string mapping,
        int sdl
    ) => SharedWith(effective, mapping, KeyInput(sdl));

    /// <summary>The other actions that already use this key or button in the mode.</summary>
    public static List<string> SharedWith(
        IReadOnlyDictionary<(string Section, string Key), string> effective,
        string mapping,
        string input
    ) =>
        Controls
            .Where(c =>
                !c.Mapping.Equals(mapping, StringComparison.OrdinalIgnoreCase)
                && !IsFollower(mapping, c.Mapping)
                && EffectiveInputs(Value(effective, c.Mapping), c.DefaultKey)
                    .Contains(input, StringComparer.OrdinalIgnoreCase)
            )
            .Select(c => c.Label)
            .ToList();

    static bool IsFollower(string leader, string mapping) =>
        Followers.TryGetValue(leader, out var f)
        && f.Any(x => x.Mapping.Equals(mapping, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The new values for <paramref name="control"/> and its followers in one file whose mappings start from
    /// <paramref name="value"/>. A follower moves when it had the same keys and buttons as the leader; a leader with
    /// none of its own (VR controller or axis only) moves none.
    /// </summary>
    static List<(string Key, string Value)> Changes(
        Func<string, string> value,
        Control control,
        KeyEdit edit,
        string input
    )
    {
        var leader = value(control.Mapping);
        var result = new List<(string, string)>
        {
            ("Mapping." + control.Mapping, Apply(edit, leader, control.DefaultKey, input)),
        };
        foreach (var (m, def) in Followers.GetValueOrDefault(control.Mapping) ?? [])
        {
            var current = value(m);
            if (Follows(current, def, leader, control.DefaultKey, edit, input))
            {
                result.Add(("Mapping." + m, Apply(edit, current, def, input)));
            }
        }
        return result;
    }

    /// <summary>Writes each file's changes; if one fails, the files already written are put back as they were.</summary>
    static void WriteAll(List<Write> writes)
    {
        var written = new List<(string File, byte[]? Before)>();
        try
        {
            foreach (var (file, changes, pins) in writes.Where(w => w.Changes.Count > 0))
            {
                written.Add((file, File.Exists(file) ? File.ReadAllBytes(file) : null));
                VpxIniLayers.SetOverrides(file, VpxInput.Section, changes); // the player's own file has no notes
                if (pins.Count > 0)
                {
                    VpxIniLayers.NoteSetToDefault(file, VpxInput.Section, pins);
                }
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
    static int? KeyOf(string alternative) =>
        InputOf(alternative) is { } i && IsKey(i) ? int.Parse(i.Split(';')[1]) : null;

    /// <summary>
    /// An alternative the Setup tab sets, written the same way every time (<c>Key;225</c>,
    /// <c>SDLJoy_&lt;guid in lower case&gt;_1;4</c>): one key, or one controller button or d-pad direction. Null for
    /// combinations, axes (a third part, or 0x200 and up), the VR controllers and anything else.
    /// </summary>
    static string? InputOf(string alternative)
    {
        if (alternative.Contains('&'))
        {
            return null;
        }

        var parts = alternative.Split(';').Select(p => p.Trim()).ToArray();
        if (parts.Length != 2 || !int.TryParse(parts[1], out var code) || code < 0)
        {
            return null;
        }

        if (parts[0].Equals("Key", StringComparison.OrdinalIgnoreCase))
        {
            return KeyInput(code);
        }

        return parts[0].StartsWith("SDLJoy_", StringComparison.OrdinalIgnoreCase) && code < 0x200
            ? $"SDLJoy_{parts[0]["SDLJoy_".Length..].ToLowerInvariant()};{code}"
            : null;
    }

    static bool IsKey(string input) => input.StartsWith("Key;", StringComparison.OrdinalIgnoreCase);

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
