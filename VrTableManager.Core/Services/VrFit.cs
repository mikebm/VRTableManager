using System.Globalization;
using System.Text.RegularExpressions;

namespace VrTableManager.Services;

/// <summary>
/// Fits every VPX table to the player in VR. VPX 10.8.1 (src/renderer/VRDevice.cpp) places each table from a few
/// settings, so one set of values lines every table up the same way:
/// <list type="bullet">
/// <item><c>[Player] LockbarHeight</c> (cm, ground to top of lockbar; VPX's default 85): every table's lockbar is put
/// at this height, its playfield below it by the table's own glass height.</item>
/// <item><c>[Player] LockbarWidth</c> (cm; default 70): every table is scaled so its lockbar is this wide, the lockbar
/// being the playfield width plus 2¼". 70 cm makes a standard 20¼" playfield about 22% too big. For real size each
/// table needs its own value, so the manager adds it to that table's launch.</item>
/// <item><c>[Player] ScreenPlayerY</c> (cm; VPX's default -10, its range -70 to 30): where your eyes are when VPX
/// recenters the view (keypad 5). Positive is behind the lockbar, negative over the glass; not clamped when read.</item>
/// <item><c>[PlayerVR] TableX/Y/Z, Orientation</c>: the offset VPX saves when you move or recenter the table. Saved for
/// every table (VPX always writes them to its app settings), so they're never set per table. TableY has the same
/// sense as ScreenPlayerY.</item>
/// </list>
/// Until the player picks their own, VR launches get a real cabinet's values (<see cref="LaunchSettings"/>), not VPX's.
/// With <see cref="AppSettings.VrFitByVpx"/> none of this happens and VPX places tables as it does on its own.
/// Newer VPX builds only use the lockbar values when <c>[PlayerVR] OverrideLockbarHeight/Width</c> are on, and keep a
/// table's native size otherwise, so those are written too.
/// </summary>
public static class VrFit
{
    /// <summary>Every table at its real size (each one's own lockbar width).</summary>
    public const string SizeReal = "Real";

    /// <summary>Every table scaled to one lockbar width (to match a controller stand or a real cabinet).</summary>
    public const string SizeSame = "Same";

    /// <summary>VPX's own scaling: every table's lockbar 70 cm wide, unless changed in VPX.</summary>
    public const string SizeVpx = "Vpx";

    /// <summary>A real cabinet's lockbar: about 36" (91 cm) from the floor to its top, on standard legs.</summary>
    public const double StandardLockbarHeight = 91;

    /// <summary>
    /// Where a player's eyes are, standing at a real cabinet: 10 cm behind the lockbar felt right in the headset.
    /// VPX's own -10 (10 cm over the glass) puts you leaning into the table. Exact only after a recenter (keypad 5):
    /// until then the saved table position counts from the headset's tracking origin, not your head.
    /// </summary>
    public const double StandardEyesBehind = 10;

    /// <summary>
    /// The eye distance one click of the Setup tab's − / + buttons moves: 1 cm, or in feet-and-inches mode 1" (on
    /// whole inches). Kept within <see cref="MinEyesBehind"/> to <see cref="MaxEyesBehind"/>.
    /// </summary>
    public static double StepEyes(double cm, int direction, bool inches) =>
        Step(cm, direction, inches, 1, MinEyesBehind, MaxEyesBehind);

    /// <summary>The player height − / + move: 1 cm or 1", within <see cref="MinPlayerHeight"/> to <see cref="MaxPlayerHeight"/>.</summary>
    public static double StepPlayerHeight(double cm, int direction, bool inches) =>
        Step(cm, direction, inches, 1, MinPlayerHeight, MaxPlayerHeight);

    /// <summary>The lockbar height − / + move: 1 cm or 1", within <see cref="MinLockbarHeight"/> to <see cref="MaxLockbarHeight"/>.</summary>
    public static double StepLockbarHeight(double cm, int direction, bool inches) =>
        Step(cm, direction, inches, 1, MinLockbarHeight, MaxLockbarHeight);

    public const double MinPlayerHeight = 100,
        MaxPlayerHeight = 230,
        MinLockbarHeight = 40,
        MaxLockbarHeight = 130;

    /// <summary>Where the player starts when stepping an empty height.</summary>
    public const double TypicalPlayerHeight = 175;

    /// <summary>One click: to the next multiple of <paramref name="stepCm"/> (or of 1" in feet-and-inches mode), kept in range.</summary>
    static double Step(double cm, int direction, bool inches, double stepCm, double min, double max)
    {
        var unit = inches ? 2.54 : stepCm;
        return Math.Clamp((Math.Round(cm / unit) + direction) * unit, min, max);
    }

