namespace VrTableManager.Services;

/// <summary>
/// Works out everything a VPX table's ROM needs, the way PinMAME will see it when the table starts:
/// <list type="bullet">
/// <item>the cGameName the script really uses (not one of the commented-out alternatives);</item>
/// <item>when that name is an alias (Mass Effect runs "afm_113b_me"), the real ROM set behind it,
/// written to the table's pinmame\alias.txt so PinMAME finds it;</item>
/// <item>parent sets a clone takes files from (afm_113b needs afm_113), when the set isn't complete without them.</item>
/// </list>
/// It also moves Serum colorizations from the old altcolor\ folder to serum\, where VPX 10.8.1 looks.
/// Uses VPX's own PinMAME for the facts; without it (VPX not installed), only the script is used.
/// </summary>
public static class RomRequirements
{
    /// <summary>Bump when the check learns something new, so installed tables are checked again.</summary>
    public const int Version = 1;

    /// <summary>Updates the table's manifest (RomNames, RomParents, RomAlias). Returns true when anything changed.</summary>
    public static bool Update(InstalledTable t, AppSettings s)
    {
        if (!t.IsVpx || !File.Exists(t.TablePath)) return false;
        var m = t.Manifest;
        // Only counts as checked when PinMAME could be asked; otherwise try again once VPX is installed.
        if (File.Exists(RomAudio.PinmameDll(s))) m.RomCheck = Version;
        var before = (string.Join(",", m.RomNames), string.Join(",", m.RomParents), m.RomAlias);
        MoveSerum(t.Folder);

        var info = RomDetector.Inspect(t.TablePath);
        if (info.NoRomNeeded)
        {
            m.RomNames = [];
            m.RomParents = [];
            m.RomAlias = null;
            return Changed();
        }
        if (info.GameName is not { } name) return Changed();

        var pinmameDir = Path.Combine(t.Folder, "pinmame");
        var dll = RomAudio.PinmameDll(s);
        var known = PinmameInfo.Query(dll, pinmameDir, [name, .. info.AliasTargets]);
        if (known.Count == 0)
        {
            // No PinMAME to ask: trust the script.
            m.RomNames = [name];
            return Changed();
        }

        var game = name;
        m.RomAlias = null;
        if (known.TryGetValue(name, out var direct) && !direct.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            // PinMAME already resolves it through alias.txt (written on an earlier check).
            game = direct.Name;
            m.RomAlias = name;
        }
        else if (!known.ContainsKey(name))
        {
            // Not a real ROM set, so an alias. Prefer the target the alias name is built on ("afm_113b" for "afm_113b_me").
            var target = info.AliasTargets.Where(known.ContainsKey)
                .OrderByDescending(a => name.StartsWith(a, StringComparison.OrdinalIgnoreCase) ? a.Length : 0)
                .FirstOrDefault();
            if (target != null)
            {
                game = known[target].Name;
                m.RomAlias = name;
                WriteAlias(pinmameDir, name, game);
            }
        }

        m.RomNames = [game];
        var family = PinmameInfo.Family(s, pinmameDir, game);
        m.RomParents = family.Count > 0 && !family[0].Found ? family.Skip(1).Select(g => g.Name).ToList() : [];
        return Changed();

        bool Changed() => before != (string.Join(",", m.RomNames), string.Join(",", m.RomParents), m.RomAlias);
    }

    /// <summary>
    /// After ROM files were added: is the set complete now, or does it still need a parent? Asks PinMAME
    /// only (milliseconds); the script was already read when the table was installed or checked.
    /// </summary>
    public static void RefreshParents(InstalledTable t, AppSettings s)
    {
        if (!t.IsVpx || t.Manifest.RomNames.Count != 1) return;
        var family = PinmameInfo.Family(s, Path.Combine(t.Folder, "pinmame"), t.Manifest.RomNames[0]);
        if (family.Count == 0) return; // no PinMAME to ask: keep what's known
        t.Manifest.RomParents = family[0].Found ? [] : family.Skip(1).Select(g => g.Name).ToList();
    }

    /// <summary>Adds "alias,rom" to pinmame\alias.txt (VPX 10.8.1's PinMAME reads it from the table's pinmame folder).</summary>
    public static void WriteAlias(string pinmameDir, string alias, string rom)
    {
        var file = Path.Combine(pinmameDir, "alias.txt");
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
        var line = $"{alias},{rom}";
        var existing = lines.FindIndex(l => l.Split(',', 2)[0].Trim().Equals(alias, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0 && lines[existing].Trim().Equals(line, StringComparison.OrdinalIgnoreCase)) return;
        if (existing >= 0) lines[existing] = line; else lines.Add(line);
        Directory.CreateDirectory(pinmameDir);
        File.WriteAllLines(file, lines);
    }

    /// <summary>Table downloads still ship colorizations as altcolor\&lt;rom&gt;\; VPX 10.8.1's Serum plugin reads serum\&lt;rom&gt;\.</summary>
    public static void MoveSerum(string tableFolder)
    {
        var alt = Path.Combine(tableFolder, "altcolor");
        if (!Directory.Exists(alt)) return;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(alt))
            {
                var dest = Path.Combine(tableFolder, "serum", Path.GetFileName(dir));
                if (Directory.Exists(dest)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                Directory.Move(dir, dest);
            }
            if (!Directory.EnumerateFileSystemEntries(alt).Any()) Directory.Delete(alt);
        }
        catch { /* a file in use: try again next time */ }
    }
}
