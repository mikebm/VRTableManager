# Launching a table from the command line

VR Table Manager can play a single table without opening its window. The VR lobby uses this to start every table, and you can run it yourself from a shortcut, a script or another front end.

## Play one table

```powershell
VrTableManager.exe --play "C:\Tables\Medieval Madness.vpx" --vr
```

This launches the table through the same engine as the manager's **Play** button and waits for it to close. Then it exits. The manager window never opens.

- `--play <table>` must come first, with the table's path straight after it.
- The table must be one the manager already lists under **My Tables**. It's looked up by its full path, so use the path the manager shows for that table. A table it doesn't know fails with *Table not found in the manager's folders*.
- `--vr` plays in VR. Leave it out to play on the desktop.
- It counts as a play, just like the **Play** button: the play and the time played go into your play history.
- The exit code is `0` when the table ran and closed, and `1` when it couldn't start.

Tables for every engine the manager supports (VPX, Future Pinball, Pinball FX / M) work this way. Each engine has to be set up in the manager first.

## Options

| Option | What it does |
|---|---|
| `--vr` | Play in VR instead of on the desktop. |
| `--settings <file>` | Use a different `settings.json`. The default is `%APPDATA%\VrTableManager\settings.json`. Play history is kept next to whichever settings file is used. |
| `--return <exe>` | Start this program after the table closes. |
| `--return-arg <arg>` | One argument for the `--return` program. Repeat it for each argument, in order. |
| `--status <file>` | Write the launch's progress (starting, running, exited, failed) to this JSON file. The lobby reads it to drive its loading screen. |
| `--lobby-pid <pid>` | The process id of a VR lobby that's waiting on this launch. If that lobby is still open when the table closes, `--return` is skipped, so you don't end up with two lobbies. |

`--status` and `--lobby-pid` exist for the lobby. You won't usually need them yourself.

## Play a table, then go to the VR lobby

The lobby can't launch a table itself from the command line. Its `--select=<table>` option only highlights that table in the ring. To start straight into a table and land in the lobby when you leave it, have the manager play the table and start the lobby afterwards with `--return`. This is what the lobby does when you pick a table.

The lobby runs on the Godot copy the manager downloads, in `%LOCALAPPDATA%\VrTableManager\godot\`, using the `VrLobby` folder next to `VrTableManager.exe`. Godot passes anything after a lone `--` to the lobby:

```powershell
VrTableManager.exe --play "C:\Tables\Medieval Madness.vpx" --vr `
  --return "$env:LOCALAPPDATA\VrTableManager\godot\<folder>\Godot_v<version>_mono_win64.exe" `
  --return-arg --path --return-arg "<VR Table Manager folder>\VrLobby" `
  --return-arg -- --return-arg "--select=C:\Tables\Medieval Madness.vpx"
```

Replace the Godot path (use the `Godot_v…_mono_win64.exe`, not the `_console` one) and the `VrLobby` folder with the ones on your PC. The `--select` at the end should bring the lobby back on the table you just played.

## VR lobby options

These go after `--` on the Godot command line, for example `godot --path VrLobby -- --select=<table>`. Most are for development and testing.

| Option | What it does |
|---|---|
| `--select=<table>` | Start with this table selected (its full path, as the manager lists it). |
| `--settings=<file>` | Use a different manager `settings.json`. Also `VRLOBBY_SETTINGS`. |
| `--state=<file>` | Use a different lobby state file. Also `VRLOBBY_STATE`. |
| `--selftest` | Run the in-engine self-test and quit. |
| `--screenshot=<png>` | Desktop only (`--xr-mode off`): save one frame and quit. `--pitch`, `--fov` and `--cam-y` move the camera. |
| `--perf-trace[=steps]` | Step through the tables by itself and print slow frames. |
| `--borrow-all` | Show every table in the default cabinet. |
| `--no-depth` | Show a flat playfield video, for comparison. |
| `--dump-cabinet=<table.vpx>` | Write that table's VR cabinet as the default cabinet, then quit. |
| `--record-audio=<table>` | Record a ROM table's attract music, then quit. The lobby runs this itself. |