    /// <summary>An eye distance as the Setup tab shows it: "35 cm", or "14"" in whole inches.</summary>
    public static string FormatEyes(double cm, bool inches) =>
        inches ? $"{Math.Round(cm / 2.54):0}\"" : $"{Math.Round(cm):0} cm";

    public const double VpxLockbarWidth = 70;

    /// <summary>
    /// How far the eyes can be from the lockbar. VPX lists ScreenPlayerY as -70 to 30, but that's only its in-game
    /// slider: a value read from the settings file isn't clamped (LayeredINIPropertyStore::LoadFromINI), and TableY's
    /// own range is ±100. Moving the slider in VPX's menu would bring it back within 30.
    /// </summary>
    public const double MinEyesBehind = -70,
        MaxEyesBehind = 100;

    /// <summary>VPX's lockbar: the playfield width plus 2¼".</summary>
    const double LockbarMarginCm = 2.25 * 2.54;

    /// <summary>
    /// A comfortable lockbar height for someone this tall: about 0.52 × height, which puts it near the top of the
    /// thigh (175 cm → 91 cm, a real cabinet's). Kept between 60 and 110 cm.
    /// </summary>
    public static double LockbarHeightFor(double heightCm) =>
        Math.Round(Math.Clamp(heightCm * 0.52, 60, 110));

    /// <summary>The lockbar width that shows this table at real size (scale 1), or null if its width can't be read.</summary>
    public static double? RealLockbarWidth(string vpxPath) =>
        VpxCabinet.PlayfieldWidthCm(vpxPath) is { } w and > 10
            ? Math.Round(w + LockbarMarginCm, 1)
            : null;

    /// <summary>The player's choices, in centimetres. EyesBehind is negative over the glass.</summary>
    public sealed record Choice(
        double LockbarHeightCm,
        double EyesBehindCm,
        string Size,
        double SameWidthCm
    );

    /// <summary>VPX's own defaults, used once the player has saved a fit (VPX writes an empty value for them).</summary>
    public const double VpxLockbarHeight = 85,
        VpxEyesBehind = -10;

    /// <summary>
    /// Writes the choice into the VR settings file. <paramref name="resetPosition"/> also puts the table where a
    /// recenter would with your headset at its centre point: straight ahead, your eyes EyesBehind from the lockbar,
    /// no height offset (the height comes from the lockbar). Without it, only the height offset is cleared, which
    /// would otherwise move every lockbar away from the chosen height.
    /// The lockbar width is only the panel's in "same width" mode: otherwise a width set in VPX's own menu is left
    /// alone, except the one this panel wrote when it was in "same width" mode (<paramref name="previousSize"/>).
    /// </summary>
    public static void Write(string vrIni, Choice c, bool resetPosition, string previousSize)
    {
        var same = c.Size == SizeSame;
        var eyes = Num(Math.Clamp(c.EyesBehindCm, MinEyesBehind, MaxEyesBehind));
        var player = new List<(string, string)>
        {
            ("LockbarHeight", Num(c.LockbarHeightCm)),
            ("ScreenPlayerX", ""),
            ("ScreenPlayerY", eyes),
        };
        var vr = new List<(string, string)> { ("OverrideLockbarHeight", "1"), ("TableZ", "") };
        if (same)
        {
            player.Add(("LockbarWidth", Num(c.SameWidthCm)));
            vr.Add(("OverrideLockbarWidth", "1"));
        }
        else if (previousSize == SizeSame)
        {
            player.Add(("LockbarWidth", ""));
            vr.Add(("OverrideLockbarWidth", ""));
        }
        if (resetPosition)
        {
            vr.AddRange([("TableX", ""), ("TableY", eyes), ("Orientation", "")]);
        }
        VpxIniLayers.SetOverrides(vrIni, "Player", player);
        VpxIniLayers.SetOverrides(vrIni, "PlayerVR", vr);
    }

    /// <summary>
    /// For leaving VR placement to VPX (<see cref="AppSettings.VrFitByVpx"/>): clears the values a saved fit wrote into
    /// the VR settings file (lockbar height and width, eye position, and the override switches newer VPX builds read),
    /// so your own VPinballX.ini's or VPX's defaults apply. Where VPX saved the table during play is left alone.
    /// </summary>
    public static void Clear(string vrIni)
    {
        VpxIniLayers.SetOverrides(
            vrIni,
            "Player",
            [
                ("LockbarHeight", ""),
                ("LockbarWidth", ""),
                ("ScreenPlayerX", ""),
                ("ScreenPlayerY", ""),
            ]
        );
        VpxIniLayers.SetOverrides(
            vrIni,
            "PlayerVR",
            [("OverrideLockbarHeight", ""), ("OverrideLockbarWidth", "")]
        );
    }

