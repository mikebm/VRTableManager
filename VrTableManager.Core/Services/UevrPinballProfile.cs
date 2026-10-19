using System.Runtime.InteropServices;

namespace VrTableManager.Services;

/// <summary>One UEVR setting the pinball profile sets, and why (shown on the Setup tab).</summary>
public sealed record UevrSetting(string Key, string Value, string Why);

public sealed record UevrApplyResult(bool BackedUp, List<string> Changed);

/// <summary>
/// UEVR settings tuned for Zen's pinball games (Pinball FX, Pinball M), written into the game's
/// UEVR profile (%APPDATA%\UnrealVRMod\&lt;game process&gt;\config.txt) before each VR launch.
///
/// Only the keys below are touched; everything else in the profile stays as UEVR or the user left
/// it. The first time, the original file is copied to config.vrtm-original.txt so it can be restored.
/// Key names are from UEVR's source (mods/VR.hpp, mods/FrameworkConfig.hpp; config.txt uses
/// "&lt;Section&gt;_&lt;Name&gt;").
///
/// Not set here: OpenXR vs OpenVR. UEVR's injector writes Frontend_RequestedRuntime itself on every
/// injection from the choice in its own window, so the app only reads it (<see cref="LastRuntime"/>).
/// </summary>
public static class UevrPinballProfile
{
    public static readonly IReadOnlyList<UevrSetting> Settings =
    [
        new("VR_DecoupledPitch", "true", "Keeps the table level instead of tilting and skewing with the game camera's pitch (the usual fix players report)."),
        new("VR_DecoupledPitchUIAdjust", "true", "Keeps menus lined up with the level view."),
        new("VR_EnableGUI", "true", "Makes sure the game's menus and score display are drawn in the headset."),
        new("UI_FollowView", "true", "Menus and HUD stay in front of you, so you can't lose them behind or under the table."),
        new("UI_Distance", "1.500000", "Menus 1.5 m away instead of 2 m: easier to read."),
        new("UI_Size", "1.600000", "Menus a little smaller to match the closer distance."),
        new("VR_ControllersAllowed", "false", "Quest controllers don't send input to the game. Players report they confuse it and can crash it; use a gamepad."),
        new("UI_Framework_MouseEmulation", "false", "Quest controllers don't act as a mouse pointer on UEVR's menu."),
        new("FrameworkConfig_L3R3LongPress", "true", "UEVR's own menu needs a long press of both thumbsticks, so it doesn't pop up mid-game."),
        new("FrameworkConfig_MenuOpen", "false", "UEVR's menu starts closed instead of covering the view."),
    ];

    public static string ConfigPath(ZenGame g) => Path.Combine(PfxSetup.ProfileDirFor(g), "config.txt");
    public static string BackupPath(ZenGame g) => Path.Combine(PfxSetup.ProfileDirFor(g), "config.vrtm-original.txt");

    /// <summary>Writes the pinball settings into the game's UEVR profile (creating it if UEVR hasn't yet).</summary>
    public static UevrApplyResult Apply(ZenGame g) => ApplyTo(ConfigPath(g), BackupPath(g));

    public static UevrApplyResult ApplyTo(string configPath, string backupPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var backedUp = false;
        if (File.Exists(configPath) && !File.Exists(backupPath))
        {
            File.Copy(configPath, backupPath);
            backedUp = true;
        }

        var lines = File.Exists(configPath) ? File.ReadAllLines(configPath).ToList() : [];
        var changed = new List<string>();
        foreach (var s in Settings)
        {
            var i = lines.FindIndex(l => KeyOf(l).Equals(s.Key, StringComparison.OrdinalIgnoreCase));
            var line = $"{s.Key}={s.Value}";
            if (i < 0) { lines.Add(line); changed.Add(s.Key); }
            else if (!ValueOf(lines[i]).Equals(s.Value, StringComparison.OrdinalIgnoreCase)) { lines[i] = line; changed.Add(s.Key); }
        }
        // UEVR keeps the file sorted by key; do the same so diffs stay readable.
        lines = lines.Where(l => l.Trim() != "").OrderBy(KeyOf, StringComparer.Ordinal).ToList();
        File.WriteAllLines(configPath, lines);
        return new UevrApplyResult(backedUp, changed);
    }

    public static bool HasBackup(ZenGame g) => File.Exists(BackupPath(g));

    /// <summary>Puts back the profile as it was before the app first changed it.</summary>
    public static bool RestoreOriginal(ZenGame g)
    {
        if (!File.Exists(BackupPath(g))) return false;
        File.Copy(BackupPath(g), ConfigPath(g), overwrite: true);
        return true;
    }

    /// <summary>"OpenXR" / "OpenVR" from the last time UEVR's injector ran, or null if unknown.</summary>
    public static string? LastRuntime(ZenGame g) => RuntimeFrom(ConfigPath(g));

    public static string? RuntimeFrom(string configPath)
    {
        if (!File.Exists(configPath)) return null;
        var line = File.ReadLines(configPath).FirstOrDefault(l => KeyOf(l) == "Frontend_RequestedRuntime");
        var v = line == null ? "" : ValueOf(line).ToLowerInvariant();
        return v.Contains("openxr") ? "OpenXR" : v.Contains("openvr") ? "OpenVR" : null;
    }

    /// <summary>True when every pinball setting is currently in the profile.</summary>
    public static bool IsApplied(ZenGame g)
    {
        var path = ConfigPath(g);
        if (!File.Exists(path)) return false;
        var kv = File.ReadAllLines(path).Where(l => l.Contains('=')).GroupBy(KeyOf).ToDictionary(x => x.Key, x => ValueOf(x.Last()));
        return Settings.All(s => kv.TryGetValue(s.Key, out var v) && v.Equals(s.Value, StringComparison.OrdinalIgnoreCase));
    }

    static string KeyOf(string line) => line.IndexOf('=') is var i and > 0 ? line[..i].Trim() : line.Trim();
    static string ValueOf(string line) => line.IndexOf('=') is var i and > 0 ? line[(i + 1)..].Trim() : "";
}

/// <summary>Is an Xbox-style (XInput) gamepad plugged in? Windows only; null when it can't tell.</summary>
public static partial class Gamepad
{
    public static bool? IsConnected()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            for (uint i = 0; i < 4; i++)
                if (XInputGetState(i, out _) == 0) return true; // ERROR_SUCCESS
            return false;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetState(uint userIndex, out XInputState state);
}
