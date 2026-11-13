using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// ROMs for My Tables' cards: finding, downloading (Stern's site, the catalog's sources) and adding a ROM.
public sealed partial class RomsViewModel(MainViewModel app) : ChildViewModel(app)
{
    // ---- ROMs ------------------------------------------------------------------

    /// <summary>The catalog's ROM download (usually on VPForums) for a table's missing ROM.</summary>
    internal VpsFile? FindRomSource(InstalledTable t)
    {
        // Missing ROM first; otherwise the one the table already uses (to fetch its whole ROM family).
        var missing = t.MissingRoms.Count > 0 ? t.MissingRoms : t.Manifest.RomNames;
        if (missing.Count == 0)
        {
            return null;
        }

        var game = App.Catalog.CatalogGame(t.Manifest.GameId);
        bool Matches(VpsFile r, string name) =>
            string.Equals(r.Version, name, StringComparison.OrdinalIgnoreCase)
            && UsableRomUrl(r) != null;
        foreach (var name in missing)
        {
            // This table's own catalog entry first; a parent set or a borrowed ROM (an original running
            // Attack from Mars's ROM) is often only listed under the machine it came from.
            if (game?.RomFiles?.FirstOrDefault(r => Matches(r, name)) is { } own)
            {
                return own;
            }

            if (
                App.Catalog._allGames.SelectMany(g => g.RomFiles ?? []).FirstOrDefault(r => Matches(r, name)) is
                { } other
            )
            {
                return other;
            }
        }
        return null;
    }

    /// <summary>Stern-made machine, or the catalog points at Stern's game code for it.</summary>
    internal bool IsSternTable(InstalledTable t)
    {
        if (!t.IsVpx)
        {
            return false;
        }

        var game = App.Catalog.CatalogGame(t.Manifest.GameId);
        return string.Equals(
                t.Manifest.Manufacturer ?? game?.Manufacturer,
                "Stern",
                StringComparison.OrdinalIgnoreCase
            )
            || (game?.RomFiles ?? []).Any(r =>
                r.Urls?.Any(u => !u.Broken && SternGameCode.IsSternUrl(u.Url)) == true
            );
    }

    /// <summary>The catalog's links to Stern's game code search, or a search by the machine's name.</summary>
    List<string> SternSearchUrls(InstalledTable t)
    {
        var game = App.Catalog.CatalogGame(t.Manifest.GameId);
        var urls = (game?.RomFiles ?? [])
            .SelectMany(r => r.Urls ?? [])
            .Where(u => !u.Broken && SternGameCode.IsSternUrl(u.Url))
            .Select(u => u.Url)
            .Distinct()
            .ToList();
        if (urls.Count == 0)
        {
            // PinMAME's name for the machine searches better than the catalog's ("Avatar" vs "James Cameron's Avatar").
            var rom = t.Manifest.RomNames.FirstOrDefault();
            var name =
                rom != null
                    ? PinmameInfo.Get(Settings, Path.Combine(t.Folder, "pinmame"), rom)?.Description
                    : null;
            name = name?.Split(" (")[0].Replace(", The", "") ?? t.Manifest.MatchName;
            urls.Add(SternGameCode.SearchUrl(name));
        }
        return urls;
    }

    readonly HashSet<string> _romRechecked = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _romCheckOne = new(1);

