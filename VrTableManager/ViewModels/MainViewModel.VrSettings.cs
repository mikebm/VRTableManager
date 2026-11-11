using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Setup → "VR & graphics": OpenXR runtime, refresh rate, ASW and VPX's graphics settings (in the VR ini).
public sealed partial class MainViewModel
{
    // ---- OpenXR runtime ------------------------------------------------------------------

    public ObservableCollection<OpenXrRuntime> Runtimes { get; } = [];

    [ObservableProperty]
    public partial string RuntimeTitle { get; set; } = "";

    [ObservableProperty]
    public partial string RuntimeAdvice { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRuntimeWarning { get; set; }

    [ObservableProperty]
    public partial bool IsMetaRuntime { get; set; }

    [ObservableProperty]
    public partial bool WarnSteamVrRuntime { get; set; }

    [ObservableProperty]
    public partial bool ExplainFailedLaunches { get; set; }

    /// <summary>VR play uses VPX's built-in PinMAME, which reads ROMs from each table's own folder.</summary>
    [ObservableProperty]
    public partial bool UseBuiltInPinmame { get; set; }

    /// <summary>The VR lobby records a ROM table's start-up music in the background the first time you stop on it.</summary>
    [ObservableProperty]
    public partial bool LobbyRecordsRomAudio { get; set; }

    /// <summary>The VR lobby shows a loading screen until the picked table takes the headset over.</summary>
    [ObservableProperty]
    public partial bool LobbyWaitsForTable { get; set; }

    /// <summary>The VR lobby stays open, hidden, while you play, and takes the headset back when the table closes.</summary>
    [ObservableProperty]
    public partial bool LobbyStaysOpen { get; set; }

    /// <summary>The loading screen plays the table's loading video (or the one for every table) on a big screen.</summary>
    [ObservableProperty]
    public partial bool LobbyLoadingVideos { get; set; }

    public bool HasMetaLinkApp => File.Exists(MetaLinkApp);
    public bool HasOculusDebugTool => File.Exists(MetaAsw.DebugToolPath);

    void RefreshVrStatus()
    {
        Runtimes.Clear();
        foreach (var r in OpenXrRuntimes.List())
        {
            Runtimes.Add(r);
        }

        var active = Runtimes.FirstOrDefault(r => r.IsActive);
        IsMetaRuntime = active?.IsMeta == true;
        IsRuntimeWarning = active == null || active.IsSteamVr;
        RuntimeTitle = active == null ? "No OpenXR runtime is active" : $"Active: {active.Name}";
        RuntimeAdvice = active switch
        {
            null =>
                "Install the Meta Quest Link app (or Virtual Desktop), then make it the active runtime below.",
            { IsMeta: true } =>
                "Best for Quest Link and Air Link: VPX talks to the headset directly. Set the refresh rate and render resolution in the Meta Quest Link app (Devices → your Quest → Graphics preferences). ASW is set below.",
            { IsSteamVr: true } =>
                "SteamVR adds a layer between VPX and a Quest, which often costs frames and adds stutter. Unless you need it, switch to Meta Quest Link (cable/Air Link) or Virtual Desktop (VDXR). In SteamVR, turn Motion Smoothing off for pinball.",
            { Name: "Virtual Desktop (VDXR)" } =>
                "Good over Wi-Fi 6/6E. In Virtual Desktop's Streaming tab, try a frame rate the PC can always hold, and leave Synchronous Spacewarp off for pinball (it smears the ball).",
            _ => "VPX uses this runtime for VR.",
        };
        IsAswAvailable = MetaAsw.IsAvailable;
    }

    [RelayCommand]
    async Task MakeRuntimeActiveAsync(OpenXrRuntime? runtime)
    {
        if (runtime == null || runtime.IsActive)
        {
            return;
        }

        var pick = await _ui.ChoiceAsync(
            $"Use {runtime.Name} for VR?",
            $"VPX, UEVR (in OpenXR mode) and other OpenXR games will use {runtime.Name}.\n\n"
                + "Windows will ask for administrator permission, because this setting is shared by every user on the PC.",
            ["SWITCH", "CANCEL"]
        );
        if (pick != 0)
        {
            return;
        }

        var ok = await OpenXrRuntimes.SetActiveAsync(runtime);
        RefreshVrStatus();
        RefreshSetupStatus();
        if (!ok)
        {
            RuntimeAdvice =
                "The runtime wasn't changed (the administrator prompt was declined or failed). "
                + RuntimeAdvice;
        }
    }

    [RelayCommand]
    void OpenMetaLinkApp() => _ui.OpenExternal(MetaLinkApp);

    [RelayCommand]
    void OpenOculusDebugTool() => _ui.OpenExternal(MetaAsw.DebugToolPath);

    // ---- ASW (Meta Quest Link) -------------------------------------------------------------

    public string[] AswModes { get; } = MetaAsw.Modes;

    [ObservableProperty]
    public partial string AswMode { get; set; } = MetaAsw.Modes[0];

    [ObservableProperty]
    public partial bool IsAswAvailable { get; set; }

    /// <summary>Before a VR launch through Meta Quest Link: applies the chosen ASW mode (it resets when the Oculus service restarts).</summary>
    async Task ApplyAswAsync()
    {
        if (Settings.MetaAswMode == MetaAsw.Modes[0] || OpenXrRuntimes.Active()?.IsMeta != true)
        {
            return;
        }

        await MetaAsw.ApplyAsync(Settings.MetaAswMode);
    }

    // ---- VPX graphics (VR ini) ---------------------------------------------------------------

    public string[] GraphicsPresets { get; } = VpxGraphics.Presets.Select(p => p.Name).ToArray();
    public string[] RenderScales { get; } =
    ["60%", "70%", "80%", "90%", "100%", "110%", "120%", "130%", "150%", "175%", "200%"];
    public string[] MsaaNames { get; } = VpxGraphics.MsaaNames;
    public string[] PostAaNames { get; } = VpxGraphics.PostAaNames;
    public string[] ReflectionNames { get; } = VpxGraphics.ReflectionNames;
    public string[] AoNames { get; } = VpxGraphics.AoNames;
    public string[] SharpenNames { get; } = VpxGraphics.SharpenNames;
    public string[] FpsNames { get; } = VpxGraphics.FpsNames;

    [ObservableProperty]
    public partial string GfxRenderScale { get; set; } = "100%";

    [ObservableProperty]
    public partial int GfxMsaa { get; set; }

    [ObservableProperty]
    public partial int GfxPostAa { get; set; }

    [ObservableProperty]
    public partial int GfxReflections { get; set; }

    [ObservableProperty]
    public partial int GfxAo { get; set; }

    [ObservableProperty]
    public partial bool GfxBloom { get; set; }

    [ObservableProperty]
    public partial int GfxSharpen { get; set; }

    [ObservableProperty]
    public partial int GfxFps { get; set; }

    [ObservableProperty]
    public partial string GraphicsPresetLabel { get; set; } = "";

    [ObservableProperty]
    public partial string GraphicsStatus { get; set; } = "";

    /// <summary>VR play uses the panel's graphics settings; off, VPX's own (<see cref="AppSettings.UseVrGraphics"/>).</summary>
    [ObservableProperty]
    public partial bool UseVrGraphics { get; set; }

    bool _loadingGraphics;

    static string ScaleText(double s) => $"{Math.Round(s * 100):0}%";

    static double ParseScale(string s) =>
        double.TryParse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
            ? p / 100
            : 1.0;

    void LoadGraphics()
    {
        _loadingGraphics = true;
        try
        {
            var g = VpxGraphics.Read(VpxIniLayers.Effective(Settings, vr: true)); // what VR play will use, own settings included
            GfxRenderScale =
                RenderScales.FirstOrDefault(s => Math.Abs(ParseScale(s) - g.RenderScale) < 0.001)
                ?? ScaleText(g.RenderScale);
            GfxMsaa = g.Msaa;
            GfxPostAa = g.PostAa;
            GfxReflections = g.Reflections;
            GfxAo = g.AmbientOcclusion;
            GfxBloom = g.Bloom;
            GfxSharpen = g.Sharpen;
            GfxFps = g.FpsOverlay;
            GraphicsPresetLabel = g.PresetName is { } p
                ? $"Matches the {p} preset"
                : "Custom settings";
            LoadVrView();
        }
        finally
        {
            _loadingGraphics = false;
        }
    }

    VpxGraphics CurrentGraphics() =>
        new()
        {
            RenderScale = ParseScale(GfxRenderScale),
            Msaa = GfxMsaa,
            PostAa = GfxPostAa,
            Reflections = GfxReflections,
            AmbientOcclusion = GfxAo,
            Bloom = GfxBloom,
            Sharpen = GfxSharpen,
            FpsOverlay = GfxFps,
        };

    void SaveGraphics()
    {
        if (_loadingGraphics)
        {
            return;
        }

        try
        {
            var g = CurrentGraphics();
            g.Write(VpxSetup.VrIniPath(Settings));
            GraphicsPresetLabel = g.PresetName is { } p
                ? $"Matches the {p} preset"
                : "Custom settings";
            GraphicsStatus = "✓ Saved. Takes effect the next time a table starts.";
        }
        catch (Exception e)
        {
            GraphicsStatus = "Couldn't save the VR settings: " + e.Message;
        }
    }

    partial void OnGfxRenderScaleChanged(string value) => SaveGraphics();

    partial void OnGfxMsaaChanged(int value) => SaveGraphics();

    partial void OnGfxPostAaChanged(int value) => SaveGraphics();

    partial void OnGfxReflectionsChanged(int value) => SaveGraphics();

    partial void OnGfxAoChanged(int value) => SaveGraphics();

    partial void OnGfxBloomChanged(bool value) => SaveGraphics();

    partial void OnGfxSharpenChanged(int value) => SaveGraphics();

    partial void OnGfxFpsChanged(int value) => SaveGraphics();

    [RelayCommand]
    void ApplyGraphicsPreset(string name)
    {
        if (VpxGraphics.Presets.FirstOrDefault(p => p.Name == name).Settings is not { } preset)
        {
            return;
        }

        _loadingGraphics = true;
        try
        {
            GfxRenderScale = ScaleText(preset.RenderScale);
            GfxMsaa = preset.Msaa;
            GfxPostAa = preset.PostAa;
            GfxReflections = preset.Reflections;
            GfxAo = preset.AmbientOcclusion;
            GfxBloom = preset.Bloom;
            GfxSharpen = preset.Sharpen;
        }
        finally
        {
            _loadingGraphics = false;
        }
        SaveGraphics();
    }

    // ---- Where the table sits in VR ----------------------------------------------------------

    /// <summary>The table position VPX saved (moved in the headset), or that it's at VPX's default.</summary>
    [ObservableProperty]
    public partial string VrViewText { get; set; } = "";

    void LoadVrView()
    {
        var ini = VpxIniLayers.Effective(Settings, vr: true);
        var v = VrView.Read(ini);
        VrViewText = v.IsDefault
            ? "The table is where VPX puts it by default."
            : $"Moved in the headset: {v.Describe()}.";
        LoadVrFit(ini);
    }

    // ---- Fit VR to me (VrFit) ----------------------------------------------------------------

    public string[] FitSizes { get; } =
    [
        "Real size (each table its own width)",
        "The same width for every table",
        "VPX's own sizing (70 cm lockbar)",
    ];

    static readonly string[] FitSizeIds = [VrFit.SizeReal, VrFit.SizeSame, VrFit.SizeVpx];

    [ObservableProperty]
    public partial bool FitUseInches { get; set; }

    /// <summary>Your height; typing it fills in the lockbar height.</summary>
    [ObservableProperty]
    public partial string FitHeightText { get; set; } = "";

    [ObservableProperty]
    public partial string FitLockbarText { get; set; } = "";

    /// <summary>How far your eyes are behind the lockbar, as typed (negative is over the glass); − / + step it.</summary>
    [ObservableProperty]
    public partial string FitEyesText { get; set; } = "";

    [RelayCommand]
    void FitEyesCloser() => StepFitEyes(-1);

    [RelayCommand]
    void FitEyesFurther() => StepFitEyes(+1);

    void StepFitEyes(int direction)
    {
        var cm = VrFit.ParseLength(FitEyesText, FitUseInches) ?? VrFit.StandardEyesBehind;
        FitEyesText = VrFit.FormatEyes(VrFit.StepEyes(cm, direction, FitUseInches), FitUseInches);
    }

    [RelayCommand]
    void FitHeightLess() => StepFitHeight(-1);

    [RelayCommand]
    void FitHeightMore() => StepFitHeight(+1);

    /// <summary>Your height is optional: clearing it leaves the lockbar height as it is.</summary>
    [RelayCommand]
    void FitHeightClear() => FitHeightText = "";

    void StepFitHeight(int direction)
    {
        var cm = VrFit.ParseLength(FitHeightText, FitUseInches) ?? VrFit.TypicalPlayerHeight;
        FitHeightText = VrFit.Format(
            VrFit.StepPlayerHeight(cm, direction, FitUseInches),
            FitUseInches
        );
    }

    [RelayCommand]
    void FitLockbarLower() => StepFitLockbar(-1);

    [RelayCommand]
    void FitLockbarHigher() => StepFitLockbar(+1);

    void StepFitLockbar(int direction)
    {
        var cm = VrFit.ParseLength(FitLockbarText, FitUseInches) ?? VrFit.StandardLockbarHeight;
        FitLockbarText = VrFit.Format(
            VrFit.StepLockbarHeight(cm, direction, FitUseInches),
            FitUseInches
        );
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFitSameWidth))]
    public partial int FitSizeIndex { get; set; } = 2;

    public bool IsFitSameWidth => FitSizeIndex == 1;

    [ObservableProperty]
    public partial string FitSameWidthText { get; set; } = "";

    /// <summary>Also put the table straight ahead of the headset's centre point, StandBack in front of you.</summary>
    [ObservableProperty]
    public partial bool FitResetPosition { get; set; } = true;

    [ObservableProperty]
    public partial string FitSummary { get; set; } = "";

    [ObservableProperty]
    public partial string FitStatus { get; set; } = "";

    bool _loadingFit;

    /// <summary>
    /// The exact length behind each box's text, while the text is what the panel put there: a box shows a rounded
    /// value (10 cm as 4", 98.4 as 98 cm), and saving that rounding would drift the setting.
    /// </summary>
    readonly Dictionary<string, (string Text, double Cm)> _fitExact = [];

    string ShowFit(string field, double cm, bool inches, bool eyes = false)
    {
        var text = eyes ? VrFit.FormatEyes(cm, inches) : VrFit.Format(cm, inches);
        _fitExact[field] = (text, cm);
        return text;
    }

    /// <summary>A box's length in cm: the exact one if the text is still what the panel showed, else as typed.</summary>
    double? FitCm(string field, string text) =>
        _fitExact.TryGetValue(field, out var e) && e.Text == text
            ? e.Cm
            : VrFit.ParseLength(text, FitUseInches);

    void LoadVrFit(Dictionary<(string Section, string Key), string> ini)
    {
        _loadingFit = true;
        try
        {
            var inches = Settings.VrUseInches;
            FitUseInches = inches;
            _fitExact.Clear();
            // What VR play uses: the VR settings' own values, else the defaults launches get.
            FitHeightText = Settings.VrPlayerHeightCm is { } height
                ? ShowFit(nameof(FitHeightText), height, inches)
                : "";
            FitLockbarText = ShowFit(
                nameof(FitLockbarText),
                VrFit.LockbarHeight(ini, Settings),
                inches
            );
            FitEyesText = ShowFit(
                nameof(FitEyesText),
                VrFit.EyesBehind(ini, Settings),
                inches,
                eyes: true
            );
            FitSizeIndex = Math.Max(0, Array.IndexOf(FitSizeIds, Settings.VrTableSizing));
            // In "same width" mode a width changed in VPX's menu is the one in use.
            var sameWidth =
                Settings.VrTableSizing == VrFit.SizeSame
                    ? VrFit.Value(ini, "LockbarWidth", Settings.VrSameLockbarWidthCm)
                    : Settings.VrSameLockbarWidthCm;
            FitSameWidthText = ShowFit(nameof(FitSameWidthText), sameWidth, inches);
            FitSummary = "Now: " + VrFit.Describe(ini, Settings);
            // The boxes now show what's saved, so an earlier "Not saved yet" no longer applies.
            FitStatus = "";
        }
        finally
        {
            _loadingFit = false;
        }
    }

    partial void OnFitHeightTextChanged(string value)
    {
        if (!_loadingFit && VrFit.ParseLength(value, FitUseInches) is { } h and >= 100 and <= 230)
        {
            FitLockbarText = VrFit.Format(VrFit.LockbarHeightFor(h), FitUseInches);
        }
        FitChanged();
    }

    partial void OnFitLockbarTextChanged(string value) => FitChanged();

    partial void OnFitEyesTextChanged(string value) => FitChanged();

    partial void OnFitSizeIndexChanged(int value) => FitChanged();

    partial void OnFitSameWidthTextChanged(string value) => FitChanged();

    partial void OnFitResetPositionChanged(bool value) => FitChanged();

    /// <summary>Nothing changes until FIT EVERY TABLE TO ME; says so as soon as a value differs from what's saved.</summary>
    void FitChanged()
    {
        if (!_loadingFit)
        {
            FitStatus = "Not saved yet: click FIT EVERY TABLE TO ME to use these for every table.";
        }
    }

    [RelayCommand]
    async Task ResetVrFitAsync()
    {
        var pick = await _ui.ChoiceAsync(
            "Go back to the default VR fit?",
            $"Every table gets a real cabinet's lockbar ({VrFit.Format(VrFit.StandardLockbarHeight, FitUseInches)} high), "
                + $"your eyes {VrFit.FormatEyes(VrFit.StandardEyesBehind, FitUseInches)} behind it, at its real size, "
                + "and the table goes back straight ahead. Forgets the values you set here and any position set in the headset.",
            ["RESET", "CANCEL"]
        );
        if (pick != 0)
        {
            return;
        }

        try
        {
            VrFit.Reset(VpxSetup.VrIniPath(Settings));
        }
        catch (Exception e)
        {
            FitStatus = "Couldn't reset the VR settings: " + e.Message;
            return;
        }
        Settings.VrPlayerHeightCm = null;
        var defaults = new AppSettings();
        Settings.VrTableSizing = defaults.VrTableSizing;
        Settings.VrSameLockbarWidthCm = defaults.VrSameLockbarWidthCm;
        Settings.VrFitSaved = false;
        Settings.Save();
        FitResetPosition = true;
        LoadVrView();
        FitStatus = "✓ Back to the defaults, for every table, from the next table you start.";
    }

    partial void OnFitUseInchesChanged(bool value)
    {
        if (_loadingFit)
        {
            return;
        }

        // Re-show each box in the new unit, keeping its exact length (read in the old unit if it was typed).
        string Convert(string field, string text, bool eyes = false)
        {
            var cm =
                _fitExact.TryGetValue(field, out var e) && e.Text == text
                    ? e.Cm
                    : VrFit.ParseLength(text, !value);
            return cm is { } c ? ShowFit(field, c, value, eyes) : text;
        }
        _loadingFit = true;
        try
        {
            FitHeightText = Convert(nameof(FitHeightText), FitHeightText);
            FitLockbarText = Convert(nameof(FitLockbarText), FitLockbarText);
            FitSameWidthText = Convert(nameof(FitSameWidthText), FitSameWidthText);
            FitEyesText = Convert(nameof(FitEyesText), FitEyesText, eyes: true);
        }
        finally
        {
            _loadingFit = false;
        }
        Settings.VrUseInches = value;
        Settings.Save();
    }

    [RelayCommand]
    void ApplyVrFit()
    {
        var inches = FitUseInches;
        double? height = null;
        if (!string.IsNullOrWhiteSpace(FitHeightText))
        {
            height = FitCm(nameof(FitHeightText), FitHeightText);
            if (height is not (>= 100 and <= 230))
            {
                FitStatus = "Your height doesn't look right: try 178 cm or 5'10\".";
                return;
            }
        }
        var lockbar = FitCm(nameof(FitLockbarText), FitLockbarText);
        if (lockbar is not (>= 40 and <= 130))
        {
            FitStatus = "The lockbar height should be between 40 and 130 cm (16 to 51\").";
            return;
        }
        var eyes = FitCm(nameof(FitEyesText), FitEyesText);
        if (eyes is not (>= VrFit.MinEyesBehind and <= VrFit.MaxEyesBehind))
        {
            FitStatus =
                "Your eyes can be up to 100 cm (39\") behind the lockbar, or up to 70 cm (27\") over the glass (a negative number).";
            return;
        }
        var size = FitSizeIds[Math.Clamp(FitSizeIndex, 0, FitSizeIds.Length - 1)];
        var width = Settings.VrSameLockbarWidthCm;
        if (size == VrFit.SizeSame)
        {
            var w = FitCm(nameof(FitSameWidthText), FitSameWidthText);
            if (w is not (>= 30 and <= 120))
            {
                FitStatus = "The lockbar width should be between 30 and 120 cm (12 to 47\").";
                return;
            }
            width = w.Value;
        }

        try
        {
            VrFit.Write(
                VpxSetup.VrIniPath(Settings),
                new VrFit.Choice(lockbar.Value, eyes.Value, size, width),
                FitResetPosition,
                Settings.VrTableSizing
            );
        }
        catch (Exception e)
        {
            FitStatus = "Couldn't save the VR settings: " + e.Message;
            return;
        }
        Settings.VrPlayerHeightCm = height;
        Settings.VrTableSizing = size;
        Settings.VrSameLockbarWidthCm = width;
        Settings.VrFitSaved = true;
        Settings.Save();
        LoadVrView();
        FitStatus =
            "✓ Saved for every table, from the next table you start. You don't need to click this again: tables you add later get it too. If the lockbar isn't right in front of you, "
            + "stand where you play and press keypad 5 (recenter): VPX remembers it for every table.";
    }

    [RelayCommand]
    async Task ResetVrViewAsync()
    {
        var pick = await _ui.ChoiceAsync(
            "Put the table back where VPX starts it?",
            "Forgets the position, height and turn you set in the headset. You can line it up again with the left thumbstick click while playing.",
            ["RESET", "CANCEL"]
        );
        if (pick != 0)
        {
            return;
        }

        try
        {
            VrView.Reset(VpxSetup.VrIniPath(Settings));
        }
        catch (Exception e)
        {
            VrViewText = "Couldn't reset the position: " + e.Message;
            return;
        }
        LoadVrView();
    }

    // ---- Per-table VR height ---------------------------------------------------------------

    [RelayCommand]
    void ClearTableVrHeight(InstalledItem? item)
    {
        if (item == null)
        {
            return;
        }

        try
        {
            VrFit.WriteTableHeight(item.Table.TablePath, null);
            item.TableVrHeight = null;
            PlayStatus = $"{item.Title} sits at the same height as every table again in VR.";
        }
        catch (Exception e)
        {
            PlayStatus = "Couldn't save the table's settings: " + e.Message;
        }
    }

    // ---- Per-table render scale ------------------------------------------------------------

    [RelayCommand]
    void SetTableScale(object? parameter)
    {
        if (parameter is not TableScaleChoice c)
        {
            return;
        }

        try
        {
            VpxGraphics.WriteTableScale(c.Item.Table.TablePath, c.Scale);
            c.Item.TableScale = c.Scale;
            PlayStatus = c.Scale is { } s
                ? $"{c.Item.Title} will render at {ScaleText(s)} (other tables use {GfxRenderScale})."
                : $"{c.Item.Title} uses the global render scale again ({GfxRenderScale}).";
        }
        catch (Exception e)
        {
            PlayStatus = "Couldn't save the table's settings: " + e.Message;
        }
    }

    // ---- Pre-launch checks -------------------------------------------------------------------

    /// <summary>
    /// Everything that would make a VR launch fail or play badly, asked about once before starting:
    /// the headset, the OpenXR runtime, the table file and its ROM. Returns false if the user cancels.
    /// </summary>
    async Task<bool> PreflightAsync(InstalledItem item, IPinballEngine engine)
    {
        if (!await EnsureHeadsetAsync())
        {
            return false;
        }

        if (item.Table.IsVpx && !File.Exists(item.Table.TablePath))
        {
            await _ui.ChoiceAsync(
                "Table file missing",
                $"{item.Table.TablePath}\n\nisn't there any more. Reinstall the table from Get Tables.",
                ["OK"]
            );
            return false;
        }

        if (item.NeedsRom)
        {
            List<string> buttons = item.CanGetRom
                ? ["GET ROM", "PLAY ANYWAY", "CANCEL"]
                : ["ADD ROM", "PLAY ANYWAY", "CANCEL"];
            var pick = await _ui.ChoiceAsync(
                "This table's ROM isn't installed",
                $"{item.RomStatus}. Without it the table shows a PinMAME error and closes.",
                [.. buttons]
            );
            switch (pick < 0 ? "CANCEL" : buttons[pick])
            {
                case "GET ROM":
                    _ = GetRomAsync(item.Table);
                    return false;
                case "ADD ROM":
                    await AddRomAsync(item);
                    return false;
                case "PLAY ANYWAY":
                    break;
                default:
                    return false;
            }
        }

        if (!await OfferDmdFixAsync(item))
        {
            return false;
        }

        // Future Pinball's BAM uses OpenVR (SteamVR) on purpose; the runtime check is for OpenXR games.
        var usesOpenXr = engine.Id != TableManifest.FuturePinball;
        if (
            usesOpenXr
            && Settings.WarnSteamVrRuntime
            && OpenXrRuntimes.Active() is { IsSteamVr: true }
        )
        {
            var meta = OpenXrRuntimes.List().FirstOrDefault(r => r.IsMeta);
            List<string> buttons =
                meta != null
                    ? ["SWITCH TO META QUEST LINK", "PLAY ANYWAY", "DON'T ASK AGAIN", "CANCEL"]
                    : ["PLAY ANYWAY", "DON'T ASK AGAIN", "CANCEL"];
            var pick = await _ui.ChoiceAsync(
                "SteamVR is the active OpenXR runtime",
                "With a Quest, SteamVR adds an extra layer on top of Quest Link or Virtual Desktop, which often costs frames and adds stutter.\n\n"
                    + (
                        meta != null
                            ? "Switching to Meta Quest Link needs administrator permission once. "
                            : ""
                    )
                    + "You can change this any time on Setup → Headset.",
                [.. buttons]
            );
            switch (pick < 0 ? "CANCEL" : buttons[pick])
            {
                case "SWITCH TO META QUEST LINK":
                    if (!await OpenXrRuntimes.SetActiveAsync(meta!))
                    {
                        PlayStatus = "The runtime wasn't changed; playing through SteamVR.";
                    }

                    RefreshVrStatus();
                    break;
                case "DON'T ASK AGAIN":
                    WarnSteamVrRuntime = false;
                    break;
                case "PLAY ANYWAY":
                    break;
                default:
                    return false;
            }
        }

        if (usesOpenXr)
        {
            await ApplyAswAsync();
        }

        return true;
    }

    /// <summary>After a VPX table closes: if it crashed or ended within seconds, show what its log says.</summary>
    async Task ExplainVpxExitAsync(InstalledItem item, DateTime started)
    {
        if (!Settings.ExplainFailedLaunches || !item.Table.IsVpx)
        {
            return;
        }

        var quick = DateTime.Now - started < TimeSpan.FromSeconds(20);
        if (VpxLog.Find(Settings) is not { } log || VpxLog.LastSession(log) is not { } session)
        {
            return;
        }

        if (session.Started is { } t && t < started.AddSeconds(-10))
        {
            return; // not this run's log
        }

        if (!quick && session.ClosedCleanly)
        {
            return;
        }
        // A quick, clean close with nothing wrong (you just left the table): only harmless log lines, no dialog.
        if (session.ClosedCleanly && session.Problems.Count == 0 && session.Hints.Count == 0)
        {
            return;
        }

        await ShowLogSummaryAsync(
            quick && session.ClosedCleanly
                ? $"{item.Title} closed straight away"
                : $"{item.Title} stopped unexpectedly",
            log,
            session
        );
    }

    async Task ShowLogSummaryAsync(string title, string log, VpxLog.Session session)
    {
        var lines = new List<string>();
        if (session.Table != null)
        {
            lines.Add($"Table: {Path.GetFileName(session.Table)}");
        }

        if (session.Started is { } t)
        {
            lines.Add(
                $"Started {t:g} · {(session.ClosedCleanly ? "closed normally" : "did not close normally")} · {(session.UsedVr ? "VR started" : "VR did not start")}"
            );
        }

        lines.AddRange(session.Hints.Select(h => "• " + h));
        var details =
            session.Problems.Count == 0
                ? "No errors in the log."
                : string.Join("\n", session.Problems.Take(30));
        if (session.Harmless.Count > 0)
        {
            details +=
                "\n\nAlso logged, usually harmless (a PuP pack missing some of its files, and the like):\n"
                + string.Join("\n", session.Harmless.Take(15));
        }

        var pick = await _ui.ChoiceAsync(
            title,
            string.Join("\n", lines),
            ["OPEN FULL LOG", "CLOSE"],
            details
        );
        if (pick == 0)
        {
            _ui.OpenExternal(log);
        }
    }

    [RelayCommand]
    async Task ViewLastVpxLogAsync()
    {
        if (VpxLog.Find(Settings) is not { } log)
        {
            await _ui.ChoiceAsync(
                "No VPX log yet",
                "VPX writes its log once a table has been started.",
                ["OK"]
            );
            return;
        }
        if (VpxLog.LastSession(log) is not { } session)
        {
            _ui.OpenExternal(log);
            return;
        }
        await ShowLogSummaryAsync("Last VPX run", log, session);
    }
}

/// <summary>One entry in a card's "Render scale" menu.</summary>
public sealed record TableScaleChoice(InstalledItem Item, string Label, double? Scale);
