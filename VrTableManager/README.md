# VR Table Manager

Browse the Virtual Pinball Spreadsheet, install tables with one click, and play them in VR (Quest 3 via Link / Air Link / Virtual Desktop).

## Run

```
dotnet run --project VrTableManager
```

## How it works

- **Get Tables** – catalog from the Virtual Pinball Spreadsheet (`vpsdb.json`), filterable by VPX / Future Pinball and "VR-ready" (tables tagged `VR` or `Hybrid`).
- **Install** – direct links (GitHub etc.) download straight away. Community sites (VPUniverse, VPForums, Pinball Nirvana) ask that you download yourself, so the file page opens in your default browser: you sign in, accept the terms and click Download, and `AssistedDownloader` picks the finished file up from your Downloads folder (or **I SAVED IT…** if you saved it elsewhere; ROM pages take every file until **DONE**). Several pages can wait at once (they don't take a download slot): each new file goes to the waiting job that takes its type and whose game, ROM or kind ("pup") its name carries, and when that can't be told the job asks you to use **I SAVED IT…**. `BrowserDownloader` (Playwright) is only used for a site listed in `SitePolicy`, which is empty: add one only with that site's written permission. It identifies itself as automated and never accepts terms for you.
- **Unpack** – each VPX table gets its own folder (`Name (Manufacturer Year)`) laid out the way VPX 10.8.1 expects: `pinmame/roms`, `music`, `altsound`, `serum` / `vni`, `pupvideos`, backglass next to the `.vpx` (see VPX's `docs/FileLayout.md`). A `vrtm.json` manifest records which VPS entry it came from.
- **ROMs** – a table loads exactly one ROM, named in its script (`Const cGameName = "mm_109c"`); the installer reads it from the `.vpx` (falling back to VPS's ROM list). After a table installs, its ROM is fetched from the catalog's ROM link (usually VPForums, through the same sign-in browser) and saved as `pinmame/roms/<rom>.zip`, whether the download is the ROM zip itself or a pack with several ROMs inside. My Tables has **Get ROM** for this, and **Add ROM** for a file you already have. When the ROM page offers several files (VPUniverse's PinMAME ROM pages hold every version of a machine's ROM), all of them are downloaded and installed under their own names: PinMAME "clone" versions only contain the files that differ and need their parent set beside them. **All ROMs** does the same for a table that already has its ROM.
- **PuP packs** – PinUP Player videos for the backglass, DMD and topper. VPX 10.8.1's PUP plugin (turned on for VR and desktop play; Setup can turn it off) reads them from the table's own folder, `pupvideos\<pack>\`, and shows them on the cabinet in VR. My Tables → **PUP ▾** lists the catalog's packs for the table (`pupPackFiles`, nearly all on VPUniverse, fetched through the sign-in browser; Mega/WeTransfer links are skipped). The pack folder (the one holding `screens.pup` / `playlists.pup`) is found anywhere in the archive and named after what the table script opens (`PuPlayer.Init pBackglass, "name"`), or its own name for ROM packs that follow the DMD. Only that folder is copied: option `.bat` files and installers in packs are never run. The pack's catalog id and version go into `vrtm.json` (`PupPack`) for updates.
- **DMD colours and AltSound** – for ROM tables, My Tables → **COLOUR DMD ▾** and **ALTSOUND ▾** list the catalog's colorizations (`altColorFiles`; this ROM's first, Serum before Pin2DMD; ROM patches and PAC-only entries left out) and sound packs (`altSoundFiles`). They install where VPX 10.8.1's plugins look, named after the ROM the script runs (its alias when it has one): Serum as `serum\<rom>\<rom>.cRZ`, Pin2DMD palettes as `vni\<rom>\` (the VNI plugin can't read encrypted `.pac` files, so PAC-only downloads are refused), AltSound as `altsound\<rom>\` (altsound.csv, g-sound.csv or the legacy PinSound folders). Older layouts from table downloads (`altcolor\`, `pinmame\altcolor\`, `pinmame\altsound\`) are moved there. Recorded in `vrtm.json` (`AltColor`, `AltSound`) for updates.
- **Backglass** – My Tables → **BACKGLASS ▾** lists the catalog's backglasses (`b2sFiles`, newest first) and takes a `.directb2s` you already have (or the archive it came in). It's copied beside the table as `<table name>.directb2s`, whatever the download calls it, since that's the name VPX's B2S plugin looks for; when an archive has several, the one already named like the table wins, else the biggest, and the job says which. Recorded in `vrtm.json` (`Backglass`). A table update with a new file name renames the backglass to match.
- **Rules** – My Tables → **RULES ▾** opens, in your browser, The Pinball Primer's rules-and-strategy page for the real machine (its game list is cached weekly in `%APPDATA%\VrTableManager`, matched by name and year) and the catalog's instruction cards (`ruleFiles`). VPX and Future Pinball tables only; Pinball FX and Pinball M are store games with their own rules.
- **Plugins** – VR and desktop launches turn on VPX's built-in PinMAME, Serum, VNI and AltSound plugins, the PinUP Player plugin, and FlexDMD (script-drawn DMDs, used by many originals) and B2S (`.directb2s` backglasses). Each group has a switch in Setup. They go in the VR and desktop override files (see [VPX settings](../docs/vpx-settings.md)), so desktop play also finds ROMs and PuP packs in the table's folder.
- **Favourites and play history** – shared with the VR lobby in `%APPDATA%\VrTableManager\play-stats.json`. Tap ☆ on a table; My Tables filters by All / Favourites / Most played / Recently played. A play is counted when a table actually launches (from here or the lobby), with the time played added when it closes.
- **Play in VR** – VPX runs with `-Ini VrTableManager-VR.play.ini -Play <table>` (your own `VPinballX.ini` with `VrTableManager-VR.ini` on top; DESKTOP uses `VrTableManager-Desktop.play.ini` the same way) (in 10.8.1, `-Play` runs the player without the editor and exits when the table closes; `-Minimized`/`-ExtMinimized` are editor-only and rejected with `-Play`), where the VR overrides set `[PlayerVR] AskToTurnOn = 0`. Settings VPX changes during play are kept in that mode's override file. Future Pinball runs `BAM\FPLoader.exe /open <table> /play /exit` (BAM-OpenVR + SteamVR do the VR). The window hides while you play and comes back when the table closes.
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

- **Tables** – each installed table's `vrtm.json` records the VPS table file id and version. When VPS lists a different version for that entry, My Tables shows an **Update** badge (and a count on the tab). Updating re-runs the install into the same folder, removes the old `.vpx`/`.fpt`, and keeps ROMs, NVRAM (high scores), PuP packs and table settings.
- **Add-ons** – same check on the PuP pack, colours, AltSound and backglass recorded in `vrtm.json`; **Update** replaces the installed folder (or file, for a backglass).
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
