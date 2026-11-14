using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Setup → Fit: where the table sits in VR, and fitting every table to you.
public sealed partial class FitViewModel(MainViewModel app) : ChildViewModel(app)
{
    // ---- Where the table sits in VR ----------------------------------------------------------

    /// <summary>The table position VPX saved (moved in the headset), or that it's at VPX's default.</summary>
    [ObservableProperty]
    public partial string VrViewText { get; set; } = "";

    internal void LoadVrView()
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
        "The same width for every table (a cabinet or controller stand)",
        "VPX's default sizing (70 cm lockbar)",
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
        var pick = await Ui.ChoiceAsync(
            "Go back to the default VR fit?",
            $"Every table gets a real cabinet's lockbar ({VrFit.Format(VrFit.StandardLockbarHeight, FitUseInches)} high), "
                + $"your eyes {VrFit.FormatEyes(VrFit.StandardEyesBehind, FitUseInches)} behind it, at its real size, "
                + "and the table goes back straight ahead, unless your own VPinballX.ini sets them. "
                + "Forgets the values you set here and any position set in the headset.",
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
        var pick = await Ui.ChoiceAsync(
            "Forget the position you set in the headset?",
            "Forgets the position, height and turn you set in the headset, so the table goes back to where VR play starts it "
                + "(your own VPinballX.ini still counts). You can line it up again with the left thumbstick click while playing.",
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
}
