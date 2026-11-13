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
/// while a list is in A–Z order, where each letter is one run of cards. Each list's rail is on its view model
/// (<see cref="LibraryViewModel.InstalledLetters"/>, <see cref="CatalogViewModel.CatalogLetters"/>).
/// </summary>
static class LetterRails
{
    /// <summary>Fewer tables than this fit on a screen or two: no rail.</summary>
    public const int From = 20;

    /// <summary>A–Z order by the lobby's rule, so every letter is one run (and "The Getaway" sits under G).</summary>
    public static string SortKey(string title) => LobbyNav.SortKey(title);

    public static void Fill(ObservableCollection<LetterChoice> target, IEnumerable<string> titles)
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
    public static void SetActive(
        ObservableCollection<LetterChoice> rail,
        IReadOnlySet<string> letters
    )
    {
        foreach (var l in rail)
        {
            l.IsActive = letters.Contains(l.Letter);
        }
    }
}
