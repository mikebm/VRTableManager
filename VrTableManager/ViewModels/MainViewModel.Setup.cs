using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VrTableManager.ViewModels;

// Setup's section rail: which section shows, remembered in settings so Setup opens where you left it.
public sealed partial class MainViewModel
{
    /// <summary>The rail's sections, in order (the keys the view's buttons and pages use).</summary>
    public static readonly string[] SetupSections =
    [
        "vpx",
        "play",
        "controls",
        "headset",
        "fit",
        "graphics",
        "lobby",
        "downloads",
        "games",
        "updates",
        "maintenance",
    ];

    [ObservableProperty]
    public partial string SetupSection { get; set; } = SetupSections[0];

    partial void OnSetupSectionChanged(string value)
    {
        // An unknown key (an older or hand-edited settings file) would leave Setup blank: show VPX instead.
        if (!SetupSections.Contains(value))
        {
            SetupSection = SetupSections[0];
            return;
        }
        if (Settings.SetupSection == value)
        {
            return;
        }
        Settings.SetupSection = value;
        Settings.Save();
    }

    /// <summary>The NEXT button at the foot of each Setup section.</summary>
    [RelayCommand]
    void ShowSetupSection(string section) => SetupSection = section;
}
