namespace VrTableManager.Services;

/// <summary>Minimal INI editor that keeps comments and ordering intact.</summary>
public static class IniFile
{
    /// <summary>All values by (section, key), case-insensitive. Empty when the file doesn't exist.</summary>
    public static Dictionary<(string Section, string Key), string> Read(string path)
    {
        var result = new Dictionary<(string, string), string>(SectionKeyComparer.Instance);
        if (!File.Exists(path)) return result;
        var section = "";
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            var eq = line.IndexOf('=');
            if (eq > 0 && !line.StartsWith(';')) result[(section, line[..eq].Trim())] = line[(eq + 1)..].Trim();
        }
        return result;
    }

    sealed class SectionKeyComparer : IEqualityComparer<(string, string)>
    {
        public static readonly SectionKeyComparer Instance = new();
        public bool Equals((string, string) a, (string, string) b) =>
            StringComparer.OrdinalIgnoreCase.Equals(a.Item1, b.Item1) && StringComparer.OrdinalIgnoreCase.Equals(a.Item2, b.Item2);
        public int GetHashCode((string, string) k) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item1), StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item2));
    }

    public static void Set(string path, string section, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var header = $"[{section}]";
        var start = lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim() != "") lines.Add("");
            lines.Add(header);
            lines.Add($"{key} = {value}");
        }
        else
        {
            var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            var existing = -1;
            for (var i = start + 1; i < end; i++)
            {
                var eq = lines[i].IndexOf('=');
                if (eq > 0 && lines[i][..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) { existing = i; break; }
            }
            if (existing >= 0) lines[existing] = $"{key} = {value}";
            else lines.Insert(end, $"{key} = {value}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }
}
