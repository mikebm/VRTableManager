using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Setup → VR lobby → PREPARE TABLES: the lobby, run without a window, reads every table's file once now (its
// cabinet, playfield picture and depth map) instead of the first time the table comes round the ring.
public sealed partial class LobbyViewModel
{
    /// <summary>The lobby is reading every table in the background (PREPARE TABLES).</summary>
    [ObservableProperty]
    public partial bool IsPreparingLobbyTables { get; set; }

    /// <summary>Setup → PREPARE TABLES: asks first whether to record missing attract music afterwards.</summary>
    [RelayCommand]
    Task PrepareLobbyTablesAsync() => PrepareLobbyTablesCoreAsync(music: null);

    /// <summary>
    /// Reads every table for the lobby; then, as a second job, records the ROM tables' missing attract music when
    /// <paramref name="music"/> says so (null: asks, with how long it would take, when there is any to record).
    /// Recording is real time, so it's never slipped silently into PREPARE TABLES.
    /// </summary>
    internal async Task PrepareLobbyTablesCoreAsync(bool? music)
    {
        if (IsPreparingLobbyTables)
        {
            return;
        }

        if (IsLobbyRunning)
        {
            App.Library.PlayStatus = "Close the VR lobby first, then prepare its tables.";
            return;
        }

        if (App.Media.IsBatchRecording && App.Media._batchUsesVpx)
        {
            // VPX has each table open in turn, and a cabinet read that fails then is kept as "no cabinet".
            App.Library.PlayStatus =
                "Tables are being recorded in VPX: wait for them, or press STOP, then prepare the tables.";
            return;
        }

        if (!LobbyRunner.Available)
        {
            App.Library.PlayStatus = "Couldn't find the VrLobby folder next to this app.";
            return;
        }
        // As for LAUNCH VR LOBBY: an exported lobby brings its own .NET; a project runs on this app's own, else the
        // installed one (off the UI thread: mklink, copies).
        var dotnetRoot =
            LobbyRunner.FindExported() != null
                ? null
                : await Task.Run(() =>
                    LobbyDotnet.Prepare(
                        AppContext.BaseDirectory,
                        LobbyDotnet.DefaultRoot,
                        Environment.Version.ToString(),
                        out _
                    )
                );

        var allTables = App.Library._allInstalled.Select(i => i.Table).ToList();
        var noMusic =
            music != false && CantGenerateMusicWhy() == null
                ? (await AttractMusicTodoAsync(allTables)).Count
                : 0;
        if (music == null && noMusic > 0)
        {
            var pick = await Ui.ChoiceAsync(
                "Record attract music too?",
                AttractMusicSummary(noMusic)
                    + "\n\nIt runs after the tables are prepared, as a job of its own under Downloads, and you can "
                    + "cancel it there. Setup → VR lobby → Music → GENERATE ATTRACT MUSIC does it on its own any time.",
                ["PREPARE AND RECORD MUSIC", "PREPARE ONLY"]
            );
            if (pick < 0)
            {
                return;
            }
            music = pick == 0;
        }

        IsPreparingLobbyTables = true;
        var job = new JobItem("Preparing tables for the VR lobby");
        App.Downloads.Jobs.Insert(0, job);
        var progress = job.ProgressReporter;
        var started = DateTime.UtcNow;
        int total = 0,
            all = 0,
            prepared = 0,
            failed = 0;
        var finished = false;
        string? firstProblem = null;
        try
        {
            var program = await LobbyRunner.PrepareAsync(
                dotnetRoot,
                job.ProgressReporter,
                job.Report,
                job.Cts.Token
            );
            job.Report("Looking for tables the lobby hasn't read yet…");
            var code = await LobbyRunner.PrepareTablesAsync(
                program,
                dotnetRoot,
                line =>
                {
                    var parts = line.Split(' ', 4);
                    switch (parts[0])
                    {
                        case "PREPARE-TOTAL" when parts.Length >= 3:
                            int.TryParse(parts[1], out total);
                            int.TryParse(parts[2], out all);
                            break;
                        case "PREPARE"
                            when parts.Length >= 4
                                && int.TryParse(parts[1], out var i)
                                && total > 0:
                            progress.Report((i - 1) / (double)total);
                            job.Report(
                                $"{i} of {total}{TimeLeft(started, i - 1, total)}: {parts[3]}"
                            );
                            break;
                        case "PREPARE-ERROR":
                            failed++;
                            firstProblem ??= line["PREPARE-ERROR ".Length..];
                            break;
                        case "PREPARE-DONE" when parts.Length >= 3:
                            int.TryParse(parts[1], out prepared);
                            finished = true;
                            break;
                    }
                },
                job.Cts.Token
            );

            if (!finished)
            {
                job.Finish(
                    $"The lobby stopped before it finished (exit code {code}). What it prepared is kept; PREPARE TABLES again carries on.",
                    error: true
                );
            }
            else if (total == 0)
            {
                job.Finish($"All {all} tables were ready already.");
            }
            else
            {
                job.Finish(
                    $"Prepared {prepared} of {all} tables in {Took(DateTime.UtcNow - started)}"
                        + (total < all ? "; the rest were ready already." : ".")
                        + (failed > 0 ? $" {failed} couldn't be read, e.g. {firstProblem}" : ""),
                    error: failed > 0 && prepared == 0
                );
            }
        }
        catch (OperationCanceledException)
        {
            job.Finish(
                "Cancelled. What was prepared is kept; PREPARE TABLES again carries on.",
                error: true
            );
        }
        catch (Exception e)
        {
            job.Finish("Couldn't prepare the tables: " + e.Message, error: true);
        }
        finally
        {
            IsPreparingLobbyTables = false;
        }

        // The second step you chose: the music, after the tables (also when a few couldn't be read).
        if (finished && music == true)
        {
            await GenerateAttractMusicAsync(allTables);
        }
    }

