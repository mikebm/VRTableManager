using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Setup → Graphics: VPX's graphics settings for VR play (in the VR ini).
public sealed partial class GraphicsViewModel(MainViewModel app) : ChildViewModel(app)
{
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

    partial void OnUseVrGraphicsChanged(bool value)
    {
        SaveSetting(s => s.UseVrGraphics = value);
        if (_loadingGraphics)
        {
            return;
        }

        LoadGraphics(); // now shows what VR play will use: the panel's, or VPX's own
        GraphicsStatus = value
            ? "✓ VR play uses these settings again from the next table."
            : "✓ VR play uses VPX's own graphics settings from the next table. These are kept for when you turn them back on.";
    }

    internal override void LoadSettings(AppSettings s)
    {
        _loadingGraphics = true; // loading, not a change: no status line
        UseVrGraphics = s.UseVrGraphics;
        _loadingGraphics = false;
    }

    bool _loadingGraphics;

    internal static string ScaleText(double s) => $"{Math.Round(s * 100):0}%";

    static double ParseScale(string s) =>
        double.TryParse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
            ? p / 100
            : 1.0;

    internal void LoadGraphics()
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
            App.Fit.LoadVrView();
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
}
