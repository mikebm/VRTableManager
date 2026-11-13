using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Lobby;
using VrTableManager.Models;
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

/// <summary>What a line of the Collection filter is: "Any collection", a section header, or one to pick.</summary>
public enum CollectionKind
{
    Any,
    Header,

    /// <summary>One of your own collections.</summary>
    Mine,

    /// <summary>A ready-made smart collection (<see cref="SmartCollections.BuiltIn"/>).</summary>
    Ready,

    /// <summary>A smart collection you saved.</summary>
    Saved,
}

/// <summary>A line of the Collection filter. Headers ("YOUR COLLECTIONS", "SMART") can't be picked.</summary>
public sealed record CollectionOption(
    CollectionKind Kind,
    string Name,
    string? Tip = null,
    int? Count = null
)
{
    /// <summary>How many of your tables are in it, from the whole library (not narrowed by the other filters).</summary>
    public string CountText => Count?.ToString() ?? "";

    public bool IsHeader => Kind == CollectionKind.Header;
    public bool IsSelectable => !IsHeader;
    public bool IsSmart => Kind is CollectionKind.Ready or CollectionKind.Saved;
}

/// <summary>
/// My Tables: your own collections of tables and smart collections (<see cref="TableCollections"/>, shared with the
/// VR lobby, where each is a filter). A card's ⋯ → Collections puts the table in one of yours or starts a new one.
/// The Collection filter lists yours, then the smart ones in a section of their own: ready-made ones (Never played,
/// New this month …) and ones you saved from the search and filters with SAVE AS SMART.
/// </summary>
public sealed partial class LibraryViewModel
{
    const string AnyCollection = "Any collection";

    /// <summary>Names a collection can't have: the lobby's own filters, and this list's "Any collection".</summary>
    static readonly string[] ReservedNames = [.. LobbyNav.Filters, AnyCollection];

    static readonly CollectionOption AnyOption = new(CollectionKind.Any, AnyCollection);

    TableCollections _collections = new();

    public ObservableCollection<CollectionOption> InstalledCollections { get; } = [AnyOption];

    [
        ObservableProperty,
        NotifyPropertyChangedFor(
            nameof(IsCollectionChosen),
            nameof(IsSmartChosen),
            nameof(CanEditCollection),
            nameof(CanSaveSmart)
        )
    ]
    public partial CollectionOption? InstalledCollectionChoice { get; set; } = AnyOption;

    /// <summary>Anything but "Any collection": one of yours or a smart one.</summary>
    public bool IsCollectionChosen => InstalledCollectionChoice is { Kind: not CollectionKind.Any };

    public bool IsSmartChosen => InstalledCollectionChoice is { IsSmart: true };

    /// <summary>Yours and the smart ones you saved can be renamed and deleted; ready-made ones can't.</summary>
    public bool CanEditCollection =>
        InstalledCollectionChoice is { Kind: CollectionKind.Mine or CollectionKind.Saved };

    partial void OnInstalledCollectionChoiceChanged(
        CollectionOption? oldValue,
        CollectionOption? newValue
    )
    {
        // A header isn't a choice. Arrowing onto one (with the box closed) steps on past it the way you were
        // going, to the first line under it going down or the last one above it going up.
        if (newValue is { IsHeader: true })
        {
            InstalledCollectionChoice = StepPastHeader(oldValue, newValue);
            return;
        }
        InstalledFilterChanged(newValue?.Name);
    }

    /// <summary>The line to land on instead of <paramref name="header"/>, coming from <paramref name="from"/>.</summary>
    CollectionOption StepPastHeader(CollectionOption? from, CollectionOption header)
    {
        var at = InstalledCollections.IndexOf(header);
        var down = from == null || InstalledCollections.IndexOf(from) < at;
        var step = down ? 1 : -1;
        for (var i = at + step; i >= 0 && i < InstalledCollections.Count; i += step)
        {
            if (InstalledCollections[i].IsSelectable)
            {
                return InstalledCollections[i];
            }
        }
        return from is { IsSelectable: true } ? from : AnyOption;
    }

    bool MatchesInstalledCollection(InstalledItem i) =>
        InstalledCollectionChoice switch
        {
            { Kind: CollectionKind.Mine } c => _collections.Contains(c.Name, i.Table.TablePath),
            { Kind: CollectionKind.Ready } c => SmartCollections.FindBuiltIn(c.Name) is { } b
                && b.Test(SmartTableOf(i), DateTime.Now),
            { Kind: CollectionKind.Saved } c => _collections.FindSmart(c.Name) is { } r
                && SmartCollections.Matches(r, SmartTableOf(i), _collections),
            _ => true,
        };

    SmartTable SmartTableOf(InstalledItem i) =>
        SmartTable.From(i.Table, CatalogGameOf(i), _stats, SearchEntryOf(i));

