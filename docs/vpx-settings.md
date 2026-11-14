# VPX settings for VR and desktop play

Visual Pinball X keeps its settings in one file, `VPinballX.ini`. That's a problem if you want different settings in the headset than on your monitor, or if you want the app to switch things on for you (PinMAME, PuP packs, VR) without it rewriting your own file.

So when you play a table from VR Table Manager, its settings come in layers:

| Layer | File | Used for | Who changes it |
|---|---|---|---|
| 1. Your settings | `VPinballX.ini`, the one VPX itself loads | VR **and** desktop play | You, in VPX or by hand. The app only writes to it in one case, covered under "VR graphics switched off" below. |
| 2. VR settings | `VrTableManager-VR.ini`, next to `VPinballX.exe` | **Play in VR** and the VR lobby | The app, VPX's in-game menu during VR play, and you |
| 2. Desktop settings | `VrTableManager-Desktop.ini`, next to `VPinballX.exe` | **DESKTOP** play | The app, VPX's in-game menu during desktop play, and you |
| 3. Table settings | `<table>.ini`, next to the table's `.vpx` | That one table | VPX, and the per-table render scale in the app |

A higher layer wins. Anything a layer doesn't set comes from the one below.

## What this means in practice

- **Change something in your own `VPinballX.ini` and it applies everywhere.** Change a key or the volume in desktop VPX, and VR play picks it up too, unless the VR file has its own value for it.
- **The VR and desktop files only hold the differences.** A setting with an empty value (`AAFactor = `) isn't an override, so yours is used. VPX writes an empty value for anything left at its default, which is why most lines in these files are empty.
- **Changes you make while playing stay with that mode.** Change something in VPX's in-game menu while playing in VR (sound, graphics, lining the table up) and it goes into the VR file when the table closes. Desktop play and your own `VPinballX.ini` don't change. The one exception is graphics with the VR graphics switched off, next.
- **VR graphics switched off.** With **Use these graphics settings in VR** unticked (Setup → VR graphics), VR play uses the graphics from your own `VPinballX.ini`. Change one of them in VPX's menu during VR play and it goes back into your `VPinballX.ini` when the table closes, just as if you'd played without the app, so desktop play picks it up too. That covers render scale, MSAA, anti-aliasing, reflections, ambient occlusion, bloom, sharpen and the FPS display. Anything else still goes into the VR file, and a graphics setting the table's own `<table>.ini` sets stays with that table. If your `VPinballX.ini` can't be written (read-only, or open in another program), the change isn't kept, and the play log says why.
- **The app's own options live in the VR and desktop files.** Setup's switches (PinMAME, PuP packs, FlexDMD and B2S, the VR backglass) and the VR graphics presets are written there.
- **Tables you open in VPX directly use only your `VPinballX.ini`.** Nothing changes for them.

## Which `VPinballX.ini` is "yours"

The same one VPX loads when you start it on its own:

1. `%APPDATA%\VPinballX\<version>\VPinballX.ini` (for example `…\VPinballX\10.8\VPinballX.ini`), if it exists.
2. Otherwise `VPinballX.ini` next to `VPinballX.exe`.

If neither exists yet, start `VPinballX.exe` on its own once (not from the app) and VPX creates one.

## Editing the files

**Setup → Visual Pinball X → VPX settings, in layers** has three buttons: **EDIT MY VPINBALLX.INI**, **EDIT VR SETTINGS** and **EDIT DESKTOP SETTINGS**. Each opens the file in your usual editor for `.ini` files, usually Notepad. Edit them while no table is running, since VPX saves its settings when a table closes.

The format is VPX's own: `[Section]` headings, then `Key = value` lines. For example, to render VR at 80% without touching your desktop settings, put this in the VR file:

```ini
[Player]
AAFactor = 0.8
```

To go back to your own value, empty it (`AAFactor = `) or delete the line.

There's one catch. If the VR or desktop file had a value for a setting, and you put it back to VPX's default in VPX's own menu during play, the app remembers that, so VPX's default keeps winning over your own value in that mode. It notes it at the end of the file:

```ini
[VrTableManager.Defaults]
Player/AAFactor = 1
```

Delete that line (or the whole section) to get your own value back. Changing the setting on Setup, or in VPX's menu during play, clears it too. A value you type into the file by hand wins while it's there, but if you empty it again, VPX's default comes back until you delete the line.

Some settings are better changed on Setup, because the app writes them before every launch. If you edit one by hand, it's replaced at the next launch:

- `[PlayerVR] AskToTurnOn`
- `[PlayerVR] DisplayRefreshRate`
- `[PlayerVR] AddBackglass`
- `Enable` in `[Plugin.PinMAME]`, `[Plugin.Serum]`, `[Plugin.VNI]`, `[Plugin.AltSound]`, `[Plugin.PUP]`, `[Plugin.FlexDMD]` and `[Plugin.B2S]`

## How it works

