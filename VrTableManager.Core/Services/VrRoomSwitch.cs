using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VrTableManager.Services;

/// <summary>
/// Some tables have a hand-set VR room switch at the top of their script instead of detecting VR themselves
/// (<c>VRRoom = 0        '0 = Desktop/FS    1 = VR Room</c> in The Getaway, <c>Const VRRoom = 0 '0=VR Room off,
/// 1=360 Room…</c> in Batman 66 and Monster Bash, <c>const VR_Room = 1</c> in Black Knight 2000). Left at 0 such a
/// table plays in the headset as a desktop table: no cabinet or room, and often much too dark, since the VR branch
/// also sets the table's VR lighting. Left on, it shows the VR room on a desktop.
/// <para>
/// Before VPX starts, this sets the switch for the mode being played: on (the table's own room choice, or 1) for
/// VR, 0 for desktop. It never touches the .vpx: VPX 10.8 plays <c>&lt;table&gt;.vbs</c> next to the table in place
/// of its built-in script, so the change is a copy of the script there. What the table had is kept in
/// <c>&lt;table&gt;.vpx.vrroom</c>; once a launch wants the table's own value again, the copy is removed (or a
/// script of the user's own is put back to its value) along with that note.
/// </para>
/// <para>
/// Only a plain top-level assignment whose comment says 0 is off/desktop counts, and only in a script that
/// doesn't check <c>RenderingMode</c> (those pick their room themselves; MF Doom's "0 = big room" is a room).
/// </para>
/// </summary>
public static partial class VrRoomSwitch
{
    /// <summary>The switch in a script: where its value is, the variable's name and the value.</summary>
    public sealed record Switch(int ValueIndex, int ValueLength, string Name, int Value);

    sealed record Note(int Own, bool Created, string? ScriptHash);

    public static string ScriptPath(string vpx) => Path.ChangeExtension(vpx, ".vbs");

    public static string NotePath(string vpx) => vpx + ".vrroom";

    /// <summary>The table's VR room switch, or null when it has none this can safely set (see the class notes).</summary>
    public static Switch? Find(string script)
    {
        if (script.Contains("RenderingMode", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var found = SwitchLine()
            .Matches(script)
            .Where(m => SaysZeroIsOff().IsMatch(m.Groups["comment"].Value))
            .ToList();
        if (found.Count != 1)
        {
            return null; // none, or more than one: not sure which is the real one
        }

        var v = found[0].Groups["value"];
        return new Switch(v.Index, v.Length, found[0].Groups["name"].Value, int.Parse(v.Value));
    }

    public static string WithValue(string script, Switch sw, int value) =>
        script[..sw.ValueIndex] + value + script[(sw.ValueIndex + sw.ValueLength)..];

    /// <summary>
    /// Sets the table's switch for a launch in VR (<paramref name="vr"/>) or on the desktop; with
    /// <paramref name="enabled"/> off it only puts back what the table had. Returns a line for the play log when
    /// it changed something, else null. Never throws: a table must start even if this can't be done.
    /// </summary>
    public static string? Prepare(string vpx, bool vr, bool enabled)
    {
        try
        {
            return PrepareCore(vpx, vr, enabled);
        }
        catch (Exception e)
        {
            return "VR room switch skipped: " + e.Message;
        }
    }

    static string? PrepareCore(string vpx, bool vr, bool enabled)
    {
        var sidecar = ScriptPath(vpx);
        var notePath = NotePath(vpx);
        var note = ReadNote(notePath);
        var builtIn = RomAudio.Script(vpx);
        var hash = Hash(builtIn);

        // A copy made from an older version of the table (it was updated since) would hide the new script.
        if (note is { Created: true } && note.ScriptHash != hash)
        {
            File.Delete(sidecar);
            File.Delete(notePath);
            note = null;
        }
        // A script of the user's own in the table's user\ or scripts\ folder: one beside the table would hide it.
        if (note == null && !File.Exists(sidecar) && OtherScriptOverride(vpx))
        {
            return null;
        }

        var hasSidecar = File.Exists(sidecar);
        var current = hasSidecar ? Encoding.Latin1.GetString(File.ReadAllBytes(sidecar)) : builtIn;
        if (Find(current) is not { } sw)
        {
            if (note != null)
            {
                File.Delete(notePath);
            }

            return null;
        }

        var own = note?.Own ?? sw.Value;
        var want =
            !enabled ? own
            : vr ? (own == 0 ? 1 : own)
            : 0;
        if (want == own)
        {
            if (note == null)
            {
                return null;
            }

            var restored = WithValue(current, sw, own);
            if (note.Created && restored == builtIn)
            {
                File.Delete(sidecar);
            }
            else if (restored != current)
            {
                File.WriteAllBytes(sidecar, Encoding.Latin1.GetBytes(restored));
            }

            File.Delete(notePath);
            return $"{sw.Name} back to the table's own {own}";
        }

        if (note == null)
        {
            File.WriteAllText(notePath, JsonSerializer.Serialize(new Note(own, !hasSidecar, hash)));
        }

        if (sw.Value != want)
        {
            File.WriteAllBytes(sidecar, Encoding.Latin1.GetBytes(WithValue(current, sw, want)));
        }

        return $"{sw.Name} = {want} for {(vr ? "VR" : "desktop")} (the table has {own})";
    }

    static Note? ReadNote(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Note>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    static bool OtherScriptOverride(string vpx)
    {
        var dir = Path.GetDirectoryName(vpx)!;
        var name = Path.GetFileName(ScriptPath(vpx));
        return File.Exists(Path.Combine(dir, "user", name))
            || File.Exists(Path.Combine(dir, "scripts", name));
    }

    static string Hash(string script) =>
        Convert.ToHexString(SHA256.HashData(Encoding.Latin1.GetBytes(script)));

    // At the start of a line (inside an If block it's the table choosing for itself), optional Const, the
    // name, a number, and the rest of the line (its comment).
    [GeneratedRegex(
        @"^(?:Const[ \t]+)?(?<name>VR_?Room)[ \t]*=[ \t]*(?<value>\d+)[ \t]*(?:'(?<comment>[^\r\n]*))?\r?$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline
    )]
    private static partial Regex SwitchLine();

    // "0 = Desktop/FS", "0=VR Room off", "0 - VR Room off", "0 = desktop or cab mode", "1 = VR Room; 0 = desktop".
    [GeneratedRegex(
        @"\b0\s*(?:[=:-]|is)?\s*(?:VR\s*Room\s*)?(?:off|desktop|cab|fs\b|none|disabled|no\b)",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SaysZeroIsOff();
}
