# Pinball FX and Pinball M

Zen's Pinball FX and Pinball M (now sold as Pinball FX Midnight) aren't VR games, but [UEVR](https://github.com/praydog/UEVR) can put them in your headset. VR Table Manager sets that up and launches them for you. Future Pinball is on the same Setup page, played in VR through BAM.

## Setting it up

1. Install Pinball FX or Pinball M from Steam or Epic. The app finds them by itself, and each shows up in My Tables as one entry.
2. Download UEVR and unzip it somewhere. **Setup → Other games → UEVR DOWNLOAD PAGE** takes you there.
3. On the same page, point **Folder containing UEVRInjector.exe** at that folder.

That's all you need. If you have a community UEVR profile for the game, **IMPORT PROFILE** installs it.

## Playing

**Play in VR** starts UEVR's injector, launches the game through Steam or Epic, and closes the injector when you quit. **Desktop** launches the game without UEVR. You pick the table inside the game, since Pinball FX can't be told which table to open.

A couple of things to know:

- **Use a gamepad.** Players report crashes with VR controllers in Pinball FX under UEVR, so the app turns the Quest controllers off for the game.
- **Use OpenXR.** UEVR's injector remembers whether you last picked OpenXR or OpenVR in its window, and the app can't change that. Players recommend OpenXR for these games. If your last run used OpenVR, the checklist before launching says so.

## Pinball settings for UEVR

Before each VR launch, from the app or the VR lobby, the app writes a handful of pinball-friendly settings into the game's UEVR profile:

- the table stays level instead of tilting with your head;
- menus stay in front of you, at a readable size and distance;
- the Quest controllers are off for the game, and UEVR's own menu only opens on a long thumbstick press, closed at start.

Nothing else in the profile changes, and your original profile is backed up the first time. Under **Setup → Other games**, each game has **RESET TO PINBALL DEFAULTS** and **RESTORE MY ORIGINAL**. Untick **Apply pinball settings to UEVR before each VR launch** to leave the profile alone completely, and untick **Show the 'before you start' checklist before launching** to skip the checklist.

## Updates

The app checks for new UEVR releases along with everything else, and **Update all** installs them over your UEVR folder. Game profiles live in `%APPDATA%\UnrealVRMod`, so an update doesn't touch them.