    /// <summary>Fewer tables not yet prepared (or without music) than this aren't worth a question: they're read as they come round.</summary>
    const int AskToPrepareFrom = 20;

    /// <summary>
    /// On LAUNCH VR LOBBY, while many tables haven't been read for the lobby (no cabinet file yet), or many ROM tables
    /// have no attract music yet: offers PREPARE TABLES and/or recording the music first, until ticked off. Shown says
    /// whether it asked (then the lobby video tip waits for another launch: one question at a time); Launch is false
    /// when the launch stops (preparing instead, or closed).
    /// </summary>
    async Task<(bool Shown, bool Launch)> LobbyPrepareTipAsync()
    {
        if (!Settings.AskToPrepareLobbyTables)
        {
            return (false, true);
        }

        var tables = App.Library._allInstalled.Select(i => i.Table).ToList();
        var unprepared = await Task.Run(() => Lobby.CabinetFiles.Unprepared(tables));
        var noMusic =
            CantGenerateMusicWhy() == null ? (await AttractMusicTodoAsync(tables)).Count : 0;
        var prepare = unprepared >= AskToPrepareFrom;
        if (!prepare && noMusic < AskToPrepareFrom)
        {
            return (false, true);
        }

        const string PrepareFirst = "PREPARE TABLES FIRST",
            PrepareAndMusic = "PREPARE AND RECORD MUSIC",
            MusicFirst = "RECORD MUSIC FIRST",
            Launch = "LAUNCH VR LOBBY",
            LaunchDontAsk = "LAUNCH, DON'T ASK AGAIN";
        var music =
            noMusic > 0
                ? $"{AttractMusicSummary(noMusic)} Without it, the lobby records a table's music only when you stop on it, "
                    + "and it takes about half a minute to arrive.\n\n"
                : "";
        string title;
        string text;
        string[] buttons;
        if (prepare)
        {
            title = "Prepare your tables for the VR lobby?";
            text =
                $"{unprepared} of your tables haven't been prepared for the VR lobby yet. The first time each one comes "
                + "round the ring, the lobby reads its table file for its cabinet and playfield: a few seconds a table, "
                + "longer on a hard drive, and it waits behind the tables before it.\n\n"
                + music
                + "• PREPARE TABLES FIRST reads them all now, in the background: follow it under Downloads, then launch "
                + "the lobby when it's done. A big library on a hard drive can take an hour or more, once.\n\n"
                + (
                    noMusic > 0
                        ? "• PREPARE AND RECORD MUSIC records the missing music once the tables are read.\n\n"
                        : ""
                )
                + "• Later, Setup → VR lobby → PREPARE TABLES does the same, and only reads tables added or updated "
                + "since.";
            buttons =
                noMusic > 0
                    ? [PrepareFirst, PrepareAndMusic, Launch, LaunchDontAsk]
                    : [PrepareFirst, Launch, LaunchDontAsk];
        }
        else
        {
            title = "Record your tables' attract music?";
            text =
                music
                + "• RECORD MUSIC FIRST records it now: follow it under Downloads, then launch the lobby when it's done.\n\n"
                + "• Later, Setup → VR lobby → Music → GENERATE ATTRACT MUSIC does the same.";
            buttons = [MusicFirst, Launch, LaunchDontAsk];
        }

        var pick = await Ui.ChoiceAsync(title, text, buttons);
        var picked = pick >= 0 ? buttons[pick] : null;
        switch (picked)
        {
            case PrepareFirst or PrepareAndMusic:
                // An hour, maybe: not holding LAUNCH VR LOBBY meanwhile.
                _ = PrepareLobbyTablesCoreAsync(music: picked == PrepareAndMusic);
                return (true, false);
            case MusicFirst:
                _ = GenerateAttractMusicAsync(tables);
                return (true, false);
            case LaunchDontAsk:
                Settings.AskToPrepareLobbyTables = false;
                Settings.Save();
                break;
        }
        return (true, picked is Launch or LaunchDontAsk);
    }

