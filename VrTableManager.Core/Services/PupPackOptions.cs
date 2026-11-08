using System.Text.RegularExpressions;

namespace VrTableManager.Services;

/// <summary>
/// What <see cref="PupPackOptions.SetUpForVr"/> found in a pack and did with it.
/// </summary>
/// <param name="Options">The layouts the pack offers (its option .bat files and PuP-Pack_Options folders).</param>
/// <param name="Applied">The VR layout copied over the pack's default, if there was exactly one.</param>
/// <param name="VrOptions">The layouts whose name says VR.</param>
/// <param name="ReadMes">The pack's own notes (readme, instructions) at its top level.</param>
public sealed record PupLayout(
    IReadOnlyList<string> Options,
    string? Applied,
    IReadOnlyList<string> VrOptions,
    IReadOnlyList<string> ReadMes
)
{
    public static readonly PupLayout None = new([], null, [], []);

    /// <summary>
    /// What to tell the user after the install ("" when there's nothing to say): the layout used, or, when the
    /// pack has layouts this app couldn't pick between, how to switch one by hand; and the pack's own notes.
    /// </summary>
    public string Note(string shownFolder)
    {
        static string List(IEnumerable<string> names) =>
            string.Join("; ", names.Select(n => $"\"{n}\""));
        var note = "";
        if (Applied != null)
        {
            note = $" Set up with the pack's VR layout ({List([Applied])}).";
        }
        else if (Options.Count > 0)
        {
            note =
                (
                    VrOptions.Count > 1
                        ? $" The pack has several VR layouts ({List(VrOptions)}), so it's left on its default one."
                        : $" The pack comes with layouts its own .bat files switch between ({List(Options)}). This app doesn't run them, so it's on the pack's default, which may not suit VR."
                )
                + $" If the backglass or DMD look wrong, open {shownFolder}, see which files the chosen layout's .bat copies (open it in Notepad), and copy them yourself, skipping any .exe or .bat.";
        }

        if (ReadMes.Count > 0)
        {
            note +=
                $" The pack's own notes: {string.Join(", ", ReadMes.Take(3))}{(ReadMes.Count > 3 ? "…" : "")} in {shownFolder}.";
        }

        return note;
    }
}

/// <summary>
/// Many PuP packs ship several layouts (backglass only, FullDMD, VR/desktop…) as option .bat files that copy a
/// PuP-Pack_Options\&lt;layout&gt;\ folder's screens.pup/triggers.pup over the pack's own. This app never runs
/// them. When exactly one layout is named for VR, its files are copied the way its .bat would, minus any programs
/// (desktop helpers such as PuPDesktopPunch.exe); otherwise the pack keeps its default and the user is told.
/// </summary>
public static partial class PupPackOptions
{
    /// <summary>Never copied out of a layout: programs and scripts the 10.8.1 plugin doesn't use.</summary>
    static readonly string[] Programs =
    [
        ".exe",
        ".bat",
        ".cmd",
        ".com",
        ".vbs",
        ".ps1",
        ".dll",
        ".msi",
        ".reg",
        ".lnk",
        ".js",
    ];
    static readonly string[] NoteFiles =
    [
        ".txt",
        ".pdf",
        ".rtf",
        ".md",
        ".doc",
        ".docx",
        ".htm",
        ".html",
    ];

    /// <summary>Left in a pack once it has been set up, so <see cref="SetUpOnce"/> doesn't redo it (or undo a layout picked by hand since).</summary>
    public const string Marker = ".vrtm-layout";

    /// <summary>In the marker when no layout was applied. Older versions left it empty.</summary>
    const string NoLayout = "(none)";