    /// <summary>Re-reads collections.json (the lobby never writes it, but another manager window could).</summary>
    void LoadCollections()
    {
        _collections = TableCollections.Load();
        ShowCollections();
    }

    /// <summary>Fills the Collection filter and every card's submenu from <see cref="_collections"/>.</summary>
    void ShowCollections()
    {
        CountCollections();

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

    /// <summary>What the collection counts were worked out from; they're redone only when one of these changes.</summary>
    (List<InstalledItem>, PlayStats, TableCollections, List<VpsGame>, DateTime, int)? _countedFor;

    /// <summary>
    /// Fills the Collection filter's lines, each with how many of your tables are in it. Runs with every refilter
    /// (so on every search keystroke), but only counts again when the tables, plays and favourites, collections,
    /// catalog or day changed: the stats and collections are replaced, not edited, whenever they change. The counts
    /// are of the whole library, so the search and other filters don't change them.
    /// </summary>
    void CountCollections()
    {
        var details = new HashCode();
        foreach (var i in _allInstalled)
        {
            details.Add(i.Title);
            details.Add(i.Table.Manifest.GameId);
            details.Add(i.Table.Manifest.Manufacturer);
            details.Add(i.Table.MissingRoms.Count);
        }
        var key = (
            _allInstalled,
            _stats,
            _collections,
            App.Catalog._allGames,
            DateTime.Today,
            details.ToHashCode()
        );
        if (_countedFor == key)
        {
            return;
        }
        _countedFor = key;

        var now = DateTime.Now;
        var tables = _allInstalled.Select(SmartTableOf).ToList();
        int CountWhere(Func<SmartTable, bool> test) => tables.Count(test);

        var options = new List<CollectionOption> { AnyOption };
        var mine = _collections
            .Collections.Where(c =>
                !ReservedNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase)
            )
            .ToList();
        if (mine.Count > 0)
        {
            options.Add(new(CollectionKind.Header, "YOUR COLLECTIONS"));
            options.AddRange(
                mine.Select(c => new CollectionOption(
                    CollectionKind.Mine,
                    c.Name,
                    Count: CountWhere(t => c.Contains(t.Path))
                ))
            );
        }
        options.Add(new(CollectionKind.Header, "SMART"));
        options.AddRange(
            SmartCollections.BuiltIn.Select(b => new CollectionOption(
                CollectionKind.Ready,
                b.Name,
                b.Description,
                CountWhere(t => b.Test(t, now))
            ))
        );
        options.AddRange(
            _collections.Smart.Select(r => new CollectionOption(
                CollectionKind.Saved,
                r.Name,
                "Saved: " + r.Describe(),
                CountWhere(t => SmartCollections.Matches(r, t, _collections))
            ))
        );
        RefillCollectionOptions(options);
    }

    /// <summary>Replaces the Collection filter's lines, keeping the choice when it's still there (else "Any").</summary>
    void RefillCollectionOptions(List<CollectionOption> options)
    {
        if (InstalledCollections.SequenceEqual(options))
        {
            return;
        }

        var selected = InstalledCollectionChoice;
        _refillingChoices = true;
        try
        {
            InstalledCollections.Clear();
            foreach (var o in options)
            {
                InstalledCollections.Add(o);
            }
            // Through null, so the box shows the choice again whether or not it cleared itself (see Refill).
            InstalledCollectionChoice = null;
            InstalledCollectionChoice = Option(selected?.Kind, selected?.Name);
        }
        finally
        {
            _refillingChoices = false;
        }
    }

