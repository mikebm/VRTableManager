using System.Text.RegularExpressions;

namespace VrTableManager.Services;

/// <summary>
/// VPX plays a script's <c>PlayMusic "StarWarsESB\0ESB01.ogg"</c> from the table folder's music\ (or its own
/// Music\), but some downloads put that folder right beside the .vpx (Star Wars - The Empire Strikes Back,
/// Hankin 1980), where VPX never looks: the table plays without its music. Such a folder goes into music\.
/// <para>
/// Only a folder the script's PlayMusic names as the start of its path and that holds nothing but audio is moved,
/// so a table's other folders (pinmame, PuP, altsound, a user\ script, audio it plays some other way) stay put.
/// </para>
/// </summary>
public static partial class TableMusic
{
    static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ogg",
        ".mp3",
        ".wav",
        ".flac",
    };

    /// <summary>Moves the script's music folders from beside the table into music\. Never throws.</summary>
    public static void FixLayout(string folder, string vpx)
    {
        try
        {
            var dirs = Directory
                .EnumerateDirectories(folder)
                .Where(d =>
                    !Path.GetFileName(d).Equals("music", StringComparison.OrdinalIgnoreCase)
                )
                .ToList();
            if (dirs.Count == 0)
            {
                return;
            }

            var script = RomAudio.Script(vpx);
            var named = PathStart()
                .Matches(script)
                .Select(m => m.Groups["dir"].Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                if (named.Contains(Path.GetFileName(dir)) && OnlyAudio(dir))
                {
                    Move(dir, Path.Combine(folder, "music", Path.GetFileName(dir)));
                }
            }
        }
        catch (Exception) { } // a folder in use, or a table file this can't read: it still installs and plays
    }

    static bool OnlyAudio(string dir)
    {
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
        return files.Count > 0 && files.All(f => AudioExts.Contains(Path.GetExtension(f)));
    }

    /// <summary>Into music\ (merged with what's there; a file already there is replaced by the table's).</summary>
    static void Move(string from, string to)
    {
        if (!Directory.Exists(to))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            Directory.Move(from, to);
            return;
        }

        foreach (
            var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList()
        )
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(file, dest, overwrite: true);
        }
        Directory.Delete(from, recursive: true);
    }

    // PlayMusic's own argument starting with a folder name and a backslash (or slash): Case 0:PlayMusic
    // "StarWarsESB\0ESB01.ogg", PlayMusic("Tunes/" & n & ".mp3"). Not on a commented-out line (no ' before it on
    // the line, which also passes over one after a string with an apostrophe in it: missing a folder is safe,
    // moving one that something else plays from isn't). Other strings, such as a media player's "Tunes\" path,
    // aren't VPX's music and stay where the table expects them.
    [GeneratedRegex(
        @"^[^'\r\n]*?\bPlayMusic\s*\(?\s*""(?<dir>[^""\\/:\r\n]+)[\\/]",
        RegexOptions.IgnoreCase | RegexOptions.Multiline
    )]
    private static partial Regex PathStart();
}
