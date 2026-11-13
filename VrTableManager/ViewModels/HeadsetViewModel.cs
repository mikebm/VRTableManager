using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Setup → Headset: the OpenXR runtime, refresh rate and ASW.
public sealed partial class HeadsetViewModel(MainViewModel app) : ChildViewModel(app)
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

    public string[] RefreshRates { get; } =
    ["0 (headset default)", "72 Hz", "80 Hz", "90 Hz", "120 Hz"];

    [ObservableProperty]
    public partial string RefreshRate { get; set; } = "";

    partial void OnWarnSteamVrRuntimeChanged(bool value) =>
        SaveSetting(s => s.WarnSteamVrRuntime = value);

    partial void OnRefreshRateChanged(string value) =>
        SaveSetting(s => s.VrRefreshRate = int.TryParse(value?.Split(' ')[0], out var r) ? r : 0);

    partial void OnAswModeChanged(string value) => SaveSetting(s => s.MetaAswMode = value);

    internal override void LoadSettings(AppSettings s)
    {
        RefreshRate =
            RefreshRates.FirstOrDefault(r => r.StartsWith(s.VrRefreshRate.ToString()))
            ?? RefreshRates[0];
        AswMode = AswModes.Contains(s.MetaAswMode) ? s.MetaAswMode : AswModes[0];
        WarnSteamVrRuntime = s.WarnSteamVrRuntime;
    }

    internal static string MetaLinkApp =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Oculus",
            "Support",
            "oculus-client",
            "OculusClient.exe"
        );

    public bool HasMetaLinkApp => File.Exists(MetaLinkApp);
    public bool HasOculusDebugTool => File.Exists(MetaAsw.DebugToolPath);

    internal void RefreshVrStatus()
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

        var pick = await Ui.ChoiceAsync(
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
        App.RefreshSetupStatus();
        if (!ok)
        {
            RuntimeAdvice =
                "The runtime wasn't changed (the administrator prompt was declined or failed). "
                + RuntimeAdvice;
        }
    }

    [RelayCommand]
    void OpenMetaLinkApp() => Ui.OpenExternal(MetaLinkApp);

    [RelayCommand]
    void OpenOculusDebugTool() => Ui.OpenExternal(MetaAsw.DebugToolPath);

    // ---- ASW (Meta Quest Link) -------------------------------------------------------------

    public string[] AswModes { get; } = MetaAsw.Modes;

    [ObservableProperty]
    public partial string AswMode { get; set; } = MetaAsw.Modes[0];

    [ObservableProperty]
    public partial bool IsAswAvailable { get; set; }

    /// <summary>Before a VR launch through Meta Quest Link: applies the chosen ASW mode (it resets when the Oculus service restarts).</summary>
    internal async Task ApplyAswAsync()
    {
        if (Settings.MetaAswMode == MetaAsw.Modes[0] || OpenXrRuntimes.Active()?.IsMeta != true)
        {
            return;
        }

        await MetaAsw.ApplyAsync(Settings.MetaAswMode);
    }
}
