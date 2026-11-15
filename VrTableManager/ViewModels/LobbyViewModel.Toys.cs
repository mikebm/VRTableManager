using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Lobby;

namespace VrTableManager.ViewModels;

// Setup → VR lobby: the saved 3D toys (ToyFiles), their size limit and clearing them.
public sealed partial class LobbyViewModel
{
    /// <summary>The sizes Setup offers for the saved toys, as shown: "Unlimited", "1 GB"…</summary>
    public IReadOnlyList<string> LobbyToyCacheChoices { get; } =
        ToyFiles.LimitsGb.Select(gb => gb <= 0 ? "Unlimited" : $"{gb} GB").ToList();

    /// <summary>Which of <see cref="LobbyToyCacheChoices"/> is chosen (AppSettings.LobbyToyCacheGb).</summary>
    [ObservableProperty]
    public partial int LobbyToyCacheIndex { get; set; }

    /// <summary>How much the saved toys take now.</summary>
    [ObservableProperty]
    public partial string LobbyToyCacheText { get; set; } = "";

    bool _loadingToyCache;

    partial void OnLobbyToyCacheIndexChanged(int value)
    {
        if (_loadingToyCache || value < 0 || value >= ToyFiles.LimitsGb.Count)
        {
            return;
        }

        var gb = ToyFiles.LimitsGb[value];
        SaveSetting(s => s.LobbyToyCacheGb = gb);
        // Within the new size at once: the tables used longest ago go (a file the lobby has open is skipped).
        var dropped = ToyFiles.Prune(ToyFiles.Bytes(gb));
        RefreshToyCacheText();
        if (dropped > 0)
        {
            App.Library.PlayStatus =
                $"Dropped the saved 3D toys of {dropped} table(s) used longest ago, to fit {LobbyToyCacheChoices[value]}.";
        }
    }

    void LoadToyCacheSettings(Services.AppSettings s)
    {
        _loadingToyCache = true;
        var i = ToyFiles.LimitsGb.ToList().IndexOf(s.LobbyToyCacheGb);
        LobbyToyCacheIndex = i < 0 ? 0 : i;
        _loadingToyCache = false;
        RefreshToyCacheText();
    }

    void RefreshToyCacheText()
    {
        var bytes = ToyFiles.SizeBytes();
        LobbyToyCacheText =
            bytes == 0
                ? "No 3D toys saved yet."
                : $"Saved 3D toys take {bytes / (1024.0 * 1024 * 1024):0.0} GB.";
    }

    /// <summary>Deletes every table's saved toys; the lobby reads each table's again when it's next picked.</summary>
    [RelayCommand]
    void ClearLobbyToys()
    {
        var freed = ToyFiles.ClearAll();
        RefreshToyCacheText();
        App.Library.PlayStatus =
            freed > 0
                ? $"Cleared the saved 3D toys ({freed / (1024.0 * 1024 * 1024):0.0} GB). The VR lobby reads a table's again when you pick it, or PREPARE TABLES does them all."
                : "There were no saved 3D toys to clear.";
    }
}
