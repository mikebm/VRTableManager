using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace VrTableManager.Services;

/// <summary>
/// Works out which PinMAME ROM a VPX table loads. VPS often lists several ROM versions for a
/// machine, but each table script names exactly one: <c>Const cGameName = "mm_109c"</c>.
/// Unlocked .vpx files keep the script as plain text, so a byte scan finds it without parsing
/// the file format.
/// </summary>
public static partial class RomDetector
{
    /// <summary>The ROM set the table loads (see <see cref="VpxRomInfo.RomName"/>).</summary>
    public static string? FromVpx(string vpxPath) => Inspect(vpxPath).RomName;

    /// <summary>
    /// Reads the table script for the ROM name (cGameName) and whether it drives PinMAME at all
    /// (LoadVPM / VPinMAME.Controller). Original tables often set cGameName for other things
    /// (high scores, backglass) without using a ROM; those don't need one.
    /// </summary>
    public static VpxRomInfo Inspect(string vpxPath)
    {
        if (!File.Exists(vpxPath))
        {
            return new(null, false);
        }

        const int Chunk = 4 << 20,
            Overlap = 256;
        var buffer = new byte[Chunk + Overlap];
        var carry = 0;
        string? gameName = null,
            pupName = null,
            controllerRom = null;
        var pinMame = false;
        var pup = false;
        bool? pupSwitch = null;
        bool ownDmd = false,
            ownDmdOff = false;
        var aliasTargets = new List<string>();
        using var fs = File.OpenRead(vpxPath);
        int read,
            chunksAfter = 0;
        // Alias lines sit near cGameName (Mass Effect's is six lines on), so read one more chunk after
        // both answers are in rather than the whole file (tables run to hundreds of MB).
        while (chunksAfter < 2 && (read = fs.Read(buffer, carry, Chunk)) > 0)
        {
            if (gameName != null && pinMame)
            {
                chunksAfter++;
            }

            var total = carry + read;
            var text = Encoding.Latin1.GetString(buffer, 0, total);
            gameName ??= FromScript(text);
            pinMame = pinMame || UsesPinMame(text);
            controllerRom ??= ControllerRomFromScript(text);
            pup = pup || UsesPup(text);
            pupName ??= PupNameFromScript(text, gameName);
            pupSwitch ??= PupSwitch(text);
            ownDmd = ownDmd || CreatesOwnDmd(text);
            ownDmdOff = ownDmdOff || OwnDmdSwitchedOff(text);
            foreach (var a in AliasTargetsIn(text))
            {
                if (!aliasTargets.Contains(a, StringComparer.OrdinalIgnoreCase))
                {
                    aliasTargets.Add(a);
                }
            }
            // Keep the tail so a match split across chunks is still found.
            var keep = Math.Min(Overlap, total);
            Buffer.BlockCopy(buffer, total - keep, buffer, 0, keep);
            carry = keep;
        }
        return new(gameName, pinMame)
        {
            ControllerRom = controllerRom,
            AliasTargets = aliasTargets,
            UsesPup = pup,
            PupName = pupName,
            PupOff = pupSwitch == false,
            UsesOwnDmd = ownDmd && !ownDmdOff,
        };
    }

    public static bool UsesPup(string script) => PupRegex().IsMatch(script);

    /// <summary>An uncommented line creates FlexDMD or UltraDMD, which draw on the table's DMD flasher.</summary>
    public static bool CreatesOwnDmd(string script) =>
        CodeLinesWith(script, "CreateObject").Any(l => OwnDmdObjectRegex().IsMatch(l));

