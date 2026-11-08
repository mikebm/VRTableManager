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
/// In VR it also carries <see cref="VrBackglass"/>' block moving VPX's backglass onto a blank cabinet backglass.
/// The same copy carries <see cref="PupRootPaths"/>' fix for scripts that build paths onto PuPlayer.GetRoot without a
/// backslash, in VR and on the desktop alike; a table that needs only that keeps its copy between launches.
/// </para>
/// <para>
/// Only a plain top-level assignment counts: True/False (<c>Const VRRoom = False ' Turns on VR Room and Cabinet</c>
/// in Guardians of the Galaxy), or a number whose comment says 0 is off/desktop; and only in a script that
/// doesn't check <c>RenderingMode</c> (those pick their room themselves; MF Doom's "0 = big room" is a room).
/// </para>
/// </summary>
public static partial class VrRoomSwitch
{
    /// <summary>
    /// The switch in a script: where its value is, the variable's name and the value (True/False as 1/0, with
    /// <paramref name="IsBool"/> set so it's written back the same way).
    /// </summary>
    public sealed record Switch(
        int ValueIndex,
        int ValueLength,
        string Name,
        int Value,
        bool IsBool = false
    );

    /// <param name="Own">The table's own switch value, put back when the mode no longer wants it changed.</param>
    /// <param name="Created">The .vbs is this app's copy (rebuilt from the table's script, removed when not needed).</param>
    /// <param name="ScriptHash">The table's built-in script the copy was made from.</param>
    /// <param name="WrittenHash">What this app last wrote to its copy; a copy changed since is the user's.</param>
    sealed record Note(int Own, bool Created, string? ScriptHash, string? WrittenHash = null);

    public static string ScriptPath(string vpx) => Path.ChangeExtension(vpx, ".vbs");

    public static string NotePath(string vpx) => vpx + ".vrroom";

