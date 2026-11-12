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

    /// <summary>While it waits: the key is added to the action's keys (ADD) rather than replacing them (SET).</summary>
    public bool CaptureAdds { get; set; }
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
                    c.DefaultKey,
                    d => ControllerName(d, desktop)
                );
                row.VrKeys = VpxKeyMap.Describe(
                    VpxKeyMap.Value(vr, c.Mapping),
                    c.DefaultKey,
                    d => ControllerName(d, vr)
                );
            }
        }
        catch (Exception e)
        {
            KeyMapStatus = "Couldn't read VPX's controls: " + e.Message;
        }
    }

    /// <summary>Controller names by device (<c>SDLJoy_…_1</c>), from VPX's settings and the controllers seen here.</summary>
    readonly Dictionary<string, string> _controllerNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The controllers watched while a row waits; null otherwise (VPX's SDL library isn't held).</summary>
    JoystickCapture? _joysticks;

    Avalonia.Threading.DispatcherTimer? _joystickTimer;

    string? ControllerName(
        string device,
        IReadOnlyDictionary<(string Section, string Key), string> ini
    ) =>
        _controllerNames.TryGetValue(device, out var n) ? n
        : ini.GetValueOrDefault((VpxInput.Section, $"Device.{device}.Name"))
            is { Length: > 0 } saved
            ? saved
        : null;

    /// <summary>SET on a row: wait for the key that replaces its keyboard keys (click again to stop waiting).</summary>
    [RelayCommand]
    void CaptureKey(ControlRow row) => StartCapture(row, adds: false);

    /// <summary>ADD on a row: wait for one more key for it, its other keys still working.</summary>
    [RelayCommand]
    void AddKey(ControlRow row) => StartCapture(row, adds: true);

    void StartCapture(ControlRow row, bool adds)
    {
        var was = row.IsCapturing;
        foreach (var r in ControlRows)
        {
            r.IsCapturing = false;
        }

        row.CaptureAdds = adds;
        row.IsCapturing = !was;
        if (!row.IsCapturing)
        {
            PauseControllers();
            KeyMapStatus = "";
            return;
        }

        var controllers = WatchControllers(out var why);
        KeyMapStatus =
            (
                adds
                    ? $"Press the key or controller button to add to {row.Label}."
                    : $"Press the key or controller button for {row.Label}."
            )
            + " Click the button again to cancel."
            + (
                why != null ? " " + why
                : controllers == 0
                    ? " (No controller is plugged in: plug one in now, or use the keyboard.)"
                : ""
            );
    }

    /// <summary>
    /// Starts reading game controllers and cabinet boards through VPX's SDL library, polled on the UI thread. The
    /// library stays loaded while the Controls section is open (<see cref="ReleaseControllers"/>). Returns how many
    /// are plugged in; <paramref name="why"/> says why they can't be read (no VPX yet).
    /// </summary>
    int WatchControllers(out string? why)
    {
        why = null;
        if (_joysticks == null)
        {
            _joysticks = JoystickCapture.TryOpen(Settings, out why);
            if (_joysticks == null)
            {
                return 0;
            }
        }
        else
        {
            _joysticks.Restart(); // what's held now doesn't count
        }

        if (_joystickTimer == null)
        {
            _joystickTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(30),
            };
            _joystickTimer.Tick += (_, _) => PollControllers();
            _joystickTimer.Start();
        }

        return _joysticks.Count;
    }

    /// <summary>No row waits: stop polling, keeping the library for the next SET or ADD.</summary>
    void PauseControllers()
    {
        _joystickTimer?.Stop();
        _joystickTimer = null;
        if (_joysticks != null)
        {
            foreach (var (device, name) in _joysticks.Names)
            {
                _controllerNames[device] = name;
            }
        }
    }

    /// <summary>
    /// Leaving the Controls section, the window or the app: lets VPX's SDL library go, so a VPX update can replace it.
    /// </summary>
    public void ReleaseControllers()
    {
        PauseControllers();
        if (_joysticks == null)
        {
            return;
        }

        var joysticks = _joysticks;
        _joysticks = null;
        try
        {
            joysticks.Dispose();
        }
        catch (Exception e)
        {
            KeyMapStatus = "Couldn't close the controllers: " + e.Message;
        }
    }

    void PollControllers()
    {
        if (_joysticks == null)
        {
            return;
        }

        if (CapturingRow is not { } row)
        {
            PauseControllers();
            return;
        }

        try
        {
            if (_joysticks.Poll() is { } pressed)
            {
                foreach (var (device, name) in _joysticks.Names)
                {
                    _controllerNames[device] = name;
                }

                Capture(row, pressed.Binding);
            }
        }
        catch (Exception e)
        {
            ReleaseControllers();
            KeyMapStatus =
                "Couldn't read the controllers: " + e.Message + " The keyboard still works.";
        }
    }

    /// <summary>RESET on a row: its keyboard key back to VPX's default, where "Save keys for" points.</summary>
    [RelayCommand]
    void ResetKey(ControlRow row)
    {
        CancelKeyCapture();
        try
        {
            var target = Settings.ControlsTarget;
            var status = VpxKeyMap.Reset(Settings, row.Control.Mapping, target);
            KeyMapStatus =
                status + AlsoDoes(row, VpxKeyMap.KeyInput(row.Control.DefaultKey), target);
        }
        catch (Exception e)
        {
            KeyMapStatus = "Couldn't reset the key: " + e.Message;
        }

        RefreshControls();
    }

    public void CancelKeyCapture()
    {
        PauseControllers();
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
                $"VPX can't use that key ({physicalKey}). Press another, or click the button again to cancel.";
            return true;
        }

        Capture(row, VpxKeyMap.KeyInput(sdl));
        return true;
    }

    /// <summary>The key or controller button given to the waiting row: saved where "Save keys for" points.</summary>
    void Capture(ControlRow row, string input)
    {
        row.IsCapturing = false;
        PauseControllers();
        var target = Settings.ControlsTarget; // the saved choice, not the dropdown's index (-1 while it's torn down)
        string? Name(string d) => _controllerNames.GetValueOrDefault(d);
        try
        {
            var status = row.CaptureAdds
                ? VpxKeyMap.Add(Settings, row.Control.Mapping, input, target, Name)
                : VpxKeyMap.Set(Settings, row.Control.Mapping, input, target, Name);
            KeyMapStatus = status + AlsoDoes(row, input, target);
        }
        catch (Exception e)
        {
            KeyMapStatus = "Couldn't save it: " + e.Message;
        }

        RefreshControls();
    }

    /// <summary>" Z also does: Nudge left." when other actions use the input where it now applies, else "".</summary>
    string AlsoDoes(ControlRow row, string input, ControlsTarget target)
    {
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
                    input
                )
            )
            .Distinct()
            .ToList();
        return shared.Count > 0
            ? $" {VpxKeyMap.InputName(input, d => _controllerNames.GetValueOrDefault(d))} also does: {string.Join(", ", shared)}."
            : "";
    }
}
