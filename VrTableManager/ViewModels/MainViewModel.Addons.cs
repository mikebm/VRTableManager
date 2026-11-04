using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Add-ons from the catalog, each into the table's own folder where VPX 10.8.1's plugins look (My Tables → PUP,
// COLOUR DMD, ALTSOUND): PuP packs (pupvideos\), DMD colorizations (serum\ or vni\) and AltSound (altsound\).
// Tracked in the table's vrtm.json so a new catalog version shows as an update.
public sealed partial class MainViewModel
{
    [ObservableProperty] public partial int AddonUpdateCount { get; set; }
    [ObservableProperty] public partial string AddonUpdateText { get; set; } = "Not checked yet.";
    /// <summary>VR play shows PuP packs through VPX's PinUP Player plugin.</summary>
    [ObservableProperty] public partial bool UsePupPlugin { get; set; }
    /// <summary>VR play turns on VPX's FlexDMD and B2S plugins.</summary>
    [ObservableProperty] public partial bool UseDisplayPlugins { get; set; }

    IReadOnlyList<AddonChoice> ChoicesFor(AddonSlot slot)
    {
        var t = slot.Item.Table;
        if (!Addons.CanHold(t, Settings) || t.Manifest.GameId == "" || _allGames.FirstOrDefault(g => g.Id == t.Manifest.GameId) is not { } game) return [];
        return slot.Kind == AddonKind.Pup
            ? PupPacks.SourcesFor(game, t).Select(f => new AddonChoice(slot, game, f, PupPacks.Describe(f))).ToList()
            : AltPacks.SourcesFor(game, t, slot.Kind).Select(f => new AddonChoice(slot, game, f, AltPacks.Describe(f, t))).ToList();
    }

    void RefreshAddonState(InstalledItem item)
    {
        foreach (var slot in item.Addons) slot.State = Addons.StateOf(item.Table, slot.Kind);
    }

    /// <summary>Catalog add-ons and their updates for every card (from the loaded catalog; cheap).</summary>
    void ApplyAddons()
    {
        if (_allGames.Count == 0) return;
        var updates = Addons.FindUpdates(_allInstalled.Select(i => i.Table), _allGames);
        foreach (var item in _allInstalled)
            foreach (var slot in item.Addons)
            {
                slot.Choices = ChoicesFor(slot);
                slot.Update = updates.FirstOrDefault(u => u.Kind == slot.Kind && string.Equals(u.Table.TablePath, item.Table.TablePath, StringComparison.OrdinalIgnoreCase));
            }
        AddonUpdateCount = updates.Count;
        var tracked = _allInstalled.Sum(i => Enum.GetValues<AddonKind>().Count(k => Addons.Record(i.Table, k) != null));
        AddonUpdateText = updates.Count switch
        {
            0 when tracked == 0 => "No PuP packs, DMD colours or AltSound installed from the catalog yet.",
            0 => tracked == 1 ? "Your add-on is up to date." : $"All {tracked} add-ons are up to date.",
            1 => "1 add-on has a newer version.",
            _ => $"{updates.Count} add-ons have newer versions.",
        };
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    Task GetAddon(AddonChoice choice) => GetAddonAsync(choice.Slot.Kind, choice.Slot.Item.Table, choice.Game, choice.File);

    [RelayCommand(AllowConcurrentExecutions = true)]
    Task UpdateAddon(AddonSlot slot) => slot.Update is { } u ? GetAddonAsync(u.Kind, u.Table, u.Game, u.File) : Task.CompletedTask;

    [RelayCommand]
    async Task UpdateAllAddonsAsync()
    {
        // One at a time: they're big and mostly come from the same site.
        foreach (var u in _allInstalled.SelectMany(i => i.Addons).Select(s => s.Update).OfType<AddonUpdate>().ToList())
            await GetAddonAsync(u.Kind, u.Table, u.Game, u.File);
    }

    async Task GetAddonAsync(AddonKind kind, InstalledTable table, VpsGame game, VpsFile file)
    {
        if (!TryBeginBusy(table.TablePath)) return;
        try { await GetAddonCoreAsync(kind, table, game, file); }
        finally { EndBusy(table.TablePath); }
    }

    static string KindName(AddonKind kind) => kind switch { AddonKind.Pup => "PuP pack", AddonKind.AltColor => "DMD colours", _ => "AltSound" };

    async Task GetAddonCoreAsync(AddonKind kind, InstalledTable table, VpsGame game, VpsFile file)
    {
        var updating = Addons.Record(table, kind) != null;
        var job = new JobItem($"{(updating ? "Update: " : "")}{KindName(kind)} for {table.Manifest.GameName}");
        Jobs.Insert(0, job);
        _pending.Add(new PendingDownload
        {
            Id = job.Id, Kind = PendingDownload.KindFor(kind), Title = job.Title,
            GameId = game.Id, AddonFileId = file.Id, TablePath = table.TablePath,
        });
        try
        {
            var url = Addons.UsableUrl(file) ?? throw new InvalidOperationException("The catalog has no working link for this file.");
            // A page with several files: prefer the one named like the kind (or, for colours, the ROM).
            string[] hint = kind switch
            {
                AddonKind.Pup => ["pup"],
                AddonKind.AltColor => [(file as VpsAltColorFile)?.Folder ?? "serum"],
                _ => ["altsound"],
            };
            // On a page you download from yourself, say which file: a table's page also lists its .vpx.
            var what = kind switch
            {
                AddonKind.Pup when file is VpsTableFile => "the PuP pack (the .zip with \"PuP\" in its name, newest version, not the .vpx)",
                AddonKind.Pup => "the PuP pack",
                AddonKind.AltColor => "the colorization",
                _ => "the AltSound pack",
            };
            var downloaded = await FetchAsync(url, job, hint, what);
            InstalledAddon record;
            var note = "";
            if (kind == AddonKind.Pup)
            {
                job.Report("Reading the table script…");
                var names = await Task.Run(() => PupPacks.ExpectedNames(table));
                record = await Task.Run(() => PupPacks.Install(downloaded, table, file, names, job.Report));
                table.Manifest.PupName ??= names.Script;
                note = names.ScriptTurnsPupOff ? " The table script has usePUP = False: set it to True (VPX editor → Script) to see the pack."
                    : Settings.UsePupPlugin ? " It shows on the cabinet's backglass and DMD in VR."
                    : " Turn on the PinUP Player plugin in Setup to see it in VR.";
            }
            else
            {
                record = await Task.Run(() => AltPacks.Install(kind, downloaded, table, file, job.Report));
                note = !Settings.UseBuiltInPinmame ? " Turn on VPX's built-in PinMAME in Setup to use it."
                    : kind == AddonKind.AltColor ? " VPX colours the DMD with it."
                    : " VPX plays it instead of the ROM's own sounds.";
            }
            CleanJobDownloads(job); // archives can be gigabytes
            Addons.SetRecord(table, kind, record);
            TableInstaller.SaveManifest(table);
            job.Finish($"✓ {(updating ? "Updated" : "Installed")} {KindName(kind)}{(record.Version is { } v ? " v" + v : "")} in {record.Location}\\{record.Folder}.{note}");
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
    async Task RemoveAddonAsync(AddonSlot slot)
    {
        var table = slot.Item.Table;
        var state = slot.State;
        var record = Addons.Record(table, slot.Kind);
        var folder = state.Tracked is { } tr ? (slot.Kind == AddonKind.AltColor ? $"{tr.Location}\\{tr.Folder}" : tr.Folder) : state.Folders.FirstOrDefault();
        if (folder == null) return;
        var shown = slot.Kind switch { AddonKind.Pup => $"{PupPacks.FolderName}\\{folder}", AddonKind.AltSound => $"{AltPacks.SoundFolder}\\{folder}", _ => folder };
        if (!await _ui.ConfirmAsync($"Remove {slot.Name}?", $"Delete {shown} from {slot.Item.Title}'s folder? The table itself stays.")) return;
        try
        {
            if (slot.Kind == AddonKind.Pup) PupPacks.Remove(table, folder);
            else AltPacks.Remove(table, slot.Kind, folder);
            if (state.Tracked != null && record != null)
            {
                Addons.SetRecord(table, slot.Kind, null);
                TableInstaller.SaveManifest(table);
            }
            PlayStatus = $"Removed {slot.Item.Title}'s {slot.Name}.";
        }
        catch (Exception e) { PlayStatus = $"Couldn't remove the {slot.Name}: " + e.Message; }
        RefreshInstalled();
    }

    [RelayCommand]
    void OpenAddonFolder(AddonSlot slot)
    {
        var t = slot.Item.Table;
        var dir = slot.Kind switch
        {
            AddonKind.Pup => Path.Combine(t.Folder, PupPacks.FolderName),
            AddonKind.AltSound => Path.Combine(t.Folder, AltPacks.SoundFolder),
            _ => slot.State.Folders.FirstOrDefault() is { } f ? Path.Combine(t.Folder, f) : t.Folder,
        };
        if (Directory.Exists(dir)) _ui.OpenExternal(dir);
    }

    void ResumeAddon(PendingDownload d, Func<string?, InstalledTable?> find, List<string> skipped)
    {
        var kind = PendingDownload.AddonKindOf(d.Kind);
        var game = _allGames.FirstOrDefault(g => g.Id == d.GameId);
        var file = game == null || kind == null ? null : Addons.FilesOf(game, kind.Value).FirstOrDefault(f => f.Id == d.AddonFileId);
        if (find(d.TablePath) is { } t && game != null && file != null) _ = GetAddonAsync(kind!.Value, t, game, file);
        else skipped.Add(d.Title);
    }
}

/// <summary>One of the catalog's files for a table, as an item in an add-on menu on its card.</summary>
public sealed record AddonChoice(AddonSlot Slot, VpsGame Game, VpsFile File, string Label);
