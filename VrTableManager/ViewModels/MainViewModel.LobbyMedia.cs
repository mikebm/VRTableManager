using CommunityToolkit.Mvvm.Input;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Everything the VR lobby can show for a table, from the catalog: the playfield video, loading video, attract audio
// and wheel art (LobbyMedia). Every Get… command in My Tables → MORE goes through GetLobbyMediaCoreAsync, asking for
// one piece (plus what it can take for free from the same download) or, for Get all VR lobby media, every missing one.
// GET LOBBY MEDIA does the tables shown, one after another.
public sealed partial class MainViewModel
{
    /// <summary>Every catalog download this table's lobby media can come from (LobbyMedia.Next picks the order).</summary>
    List<(MediaPackAudio.Source Source, LobbyMedia.Kind Kind)> LobbyMediaSources(InstalledTable t)
    {
        var audio = AttractAudioSourcesFor(t);
        return
        [
            .. audio.Where(s => !s.IsDirect).Select(s => (s, LobbyMedia.Kind.Pack)),
            .. WheelArtSourcesFor(t)
                .Where(w => w.WheelPack)
                .Select(w => (w.Source, LobbyMedia.Kind.WheelFiles)),
            .. audio.Where(s => s.IsDirect).Select(s => (s, LobbyMedia.Kind.Sound)),
        ];
    }

    /// <param name="Left">What was wanted and is still missing.</param>
    /// <param name="Ran">A job ran (false: the catalog has nothing that could help).</param>
    /// <param name="HadPack">A media pack was downloaded.</param>
    sealed record LobbyMediaResult(
        LobbyMedia.Needs Left,
        IReadOnlyList<LobbyMedia.Problem> Problems,
        bool Ran,
        bool HadPack
    );

    /// <summary>
    /// Downloads what can fill <paramref name="wanted"/>, as one job, and keeps each piece (replacing one already
    /// there). <paramref name="bonus"/> is taken too when a download made anyway has it, but never causes a download.
    /// Returns what's still wanted afterwards, or null when the job was cancelled. The caller holds the table busy.
    /// </summary>
    /// <param name="hint">Added when something wanted wasn't found: what else you can do.</param>
    /// <param name="started">Gets the job as soon as it exists (the batch uses it to stop the running one).</param>
    async Task<LobbyMediaResult?> GetLobbyMediaCoreAsync(
        InstalledTable table,
        LobbyMedia.Needs wanted,
        LobbyMedia.Needs? bonus = null,
        string? hint = null,
        Action<JobItem>? started = null
    )
    {
        var left = LobbyMediaSources(table);
        if (LobbyMedia.Next(left, wanted) == null)
        {
            return new(wanted, [], false, false);
        }

        var what = wanted.Names();
        var title = what.Count == 1 ? char.ToUpper(what[0][0]) + what[0][1..] : "VR lobby media";
        var job = new JobItem($"{title} for {table.Manifest.GameName}")
        {
            FileNames = [table.Manifest.GameName],
        };
        Jobs.Insert(0, job);
        started?.Invoke(job);
        var take = wanted.Or(bonus ?? new());
        var got = new List<string>();
        var problems = new List<LobbyMedia.Problem>();
        var hadPack = false;
        try
        {
            while (LobbyMedia.Next(left, take.And(wanted)) is { } next)
            {
                left.Remove(next);
                var (src, kind) = next;
                job.Report($"Getting the {src.Label}…");
                try
                {
                    var file = await DownloadAttractSourceAsync(job, src);
                    hadPack |= kind == LobbyMedia.Kind.Pack;
                    await Task.Run(() =>
                        LobbyMedia.TakeFrom(
                            file,
                            kind,
                            src.Label,
                            table,
                            take,
                            got,
                            problems,
                            () => PlayfieldPicture(table),
                            job.Report,
                            job.Cts.Token
                        )
                    );
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    problems.Add(new(null, $"the {src.Label} failed ({e.Message})"));
                }
                finally
                {
                    CleanJobDownloads(job); // the pack's other files aren't kept
                }
            }

            var stillWanted = take.And(wanted);
            var missing = stillWanted.Names();
            var detail =
                problems.Count > 0
                    ? " (" + string.Join("; ", problems.Select(p => p.Text)) + ")"
                    : "";
            job.Finish(
                (got.Count > 0 ? "✓ Got the " + LobbyMedia.JoinAnd(got) + "." : "Got nothing.")
                    + (
                        missing.Count > 0
                            ? $" No {LobbyMedia.JoinOr(missing)} in the catalog's downloads{detail}."
                                + (hint != null ? " " + hint : "")
                        : got.Count > 0 ? " The VR lobby uses it from now on."
                        : ""
                    ),
                error: stillWanted.SameAs(wanted)
            );
            return new(stillWanted, problems, true, hadPack);
        }
        catch (OperationCanceledException)
        {
            job.Finish(
                "Cancelled" + (got.Count > 0 ? $"; kept the {LobbyMedia.JoinAnd(got)}." : ""),
                error: true
            );
            return null;
        }
        finally
        {
            CleanJobDownloads(job);
            ShowLobbyMedia(table); // also after a cancel: what was saved before it stays
        }
    }

