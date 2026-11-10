using CommunityToolkit.Mvvm.Input;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Everything the VR lobby can show for a table, in one go: the playfield video, loading video, attract audio and
// wheel art. One media pack has all four, so it's downloaded once and each missing piece is taken out of it; the
// catalog's wheel art and sound files fill what the pack lacks, and VPX can record a playfield video at the end.
// Only fills gaps: nothing the table already has (or that you chose yourself) is replaced.
// My Tables → MORE → Get all VR lobby media for one table, GET LOBBY MEDIA for the tables shown.
public sealed partial class MainViewModel
{
    /// <summary>Where this table's missing media can come from, in the order worth downloading.</summary>
    List<(MediaPackAudio.Source Source, LobbyMedia.Kind Kind)> LobbyMediaSources(
        InstalledTable t,
        LobbyMedia.Needs needs
    )
    {
        var audio = AttractAudioSourcesFor(t);
        return LobbyMedia.Order(
            audio.Where(s => !s.IsDirect),
            WheelArtSourcesFor(t).Where(w => w.WheelPack).Select(w => w.Source),
            audio.Where(s => s.IsDirect),
            needs
        );
    }

    /// <summary>
    /// Downloads what this table's missing lobby media can come from, as one job, and keeps each missing piece.
    /// Returns what's still missing afterwards (null when the job was cancelled). The caller holds the table busy.
    /// </summary>
    /// <param name="started">Gets the job as soon as it exists (the batch uses it to stop the running one).</param>
    async Task<LobbyMedia.Needs?> GetLobbyMediaCoreAsync(
        InstalledTable table,
        Action<JobItem>? started = null
    )
    {
        var needs = LobbyMedia.Needs.Of(table);
        var sources = LobbyMediaSources(table, needs);
        if (!needs.Any || sources.Count == 0)
        {
            return needs;
        }

        var job = new JobItem($"VR lobby media for {table.Manifest.GameName}")
        {
            FileNames = [table.Manifest.GameName],
        };
        Jobs.Insert(0, job);
        started?.Invoke(job);
        var got = new List<string>();
        var problems = new List<string>();
        try
        {
            foreach (var (src, kind) in sources)
            {
                if (!LobbyMedia.Helps(kind, needs))
                {
                    continue;
                }

                job.Report($"Getting the {src.Label}…");
                try
                {
                    var file = await DownloadAttractSourceAsync(job, src);
                    var before = problems.Count;
                    got.AddRange(
                        await Task.Run(() =>
                            LobbyMedia.TakeFrom(
                                file,
                                kind,
                                table,
                                needs,
                                problems,
                                () => PlayfieldPicture(table),
                                job.Report,
                                job.Cts.Token
                            )
                        )
                    );
                    for (var i = before; i < problems.Count; i++)
                    {
                        problems[i] += $" in the {src.Label}";
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    problems.Add($"the {src.Label} failed ({e.Message})");
                }
                finally
                {
                    CleanJobDownloads(job); // the pack's other files aren't kept
                }

                ShowLobbyMedia(table, needs);
                if (!needs.Any)
                {
                    break;
                }
            }

            var missing = needs.Names();
            job.Finish(
                (got.Count > 0 ? "✓ Got the " + LobbyMedia.JoinAnd(got) + "." : "Got nothing new.")
                    + (
                        missing.Count > 0
                            ? $" No {LobbyMedia.JoinOr(missing)} in the catalog's downloads"
                                + (problems.Count > 0 ? " (" + string.Join("; ", problems) + ")" : "")
                                + "."
                            : ""
                    ),
                error: got.Count == 0
            );
            return needs;
        }
        catch (OperationCanceledException)
        {
            job.Finish("Cancelled", error: true);
            return null;
        }
        finally
        {
            CleanJobDownloads(job);
        }
    }

    /// <summary>Updates the cards for what the table has now.</summary>
    void ShowLobbyMedia(InstalledTable t, LobbyMedia.Needs needs)
    {
        SetHasLobbyVideo(t.TablePath, !needs.Video && t.HasOwnFolder);
        SetHasLoadingVideo(t.TablePath, !needs.Loading && t.HasOwnFolder);
        SetHasAttractAudio(t.TablePath, !needs.Audio);
        SetHasWheelArt(t.TablePath, WheelArt.Saved(t.Folder) != null);
    }

    // ---- One table (MORE → Get all VR lobby media) ----

    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task GetLobbyMedia(InstalledItem item)
    {
        var table = item.Table;
        if (!item.CanGetLobbyMedia)
        {
            return;
        }

        var before = LobbyMedia.Needs.Of(table);
        if (!before.Any)
        {
            PlayStatus =
                $"{item.Title} already has all its VR lobby media. MORE has Replace items to swap one.";
            return;
        }

        if (!TryBeginBusy(table.TablePath))
        {
            return;
        }

        LobbyMedia.Needs? after;
        try
        {
            after = await GetLobbyMediaCoreAsync(table);
        }
        finally
        {
            EndBusy(table.TablePath);
        }

        var fromCatalog = LobbyMediaSources(table, before).Count > 0;
        if (!fromCatalog && after is { } a && !(a.Video && item.CanHaveLobbyVideo))
        {
            PlayStatus =
                $"The catalog has no media pack, wheel art or sound file for {item.Title}. "
                + $"MORE → My artwork and Use my own… take files you have.";
            return;
        }

        if (after is not { Video: true } || !item.CanHaveLobbyVideo)
        {
            return; // cancelled, or the playfield video is there
        }

        var why = LobbyMediaSources(table, before).Any(s => s.Kind == LobbyMedia.Kind.Pack)
            ? "The media pack has no playfield video for this table."
            : "The catalog has no media pack for this table.";
        if (
            await _ui.ChoiceAsync(
                $"Record {item.Title}'s attract mode?",
                why
                    + "\n\nVPX can record the table's attract mode instead: a small VPX window opens for about half a minute "
                    + "(sound off) and closes by itself. Don't use the PC for anything else meanwhile.",
                ["RECORD IN VPX", "NOT NOW"]
            ) == 0
        )
        {
            await RecordLobbyVideoCoreAsync(item);
        }
    }

    // ---- The tables shown (My Tables → GET LOBBY MEDIA) ----

    /// <summary>
    /// Fills the missing lobby media of every table shown in My Tables (so a search or filter picks which), one
    /// table after another: their media packs first, then, if you choose, VPX recordings for those still without a
    /// playfield video. STOP (or cancelling the running job) ends it.
    /// </summary>
    [RelayCommand]
    async Task GetAllLobbyMedia()
    {
        if (IsBatchRecording)
        {
            return;
        }

        var shown = Installed
            .Where(i => i.Table.HasOwnFolder && !i.Table.IsIncomplete)
            .DistinctBy(i => i.Table.TablePath, StringComparer.OrdinalIgnoreCase)
            .Select(i => (Item: i, Needs: LobbyMedia.Needs.Of(i.Table)))
            .Where(x => x.Needs.Any)
            .ToList();
        var fromCatalog = shown
            .Where(x => LobbyMediaSources(x.Item.Table, x.Needs).Count > 0)
            .ToList();
        var recordable = shown.Count(x => x.Needs.Video && !x.Item.NeedsRom);
        if (fromCatalog.Count == 0 && recordable == 0)
        {
            PlayStatus =
                shown.Count == 0
                    ? "Every table shown already has all its VR lobby media."
                    : "The catalog has nothing more for the tables shown, and none can be recorded (they need a ROM first).";
            return;
        }

        var filtered = Installed.Count < _allInstalled.Count ? " shown" : "";
        var message =
            $"{shown.Count} of the tables{filtered} are missing some VR lobby media: a playfield video, loading video, "
            + "attract audio or wheel art. Only what's missing is added; nothing you have is replaced.\n\n"
            + (
                fromCatalog.Count > 0
                    ? $"• {fromCatalog.Count} can get it from the catalog's media packs and wheel art. Each table's download page "
                        + "opens in your browser, one after another: click Download there (some sites make you wait on a timer). "
                        + "The app keeps what it needs and deletes the rest of each pack.\n\n"
                    : ""
            )
            + (
                recordable > 0
                    ? $"• Tables still without a playfield video afterwards (up to {recordable}) can be recorded in VPX: a small "
                        + "window for about half a minute each, sound off. Leave the PC alone meanwhile."
                    : ""
            )
            + "\n\nTo do only some tables, search or filter My Tables first.";
        string[] buttons =
            recordable > 0 && fromCatalog.Count > 0
                ? ["GET ALL, RECORD THE REST", "GET ALL, DON'T RECORD", "CANCEL"]
            : recordable > 0 ? ["RECORD IN VPX", "CANCEL"]
            : ["GET ALL", "CANCEL"];
        var pick = await _ui.ChoiceAsync("Get the VR lobby media?", message, buttons);
        if (pick < 0 || pick == buttons.Length - 1)
        {
            return;
        }

        var record = recordable > 0 && pick == 0;
        if (record && IsLobbyRunning)
        {
            PlayStatus = "Close the VR lobby first: recording runs the tables in VPX.";
            return;
        }

        IsBatchRecording = true;
        _batchStop = false;
        int filled = 0,
            recorded = 0,
            stillMissing = 0;
        var stopped = false;
        try
        {
            for (var n = 0; n < shown.Count && !stopped; n++)
            {
                if (_batchStop)
                {
                    stopped = true;
                    break;
                }

                var (item, _) = shown[n];
                var table = item.Table;
                BatchRecordingText = $"■  STOP ({n + 1}/{shown.Count})";
                if (!TryBeginBusy(table.TablePath))
                {
                    continue; // something else is running for this table: skip it
                }

                LobbyMedia.Needs? left;
                try
                {
                    left = await GetLobbyMediaCoreAsync(table, j => _batchJob = j);
                }
                finally
                {
                    EndBusy(table.TablePath);
                }

                if (left == null)
                {
                    stopped = true;
                    break;
                }

                if (record && left.Video && !item.NeedsRom && !_batchStop)
                {
                    if (!await RecordLobbyVideoCoreAsync(item, j => _batchJob = j))
                    {
                        stopped = true;
                        break;
                    }
                    if (LobbyVideo.Existing(table.Folder) != null)
                    {
                        left.Video = false;
                        recorded++;
                    }
                }

                filled += shown[n].Needs.Any && !shown[n].Needs.SameAs(left) ? 1 : 0;
                stillMissing += left.Any ? 1 : 0;
            }
        }
        finally
        {
            IsBatchRecording = false;
            _batchJob = null;
        }

        PlayStatus =
            $"Added VR lobby media to {filled} table{(filled == 1 ? "" : "s")}"
            + (recorded > 0 ? $" ({recorded} recorded in VPX)" : "")
            + "."
            + (
                stillMissing > 0
                    ? $" {stillMissing} still miss something the catalog doesn't have (the Downloads list says what)."
                    : ""
            )
            + (stopped ? " Stopped before the end." : "");
    }
}
