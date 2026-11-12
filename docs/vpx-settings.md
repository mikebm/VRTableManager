# VPX settings for VR and desktop play

Visual Pinball X keeps its settings in one file, `VPinballX.ini`. You might tune that file for your monitor, then want different settings in the headset. You might also want the manager's changes (VPX's built-in PinMAME, PuP packs, VR switched on) without losing your own.

So when you play a table from VR Table Manager, its settings come in layers:

| Layer | File | Used for | Who changes it |
|---|---|---|---|
| 1. Your settings | `VPinballX.ini` (the one VPX itself loads) | VR **and** desktop play | You, in VPX or by hand. The manager writes to it only for the case described under "VR graphics switched off" below. |
| 2. VR settings | `VrTableManager-VR.ini`, next to `VPinballX.exe` | **Play in VR** and the VR lobby | The manager, VPX's in-game menu during VR play, and you |
| 2. Desktop settings | `VrTableManager-Desktop.ini`, next to `VPinballX.exe` | **DESKTOP** play | The manager, VPX's in-game menu during desktop play, and you |
| 3. Table settings | `<table>.ini`, next to the table's `.vpx` | That one table | VPX, and the per-table render scale in the manager |

A higher layer wins. Anything a layer doesn't set comes from the layer below it.

## What this means in practice

- **Change something in your own `VPinballX.ini` and it applies everywhere.** Change a key binding or the sound volume in desktop VPX, and VR play picks it up too, unless the VR file has its own value for that setting.
- **The VR and desktop files only hold differences.** A setting with an empty value (`AAFactor = `) isn't an override, so yours is used. VPX writes empty values for anything left at its default, so most lines in these files are empty.
- **Changes made while playing are kept for that mode.** If you change a setting in VPX's in-game menu while playing in VR (sound, graphics, or lining the table up with the left thumbstick click), it goes into the VR file when the table closes. It won't affect desktop play or your own `VPinballX.ini`, except for graphics with the VR graphics switched off (next point).
- **VR graphics switched off.** With **Use these graphics settings in VR** unchecked (Setup → VR graphics), VR play uses the graphics in your own `VPinballX.ini`. A graphics setting you change in VPX's in-game menu during VR play then goes back into your `VPinballX.ini` when the table closes, just as it would outside the manager. These are the panel's settings: render scale, MSAA, post-processing anti-aliasing, reflections, ambient occlusion, bloom, sharpen and the FPS display. Since it's your own file, desktop play and VPX on its own pick the change up too. Other changes still go into the VR file. A graphics setting the table's own `<table>.ini` sets (its render scale, say) stays that table's and is never saved as yours. If your `VPinballX.ini` can't be written (read-only, or open in another program), the change isn't kept and the play log says why.
- **The manager's own options live in the VR and desktop files.** The Setup tab's checkboxes (built-in PinMAME, PuP packs, FlexDMD and B2S, adding a backglass in VR) and the VR graphics presets are written there.
- **Tables you open in VPX directly, outside the manager, use only your `VPinballX.ini`.** Nothing changes for them.

## Which `VPinballX.ini` is "yours"

The same one VPX loads when you start it on its own:

1. `%APPDATA%\VPinballX\<version>\VPinballX.ini` (for example `…\VPinballX\10.8\VPinballX.ini`), if it exists.
2. Otherwise `VPinballX.ini` next to `VPinballX.exe`.

If neither exists yet, start `VPinballX.exe` on its own once (not from the manager) and VPX creates it.

## Editing the files

**Setup → Visual Pinball X → VPX settings, in layers** has three buttons:

- **EDIT MY VPINBALLX.INI** opens your own settings.
- **EDIT VR SETTINGS** opens `VrTableManager-VR.ini`.
- **EDIT DESKTOP SETTINGS** opens `VrTableManager-Desktop.ini`.

Each opens in your default editor for `.ini` files (usually Notepad). Edit them while no table is running: VPX saves its settings when a table closes.

The format is VPX's own: `[Section]` headings, then `Key = value` lines. For example, to render VR at 80% without touching your desktop settings, put this in the VR file:

```ini
[Player]
AAFactor = 0.8
```

To go back to your own value for a setting, empty it (`AAFactor = `) or delete the line.

One exception: if the VR or desktop file had a value for a setting and you put it back to its default in VPX's own menu during play, the manager remembers that, so VPX's default keeps winning over your own value in that mode. It's noted at the end of the file:

