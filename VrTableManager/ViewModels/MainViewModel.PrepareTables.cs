using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Setup → VR lobby → PREPARE TABLES: the lobby, run without a window, reads every table's file once now (its
// cabinet, playfield picture and depth map) instead of the first time the table comes round the ring.
public sealed partial class MainViewModel
{
    /// <summary>The lobby is reading every table in the background (PREPARE TABLES).</summary>
    [ObservableProperty]
    public partial bool IsPreparingLobbyTables { get; set; }

    [RelayCommand]
    async Task PrepareLobbyTablesAsync()
    {
        if (IsPreparingLobbyTables)
        {
            return;
        }

        if (IsLobbyRunning)
        {
            PlayStatus = "Close the VR lobby first, then prepare its tables.";
            return;
        }

        if (IsBatchRecording && _batchUsesVpx)
        {
            // VPX has each table open in turn, and a cabinet read that fails then is kept as "no cabinet".
            PlayStatus =
                "Tables are being recorded in VPX: wait for them, or press STOP, then prepare the tables.";
            return;
        }

        var lobbyDir = LobbyRunner.FindLobbyDir();
        if (lobbyDir == null)
        {
            PlayStatus = "Couldn't find the VrLobby project next to this app.";
            return;
        }
        // As for LAUNCH VR LOBBY: on this app's own .NET, else the installed one (off the UI thread: mklink, copies).
        var dotnetRoot = await Task.Run(() =>
            LobbyDotnet.Prepare(
                AppContext.BaseDirectory,
                LobbyDotnet.DefaultRoot,
                Environment.Version.ToString(),
                out _
            )
        );

        IsPreparingLobbyTables = true;
        var job = new JobItem("Preparing tables for the VR lobby");
        Jobs.Insert(0, job);
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
            var godot = await LobbyRunner.EnsureGodotAsync(
                job.ProgressReporter,
                job.Report,
                job.Cts.Token
            );
            if (LobbyRunner.NeedsBuild(lobbyDir))
            {
                await LobbyRunner.BuildAsync(lobbyDir, job.Report, job.Cts.Token);
            }

            await LobbyRunner.ImportIfNeededAsync(
                godot,
                lobbyDir,
                dotnetRoot,
                job.Report,
                job.Cts.Token
            );
            job.Report("Looking for tables the lobby hasn't read yet…");
            var code = await LobbyRunner.PrepareTablesAsync(
                godot,
                lobbyDir,
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
    }

    /// <summary>Fewer tables not yet prepared than this aren't worth a question: they're read as they come round.</summary>
    const int AskToPrepareFrom = 20;

    /// <summary>
    /// On LAUNCH VR LOBBY, while many tables haven't been read for the lobby (no cabinet file yet): offers PREPARE
    /// TABLES first, until ticked off. Shown says whether it asked (then the lobby video tip waits for another
    /// launch: one question at a time); Launch is false when the launch stops (preparing instead, or closed).
    /// </summary>
    async Task<(bool Shown, bool Launch)> LobbyPrepareTipAsync()
    {
        if (!Settings.AskToPrepareLobbyTables)
        {
            return (false, true);
        }

        var tables = _allInstalled.Select(i => i.Table).ToList();
        var unprepared = await Task.Run(() => Lobby.CabinetFiles.Unprepared(tables));
        if (unprepared < AskToPrepareFrom)
        {
            return (false, true);
        }

        string[] buttons = ["PREPARE TABLES FIRST", "LAUNCH VR LOBBY", "LAUNCH, DON'T ASK AGAIN"];
        var pick = await _ui.ChoiceAsync(
            "Prepare your tables for the VR lobby?",
            $"{unprepared} of your tables haven't been prepared for the VR lobby yet. The first time each one comes "
                + "round the ring, the lobby reads its table file for its cabinet and playfield: a few seconds a table, "
                + "longer on a hard drive, and it waits behind the tables before it.\n\n"
                + "• PREPARE TABLES FIRST reads them all now, in the background: follow it under Downloads, then launch "
                + "the lobby when it's done. A big library on a hard drive can take an hour or more, once.\n\n"
                + "• Later, Setup → VR lobby → PREPARE TABLES does the same, and only reads tables added or updated "
                + "since.",
            buttons
        );
        if (pick == 0)
        {
            _ = PrepareLobbyTablesCommand.ExecuteAsync(null); // an hour, maybe: not holding LAUNCH VR LOBBY meanwhile
            return (true, false);
        }

        if (pick == 2)
        {
            Settings.AskToPrepareLobbyTables = false;
            Settings.Save();
        }
        return (true, pick is 1 or 2);
    }

    /// <summary>After GET LOBBY MEDIA: " …PREPARE TABLES…" while many tables haven't been prepared, else "".</summary>
    async Task<string> PrepareHintAsync()
    {
        var tables = _allInstalled.Select(i => i.Table).ToList();
        var unprepared = await Task.Run(() => Lobby.CabinetFiles.Unprepared(tables));
        return unprepared < AskToPrepareFrom
            ? ""
            : $" {unprepared} tables haven't been prepared for the VR lobby yet: Setup → VR lobby → PREPARE TABLES reads them now, so they all come round the ring at once.";
    }

    /// <summary>
    /// Recording runs the tables in VPX: not while the VR lobby runs, nor while PREPARE TABLES is reading them (a
    /// table it can't read while VPX has it is kept as having no cabinet). Says why; true when it can't.
    /// </summary>
    bool CantRecordNow()
    {
        if (IsLobbyRunning)
        {
            PlayStatus = "Close the VR lobby first: recording runs the tables in VPX.";
            return true;
        }

        if (IsPreparingLobbyTables)
        {
            PlayStatus =
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
