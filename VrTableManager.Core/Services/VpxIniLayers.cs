using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace VrTableManager.Services;

/// <summary>
/// VPX reads one settings file per run, but players want to keep tuning their own <c>VPinballX.ini</c> and still
/// have the manager's VR and desktop changes on top. So each launch gets a file built from layers:
/// <list type="number">
/// <item>the player's own <c>VPinballX.ini</c> (the one VPX itself would load; read fresh every launch, and written
/// only with the VR graphics switched off, when it holds the graphics VR play uses),</item>
/// <item>the overrides for the mode: <c>VrTableManager-VR.ini</c> or <c>VrTableManager-Desktop.ini</c> beside VPX.
/// Only keys with a value count; VPX writes an empty value for anything left at its default, so the VR file from
/// before this change works as an override file as it is. A setting the file had a value for that's put back to VPX's
/// default during play in that mode is noted in the file's own section, so the default wins there
/// (<see cref="DefaultsSection"/>).</item>
/// </list>
/// The result goes to <c>…-VR.play.ini</c> / <c>…-Desktop.play.ini</c>, which VPX runs with (<c>-Ini</c>). Whatever
/// VPX changed in it during play (the in-game menu, lining the table up in VR) is saved into that mode's overrides
/// once the table closes, since the play file is rebuilt next time. The exception is VR play with the VR graphics
/// switched off: its graphics changes go into the player's own <c>VPinballX.ini</c>, as they would outside the manager.
/// </summary>
public static class VpxIniLayers
{
    /// <summary>VPX bookkeeping, not settings: never kept as overrides.</summary>
    static readonly string[] NotSettings = ["Version", "RecentDir"];

    public static string OverridePath(AppSettings s, bool vr) =>
        vr ? VpxSetup.VrIniPath(s) : VpxSetup.DesktopIniPath(s);

    public static string PlayPath(AppSettings s, bool vr) =>
        Path.ChangeExtension(OverridePath(s, vr), ".play.ini");

