# The VR lobby

The VR lobby is where you pick a table without taking your headset off. Your tables stand around you as pinball cabinets in an arcade, each with its real playfield and backglass. The one you've picked steps up in front of you under a spotlight. Pull the trigger and it plays. When you quit the table, you're back in the lobby on the same table.

Start it with **LAUNCH VR LOBBY** on My Tables. Without a headset it opens in a normal window, with keyboard controls.

## Controls

| Quest controllers | Keyboard | What it does |
|---|---|---|
| Stick left / right (either hand) | ← → | Previous / next table. Hold to keep going; it speeds up after a second. |
| Stick up / down (either hand) | ↑ ↓, Page Up / Page Down | Previous / next letter (or maker, or decade) |
| Point at a cabinet | | Picks it |
| Point at a letter on the rail, then trigger | Type the letter | Jumps to that letter |
| Trigger or A | Enter | Plays the table |
| Y | Ctrl+R or ? | A random table |
| B | Tab | Next filter |
| Right grip | F | Favourite / unfavourite (a ★ shows over the cabinet) |
| Hold left grip, left stick up / down | + / − | Raise / lower yourself |
| Hold left grip, click left stick | Home | Reset your height |
| Hold Menu for a second | Esc | Leave the lobby |

You can also use your cabinet's flipper buttons or a game controller. See [Cabinet buttons and game controllers](cabinet-buttons.md).

## Getting around a big collection

Say you want the M tables in a 100-table collection:

- Point the right-hand laser at **M** on the rail at your knees and pull the trigger. That's the quickest.
- Or push the stick down to jump a letter at a time. Hold it and the jumps speed up, so # to M takes about two seconds. A big letter flashes so you know where you are.
- On the keyboard, just press **M**.

Empty letters are greyed out and skipped. Tables starting with "The" go under their second word, so The Getaway is under G.

## Filters

Press **B** (Tab on the keyboard) to go through them: All, VPX, Future Pinball, Pinball FX, Pinball M, Recently played, Most played, Favourites, Manufacturer and Decade. After those come your own collections and smart collections from VR Table Manager.

- **Manufacturer** and **Decade** group the tables, and the rail shows Bally, Gottlieb, Stern… or 1970s, 1980s… instead of letters.
- **Recently played** is the last 30 tables you launched. **Most played** is your top 30.
- **Pinball FX** and **Pinball M** each show up as one cabinet when the game's installed. You pick the table inside the game.
- Collections you add or change in VR Table Manager show up in the lobby while it's open.
- An empty filter tells you why, like "No plays counted yet".

Favourites, play counts and play time are shared with VR Table Manager. Star a table in one and it's starred in the other.

## The cabinets

A VR-ready table usually has its own VR cabinet built into its file, with the real side art, backbox, lockdown bar, legs and coin door. The lobby stands the table in that cabinet. Tables without one (desktop-only tables, Future Pinball, Pinball FX and M) stand on a generic cabinet that wears the table's own art: its cabinet art, side walls, back panel and backglass where the table file has them.

**Setup → VR lobby** has the options:

- **Cabinet style:** each table's own cabinet when it has one, or always the generic cabinet. If just one table's own cabinet looks wrong, its **⋯** menu → **My artwork** → **Use the generic cabinet in the VR lobby** does it for that table alone.
- **Cabinet size:** 100% is a real machine. Smaller cabinets leave you more room.
- **Cabinets in the ring:** the one you've picked and as many either side. More cabinets stand closer together round a bigger ring, and each one costs the headset a little frame rate.

The lobby reads these when it starts.

### Prepare tables

The first time the lobby shows a table, it reads the table's file for its cabinet, playfield and pictures. That takes a few seconds a table, longer on a hard drive, so cabinets can pop in as you turn the ring. **Setup → VR lobby → PREPARE TABLES** reads them all ahead of time, in the background, and you can follow it under Downloads. A big collection on a hard drive can take an hour or more the first time. After that, only new or updated tables need reading.

## Videos and music

- **Playfield videos:** with a lobby video, a cabinet's playfield moves, ramps and all. Every cabinet you can see plays its video, so the ones you turn towards are already moving. Get them from a table's **⋯** menu, or for everything at once with **GET LOBBY MEDIA** on My Tables. Turn them off under **Setup → VR lobby** if your PC or drive struggles.
- **Wheel art:** the game's logo floats over the selected cabinet's title.
- **Music:** when you stop on a table, about 10 seconds of its music plays from the backbox. It comes from the table's media pack or a music file in its folder. If there's neither, the lobby records the start-up music from the table's ROM the first time you stop on it (about 50 seconds, in the background; your scores and settings aren't touched). You can turn the recording off under **Setup → VR lobby → Music**.

[Custom artwork, videos and sounds](custom-artwork.md) covers getting these, and using your own.

## Starting a table

When you pick a table, the lobby stays in the headset while it loads, showing the table's name and a timer, or its loading video if it has one. If the table fails to start, you're back in the ring with the reason.

While you play, the lobby waits hidden behind the table, so when you quit, it's back straight away on the table you just played.

**Setup → VR lobby → Starting tables** has the switches:

- **Show a loading screen in the VR lobby while a table starts.** Turn it off if tables won't open in VR while the lobby is waiting. Some OpenXR runtimes only allow one VR app at a time.
- **Keep the VR lobby open behind the table, for a quick return.** Turn it off if a big table runs short of graphics memory.
- **Play a loading video on the loading screen**, and a loading video for every table that doesn't have its own.

## If your height looks wrong

The lobby puts the floor where your Quest's boundary says it is. If the cabinets feel too tall or too short, hold the **left grip** and push the **left stick** up or down. It moves about 30 cm a second, up to 60 cm either way, and shows how far you've moved. Hold the left grip and click the left stick to reset it. The lobby remembers your height for next time.

If you start facing the wrong way or inside a cabinet, face where you want and recenter: hold the Oculus button, or use VPX's **Align VR view** control (keypad 5, or whatever button you mapped it to). Recentering doesn't change your height.

## Cabinet feedback (DOF)

If your cabinet has DirectOutput set up, the lobby can fire its effects as you browse, the same way PinUP Popper's menu does. Turn it on under **Setup → VR lobby → Cabinet feedback (DOF)**. See [Cabinet buttons and game controllers](cabinet-buttons.md#cabinet-feedback-dof) for the details.
