using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Lobby;

namespace VrTableManager.ViewModels;

// Setup → VR lobby → Cabinets: how big the lobby's cabinets are and how many stand in its ring (LobbyLayout).
// The lobby reads them from the settings file when it starts, like its other settings.
public sealed partial class MainViewModel
{
    public IReadOnlyList<int> LobbyCabinetCounts => LobbyLayout.Counts;
    public IReadOnlyList<int> LobbyCabinetScales => LobbyLayout.Scales;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(LobbyRingSummary))]
    public partial int LobbyCabinetCount { get; set; } = LobbyLayout.DefaultCount;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(LobbyRingSummary))]
    public partial int LobbyCabinetScale { get; set; } = LobbyLayout.DefaultScalePercent;

    partial void OnLobbyCabinetCountChanged(int value)
    {
        // A hand-edited settings file can hold any number; Setup only offers the ones the lobby uses.
        var count = LobbyLayout.ClampCount(value);
        if (count != value)
        {
            LobbyCabinetCount = count;
            return;
        }
        if (Settings.LobbyCabinetCount == count)
        {
            return;
        }
        Settings.LobbyCabinetCount = count;
        Settings.Save();
    }

    partial void OnLobbyCabinetScaleChanged(int value)
    {
        var scale = LobbyLayout.ClampScale(value);
        if (scale != value)
        {
            LobbyCabinetScale = scale;
            return;
        }
        if (Settings.LobbyCabinetScalePercent == scale)
        {
            return;
        }
        Settings.LobbyCabinetScalePercent = scale;
        Settings.Save();
    }

    /// <summary>What the lobby will do with these: how many fit, how far apart, how far away.</summary>
    public string LobbyRingSummary =>
        LobbyLayout.Describe(
            LobbyLayout.Ring(LobbyCabinetCount, LobbyCabinetScale),
            LobbyCabinetCount
        );

    [RelayCommand]
    void ResetLobbyCabinets()
    {
        LobbyCabinetCount = LobbyLayout.DefaultCount;
        LobbyCabinetScale = LobbyLayout.DefaultScalePercent;
    }
}