Before each launch, the app combines the layers into `VrTableManager-VR.play.ini` or `VrTableManager-Desktop.play.ini` next to `VPinballX.exe`, and starts VPX with it. When the table closes, it compares that file with what it started with, and copies anything VPX changed into the VR or desktop file. Values written differently but meaning the same (`1.5` and `1.500000`) don't count as changes.

The `.play.ini` files are rebuilt every launch, so editing them does nothing lasting.

## Fit VR to me

**Setup → Fit & position → FIT VR TO ME** lines every VPX table up the same way, so you're not moving each one around in the headset.

You don't have to do anything. Until you set your own values, every VR launch uses a real cabinet's: the lockbar 91 cm (36") off the floor, your eyes 10 cm behind it, and each table at its real size. Change the values in the panel and click **FIT EVERY TABLE TO ME** to use them (until you click it, the panel says "Not saved yet"). It's saved once for every table, including ones you add later. **RESET TO DEFAULTS** goes back to the real-cabinet values and puts the table straight ahead. The values go in the VR file, so desktop play doesn't change.

### Leaving it to VPX

If you'd rather VPX placed and sized tables exactly the way it does without the app, tick **Leave table size and position to VPX** at the top of the panel. VR launches then add nothing for where tables stand or how big they are, so VPX uses your own `VPinballX.ini`, or its own defaults (an 85 cm high, 70 cm wide lockbar, and your eyes 10 cm over the glass). The rest of the panel greys out.

Turning it on clears the values you saved here from the VR settings file. Where VPX saved the table during play stays. A height you set for one table in the headset is then kept the way VPX keeps it, for every table, not just that one. Untick it to go back to fitting tables to you.

### The settings

VPX 10.8.1 places every table in VR using these settings:

| Setting | VPX's default | What it does |
|---|---|---|
| `[Player] LockbarHeight` | 85 cm | How high every table's lockbar is above your floor. The app uses 91 cm until you set your own. Type in your height and it suggests 0.52 × that (175 cm gives 91 cm). |
| `[Player] LockbarWidth` | 70 cm | Every table is scaled so its lockbar is this wide. VPX counts the lockbar as the playfield width plus 2¼", so 70 cm shows a standard 20¼" table about 22% too big. |
| `[Player] ScreenPlayerY` | -10 cm | Where your eyes are when VPX recenters the view (keypad 5), this far behind the lockbar. VPX's own -10 puts you over the glass, leaning into the table. The app uses 10 cm, and Setup's − and + move it 1 cm at a time, up to 100 cm back. It's only exact after you recenter. VPX's slider only goes from -70 to 30, but a bigger value in the file still works; moving VPX's slider brings it back under 30. |
| `[PlayerVR] TableX/Y/Z`, `Orientation` | 0 | Where VPX saved the table when you moved or recentered it. VPX saves these for every table, never for just one. |

**Table size** has three choices:

- **Real size**, the app's default. Just before each VR launch, the app reads the table's playfield width from its `.vpx` and gives that launch the table's own `LockbarWidth`, so VPX shows it at its real size and a widebody comes out wider than a standard table. Nothing's saved, so a width you change in VPX's menu only lasts until the table closes. A table whose own `<table>.ini` sets a `LockbarWidth` keeps it. Pick this if you play standing in open space.
- **The same width for every table (a cabinet or controller stand)**: every table is sized to one lockbar width, so it always lines up with the real thing in front of you. See below.
- **VPX's default sizing**: what VPX does without the app, a 70 cm lockbar for every table (or whatever you set in VPX). That makes a standard table about a fifth too big.

### Playing at a cabinet or controller stand

If you play VR at a real pinball cabinet, a cabinet-style controller like a PinOne on a stand, or anything else with a lockbar and flipper buttons you can touch, you want every table's lockbar to land exactly on your real one. Here's how:

1. **Measure the lockbar height**: from the floor to the top of your lockbar. Put it in **Lockbar height**.
2. **Pick "The same width for every table"** under **Table size**, and enter a **Lockbar width**. On a cabinet, measure across the playfield between the side rails and add 6 cm (2¼"), since that's how VPX measures it. A standard (WPC) body comes to 57 cm (22½"), a widebody about 65 cm. On a controller stand, 57 cm is a good place to start.
3. Leave **Eyes behind it** at 10 cm unless you stand further back than usual.
4. Tick **Also put the table straight ahead**, and click **FIT EVERY TABLE TO ME**.
5. Start any table, stand where you play with your hands on the buttons, and recenter once: keypad 5, or the cabinet button you mapped to VPX's **Align VR view**. VPX keeps that position for every table.

With Real size instead, a widebody would stick out past your cabinet and a narrow EM would sit inside it, so the flippers wouldn't line up with your buttons.

If one table still sits too high or low (it happens when a table's author set an unusual glass height), fix just that one, as below.

**FIT EVERY TABLE TO ME** also clears `TableZ`, since a height offset would move every lockbar away from the height you picked. Tick **Also put the table straight ahead** and it resets where the table sits too: straight ahead of the headset's centre, with your eyes the distance you chose behind the lockbar. If the lockbar then isn't where you're standing, press keypad 5 once from where you play. VPX keeps that for every table.

Lining the table up with your VR controllers (left thumbstick click) takes the lockbar height and width from the controllers until that table closes.

### One table sits differently

Some tables sit higher or lower than others, because their authors set a different glass height and VPX puts the playfield that far below your lockbar. Jaws's glass is 24 cm above its playfield and Attack from Mars's is 16 cm, for example, so Jaws sits about 3" lower.

To fix one table, raise or lower it while you're playing it (keypad 8 and 2, or VPX's VR menu on the left controller's X). When it closes, the app saves that height (`[PlayerVR] TableZ`) in the table's own `<table>.ini`, so only that table keeps it. Its card in **My Tables** then says something like "sits 6 cm higher in VR", and its **⋯** menu can clear it. Lowering it back to 0 in the headset clears it too.

Recentering (keypad 5, or lining up with the controllers) also sets VPX's height to 0, but it moves where you stand at the same time, so the app can tell the two apart and keeps the table's height.

Every other table's height is 0: each VR launch sets `TableZ` to 0 for every table, which also drops any height saved for every table before heights were per table. Where you stand (forward, back, sideways, turned) is still kept for every table, since that's about your room, not the table. To change every table's height, change the lockbar height in **FIT VR TO ME**. That includes VPX's **Save Globally** on its VR Settings page, which for a height now only applies to the table you're playing.

VPX's own **Save as Table Override** works too: the table keeps its whole position for itself. When any table closes, VPX saves the position it ended at for every table, so the app checks which position values the table keeps for itself and doesn't save those for every table. The other tables stay where they were.

The app also writes `[PlayerVR] OverrideLockbarHeight` and `OverrideLockbarWidth`. VPX 10.8.1 ignores them, but newer VPX builds use them to decide whether your lockbar values apply. Future Pinball's BAM keeps its own VR position, which none of this changes.

## Controls

VR play uses the controls from your own `VPinballX.ini` until the VR file has mappings of its own. That happens when you remap a button during VR play, when you save a key for VR only (below), or when you click **USE MY DESKTOP VPX CONTROLS** under Setup → Controls. That button copies your desktop mappings into the VR file and keeps the Quest controller buttons as alternatives.

### Setting keys on Setup

**Setup → Controls** lists VPX's actions (flippers, magna-saves, plunger, Start, coins, nudge and tilt, Exit, the in-game menu, Align VR view…) with the keys and buttons each one has in desktop play and in VR play. It needs VPX 10.8.1 or later. Each row has three buttons:

- **SET**: press a key, or a button on a game controller or cabinet board. A key becomes the action's only keyboard key. A button becomes its only button on that controller; its keys and buttons on other controllers stay.
- **ADD**: press a key or button and it works for the action alongside what it already has, VPX's default key included. ADD on Left flipper with Z gives you "Left Shift / Z".
- **RESET**: the action goes back to VPX's default key. Its other keys and controller buttons go; Quest controller bindings, analog axes and key combinations stay. If that empties the action in the VR or desktop file while your own `VPinballX.ini` has a key for it, the app notes it under `[VrTableManager.Defaults]` (see above), so your own key doesn't come back in that mode.

**Controllers and cabinet boards.** Xbox, PlayStation and other USB or Bluetooth game controllers should work, and so should cabinet boards in joystick mode (Pinscape, PinOne and the like). A board in keyboard mode sends keys, which work as keys. The app reads controllers with VPX's own SDL library, so VPX has to be installed first. Buttons and d-pad directions count; analog sticks, triggers and plungers don't, so map those in VPX's menu. A row shows a button as "PS4 Controller button 5", numbered from 1 like Windows' game controller settings. With two identical controllers, which one is "1" follows the order Windows lists them in.

The Quest controllers' buttons can't be set here, since VPX only sees them in the headset. Set those in VPX's own menu during VR play.

**Save keys for** picks where a key goes:

| Choice | Written to | Used by |
| --- | --- | --- |
| Desktop and VR play | Your own `VPinballX.ini` | Desktop and VR play, and VPX on its own. If the VR or desktop file has its own mapping for that action, it's changed there too, so it doesn't hide yours. |
| VR play only | `VrTableManager-VR.ini` | VR play. Your `VPinballX.ini` isn't changed. |
| Desktop play only | `VrTableManager-Desktop.ini` | Desktop play. Your `VPinballX.ini` isn't changed. |

SET only replaces the action's keyboard keys. Its bindings on VR controllers, gamepads and cabinet boards stay, and so do key combinations. The staged flipper and the in-game menu's navigation keys follow the flipper or magna-save key they share, unless you gave them a different one. If another action already uses the key, the page tells you. VPX allows it, but it's usually a mistake.

## Backups

**BACK UP NOW** under Setup → Maintenance includes the VR and desktop files, and the settings in VPX's own folder (`%APPDATA%\VPinballX`). A `VPinballX.ini` kept next to `VPinballX.exe` isn't included, so copy that one yourself.
