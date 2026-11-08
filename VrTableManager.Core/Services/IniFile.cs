namespace VrTableManager.Services;

/// <summary>Minimal INI editor that keeps comments and ordering intact.</summary>
public static class IniFile
{
    /// <summary>All values by (section, key), case-insensitive. Empty when the file doesn't exist.</summary>
    public static Dictionary<(string Section, string Key), string> Read(string path)
    {
        var result = new Dictionary<(string, string), string>(SectionKeyComparer.Instance);
        if (!File.Exists(path))
            return result;
        var section = "";
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq > 0 && !line.StartsWith(';'))
                result[(section, line[..eq].Trim())] = line[(eq + 1)..].Trim();
        }
        return result;
    }

    sealed class SectionKeyComparer : IEqualityComparer<(string, string)>
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
            return result;
        var inside = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inside = line[1..^1].Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (inside && eq > 0 && !line.StartsWith(';'))
                result.Add((line[..eq].Trim(), line[(eq + 1)..].Trim()));
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
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var header = $"[{section}]";
        var start = lines.FindIndex(l =>
            l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase)
        );
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim() != "")
                lines.Add("");
            lines.Add(header);
            start = lines.Count - 1;
        }
        foreach (var (key, value) in values)
        {
            var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0)
                end = lines.Count;
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
                lines[existing] = $"{key} = {value}";
            else
            {
                // Before any blank lines that separate this section from the next.
                var at = end;
                while (at > start + 1 && lines[at - 1].Trim() == "")
                    at--;
                lines.Insert(at, $"{key} = {value}");
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }
}
