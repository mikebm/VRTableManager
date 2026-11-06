using CommunityToolkit.Mvvm.ComponentModel;

namespace VrTableManager.ViewModels;

// My Tables → SIZE: large cards (the original), medium, small, or text only (no pictures), remembered in settings.
public sealed partial class MainViewModel
{
    public string[] CardSizes { get; } = ["Large", "Medium", "Small", "Text"];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CardWidth), nameof(CardArtHeight), nameof(ShowCardArt), nameof(ShowCardDetails), nameof(BadgesOnArt), nameof(BadgesInText), nameof(PlayLabel))]
    public partial string CardSize { get; set; } = "Large";

    partial void OnCardSizeChanged(string value)
    {
        if (!CardSizes.Contains(value)) { CardSize = "Large"; return; }
        foreach (var i in _allInstalled) i.Compact = IsCompactCards;
        if (Settings.CardSize == value) return;
        Settings.CardSize = value;
        Settings.Save();
    }

    /// <summary>Below Large, a card's buttons are just Play and MORE; the rest move into MORE's menu (narrow cards got tall otherwise).</summary>
    public bool IsCompactCards => CardSize != "Large";

    /// <summary>Card width in px. Text-only cards keep the large width: without the picture, the name is what you read.</summary>
    public double CardWidth => CardSize switch { "Medium" => 220, "Small" => 184, _ => 264 };
    public double CardArtHeight => CardSize switch { "Medium" => 250, "Small" => 200, _ => 300 };
    public bool ShowCardArt => CardSize != "Text";
    /// <summary>Large cards keep their badges on the picture; on narrower ones (and text-only) they would overlap, so they get a row of their own.</summary>
    public bool BadgesOnArt => CardSize == "Large";
    public bool BadgesInText => CardSize != "Large";
    /// <summary>Small cards leave out the longer lines (disk use, add-on versions, why a ROM can't be downloaded); the badges still say what's installed.</summary>
    public bool ShowCardDetails => CardSize != "Small";

    /// <summary>Small cards are too narrow for the full label beside the ⋯ button.</summary>
    public string PlayLabel => CardSize == "Small" ? "▶  PLAY" : "▶  PLAY IN VR";
}
