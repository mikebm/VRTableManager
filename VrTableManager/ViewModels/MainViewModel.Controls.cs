using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

/// <summary>One VPX action on Setup → Controls: the key it has in desktop and in VR play.</summary>
public sealed partial class ControlRow(VpxKeyMap.Control control, bool firstInGroup)
    : ObservableObject
{
    public VpxKeyMap.Control Control { get; } = control;
    public string Label => Control.Label;

    /// <summary>The group's heading, on its first row only.</summary>
    public string GroupHeader { get; } = firstInGroup ? control.Group.ToUpperInvariant() : "";

    [ObservableProperty]
    public partial string DesktopKeys { get; set; } = "";

    [ObservableProperty]
    public partial string VrKeys { get; set; } = "";

    /// <summary>Waiting for the key to give this action.</summary>
    [ObservableProperty]
    public partial bool IsCapturing { get; set; }
}

// Setup → Controls: VPX's keyboard mappings, set here instead of in VPX's in-game menu.
public sealed partial class MainViewModel
{
    public ObservableCollection<ControlRow> ControlRows { get; } = [];

    /// <summary>Where a key set here is saved, in <see cref="ControlsTarget"/> order.</summary>
    public string[] ControlsTargets { get; } =
    ["Desktop and VR play (your own VPinballX.ini)", "VR play only", "Desktop play only"];

    [ObservableProperty]
    public partial int ControlsTargetIndex { get; set; }

    partial void OnControlsTargetIndexChanged(int value)
    {
        if (value < 0 || (int)Settings.ControlsTarget == value)
        {
            return;
        }

        Settings.ControlsTarget = (ControlsTarget)value;
        Settings.Save();
    }

    /// <summary>VPX 10.8.1 or later: the only format this writes.</summary>
    [ObservableProperty]
    public partial bool ControlsSupported { get; set; }

    /// <summary>What the last key change did (or why it didn't), shown above the list.</summary>
    [ObservableProperty]
    public partial string KeyMapStatus { get; set; } = "";

    /// <summary>The row waiting for a key, or null.</summary>
    public ControlRow? CapturingRow => ControlRows.FirstOrDefault(r => r.IsCapturing);

    /// <summary>Reads the keys desktop and VR play use now.</summary>
    public void RefreshControls()
    {
        ControlsTargetIndex = (int)Settings.ControlsTarget;
        try
        {
            ControlsSupported = VpxKeyMap.IsSupported(Settings);
            var desktop = VpxIniLayers.Effective(Settings, vr: false);
            var vr = VpxIniLayers.Effective(Settings, vr: true);
            if (ControlRows.Count == 0)
            {
                string? group = null;
                foreach (var c in VpxKeyMap.Controls)
                {
                    ControlRows.Add(new ControlRow(c, c.Group != group));
                    group = c.Group;
                }
            }

            foreach (var row in ControlRows)
            {
                var c = row.Control;
                row.DesktopKeys = VpxKeyMap.Describe(
                    VpxKeyMap.Value(desktop, c.Mapping),
                    c.DefaultKey
                );
                row.VrKeys = VpxKeyMap.Describe(VpxKeyMap.Value(vr, c.Mapping), c.DefaultKey);
            }
        }
        catch (Exception e)
        {
            KeyMapStatus = "Couldn't read VPX's controls: " + e.Message;
        }
    }

    /// <summary>SET on a row: wait for a key (again to stop waiting).</summary>
    [RelayCommand]
    void CaptureKey(ControlRow row)
    {
        var was = row.IsCapturing;
        foreach (var r in ControlRows)
        {
            r.IsCapturing = false;
        }

        row.IsCapturing = !was;
        KeyMapStatus = row.IsCapturing
            ? $"Press the key for {row.Label}. Click SET again to cancel."
            : "";
    }

    public void CancelKeyCapture()
    {
        if (CapturingRow is { } row)
        {
            row.IsCapturing = false;
            KeyMapStatus = "";
        }
    }

    /// <summary>
    /// The key pressed while a row waits, by its physical key's name (Avalonia's <c>PhysicalKey</c>). Returns whether
    /// a row was waiting, so the key goes nowhere else.
    /// </summary>
    public bool KeyPressedForCapture(string physicalKey)
    {
        if (CapturingRow is not { } row)
        {
            return false;
        }

        if (VpxKeyMap.FromCode(physicalKey) is not { } sdl)
        {
            KeyMapStatus =
                $"VPX can't use that key ({physicalKey}). Press another, or click SET again to cancel.";
            return true;
        }

        row.IsCapturing = false;
        var target = Settings.ControlsTarget; // the saved choice, not the dropdown's index (-1 while it's torn down)
        try
        {
            var status = VpxKeyMap.Set(Settings, row.Control.Mapping, sdl, target);
            // The modes the key now applies in: both for "desktop and VR".
            var modes = target switch
            {
                ControlsTarget.VrOnly => new[] { true },
                ControlsTarget.DesktopOnly => [false],
                _ => [false, true],
            };
            var shared = modes
                .SelectMany(vr =>
                    VpxKeyMap.SharedWith(
                        VpxIniLayers.Effective(Settings, vr),
                        row.Control.Mapping,
                        sdl
                    )
                )
                .Distinct()
                .ToList();
            KeyMapStatus =
                status
                + (
                    shared.Count > 0
                        ? $" {VpxKeyMap.KeyName(sdl)} also does: {string.Join(", ", shared)}."
                        : ""
                );
        }
        catch (Exception e)
        {
            KeyMapStatus = "Couldn't save the key: " + e.Message;
        }

        RefreshControls();
        return true;
    }
}
