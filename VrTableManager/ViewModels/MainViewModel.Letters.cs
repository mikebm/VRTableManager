using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VrTableManager.Lobby;

namespace VrTableManager.ViewModels;

/// <summary>
/// One letter of an A–Z rail: dimmed when no table shown files under it, lit while cards under it are on screen.
/// </summary>
public sealed partial class LetterChoice(string letter) : ObservableObject
{
    public string Letter { get; } = letter;

    [ObservableProperty]
    public partial bool HasTables { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }
}

/// <summary>
/// The A–Z rails beside My Tables and Get Tables, like the VR lobby's letter rail: a letter jumps to the first table
/// filed under it (<see cref="LobbyNav.LetterOf"/>: "The Addams Family" under A, digits under #). They show only
/// while a list is in A–Z order, where each letter is one run of cards.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Fewer tables than this fit on a screen or two: no rail.</summary>
    const int LettersFrom = 20;

    public ObservableCollection<LetterChoice> InstalledLetters { get; } = [];
    public ObservableCollection<LetterChoice> CatalogLetters { get; } = [];

    [ObservableProperty]
    public partial bool ShowInstalledLetters { get; set; }

    [ObservableProperty]
    public partial bool ShowCatalogLetters { get; set; }

    /// <summary>A–Z order by the lobby's rule, so every letter is one run (and "The Getaway" sits under G).</summary>
    static string SortKey(string title) => LobbyNav.SortKey(title);

    void UpdateInstalledLetters(bool alphabetical)
    {
        ShowInstalledLetters = alphabetical && Installed.Count >= LettersFrom;
        FillLetters(InstalledLetters, Installed.Select(i => i.Title));
    }

    void UpdateCatalogLetters(bool alphabetical)
    {
        ShowCatalogLetters = alphabetical && _filtered.Count >= LettersFrom;
        FillLetters(CatalogLetters, _filtered.Select(g => g.Name));
    }

    static void FillLetters(ObservableCollection<LetterChoice> target, IEnumerable<string> titles)
    {
        if (target.Count == 0)
        {
            foreach (var c in LobbyNav.Letters)
            {
                target.Add(new LetterChoice(c.ToString()));
            }
        }

        var have = titles.Select(LobbyNav.LetterOf).ToHashSet();
        foreach (var l in target)
        {
            l.HasTables = have.Contains(l.Letter);
        }
    }

    /// <summary>Lights the rail's letters for the cards on screen (none, as when the list is empty).</summary>
    public void SetActiveLetters(bool installed, IReadOnlySet<string> letters)
    {
        foreach (var l in installed ? InstalledLetters : CatalogLetters)
        {
            l.IsActive = letters.Contains(l.Letter);
        }
    }

    /// <summary>
    /// The card a letter jumps to in My Tables. Favourites come first, so it's the letter's first table after them;
    /// a letter only favourites have jumps to the first of those.
    /// </summary>
    public InstalledItem? FirstInstalledAt(string letter) =>
        Installed.FirstOrDefault(i => !i.IsFavourite && LobbyNav.LetterOf(i.Title) == letter)
        ?? Installed.FirstOrDefault(i => LobbyNav.LetterOf(i.Title) == letter);

    /// <summary>
    /// The card a letter jumps to in Get Tables. A page or two past what's shown, those pages are added; further (or
    /// before the first card shown), the cards start at the letter instead, since laying out every card up to "W" of
    /// a 2,000-table catalog freezes the window. SHOW EARLIER TABLES brings back the ones before.
    /// </summary>
    public GameItem? FirstCatalogAt(string letter)
    {
        var index = _filtered.FindIndex(g => LobbyNav.LetterOf(g.Name) == letter);
        if (index < 0)
        {
            return null;
        }

        if (index < _pageStart || index >= _pageStart + Games.Count + 2 * PageSize)
        {
            _pageStart = index;
            Games.Clear();
            AddPage();
        }
        else
        {
            while (_pageStart + Games.Count <= index && HasMore)
            {
                AddPage();
            }
        }

        var shown = index - _pageStart;
        return shown >= 0 && shown < Games.Count ? Games[shown] : null;
    }
}