    /// <summary>The table's VR room switch, or null when it has none this can safely set (see the class notes).</summary>
    public static Switch? Find(string script)
    {
        if (script.Contains("RenderingMode", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A number counts when its comment says 0 is off. True/False says on/off by itself, but only above the
        // first Sub or Function (an unindented one inside a Sub is the script changing it, not the setting), and
        // only when there's no numbered switch.
        var matches = SwitchLine().Matches(script);
        var found = matches
            .Where(m =>
                char.IsDigit(m.Groups["value"].Value[0])
                && SaysZeroIsOff().IsMatch(m.Groups["comment"].Value)
            )
            .ToList();
        if (found.Count == 0)
        {
            var firstSub = FirstSub().Match(script) is { Success: true } s
                ? s.Index
                : script.Length;
            found = matches
                .Where(m => !char.IsDigit(m.Groups["value"].Value[0]) && m.Index < firstSub)
                .ToList();
        }
        if (found.Count != 1)
        {
            return null; // none, or more than one: not sure which is the real one
        }

        var v = found[0].Groups["value"];
        var isBool = !char.IsDigit(v.Value[0]);
        var value = isBool
            ? (v.Value.Equals("True", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            : int.Parse(v.Value);
        return new Switch(v.Index, v.Length, found[0].Groups["name"].Value, value, isBool);
    }

    public static string WithValue(string script, Switch sw, int value) =>
        script[..sw.ValueIndex]
        + (sw.IsBool ? (value != 0 ? "True" : "False") : value.ToString())
        + script[(sw.ValueIndex + sw.ValueLength)..];

    /// <summary>
    /// Sets the table's switch for a launch in VR (<paramref name="vr"/>) or on the desktop; with
    /// <paramref name="enabled"/> off it only puts back what the table had. Returns a line for the play log when
    /// it changed something, else null. Never throws: a table must start even if this can't be done.
    /// </summary>
    /// <param name="fixPupRoot">Also apply <see cref="PupRootPaths"/> (VPX's PinUP Player plugin in use).</param>
    /// <param name="backglass">In VR, also move VPX's backglass onto the cabinet (<see cref="VrBackglass"/>).</param>
    public static string? Prepare(
        string vpx,
        bool vr,
        bool enabled,
        bool fixPupRoot = true,
        VrBackglass.Placement? backglass = null
    )
    {
        try
        {
            return PrepareCore(vpx, vr, enabled, fixPupRoot, backglass);
        }
        catch (Exception e)
        {
            return "VR room switch skipped: " + e.Message;
        }
    }

    static string? PrepareCore(
        string vpx,
        bool vr,
        bool enabled,
        bool fixPupRoot,
        VrBackglass.Placement? backglass
    )
    {
        var sidecar = ScriptPath(vpx);
        var notePath = NotePath(vpx);
        var note = ReadNote(notePath);
        var builtIn = RomAudio.Script(vpx);
        var hash = Hash(builtIn);

        // A copy made from an older version of the table (it was updated since) would hide the new script. One the
        // user changed since this app wrote it is theirs: it stays (an older version's note, without WrittenHash,
        // can't tell, and goes as it always did).
        if (note is { Created: true } && note.ScriptHash != hash)
        {
            var ours =
                note.WrittenHash == null
                || (
                    File.Exists(sidecar)
                    && note.WrittenHash
                        == Hash(Encoding.Latin1.GetString(File.ReadAllBytes(sidecar)))
                );
            if (ours)
            {
                File.Delete(sidecar);
            }
            File.Delete(notePath);
            note = ours ? null : note with { Created = false, WrittenHash = null };
        }
        // A script of the user's own in the table's user\ or scripts\ folder: one beside the table would hide it.
        if (note == null && !File.Exists(sidecar) && OtherScriptOverride(vpx))
        {
            return null;
        }

        var hasSidecar = File.Exists(sidecar);
        var current = hasSidecar ? Encoding.Latin1.GetString(File.ReadAllBytes(sidecar)) : builtIn;
        // The copy this app makes is rebuilt from the table's own script each launch; a .vbs of the user's own is
        // changed in place. A copy of this app's that was edited since it was written is the user's now.
        // With no record of what this app wrote (an older version's note, or the note lost), the copy is this
        // app's only when it's exactly what this app makes from the table's script; anything else is the user's.
        var created =
            !hasSidecar ? note?.Created ?? true
            : note is { Created: false } ? false
            : note?.WrittenHash is { } written ? written == Hash(current)
            : AppMade(current, builtIn);
        var script = created ? builtIn : current;
        var sw = Find(script);
        var own = note?.Own ?? sw?.Value ?? 0;
        var want =
            sw == null ? own
            : !enabled ? own
            : vr ? (own == 0 ? 1 : own)
            : 0;

        // Written only when the value changes, so "false" stays "false" rather than becoming "False".
        var wanted = sw == null || want == sw.Value ? script : WithValue(script, sw, want);
        // A user's own .vbs gets the GetRoot fix too: it's the script VPX plays, and the fix only adds a backslash.
        var withRoot = fixPupRoot ? PupRootPaths.Fix(wanted) : wanted;
        // Logged once: when the script VPX played last time still lacked it.
        var rootFixed = withRoot != wanted && PupRootPaths.Fix(current) != current;
        // VR only: the block that moves VPX's backglass onto the cabinet (taken out again for the desktop).
        var withBackglass = VrBackglass.WithScript(withRoot, vr ? backglass : null);
        var backglassMoved = backglass != null && vr && withBackglass != current;
        wanted = withBackglass;

        var lines = new List<string>();
        if (sw != null && want != own)
        {
            lines.Add($"{sw.Name} = {want} for {(vr ? "VR" : "desktop")} (the table has {own})");
        }
        else if (sw != null && note != null && wanted != current)
        {
            lines.Add($"{sw.Name} back to the table's own {own}");
        }
        if (rootFixed)
        {
            lines.Add("backslash added after PuPlayer.GetRoot in its paths");
        }
        if (backglassMoved)
        {
            lines.Add($"VPX's VR backglass moved onto the cabinet's \"{backglass!.Panel}\"");
        }

        if (created && wanted == builtIn)
        {
            if (hasSidecar)
            {
                File.Delete(sidecar);
            }
        }
        else if (wanted != current)
        {
            File.WriteAllBytes(sidecar, Encoding.Latin1.GetBytes(wanted));
        }

        // The note says the copy is this app's (to be rebuilt or removed) or which value to put back in the user's.
        var needsNote = (created && wanted != builtIn) || (sw != null && want != own);
        var newNote = new Note(own, created, hash, created ? Hash(wanted) : null);
        if (needsNote && note != newNote)
        {
            File.WriteAllText(notePath, JsonSerializer.Serialize(newNote));
        }
        else if (!needsNote && note != null)
        {
            File.Delete(notePath);
        }

        return lines.Count > 0 ? string.Join("; ", lines) : null;
    }

    /// <summary>
    /// The script is one this app could have written from the table's: the table's own, with its room switch at
    /// any value, with or without the GetRoot fix and the backglass block.
    /// </summary>
    static bool AppMade(string current, string builtIn)
    {
        var bare = VrBackglass.WithScript(current, null);
        var sw = Find(builtIn);
        IEnumerable<string> bases =
            sw == null
                ? [builtIn]
                : new[] { 0, 1, 2, 3, 4, sw.Value }
                    .Distinct()
                    .Select(v => v == sw.Value ? builtIn : WithValue(builtIn, sw, v));
        return bases.Any(b => bare == b || bare == PupRootPaths.Fix(b));
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
        @"^(?:Const[ \t]+)?(?<name>VR_?Room)[ \t]*=[ \t]*(?<value>\d+|True|False)[ \t]*(?:'(?<comment>[^\r\n]*))?\r?$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline
    )]
    private static partial Regex SwitchLine();

    [GeneratedRegex(
        @"^[ \t]*(?:(?:Public|Private)[ \t]+)?(?:Sub|Function)[ \t]",
        RegexOptions.IgnoreCase | RegexOptions.Multiline
    )]
    private static partial Regex FirstSub();

    // "0 = Desktop/FS", "0=VR Room off", "0 - VR Room off", "0 = desktop or cab mode", "1 = VR Room; 0 = desktop".
    [GeneratedRegex(
        @"\b0\s*(?:[=:-]|is)?\s*(?:VR\s*Room\s*)?(?:off|desktop|cab|fs\b|none|disabled|no\b)",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SaysZeroIsOff();
}
