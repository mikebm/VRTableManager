using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// PuP packs (PinUP Player videos for the backglass/DMD) from the catalog, into each table's pupvideos\
// (My Tables → PUP PACK). Tracked in the table's vrtm.json so a new catalog version shows as an update.
public sealed partial class MainViewModel
{
    [ObservableProperty] public partial int PupUpdateCount { get; set; }
    [ObservableProperty] public partial string PupUpdateText { get; set; } = "Not checked yet.";
    /// <summary>VR play shows PuP packs through VPX's PinUP Player plugin.</summary>
    [ObservableProperty] public partial bool UsePupPlugin { get; set; }

    IReadOnlyList<PupChoice> PupChoicesFor(InstalledItem item) =>
        PupPacks.CanHold(item.Table, Settings) && item.Table.Manifest.GameId != "" && _allGames.FirstOrDefault(g => g.Id == item.Table.Manifest.GameId) is { } game
            ? PupPacks.SourcesFor(game).Select(f => new PupChoice(item, game, f, PupPacks.Describe(f))).ToList()
            : [];

    /// <summary>Catalog packs, PuP state and pack updates for every card (from the loaded catalog; cheap).</summary>
    void ApplyPupPacks()
    {
        if (_allGames.Count == 0) return;
        var updates = PupPacks.FindUpdates(_allInstalled.Select(i => i.Table), _allGames)
            .ToDictionary(u => u.Table.TablePath, StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allInstalled)
        {
            item.PupChoices = PupChoicesFor(item);
            item.PupUpdate = updates.GetValueOrDefault(item.Table.TablePath);
        }
        PupUpdateCount = updates.Count;
        var tracked = _allInstalled.Count(i => i.Table.Manifest.PupPack != null);
        PupUpdateText = updates.Count switch
        {
            0 when tracked == 0 => "No PuP packs installed from the catalog yet.",
            0 => tracked == 1 ? "Your PuP pack is up to date." : $"All {tracked} PuP packs are up to date.",
            1 => "1 PuP pack has a newer version.",
            _ => $"{updates.Count} PuP packs have newer versions.",
        };
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    Task GetPupPack(PupChoice choice) => GetPupPackAsync(choice.Item.Table, choice.Game, choice.File);

    [RelayCommand(AllowConcurrentExecutions = true)]
    Task UpdatePupPack(InstalledItem item) => item.PupUpdate is { } u ? GetPupPackAsync(u.Table, u.Game, u.File) : Task.CompletedTask;

    [RelayCommand]
    async Task UpdateAllPupPacksAsync()
    {
        // One at a time: they're big and come from the same site.
        foreach (var u in _allInstalled.Select(i => i.PupUpdate).OfType<PupUpdate>().ToList())
            await GetPupPackAsync(u.Table, u.Game, u.File);
    }

    async Task GetPupPackAsync(InstalledTable table, VpsGame game, VpsFile file)
    {
        if (!TryBeginBusy(table.TablePath)) return;
        try { await GetPupPackCoreAsync(table, game, file); }
        finally { EndBusy(table.TablePath); }
    }

    async Task GetPupPackCoreAsync(InstalledTable table, VpsGame game, VpsFile file)
    {
        var updating = table.Manifest.PupPack != null;
        var job = new JobItem($"{(updating ? "Update: " : "")}PuP pack for {table.Manifest.GameName}");
        Jobs.Insert(0, job);
        _pending.Add(new PendingDownload
        {
            Id = job.Id, Kind = PendingDownload.PupKind, Title = job.Title,
            GameId = game.Id, PupFileId = file.Id, TablePath = table.TablePath,
        });
        try
        {
            var url = PupPacks.UsableUrl(file) ?? throw new InvalidOperationException("The catalog has no working link for this PuP pack.");
            var downloaded = await FetchAsync(url, job, ["pup"]);
            job.Report("Reading the table script…");
            var names = await Task.Run(() => PupPacks.ExpectedNames(table));
            var record = await Task.Run(() => PupPacks.Install(downloaded, table, file, names, job.Report));
            CleanJobDownloads(job); // the archive can be gigabytes
            table.Manifest.PupPack = record;
            table.Manifest.PupName ??= names.Script;
            TableInstaller.SaveManifest(table);
            job.Finish($"✓ {(updating ? "Updated" : "Installed")} the PuP pack{(record.Version is { } v ? " v" + v : "")} in {PupPacks.FolderName}\\{record.Folder}."
                + (names.ScriptTurnsPupOff ? " The table script has usePUP = False: set it to True (VPX editor → Script) to see the pack."
                    : Settings.UsePupPlugin ? " It shows on the cabinet's backglass and DMD in VR."
                    : " Turn on the PinUP Player plugin in Setup to see it in VR."));
            RefreshInstalled();
        }
        catch (OperationCanceledException)
        {
            job.Finish("Cancelled", error: true);
        }
        catch (Exception e)
        {
            job.Finish("Failed: " + e.Message, error: true);
        }
        finally
        {
            CleanJobDownloads(job);
            if (!_closing) _pending.Remove(job.Id);
        }
    }

    [RelayCommand]
    async Task RemovePupPackAsync(InstalledItem item)
    {
        var state = item.PupState;
        var folder = state.Tracked?.Folder ?? state.Folders.FirstOrDefault();
        if (folder == null) return;
        if (!await _ui.ConfirmAsync("Remove PuP pack?", $"Delete {PupPacks.FolderName}\\{folder} from {item.Title}'s folder? The table itself stays.")) return;
        try
        {
            PupPacks.Remove(item.Table, folder);
            if (item.Table.Manifest.PupPack?.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase) == true)
            {
                item.Table.Manifest.PupPack = null;
                TableInstaller.SaveManifest(item.Table);
            }
            PlayStatus = $"Removed {item.Title}'s PuP pack.";
        }
        catch (Exception e) { PlayStatus = "Couldn't remove the PuP pack: " + e.Message; }
        RefreshInstalled();
    }

    [RelayCommand]
    void OpenPupFolder(InstalledItem item)
    {
        var dir = Path.Combine(item.Table.Folder, PupPacks.FolderName);
        if (Directory.Exists(dir)) _ui.OpenExternal(dir);
    }

    void ResumePup(PendingDownload d, Func<string?, InstalledTable?> find, List<string> skipped)
    {
        var game = _allGames.FirstOrDefault(g => g.Id == d.GameId);
        var file = game?.PupPackFiles?.FirstOrDefault(f => f.Id == d.PupFileId);
        if (find(d.TablePath) is { } t && game != null && file != null) _ = GetPupPackAsync(t, game, file);
        else skipped.Add(d.Title);
    }
}

/// <summary>One of the catalog's PuP packs for a table, as an item in the card's PUP PACK menu.</summary>
public sealed record PupChoice(InstalledItem Item, VpsGame Game, VpsFile File, string Label);