    /// <summary>Updates the table's cards for the lobby media on disk.</summary>
    void ShowLobbyMedia(InstalledTable t)
    {
        SetHasLobbyVideo(t.TablePath, t.HasOwnFolder && LobbyVideo.Existing(t.Folder) != null);
        SetHasLoadingVideo(t.TablePath, LoadingVideo.Own(t) != null);
        SetHasAttractAudio(t.TablePath, t.IsVpx && MediaPackAudio.Existing(t.Folder) != null);
        SetHasWheelArt(t.TablePath, t.HasOwnFolder && WheelArt.Saved(t.Folder) != null);
    }

    /// <summary>The playfield video wasn't in the catalog's downloads: offers to record the attract mode in VPX.</summary>
    async Task OfferRecordingAsync(InstalledItem item, string why)
    {
        if (item.NeedsRom)
        {
            PlayStatus =
                why
                + $" VPX could record {item.Title}'s attract mode instead, once the table has its ROM.";
            return;
        }

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

    // ---- One table (MORE → Get all VR lobby media) ----

    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task GetLobbyMedia(InstalledItem item)
    {
        var table = item.Table;
        if (!item.CanGetLobbyMedia)
        {
            return;
        }

        var needs = LobbyMedia.Needs.Of(table);
        if (!needs.Any)
        {
            PlayStatus =
                $"{item.Title} already has all its VR lobby media. MORE has Replace items to swap one.";
            return;
        }

        if (!TryBeginBusy(table.TablePath))
        {
            return;
        }

        LobbyMediaResult? r;
        try
        {
            r = await GetLobbyMediaCoreAsync(table, needs);
        }
        finally
        {
            EndBusy(table.TablePath);
        }

        if (r == null)
        {
            return; // cancelled
        }

        if (r.Left.Video && item.CanHaveLobbyVideo)
        {
            await OfferRecordingAsync(item, LobbyMedia.WhyNoVideo(r.Problems, r.HadPack));
        }
        else if (!r.Ran)
        {
            PlayStatus =
                $"The catalog has no media pack, wheel art or sound file for {item.Title}. "
                + "MORE → My artwork and the Use my own… items take files you have.";
        }
    }

    // ---- The tables shown (My Tables → GET LOBBY MEDIA) ----

    /// <summary>
    /// Fills the missing lobby media of every table shown in My Tables (so a search or filter picks which), one
    /// table after another: from the catalog first, then, if you choose, VPX recordings for those still without a
    /// playfield video. Cancelling one table's job skips that table; STOP ends the run.
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
        var fromCatalog = shown.Count(x =>
            LobbyMedia.Next(LobbyMediaSources(x.Item.Table), x.Needs) != null
        );
        var recordable = shown.Count(x => x.Needs.Video && !x.Item.NeedsRom);
        var toRecord = shown
            .Where(x => x.Needs.Video && x.Item.CanHaveLobbyVideo && !x.Item.NeedsRom)
            .Select(x => x.Item)
            .ToList();
        if (fromCatalog == 0 && recordable == 0)
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
                fromCatalog > 0
                    ? $"• {fromCatalog} can get some from the catalog's media packs, wheel art and sound files. Each download "
                        + "page opens in your browser, one after another: click Download there (some sites make you wait on a "
                        + "timer). The app keeps what it needs and deletes the rest of each download.\n\n"
                    : ""
            )
            + (
                recordable > 0
                    ? $"• Tables still without a playfield video afterwards (up to {recordable}) can be recorded in VPX: a small "
                        + "window for about half a minute each, sound off. Leave the PC alone meanwhile.\n\n"
                    : ""
            )
            + (
                recordable > 0 && fromCatalog > 0 && toRecord.Count > 0
                    ? $"• RECORD VIDEOS ONLY skips the catalog and just records a playfield video in VPX for the {toRecord.Count} "
                        + $"table{(toRecord.Count == 1 ? "" : "s")} without one.\n\n"
                    : ""
            )
            + "Cancelling one table's job in the Downloads list skips that table; STOP ends the run. "
            + "To do only some tables, search or filter My Tables first.";
        string[] buttons =
            recordable > 0 && fromCatalog > 0
                ? toRecord.Count > 0
                    ?
                    [
                        "GET ALL, RECORD THE REST",
                        "GET ALL, DON'T RECORD",
                        "RECORD VIDEOS ONLY",
                        "CANCEL",
                    ]
                    : ["GET ALL, RECORD THE REST", "GET ALL, DON'T RECORD", "CANCEL"]
                : recordable > 0
                    ? ["RECORD IN VPX", "CANCEL"]
                    : ["GET ALL", "CANCEL"];
        var pick = await _ui.ChoiceAsync("Get the VR lobby media?", message, buttons);
        if (pick < 0 || pick == buttons.Length - 1)
        {
            return;
        }

