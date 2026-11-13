using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

/// <summary>
/// My Tables: the maker / era / type / theme filters (the same ones as Get Tables) and the forgiving search.
/// Type and theme come from the table's catalog entry, so tables that aren't linked to one only pass "Any".
/// </summary>
public sealed partial class LibraryViewModel
{
    /// <summary>Makers and themes of the installed tables, most common first.</summary>
    public ObservableCollection<string> InstalledMakers { get; } = [TableFilters.AnyMaker];
    public ObservableCollection<string> InstalledThemes { get; } = [TableFilters.AnyTheme];

    [ObservableProperty]
    public partial string InstalledMakerFilter { get; set; } = TableFilters.AnyMaker;

    [ObservableProperty]
    public partial string InstalledEraFilter { get; set; } = TableFilters.AnyEra;

    [ObservableProperty]
    public partial string InstalledTypeFilter { get; set; } = TableFilters.AnyType;

    [ObservableProperty]
    public partial string InstalledThemeFilter { get; set; } = TableFilters.AnyTheme;

    [ObservableProperty]
    public partial bool HasInstalledFilters { get; set; }

    /// <summary>True while a choice list is being refilled: the ComboBox briefly clears its selection then.</summary>
    bool _refillingChoices;

    partial void OnInstalledMakerFilterChanged(string value) => InstalledFilterChanged(value);

    partial void OnInstalledEraFilterChanged(string value) => InstalledFilterChanged(value);

    partial void OnInstalledTypeFilterChanged(string value) => InstalledFilterChanged(value);

    partial void OnInstalledThemeFilterChanged(string value) => InstalledFilterChanged(value);

    void InstalledFilterChanged(string? value)
    {
        if (value != null && !_refillingChoices)
        {
            FilterInstalled();
        }
    }

    [RelayCommand]
    void ClearInstalledFilters()
    {
        _refillingChoices = true;
        InstalledMakerFilter = TableFilters.AnyMaker;
        InstalledEraFilter = TableFilters.AnyEra;
        InstalledTypeFilter = TableFilters.AnyType;
        InstalledThemeFilter = TableFilters.AnyTheme;
        InstalledCollectionChoice = AnyOption;
        _refillingChoices = false;
        FilterInstalled();
    }

    // ---- The catalog entry behind an installed table ----

    VpsGame? CatalogGameOf(InstalledItem i) => App.Catalog.CatalogGame(i.Table.Manifest.GameId);

    TableFacts FactsOf(InstalledItem i) => TableFilters.FactsOf(i.Table.Manifest, CatalogGameOf(i));

    bool MatchesInstalledFilters(InstalledItem i) =>
        TableFilters.Matches(
            FactsOf(i),
            InstalledMakerFilter,
            InstalledEraFilter,
            InstalledTypeFilter,
            InstalledThemeFilter
        );

    // ---- Search ----

    /// <summary>
    /// Search fields per table, cleaned up once. Rebuilt when what's searched changes: the table's own details
    /// (getting a ROM can narrow its ROM list), its catalog link, or the catalog itself (themes and designers).
    /// </summary>
    readonly Dictionary<
        InstalledItem,
        (string Key, List<VpsGame> Games, SearchEntry Entry)
    > _installedIndex = [];

    static string SearchKeyOf(InstalledItem i)
    {
        var m = i.Table.Manifest;
        return string.Join(
            '\n',
            [
                m.GameId,
                i.Title,
                m.Manufacturer ?? "",
                m.Year?.ToString() ?? "",
                .. m.Authors,
                "",
                .. m.RomNames,
            ]
        );
    }

    SearchEntry SearchEntryOf(InstalledItem i)
    {
        var key = SearchKeyOf(i);
        if (
            _installedIndex.TryGetValue(i, out var c)
            && c.Key == key
            && ReferenceEquals(c.Games, App.Catalog._allGames)
        )
        {
            return c.Entry;
        }

        var entry = TableSearch.Index(i.Table.Manifest, CatalogGameOf(i));
        _installedIndex[i] = (key, App.Catalog._allGames, entry);
        return entry;
    }

    // ---- Choice lists ----

    (List<InstalledItem>, List<VpsGame>, int)? _choicesFor;

    /// <summary>
    /// Refills the maker and theme lists from the installed tables, when those, their makers or catalog links,
    /// or the catalog changed.
    /// </summary>
    void UpdateInstalledChoices()
    {
        var details = new HashCode();
        foreach (var i in _allInstalled)
        {
            details.Add(i.Table.Manifest.GameId);
            details.Add(i.Table.Manifest.Manufacturer);
        }
        var key = (_allInstalled, App.Catalog._allGames, details.ToHashCode());
        if (_choicesFor == key)
        {
            return;
        }

        _choicesFor = key;
        foreach (var gone in _installedIndex.Keys.Except(_allInstalled).ToList())
        {
            _installedIndex.Remove(gone);
        }

        var facts = _allInstalled.Select(FactsOf).ToList();
        Refill(
            InstalledMakers,
            TableFilters.Choices(TableFilters.AnyMaker, facts.Select(f => f.Manufacturer)),
            () => InstalledMakerFilter,
            v => InstalledMakerFilter = v
        );
        Refill(
            InstalledThemes,
            TableFilters.Choices(TableFilters.AnyTheme, facts.SelectMany(f => f.Themes ?? [])),
            () => InstalledThemeFilter,
            v => InstalledThemeFilter = v
        );
    }

    /// <summary>Replaces a choice list, keeping its selection when it's still there (else back to "Any").</summary>
    void Refill(
        ObservableCollection<string> list,
        List<string> items,
        Func<string?> get,
        Action<string> set
    )
    {
        if (list.SequenceEqual(items))
        {
            return;
        }

        var selected = get();
        _refillingChoices = true;
        try
        {
            list.Clear();
            foreach (var item in items)
            {
                list.Add(item);
            }
            // Set it even when it's unchanged: whether or not the ComboBox wrote null back while the list was
            // cleared, going through null makes the property change, so the box shows the choice again.
            set(null!);
            set(
                items.FirstOrDefault(x =>
                    string.Equals(x, selected, StringComparison.OrdinalIgnoreCase)
                ) ?? items[0]
            );
        }
        finally
        {
            _refillingChoices = false;
        }
    }
}
