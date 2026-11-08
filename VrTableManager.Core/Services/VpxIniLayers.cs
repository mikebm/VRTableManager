using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace VrTableManager.Services;

/// <summary>
/// VPX reads one settings file per run, but players want to keep tuning their own <c>VPinballX.ini</c> and still
/// have the manager's VR and desktop changes on top. So each launch gets a file built from layers:
/// <list type="number">
/// <item>the player's own <c>VPinballX.ini</c> (the one VPX itself would load; read fresh every launch, never written),</item>
/// <item>the overrides for the mode: <c>VrTableManager-VR.ini</c> or <c>VrTableManager-Desktop.ini</c> beside VPX.
/// Only keys with a value count; VPX writes an empty value for anything left at its default, so the VR file from
/// before this change works as an override file as it is.</item>
/// </list>
/// The result goes to <c>…-VR.play.ini</c> / <c>…-Desktop.play.ini</c>, which VPX runs with (<c>-Ini</c>). Whatever
/// VPX changed in it during play (the in-game menu, lining the table up in VR) is saved into that mode's overrides
/// once the table closes, since the play file is rebuilt next time.
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
            candidates.Add(Path.Combine(prefs, version, "VPinballX.ini"));
        else if (Directory.Exists(prefs))
            candidates.AddRange(
                Directory
                    .EnumerateDirectories(prefs)
                    .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                    .Select(d => Path.Combine(d, "VPinballX.ini"))
            );
        candidates.Add(Path.Combine(s.VpxDir, "VPinballX.ini"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>The value VPX will use for each key in this mode: the player's own, then the mode's overrides.</summary>
    public static Dictionary<(string Section, string Key), string> Effective(AppSettings s, bool vr)
    {
        var result = BasePath(s) is { } b ? IniFile.Read(b) : IniFile.Read("");
        foreach (var (k, v) in Overrides(OverridePath(s, vr)))
            result[k] = v;
        return result;
    }

    static IEnumerable<KeyValuePair<(string Section, string Key), string>> Overrides(string path) =>
        IniFile
            .Read(path)
            .Where(kv =>
                kv.Value != ""
                && !NotSettings.Contains(kv.Key.Section, StringComparer.OrdinalIgnoreCase)
            );

    /// <summary>Writes the layered settings for this mode to <paramref name="path"/> (the play file by default).</summary>
    /// <param name="extra">Settings for this launch only, on top of the overrides; not saved back unless VPX changes them.</param>
    public static Play Build(
        AppSettings s,
        bool vr,
        string? path = null,
        IEnumerable<(string Section, string Key, string Value)>? extra = null
    )
    {
        path ??= PlayPath(s, vr);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Starting from the player's file keeps its layout and comments; overrides go in section by section.
        if (BasePath(s) is { } b)
            File.Copy(b, path, overwrite: true);
        else
            File.WriteAllText(path, "");
        foreach (
            var section in Overrides(OverridePath(s, vr))
                .GroupBy(kv => kv.Key.Section, StringComparer.OrdinalIgnoreCase)
        )
            IniFile.SetMany(path, section.Key, section.Select(kv => (kv.Key.Key, kv.Value)));
        foreach (
            var section in (extra ?? []).GroupBy(e => e.Section, StringComparer.OrdinalIgnoreCase)
        )
            IniFile.SetMany(path, section.Key, section.Select(e => (e.Key, e.Value)));
        return new Play(path, OverridePath(s, vr), IniFile.Read(path));
    }

    /// <summary>One launch's play file and what it held when VPX started.</summary>
    public sealed class Play(
        string path,
        string overridePath,
        Dictionary<(string Section, string Key), string> before
    )
    {
        public string Path { get; } = path;
        int _saved;

        /// <summary>
        /// Keeps what VPX changed during play as overrides for this mode. Runs once, however often it's called
        /// (when the table closes, and again by a caller that waited for it). Returns how many settings were kept.
        /// </summary>
        public int SaveBack()
        {
            if (Interlocked.Exchange(ref _saved, 1) == 1)
                return 0;
            var changed = Changes(before, IniFile.Read(Path)).ToList();
            foreach (
                var section in changed.GroupBy(c => c.Section, StringComparer.OrdinalIgnoreCase)
            )
                IniFile.SetMany(overridePath, section.Key, section.Select(c => (c.Key, c.Value)));
            return changed.Count;
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
                continue;
            if (!Same(before.GetValueOrDefault((section, key)) ?? "", value))
                yield return (section, key, value);
        }
    }

    static bool Same(string a, string b)
    {
        if (a == b)
            return true;
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
                    log($"kept {n} setting{(n == 1 ? "" : "s")} changed during play as overrides");
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
                await watcher.WaitForExitAsync();
            Save();
        });
    }

    /// <summary>For a caller about to quit right after VPX exits: saves now rather than on the background wait.</summary>
    public static void Finish(Process vpx)
    {
        if (Saves.TryGetValue(vpx, out var save))
            save();
    }
}