    /// <summary>
    /// Back to the manager's defaults: clears the lockbar and eye values from the VR settings file, so every launch
    /// gets a real cabinet's (<see cref="LaunchSettings"/>, once <see cref="AppSettings.VrFitSaved"/> is off again),
    /// and puts the table straight ahead at the default eye distance. The player's own VPinballX.ini still counts.
    /// </summary>
    public static void Reset(string vrIni)
    {
        VpxIniLayers.SetOverrides(
            vrIni,
            "Player",
            [
                ("LockbarHeight", ""),
                ("LockbarWidth", ""),
                ("ScreenPlayerX", ""),
                ("ScreenPlayerY", ""),
            ]
        );
        VpxIniLayers.SetOverrides(
            vrIni,
            "PlayerVR",
            [
                ("OverrideLockbarHeight", ""),
                ("OverrideLockbarWidth", ""),
                ("TableX", ""),
                ("TableY", Num(StandardEyesBehind)),
                ("TableZ", ""),
                ("Orientation", ""),
            ]
        );
    }

    /// <summary>
    /// Settings for one VR launch of this table, on top of the VR settings (<paramref name="vrSettings"/>, what VR
    /// play uses without them):
    /// <list type="bullet">
    /// <item>until the player saves a fit (<see cref="AppSettings.VrFitSaved"/>), a real cabinet's lockbar height and
    /// eye position, and the table's starting position at that eye distance, for any of them not set in VPX or the VR
    /// file. Kept if VPX changes them during play, like any setting. After a fit is saved nothing is added: VPX writes
    /// an empty value for one at its own default (85 cm, -10 cm), which then means exactly that.</item>
    /// <item>in real-size mode, the table's own lockbar width, unless its .ini sets one (VPX uses that anyway). Never
    /// kept (NotKept), or the next table would get this one's width.</item>
    /// <item>for a .vpx, every table's height (TableZ) is 0 and never kept: a table's height is its own
    /// (<see cref="KeepTableHeight"/>), and the position keys its .ini sets aren't kept either (<see cref="OwnPositionKeys"/>).</item>
    /// </list>
    /// </summary>
    public static (
        List<(string Section, string Key, string Value)> Extra,
        List<(string Section, string Key)> NotKept
    ) LaunchSettings(
        AppSettings s,
        Dictionary<(string Section, string Key), string> vrSettings,
        string tablePath
    )
    {
        List<(string, string, string)> extra = [];
        List<(string, string)> notKept = [];
        if (s.VrFitByVpx)
        {
            return (extra, notKept); // left to VPX: nothing added, nothing held back
        }
        // Put back to VPX's default in VR play is set, to the default (VpxIniLayers' DefaultsSection), not unset.
        var atDefault = VpxIniLayers.SetToDefault(VpxIniLayers.OverridePath(s, vr: true));
        bool Unset(string section, string key) =>
            string.IsNullOrWhiteSpace(vrSettings.GetValueOrDefault((section, key)))
            && !atDefault.Contains((section, key));

        if (!s.VrFitSaved)
        {
            if (Unset("Player", "LockbarHeight"))
            {
                extra.Add(("Player", "LockbarHeight", Num(StandardLockbarHeight)));
                extra.Add(("PlayerVR", "OverrideLockbarHeight", "1"));
            }
            if (Unset("Player", "ScreenPlayerY"))
            {
                extra.Add(("Player", "ScreenPlayerY", Num(StandardEyesBehind)));
            }
            // Where you start: VPX places the table from TableY, and only uses ScreenPlayerY when you recenter.
            if (Unset("PlayerVR", "TableY"))
            {
                extra.Add(("PlayerVR", "TableY", Num(StandardEyesBehind)));
            }
        }

        var isVpx = tablePath.EndsWith(".vpx", StringComparison.OrdinalIgnoreCase);
        var table = isVpx ? IniFile.Read(VpxGraphics.TableIniPath(tablePath)) : [];
        if (
            s.VrTableSizing == SizeReal
            && isVpx
            && string.IsNullOrWhiteSpace(table.GetValueOrDefault(("Player", "LockbarWidth")))
            && RealLockbarWidth(tablePath) is { } w
        )
        {
            extra.Add(("Player", "LockbarWidth", Num(w)));
            notKept.Add(("Player", "LockbarWidth"));
        }

        // Height (keypad 8 and 2) is about one table's glass height, never everyone's: every table's is 0, and a move
        // goes to the table's own .ini instead (KeepTableHeight). The lockbar height in FIT VR TO ME is everyone's.
        // Setting it here also drops a height saved for every table before heights were per table.
        if (isVpx)
        {
            extra.Add(("PlayerVR", "TableZ", ""));
            notKept.Add(("PlayerVR", "TableZ"));
        }
        return (extra, notKept);
    }