    /// <summary>
    /// The script switches FlexDMD/UltraDMD off (<c>Const UseUltraDMD = 0</c>, <c>UseFlexDMD = False</c>): the
    /// code that creates them is there but doesn't run. The first assignment is the setting.
    /// </summary>
    public static bool OwnDmdSwitchedOff(string script)
    {
        foreach (var word in (string[])["UseFlexDMD", "UseUltraDMD"])
        {
            foreach (var line in CodeLinesWith(script, word))
            {
                if (OwnDmdSwitchRegex().Match(line) is { Success: true } m)
                {
                    return m.Groups[1].Value is "0"
                        || m.Groups[1].Value.Equals("False", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        return DmdModeIsPup(script);
    }

    /// <summary>
    /// A display-mode setting whose own comment says the value chosen is PuP:
    /// <c>Const DMDMode = 2 ' 2=PUP (Only use Pup)</c> in Guardians of the Galaxy. The first assignment is the setting.
    /// </summary>
    static bool DmdModeIsPup(string script)
    {
        if (script.IndexOf("DMDMode", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (DmdModeRegex().Match(script) is not { Success: true } m)
        {
            return false;
        }

        return Regex.IsMatch(
            m.Groups["comment"].Value,
            $@"(?<![0-9])\b{m.Groups["value"].Value}\s*[=:-]\s*\(?\s*PuP\b",
            RegexOptions.IgnoreCase
        );
    }

    /// <summary>
    /// The script's own PuP switch (<c>usePUP = False</c>, as JP's template ships it): false when it's off,
    /// null when there's none. The first assignment is the setting; later ones are the script's fallbacks.
    /// </summary>
    public static bool? PupSwitch(string script)
    {
        foreach (var line in CodeLinesWith(script, "usePUP"))
        {
            if (UsePupSwitchRegex().Match(line) is { Success: true } m)
            {
                return m.Groups[1].Value is "1"
                    || m.Groups[1].Value.Equals("True", StringComparison.OrdinalIgnoreCase);
            }
        }

        return null;
    }

    /// <summary>
    /// The PuP pack folder the script opens: the second argument of <c>PuPlayer.Init pBackglass, cGameName</c>,
    /// either a literal or a variable set elsewhere (<c>Const cPuPPack = "jaws50"</c>; cGameName is the ROM).
    /// </summary>
    public static string? PupNameFromScript(string script, string? gameName = null)
    {
        foreach (var line in CodeLinesWith(script, "Init"))
        {
            if (PupInitRegex().Match(line) is not { Success: true } m)
            {
                continue;
            }

            if (m.Groups[1].Success)
            {
                return m.Groups[1].Value;
            }

            var variable = m.Groups[2].Value;
            if (variable.Equals("cGameName", StringComparison.OrdinalIgnoreCase))
            {
                return gameName ?? FromScript(script);
            }

            foreach (var assign in CodeLinesWith(script, variable))
            {
                if (
                    Regex.Match(
                        assign,
                        $@"\b{Regex.Escape(variable)}[ \t]*=[ \t]*""([^""\r\n]{{1,64}})""",
                        RegexOptions.IgnoreCase
                    ) is
                    { Success: true } a
                )
                {
                    return a.Groups[1].Value;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The cGameName the script actually uses. Tables often list alternatives commented out
    /// (<c>' Const cGameName = "afm_113"</c>), so only a line that isn't a comment counts.
    /// </summary>
    public static string? FromScript(string script)
    {
        foreach (var line in CodeLinesWith(script, "cGameName"))
        {
            if (GameNameRegex().Match(line) is { Success: true } m)
            {
                return m.Groups[1].Value;
            }
        }

        return null;
    }

    /// <summary>ROM names on (uncommented) script lines that mention an alias, e.g. <c>EnsureVPMAlias cGameName, "afm_113b"</c>.</summary>
    public static List<string> AliasTargetsIn(string script) =>
        CodeLinesWith(script, "alias")
            .Select(l => AliasRegex().Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// An uncommented line loads PinMAME. Originals built from a ROM table's script keep the old
    /// <c>' LoadVPM "01560000", "S11.VBS", 3.26</c> line commented out; that doesn't need a ROM.
    /// </summary>
    /// <remarks>
    /// Many originals also paste in all of controller.vbs, whose <c>Sub LoadVPM</c> and <c>Sub LoadController</c>
    /// create VPinMAME.Controller but only run when called: a definition, or a line inside one of those routines,
    /// doesn't count (Harry Potter's VR room calls <c>LoadController("EM")</c> and needs no ROM).
    /// </remarks>
    public static bool UsesPinMame(string script) =>
        CodeLinesAt(script, "LoadVPM")
            .Any(l =>
                PinMameRegex().IsMatch(l.code)
                && !LoaderDefinitionRegex().IsMatch(l.code)
                && !InLoaderRoutine(script, l.index)
            )
        || CodeLinesAt(script, "VPinMAME.Controller")
            .Any(l => PinMameRegex().IsMatch(l.code) && !InLoaderRoutine(script, l.index));

    /// <summary>
    /// The position sits inside controller.vbs's own loader routines (LoadVPM, LoadVPMALT, LoadController, LoadVPinMAME),
    /// which only do anything when the table calls them.
    /// </summary>
    static bool InLoaderRoutine(string text, int index)
    {
        var from = Math.Max(0, index - 64 * 1024);
        var start = RoutineStartRegex().Match(text, from, index - from);
        if (!start.Success)
        {
            return false;
        }

        var end = RoutineEndRegex().Match(text, from, index - from);
        if (end.Success && end.Index > start.Index)
        {
            return false; // the last routine before it has already ended
        }

        var name = start.Groups["name"].Value;
        return name.StartsWith("LoadVPM", StringComparison.OrdinalIgnoreCase)
            || name.Equals("LoadVPinMAME", StringComparison.OrdinalIgnoreCase)
            || name.Equals("LoadController", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The ROM the script hands PinMAME when it isn't cGameName: <c>.GameName = "swrds_l2"</c>, or
    /// <c>.GameName = cRomName</c> with <c>Const cRomName = "swrds_l2"</c>. Rethemes keep their own cGameName
    /// for the PuP pack and high scores and run another machine's ROM (Fallout runs Swords of Fury's).
    /// Null when the script passes cGameName itself, or no name can be read.
    /// </summary>
    public static string? ControllerRomFromScript(string script)
    {
        foreach (var line in CodeLinesWith(script, ".GameName"))
        {
            if (ControllerGameNameRegex().Match(line) is not { Success: true } m)
            {
                continue;
            }
            if (m.Groups["rom"].Success)
            {
                return m.Groups["rom"].Value;
            }

            var variable = m.Groups["var"].Value;
            if (variable.Equals("cGameName", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var assign = new Regex(
                $"\\b{Regex.Escape(variable)}[ \\t]*=[ \\t]*\"([A-Za-z0-9_]{{2,32}})\"",
                RegexOptions.IgnoreCase
            );
            foreach (var l in CodeLinesWith(script, variable))
            {
                if (assign.Match(l) is { Success: true } a)
                {
                    return a.Groups[1].Value;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The code part of each line containing <paramref name="word"/> (scripts end lines with \r\n or a
    /// lone \r): up to the word, nothing after a ' comment mark. A plain search finds the few lines
    /// first, so this stays fast over the hundreds of MB of a table file.
    /// </summary>
    static IEnumerable<string> CodeLinesWith(string text, string word) =>
        CodeLinesAt(text, word).Select(l => l.code);

    /// <summary><see cref="CodeLinesWith"/>, with where in the text the word was found.</summary>
    static IEnumerable<(int index, string code)> CodeLinesAt(string text, string word)
    {
        for (
            var i = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            i >= 0;
            i = text.IndexOf(word, i + word.Length, StringComparison.OrdinalIgnoreCase)
        )
        {
            var start = text.LastIndexOfAny(['\r', '\n'], i) + 1;
            var end = text.IndexOfAny(['\r', '\n'], i);
            if (end < 0)
            {
                end = text.Length;
            }
            // A "line" can run through binary data; only look nearby.
            start = Math.Max(start, i - 300);
            end = Math.Min(end, i + 300);
            var line = text[start..end];
            var comment = line.IndexOf('\'');
            if (comment >= 0 && comment < i - start)
            {
                continue; // the word is inside a comment
            }

            yield return (i, comment >= 0 ? line[..comment] : line);
        }
    }

    [GeneratedRegex("\\bcGameName[ \\t]*=[ \\t]*\"([A-Za-z0-9_]{2,32})\"", RegexOptions.IgnoreCase)]
    private static partial Regex GameNameRegex();

    /// <summary><c>Controller.GameName = "rom"</c> or <c>.GameName = variable</c> (inside <c>With Controller</c>).</summary>
    [GeneratedRegex(
        "\\.GameName[ \\t]*=[ \\t]*(?:\"(?<rom>[A-Za-z0-9_]{2,32})\"|(?<var>[A-Za-z_][A-Za-z0-9_]*))",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex ControllerGameNameRegex();

    [GeneratedRegex("alias[^\"]*\"([a-z0-9_]{2,32})\"", RegexOptions.IgnoreCase)]
    private static partial Regex AliasRegex();

    [GeneratedRegex("LoadVPM|VPinMAME\\.Controller", RegexOptions.IgnoreCase)]
    private static partial Regex PinMameRegex();

    /// <summary><c>Sub LoadVPM(VPMver, VBSfile, VBSver)</c>: the routine itself, not a call to it.</summary>
    [GeneratedRegex("\\b(?:Sub|Function)[ \\t]+LoadVPM", RegexOptions.IgnoreCase)]
    private static partial Regex LoaderDefinitionRegex();

    /// <summary>A line starting <c>Sub name</c> / <c>Function name</c>; searched backwards for the last one.</summary>
    [GeneratedRegex(
        "(?<=[\\r\\n])[ \\t]*(?:(?:Private|Public)[ \\t]+)?(?:Sub|Function)[ \\t]+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.RightToLeft
    )]
    private static partial Regex RoutineStartRegex();

    [GeneratedRegex(
        "(?<=[\\r\\n])[ \\t]*End[ \\t]+(?:Sub|Function)\\b",
        RegexOptions.IgnoreCase | RegexOptions.RightToLeft
    )]
    private static partial Regex RoutineEndRegex();

    [GeneratedRegex("PinUpPlayer\\.PinDisplay", RegexOptions.IgnoreCase)]
    private static partial Regex PupRegex();

    /// <summary><c>PuPlayer.Init pBackglass, "name"</c> or <c>PuPlayer.B2SInit "", variable</c>.</summary>
    [GeneratedRegex(
        "\\bPuPlayer\\.(?:B2S)?Init[ \\t(]+(?:[A-Za-z0-9_]+|\"[^\"\\r\\n]*\")[ \\t]*,[ \\t]*(?:\"([^\"\\r\\n]{1,64})\"|([A-Za-z_][A-Za-z0-9_]*))",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex PupInitRegex();

    [GeneratedRegex("\\busePUP[ \\t]*=[ \\t]*(True|False|1|0)\\b", RegexOptions.IgnoreCase)]
    private static partial Regex UsePupSwitchRegex();

    [GeneratedRegex("\"(?:FlexDMD\\.FlexDMD|UltraDMD\\.DMDObject)\"", RegexOptions.IgnoreCase)]
    private static partial Regex OwnDmdObjectRegex();

    [GeneratedRegex(
        "\\bUse(?:Flex|Ultra)DMD[ \\t]*=[ \\t]*(True|False|\\d)\\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex OwnDmdSwitchRegex();

    [GeneratedRegex(
        @"^(?:Const[ \t]+)?DMDMode[ \t]*=[ \t]*(?<value>\d+)[ \t]*'(?<comment>[^\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline
    )]
    private static partial Regex DmdModeRegex();
}

/// <param name="GameName">cGameName from the script, if found.</param>
/// <param name="UsesPinMame">The script loads PinMAME, so it needs a ROM.</param>
public sealed record VpxRomInfo(string? GameName, bool UsesPinMame)
{
    /// <summary>
    /// ROM names the script ties to an alias. When cGameName isn't a real PinMAME game (Mass Effect
    /// runs "afm_113b_me"), one of these is what it stands for; PinMAME learns that from alias.txt.
    /// </summary>
    public IReadOnlyList<string> AliasTargets { get; init; } = [];

    /// <summary>The ROM the script gives PinMAME when it isn't cGameName (see <see cref="RomDetector.ControllerRomFromScript"/>).</summary>
    public string? ControllerRom { get; init; }

    /// <summary>The ROM set PinMAME is asked to load: the controller's own name when the script sets one, else cGameName.</summary>
    public string? RomName => ControllerRom ?? GameName;

    /// <summary>The script creates PinUP Player (<c>PinUpPlayer.PinDisplay</c>), so it has a PuP pack.</summary>
    public bool UsesPup { get; init; }

    /// <summary>The pack folder it opens (pupvideos\&lt;name&gt;), when the script names it.</summary>
    public string? PupName { get; init; }

    /// <summary>The script has PuP turned off (<c>usePUP = False</c>): a pack won't show until it's set to True.</summary>
    public bool PupOff { get; init; }

    /// <summary>The script draws its own DMD with FlexDMD or UltraDMD (created, and not switched off).</summary>
    public bool UsesOwnDmd { get; init; }

    /// <summary>The script was read and it doesn't use PinMAME: no ROM needed, whatever cGameName says.</summary>
    public bool NoRomNeeded => GameName != null && !UsesPinMame;
}

/// <summary>
/// The machine script a table loads with <c>LoadVPM "…", "beav_butt.VBS", 3.50</c>. The usual ones (WPC.VBS,
/// gts3.vbs, sega.vbs …) come with VPX in its scripts folder; a custom ROM (Beavis and Butt-Head's beav_butt) can
/// need one of its own, offered on the ROM's download page. Without it the script stops at its first line that uses
/// it and VPX closes: the table can't be played or recorded.
/// </summary>
public static partial class MachineScript
{
    /// <summary>The .vbs file the table's script loads (or null when it loads none).</summary>
    public static string? Needed(string vpxPath)
    {
        var sidecar = VrRoomSwitch.ScriptPath(vpxPath);
        var script = File.Exists(sidecar)
            ? File.ReadAllText(sidecar, Encoding.Latin1)
            : RomAudio.Script(vpxPath);
        foreach (Match m in LoadVpmRegex().Matches(script))
        {
            var lineStart = script.LastIndexOfAny(['\r', '\n'], m.Index) + 1;
            if (script.AsSpan(lineStart, m.Index - lineStart).Contains('\''))
            {
                continue; // commented out
            }

            return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>
    /// The machine script the table loads when it's in neither of the places VPX looks (next to the table, VPX's
    /// scripts folder); null when it's there, the table loads none, or the table can't be read. Never throws.
    /// </summary>
    public static string? Missing(string vpxPath, AppSettings s)
    {
        try
        {
            if (Needed(vpxPath) is not { } name)
            {
                return null;
            }

            if (VpxSetup.FindExe(s) is not { } exe)
            {
                return null; // without VPX there's nothing to compare with
            }

            string[] dirs =
            [
                Path.GetDirectoryName(vpxPath)!,
                Path.Combine(s.VpxDir, "scripts"),
                Path.Combine(Path.GetDirectoryName(exe)!, "scripts"),
            ];
            return dirs.Any(d => File.Exists(Path.Combine(d, name))) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(
        "\\bLoadVPM\\b[^\\r\\n\"]*\"[^\"\\r\\n]*\"[ \\t]*,[ \\t]*\"([^\"\\r\\n]+\\.vbs)\"",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex LoadVpmRegex();
}

public static class RomSources
{
    /// <summary>
    /// A ROM entry that can actually be downloaded as a PinMAME ROM set. Stern's own site
    /// (sternpinball.com game_code) and *.bin entries are machine firmware updates, not ROMs.
    /// </summary>
    public static bool IsUsable(string url, string? romName)
    {
        if (romName != null && romName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return false;
        }

        if (u.Host.EndsWith("sternpinball.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !u.AbsolutePath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens a VPUniverse file search for a ROM the catalog can't download.</summary>
    public static string VpuSearchUrl(string romName) =>
        "https://vpuniverse.com/search/?q="
        + Uri.EscapeDataString(romName)
        + "&type=downloads_file";
}

/// <summary>Puts a downloaded ROM into a table's pinmame/roms folder.</summary>
public static class RomInstaller
{
    /// <summary>
    /// Handles the shapes ROM downloads come in: the ROM zip itself (any file name; it's saved as
    /// <paramref name="romName"/>.zip), or an archive with one or more ROM zips inside.
    /// Returns the installed file paths.
    /// </summary>
    public static List<string> Install(string downloaded, InstalledTable table, string romName)
    {
        var romsDir = Path.Combine(table.Folder, "pinmame", "roms");
        Directory.CreateDirectory(romsDir);
        var ext = Path.GetExtension(downloaded).ToLowerInvariant();

        if (ext == ".zip")
        {
            using var z = ZipFile.OpenRead(downloaded);
            var nested = z
                .Entries.Where(e => e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (nested.Count == 0)
            {
                // The download is the ROM.
                var dest = Path.Combine(romsDir, romName + ".zip");
                File.Copy(downloaded, dest, overwrite: true);
                return [dest];
            }
            return PlaceNested(
                nested
                    .Select(e => (e.Name, (Action<string>)(d => e.ExtractToFile(d, true))))
                    .ToList(),
                romsDir,
                romName
            );
        }

        if (ext is ".7z" or ".rar")
        {
            var tmp = Path.Combine(
                Path.GetTempPath(),
                "vrtm-rom-" + Guid.NewGuid().ToString("N")[..8]
            );
            try
            {
                TableInstaller.Extract(downloaded, tmp);
                var zips = Directory
                    .EnumerateFiles(tmp, "*.zip", SearchOption.AllDirectories)
                    .ToList();
                if (zips.Count == 0)
                {
                    throw new InvalidDataException("No ROM zip inside the download.");
                }

                return PlaceNested(
                    zips.Select(f =>
                            (Path.GetFileName(f), (Action<string>)(d => File.Copy(f, d, true)))
                        )
                        .ToList(),
                    romsDir,
                    romName
                );
            }
            finally
            {
                try
                {
                    Directory.Delete(tmp, true);
                }
                catch { }
            }
        }

        throw new InvalidDataException(
            $"Unexpected ROM download type: {Path.GetFileName(downloaded)}"
        );
    }

    /// <summary>
    /// Several downloads from one ROM page (every ROM version of a machine). All of them go into
    /// pinmame/roms under their own names: PinMAME "clone" versions only hold the files that differ
    /// and need their parent set beside them, so keeping the whole family is what makes the table's
    /// ROM load. A single download goes through <see cref="Install"/> (renamed to <paramref name="romName"/> if needed).
    /// </summary>
    public static List<string> InstallAll(
        IReadOnlyList<string> downloads,
        InstalledTable table,
        string romName
    )
    {
        if (downloads.Count == 1)
        {
            return Install(downloads[0], table, romName);
        }

        var romsDir = Path.Combine(table.Folder, "pinmame", "roms");
        Directory.CreateDirectory(romsDir);
        var placed = new List<string>();
        foreach (var file in downloads)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".zip")
            {
                using var z = ZipFile.OpenRead(file);
                var nested = z
                    .Entries.Where(e => e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (nested.Count == 0)
                {
                    var dest = Path.Combine(romsDir, Path.GetFileName(file));
                    File.Copy(file, dest, overwrite: true);
                    placed.Add(dest);
                }
                else
                {
                    foreach (var e in nested)
                    {
                        var dest = Path.Combine(romsDir, e.Name);
                        e.ExtractToFile(dest, true);
                        placed.Add(dest);
                    }
                }
            }
            else if (ext is ".7z" or ".rar")
            {
                var tmp = Path.Combine(
                    Path.GetTempPath(),
                    "vrtm-rom-" + Guid.NewGuid().ToString("N")[..8]
                );
                try
                {
                    TableInstaller.Extract(file, tmp);
                    foreach (
                        var inner in Directory.EnumerateFiles(
                            tmp,
                            "*.zip",
                            SearchOption.AllDirectories
                        )
                    )
                    {
                        var dest = Path.Combine(romsDir, Path.GetFileName(inner));
                        File.Copy(inner, dest, true);
                        placed.Add(dest);
                    }
                }
                finally
                {
                    try
                    {
                        Directory.Delete(tmp, true);
                    }
                    catch { }
                }
            }
        }
        if (placed.Count == 0)
        {
            throw new InvalidDataException("None of the downloads contained a ROM zip.");
        }

        return placed;
    }

    static List<string> PlaceNested(
        List<(string name, Action<string> save)> zips,
        string romsDir,
        string romName
    )
    {
        var match = zips.Where(z =>
                Path.GetFileNameWithoutExtension(z.name)
                    .Equals(romName, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        if (match.Count == 0 && zips.Count == 1)
        {
            match = [(romName + ".zip", zips[0].save)];
        }

        if (match.Count == 0)
        {
            throw new InvalidDataException(
                $"The download has {zips.Count} ROM zips but none called {romName}.zip."
            );
        }

        var placed = new List<string>();
        foreach (var (name, save) in match)
        {
            var dest = Path.Combine(romsDir, Path.GetFileName(name));
            save(dest);
            placed.Add(dest);
        }
        return placed;
    }
}
