# VR Table Manager

Browse, install, organize, and launch virtual pinball tables—with VR support. The Windows desktop manager handles VPX and Future Pinball tables, while a companion in-headset lobby lets you browse and launch your library in VR. Pinball FX and Pinball M can also be launched through UEVR.

The manager and lobby share the same .NET core library for table discovery, engine setup, play history, and launching.

## Get started

### Requirements

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- For the VR lobby: [Godot 4.7.2 .NET](https://godotengine.org/download) (the .NET edition is required for C#)
- For VR play: a configured OpenXR runtime and the relevant pinball games/tools. See the [manager guide](VrTableManager/README.md) and [lobby guide](VrLobby/README.md) for engine-specific requirements.

### Build and run

From the repository root, build the solution:

```powershell
dotnet build "VR Table Manager.slnx"
```

Run the desktop manager:

```powershell
dotnet run --project VrTableManager
```

Run the companion lobby with the Godot .NET executable:

```powershell
godot --path VrLobby
```

Open the manager once before using the lobby so it can record its executable path. VR hardware is not required to run the manager, build the projects, or open the lobby in desktop mode.

### Run checks

Run the console smoke-test suite:

```powershell
dotnet run --project SmokeTest
```

Run the lobby's Godot scene self-test:

```powershell
godot --headless --path VrLobby -- --selftest
```

The smoke tests may access online catalogs or release/site endpoints, so some checks depend on network availability.

## Projects

| Project | Description |
| --- | --- |
| [VrTableManager](VrTableManager/README.md) | Avalonia desktop app for browsing and installing tables, configuring engines, and launching games. |
| [VrTableManager.Core](VrTableManager.Core/VrTableManager.Core.csproj) | Shared models, table services, engines, play tracking, and lobby logic. |
| [VrLobby](VrLobby/README.md) | Godot C# VR lobby with table browsing, favorites, filters, and launch/return flow. |
| [SmokeTest](SmokeTest/SmokeTest.csproj) | Console-based integration and behavior checks. |
| [tools/IconGen](tools/IconGen/IconGen.csproj) | Utility for generating the desktop application icon. |

## Guides

- [Desktop manager: features, engine setup, installation, and updates](VrTableManager/README.md)
- [VR lobby: setup, controls, behavior, and test coverage](VrLobby/README.md)

## Releasing

[`scripts/release.ps1`](scripts/release.ps1) builds a Windows release and publishes it on GitHub:

```powershell
.\scripts\release.ps1 -Version 1.0.0 -NoPublish   # build and zip only, into artifacts\release
.\scripts\release.ps1 -Version 1.0.0              # also tag v1.0.0 and create the GitHub release
```

It runs the smoke test, publishes the manager self-contained for win-x64, bundles the VR lobby
(already compiled, so players don't need the .NET SDK) and attaches
`VrTableManager-<version>-win-x64.zip` and its `.sha256` to the release, with GitHub's generated
change list. The working tree must be clean and pushed. Other options: `-Draft`, `-Prerelease`,
`-Notes "…"` or `-NotesFile notes.md`, `-SkipTests`, `-Repo owner/name`.

For GitHub access it uses `GITHUB_TOKEN` or `GH_TOKEN` if set, then the GitHub CLI's sign-in, then
the credential Git already uses for github.com. The token needs `contents: write` on the repository.

## Credits

- Table data from the [Virtual Pinball Spreadsheet](https://virtualpinballspreadsheet.github.io/).
- High scores are read with maps from the [Pinball Memory Maps](https://github.com/tomlogic/pinball-memory-maps) project. This program makes use of content from the Pinball Memory Maps project.