    /// <summary>
    /// When a VR play of a .vpx closes: the position keys the table's .ini sets for itself, read then so it includes
    /// one saved during that play (VPX's VR menu, "Save as Table Override"). VPX also saves the position it ended at as
    /// everyone's; kept, that would move every other table to this one's place, so these are never kept.
    /// </summary>
    public static IEnumerable<(string Section, string Key)> OwnPositionKeys(string tablePath)
    {
        var table = IniFile.Read(VpxGraphics.TableIniPath(tablePath));
        return TablePositionKeys
            .Where(k => !string.IsNullOrWhiteSpace(table.GetValueOrDefault(("PlayerVR", k))))
            .Select(k => ("PlayerVR", k))
            .ToList();
    }

    /// <summary>
    /// When a VR play of a .vpx closes: a height VPX ended at that differs from where the table started becomes that
    /// table's own (TableZ in its .ini), and 0 (every table's height) clears it. Recentering (keypad 5, or lining up
    /// with the controllers) also sets the height to 0, but it moves where you stand too, so a 0 that comes with a
    /// move of TableX/Y or Orientation is a recenter, not a lowering, and leaves the table's height alone. Returns what
    /// changed, for the play log, or null.
    /// </summary>
    public static string? KeepTableHeight(
        string tablePath,
        Dictionary<(string Section, string Key), string> before,
        Dictionary<(string Section, string Key), string> after
    )
    {
        var table = IniFile.Read(VpxGraphics.TableIniPath(tablePath));
        // Where VPX started: the table's own value, else every table's (the play file before it ran).
        double Start(string key) =>
            Height(table.GetValueOrDefault(("PlayerVR", key)))
            ?? Height(before.GetValueOrDefault(("PlayerVR", key)))
            ?? 0;
        double End(string key) => Height(after.GetValueOrDefault(("PlayerVR", key))) ?? 0;

        var start = Start("TableZ");
        var end = End("TableZ");
        if (Math.Abs(end - start) < 0.05)
        {
            return null;
        }

        var name = Path.GetFileName(tablePath);
        if (Math.Abs(end) < 0.05)
        {
            var recentered = new[] { "TableX", "TableY", "Orientation" }.Any(k =>
                Math.Abs(End(k) - Start(k)) >= 0.05
            );
            if (recentered)
            {
                return null;
            }

            WriteTableHeight(tablePath, null);
            return $"{name}: VR height back to every table's";
        }
        WriteTableHeight(tablePath, end);
        return $"{name}: VR height {Num(end)} cm, for this table only";
    }

    /// <summary>The height this table keeps for itself in VR (its .ini's TableZ, cm), or null.</summary>
    public static double? ReadTableHeight(string tablePath) =>
        Height(
            IniFile
                .Read(VpxGraphics.TableIniPath(tablePath))
                .GetValueOrDefault(("PlayerVR", "TableZ"))
        );

    /// <summary>Sets (or with null, clears) this table's own VR height.</summary>
    public static void WriteTableHeight(string tablePath, double? cm)
    {
        var ini = VpxGraphics.TableIniPath(tablePath);
        if (cm == null && !File.Exists(ini))
        {
            return;
        }

        IniFile.Set(ini, "PlayerVR", "TableZ", cm is { } v ? Num(v) : "");
    }

    static double? Height(string? v) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>The [PlayerVR] keys VPX saves for every table when one closes (VRDevice::SaveVRSettings).</summary>
    static readonly string[] TablePositionKeys = ["TableX", "TableY", "TableZ", "Orientation"];

    /// <summary>A [Player] length VR play will use (cm): the VR settings' own, else <paramref name="fallback"/>.</summary>
    public static double Value(
        Dictionary<(string Section, string Key), string> vrSettings,
        string key,
        double fallback
    ) =>
        double.TryParse(
            vrSettings.GetValueOrDefault(("Player", key)),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var v
        )
            ? v
            : fallback;

