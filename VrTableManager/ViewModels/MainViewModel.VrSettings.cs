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
    [ObservableProperty] public partial string RuntimeTitle { get; set; } = "";
    [ObservableProperty] public partial string RuntimeAdvice { get; set; } = "";
    [ObservableProperty] public partial bool IsRuntimeWarning { get; set; }
    [ObservableProperty] public partial bool IsMetaRuntime { get; set; }
    [ObservableProperty] public partial bool WarnSteamVrRuntime { get; set; }
    [ObservableProperty] public partial bool ExplainFailedLaunches { get; set; }

    public bool HasMetaLinkApp => File.Exists(MetaLinkApp);
    public bool HasOculusDebugTool => File.Exists(MetaAsw.DebugToolPath);

    void RefreshVrStatus()
    {
        Runtimes.Clear();
        foreach (var r in OpenXrRuntimes.List()) Runtimes.Add(r);
        var active = Runtimes.FirstOrDefault(r => r.IsActive);
        IsMetaRuntime = active?.IsMeta == true;
        IsRuntimeWarning = active == null || active.IsSteamVr;
        RuntimeTitle = active == null ? "No OpenXR runtime is active" : $"Active: {active.Name}";
        RuntimeAdvice = active switch
        {
            null => "Install the Meta Quest Link app (or Virtual Desktop), then make it the active runtime below.",
            { IsMeta: true } => "Best for Quest Link and Air Link: VPX talks to the headset directly. Set the refresh rate and render resolution in the Meta Quest Link app (Devices → your Quest → Graphics preferences). ASW is set below.",
            { IsSteamVr: true } => "SteamVR adds a layer between VPX and a Quest, which often costs frames and adds stutter. Unless you need it, switch to Meta Quest Link (cable/Air Link) or Virtual Desktop (VDXR). In SteamVR, turn Motion Smoothing off for pinball.",
            { Name: "Virtual Desktop (VDXR)" } => "Good over Wi-Fi 6/6E. In Virtual Desktop's Streaming tab, try a frame rate the PC can always hold, and leave Synchronous Spacewarp off for pinball (it smears the ball).",
            _ => "VPX uses this runtime for VR.",
        };
        IsAswAvailable = MetaAsw.IsAvailable;
    }

    [RelayCommand]
    async Task MakeRuntimeActiveAsync(OpenXrRuntime? runtime)
    {
        if (runtime == null || runtime.IsActive) return;
        var pick = await _ui.ChoiceAsync($"Use {runtime.Name} for VR?",
            $"VPX, UEVR (in OpenXR mode) and other OpenXR games will use {runtime.Name}.\n\n" +
            "Windows will ask for administrator permission, because this setting is shared by every user on the PC.",
            ["SWITCH", "CANCEL"]);
        if (pick != 0) return;
        var ok = await OpenXrRuntimes.SetActiveAsync(runtime);
        RefreshVrStatus();
        RefreshSetupStatus();
        if (!ok) RuntimeAdvice = "The runtime wasn't changed (the administrator prompt was declined or failed). " + RuntimeAdvice;
    }

    [RelayCommand]
    void OpenMetaLinkApp() => _ui.OpenExternal(MetaLinkApp);

    [RelayCommand]
    void OpenOculusDebugTool() => _ui.OpenExternal(MetaAsw.DebugToolPath);

    // ---- ASW (Meta Quest Link) -------------------------------------------------------------

    public string[] AswModes { get; } = MetaAsw.Modes;
    [ObservableProperty] public partial string AswMode { get; set; } = MetaAsw.Modes[0];
    [ObservableProperty] public partial bool IsAswAvailable { get; set; }

    /// <summary>Before a VR launch through Meta Quest Link: applies the chosen ASW mode (it resets when the Oculus service restarts).</summary>
    async Task ApplyAswAsync()
    {
        if (Settings.MetaAswMode == MetaAsw.Modes[0] || OpenXrRuntimes.Active()?.IsMeta != true) return;
        await MetaAsw.ApplyAsync(Settings.MetaAswMode);
    }

    // ---- VPX graphics (VR ini) ---------------------------------------------------------------

    public string[] GraphicsPresets { get; } = VpxGraphics.Presets.Select(p => p.Name).ToArray();
    public string[] RenderScales { get; } = ["60%", "70%", "80%", "90%", "100%", "110%", "120%", "130%", "150%", "175%", "200%"];
    public string[] MsaaNames { get; } = VpxGraphics.MsaaNames;
    public string[] PostAaNames { get; } = VpxGraphics.PostAaNames;
    public string[] ReflectionNames { get; } = VpxGraphics.ReflectionNames;
    public string[] AoNames { get; } = VpxGraphics.AoNames;
    public string[] SharpenNames { get; } = VpxGraphics.SharpenNames;
    public string[] FpsNames { get; } = VpxGraphics.FpsNames;

    [ObservableProperty] public partial string GfxRenderScale { get; set; } = "100%";
    [ObservableProperty] public partial int GfxMsaa { get; set; }
    [ObservableProperty] public partial int GfxPostAa { get; set; }
    [ObservableProperty] public partial int GfxReflections { get; set; }
    [ObservableProperty] public partial int GfxAo { get; set; }
    [ObservableProperty] public partial bool GfxBloom { get; set; }
    [ObservableProperty] public partial int GfxSharpen { get; set; }
    [ObservableProperty] public partial int GfxFps { get; set; }
    [ObservableProperty] public partial string GraphicsPresetLabel { get; set; } = "";
    [ObservableProperty] public partial string GraphicsStatus { get; set; } = "";

    bool _loadingGraphics;

    static string ScaleText(double s) => $"{Math.Round(s * 100):0}%";
    static double ParseScale(string s) =>
        double.TryParse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p / 100 : 1.0;

    void LoadGraphics()
    {
        _loadingGraphics = true;
        try
        {
            var g = VpxGraphics.Read(VpxSetup.VrIniPath(Settings));
            GfxRenderScale = RenderScales.FirstOrDefault(s => Math.Abs(ParseScale(s) - g.RenderScale) < 0.001) ?? ScaleText(g.RenderScale);
            GfxMsaa = g.Msaa;
            GfxPostAa = g.PostAa;
            GfxReflections = g.Reflections;
            GfxAo = g.AmbientOcclusion;
            GfxBloom = g.Bloom;
            GfxSharpen = g.Sharpen;
            GfxFps = g.FpsOverlay;
            GraphicsPresetLabel = g.PresetName is { } p ? $"Matches the {p} preset" : "Custom settings";
        }
        finally { _loadingGraphics = false; }
    }

    VpxGraphics CurrentGraphics() => new()
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
        if (_loadingGraphics) return;
        try
        {
            var g = CurrentGraphics();
            g.Write(VpxSetup.VrIniPath(Settings));
            GraphicsPresetLabel = g.PresetName is { } p ? $"Matches the {p} preset" : "Custom settings";
            GraphicsStatus = "✓ Saved. Takes effect the next time a table starts.";
        }
        catch (Exception e) { GraphicsStatus = "Couldn't save the VR settings: " + e.Message; }
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
        if (VpxGraphics.Presets.FirstOrDefault(p => p.Name == name).Settings is not { } preset) return;
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
        finally { _loadingGraphics = false; }
        SaveGraphics();
    }

    // ---- Per-table render scale ------------------------------------------------------------

    [RelayCommand]
    void SetTableScale(object? parameter)
    {
        if (parameter is not TableScaleChoice c) return;
        try
        {
            VpxGraphics.WriteTableScale(c.Item.Table.TablePath, c.Scale);
            c.Item.TableScale = c.Scale;
            PlayStatus = c.Scale is { } s
                ? $"{c.Item.Title} will render at {ScaleText(s)} (other tables use {GfxRenderScale})."
                : $"{c.Item.Title} uses the global render scale again ({GfxRenderScale}).";
        }
        catch (Exception e) { PlayStatus = "Couldn't save the table's settings: " + e.Message; }
    }

    // ---- Pre-launch checks -------------------------------------------------------------------

    /// <summary>
    /// Everything that would make a VR launch fail or play badly, asked about once before starting:
    /// the headset, the OpenXR runtime, the table file and its ROM. Returns false if the user cancels.
    /// </summary>
    async Task<bool> PreflightAsync(InstalledItem item, IPinballEngine engine)
    {
        if (!await EnsureHeadsetAsync()) return false;

        if (item.Table.IsVpx && !File.Exists(item.Table.TablePath))
        {
            await _ui.ChoiceAsync("Table file missing", $"{item.Table.TablePath}\n\nisn't there any more. Reinstall the table from Get Tables.", ["OK"]);
            return false;
        }

        if (item.NeedsRom)
        {
            List<string> buttons = item.CanGetRom ? ["GET ROM", "PLAY ANYWAY", "CANCEL"] : ["ADD ROM", "PLAY ANYWAY", "CANCEL"];
            var pick = await _ui.ChoiceAsync("This table's ROM isn't installed",
                $"{item.RomStatus}. Without it the table shows a PinMAME error and closes.", [.. buttons]);
            switch (pick < 0 ? "CANCEL" : buttons[pick])
            {
                case "GET ROM": _ = GetRomAsync(item.Table); return false;
                case "ADD ROM": await AddRomAsync(item); return false;
                case "PLAY ANYWAY": break;
                default: return false;
            }
        }

        // Future Pinball's BAM uses OpenVR (SteamVR) on purpose; the runtime check is for OpenXR games.
        var usesOpenXr = engine.Id != TableManifest.FuturePinball;
        if (usesOpenXr && Settings.WarnSteamVrRuntime && OpenXrRuntimes.Active() is { IsSteamVr: true })
        {
            var meta = OpenXrRuntimes.List().FirstOrDefault(r => r.IsMeta);
            List<string> buttons = meta != null ? ["SWITCH TO META QUEST LINK", "PLAY ANYWAY", "DON'T ASK AGAIN", "CANCEL"] : ["PLAY ANYWAY", "DON'T ASK AGAIN", "CANCEL"];
            var pick = await _ui.ChoiceAsync("SteamVR is the active OpenXR runtime",
                "With a Quest, SteamVR adds an extra layer on top of Quest Link or Virtual Desktop, which often costs frames and adds stutter.\n\n" +
                (meta != null ? "Switching to Meta Quest Link needs administrator permission once. " : "") +
                "You can change this any time on Setup → VR & graphics.", [.. buttons]);
            switch (pick < 0 ? "CANCEL" : buttons[pick])
            {
                case "SWITCH TO META QUEST LINK":
                    if (!await OpenXrRuntimes.SetActiveAsync(meta!)) PlayStatus = "The runtime wasn't changed; playing through SteamVR.";
                    RefreshVrStatus();
                    break;
                case "DON'T ASK AGAIN": WarnSteamVrRuntime = false; break;
                case "PLAY ANYWAY": break;
                default: return false;
            }
        }

        if (usesOpenXr) await ApplyAswAsync();
        return true;
    }

    /// <summary>After a VPX table closes: if it crashed or ended within seconds, show what its log says.</summary>
    async Task ExplainVpxExitAsync(InstalledItem item, DateTime started)
    {
        if (!Settings.ExplainFailedLaunches || !item.Table.IsVpx) return;
        var quick = DateTime.Now - started < TimeSpan.FromSeconds(20);
        if (VpxLog.Find(Settings) is not { } log || VpxLog.LastSession(log) is not { } session) return;
        if (session.Started is { } t && t < started.AddSeconds(-10)) return; // not this run's log
        if (!quick && session.ClosedCleanly) return;
        await ShowLogSummaryAsync(quick && session.ClosedCleanly ? $"{item.Title} closed straight away" : $"{item.Title} stopped unexpectedly", log, session);
    }

    async Task ShowLogSummaryAsync(string title, string log, VpxLog.Session session)
    {
        var lines = new List<string>();
        if (session.Table != null) lines.Add($"Table: {Path.GetFileName(session.Table)}");
        if (session.Started is { } t) lines.Add($"Started {t:g} · {(session.ClosedCleanly ? "closed normally" : "did not close normally")} · {(session.UsedVr ? "VR started" : "VR did not start")}");
        lines.AddRange(session.Hints.Select(h => "• " + h));
        var details = session.Problems.Count == 0 ? "No errors in the log." : string.Join("\n", session.Problems.Take(30));
        var pick = await _ui.ChoiceAsync(title, string.Join("\n", lines), ["OPEN FULL LOG", "CLOSE"], details);
        if (pick == 0) _ui.OpenExternal(log);
    }

    [RelayCommand]
    async Task ViewLastVpxLogAsync()
    {
        if (VpxLog.Find(Settings) is not { } log) { await _ui.ChoiceAsync("No VPX log yet", "VPX writes its log once a table has been started.", ["OK"]); return; }
        if (VpxLog.LastSession(log) is not { } session) { _ui.OpenExternal(log); return; }
        await ShowLogSummaryAsync("Last VPX run", log, session);
    }
}

/// <summary>One entry in a card's "Render scale" menu.</summary>
public sealed record TableScaleChoice(InstalledItem Item, string Label, double? Scale);
