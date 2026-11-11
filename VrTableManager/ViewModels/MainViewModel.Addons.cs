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
    [ObservableProperty]
    public partial int AddonUpdateCount { get; set; }

    [ObservableProperty]
    public partial string AddonUpdateText { get; set; } = "Not checked yet.";

    /// <summary>VR play shows PuP packs through VPX's PinUP Player plugin.</summary>
    [ObservableProperty]
    public partial bool UsePupPlugin { get; set; }

    /// <summary>VR play turns on VPX's FlexDMD and B2S plugins.</summary>
    [ObservableProperty]
    public partial bool UseDisplayPlugins { get; set; }

    /// <summary>VR play adds a backglass display to tables without one, for PuP packs and B2S backglasses.</summary>
    [ObservableProperty]
    public partial bool VrAutoBackglass { get; set; }

    /// <summary>Desktop play turns off VPX's bloom.</summary>
    [ObservableProperty]
    public partial bool DesktopBloomOff { get; set; }

    /// <summary>A table's hand-set VR room switch is turned on for VR play and off for desktop play.</summary>
    [ObservableProperty]
    public partial bool AutoVrRoom { get; set; }

    IReadOnlyList<AddonChoice> ChoicesFor(AddonSlot slot)
    {
        var t = slot.Item.Table;
        if (
            !Addons.CanHold(t, Settings)
            || t.Manifest.GameId == ""
            || _allGames.FirstOrDefault(g => g.Id == t.Manifest.GameId) is not { } game
        )
        {
            return [];
        }

        return slot.Kind == AddonKind.Pup
            ? PupPacks
                .SourcesFor(game, t)
                .Select(f => new AddonChoice(slot, game, f, PupPacks.Describe(f)))
                .ToList()
            : AltPacks
                .SourcesFor(game, t, slot.Kind)
                .Select(f => new AddonChoice(slot, game, f, AltPacks.Describe(f, t)))
                .ToList();
    }

    void RefreshAddonState(InstalledItem item)
    {
        foreach (var slot in item.Addons)
        {
            slot.State = Addons.StateOf(item.Table, slot.Kind);
            // Colours and sounds are the ROM's: only a table that runs one can take them.
            var hasRom = item.Table.Manifest.RomNames.Count > 0;
            slot.CanAddFile =
                Addons.CanHold(item.Table, Settings) && (slot.Kind == AddonKind.Pup || hasRom);
            slot.ScriptWantsIt =
                slot.Kind == AddonKind.Pup
                    ? !string.IsNullOrEmpty(item.Table.Manifest.PupName)
                    : hasRom;
        }
    }

    /// <summary>Catalog add-ons and their updates for every card (from the loaded catalog; cheap).</summary>
    void ApplyAddons()
    {
        if (_allGames.Count == 0)
        {
            return;
        }

        var updates = Addons.FindUpdates(_allInstalled.Select(i => i.Table), _allGames);
        foreach (var item in _allInstalled)
        {
            foreach (var slot in item.Addons)
            {
                slot.Choices = ChoicesFor(slot);
                slot.Update = updates.FirstOrDefault(u =>
                    u.Kind == slot.Kind
                    && string.Equals(
                        u.Table.TablePath,
                        item.Table.TablePath,
                        StringComparison.OrdinalIgnoreCase
                    )
                );
            }
        }

        AddonUpdateCount = updates.Count;
        var tracked = _allInstalled.Sum(i =>
            Enum.GetValues<AddonKind>().Count(k => Addons.Record(i.Table, k) != null)
        );
        AddonUpdateText = updates.Count switch
        {
            0 when tracked == 0 =>
                "No PuP packs, DMD colours or AltSound installed from the catalog yet.",
            0 => tracked == 1
                ? "Your add-on is up to date."
                : $"All {tracked} add-ons are up to date.",
            1 => "1 add-on has a newer version.",
            _ => $"{updates.Count} add-ons have newer versions.",
        };
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    Task GetAddon(AddonChoice choice) =>
        GetAddonAsync(choice.Slot.Kind, choice.Slot.Item.Table, choice.Game, choice.File);

    [RelayCommand(AllowConcurrentExecutions = true)]
    Task UpdateAddon(AddonSlot slot) =>
        slot.Update is { } u ? GetAddonAsync(u.Kind, u.Table, u.Game, u.File) : Task.CompletedTask;

    [RelayCommand]
    async Task UpdateAllAddonsAsync()
    {
        // One at a time: they're big and mostly come from the same site.
        foreach (
            var u in _allInstalled
                .SelectMany(i => i.Addons)
                .Select(s => s.Update)
                .OfType<AddonUpdate>()
                .ToList()
        )
        {
            await GetAddonAsync(u.Kind, u.Table, u.Game, u.File);
        }
    }

    async Task GetAddonAsync(AddonKind kind, InstalledTable table, VpsGame game, VpsFile file)
    {
        if (!TryBeginBusy(table.TablePath))
        {
            return;
        }

        try
        {
            await GetAddonCoreAsync(kind, table, game, file);
        }
        finally
        {
            EndBusy(table.TablePath);
        }
    }

    Task<string> FetchAddonAsync(AddonKind kind, VpsFile file, JobItem job)
    {
        var url =
            Addons.UsableUrl(file)
            ?? throw new InvalidOperationException(
                "The catalog has no working link for this file."
            );
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
            AddonKind.Pup when file is VpsTableFile =>
                "the PuP pack (the .zip with \"PuP\" in its name, newest version, not the .vpx)",
            AddonKind.Pup => "the PuP pack",
            AddonKind.AltColor => "the colorization",
            _ => "the AltSound pack",
        };
        return FetchAsync(url, job, hint, what);
    }

    /// <summary>
    /// PUP → "Install from a file you downloaded…": a pack you already have, without going to its page. Which
    /// catalog entry it is (one click) keeps update checks working; "Not listed" installs it untracked by version.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task InstallAddonFileAsync(AddonSlot slot)
    {
        var table = slot.Item.Table;
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads"
        );
        // Packs people already have are often unpacked folders (an altsound\<rom> or pupvideos\<pack> from another
        // setup), so a folder will do as well as the archive. Either way it's copied; the original stays.
        var form = await _ui.ChoiceAsync(
            $"Add a {KindName(slot.Kind)} you already have",
            $"Pick the archive you downloaded, or a folder that already holds the {KindName(slot.Kind)}. "
                + "It's copied into the table's folder; your file or folder stays where it is.",
            ["ARCHIVE OR FILE", "FOLDER", "CANCEL"]
        );
        if (form is < 0 or 2)
        {
            return;
        }

        var path =
            form == 1
                ? await _ui.PickFolderAsync(
                    $"Choose the folder with the {KindName(slot.Kind)} for {slot.Item.Title}"
                )
                : await _ui.PickFileAsync(
                    $"Choose the {KindName(slot.Kind)} for {slot.Item.Title}",
                    slot.Kind == AddonKind.AltColor
                        ? "*.zip;*.7z;*.rar;*.crz;*.crom;*.vni;*.pal;*.pac"
                        : "*.zip;*.7z;*.rar",
                    startFolder: downloads
                );
        if (path == null)
        {
            return;
        }

        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        VpsFile entry = new() { Id = "file:" + name };
        if (slot.Choices.Count > 0)
        {
            List<string> buttons = [.. slot.Choices.Select(c => c.Label), "NOT LISTED", "CANCEL"];
            var pick = await _ui.ChoiceAsync(
                $"Which {KindName(slot.Kind)} is this?",
                $"{name}\n\nPick the catalog version it is, so you're told when a newer one comes out. Not listed? It's installed all the same.",
                [.. buttons]
            );
            if (pick < 0 || buttons[pick] == "CANCEL")
            {
                return;
            }

            if (pick < slot.Choices.Count)
            {
                entry = slot.Choices[pick].File;
            }
        }
        var game =
            _allGames.FirstOrDefault(g => g.Id == table.Manifest.GameId)
            ?? new VpsGame { Id = table.Manifest.GameId, Name = table.Manifest.GameName };

        if (!TryBeginBusy(table.TablePath))
        {
            return;
        }

        try
        {
            await GetAddonCoreAsync(slot.Kind, table, game, entry, path);
        }
        finally
        {
            EndBusy(table.TablePath);
        }
    }

    static string KindName(AddonKind kind) =>
        kind switch
        {
            AddonKind.Pup => "PuP pack",
            AddonKind.AltColor => "DMD colours",
            _ => "AltSound",
        };

    /// <param name="localFile">A file the user already downloaded: installed as it is, nothing is fetched (and it's left where it is).</param>
    async Task GetAddonCoreAsync(
        AddonKind kind,
        InstalledTable table,
        VpsGame game,
        VpsFile file,
        string? localFile = null
    )
    {
        var updating = Addons.Record(table, kind) != null;
        string[] archives = [".zip", ".7z", ".rar"];
        var job = new JobItem(
            $"{(updating ? "Update: " : "")}{KindName(kind)} for {table.Manifest.GameName}"
                + (localFile != null ? $" — from {Path.GetFileName(localFile)}" : "")
        )
        {
            // The kind's own word too: a table's page offers its .vpx and its PuP zip side by side.
            FileNames =
            [
                table.Manifest.GameName,
                .. table.Manifest.RomNames,
                .. new[] { table.Manifest.RomAlias, table.Manifest.PupName }.OfType<string>(),
                kind switch
                {
                    AddonKind.Pup => "pup",
                    AddonKind.AltColor => "serum",
                    _ => "altsound",
                },
            ],
            FileTypes =
                kind == AddonKind.AltColor
                    ? [.. archives, ".crz", ".crom", ".cromc", ".vni", ".pal", ".pac"]
                    : archives,
        };
        Jobs.Insert(0, job);
        // Only a download can be resumed after a restart.
        if (localFile == null)
        {
            _pending.Add(
                new PendingDownload
                {
                    Id = job.Id,
                    Kind = PendingDownload.KindFor(kind),
                    Title = job.Title,
                    GameId = game.Id,
                    AddonFileId = file.Id,
                    TablePath = table.TablePath,
                }
            );
        }

        try
        {
            var downloaded = localFile ?? await FetchAddonAsync(kind, file, job);
            InstalledAddon record;
            var note = "";
            if (kind == AddonKind.Pup)
            {
                job.Report("Reading the table script…");
                var names = await Task.Run(() => PupPacks.ExpectedNames(table));
                var pup = await Task.Run(() =>
                    PupPacks.Install(downloaded, table, file, names, job.Report)
                );
                record = pup.Record;
                table.Manifest.PupName ??= names.Script;
                note =
                    (
                        names.ScriptTurnsPupOff
                            ? " The table script has usePUP = False: set it to True (VPX editor → Script) to see the pack."
                        : Settings.UsePupPlugin
                            ? " It shows on the cabinet's backglass and DMD in VR."
                        : " Turn on the PinUP Player plugin in Setup to see it in VR."
                    ) + pup.Layout.Note($"{PupPacks.FolderName}\\{record.Folder}");
            }
            else
            {
                record = await Task.Run(() =>
                    AltPacks.Install(kind, downloaded, table, file, job.Report)
                );
                note =
                    !Settings.UseBuiltInPinmame
                        ? " Turn on VPX's built-in PinMAME in Setup to use it."
                    : kind == AddonKind.AltColor ? " VPX colours the DMD with it."
                    : " VPX plays it instead of the ROM's own sounds.";
            }
            CleanJobDownloads(job); // archives can be gigabytes
            Addons.SetRecord(table, kind, record);
            TableInstaller.SaveManifest(table);
            job.Finish(
                $"✓ {(updating ? "Updated" : "Installed")} {KindName(kind)}{(record.Version is { } v ? " v" + v : "")} in {record.Location}\\{record.Folder}.{note}"
            );
            RefreshInstalled();
        }
        catch (OperationCanceledException)
        {
            job.Finish("Cancelled", error: true);
        }
        catch (InvalidDataException e) when (PacOnlyFiles.IsError(e))
        {
            // The catalog can't tell PAC-only files apart; remember this one so the list says so from now on.
            PacOnlyFiles.Mark(file);
            var others = AltPacks
                .SourcesFor(game, table, kind)
                .Where(f => f.Id != file.Id && !PacOnlyFiles.IsKnown(f))
                .Select(f => AltPacks.Describe(f, table))
                .ToList();
            job.Finish(
                "Failed: "
                    + e.Message
                    + (
                        others.Count > 0
                            ? " Try another colorization for this table: "
                                + string.Join("; ", others.Take(3))
                                + "."
                            : " The catalog has no other colorization for this table yet, so its DMD stays in the ROM's own colours."
                    ),
                error: true
            );
            RefreshInstalled();
        }
        catch (Exception e)
        {
            job.Finish("Failed: " + e.Message, error: true);
        }
        finally
        {
            CleanJobDownloads(job);
            if (!_closing)
            {
                _pending.Remove(job.Id);
            }
        }
    }

    [RelayCommand]
    async Task RemoveAddonAsync(AddonSlot slot)
    {
        var table = slot.Item.Table;
        var state = slot.State;
        var record = Addons.Record(table, slot.Kind);
        var folder = state.Tracked is { } tr
            ? (slot.Kind == AddonKind.AltColor ? $"{tr.Location}\\{tr.Folder}" : tr.Folder)
            : state.Folders.FirstOrDefault();
        if (folder == null)
        {
            return;
        }

        var shown = slot.Kind switch
        {
            AddonKind.Pup => $"{PupPacks.FolderName}\\{folder}",
            AddonKind.AltSound => $"{AltPacks.SoundFolder}\\{folder}",
            _ => folder,
        };
        if (
            !await _ui.ConfirmAsync(
                $"Remove {slot.Name}?",
                $"Delete {shown} from {slot.Item.Title}'s folder? The table itself stays."
            )
        )
        {
            return;
        }

        try
        {
            if (slot.Kind == AddonKind.Pup)
            {
                PupPacks.Remove(table, folder);
            }
            else
            {
                AltPacks.Remove(table, slot.Kind, folder);
            }

            if (state.Tracked != null && record != null)
            {
                Addons.SetRecord(table, slot.Kind, null);
                TableInstaller.SaveManifest(table);
            }
            PlayStatus = $"Removed {slot.Item.Title}'s {slot.Name}.";
        }
        catch (Exception e)
        {
            PlayStatus = $"Couldn't remove the {slot.Name}: " + e.Message;
        }
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
            _ => slot.State.Folders.FirstOrDefault() is { } f
                ? Path.Combine(t.Folder, f)
                : t.Folder,
        };
        if (Directory.Exists(dir))
        {
            _ui.OpenExternal(dir);
        }
    }

    void ResumeAddon(PendingDownload d, Func<string?, InstalledTable?> find, List<string> skipped)
    {
        var kind = PendingDownload.AddonKindOf(d.Kind);
        var game = _allGames.FirstOrDefault(g => g.Id == d.GameId);
        var file =
            game == null || kind == null
                ? null
                : Addons.FilesOf(game, kind.Value).FirstOrDefault(f => f.Id == d.AddonFileId);
        if (find(d.TablePath) is { } t && game != null && file != null)
        {
            _ = GetAddonAsync(kind!.Value, t, game, file);
        }
        else
        {
            skipped.Add(d.Title);
        }
    }
}

/// <summary>One of the catalog's files for a table, as an item in an add-on menu on its card.</summary>
public sealed record AddonChoice(AddonSlot Slot, VpsGame Game, VpsFile File, string Label);