    /// <summary>The lockbar height VR play uses: the VR settings' own, else the manager's or (after a saved fit) VPX's default.</summary>
    public static double LockbarHeight(
        Dictionary<(string Section, string Key), string> vrSettings,
        AppSettings s
    ) =>
        Value(
            vrSettings,
            "LockbarHeight",
            s.VrFitByVpx || s.VrFitSaved || AtVpxDefault(s, "LockbarHeight")
                ? VpxLockbarHeight
                : StandardLockbarHeight
        );

    /// <summary>The eye distance VR play uses on recenter, like <see cref="LockbarHeight"/>.</summary>
    public static double EyesBehind(
        Dictionary<(string Section, string Key), string> vrSettings,
        AppSettings s
    ) =>
        Value(
            vrSettings,
            "ScreenPlayerY",
            s.VrFitByVpx || s.VrFitSaved || AtVpxDefault(s, "ScreenPlayerY")
                ? VpxEyesBehind
                : StandardEyesBehind
        );

    /// <summary>A [Player] setting put back to VPX's default in VR play, which launches leave at that default (see <see cref="LaunchSettings"/>).</summary>
    static bool AtVpxDefault(AppSettings s, string key) =>
        VpxIniLayers.SetToDefault(VpxIniLayers.OverridePath(s, vr: true)).Contains(("Player", key));

    /// <summary>What VR play will use now, as one line for the Setup tab.</summary>
    public static string Describe(
        Dictionary<(string Section, string Key), string> vrSettings,
        AppSettings s
    )
    {
        var inches = s.VrUseInches;
        var height = LockbarHeight(vrSettings, s);
        var eyes = EyesBehind(vrSettings, s);
        var where =
            eyes >= 0
                ? $"your eyes {FormatEyes(eyes, inches)} behind it"
                : $"your eyes {FormatEyes(-eyes, inches)} over the glass";
        var width = Format(Value(vrSettings, "LockbarWidth", VpxLockbarWidth), inches);
        if (s.VrFitByVpx)
        {
            return $"Left to VPX: lockbar {Format(height, inches)} high, {where}, every table scaled to a {width} lockbar.";
        }
        var size = s.VrTableSizing switch
        {
            SizeReal => "every table at its real size",
            SizeSame => $"every table scaled to a {width} lockbar",
            _ => $"tables scaled to a {width} lockbar (VPX's default sizing)",
        };
        return $"Lockbar {Format(height, inches)} high, {where}, {size}.";
    }

    // ---- Lengths ---------------------------------------------------------------------------

    static readonly Regex FeetInches = new(
        @"^\s*(\d+(?:[.,]\d+)?)\s*(?:'|’|ft|feet|foot)\s*(?:(\d+(?:[.,]\d+)?)\s*(?:""|”|''|in|inch|inches)?)?\s*$",
        RegexOptions.IgnoreCase
    );
    static readonly Regex WithUnit = new(
        @"^\s*(-?\d+(?:[.,]\d+)?)\s*(cm|mm|m|""|”|''|in|inch|inches)?\s*$",
        RegexOptions.IgnoreCase
    );

    /// <summary>
    /// A length typed by the player, in centimetres: "178", "178 cm", "1.78 m", "5'10\"", "5 ft 10", "70 in".
    /// A bare number is inches when <paramref name="inches"/> is set, otherwise centimetres.
    /// </summary>
    public static double? ParseLength(string? text, bool inches)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        static double N(string v) =>
            double.Parse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);

        if (FeetInches.Match(text) is { Success: true } fi)
        {
            var total =
                N(fi.Groups[1].Value) * 12 + (fi.Groups[2].Success ? N(fi.Groups[2].Value) : 0);
            return total * 2.54;
        }
        if (WithUnit.Match(text) is not { Success: true } m)
        {
            return null;
        }

        var n = N(m.Groups[1].Value);
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "cm" => n,
            "mm" => n / 10,
            "m" => n * 100,
            "" => inches ? n * 2.54 : n,
            _ => n * 2.54,
        };
    }

    /// <summary>"91 cm", or in inches "36"" (and "5'10"" from 5 feet up, for a height).</summary>
    public static string Format(double cm, bool inches)
    {
        if (!inches)
        {
            return $"{Math.Round(cm):0} cm";
        }

        var total = Math.Round(cm / 2.54);
        return cm >= 150
            ? $"{(int)total / 12}'{(int)total % 12}\""
            : $"{Math.Round(cm / 2.54, 1):0.#}\"";
    }

    static string Num(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
