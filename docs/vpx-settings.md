# VPX settings for VR and desktop play

Visual Pinball X keeps its settings in one file, `VPinballX.ini`. You might tune that file for your monitor, then want different settings in the headset. You might also want the manager's changes (VPX's built-in PinMAME, PuP packs, VR switched on) without losing your own.

So when you play a table from VR Table Manager, its settings come in layers:

| Layer | File | Used for | Who changes it |
|---|---|---|---|
| 1. Your settings | `VPinballX.ini` (the one VPX itself loads) | VR **and** desktop play | You, in VPX or by hand. The manager never writes to it. |
| 2. VR settings | `VrTableManager-VR.ini`, next to `VPinballX.exe` | **Play in VR** and the VR lobby | The manager, VPX's in-game menu during VR play, and you |
| 2. Desktop settings | `VrTableManager-Desktop.ini`, next to `VPinballX.exe` | **DESKTOP** play | The manager, VPX's in-game menu during desktop play, and you |
| 3. Table settings | `<table>.ini`, next to the table's `.vpx` | That one table | VPX, and the per-table render scale in the manager |

A higher layer wins. Anything a layer doesn't set comes from the layer below it.

## What this means in practice

- **Change something in your own `VPinballX.ini` and it applies everywhere.** Change a key binding or the sound volume in desktop VPX, and VR play picks it up too, unless the VR file has its own value for that setting.
- **The VR and desktop files only hold differences.** A setting with an empty value (`AAFactor = `) isn't an override, so yours is used. VPX writes empty values for anything left at its default, so most lines in these files are empty.
- **Changes made while playing are kept for that mode.** If you change a setting in VPX's in-game menu while playing in VR (sound, graphics, or lining the table up with the left thumbstick click), it goes into the VR file when the table closes. It won't affect desktop play or your own `VPinballX.ini`.
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

Some settings are best changed from the Setup tab instead, because the manager writes them before every launch. If you edit one of these by hand, your value is replaced at the next launch:

- `[PlayerVR] AskToTurnOn`
- `[PlayerVR] DisplayRefreshRate`
- `[PlayerVR] AddBackglass`
- `[Plugin.PinMAME]`, `[Plugin.Serum]`, `[Plugin.VNI]`, `[Plugin.AltSound]`, `[Plugin.PUP]`, `[Plugin.FlexDMD]` and `[Plugin.B2S]` `Enable`

## Behind the scenes

Before each launch the manager combines the layers into `VrTableManager-VR.play.ini` or `VrTableManager-Desktop.play.ini`, next to `VPinballX.exe`, and starts VPX with it (`-Ini`). When the table closes, the manager compares that file with what it started with. Any setting VPX changed is copied into the VR or desktop file. Values written differently but meaning the same (`1.5` and `1.500000`) don't count as changes.

The `.play.ini` files are rebuilt every launch, so editing them has no lasting effect.

## Controls

VR play uses the controls from your own `VPinballX.ini` until the VR file has mappings of its own. That happens when you remap a button during VR play, or when you click **USE MY DESKTOP VPX CONTROLS** on the Setup tab. That button merges your desktop mappings into the VR file and keeps the Quest controller buttons as alternatives.

## Backups

**BACK UP NOW** on the Setup tab includes the VR and desktop files, and the settings in VPX's preferences folder (`%APPDATA%\VPinballX`). A `VPinballX.ini` kept next to `VPinballX.exe` isn't included, so copy that one yourself.