    /// <summary>After GET LOBBY MEDIA: " …PREPARE TABLES…" while many tables haven't been prepared, else "".</summary>
    internal async Task<string> PrepareHintAsync()
    {
        var tables = App.Library._allInstalled.Select(i => i.Table).ToList();
        var unprepared = await Task.Run(() => Lobby.CabinetFiles.Unprepared(tables));
        return unprepared < AskToPrepareFrom
            ? ""
            : $" {unprepared} tables haven't been prepared for the VR lobby yet: Setup → VR lobby → PREPARE TABLES reads them now, so they all come round the ring at once.";
    }

    /// <summary>
    /// Recording runs the tables in VPX: not while the VR lobby runs, nor while PREPARE TABLES is reading them (a
    /// table it can't read while VPX has it is kept as having no cabinet). Says why; true when it can't.
    /// </summary>
    internal bool CantRecordNow()
    {
        if (IsLobbyRunning)
        {
            App.Library.PlayStatus = "Close the VR lobby first: recording runs the tables in VPX.";
            return true;
        }

        if (IsPreparingLobbyTables)
        {
            App.Library.PlayStatus =
                "Tables are being prepared for the VR lobby: wait for it, or cancel it under Downloads, then record.";
            return true;
        }
        return false;
    }

    /// <summary>" · about 1 h 10 min left", from the pace so far; "" until a few tables give a fair pace.</summary>
    static string TimeLeft(DateTime started, int done, int total)
    {
        if (done < 3)
        {
            return "";
        }

        var left = TimeSpan.FromTicks((DateTime.UtcNow - started).Ticks / done * (total - done));
        return $" · about {Took(left)} left";
    }

    static string Took(TimeSpan t) =>
        t.TotalMinutes < 1 ? $"{Math.Max(1, (int)t.TotalSeconds)} s"
        : t.TotalHours < 1 ? $"{(int)Math.Ceiling(t.TotalMinutes)} min"
        : $"{(int)t.TotalHours} h {t.Minutes} min";
}
