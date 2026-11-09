# Cabinet buttons and game controllers in the VR lobby

You don't need the Quest controllers to pick a table in the VR lobby. A pinball cabinet controller (PinOne, PinOne mini, Pinscape, Zeb's, DragonFly and the like) or an ordinary game controller (Xbox, PlayStation, Switch Pro) works too.

## Cabinet controllers

**Nothing to set up:** the lobby uses the buttons you set up in VPX. If a button works in a table, it works in the lobby.

| Cabinet button | VPX's default key | In the lobby |
|---|---|---|
| Left / right flipper | Left / Right Shift | Previous / next table. Hold to keep going; it speeds up. |
| Left / right magna-save | Left / Right Ctrl | Previous / next letter (or manufacturer, decade) |
| Start | 1 | Play the selected table |
| Launch ball | Enter | Play |
| Lockbar fire | Left Alt | Play |
| Coin | 5 | Next filter (All, VPX, Favourites…) |
| Extra ball / Buy-in | B | Random table |
| Exit Game | Esc (Q before VPX 10.8.1) | **Hold 1 second** to leave the lobby. A short press does nothing, so leaving a table with Exit can't close the lobby too. |

Favourites still need the Quest's right grip (or a game controller's X / Square).

The buttons come from the settings VR play uses:
- **VPX 10.8.1:** the control mappings in its in-game input settings. A joystick shows there as **SDLJoy** followed by a long code, its ID in VPX: Pinscape boards, PinOne in joystick mode and other USB game controllers all appear this way. Joystick buttons, and d-pad directions, count; analog axes such as a plunger or nudge don't.
- **Older VPX:** the keys and joystick buttons in its Keys dialog.

A button you never changed uses VPX's default key from the table above.

### Keyboard mode and joystick mode

Cabinet boards send their buttons either as **keyboard keys** or as **joystick buttons**. Many, PinOne included, can do either. Both work in the lobby, with one difference:

- **Keyboard mode:** key presses only reach the window that has focus. The lobby brings itself to the front when it starts and when you come back from a table, but Windows doesn't always allow that. If the buttons do nothing, click the **VR Pinball Lobby** window on the desktop once.
- **Joystick mode:** works whether or not the lobby window has focus.

## Game controllers (Xbox, PlayStation…)

Plug one in (USB or Bluetooth) and it works in the lobby, whatever VPX has set:

| Xbox | PlayStation | In the lobby |
|---|---|---|
| Left stick, d-pad ← → | Left stick, d-pad ← → | Previous / next table |
| Left stick, d-pad ↑ ↓ | Left stick, d-pad ↑ ↓ | Previous / next letter |
| LB / RB | L1 / R1 | Previous / next table |
| A, Start (Menu) | Cross, Options | Play |
| B | Circle | Next filter |
| X | Square | Favourite / unfavourite |
| Y | Triangle | Random table |
| Back (View) | Share | Hold 1 second to leave the lobby |

In a table, the controller does what you set up in VPX.

## If a button does nothing

1. **Close the lobby and open it again.** A controller plugged in while the lobby is open is picked up too, but this rules out anything left over.
2. **Look at the lobby's log.** It's the newest `godot*.log` in `%APPDATA%\Godot\app_userdata\VR Pinball Lobby\logs`. Near the top you'll find:
   - `Cabinet buttons: PrevTable: key 225, button 0; …; joysticks: 03008a6d…`: the buttons read from VPX. Keys are SDL key numbers (225 is Left Shift, 229 Right Shift, 30 the 1 key, 40 Enter, 41 Esc); `button 3` is a joystick button. `joysticks:` lists the boards VPX has buttons for, by their ID (the code after `SDLJoy_` in VPX's settings). If there are no `button` entries, VPX has no joystick buttons mapped.
   - `Joystick 0: mjrnet Pinscape Controller, GUID 03008a6d… (uses the VPX joystick mapping)`: every controller the lobby can see, with its ID. **Uses the VPX joystick mapping** means a cabinet board in joystick mode. **Game controller** means the layout above.
   - Each button you press: `Joystick 0 button 3`.
3. **What it means:**
   - **No `Joystick` lines when you press buttons:** the lobby isn't getting them. In keyboard mode, see the focus note above. Otherwise check the board works in VPX, and in Windows' **Set up USB game controllers**.
   - **Joystick presses show but nothing happens:** the button isn't mapped to a flipper, Start or similar in VPX, or it's mapped on another board. Compare the board's GUID with the ones after `joysticks:`. Map the button in VPX, then reopen the lobby.
   - **A button does the wrong thing:** compare the logged button number with the one VPX shows for it, and let us know both numbers and the board's name from the log.
   - **Your cabinet board shows as a game controller:** it gets the game controller layout above instead of your VPX buttons. Let us know the board's name and GUID from the log.

Joystick buttons only count on the board VPX has them mapped for, so a game pad or a second board left plugged in doesn't move the lobby. If none of the connected joysticks is one VPX names (older VPX doesn't name them), the mapped button numbers work from any joystick that isn't a game controller.
