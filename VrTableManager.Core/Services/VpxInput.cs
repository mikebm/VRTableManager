namespace VrTableManager.Services;

/// <summary>
/// Brings the controls a player set up in normal (desktop) VPX into the VR settings file. VPX 10.8.1 keeps
/// them in the <c>[Input]</c> section (<c>Devices</c>, <c>Device.*</c>, <c>Mapping.*</c>) of whichever ini it
/// runs with. VR play inherits them from the player's own VPinballX.ini (<see cref="VpxIniLayers"/>) until the
/// VR overrides (<see cref="VpxSetup.VrIniPath"/>) have mappings of their own; this merges the desktop ones in
/// then, keeping the VR controller bindings as alternatives.
/// </summary>
public static class VpxInput
{
    public const string Section = "Input";

    /// <summary>Where desktop VPX keeps its settings, most likely first.</summary>
    public static IEnumerable<string> DesktopIniCandidates(AppSettings s)
    {
        // "Store settings next to the program" option.
        yield return Path.Combine(s.VpxDir, "VPinballX.ini");
        // 10.8.1+: one folder per minor version.
        if (Directory.Exists(AppFolders.VpxPrefs))
            foreach (var dir in Directory.EnumerateDirectories(AppFolders.VpxPrefs).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal))
                yield return Path.Combine(dir, "VPinballX.ini");
        yield return Path.Combine(AppFolders.VpxPrefs, "VPinballX.ini");
    }

    /// <summary>The desktop VPX settings file that has controls in the 10.8.1 format, or null.</summary>
    public static string? FindDesktopIni(AppSettings s) => DesktopIniCandidates(s).FirstOrDefault(HasMappings);

    public static bool HasMappings(string iniPath) =>
        IniFile.ReadSection(iniPath, Section).Any(kv => kv.Key.StartsWith("Mapping.", StringComparison.OrdinalIgnoreCase) && kv.Value != "");

    /// <summary>
    /// The <c>[Input]</c> values to write into the VR ini so it uses the desktop controls. Empty desktop values
    /// mean "VPX default" and leave the VR value alone. A desktop mapping replaces the VR one, but bindings on
    /// devices the desktop file doesn't know (the VR controllers, "OpenXR") are kept as alternatives, so the
    /// Quest controllers still work next to the imported gamepad or keyboard.
    /// </summary>
    public static List<(string Key, string Value)> Merge(IReadOnlyList<(string Key, string Value)> desktop, IReadOnlyList<(string Key, string Value)> vr)
    {
        var vrValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in vr) vrValues[k] = v;
        var desktopDevices = Split(desktop.FirstOrDefault(kv => kv.Key.Equals("Devices", StringComparison.OrdinalIgnoreCase)).Value, ';');

        var result = new List<(string, string)>();
        foreach (var (key, value) in desktop)
        {
            if (value == "") continue;
            vrValues.TryGetValue(key, out var current);
            current ??= "";
            string merged;
            if (key.Equals("Devices", StringComparison.OrdinalIgnoreCase))
                merged = string.Join(";", desktopDevices.Concat(Split(current, ';')).Distinct(StringComparer.OrdinalIgnoreCase));
            else if (key.StartsWith("Mapping.", StringComparison.OrdinalIgnoreCase) && !IsSensor(key))
            {
                // Bindings are "Dev;id[;...]" alternatives joined by " | "; a combination joins bindings with " & ".
                var keep = Split(current, '|').Where(alt => Devices(alt).All(d => !desktopDevices.Contains(d, StringComparer.OrdinalIgnoreCase)));
                merged = string.Join(" | ", Split(value, '|').Concat(keep).Distinct(StringComparer.OrdinalIgnoreCase));
            }
            else merged = value;
            if (merged != current) result.Add((key, merged));
        }
        return result;
    }

    /// <summary>Analog nudge / plunger sensors take one source, so the desktop one simply wins.</summary>
    static bool IsSensor(string key) =>
        key.StartsWith("Mapping.Nudge", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Mapping.Plunger", StringComparison.OrdinalIgnoreCase);

    static List<string> Split(string? s, char sep) =>
        (s ?? "").Split(sep).Select(p => p.Trim()).Where(p => p != "").ToList();

    static IEnumerable<string> Devices(string alternative) =>
        alternative.Split('&').Select(b => b.Trim().Split(';')[0].Trim()).Where(d => d != "");

    /// <summary>
    /// Copies the desktop controls into the VR ini. Returns what happened, for the UI; null when there was
    /// nothing to import (no desktop file with 10.8.1 controls).
    /// </summary>
    public static string? Import(AppSettings s)
    {
        if (FindDesktopIni(s) is not { } desktop) return null;
        var vrIni = VpxSetup.VrIniPath(s);
        var changes = Merge(IniFile.ReadSection(desktop, Section), IniFile.ReadSection(vrIni, Section));
        if (changes.Count > 0) IniFile.SetMany(vrIni, Section, changes);
        var mapped = changes.Count(c => c.Key.StartsWith("Mapping.", StringComparison.OrdinalIgnoreCase));
        return changes.Count == 0
            ? $"VR already uses the controls from {desktop}."
            : $"Copied {mapped} control mapping{(mapped == 1 ? "" : "s")} from {desktop}. VR controller buttons were kept.";
    }
}