        if (buttons[pick] == "RECORD VIDEOS ONLY")
        {
            if (IsLobbyRunning)
            {
                PlayStatus = "Close the VR lobby first: recording runs the tables in VPX.";
                return;
            }
            await RecordLobbyVideosAsync(toRecord);
            return;
        }

        var record = recordable > 0 && pick == 0;
        if (record && IsLobbyRunning)
        {
            PlayStatus = "Close the VR lobby first: recording runs the tables in VPX.";
            return;
        }

        IsBatchRecording = true;
        _batchUsesVpx = record;
        _batchStop = false;
        int filled = 0,
            recorded = 0,
            stillMissing = 0,
            needRom = 0,
            notRecorded = 0,
            skipped = 0;
        var stopped = false;
        try
        {
            for (var n = 0; n < shown.Count; n++)
            {
                if (_batchStop)
                {
                    stopped = true;
                    break;
                }

                var (item, before) = shown[n];
                var table = item.Table;
                BatchRecordingText = $"■  STOP ({n + 1}/{shown.Count})";
                if (!TryBeginBusy(table.TablePath))
                {
                    skipped++; // something else is running for this table
                    continue;
                }

                LobbyMediaResult? r;
                try
                {
                    r = await GetLobbyMediaCoreAsync(table, before, started: j => _batchJob = j);
                }
                finally
                {
                    EndBusy(table.TablePath);
                }

                if (r == null)
                {
                    if (_batchStop)
                    {
                        stopped = true;
                        break;
                    }
                    skipped++; // you cancelled this table's job
                    continue;
                }

                var left = r.Left;
                if (left.Video)
                {
                    if (!record)
                    {
                        notRecorded++;
                    }
                    else if (item.NeedsRom)
                    {
                        needRom++;
                    }
                    else if (!await RecordLobbyVideoCoreAsync(item, j => _batchJob = j))
                    {
                        if (_batchStop)
                        {
                            stopped = true;
                            break;
                        }
                        skipped++; // you cancelled this table's recording
                        continue;
                    }
                    else if (LobbyVideo.Existing(table.Folder) != null)
                    {
                        left.Video = false;
                        recorded++;
                    }
                }

                filled += left.SameAs(before) ? 0 : 1;
                stillMissing += left.Any ? 1 : 0;
            }
        }
        finally
        {
            IsBatchRecording = false;
            _batchUsesVpx = false;
            _batchJob = null;
        }

        PlayStatus = LobbyMedia.BatchSummary(
            filled,
            recorded,
            stillMissing,
            needRom,
            notRecorded,
            skipped,
            stopped
        );
    }
}
