# Launching a table from the command line

VR Table Manager can play a single table without opening its window. The VR lobby starts every table this way, and you can use it too, from a shortcut, a script or another front end.

## Play one table

```powershell
VrTableManager.exe --play "C:\Tables\Medieval Madness.vpx" --vr
```

This plays the table the same way the **Play** button does, waits for it to close, then exits. The app's window never opens.

- `--play <table>` has to come first, with the table's path right after it.
- The table has to be one the app already lists in **My Tables**, and you need to use the full path it shows for it. Otherwise you get *Table not found in the manager's folders*.
- Add `--vr` to play in VR. Leave it out to play on the desktop.
- It counts as a play, just like the **Play** button, so it shows up in your play history.
- The exit code is `0` when the table ran and closed, and `1` when it couldn't start.

It works for every kind of table the app supports (VPX, Future Pinball, Pinball FX and M), as long as that game is set up in the app first.

## Options

| Option | What it does |
|---|---|
| `--vr` | Play in VR instead of on the desktop. |
| `--settings <file>` | Use a different `settings.json` instead of `%APPDATA%\VrTableManager\settings.json`. Play history is kept next to whichever one you use. |
| `--return <exe>` | Start this program when the table closes. |
| `--return-arg <arg>` | One argument for the `--return` program. Repeat it for each argument, in order. |
| `--status <file>` | Write the launch's progress (starting, running, exited, failed) to this JSON file. |
| `--lobby-pid <pid>` | The process id of a VR lobby waiting on this launch. If it's still open when the table closes, `--return` is skipped, so you don't end up with two lobbies. |

The lobby uses `--status` and `--lobby-pid`. You won't usually need them.

## Play a table, then land in the VR lobby

The lobby can't start a table from its own command line. Its `--select` option just picks that table in the ring. To go straight into a table and land in the lobby when you quit, have VR Table Manager play the table and start the lobby afterwards with `--return`. That's what the lobby itself does when you pick a table.

The lobby is `VrLobby\VrLobby.exe`, in the folder you unzipped VR Table Manager to. Anything after a lone `--` goes to the lobby itself:

```powershell
VrTableManager.exe --play "C:\Tables\Medieval Madness.vpx" --vr `
  --return "<VR Table Manager folder>\VrLobby\VrLobby.exe" `
  --return-arg -- --return-arg "--select=C:\Tables\Medieval Madness.vpx"
```

Put in your own folder, and use `VrLobby.exe`, not `VrLobby.console.exe`. The `--select` at the end brings the lobby back on the table you just played.

## VR lobby options

These go after `--` on the lobby's command line, for example `VrLobby.exe -- --select=<table>`.

| Option | What it does |
|---|---|
| `--select=<table>` | Start with this table picked (its full path, as My Tables shows it). Without it, the lobby opens on the table you played last. |
| `--settings=<file>` | Use a different VR Table Manager `settings.json`. |
| `--state=<file>` | Use a different file for the lobby's own state (filter, height). |
