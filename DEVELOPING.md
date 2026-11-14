# Developing VR Table Manager

This is for working on the code. If you just want to play, the [README](README.md) and the guides in [docs](docs) are what you need.

## What you need

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Godot 4.7.2 .NET](https://godotengine.org/download) for the VR lobby. It has to be the .NET edition, since the lobby is C#.
- A headset with an OpenXR runtime, and the pinball games themselves, if you want to play. You don't need any of that to build, run the tests, or open the lobby on the desktop.

## Build and run

```powershell
dotnet build "VR Table Manager.slnx"
```

```powershell
dotnet run --project VrTableManager
```

```powershell
godot --path VrLobby
```

Open the manager once before the lobby. It saves where its exe is, and the lobby launches tables through it. Without a headset the lobby opens in a window with keyboard controls.

## Tests

```powershell
dotnet run --project SmokeTest
```

```powershell
godot --headless --path VrLobby -- --selftest
```

SmokeTest hits the online catalog and a few release and site endpoints, so some checks need a network connection. Checks that need a particular table installed are skipped when it isn't.

## Projects

| Project | What it is |
| --- | --- |
| [VrTableManager](VrTableManager/README.md) | The Avalonia desktop app: browsing, installing, setup and launching. |
| [VrTableManager.Core](VrTableManager.Core/VrTableManager.Core.csproj) | Everything that isn't UI: models, installs, engines, play tracking and the lobby's logic. The lobby uses it too. |
| [VrLobby](VrLobby/README.md) | The Godot C# VR lobby. |
| [SmokeTest](SmokeTest/SmokeTest.csproj) | Console checks for both apps. |
| [tools/IconGen](tools/IconGen/IconGen.csproj) | Makes the app icon. |

[VrLobby/CABINETS.md](VrLobby/CABINETS.md) explains how the lobby reads a table's cabinet, picks its art and builds the playfield's depth.

## VR lobby options for development

These go after `--` on the lobby's command line, for example `godot --path VrLobby -- --borrow-all`. Use `VrLobby.console.exe` (or Godot's console exe) for the ones that print, so the output shows in the terminal.

| Option | What it does |
|---|---|
| `--selftest` | Run the in-engine self-test and quit. |
| `--screenshot=<png>` | Desktop only (`--xr-mode off`): save one frame and quit. `--pitch`, `--fov` and `--cam-y` move the camera; `--shot-wait=<seconds>` waits longer than the default 6 for pictures still downloading. |
| `--perf-trace[=steps]` | Step through the tables on its own and print every slow frame. |
| `--borrow-all` | Show every table on the default cabinet. |
| `--no-depth` | Show the playfield video flat, to compare. |
| `--dump-cabinet=<table.vpx>` | Save that table's VR cabinet as the default cabinet, then quit. |
| `--prepare-tables` | Read every table's cabinet and pictures into the cache, then quit. Setup's PREPARE TABLES runs this. |
| `--record-audio=<table>` | Record a ROM table's attract music, then quit. The lobby runs this itself. |

From source, the manager starts the lobby in the Godot editor it downloads to `%LOCALAPPDATA%\VrTableManager\godot\`. To return to it with `--play … --return`, point `--return` at that `Godot_v…_mono_win64.exe` and add `--return-arg --path --return-arg "<repo>\VrLobby"` before the `--`.

## Releasing

[`scripts/release.ps1`](scripts/release.ps1) builds a Windows release and publishes it on GitHub:

```powershell
.\scripts\release.ps1 -Version 1.0.0 -NoPublish   # build and zip only, into artifacts\release
.\scripts\release.ps1 -Version 1.0.0              # also tag v1.0.0 and create the GitHub release
```

It runs SmokeTest, publishes the manager self-contained for win-x64, exports the VR lobby with the `Windows` preset (so players don't need .NET or Godot), and attaches `VrTableManager-<version>-win-x64.zip` and its `.sha256` to the release with GitHub's generated change list. The export needs Godot's .NET export templates installed. The working tree has to be clean and pushed. Other options: `-Draft`, `-Prerelease`, `-Notes "…"` or `-NotesFile notes.md`, `-SkipTests`, `-Repo owner/name`.

For GitHub it uses `GITHUB_TOKEN` or `GH_TOKEN` if set, then the GitHub CLI's sign-in, then the credential Git already has for github.com. The token needs `contents: write` on the repo.
