# VR Table Manager

Browse the Virtual Pinball Spreadsheet, install tables with one click, and play them in VR (Quest 3 via Link / Air Link / Virtual Desktop).

## Run

```
dotnet run --project VrTableManager
```

## How it works

- **Get Tables** – catalog from the Virtual Pinball Spreadsheet (`vpsdb.json`), filterable by VPX / Future Pinball and "VR-ready" (tables tagged `VR` or `Hybrid`).
- **Install** – direct links (GitHub etc.) download straight away. Login sites (VPUniverse, VPForums, Pinball Nirvana) open in an Edge/Chrome window driven by Playwright: if you're signed out it waits for you to sign in (it never navigates while a login page or password box is showing), then clicks through the download and catches the file. It follows each download link once and gives the site 20 s to respond before trying anything else, and after 4 tries it hands over to you. The window doesn't announce itself as automated, so sites' bot checks are less likely to loop. The browser profile is kept in `%APPDATA%\VrTableManager\browser-profile`, so you sign in once.
- **Unpack** – each VPX table gets its own folder (`Name (Manufacturer Year)`) laid out the way VPX 10.8.1 expects: `pinmame/roms`, `music`, `pinmame/altsound`, backglass next to the `.vpx`. A `vrtm.json` manifest records which VPS entry it came from.
- **ROMs** – a table loads exactly one ROM, named in its script (`Const cGameName = "mm_109c"`); the installer reads it from the `.vpx` (falling back to VPS's ROM list). After a table installs, its ROM is fetched from the catalog's ROM link (usually VPForums, through the same sign-in browser) and saved as `pinmame/roms/<rom>.zip`, whether the download is the ROM zip itself or a pack with several ROMs inside. My Tables has **Get ROM** for this, and **Add ROM** for a file you already have. When the ROM page offers several files (VPUniverse's PinMAME ROM pages hold every version of a machine's ROM), all of them are downloaded and installed under their own names: PinMAME "clone" versions only contain the files that differ and need their parent set beside them. **All ROMs** does the same for a table that already has its ROM.
- **Favourites and play history** – shared with the VR lobby in `%APPDATA%\VrTableManager\play-stats.json`. Tap ☆ on a table; My Tables filters by All / Favourites / Most played / Recently played. A play is counted when a table actually launches (from here or the lobby), with the time played added when it closes.
- **Play in VR** – VPX runs with `-Ini VrTableManager-VR.ini -Play <table>` (in 10.8.1, `-Play` runs the player without the editor and exits when the table closes; `-Minimized`/`-ExtMinimized` are editor-only and rejected with `-Play`), where that ini sets `[PlayerVR] AskToTurnOn = 0`. Future Pinball runs `BAM\FPLoader.exe /open <table> /play /exit` (BAM-OpenVR + SteamVR do the VR). The window hides while you play and comes back when the table closes.
- **Setup** – installs the latest official VPX BGFX build from GitHub (the build with OpenXR VR), shows the active OpenXR runtime, and finds Future Pinball + BAM.

## Pinball FX and Pinball M through UEVR

**Pinball settings for UEVR** (`Core/Services/UevrPinballProfile.cs`): before each VR launch (from here or the VR lobby) the app writes ten pinball-tuned settings into the game's UEVR profile (`%APPDATA%\UnrealVRMod\<game process>\config.txt`): level table (`VR_DecoupledPitch`), menus kept in front of you (`UI_FollowView`, `UI_Distance`, `UI_Size`, `VR_EnableGUI`), Quest controllers off for the game and UEVR's menu (`VR_ControllersAllowed`, `UI_Framework_MouseEmulation`), UEVR's menu only on a long thumbstick press and closed at start. Nothing else in the profile changes; the original is backed up to `config.vrtm-original.txt` the first time. Setup has **Reset to pinball defaults**, **Restore my original**, and switches for this and for the pre-launch checklist (gamepad detected?, controllers down, OpenXR in the injector, flagged when the last run used OpenVR, read from `Frontend_RequestedRuntime`).

Pinball M (now sold as **Pinball FX Midnight**, Steam app 2337640) works exactly like Pinball FX and shares the same engine code (`ZenGames` / `ZenUevrEngine`). The game's process name is read from its `Binaries\Win64\*-Win64-Shipping.exe`, so UEVR attaches to the right program; each game has its own UEVR profile folder and its own Import profile button on Setup.

`VrTableManager.Core/Services/PinballFxEngine.cs`. Pinball FX (Steam app 2328760, or Epic) is found automatically from Steam's `libraryfolders.vdf` / `appmanifest_2328760.acf` or Epic's launcher manifests, and appears in My Tables as one entry.

**Play in VR** starts `UEVRInjector.exe --attach=PinballFX-Win64-Shipping`, then launches the game through Steam/Epic. The injector waits for the game window, injects, and is closed when the game exits. **Desktop** launches the game without UEVR. **Import UEVR profile** unpacks a community profile zip into `%APPDATA%\UnrealVRMod\PinballFX-Win64-Shipping`.

Limits:
- Pinball FX has no command-line option to open a specific table, and owned DLC tables aren't recorded on disk, so you pick the table inside the game.
- UEVR's injector has no command-line flag for OpenXR vs OpenVR. It reuses whatever was last picked in its window (players recommend OpenXR for this game).
- Players report crashes with VR controllers in Pinball FX under UEVR; use a gamepad.

## Updates

Checked once each time the app starts (after the catalog loads), or with **Check now** on Setup.

- **Tables** – each installed table's `vrtm.json` records the VPS table file id and version. When VPS lists a different version for that entry, My Tables shows an **Update** badge (and a count on the tab). Updating re-runs the install into the same folder, removes the old `.vpx`/`.fpt`, and keeps ROMs, NVRAM (high scores) and table settings.
- **VPX** – latest BGFX build from github.com/vpinball.
- **UEVR** – latest stable `UEVR.zip` from github.com/praydog/UEVR, unpacked over the UEVR folder. Game profiles live in `%APPDATA%\UnrealVRMod`, so they're untouched.
- **BAM** – check only: the latest version is read from ravarcade.pl; installing stays manual.
- **Future Pinball** – no updates (final release 2010).

**Update all** updates VPX and UEVR when newer, then every table with an update, one at a time.

## Adding another engine

Implement `IPinballEngine` (in `VrTableManager.Core/Services/EngineSetup.cs`) and add it to `Engines.All`. An engine supplies a setup status line, `LaunchAsync` (it returns the process whose exit brings the picker back), and optionally `ListExtraTables` for tables that don't come from the file installer. Set `ManagesFiles => false` for store-owned games so the app never deletes them. The VR lobby picks new engines up automatically, since it uses the same library.

## Code layout and the VR lobby

Everything except the UI and the Playwright downloader lives in **`VrTableManager.Core`**: models, scanning and installing, the engines, and the lobby's navigation logic. It's shared with the VR lobby (`VrLobby`, Godot C#) and `SmokeTest`.

The lobby launches tables through this app's headless mode, which plays one table through the normal engine and then starts the lobby again:

```
VrTableManager.exe --play <table path> [--vr] [--settings <settings.json>] [--return <exe> [--return-arg <arg>]...]
```

On a normal start the app records its exe path in `settings.json` (`ManagerExe`), so the lobby can find it. Play sessions are logged to `%APPDATA%\VrTableManager\play.log`.