```ini
[VrTableManager.Defaults]
Player/AAFactor = 1
```

Delete that line (or the whole section) to bring your own value back. Changing the setting from the Setup tab, or in VPX's menu during play, also clears it. A value you type into the file by hand wins while it's there, but emptying it again brings back VPX's default until you delete the line.

Some settings are best changed from the Setup tab instead, because the manager writes them before every launch. If you edit one of these by hand, your value is replaced at the next launch:

- `[PlayerVR] AskToTurnOn`
- `[PlayerVR] DisplayRefreshRate`
- `[PlayerVR] AddBackglass`
- `[Plugin.PinMAME]`, `[Plugin.Serum]`, `[Plugin.VNI]`, `[Plugin.AltSound]`, `[Plugin.PUP]`, `[Plugin.FlexDMD]` and `[Plugin.B2S]` `Enable`

## Behind the scenes

Before each launch the manager combines the layers into `VrTableManager-VR.play.ini` or `VrTableManager-Desktop.play.ini`, next to `VPinballX.exe`, and starts VPX with it (`-Ini`). When the table closes, the manager compares that file with what it started with. Any setting VPX changed is copied into the VR or desktop file. Values written differently but meaning the same (`1.5` and `1.500000`) don't count as changes.

The `.play.ini` files are rebuilt every launch, so editing them has no lasting effect.

## Fit VR to me

**Setup → VR & graphics → FIT VR TO ME** lines every VPX table up the same way, so you don't have to move each table in the headset. You don't need to do anything for this: until you set your own values, every VR launch uses a real cabinet's (lockbar 91 cm high, eyes 10 cm behind it, each table at its real size). Click **FIT EVERY TABLE TO ME** only to change them: changes in the panel count once you click it (until then it says "Not saved yet"). **RESET TO DEFAULTS** goes back to the real-cabinet values and puts the table straight ahead. It's saved once, for every table, including tables you add later. The values go into the VR file, so desktop play doesn't change.

VPX 10.8.1 positions every table in VR using these settings:

