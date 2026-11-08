namespace VrTableManager.Services;

/// <summary>
/// Works out everything a VPX table's ROM needs, the way PinMAME will see it when the table starts:
/// <list type="bullet">
/// <item>the cGameName the script really uses (not one of the commented-out alternatives);</item>
/// <item>when that name is an alias (Mass Effect runs "afm_113b_me"), the real ROM set behind it,
/// written to the table's pinmame\alias.txt so PinMAME finds it;</item>
/// <item>parent sets a clone takes files from (afm_113b needs afm_113), when the set isn't complete without them.</item>
/// </list>
/// It also moves colorizations and AltSound into the folders VPX 10.8.1 reads (<see cref="AltPacks.FixLayout"/>).
/// Uses VPX's own PinMAME for the facts; without it (VPX not installed), only the script is used.
/// </summary>
public static class RomRequirements
{
    /// <summary>Bump when the check learns something new, so installed tables are checked again.</summary>
    public const int Version = 2;

    /// <summary>Updates the table's manifest (RomNames, RomParents, RomAlias). Returns true when anything changed.</summary>
    public static bool Update(InstalledTable t, AppSettings s)
    {
        if (!t.IsVpx || !File.Exists(t.TablePath))
        {
            return false;
        }

        var m = t.Manifest;
        // Only counts as checked when PinMAME could be asked; otherwise try again once VPX is installed.
        if (File.Exists(RomAudio.PinmameDll(s)))
        {
            m.RomCheck = Version;
        }

        var before = (string.Join(",", m.RomNames), string.Join(",", m.RomParents), m.RomAlias);
        AltPacks.FixLayout(t.Folder);

        var info = RomDetector.Inspect(t.TablePath);
        if (info.NoRomNeeded)
        {
            m.RomNames = [];
            m.RomParents = [];
            m.RomAlias = null;
            return Changed();
        }
        if (info.GameName is not { } name)
        {
            return Changed();
        }

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
        if (
            known.TryGetValue(name, out var direct)
            && !direct.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
        )
        {
            // PinMAME already resolves it through alias.txt (written on an earlier check).
            game = direct.Name;
            m.RomAlias = name;
        }
        else if (!known.ContainsKey(name))
        {
            // Not a real ROM set, so an alias. Prefer the target the alias name is built on ("afm_113b" for "afm_113b_me").
            var target = info
                .AliasTargets.Where(known.ContainsKey)
                .OrderByDescending(a =>
                    name.StartsWith(a, StringComparison.OrdinalIgnoreCase) ? a.Length : 0
                )
                .FirstOrDefault();
            if (target != null)
            {
                game = known[target].Name;
                m.RomAlias = name;
                WriteAlias(pinmameDir, name, game);
            }
        }

        m.RomNames = [game];
        m.RomParents = ParentsNeeded(s, pinmameDir, game);
        return Changed();

        bool Changed() =>
            before != (string.Join(",", m.RomNames), string.Join(",", m.RomParents), m.RomAlias);
    }

    /// <summary>
    /// After ROM files were added: is the set complete now, or does it still need a parent? Asks PinMAME
    /// only (milliseconds); the script was already read when the table was installed or checked.
    /// </summary>
    public static void RefreshParents(InstalledTable t, AppSettings s)
    {
        if (!t.IsVpx || t.Manifest.RomNames.Count != 1)
        {
            return;
        }

        var pinmameDir = Path.Combine(t.Folder, "pinmame");
        if (
            RomZip(pinmameDir, t.Manifest.RomNames[0])
            && PinmameInfo.Family(s, pinmameDir, t.Manifest.RomNames[0]).Count == 0
        )
        {
            return; // no PinMAME to ask: keep what's known
        }

        t.Manifest.RomParents = ParentsNeeded(s, pinmameDir, t.Manifest.RomNames[0]);
    }

    /// <summary>
    /// The parent sets a clone still needs. Only known once the clone's own zip is there: before that PinMAME
    /// can't find its files and reports it incomplete, which used to mark the parent (afm_113 for afm_113b) as
    /// needed for good, even after a complete afm_113b.zip was copied in by hand.
    /// </summary>
    static List<string> ParentsNeeded(AppSettings s, string pinmameDir, string game)
    {
        if (!RomZip(pinmameDir, game))
        {
            return [];
        }

        var family = PinmameInfo.Family(s, pinmameDir, game);
        return family.Count > 0 && !family[0].Found
            ? family.Skip(1).Select(g => g.Name).ToList()
            : [];
    }

    static bool RomZip(string pinmameDir, string rom) =>
        File.Exists(Path.Combine(pinmameDir, "roms", rom + ".zip"));

    /// <summary>Adds "alias,rom" to pinmame\alias.txt (VPX 10.8.1's PinMAME reads it from the table's pinmame folder).</summary>
    public static void WriteAlias(string pinmameDir, string alias, string rom)
    {
        var file = Path.Combine(pinmameDir, "alias.txt");
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
        var line = $"{alias},{rom}";
        var existing = lines.FindIndex(l =>
            l.Split(',', 2)[0].Trim().Equals(alias, StringComparison.OrdinalIgnoreCase)
        );
        if (
            existing >= 0
            && lines[existing].Trim().Equals(line, StringComparison.OrdinalIgnoreCase)
        )
        {
            return;
        }

        if (existing >= 0)
        {
            lines[existing] = line;
        }
        else
        {
            lines.Add(line);
        }

        Directory.CreateDirectory(pinmameDir);
        File.WriteAllLines(file, lines);
    }
}