    /// <summary>The line for a collection, or "Any collection" when there's none like it.</summary>
    CollectionOption Option(CollectionKind? kind, string? name) =>
        InstalledCollections.FirstOrDefault(o =>
            o.Kind == kind && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)
        ) ?? AnyOption;

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
            PlayStatus = $"The name “{name}” is already taken; nothing was added.";
        }
    }

    // ---- Smart collections you save ----

    /// <summary>The search and filters showing now, as a smart collection (not yet named).</summary>
    SmartRule CurrentRule() =>
        new()
        {
            Search = InstalledSearch.Trim(),
            Engine = InstalledEngine == InstalledEngines[0] ? "" : InstalledEngine,
            Maker = InstalledMakerFilter == TableFilters.AnyMaker ? "" : InstalledMakerFilter ?? "",
            Era = InstalledEraFilter == TableFilters.AnyEra ? "" : InstalledEraFilter ?? "",
            Type = InstalledTypeFilter == TableFilters.AnyType ? "" : InstalledTypeFilter ?? "",
            Theme = InstalledThemeFilter == TableFilters.AnyTheme ? "" : InstalledThemeFilter ?? "",
            Collection = InstalledCollectionChoice is { Kind: CollectionKind.Mine } c ? c.Name : "",
            // Most played and Recently played are orders with a top 30, not a kind of table: not kept.
            Favourites = InstalledFilter == "Favourites",
        };

    /// <summary>SAVE AS SMART shows when the search or a filter narrows the list (and no smart one is chosen).</summary>
    public bool CanSaveSmart => !IsSmartChosen && !CurrentRule().IsEmpty;

    [RelayCommand]
    async Task SaveSmartCollection()
    {
        var rule = CurrentRule();
        if (rule.IsEmpty || IsSmartChosen)
        {
            return;
        }

        if (
            await AskCollectionNameAsync(
                "Save as smart collection",
                "",
                $"A smart collection keeps these filters and stays up to date as your tables change: {rule.Describe()}.\n\nThe VR lobby shows it as a filter (B / Tab) too."
            )
            is not { } name
        )
        {
            return;
        }

        rule.Name = name;
        string? made = null;
        if (!ChangeCollections(c => made = c.SaveSmart(rule, ReservedNames)))
        {
            return;
        }

        if (made == null)
        {
            PlayStatus = $"The name “{name}” is already taken; nothing was saved.";
            return;
        }

        // Show it the clean way: the smart collection, with the filters it now holds put back to Any.
        // One refilter at the end, not one per filter put back.
        _refillingChoices = true;
        try
        {
            InstalledSearch = "";
            InstalledEngine = InstalledEngines[0];
            InstalledFilter = InstalledFilters[0];
            InstalledMakerFilter = TableFilters.AnyMaker;
            InstalledEraFilter = TableFilters.AnyEra;
            InstalledTypeFilter = TableFilters.AnyType;
            InstalledThemeFilter = TableFilters.AnyTheme;
            InstalledCollectionChoice = Option(CollectionKind.Saved, made);
        }
        finally
        {
            _refillingChoices = false;
        }
        FilterInstalled();
    }

    // ---- Rename / delete (yours and the smart ones you saved) ----

    [RelayCommand]
    async Task RenameCollection()
    {
        if (
            InstalledCollectionChoice
            is not { Kind: CollectionKind.Mine or CollectionKind.Saved } chosen
        )
        {
            return;
        }

        var old = chosen.Name;
        if (
            await AskCollectionNameAsync("Rename", old, $"A new name for “{old}”.", old)
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
            InstalledCollectionChoice = Option(chosen.Kind, name);
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
        if (
            InstalledCollectionChoice
            is not { Kind: CollectionKind.Mine or CollectionKind.Saved } chosen
        )
        {
            return;
        }

        var name = chosen.Name;
        string message;
        if (chosen.Kind == CollectionKind.Saved)
        {
            message = $"Delete the smart collection “{name}”? Your tables aren't touched.";
        }
        else
        {
            var count = _collections.Find(name)?.Tables.Count ?? 0;
            message =
                $"Delete “{name}”? "
                + (
                    count == 0 ? "It's empty."
                    : count == 1 ? "The table in it stays installed; only the collection goes."
                    : $"Its {count} tables stay installed; only the collection goes."
                );
            var users = _collections
                .Smart.Where(r =>
                    string.Equals(r.Collection, name, StringComparison.OrdinalIgnoreCase)
                )
                .Select(r => $"“{r.Name}”")
                .ToList();
            if (users.Count > 0)
            {
                message +=
                    $" The smart collection{(users.Count == 1 ? "" : "s")} {string.Join(", ", users)} use{(users.Count == 1 ? "s" : "")} it and will be empty.";
            }
        }

        if (!await Ui.ConfirmAsync("Delete", message))
        {
            return;
        }

        ChangeCollections(c => c.Delete(name));
    }

    /// <summary>
    /// Asks for a name until it's usable (or cancelled): not blank, not one of the filters' own names, and not used by
    /// another collection, smart or not.
    /// </summary>
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
            var typed = await Ui.AskTextAsync(
                title,
                prompt,
                initial,
                renaming == null ? "SAVE" : "RENAME"
            );
            if (typed == null)
            {
                return null;
            }

            var name = TableCollections.CleanName(typed);
            object? self =
                renaming == null
                    ? null
                    : (object?)_collections.Find(renaming) ?? _collections.FindSmart(renaming);
            if (
                name != null
                && renaming != null
                && string.Equals(name, renaming, StringComparison.OrdinalIgnoreCase)
            )
            {
                return name; // only the capitals change: always fine
            }

            if (name == null)
            {
                prompt = message + "\n\nThe name can't be blank.";
            }
            else if (
                ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                || SmartCollections.FindBuiltIn(name) != null
            )
            {
                prompt =
                    message
                    + $"\n\n“{name}” is already the name of one of the filters. Pick another name.";
            }
            else if (_collections.IsTaken(name, null, self))
            {
                prompt = message + $"\n\nThere's already a collection called “{name}”.";
            }
            else
            {
                return name;
            }

            initial = typed;
        }
    }
}