| Setting | VPX's default | What it does |
|---|---|---|
| `[Player] LockbarHeight` | 85 cm | Every table's lockbar is put this high above your floor. The manager uses a real cabinet's 91 cm (36") until you set your own; typing your height suggests 0.52 × it (175 cm → 91 cm). |
| `[Player] LockbarWidth` | 70 cm | Every table is scaled so its lockbar is this wide. VPX counts the lockbar as the playfield width plus 2¼", so 70 cm shows a standard 20¼" table about 22% too big. |
| `[Player] ScreenPlayerY` | -10 cm | Where your eyes are when VPX recenters the view (keypad 5): this far behind the lockbar. Higher moves you back; negative puts you over the glass, so VPX's own default leans you into the table. The manager uses 10 cm; the Setup tab's − and + move it 1 cm (1"), up to 100 cm back. It's only exact after you recenter: until then the saved table position counts from the headset's tracking origin, not your head. VPX lists -70 to 30, but that only limits its in-game slider; a larger value in the file is used as written. Moving VPX's slider brings it back within 30. |
| `[PlayerVR] TableX/Y/Z`, `Orientation` | 0 | The offset VPX saves when you move or recenter the table. VPX always saves these for every table, never for one. |

**Table size** has three choices:

- **Real size** (the default): just before each VR launch, the manager reads the table's playfield width from its `.vpx` and gives that launch the table's own `LockbarWidth`, so VPX shows it at scale 1. It isn't saved anywhere, so a width you change in VPX's menu lasts only until that table closes. A table whose own `<table>.ini` sets a `LockbarWidth` keeps that one.
- **The same width for every table**: one `LockbarWidth` for all of them, for example to match a controller stand.
- **VPX's own sizing**: VPX's 70 cm, or whatever you set in VPX.

**FIT EVERY TABLE TO ME** also clears `TableZ`, because a height offset would move every lockbar away from the height you chose. If you tick **Also put the table straight ahead**, it also resets where the table sits: straight ahead of the headset's centre point, with your eyes the distance you chose behind the lockbar. Afterwards, if the lockbar isn't where you stand, press keypad 5 once while standing where you play. VPX keeps that for every table.

Lining the table up with your VR controllers (left thumbstick click) takes the lockbar height and width from the controllers until that table closes.

### One table sits differently

Some tables sit higher or lower than others, because their authors set a different glass height. VPX puts the playfield that far below your lockbar. For example, Jaws's glass is 24 cm above its playfield and Attack from Mars's is 16 cm, so Jaws sits about 3" lower. To fix one table, raise or lower it while you play it (keypad 8 and 2, or VPX's VR menu: X on the left controller). When it closes, the manager saves that height (`[PlayerVR] TableZ`) in the table's own `<table>.ini`, so only that table keeps it. Its card in **My Tables** then says "sits 6 cm higher in VR", and its **⋯** menu can clear it. Lowering it back to 0 in the headset clears it too. Recentering (keypad 5, or lining up with the controllers) also sets VPX's height to 0, but it moves where you stand at the same time, so the manager can tell the two apart and keeps the table's height.

Every table's own height is 0: each VR launch sets `TableZ` to 0 for every table and never keeps it. That also drops a height saved for every table before heights were per table. Where you stand (forward, back, sideways, turned) is still kept for every table, since it's about your room, not the table. For every table's height, change the lockbar height in **FIT VR TO ME**. That includes VPX's **Save Globally** on its VR Settings page: for a height, it now only applies to the table you're playing.

VPX's own **Save as Table Override** (on its VR Settings page) also works: the table keeps the whole position for itself. When any table closes, VPX saves the position it ended at for every table. When the table closes, the manager checks which position values the table keeps for itself, including any saved during that play, and doesn't keep those for every table, so the other tables stay where they were.

The manager also writes `[PlayerVR] OverrideLockbarHeight` and `OverrideLockbarWidth`. VPX 10.8.1 ignores them, but newer VPX builds use them to decide whether your lockbar values apply. Future Pinball's BAM keeps its own VR position, which this doesn't change.

## Controls

VR play uses the controls from your own `VPinballX.ini` until the VR file has mappings of its own. That happens when you remap a button during VR play, when you save a key for VR only (below), or when you click **USE MY DESKTOP VPX CONTROLS** under Setup → Controls. That button merges your desktop mappings into the VR file and keeps the Quest controller buttons as alternatives.

### Setting keys on the Setup tab

Setup → **Controls** lists VPX's actions (flippers, magna-saves, plunger, Start, coins, nudge and tilt, Exit, the in-game menu, Align VR view…) with the key each one has in desktop play and in VR play. This needs VPX 10.8.1 or later. Each row has three buttons:

- **SET**: press a key, and it becomes the action's only keyboard key.
- **ADD**: press a key, and it works for the action as well as the keys it already has (VPX's default one included). For example, ADD on Left flipper with Z gives "Left Shift / Z".
- **RESET**: the action's keyboard key goes back to VPX's default. VR controller, gamepad and cabinet bindings stay, as do key combinations. If the action has none of those, its mapping is emptied, which is VPX's own default. If that empties a mapping in the VR or desktop file while your own `VPinballX.ini` has one for the action, the manager also notes it in that file (`[VrTableManager.Defaults]`, see above), so your own key doesn't come back in that mode.

All three only change keyboard keys. To give an action a second button on a gamepad, a cabinet board or the VR controllers, use VPX's own input menu.

**Save keys for** picks where the key goes:

| Choice | Written to | Used by |
| --- | --- | --- |
| Desktop and VR play | Your own `VPinballX.ini` | Desktop and VR play, and VPX started on its own. If the VR or desktop file has its own mapping for that action (one that would hide the change), its key is changed there too. |
| VR play only | `VrTableManager-VR.ini` | VR play. Your `VPinballX.ini` isn't changed. |
| Desktop play only | `VrTableManager-Desktop.ini` | Desktop play. Your `VPinballX.ini` isn't changed. |

A key set with SET replaces the keyboard keys for that action only. Bindings on the VR controllers, a gamepad or a cabinet board stay, as do key combinations. The staged flipper and the in-game menu's navigation keys move with the flipper or magna-save key they share (for SET, ADD and RESET), unless you gave them a different key. If another action already uses the key, the page says so; VPX allows it, but it's usually a mistake. Gamepad and cabinet-board buttons are still mapped in VPX's own menu.

## Backups

**BACK UP NOW** on the Setup tab includes the VR and desktop files, and the settings in VPX's preferences folder (`%APPDATA%\VPinballX`). A `VPinballX.ini` kept next to `VPinballX.exe` isn't included, so copy that one yourself.