    /// <summary>
    /// Works out once per table what its ROM really needs (see <see cref="RomRequirements"/>): tables
    /// installed by older versions may list a commented-out ROM name, miss an alias or a parent set.
    /// Reading a big table file takes a moment, so it's done in the background, one table at a time.
    /// </summary>
    internal async Task RecheckRomNeedAsync(InstalledTable t)
    {
        // VPS tags table files that run without a ROM. Tables installed before that was read, or linked to the
        // catalog after an import, don't have the tag in their manifest yet; take it from the catalog.
        if (
            t.IsVpx
            && t.Manifest.RomNames.Count > 0
            && !RomRequirements.TaggedNoRom(t.Manifest)
            && t.Manifest.TableFileId != ""
            && App.Catalog._allGames
                .FirstOrDefault(g => g.Id == t.Manifest.GameId)
                ?.TableFiles?.FirstOrDefault(f => f.Id == t.Manifest.TableFileId)
                is { } vpsFile
            && VpsCatalog.HasFeature(vpsFile, RomRequirements.NoRomFeature)
        )
        {
            t.Manifest.Features = [.. t.Manifest.Features, RomRequirements.NoRomFeature];
            t.Manifest.RomNames = [];
            t.Manifest.RomParents = [];
            t.Manifest.RomAlias = null;
            // Rescan only once it's saved: a rescan reads the manifest back from disk, so after a failed save it
            // would find the old one and land here again.
            if (TrySaveManifest(t))
            {
                App.Library.RefreshInstalledSoon();
            }

            return;
        }
        // A parent listed as missing may be out of date: it was noted before the table's own ROM was there, or the
        // ROM was copied in by hand since. Asking PinMAME again takes milliseconds.
        if (
            t.IsVpx
            && t.Manifest.RomParents.Count > 0
            && t.MissingRoms.Count > 0
            && t.Manifest.RomParents.Contains(t.MissingRoms[0], StringComparer.OrdinalIgnoreCase)
        )
        {
            var before = string.Join(",", t.Manifest.RomParents);
            await Task.Run(() => RomRequirements.RefreshParents(t, Settings));
            if (before != string.Join(",", t.Manifest.RomParents))
            {
                if (TrySaveManifest(t))
                {
                    App.Library.RefreshInstalledSoon();
                }

                return;
            }
        }
        if (
            !t.IsVpx
            || t.Manifest.RomCheck >= RomRequirements.Version
            || !File.Exists(t.TablePath)
            || !_romRechecked.Add(t.TablePath)
        )
        {
            return;
        }
        // A loose .vpx straight in the tables folder has no folder (or manifest) of its own to update.
        if (
            string.Equals(
                Path.GetFullPath(t.Folder).TrimEnd('\\', '/'),
                Path.GetFullPath(Settings.TablesDir).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return;
        }

        await _romCheckOne.WaitAsync();
        bool changed;
        try
        {
            changed = await Task.Run(() => RomRequirements.Update(t, Settings));
        }
        finally
        {
            _romCheckOne.Release();
        }
        if (TrySaveManifest(t) && changed)
        {
            App.Library.RefreshInstalledSoon();
        }
    }

    /// <summary>False when the manifest couldn't be written (read-only folder, file in use): still corrected for this session.</summary>
    static bool TrySaveManifest(InstalledTable t)
    {
        try
        {
            TableInstaller.SaveManifest(t);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    void SearchRom(InstalledItem item)
    {
        if (item.Table.MissingRoms.FirstOrDefault() is { } rom)
        {
            Ui.OpenExternal(RomSources.VpuSearchUrl(rom));
        }
    }

    /// <summary>First link that's a real ROM download (not Stern's firmware site, not a .bin).</summary>
    static string? UsableRomUrl(VpsFile r) =>
        r.Urls?.FirstOrDefault(u => !u.Broken && RomSources.IsUsable(u.Url, r.Version))?.Url;

    // Per-table commands run for several tables at once (a shared command would otherwise grey out
    // every card's button while one download runs); only the busy table's buttons are disabled.
    [RelayCommand(AllowConcurrentExecutions = true)]
    Task GetRom(InstalledItem item) => GetRomAsync(item.Table);

    internal async Task GetRomAsync(InstalledTable table)
    {
        if (!App.Library.TryBeginBusy(table.TablePath))
        {
            return; // already fetching this table's ROM
        }

        try
        {
            await GetRomCoreAsync(table);
        }
        finally
        {
            App.Library.EndBusy(table.TablePath);
        }
    }

    async Task GetRomCoreAsync(InstalledTable table)
    {
        HashSet<string> family;
        try
        {
            // Older installs list every ROM VPS knows; read the script to find the one this table loads.
            // Off the UI thread: tables run to hundreds of MB.
            if (
                table.Manifest.RomNames.Count > 1
                && await Task.Run(() => RomDetector.FromVpx(table.TablePath)) is { } exact
            )
            {
                table.Manifest.RomNames = [exact];
                try
                {
                    TableInstaller.SaveManifest(table);
                }
                catch
                { /* still usable this session */
                }
            }

            // The table's ROM set and its parents: the only ROM zips that belong to it.
            family = await Task.Run(() => RomFamily(table));
        }
        catch (Exception e)
        {
            // The table file is open elsewhere (the VPX editor) or gone.
            App.Library.PlayStatus = $"Couldn't read {table.Manifest.GameName}'s ROM needs: {e.Message}";
            return;
        }
        var job = new JobItem($"ROM for {table.Manifest.GameName}")
        {
            FileNames = [.. table.MissingRoms, .. family, table.Manifest.MatchName],
            FileTypes = [".zip", ".7z", ".rar"],
            NamesMustMatch = true,
        };
        App.Downloads.Jobs.Insert(0, job);
        App.Downloads.Pending.Add(
            new PendingDownload
            {
                Id = job.Id,
                Kind = PendingDownload.RomKind,
                Title = job.Title,
                GameId = table.Manifest.GameId,
                TablePath = table.TablePath,
            }
        );
        string? file = null;
        try
        {
            // Stern tables: Stern's own free game code first (no sign-in, checked by PinMAME before it's kept).
            if (IsSternTable(table) && table.MissingRoms.Count > 0)
            {
                var urls = SternSearchUrls(table);
                var fromStern = new List<string>();
                string? aliasNote = null;
                foreach (var rom in table.MissingRoms.ToList())
                {
                    if (
                        await App.Downloads.WithDownloadSlotAsync(
                            job,
                            () =>
                                SternGameCode.InstallAsync(
                                    table,
                                    rom,
                                    urls,
                                    Settings,
                                    job.Report,
                                    job.ProgressReporter,
                                    job.Cts.Token
                                )
                        ) is
                        { } zip
                    )
                    {
                        fromStern.Add(zip);
                    }
                    else if (await SternOtherVersionAsync(table, rom, urls, job) is { } other)
                    {
                        fromStern.Add(other.zip);
                        aliasNote =
                            $" Stern only has {other.version}; the table asks for {rom}, so PinMAME is set to run {other.version} in its place.";
                    }
                }
                if (fromStern.Count > 0)
                {
                    await Task.Run(() => RomRequirements.RefreshParents(table, Settings));
                    try
                    {
                        TableInstaller.SaveManifest(table);
                    }
                    catch { }
                    if (table.MissingRoms.Count == 0)
                    {
                        job.Finish(
                            $"✓ Installed {string.Join(", ", fromStern.Select(Path.GetFileName))} from Stern's site.{aliasNote} Ready to play."
                        );
                        App.Library.RefreshInstalled();
                        return;
                    }
                }
                job.Report("Stern's site doesn't have it; trying the catalog's other sources…");
            }

            var src =
                FindRomSource(table)
                ?? throw new InvalidOperationException(
                    $"The catalog has no download for {string.Join(" / ", table.MissingRoms)}.zip. Use Add ROM with a file you have."
                );
            var url = UsableRomUrl(src)!;
            // ROM pages often hold every version of the machine's ROM; clones need their parent,
            // so take them all (they're small).
            List<string> files =
                BrowserDownloader.IsDirectFileUrl(url)
                    ? [file = await App.Downloads.FetchAsync(url, job, [src.Version!])]
                : SitePolicy.AllowsAutomation(url)
                    ? await App.Downloads.WithDownloadSlotAsync(
                        job,
                        () =>
                            App.Downloads.Browser.DownloadAllAsync(
                                url,
                                DownloadsViewModel.JobDownloadDir(job),
                                job.Report,
                                job.Cts.Token
                            )
                    )
                : await App.Downloads.Assisted.DownloadAllAsync(
                    url,
                    DownloadsViewModel.JobDownloadDir(job),
                    job,
                    got => RomSetComplete(table, got, src.Version!)
                );
            files = await Task.Run(() => DropOtherMachinesRoms(files, table, family, job));
            job.Report("Installing ROM…");
            var placed = await Task.Run(() => RomInstaller.InstallAll(files, table, src.Version!));
            // With the files in place PinMAME can tell whether the set is complete (parents included).
            job.Report("Checking the ROM set…");
            await Task.Run(() => RomRequirements.RefreshParents(table, Settings));
            try
            {
                TableInstaller.SaveManifest(table);
            }
            catch { }
            var stillMissing = table.MissingRoms.Count > 0;
            var needsParent =
                stillMissing
                && table.MissingRoms.All(r =>
                    table.Manifest.RomParents.Contains(r, StringComparer.OrdinalIgnoreCase)
                );
            job.Finish(
                stillMissing
                    ? needsParent
                        ? $"Installed {string.Join(", ", placed.Select(Path.GetFileName))}. It's a version of {string.Join(" / ", table.MissingRoms)} and also needs "
                            + $"{string.Join(" / ", table.MissingRoms.Select(r => r + ".zip"))} beside it: use Get ROM again, or Add ROM if you have it."
                        : $"Installed {placed.Count} ROM file(s), but none is {string.Join(" / ", table.MissingRoms)}.zip. Use Add ROM if you have it."
                    : placed.Count == 1
                        ? $"✓ Installed {Path.GetFileName(placed[0])}. Ready to play."
                        : $"✓ Installed {placed.Count} ROM files ({string.Join(", ", placed.Select(Path.GetFileNameWithoutExtension))}). Ready to play.",
                error: stillMissing
            );
            App.Library.RefreshInstalled();
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
            DownloadsViewModel.CleanJobDownloads(job);
            // Finished, failed or cancelled: nothing to resume. (Not when it was cut off by the app
            // closing: that entry stays so the download is offered again next time.)
            App.Downloads.FinishPending(job);
        }
    }

    /// <summary>
    /// Stern's site only keeps a machine's latest code (AC/DC 1.70), while a table may ask for an older
    /// version (acd_168h). When no other source has that exact version, install the version Stern has (the
    /// set it's a clone of, per PinMAME) and alias the table's name to it in pinmame\alias.txt, which PinMAME
    /// honours even for a real set name. Only for the table's own ROM, not a parent it lacks.
    /// </summary>
    async Task<(string zip, string version)?> SternOtherVersionAsync(
        InstalledTable table,
        string rom,
        List<string> urls,
        JobItem job
    )
    {
        if (
            !table.Manifest.RomNames.Contains(rom, StringComparer.OrdinalIgnoreCase)
            || FindRomSource(table) != null
        )
        {
            return null;
        }

        var pinmameDir = Path.Combine(table.Folder, "pinmame");
        var others = await Task.Run(() =>
            PinmameInfo.Family(Settings, pinmameDir, rom).Skip(1).Select(g => g.Name).ToList()
        );
        foreach (var version in others)
        {
            if (
                await App.Downloads.WithDownloadSlotAsync(
                    job,
                    () =>
                        SternGameCode.InstallAsync(
                            table,
                            version,
                            urls,
                            Settings,
                            job.Report,
                            job.ProgressReporter,
                            job.Cts.Token
                        )
                )
                is not { } zip
            )
            {
                continue;
            }

            RomRequirements.WriteAlias(pinmameDir, rom, version);
            table.Manifest.RomAlias = rom;
            table.Manifest.RomNames = [version];
            table.Manifest.RomParents = [];
            return (zip, version);
        }
        return null;
    }

    /// <summary>
    /// Would the table's ROM load with these downloads added? They're installed into a scratch copy of the
    /// table's pinmame\roms (alongside what's already there, e.g. a parent) and PinMAME checks the set, the
    /// way it will when the table starts. So a ROM page's wait ends by itself once you've got what's needed:
    /// the ROM, plus its parent when it's a clone that needs one. Without PinMAME, the ROM zip being there will do.
    /// </summary>
    bool RomSetComplete(InstalledTable t, IReadOnlyList<string> downloads, string fallbackName)
    {
        var rom = t.Manifest.RomNames.FirstOrDefault() ?? fallbackName;
        var tmp = Path.Combine(
            Path.GetTempPath(),
            "vrtm-romcheck-" + Guid.NewGuid().ToString("N")[..8]
        );
        try
        {
            var roms = Path.Combine(tmp, "pinmame", "roms");
            Directory.CreateDirectory(roms);
            var own = Path.Combine(t.Folder, "pinmame", "roms");
            if (Directory.Exists(own))
            {
                foreach (var z in Directory.EnumerateFiles(own, "*.zip"))
                {
                    File.Copy(z, Path.Combine(roms, Path.GetFileName(z)), true);
                }
            }

            RomInstaller.InstallAll(
                downloads,
                new InstalledTable(t.Manifest, t.TablePath, tmp),
                rom
            );
            if (!File.Exists(Path.Combine(roms, rom + ".zip")))
            {
                return false;
            }

            return PinmameInfo.Get(Settings, Path.Combine(tmp, "pinmame"), rom) is not { } g
                || g.Found;
        }
        catch
        {
            return false;
        } // not a ROM yet (a half-read archive, a stray file): keep waiting
        finally
        {
            try
            {
                Directory.Delete(tmp, true);
            }
            catch { }
        }
    }

    /// <summary>The table's ROM names and, when VPX's PinMAME can say, the parent sets they're built on.</summary>
    HashSet<string> RomFamily(InstalledTable t)
    {
        var names = new HashSet<string>(
            [.. t.Manifest.RomNames, .. t.Manifest.RomParents, .. t.MissingRoms],
            StringComparer.OrdinalIgnoreCase
        );
        var pinmameDir = Path.Combine(t.Folder, "pinmame");
        foreach (var rom in t.Manifest.RomNames.ToList())
        {
            foreach (var g in PinmameInfo.Family(Settings, pinmameDir, rom))
            {
                names.Add(g.Name);
            }
        }

        return names;
    }

    /// <summary>
    /// Leaves out downloaded ROM zips that PinMAME knows as another machine's set (dungdrag.zip handed to
    /// Beavis and Butt-Head, which runs beav_butt): installed under the table's ROM name, they'd look like
    /// its ROM and fail when the table starts. Versions of the same machine (clones and parents) stay.
    /// Throws when nothing that's left belongs to the table.
    /// </summary>
    List<string> DropOtherMachinesRoms(
        List<string> files,
        InstalledTable t,
        HashSet<string> family,
        JobItem job
    )
    {
        var pinmameDir = Path.Combine(t.Folder, "pinmame");
        var keep = new List<string>();
        var other = new List<(string file, PinmameGame game)>();
        foreach (var f in files)
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            if (!f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || family.Contains(stem))
            {
                keep.Add(f);
                continue;
            }
            var g = PinmameInfo.Get(Settings, pinmameDir, stem);
            // Not a set PinMAME knows (a site's own file name): it may well be the ROM, renamed when installed.
            if (
                g == null
                || family.Contains(g.Name)
                || (g.CloneOf != null && family.Contains(g.CloneOf))
            )
            {
                keep.Add(f);
            }
            else
            {
                other.Add((f, g));
            }
        }
        if (other.Count == 0)
        {
            return keep;
        }

        var which = string.Join(
            ", ",
            other.Select(o => $"{Path.GetFileName(o.file)} ({o.game.Description})")
        );
        if (keep.Count == 0)
        {
            throw new InvalidOperationException(
                $"Got {which}: that's another machine's ROM, not {string.Join(" / ", t.MissingRoms.DefaultIfEmpty(t.Manifest.RomNames.FirstOrDefault() ?? "this table's"))}.zip. Nothing was installed."
            );
        }

        job.Report($"Skipping {which}: another machine's ROM.");
        return keep;
    }

    [RelayCommand]
    internal async Task AddRomAsync(InstalledItem item)
    {
        // Filter to the file it needs, starting where browsers save downloads.
        var wanted =
            item.Table.MissingRoms.FirstOrDefault()
            ?? item.Table.Manifest.RomNames.FirstOrDefault();
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads"
        );
        var file = await Ui.PickFileAsync(
            $"Choose the ROM zip for {item.Title}",
            "*.zip",
            wanted != null ? wanted + ".zip" : null,
            downloads
        );
        if (file == null)
        {
            return;
        }

        try
        {
            TableInstaller.AddRom(item.Table, file);
        }
        catch (Exception e)
        {
            App.Library.PlayStatus = "Couldn't add the ROM: " + e.Message;
            return;
        }
        App.Library.RefreshInstalled();
    }

}