    /// <summary>
    /// The settings file VPX loads when it's started without <c>-Ini</c>: <c>VPinballX.ini</c> in its preferences
    /// folder (%APPDATA%\VPinballX\&lt;major.minor&gt;), else the one beside VPinballX.exe. Null when neither exists.
    /// </summary>
    public static string? BasePath(AppSettings s)
    {
        var prefs = AppFolders.VpxPrefs;
        var version =
            VpxSetup.InstalledVersion(s) is { } v
            && System.Text.RegularExpressions.Regex.Match(v, @"(\d+)\.(\d+)") is { Success: true } m
                ? $"{m.Groups[1].Value}.{m.Groups[2].Value}"
                : null;
        var candidates = new List<string>();
        if (version != null)
        {
            candidates.Add(Path.Combine(prefs, version, "VPinballX.ini"));
        }
        else if (Directory.Exists(prefs))
        {
            candidates.AddRange(
                Directory
                    .EnumerateDirectories(prefs)
                    // By version, not as text: "10.10" is newer than "10.8".
                    .OrderByDescending(d =>
                        Version.TryParse(Path.GetFileName(d), out var folderVersion)
                            ? folderVersion
                            : new Version(0, 0)
                    )
                    .Select(d => Path.Combine(d, "VPinballX.ini"))
            );
        }

        candidates.Add(Path.Combine(s.VpxDir, "VPinballX.ini"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>The value VPX will use for each key in this mode: the player's own, then the mode's overrides.</summary>
    public static Dictionary<(string Section, string Key), string> Effective(AppSettings s, bool vr)
    {
        var result = BasePath(s) is { } b ? IniFile.Read(b) : IniFile.Read("");
        foreach (var (k, v) in Overrides(s, vr))
        {
            result[k] = v;
        }

        return result;
    }

    /// <summary>
    /// The mode's overrides. With the VR graphics switched off (<see cref="AppSettings.UseVrGraphics"/>), VR leaves
    /// out the graphics panel's keys, so the player's own VPinballX.ini decides them; they stay in the file.
    /// </summary>
    static IEnumerable<KeyValuePair<(string Section, string Key), string>> Overrides(
        AppSettings s,
        bool vr
    )
    {
        var file = IniFile.Read(OverridePath(s, vr));
        bool Counts((string Section, string Key) k) =>
            !NotSettings.Contains(k.Section, StringComparer.OrdinalIgnoreCase)
            && !k.Section.Equals(DefaultsSection, StringComparison.OrdinalIgnoreCase)
            && (!vr || s.UseVrGraphics || !VpxGraphics.IsGraphicsKey(k));
        foreach (var kv in file)
        {
            if (kv.Value != "" && Counts(kv.Key))
            {
                yield return kv;
            }
        }

        // Set back to VPX's default in this mode: empty, over what the player's own file has. A value set since wins.
        foreach (var k in DefaultKeys(file))
        {
            if (Counts(k) && string.IsNullOrEmpty(file.GetValueOrDefault(k)))
            {
                yield return new(k, "");
            }
        }
    }

    /// <summary>
    /// The override file's own section listing settings it had a value for that were put back to VPX's default during
    /// play in that mode, as <c>Section/Key = 1</c>. Any write of the setting by the manager (<see cref="SetOverrides"/>)
    /// takes the note away; so does deleting its line. An empty value is no override (VPX writes one for everything at its default), so
    /// without this a setting the player's VPinballX.ini has a value for couldn't be put back to the default there.
    /// VPX ignores the section, and it never reaches the play file.
    /// </summary>
    const string DefaultsSection = "VrTableManager.Defaults";

    static string DefaultsKey(string section, string key) => section + "/" + key;

    /// <summary>
    /// Writes settings into an override file as the manager means them: a value is an override, and an empty value is
    /// none, so the player's own VPinballX.ini decides again. A note that the setting was put back to VPX's default
    /// during play (<see cref="DefaultsSection"/>) goes with either; otherwise a reset would leave it at the default.
    /// </summary>
    public static void SetOverrides(
        string overridePath,
        string section,
        IEnumerable<(string Key, string Value)> values
    )
    {
        var list = values.ToList();
        IniFile.SetMany(overridePath, section, list);
        var noted = IniFile
            .ReadSection(overridePath, DefaultsSection)
            .Where(d => d.Value != "")
            .Select(d => d.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cleared = list.Select(v => DefaultsKey(section, v.Key))
            .Where(noted.Contains)
            .Select(k => (k, ""))
            .ToList();
        if (cleared.Count > 0)
        {
            IniFile.SetMany(overridePath, DefaultsSection, cleared);
        }
    }

    /// <summary>The settings this override file puts back to VPX's default (<see cref="DefaultsSection"/>) and hasn't given a value since.</summary>
    internal static HashSet<(string Section, string Key)> SetToDefault(string overridePath)
    {
        var file = IniFile.Read(overridePath);
        return DefaultKeys(file)
            .Where(k => string.IsNullOrEmpty(file.GetValueOrDefault(k)))
            .ToHashSet(IniFile.SectionKeyComparer.Instance);
    }

    static IEnumerable<(string Section, string Key)> DefaultKeys(
        Dictionary<(string Section, string Key), string> file
    ) =>
        file.Where(kv =>
                kv.Key.Section.Equals(DefaultsSection, StringComparison.OrdinalIgnoreCase)
                && kv.Value == "1"
                && kv.Key.Key.IndexOf('/') > 0
            )
            .Select(kv =>
            {
                var at = kv.Key.Key.IndexOf('/');
                return (kv.Key.Key[..at], kv.Key.Key[(at + 1)..]);
            });

    /// <summary>
    /// What a table does when its play is saved back (<see cref="Build"/>). <see cref="NotKept"/> runs first and
    /// names more keys never to keep as overrides (read when the table closes, so it sees what VPX saved for the
    /// table during play); <see cref="After"/> runs once the overrides are saved, with the play file as VPX started
    /// and left it. Each may throw without stopping the other or the overrides being saved; the error is logged.
    /// </summary>
    public sealed record AfterPlay(
        Func<IEnumerable<(string Section, string Key)>>? NotKept = null,
        Func<
            Dictionary<(string Section, string Key), string>,
            Dictionary<(string Section, string Key), string>,
            string?
        >? After = null
    );

    /// <summary>Writes the layered settings for this mode to <paramref name="path"/> (the play file by default).</summary>
    /// <param name="extra">Settings for this launch only, on top of the overrides; not saved back unless VPX changes them.</param>
    /// <param name="notKept">Keys of <paramref name="extra"/> that are never saved back, even if VPX changes them (one table's own value).</param>
    /// <param name="afterPlay">The table's own part of saving back (<see cref="AfterPlay"/>).</param>
    public static Play Build(
        AppSettings s,
        bool vr,
        string? path = null,
        IEnumerable<(string Section, string Key, string Value)>? extra = null,
        IEnumerable<(string Section, string Key)>? notKept = null,
        AfterPlay? afterPlay = null
    )
    {
        path ??= PlayPath(s, vr);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Starting from the player's file keeps its layout and comments; overrides go in section by section.
        if (BasePath(s) is { } b)
        {
            File.Copy(b, path, overwrite: true);
        }
        else
        {
            File.WriteAllText(path, "");
        }

        var overrides = Overrides(s, vr).ToList();
        foreach (
            var section in overrides.GroupBy(kv => kv.Key.Section, StringComparer.OrdinalIgnoreCase)
        )
        {
            IniFile.SetMany(path, section.Key, section.Select(kv => (kv.Key.Key, kv.Value)));
        }

        var launch = Forced(s, vr).Concat(extra ?? []).ToList();
        foreach (var section in launch.GroupBy(e => e.Section, StringComparer.OrdinalIgnoreCase))
        {
            IniFile.SetMany(path, section.Key, section.Select(e => (e.Key, e.Value)));
        }

        // The keys this launch plays with the override's own value (not one for this launch only): only those can be
        // put back to VPX's default by the player as far as the override file is concerned (see SaveBack).
        var launchOnly = launch
            .Select(e => (e.Section, e.Key))
            .ToHashSet(IniFile.SectionKeyComparer.Instance);
        var overridden = overrides
            .Where(kv => kv.Value != "" && !launchOnly.Contains(kv.Key))
            .Select(kv => kv.Key)
            .ToHashSet(IniFile.SectionKeyComparer.Instance);

        return new Play(
            path,
            OverridePath(s, vr),
            IniFile.Read(path),
            [.. PerTableKeys, .. notKept ?? []],
            afterPlay,
            // VR graphics switched off: the play file has the player's own graphics, so a change made in VPX's menu
            // goes back into their VPinballX.ini, as it would outside the manager, and not into the panel's settings
            // (it would replace them when switched back on).
            graphicsToOwn: vr && !s.UseVrGraphics,
            ownPath: BasePath(s),
            overridden: overridden,
            launchOnly: launchOnly
        );
    }

    /// <summary>
    /// Settings the manager's own options put on top for every launch in this mode. They're kept out of the override
    /// file, so turning an option off leaves the setting to VPX again. VR play's bloom is in the VR graphics panel.
    /// </summary>
    static IEnumerable<(string Section, string Key, string Value)> Forced(AppSettings s, bool vr)
    {
        if (!vr && s.DesktopBloomOff)
        {
            yield return ("Player", "ForceBloomOff", "1");
        }
    }

    /// <summary>
    /// Set for one table's launch and never kept, even if VPX writes them back changed: the PinUP Player folder is
    /// that table's own folder (<see cref="Launcher.PupRootFor"/>), and kept it would point every table there.
    /// </summary>
    static readonly (string Section, string Key)[] PerTableKeys = [("Plugin.PUP", "PUPFolder")];

    /// <summary>One launch's play file and what it held when VPX started.</summary>
    public sealed class Play(
        string path,
        string overridePath,
        Dictionary<(string Section, string Key), string> before,
        (string Section, string Key)[] notKept,
        AfterPlay? afterPlay = null,
        bool graphicsToOwn = false,
        string? ownPath = null,
        IReadOnlySet<(string Section, string Key)>? overridden = null,
        IReadOnlySet<(string Section, string Key)>? launchOnly = null
    )
    {
        public string Path { get; } = path;
        readonly Lock _gate = new();
        bool _saved;

        readonly List<string> _notes = [];

        /// <summary>What the table's own part of saving back did or couldn't do, for the play log; each note once.</summary>
        public List<string> TakeNotes()
        {
            lock (_gate)
            {
                var notes = _notes.ToList();
                _notes.Clear();
                return notes;
            }
        }

        /// <summary>
        /// Keeps what VPX changed during play as overrides for this mode. Runs once, however often it's called (when
        /// the table closes, and again by a caller that waited for it); a second caller waits until the first is done,
        /// so what it reads next is saved. Returns how many settings were kept as overrides; graphics changes saved
        /// into the player's own file (<c>graphicsToOwn</c>) are reported in <see cref="TakeNotes"/> instead.
        /// </summary>
        public int SaveBack()
        {
            lock (_gate)
            {
                if (_saved)
                {
                    return 0;
                }

                _saved = true;
                var after = IniFile.Read(Path);
                List<(string Section, string Key)> skip = [.. notKept];
                if (afterPlay?.NotKept is { } more)
                {
                    try
                    {
                        skip.AddRange(more());
                    }
                    catch (Exception e)
                    {
                        _notes.Add("couldn't read the table's own settings: " + e.Message);
                    }
                }

                var changed = Changes(before, after)
                    .Where(c =>
                        !skip.Any(k =>
                            k.Section.Equals(c.Section, StringComparison.OrdinalIgnoreCase)
                            && k.Key.Equals(c.Key, StringComparison.OrdinalIgnoreCase)
                        )
                    )
                    // A value set for this launch only (Forced, extra) that VPX emptied because it's VPX's default
                    // isn't a change of the player's: it mustn't empty what the override file holds for that key.
                    .Where(c =>
                        !(c.Value == "" && launchOnly?.Contains((c.Section, c.Key)) == true)
                    )
                    .ToList();
                var toOwn = graphicsToOwn
                    ? changed.Where(c => VpxGraphics.IsGraphicsKey((c.Section, c.Key))).ToList()
                    : [];
                var toOverrides = changed.Except(toOwn).ToList();
                foreach (
                    var section in toOverrides.GroupBy(
                        c => c.Section,
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                {
                    // Clears any note for these keys (DefaultsSection); a reset to the default is noted again below.
                    SetOverrides(overridePath, section.Key, section.Select(c => (c.Key, c.Value)));
                }

                // A setting the override gave a value of its own, which VPX now leaves empty: the player put it back
                // to VPX's default in this mode. Noted, so the default keeps winning over the player's own file
                // (DefaultsSection). Only those: VPX also empties a value it finds at its default, so one from the
                // player's own file or set for this launch only (Forced, extra) emptied isn't a choice to keep.
                var notes = toOverrides
                    .Where(c => c.Value == "" && overridden?.Contains((c.Section, c.Key)) == true)
                    .Select(c => (DefaultsKey(c.Section, c.Key), "1"))
                    .ToList();
                if (notes.Count > 0)
                {
                    IniFile.SetMany(overridePath, DefaultsSection, notes);
                }

                // Last, and on its own: the player's file may be read-only or open elsewhere, and that mustn't lose
                // the overrides or the table's own part below.
                if (toOwn.Count > 0)
                {
                    var n =
                        $"{toOwn.Count} graphics setting{(toOwn.Count == 1 ? "" : "s")} changed during play";
                    if (ownPath == null)
                    {
                        _notes.Add(
                            $"{n} not kept: there's no VPinballX.ini of yours to keep them in"
                        );
                    }
                    else
                    {
                        try
                        {
                            foreach (
                                var section in toOwn.GroupBy(
                                    c => c.Section,
                                    StringComparer.OrdinalIgnoreCase
                                )
                            )
                            {
                                IniFile.SetMany(
                                    ownPath,
                                    section.Key,
                                    section.Select(c => (c.Key, c.Value))
                                );
                            }
                            _notes.Add($"saved {n} into {ownPath}");
                        }
                        catch (Exception e)
                        {
                            _notes.Add($"couldn't save {n} into {ownPath}: {e.Message}");
                        }
                    }
                }

                if (afterPlay?.After is { } then)
                {
                    try
                    {
                        if (then(before, after) is { } note)
                        {
                            _notes.Add(note);
                        }
                    }
                    catch (Exception e)
                    {
                        _notes.Add("couldn't save the table's own settings: " + e.Message);
                    }
                }

                return toOverrides.Count;
            }
        }
    }

    /// <summary>Settings that differ after play. "1" and "1.000000" are the same value, not a change.</summary>
    public static IEnumerable<(string Section, string Key, string Value)> Changes(
        Dictionary<(string Section, string Key), string> before,
        Dictionary<(string Section, string Key), string> after
    )
    {
        foreach (var ((section, key), value) in after)
        {
            if (NotSettings.Contains(section, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Same(before.GetValueOrDefault((section, key)) ?? "", value))
            {
                yield return (section, key, value);
            }
        }
    }

    static bool Same(string a, string b)
    {
        if (a == b)
        {
            return true;
        }

        const NumberStyles n = NumberStyles.Float;
        var c = CultureInfo.InvariantCulture;
        return double.TryParse(a, n, c, out var x)
            && double.TryParse(b, n, c, out var y)
            && Math.Abs(x - y) <= 1e-6 * Math.Max(1, Math.Abs(x));
    }

    static readonly ConditionalWeakTable<Process, Action> Saves = new();

    /// <summary>
    /// Saves what changed during play once <paramref name="vpx"/> exits. Waits on a handle of its own: the caller
    /// may dispose its Process as soon as it has seen the exit.
    /// </summary>
    public static void SaveBackOnExit(Process vpx, Play play, Action<string> log)
    {
        void Save()
        {
            try
            {
                if (play.SaveBack() is > 0 and var n)
                {
                    log($"kept {n} setting{(n == 1 ? "" : "s")} changed during play as overrides");
                }
                foreach (var note in play.TakeNotes())
                {
                    log(note);
                }
            }
            catch (Exception e)
            {
                log("couldn't keep settings changed during play: " + e.Message);
            }
        }
        Saves.AddOrUpdate(vpx, Save);
        Process watcher;
        try
        {
            watcher = Process.GetProcessById(vpx.Id);
        }
        catch
        {
            return;
        } // already gone (Finish saves)
        _ = Task.Run(async () =>
        {
            using (watcher)
            {
                await watcher.WaitForExitAsync();
            }

            Save();
        });
    }

    /// <summary>For a caller about to quit right after VPX exits: saves now rather than on the background wait.</summary>
    public static void Finish(Process vpx)
    {
        if (Saves.TryGetValue(vpx, out var save))
        {
            save();
        }
    }
}
