using System.Text.RegularExpressions;

namespace VrTableManager.Services;

/// <summary>
/// PinUP Player's <c>PuPlayer.GetRoot</c> is its PUPVideos folder with a trailing backslash, and some scripts build
/// paths straight onto it: <c>PuPlayer.GetRoot &amp; "DarkestDungeon\ScreenType.txt"</c> (Darkest Dungeon, Big
/// Trouble in Little China), <c>puplayer.getroot &amp; cGameName &amp; "\sToken.dat"</c>. VPX's PinUP Player plugin
/// returns the folder without the backslash, so those paths miss it; Darkest Dungeon reads ScreenType.txt as it
/// loads, and the rest of its script never runs. This puts a backslash after GetRoot wherever a script joins
/// something on without one. On PinUP Player itself that makes a doubled backslash, which Windows reads as one.
/// <see cref="VrRoomSwitch"/> applies it to the copy of the script it plays beside the table.
/// </summary>
public static partial class PupRootPaths
{
    /// <summary>The script with a backslash after GetRoot wherever it's missing; the same string when none is.</summary>
    public static string Fix(string script)
    {
        if (script.IndexOf("GetRoot", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return script;
        }

        var quoted = QuotedJoin()
            .Replace(
                script,
                m => InCode(script, m.Index) ? m.Groups["head"].Value + "\\" : m.Value
            );
        return VariableJoin()
            .Replace(
                quoted,
                m => InCode(quoted, m.Index) ? m.Groups["head"].Value + "\"\\\" & " : m.Value
            );
    }

    /// <summary>
    /// The position is script code: not inside a string literal (ExecuteGlobal "… GetRoot & x") or after a '
    /// comment mark, judged from the start of its line.
    /// </summary>
    static bool InCode(string script, int index)
    {
        var start = script.LastIndexOfAny(['\r', '\n'], Math.Max(0, index - 1)) + 1;
        if (index == 0)
        {
            start = 0;
        }
        var inString = false;
        for (var i = start; i < index; i++)
        {
            if (script[i] == '"')
            {
                inString = !inString;
            }
            else if (script[i] == '\'' && !inString)
            {
                return false;
            }
        }
        return !inString;
    }

    /// <summary><c>.GetRoot &amp; "Name\…"</c>: a string that doesn't start with a backslash (or end right away).</summary>
    [GeneratedRegex(@"(?<head>\.GetRoot[ \t]*&[ \t]*"")(?=[^\\/""\r\n])", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedJoin();

    /// <summary><c>.GetRoot &amp; cGameName</c>: a variable joined on directly (not a vb constant such as vbNullString).</summary>
    [GeneratedRegex(
        @"(?<head>\.GetRoot[ \t]*&[ \t]*)(?=[A-Za-z_])(?!vb[A-Z])",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex VariableJoin();
}
