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

    /// <summary>Where the pack's own files go before its VR layout is copied over them, so they can be put back.</summary>
    public const string DefaultBackup = ".default-layout";

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
            if (File.Exists(Path.Combine(pack, Marker)))
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
        try
        {
            File.WriteAllText(Path.Combine(pack, Marker), applied ?? "");
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

    static void CopyLayout(string source, string pack)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (Programs.Contains(Path.GetExtension(file).ToLowerInvariant()))
            {
                continue;
            }

            var rel = Path.GetRelativePath(source, file);
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

    /// <summary>"PuP-Pack_Options", "PuPPack Options", "Options".</summary>
    [GeneratedRegex(@"^(?:pup[-_ ]?pack[-_ ]?)?options?$", RegexOptions.IgnoreCase)]
    private static partial Regex OptionsFolderRegex();

    /// <summary>"VR" as a word of its own ("Option 4 - VR_or_Desktop", "VR.bat"), not "non-VR" or "no VR".</summary>
    [GeneratedRegex(@"(?<!non[-_ ]?|no[-_ ])(?<![a-z0-9])vr(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex VrNameRegex();

    [GeneratedRegex("\"([^\"\\r\\n]+)\"")]
    private static partial Regex QuotedPathRegex();
}
