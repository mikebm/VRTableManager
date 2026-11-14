# VR Table Manager

**Virtual pinball in VR, without the setup headaches.** Pick a table from the 2,000+ in the Virtual Pinball Spreadsheet, click Install, and a few minutes later you're playing it in your headset. VR Table Manager downloads the table, finds its ROM, adds the extras (PuP videos, colour DMDs, remastered sound), keeps it all up to date and launches it in VR. When you're done, walk over to the next one in a 3D arcade lobby without taking the headset off.

![My Tables: your collection, with updates, add-ons and one-click Play in VR](docs/screenshots/my-tables.jpg)

![The VR lobby: your tables as 3D cabinets, picked from inside the headset](VrLobby/screenshots/screenshot.jpg)

It's built for Meta Quest (Link, Air Link or Virtual Desktop) with the official Visual Pinball X 10.8.1 VR build. Future Pinball, Pinball FX and Pinball M work too.

## Features

### Getting tables
- **The whole catalog.** Every VPX and Future Pinball table in the [Virtual Pinball Spreadsheet](https://virtualpinballspreadsheet.github.io/), with its versions, authors and features. Filter by VR-ready, maker, era, type and theme.
- **One-click install.** For VPUniverse, VPForums and the other community sites, the table's page opens in your own browser, so you see the site and accept its terms yourself. Click Download there and the app picks the file up from your Downloads folder and does the rest. GitHub and direct links just download. Installs queue up, and if you close the app mid-download it offers to finish them next time.
- **Already have the file?** **ADD A TABLE FILE…** in My Tables installs a zip, `.vpx` or `.fpt` you already downloaded. The app works out which catalog table it is from the file name and you confirm, so it still gets its ROM, add-ons and updates.
- **Ready to play.** Each table gets its own folder, laid out the way VPX 10.8.1 expects. The app reads the table's script to find the exact ROM it needs and fetches it, Stern's free game code included, along with any parent ROM it depends on.
- **Search that forgives you.** Search matches the name, maker, author, theme, year or ROM, with words in any order. Typos are fine ("adams famly"), initials work ("afm", "tz", "lotr"), and "90s" or "1992" finds machines from then.
- **A–Z rail** beside the cards, like the one in the VR lobby. Click a letter to jump there.

### Making every table look and sound its best
- **PuP packs:** PinUP Player videos on the backglass and DMD, shown in VR.
- **Colour DMDs:** Serum and Pin2DMD colorizations turn the orange dot-matrix display into full colour.
- **AltSound:** remastered soundtracks that replace the ROM's original sound.
- **Backglasses:** `.directb2s` backglasses from the catalog.
- **Rules:** The Pinball Primer's rules and strategy guide, and the table's instruction cards.
- **Your own artwork:** use your own card picture, lobby playfield, backglass, wheel art, videos and music for any table. See [Custom artwork, videos and sounds](docs/custom-artwork.md).

Add-ons are matched to the table's ROM and installed where VPX's plugins look. They're tracked, so a newer version shows up as an update. Already have a pack? Each add-on menu has **Add a … you already have** for its archive or unpacked folder.

### Staying up to date
- Every time the app starts, it checks for new versions of your tables, their add-ons, Visual Pinball X itself and UEVR. **SHOW THEM** (or the **Updates** filter) lists the tables with a newer version. Update one, or all of them with one button.
- Updating a table keeps its ROMs, high scores, settings, favourite star and play history.

### Playing in VR
- **Play in VR** launches the table in your headset. The app gets out of the way and comes back when you quit.
- **The VR lobby.** Your tables stand around you as 3D pinball cabinets in an arcade. Each one stands in its own VR cabinet when the table has one, or on a generic cabinet dressed in the table's art. Playfields move with their lobby videos, and the selected one plays a bit of its music. Pull the trigger and you're playing; quit the table and you're back in the lobby on the same table. It works with your cabinet's buttons and DOF effects, or a game controller. See [The VR lobby](docs/vr-lobby.md).
- **Lobby media in one go.** **GET LOBBY MEDIA** fetches playfield videos, loading videos, music and wheel art for every table shown, from the community media packs and the catalog.
- **Favourites, play history and high scores**, shared between the app and the lobby. Grand champion scores are read straight from each table's saved memory.
- **Collections.** Group tables however you like ("Party night", "Learning the rules"), or save a search as a smart collection that keeps itself up to date. Ready-made ones include Never played, New this month and Forgotten favourites. They're filters in the VR lobby too.
- **Pinball FX and Pinball M** launch in VR through UEVR, with pinball-tuned VR settings. See [Pinball FX and Pinball M](docs/pinball-fx-and-pinball-m.md).

### Setup
- Installs the official VPX VR build, checks your OpenXR runtime (Meta Quest Link, Virtual Desktop, SteamVR), and sets the refresh rate, spacewarp and graphics quality. Rather tune VPX yourself? Switch the VR graphics off and VR play uses your own settings.
- **FIT VR TO ME** lines every table up the same way, at the right height and real size, so you're not moving each one around in the headset.
- Set VPX's keys and buttons on the **Controls** page, for desktop and VR or just one of them.
- Your own `VPinballX.ini` stays as it is, with separate VR and desktop settings on top. See [VPX settings for VR and desktop play](docs/vpx-settings.md).
- Copies the tables of another VPX install (with their ROMs, NVRAM, AltSound, AltColor and PuP packs) into the per-table layout, leaving the other install alone.
- Tells you what went wrong when a table crashes or exits early, from VPX's own log.
- Shows what's using your disk, sets render scale per table, clears caches, and backs up your settings and play history.

## Screenshots

| | |
|---|---|
| ![Table cards with PUP, COLOUR DMD, ALTSOUND and RULES menus, and the PUP menu open](docs/screenshots/table-cards.jpg) | ![Get Tables: the whole Virtual Pinball Spreadsheet catalog](docs/screenshots/get-tables.jpg) |
| **Add-ons and rules** on every table card, with Play and a ⋯ menu | **Get Tables:** 2,000+ tables, filtered to what works in VR |
| ![A table's versions, authors and features, each with Install](docs/screenshots/table-versions.jpg) | ![Setup: installing Visual Pinball X, your table folders and the layered VPX settings](docs/screenshots/setup.jpg) |
| **Every version** of a table, with one-click Install | **Setup:** installing VPX, your table folders, and your own VPinballX.ini with VR and desktop settings on top |

| | |
|---|---|
| ![Attack from Mars in the VR lobby](VrLobby/screenshots/screenshot-afm.jpg) | ![Total Nuclear Annihilation in the VR lobby](VrLobby/screenshots/screenshot-tna.jpg) |
| **The VR lobby:** each table in its own cabinet, with its high scores overhead | The arcade room, with a wheel at your feet to jump between makers or letters |

## Getting started

1. Download the latest `VrTableManager-<version>-win-x64.zip` from [Releases](https://github.com/mikebm/VRTableManager/releases), unzip it anywhere and run `VrTableManager.exe`.
2. On **Setup**, install Visual Pinball X with one click, and make sure your headset's OpenXR runtime is active (Meta Quest Link or Virtual Desktop).
3. On **Get Tables**, pick a VR-ready table and click **Install**. Its page opens in your browser: sign in if it asks, accept the site's terms and click Download. The app takes it from there.
4. Put your headset on and hit **Play in VR**, or **Launch VR Lobby** to browse your collection from inside the headset.

You need Windows 10 or 11 (64-bit) and a web browser. Nothing else to install: the VR lobby is in the zip, ready to run. [Installing tables and add-ons](docs/installing-tables.md) goes through it all in more detail.

## Guides

- [Installing tables and add-ons](docs/installing-tables.md): installing, ROMs, PuP packs, colour DMDs, AltSound and backglasses, and where each one goes
- [The VR lobby](docs/vr-lobby.md): controls, getting around, cabinets, videos and music
- [Custom artwork, videos and sounds](docs/custom-artwork.md): your own pictures, videos and music for any table
- [VPX settings for VR and desktop play](docs/vpx-settings.md): your own VPinballX.ini, VR and desktop settings, FIT VR TO ME and controls
- [Cabinet buttons and game controllers](docs/cabinet-buttons.md): PinOne, Pinscape, Xbox and PlayStation controllers in the lobby, and DOF
- [Pinball FX and Pinball M](docs/pinball-fx-and-pinball-m.md): playing them in VR through UEVR
- [Launching a table from the command line](docs/command-line.md): `VrTableManager.exe --play`, for shortcuts and other front ends

Want to build it yourself or help out? See [DEVELOPING.md](DEVELOPING.md).

## Credits

- Table data from the [Virtual Pinball Spreadsheet](https://virtualpinballspreadsheet.github.io/).
- High scores are read with maps from the [Pinball Memory Maps](https://github.com/tomlogic/pinball-memory-maps) project. This program makes use of content from the Pinball Memory Maps project, available under the [Open Database License (ODbL) v1.0](http://opendatacommons.org/licenses/odbl/1.0/).
- Rules and strategy pages from [The Pinball Primer](https://pinballprimer.github.io/).

## License

VR Table Manager is released under the [MIT License](LICENSE). The libraries, fonts and data it uses are under their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
