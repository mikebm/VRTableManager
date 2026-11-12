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
    /// While a pack is on its desktop layout for a desktop launch: that layout's name on the first line
    /// (<c>layout: …</c>), then the files the switch added, one per line. The files it replaced or set aside are in
    /// <see cref="DesktopBackup"/>, so the VR launch after puts the pack back exactly as it was.
    /// </summary>
    public const string DesktopMarker = ".vrtm-desktop";

    /// <summary>The pack's files the desktop switch replaced or set aside, kept until the next VR launch.</summary>
    public const string DesktopBackup = ".vrtm-desktop-backup";

    const string LayoutLine = "layout: ";

    /// <summary>
    /// Puts each pack in a table's pupvideos\ on the layout for this launch. Only a pack still on the layout this
    /// app chose (named in <see cref="Marker"/>, its files unchanged) is switched, so a layout picked or edited by
    /// hand stays. Desktop: the pack's one layout named Desktop and not VR, when there is one. VR: the pack as it
    /// was before the desktop switch. Returns "pack: layout" for each one switched. Never throws for a folder
    /// it can't read: the launch goes on with the pack as it is.
    /// </summary>
    public static List<string> SwitchFor(string pupDir, bool vr)
    {
        var switched = new List<string>();
        List<string> packs;
        try
        {
            packs = Directory.Exists(pupDir) ? PupPacks.PackRoots(pupDir) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return switched;
        }

        foreach (var pack in packs)
        {
            try
            {
                if ((vr ? BackToVr(pack) : ToDesktop(pack)) is { } name)
                {
                    switched.Add($"{Path.GetFileName(pack)}: {name}");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { /* leave it as it is; tried again next launch */
            }
        }
        return switched;
    }

    /// <summary>
    /// The layout a marker names: by its name, or by its folder's name. An older version named a layout after its
    /// folder when its .bat didn't mention a .pup file ("VR - 3 Screen"); that .bat is a layout of its own now
    /// ("5 VR - 3 Screen"), so the folder name must still find it.
    /// </summary>
    static Option? Named(List<Option> options, string name) =>
        options.FirstOrDefault(o => o.Source != null && o.Name == name)
        ?? options.FirstOrDefault(o =>
            o.Source != null
            && Path.GetFileName(Path.TrimEndingDirectorySeparator(o.Source))
                .Equals(name, StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>The layout this app put the pack on, when the pack's files still match it.</summary>
    static Option? ChosenLayoutInPlace(string pack, List<Option> options)
    {
        var markerFile = Path.Combine(pack, Marker);
        var chosen = File.Exists(markerFile) ? File.ReadAllText(markerFile).Trim() : "";
        if (chosen.Length == 0 || chosen == NoLayout)
        {
            return null;
        }

        var layout = Named(options, chosen);
        return layout != null && IsInPlace(layout, pack) ? layout : null;
    }

    /// <summary>
    /// Every file of the layout is in the pack, unchanged. The files its .bat writes with echo don't count: a
    /// layout folder may ship one (ScreenType.txt) that the echo then replaces.
    /// </summary>
    static bool IsInPlace(Option layout, string pack) =>
        LayoutFiles(layout.Source!)
            .Where(rel =>
                !layout.Echoes.Any(e => e.File.Equals(rel, StringComparison.OrdinalIgnoreCase))
            )
            .All(rel =>
                File.Exists(Path.Combine(pack, rel))
                && SameContent(Path.Combine(pack, rel), Path.Combine(layout.Source!, rel))
            );

    static string? ToDesktop(string pack)
    {
        if (File.Exists(Path.Combine(pack, DesktopMarker)))
        {
            return null;
        }

        var options = FindOptions(pack);
        var vrLayout = ChosenLayoutInPlace(pack, options);
        if (vrLayout == null)
        {
            return null;
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
            return null;
        }

        var source = desktop[0].Source!;
        var desktopFiles = LayoutFiles(source)
            .Concat(desktop[0].Echoes.Select(e => e.File))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var vrOnly = LayoutFiles(vrLayout.Source!)
            .Except(desktopFiles, StringComparer.OrdinalIgnoreCase)
            .Where(rel => File.Exists(Path.Combine(pack, rel)))
            .ToList();
        var added = desktopFiles.Where(rel => !File.Exists(Path.Combine(pack, rel))).ToList();
        // Written first: if what follows stops halfway, the next VR launch still puts back what it can.
        File.WriteAllLines(
            Path.Combine(pack, DesktopMarker),
            [LayoutLine + desktop[0].Name, .. added]
        );
        var backup = Path.Combine(pack, DesktopBackup);
        foreach (var rel in desktopFiles.Except(added, StringComparer.OrdinalIgnoreCase))
        {
            CopyInto(Path.Combine(pack, rel), Path.Combine(backup, rel));
        }
        // The VR layout's files the desktop one doesn't have (its PinUpPlayer.ini and the like) are set aside.
        foreach (var rel in vrOnly)
        {
            CopyInto(Path.Combine(pack, rel), Path.Combine(backup, rel));
            File.Delete(Path.Combine(pack, rel));
        }
        foreach (var rel in LayoutFiles(source))
        {
            CopyInto(Path.Combine(source, rel), Path.Combine(pack, rel));
        }
        WriteEchoes(desktop[0], pack, backUp: false);
        return desktop[0].Name;
    }

    static string? BackToVr(string pack)
    {
        var markerFile = Path.Combine(pack, DesktopMarker);
        if (!File.Exists(markerFile))
        {
            return null;
        }

        var lines = File.ReadAllLines(markerFile).ToList();
        var desktopName =
            lines.Count > 0 && lines[0].StartsWith(LayoutLine, StringComparison.Ordinal)
                ? lines[0][LayoutLine.Length..]
                : null;
        // The first version wrote only the added files, with no layout line and no backup.
        var added = desktopName != null ? lines.Skip(1).ToList() : lines;
        var backup = Path.Combine(pack, DesktopBackup);
        var options = FindOptions(pack);

        // Changed by hand while on the desktop layout: that's the user's layout now, kept as it is.
        var desktop = desktopName == null ? null : Named(options, desktopName);
        if (desktop != null && !IsInPlace(desktop, pack))
        {
            File.WriteAllText(Path.Combine(pack, Marker), NoLayout);
            Addons.DeleteFolder(backup);
            File.Delete(markerFile);
            return null;
        }

        var root = Path.GetFullPath(pack).TrimEnd('\\') + "\\";
        foreach (var rel in added)
        {
            var path = Path.GetFullPath(Path.Combine(pack, rel));
            if (
                rel.Length > 0
                && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && File.Exists(path)
            )
            {
                File.Delete(path);
            }
        }

        var vrName = File.Exists(Path.Combine(pack, Marker))
            ? File.ReadAllText(Path.Combine(pack, Marker)).Trim()
            : "";
        if (Directory.Exists(backup))
        {
            foreach (var file in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories))
            {
                CopyInto(file, Path.Combine(pack, Path.GetRelativePath(backup, file)));
            }
            Addons.DeleteFolder(backup);
        }
        else if (Named(options, vrName) is { } vrLayout)
        {
            CopyLayout(vrLayout.Source!, pack);
            WriteEchoes(vrLayout, pack, backUp: false);
        }
        File.Delete(markerFile);
        return vrName.Length > 0 ? vrName : "its layout before desktop play";
    }

    static void CopyInto(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
    }

    /// <summary>
    /// Sets up every pack in a table's pupvideos\ that hasn't been yet: packs installed before this app chose VR
    /// layouts, and packs copied in from another install. Returns "pack: layout" for each one switched.
    /// </summary>
    public static List<string> SetUpOnce(string pupDir)
    {
        var switched = new List<string>();
        List<string> packs;
        try
        {
            packs = Directory.Exists(pupDir) ? PupPacks.PackRoots(pupDir) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return switched; // a folder it can't read doesn't stop the launch
        }

        foreach (var pack in packs)
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
            WriteEchoes(vr[0], pack, backUp: true);
            applied = vr[0].Name;
        }
        else if (vr.Count == 0 && OwnFullDmdLayout(pack, options) is { } own)
        {
            CopyLayout(own.Source!, pack);
            WriteEchoes(own, pack, backUp: true);
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

    /// <summary>A layout: its name, the folder its files come from, and the files its .bat writes with echo.</summary>
    record Option(string Name, string? Source, IReadOnlyList<Echo> Echoes);

    /// <summary>
    /// A line like <c>echo 2 &gt; ScreenType.txt</c> in a layout's .bat: Darkest Dungeon's script reads the screen
    /// setup from that file, and the pack ships it as 99 ("not set up"), which shows a "run the batch file" message
    /// and its apron screens over the playfield. <see cref="Text"/> is what cmd writes: everything up to the &gt;.
    /// </summary>
    record Echo(string File, string Text);

    /// <summary>
    /// Writes the files a layout's .bat writes with echo, as cmd does (the text, then a line break). With
    /// <paramref name="backUp"/>, the pack's own copy is kept in <see cref="DefaultBackup"/> the first time.
    /// </summary>
    /// <param name="onlyOnce">Skip a file already backed up: it was written before, and has been changed since by hand.</param>
    static bool WriteEchoes(Option layout, string pack, bool backUp, bool onlyOnce = false)
    {
        var changed = false;
        foreach (var e in layout.Echoes)
        {
            var dest = Path.Combine(pack, e.File);
            var bytes = System.Text.Encoding.Latin1.GetBytes(e.Text + "\r\n");
            if (File.Exists(dest) && File.ReadAllBytes(dest).AsSpan().SequenceEqual(bytes))
            {
                continue;
            }

            var backup = Path.Combine(pack, DefaultBackup, e.File);
            if (onlyOnce && File.Exists(backup))
            {
                continue;
            }
            if (backUp && File.Exists(dest) && !File.Exists(backup))
            {
                CopyInto(dest, backup);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Packs this app put on a layout before it wrote the layout's echo files (Darkest Dungeon's ScreenType.txt
    /// stayed 99) get them now, when the layout's files are still the ones it copied and the pack isn't on its
    /// desktop layout. Returns "pack: layout" for each one changed. Never throws.
    /// </summary>
    public static List<string> WriteMissingEchoes(string pupDir)
    {
        var done = new List<string>();
        try
        {
            if (!Directory.Exists(pupDir))
            {
                return done;
            }

            foreach (var pack in PupPacks.PackRoots(pupDir))
            {
                if (File.Exists(Path.Combine(pack, DesktopMarker)))
                {
                    continue;
                }

                if (
                    ChosenLayoutInPlace(pack, FindOptions(pack)) is { Echoes.Count: > 0 } layout
                    && WriteEchoes(layout, pack, backUp: true, onlyOnce: true)
                )
                {
                    done.Add($"{Path.GetFileName(pack)}: {layout.Name}");
                }
            }
        }
        catch (Exception) { } // a pack it can't set up just plays as it is
        return done;
    }

    /// <summary>
    /// The plain echo lines of a .bat that runs straight through. A menu .bat (goto, choice, set /p) picks its
    /// lines at run time, so none of its echoes is taken. Targets with variables (%~dp0), device names (nul) or a
    /// path out of the pack are skipped, and so is <c>echo 1&gt;file</c>, which cmd reads as a redirect.
    /// </summary>
    static List<Echo> EchoesIn(string bat)
    {
        var list = new List<Echo>();
        if (MenuBatRegex().IsMatch(bat))
        {
            return list;
        }

        foreach (Match m in EchoLineRegex().Matches(bat))
        {
            var file = m.Groups["file"].Value.Trim().Replace('/', '\\');
            var text = m.Groups["text"].Value;
            if (
                file.Length == 0
                || file.Contains('%')
                || file.Contains(':')
                || Path.IsPathRooted(file)
                || file.Contains("..")
                || DeviceNameRegex().IsMatch(Path.GetFileName(file))
                || Programs.Contains(Path.GetExtension(file).ToLowerInvariant())
                || (text.Length > 0 && char.IsDigit(text[^1]))
            )
            {
                continue;
            }

            list.Add(new(file, text));
        }
        return list;
    }

    /// <summary>
    /// With no layout named for VR: the one layout whose FullDMD (screen 5, what the VR cabinet's DMD shows) is a
    /// display of its own. Desktop and two-screen layouts draw it onto the backglass instead
    /// (<c>5,FullDMD,…,"2,0,0,100,100"</c>), which leaves the cabinet DMD dark. Null when the pack's current
    /// layout already has its own FullDMD (or has no FullDMD at all), or when no layout or several do. A pack with
    /// no screens.pup of its own (its .pup files only in its layouts, as Futurama ships) needs one either way.
    /// </summary>
    static Option? OwnFullDmdLayout(string pack, List<Option> options)
    {
        var current = FullDmd(pack);
        if (current == true || (current == null && File.Exists(Path.Combine(pack, "screens.pup"))))
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
                var cols = CsvFields(line);
                if (cols.Count < 6 || cols[0].Trim() != "5")
                {
                    continue;
                }

                // "off" and the audio-only modes show nothing.
                if (cols[5].Trim().ToLowerInvariant() is "off" or "musiconly")
                {
                    return false;
                }

                var pos = cols.Count > 7 ? cols[7].Trim() : "";
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
            // "1 - Desktop.bat" copies PuP-Pack_Options\Desktop\* without naming a .pup file: one that copies a
            // layout folder in the options folder counts too.
            var name = Path.GetFileNameWithoutExtension(bat)!;
            var source = FolderNamed(name) ?? SourceIn(text, pack);
            if (
                !text.Contains(".pup", StringComparison.OrdinalIgnoreCase)
                && !(
                    source != null
                    && CopyCommandRegex().IsMatch(text)
                    && folders.Any(f =>
                        Path.GetFullPath(f)
                            .Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)
                    )
                )
            )
            {
                continue;
            }

            list.Add(new(name, source, EchoesIn(text)));
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
                list.Add(new(Path.GetFileName(f)!, f, []));
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
            // ".\screens-vr.pup" is the pack's own folder, not a layout: copying it onto itself fails.
            if (
                Directory.Exists(full)
                && !string.Equals(
                    Path.GetFullPath(full).TrimEnd('\\', '/'),
                    Path.GetFullPath(pack).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase
                )
            )
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

    /// <summary>A line of a .pup file split into fields: commas inside double quotes don't split, quotes come off.</summary>
    public static List<string> CsvFields(string line)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }
        fields.Add(field.ToString());
        return fields;
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

    /// <summary><c>echo 2 &gt; ScreenType.txt</c> (not <c>&gt;&gt;</c>, not echo off): the text up to &gt;, and the file.</summary>
    [GeneratedRegex(
        @"^[ \t]*@?echo[ \t]+(?<text>[^>\r\n]*)>[ \t]*""?(?<file>[^""\r\n<>|&]+?)""?[ \t]*\r?$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline
    )]
    private static partial Regex EchoLineRegex();

    [GeneratedRegex(
        @"^[ \t]*(?:goto|choice|set[ \t]+/p|:[A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.Multiline
    )]
    private static partial Regex MenuBatRegex();

    [GeneratedRegex(@"^(?:con|prn|aux|nul|com\d|lpt\d)(?:\.|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceNameRegex();

    [GeneratedRegex(
        @"^[ \t]*@?(?:xcopy|copy|robocopy)\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline
    )]
    private static partial Regex CopyCommandRegex();
}