    /// <summary>
    /// Set up by an older version that applied no layout (an empty marker): checked once more, now that a layout
    /// with its own FullDMD can be picked. Its current layout must still draw the FullDMD elsewhere, so one
    /// picked by hand that shows it isn't touched.
    /// </summary>
    static bool NeedsRecheck(string pack)
    {
        try
        {
            return File.ReadAllText(Path.Combine(pack, Marker)).Trim().Length == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Where the pack's own files go before its VR layout is copied over them, so they can be put back.</summary>
    public const string DefaultBackup = ".default-layout";

    /// <summary>
    /// While a pack is on its desktop layout for a desktop launch: the files that switch added (one per line), so
    /// the VR launch after takes them out again. The VR layout itself is the one named in <see cref="Marker"/>.
    /// </summary>
    public const string DesktopMarker = ".vrtm-desktop";

    /// <summary>
    /// Puts each pack in a table's pupvideos\ on the layout for this launch. Only packs whose layout this app
    /// chose (named in <see cref="Marker"/>) are switched, so a layout picked by hand stays. Desktop: the pack's
    /// one layout named Desktop and not VR, when there is one and it isn't the VR layout. VR: the VR layout
    /// back, without the files the desktop one added. Returns "pack: layout" for each one switched.
    /// </summary>
    public static List<string> SwitchFor(string pupDir, bool vr)
    {
        var switched = new List<string>();
        if (!Directory.Exists(pupDir))
        {
            return switched;
        }

        foreach (var pack in PupPacks.PackRoots(pupDir))
        {
            try
            {
                var markerFile = Path.Combine(pack, Marker);
                var chosen = File.Exists(markerFile) ? File.ReadAllText(markerFile).Trim() : "";
                if (chosen.Length == 0 || chosen == NoLayout)
                {
                    continue;
                }

                var options = FindOptions(pack);
                var vrLayout = options.FirstOrDefault(o => o.Source != null && o.Name == chosen);
                var desktopFile = Path.Combine(pack, DesktopMarker);
                if (vrLayout == null)
                {
                    continue;
                }

                if (vr)
                {
                    if (!File.Exists(desktopFile))
                    {
                        continue;
                    }

                    foreach (var rel in File.ReadAllLines(desktopFile))
                    {
                        var added = Path.GetFullPath(Path.Combine(pack, rel));
                        if (
                            rel.Length > 0
                            && added.StartsWith(
                                Path.GetFullPath(pack).TrimEnd('\\') + "\\",
                                StringComparison.OrdinalIgnoreCase
                            )
                            && File.Exists(added)
                        )
                        {
                            File.Delete(added);
                        }
                    }
                    CopyLayout(vrLayout.Source!, pack);
                    File.Delete(desktopFile);
                    switched.Add($"{Path.GetFileName(pack)}: {vrLayout.Name}");
                    continue;
                }

                if (File.Exists(desktopFile))
                {
                    continue;
                }

                var desktop = options
                    .Where(o =>
                        o.Source != null
                        && DesktopNameRegex().IsMatch(o.Name)
                        && !VrNameRegex().IsMatch(o.Name)
                    )
                    .ToList();
                if (desktop.Count != 1 || desktop[0].Name == vrLayout.Name)
                {
                    continue;
                }

                var added2 = LayoutFiles(desktop[0].Source!)
                    .Where(rel => !File.Exists(Path.Combine(pack, rel)))
                    .ToList();
                // Written first: if the copy stops halfway, the next VR launch still puts the VR layout back.
                File.WriteAllLines(desktopFile, added2);
                CopyLayout(desktop[0].Source!, pack);
                switched.Add($"{Path.GetFileName(pack)}: {desktop[0].Name}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { /* leave it as it is; tried again next launch */
            }
        }
        return switched;
    }

    /// <summary>
    /// Sets up every pack in a table's pupvideos\ that hasn't been yet: packs installed before this app chose VR
    /// layouts, and packs copied in from another install. Returns "pack: layout" for each one switched.
    /// </summary>
    public static List<string> SetUpOnce(string pupDir)
    {
        var switched = new List<string>();
        if (!Directory.Exists(pupDir))
        {
            return switched;
        }

        foreach (var pack in PupPacks.PackRoots(pupDir))
        {
            if (File.Exists(Path.Combine(pack, Marker)) && !NeedsRecheck(pack))
            {
                continue;
            }

            try
            {
                if (SetUpForVr(pack).Applied is { } applied)
                {
                    switched.Add($"{Path.GetFileName(pack)}: {applied}");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { /* leave it as it is; tried again next launch */
            }
        }
        return switched;
    }

    public static PupLayout SetUpForVr(string pack)
    {
        if (!Directory.Exists(pack))
        {
            return PupLayout.None;
        }

        var options = FindOptions(pack);
        var vr = options.Where(o => o.Source != null && VrNameRegex().IsMatch(o.Name)).ToList();
        string? applied = null;
        if (vr.Count == 1)
        {
            CopyLayout(vr[0].Source!, pack);
            applied = vr[0].Name;
        }
        else if (vr.Count == 0 && OwnFullDmdLayout(pack, options) is { } own)
        {
            CopyLayout(own.Source!, pack);
            applied = own.Name;
        }
        try
        {
            File.WriteAllText(Path.Combine(pack, Marker), applied ?? NoLayout);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var readMes = Directory
            .EnumerateFiles(pack)
            .Where(f => NoteFiles.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => Path.GetFileName(f)!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new(
            options.Select(o => o.Name).ToList(),
            applied,
            vr.Select(o => o.Name).ToList(),
            readMes
        );
    }

    record Option(string Name, string? Source);

    /// <summary>
    /// With no layout named for VR: the one layout whose FullDMD (screen 5, what the VR cabinet's DMD shows) is a
    /// display of its own. Desktop and two-screen layouts draw it onto the backglass instead
    /// (<c>5,FullDMD,…,"2,0,0,100,100"</c>), which leaves the cabinet DMD dark. Null when the pack's current
    /// layout already has its own FullDMD, or when no layout or several do.
    /// </summary>
    static Option? OwnFullDmdLayout(string pack, List<Option> options)
    {
        if (FullDmd(pack) is not false)
        {
            return null;
        }

        var own = options.Where(o => o.Source != null && FullDmd(o.Source) == true).ToList();
        return own.Count == 1 ? own[0] : null;
    }

    /// <summary>
    /// From a layout's screens.pup: true when screen 5 is shown on its own, false when it's off or drawn onto
    /// another screen, null when there's no screens.pup or no screen 5 in it.
    /// </summary>
    public static bool? FullDmd(string dir)
    {
        try
        {
            var file = Path.Combine(dir, "screens.pup");
            if (!File.Exists(file))
            {
                return null;
            }

            foreach (var line in File.ReadLines(file).Take(200))
            {
                // ScreenNum,ScreenDes,PlayList,PlayFile,Loopit,Active,Priority,CustomPos (quoted, with commas)
                var cols = line.Split(',', 8);
                if (cols.Length < 6 || cols[0].Trim() != "5")
                {
                    continue;
                }

                if (cols[5].Trim().Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var pos = cols.Length > 7 ? cols[7].Trim().Trim('"').Trim() : "";
                var on = pos.Split(',')[0].Trim();
                return on.Length == 0 || on == "5";
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The pack's layouts: .bat files at its top that copy .pup files (EditThisPuPPack.bat and the like don't),
    /// and the folders in its options folder. A layout's files are the folder of the same name, or the folder
    /// its .bat copies from.
    /// </summary>
    static List<Option> FindOptions(string pack)
    {
        var folders = Directory
            .EnumerateDirectories(pack)
            .Where(d => OptionsFolderRegex().IsMatch(Path.GetFileName(d)!))
            .SelectMany(Directory.EnumerateDirectories)
            .ToList();
        string? FolderNamed(string name) =>
            folders.FirstOrDefault(f =>
                Path.GetFileName(f)!.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)
            );

        var list = new List<Option>();
        foreach (
            var bat in Directory
                .EnumerateFiles(pack)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".bat" or ".cmd")
                .Order(StringComparer.OrdinalIgnoreCase)
        )
        {
            string text;
            try
            {
                text = File.ReadAllText(bat);
            }
            catch (IOException)
            {
                continue;
            }
            if (!text.Contains(".pup", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(bat)!;
            list.Add(new(name, FolderNamed(name) ?? SourceIn(text, pack)));
        }
        foreach (var f in folders.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (
                !list.Any(o =>
                    o.Source != null
                    && Path.GetFullPath(o.Source)
                        .Equals(Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                list.Add(new(Path.GetFileName(f)!, f));
            }
        }

        return list;
    }

    /// <summary>The folder inside the pack a .bat copies from: <c>xcopy /y "PuP-Pack_Options\Option 4 - VR\*.pup"</c>.</summary>
    static string? SourceIn(string bat, string pack)
    {
        foreach (Match m in QuotedPathRegex().Matches(bat))
        {
            var dir = Path.GetDirectoryName(m.Groups[1].Value.Replace('/', '\\'));
            if (string.IsNullOrEmpty(dir) || Path.IsPathRooted(dir) || dir.Contains(".."))
            {
                continue;
            }

            var full = Path.Combine(pack, dir);
            if (Directory.Exists(full))
            {
                return full;
            }
        }
        return null;
    }

    /// <summary>A layout's files, relative to it, without the programs (never copied).</summary>
    static IEnumerable<string> LayoutFiles(string source) =>
        Directory
            .EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Where(f => !Programs.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => Path.GetRelativePath(source, f));

    static void CopyLayout(string source, string pack)
    {
        foreach (var rel in LayoutFiles(source).ToList())
        {
            var file = Path.Combine(source, rel);
            var dest = Path.Combine(pack, rel);
            // The pack's own file is kept the first time it's replaced (a later run finds the backup already there).
            var backup = Path.Combine(pack, DefaultBackup, rel);
            if (File.Exists(dest) && !File.Exists(backup) && !SameContent(dest, file))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(dest, backup);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    static bool SameContent(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length
        && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    /// <summary>A pack's folder of layouts: "PuP-Pack_Options", "PuPPack Options", "Options".</summary>
    public static bool IsOptionsFolder(string name) => OptionsFolderRegex().IsMatch(name);

    /// <summary>"PuP-Pack_Options", "PuPPack Options", "Options".</summary>
    [GeneratedRegex(@"^(?:pup[-_ ]?pack[-_ ]?)?options?$", RegexOptions.IgnoreCase)]
    private static partial Regex OptionsFolderRegex();

    /// <summary>"VR" as a word of its own ("Option 4 - VR_or_Desktop", "VR.bat"), not "non-VR" or "no VR".</summary>
    [GeneratedRegex(@"(?<!non[-_ ]?|no[-_ ])(?<![a-z0-9])vr(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex VrNameRegex();

    /// <summary>"Desktop" as a word of its own ("Option 3 - Desktop").</summary>
    [GeneratedRegex(@"(?<![a-z0-9])desktop(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex DesktopNameRegex();

    [GeneratedRegex("\"([^\"\\r\\n]+)\"")]
    private static partial Regex QuotedPathRegex();
}
