using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VrTableManager.Lobby;

namespace VrTableManager.ViewModels;

// My Tables' A–Z rail (see LetterRails).
public sealed partial class LibraryViewModel
{
    public ObservableCollection<LetterChoice> InstalledLetters { get; } = [];

    [ObservableProperty]
    public partial bool ShowInstalledLetters { get; set; }

    void UpdateInstalledLetters(bool alphabetical)
    {
        ShowInstalledLetters = alphabetical && Installed.Count >= LetterRails.From;
        LetterRails.Fill(InstalledLetters, Installed.Select(i => i.Title));
    }

    /// <summary>Lights the rail's letters for the cards on screen.</summary>
    public void SetActiveLetters(IReadOnlySet<string> letters) =>
        LetterRails.SetActive(InstalledLetters, letters);

    /// <summary>
    /// The card a letter jumps to: the letter's first table.
    /// </summary>
    public InstalledItem? FirstInstalledAt(string letter) =>
        Installed.FirstOrDefault(i => LobbyNav.LetterOf(i.Title) == letter);
}
