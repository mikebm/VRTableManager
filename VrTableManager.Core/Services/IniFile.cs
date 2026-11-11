using System.Text;

namespace VrTableManager.Services;

/// <summary>Minimal INI editor that keeps comments and ordering intact.</summary>
public static class IniFile
{
    /// <summary>All values by (section, key), case-insensitive. Empty when the file doesn't exist.</summary>
    public static Dictionary<(string Section, string Key), string> Read(string path)
    {
        var result = new Dictionary<(string, string), string>(SectionKeyComparer.Instance);
        if (!File.Exists(path))
        {
            return result;
        }

        var section = "";
        foreach (var raw in Load(path, out _))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq > 0 && !line.StartsWith(';'))
            {
                result[(section, line[..eq].Trim())] = line[(eq + 1)..].Trim();
            }
        }
        return result;
    }

    internal sealed class SectionKeyComparer : IEqualityComparer<(string, string)>
    {
        public static readonly SectionKeyComparer Instance = new();

        public bool Equals((string, string) a, (string, string) b) =>
            StringComparer.OrdinalIgnoreCase.Equals(a.Item1, b.Item1)
            && StringComparer.OrdinalIgnoreCase.Equals(a.Item2, b.Item2);

        public int GetHashCode((string, string) k) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item1),
                StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item2)
            );
    }

    /// <summary>One section's keys and values in file order (empty when the file or section doesn't exist).</summary>
    public static List<(string Key, string Value)> ReadSection(string path, string section)
    {
        var result = new List<(string, string)>();
        if (!File.Exists(path))
        {
            return result;
        }

        var inside = false;
        foreach (var raw in Load(path, out _))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inside = line[1..^1].Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (inside && eq > 0 && !line.StartsWith(';'))
            {
                result.Add((line[..eq].Trim(), line[(eq + 1)..].Trim()));
            }
        }
        return result;
    }

    public static void Set(string path, string section, string key, string value) =>
        SetMany(path, section, [(key, value)]);

    /// <summary>Sets several keys of one section in a single read and write.</summary>
    public static void SetMany(
        string path,
        string section,
        IEnumerable<(string Key, string Value)> values
    )
    {
        var lines = Load(path, out var text);
        var header = $"[{section}]";
        var start = lines.FindIndex(l =>
            l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase)
        );
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim() != "")
            {
                lines.Add("");
            }

            lines.Add(header);
            start = lines.Count - 1;
        }
        foreach (var (key, value) in values)
        {
            var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0)
            {
                end = lines.Count;
            }

            var existing = -1;
            for (var i = start + 1; i < end; i++)
            {
                var eq = lines[i].IndexOf('=');
                if (eq > 0 && lines[i][..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    existing = i;
                    break;
                }
            }
            if (existing >= 0)
            {
                lines[existing] = $"{key} = {value}";
            }
            else
            {
                // Before any blank lines that separate this section from the next.
                var at = end;
                while (at > start + 1 && lines[at - 1].Trim() == "")
                {
                    at--;
                }

                lines.Insert(at, $"{key} = {value}");
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Save(path, lines, text);
    }

    /// <summary>How a file was written, so a rewrite keeps it: encoding (and byte-order mark), line ending, final newline.</summary>
    sealed record Text(Encoding Encoding, bool Bom, string NewLine, bool FinalNewLine);

    static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>
    /// The file's lines. UTF-8 when it is valid UTF-8; otherwise it's read byte for byte (Latin-1), so a file in the
    /// PC's own code page is written back exactly as it was, and its non-ASCII text isn't lost.
    /// </summary>
    static List<string> Load(string path, out Text text)
    {
        if (!File.Exists(path))
        {
            text = new Text(StrictUtf8, false, "\r\n", true);
            return [];
        }

        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var body = bom ? bytes.AsSpan(3) : bytes.AsSpan();
        Encoding encoding;
        string s;
        try
        {
            s = StrictUtf8.GetString(body);
            encoding = StrictUtf8;
        }
        catch (DecoderFallbackException)
        {
            s = Encoding.Latin1.GetString(body);
            encoding = Encoding.Latin1;
        }

        var newLine = s.Contains("\r\n") || !s.Contains('\n') ? "\r\n" : "\n";
        var final = s.Length == 0 || s.EndsWith('\n');
        var lines = s.ReplaceLineEndings("\n").Split('\n').ToList();
        if (final)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        text = new Text(encoding, bom, newLine, final || s.Length == 0);
        return lines;
    }

    /// <summary>
    /// Writes a copy beside the file, then swaps it in, so a crash or quit mid-write leaves the old file whole. A file
    /// that can't be replaced (read-only, open elsewhere) throws and is left as it was.
    /// </summary>
    static void Save(string path, List<string> lines, Text text)
    {
        var s = string.Join(text.NewLine, lines) + (text.FinalNewLine ? text.NewLine : "");
        var bytes = text.Bom
            ? [0xEF, 0xBB, 0xBF, .. text.Encoding.GetBytes(s)]
            : text.Encoding.GetBytes(s);
        var temp = path + ".vrtm-new";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch { }
            throw;
        }
    }
}
