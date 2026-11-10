using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Lobby;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

/// <summary>One line of a card's ⋯ → Collections submenu: a collection (ticked when the table is in it) or "New collection…".</summary>
public sealed record CollectionChoice(InstalledItem Item, string? Name, bool IsIn)
{
    public bool IsNew => Name == null;
    public string Header => Name ?? "New collection…";
    public MenuItemToggleType ToggleType =>
        IsNew ? MenuItemToggleType.None : MenuItemToggleType.CheckBox;
}

/// <summary>
/// My Tables: your own collections of tables (<see cref="TableCollections"/>, shared with the VR lobby, where each
/// is a filter). A card's ⋯ → Collections puts the table in one or takes it out, or starts a new one; the
/// Collection filter shows one, with RENAME and DELETE beside it.
/// </summary>
public sealed partial class MainViewModel
{
    const string AnyCollection = "Any collection";

    /// <summary>Names a collection can't have: the lobby's own filters, and this list's "Any collection".</summary>
    static readonly string[] ReservedNames = [.. LobbyNav.Filters, AnyCollection];

    TableCollections _collections = new();

    public ObservableCollection<string> InstalledCollections { get; } = [AnyCollection];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCollectionChosen))]
    public partial string InstalledCollectionFilter { get; set; } = AnyCollection;

    public bool IsCollectionChosen => InstalledCollectionFilter is { } c && c != AnyCollection;

    partial void OnInstalledCollectionFilterChanged(string value) => InstalledFilterChanged(value);

    bool MatchesInstalledCollection(InstalledItem i) =>
        !IsCollectionChosen || _collections.Contains(InstalledCollectionFilter, i.Table.TablePath);

    /// <summary>Re-reads collections.json (the lobby never writes it, but another manager window could).</summary>
    void LoadCollections()
    {
        _collections = TableCollections.Load();
        ShowCollections();
    }

    /// <summary>Fills the Collection filter and every card's submenu from <see cref="_collections"/>.</summary>
    void ShowCollections()
    {
        Refill(
            InstalledCollections,
            [
                AnyCollection,
                .. _collections.Names.Where(n =>
                    !string.Equals(n, AnyCollection, StringComparison.OrdinalIgnoreCase)
                ),
            ],
            () => InstalledCollectionFilter,
            v => InstalledCollectionFilter = v
        );
        foreach (var item in _allInstalled)
        {
            item.CollectionChoices =
            [
                .. _collections.Collections.Select(c => new CollectionChoice(
                    item,
                    c.Name,
                    c.Contains(item.Table.TablePath)
                )),
                new CollectionChoice(item, null, false),
            ];
        }
    }

    /// <summary>Saves a change to the collections; false (with the reason in the status line) when it couldn't.</summary>
    bool ChangeCollections(Action<TableCollections> change)
    {
        try
        {
            _collections = TableCollections.Mutate(change);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            PlayStatus = "Couldn't save your collections: " + e.Message;
            ShowCollections(); // a ticked menu item goes back to how things really are
            return false;
        }
        ShowCollections();
        FilterInstalled();
        return true;
    }

    [RelayCommand]
    async Task ChooseCollection(CollectionChoice choice)
    {
        var path = choice.Item.Table.TablePath;
        if (!choice.IsNew)
        {
            ChangeCollections(c => c.Set(choice.Name!, path, !choice.IsIn));
            return;
        }

        if (
            await AskCollectionNameAsync(
                "New collection",
                "",
                $"Name the new collection. {choice.Item.Title} goes in it, and the VR lobby shows it as a filter (B / Tab)."
            )
            is not { } name
        )
        {
            return;
        }

        string? made = null;
        var saved = ChangeCollections(c =>
        {
            made = c.Create(name, ReservedNames);
            if (made != null)
            {
                c.Set(made, path, true);
            }
        });
        if (saved && made == null)
        {
            // Another manager window made one with that name in the meantime.
            PlayStatus = $"There's already a collection called “{name}”; nothing was added.";
        }
    }

    [RelayCommand]
    async Task RenameCollection()
    {
        if (!IsCollectionChosen)
        {
            return;
        }

        var old = InstalledCollectionFilter;
        if (
            await AskCollectionNameAsync("Rename collection", old, $"A new name for “{old}”.", old)
            is not { } name
        )
        {
            return;
        }

        var renamed = false;
        if (!ChangeCollections(c => renamed = c.Rename(old, name, ReservedNames)))
        {
            return;
        }

        if (renamed)
        {
            InstalledCollectionFilter = _collections.Find(name)?.Name ?? AnyCollection;
        }
        else
        {
            // Another manager window deleted it, or took the name, in the meantime.
            PlayStatus = $"Couldn't rename “{old}” to “{name}”: it's gone, or the name is taken.";
        }
    }

    [RelayCommand]
    async Task DeleteCollection()
    {
        if (!IsCollectionChosen)
        {
            return;
        }

        var name = InstalledCollectionFilter;
        var count = _collections.Find(name)?.Tables.Count ?? 0;
        if (
            !await _ui.ConfirmAsync(
                "Delete collection",
                $"Delete “{name}”? "
                    + (
                        count == 0 ? "It's empty."
                        : count == 1 ? "The table in it stays installed; only the collection goes."
                        : $"Its {count} tables stay installed; only the collection goes."
                    )
            )
        )
        {
            return;
        }

        ChangeCollections(c => c.Delete(name));
    }

    /// <summary>Asks for a collection name until it's usable (or cancelled): not blank, not taken, not one of the lobby's own filters.</summary>
    async Task<string?> AskCollectionNameAsync(
        string title,
        string initial,
        string message,
        string? renaming = null
    )
    {
        var prompt = message;
        while (true)
        {
            var typed = await _ui.AskTextAsync(
                title,
                prompt,
                initial,
                renaming == null ? "CREATE" : "RENAME"
            );
            if (typed == null)
            {
                return null;
            }

            var name = TableCollections.CleanName(typed);
            if (name == null)
            {
                prompt = message + "\n\nThe name can't be blank.";
            }
            else if (ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                prompt =
                    message
                    + $"\n\n“{name}” is already the name of one of the filters. Pick another name.";
            }
            else if (
                _collections.Find(name) is { } other
                && !string.Equals(other.Name, renaming, StringComparison.OrdinalIgnoreCase)
            )
            {
                prompt = message + $"\n\nThere's already a collection called “{other.Name}”.";
            }
            else
            {
                return name;
            }

            initial = typed;
        }
    }
}
