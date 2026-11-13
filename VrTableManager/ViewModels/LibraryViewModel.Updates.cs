using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// My Tables: newer catalog versions of installed tables, and their rule sheets.
public sealed partial class LibraryViewModel
{
    [ObservableProperty]
    public partial int TableUpdateCount { get; set; }

    [ObservableProperty]
    public partial string TableUpdateText { get; set; } = "Checking after the catalog loads…";

    /// <summary>Table updates come from the already-loaded catalog, so this is free to call often.</summary>
    internal void ApplyTableUpdates()
    {
        if (App.Catalog._allGames.Count == 0)
        {
            return;
        }

        var updates = TableUpdates
            .Find(_allInstalled.Select(i => i.Table), App.Catalog._allGames)
            .ToDictionary(u => u.Table.TablePath, StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allInstalled)
        {
            item.Update = updates.GetValueOrDefault(item.Table.TablePath);
            item.RomSource = App.Roms.FindRomSource(item.Table);
            item.HasSternSource = App.Roms.IsSternTable(item.Table);
            item.AttractAudioSources = App.Media.AttractAudioSourcesFor(item.Table);
            item.WheelArtSources = App.Media.WheelArtSourcesFor(item.Table);
            _ = App.Roms.RecheckRomNeedAsync(item.Table);
        }
        TableUpdateCount = updates.Count;
        var tracked = _allInstalled.Count(i => i.Table.Manifest.TableFileId != "");
        TableUpdateText = updates.Count switch
        {
            0 when tracked == 0 => "No tables installed from the catalog yet.",
            0 => tracked == 1
                ? "Your table is up to date."
                : $"All {tracked} tables are up to date.",
            // Name them when they fit on the line; SHOW THEM lists them all either way.
            1 => $"{updates.Values.First().Table.Manifest.GameName} has a newer version.",
            <= 3 =>
                $"{string.Join(", ", updates.Values.Select(u => u.Table.Manifest.GameName).Order())} have newer versions.",
            _ => $"{updates.Count} tables have newer versions.",
        };
        if (InstalledFilter == UpdatesFilter)
        {
            FilterInstalled(); // one was updated, or a new version came out
        }
        App.Addons.ApplyAddons();
        ApplyRules();
        App.Updates.UpdateCount =
            TableUpdateCount
            + App.Addons.AddonUpdateCount
            + (App.Updates.HasVpxUpdate ? 1 : 0)
            + (App.Updates.HasUevrUpdate ? 1 : 0)
            + (App.Updates.HasAppUpdate ? 1 : 0);
    }

    // ---- Rules: The Pinball Primer's page for the machine and the catalog's instruction cards (My Tables → RULES) ----

    List<RuleSheets.PrimerEntry> _primer = [];

    internal async Task LoadRuleSheetsAsync()
    {
        _primer = await Task.Run(() => RuleSheets.LoadAsync());
        ApplyRules();
    }

    void ApplyRules()
    {
        // Store games (Pinball FX, Pinball M) are whole collections, not one machine.
        foreach (
            var item in _allInstalled.Where(i =>
                i.Table.Manifest.Engine is TableManifest.Vpx or TableManifest.FuturePinball
            )
        )
        {
            var m = item.Table.Manifest;
            var game = App.Catalog.CatalogGame(m.GameId);
            item.Rules = RuleSheets
                .LinksFor(game?.Name ?? m.GameName, game?.Year ?? m.Year, game, _primer)
                .Select(l => new RuleChoice(item, l.Label, l.Url))
                .ToList();
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task UpdateTableAsync(InstalledItem item)
    {
        if (item.Update is not { } u)
        {
            return;
        }

        await App.Catalog.InstallAsync(u.Game, u.File, u.Table);
    }

    [RelayCommand]
    internal async Task UpdateAllTablesAsync()
    {
        // One at a time: the browser downloader handles one site page at a time anyway.
        foreach (var u in _allInstalled.Select(i => i.Update).OfType<TableUpdate>().ToList())
        {
            await App.Catalog.InstallAsync(u.Game, u.File, u.Table);
        }
    }

}
