# Installing tables and add-ons

This covers getting a table into VR Table Manager and adding its extras: the ROM, PuP pack, colour DMD, AltSound and backglass. Each part says what to click, then what the app actually does with the files, so if you already know your way around VPX you can see exactly where everything goes.

## Installing a table from the catalog

1. Go to **Get Tables** and find the table. Turn on the VR-ready filter if you only want tables that play in VR.
2. Click the table to see its versions. Each one lists its authors and what it includes (VR room, PuP support and so on).
3. Click **INSTALL** on the version you want.

Where the file is hosted decides what happens next:

- **GitHub and direct links** just download.
- **VPUniverse, VPForums and other community sites** want you to download yourself. The download page opens in your normal browser. Sign in if it asks, accept the site's terms and click Download. The app watches your Downloads folder and takes the file from there. If you saved it somewhere else, click **I SAVED IT…** on the job and point it at the file.

You can follow every install under **Setup → Downloads**. If you close the app halfway through, it offers to finish next time.

### What it does

- **A folder per table.** Each table gets its own folder in your tables folder, named `Name (Manufacturer Year)`, laid out the way VPX 10.8.1 expects:

  ```
  Medieval Madness (Williams 1997)\
      Medieval Madness.vpx
      Medieval Madness.directb2s     backglass, named to match the table
      pinmame\roms\                  the ROM
      pinmame\nvram\                 high scores and settings
      music\                         table music
      altsound\<rom>\                AltSound
      serum\<rom>\, vni\<rom>\       colour DMD
      pupvideos\<pack>\              PuP pack
      vrtm.json                      what the app installed, for updates
  ```

  This is VPX 10.8.1's per-table layout, so tables play the same if you open them in VPX yourself.
- **Only what belongs to the table is kept.** The download is unpacked, the table and its files go into the folder, and anything else in the archive is left out. Installers and `.bat` files in a download are never run.
- **`vrtm.json`** records which catalog entry and version the table came from, and which add-ons you installed. That's how updates work. You can ignore it, but don't delete it unless you want the app to forget where the table came from.
- **Downloads are tidied up.** By default the downloaded zip is deleted once it's installed. Untick **Delete downloaded files after installing** under **Setup → Downloads** to keep them. Files from an install that failed are always kept.

## Adding a table you already have

- **One table file:** **＋ ADD A TABLE FILE…** on My Tables takes a zip, `.vpx` or `.fpt`. The app guesses which catalog table it is from the file name and asks you to confirm, so it still gets its ROM, add-ons and updates. A table that isn't in the catalog installs under its own name.
- **A whole existing VPX setup:** **Setup → Visual Pinball X → COPY TABLES FROM ANOTHER VPX INSTALL…** copies its tables, with their ROMs, NVRAM, AltSound, colour DMDs and PuP packs, into the per-table layout above. The other install isn't touched.
- **Tables somewhere else:** **ADD TABLE FOLDER…** on the same page adds another folder, such as one on a second drive. My Tables and the VR lobby show the tables in all your folders. New installs go into the one marked **INSTALLS HERE**.

If the app matched a table to the wrong catalog entry, or didn't match it, use **Fix table match…** in its **⋯** menu. That only changes which catalog table it's linked to, so it gets the right ROM, add-ons, picture and updates. The table's files stay as they are.

## The ROM

Most tables based on a real machine need its ROM to run. The app gets it for you right after the table installs.

- **What it does:** a table loads exactly one ROM, named in its script (`Const cGameName = "mm_109c"`). The app reads that name out of the `.vpx`, so it gets the exact version the table wants, not just one for the right machine. It fetches it from the catalog's ROM link, usually VPForums, in your browser like a table download. The ROM is saved as `pinmame\roms\<rom>.zip` in the table's folder. If the download is a pack of several ROMs, the right one is taken out. Stern's free game code is included.
- **Parent ROMs:** some ROMs are "clones" that only hold the files that differ from another version. They need their parent set beside them, and the app gets that too.
- **ROM ▾ on a table card:**
  - **Get ROM** fetches it again, or for the first time if it was missing.
  - **Get all ROM versions** downloads every version on the ROM page, each under its own name. That's handy if you switch between versions in the script.
  - **Add a ROM zip you have…** copies in one you already have.
  - **Search VPUniverse for the ROM** shows up when the catalog has no download for it, and opens a search.

Tables without a ROM (original tables, most EMs) skip all this.

## Add-ons

Each table card has a menu for each kind of add-on the catalog has for that table. Choose one and it downloads and installs like a table. Each menu also has **Add a … you already have**, for an archive or unpacked folder you've got already. That's copied in, and your copy is left where it is.

Add-ons are recorded in `vrtm.json`, so when a newer version comes out you get an update for it, and updating the table keeps them.

### PuP packs

PinUP Player videos for the backglass, DMD and topper. In VR, VPX shows them on the cabinet.

- **What it does:** VPX 10.8.1's PinUP plugin reads packs from the table's own folder, so the pack goes in `pupvideos\<pack>\`. The app finds the actual pack folder anywhere in the download (the one with `screens.pup` and `playlists.pup`) and names it after what the table's script opens, so the script finds it. Only that folder is copied. Option `.bat` files and installers in the pack are never run.
- **Fix the PuP DMD for VR** in the **⋯** menu is for tables whose PuP DMD doesn't show on the VR cabinet in VPX 10.8.1. It switches the table's PuP display to VPX's Score View. The original table file is kept, and **Undo the PuP DMD fix** puts it back.

### Colour DMD

Serum and Pin2DMD colorizations turn the orange dot-matrix display into full colour. ROM tables only.

- **What it does:** they install where VPX 10.8.1's plugins look, named after the ROM the script runs: Serum files as `serum\<rom>\<rom>.cRZ`, Pin2DMD palettes in `vni\<rom>\`. The menu lists this ROM's colorizations first, Serum before Pin2DMD. Encrypted `.pac` files can't be read by VPX's plugin, so downloads that only have those are refused. Colorizations in older layouts (`altcolor\`, `pinmame\altcolor\`) are moved to the right place.

### AltSound

Remastered or re-orchestrated soundtracks that replace the ROM's own sound. ROM tables only.

- **What it does:** the pack goes in `altsound\<rom>\`. It works with `altsound.csv`, `g-sound.csv` and the older PinSound folders. Packs in the older `pinmame\altsound\` layout are moved there.

### Backglass

A `.directb2s` backglass, shown beside the table on the desktop, or in VR when VPX adds a backglass to the room.

- **What it does:** the file is copied beside the table as `<table name>.directb2s`, whatever the download calls it, because that's the name VPX's B2S plugin looks for. If an archive has several, the one already named like the table wins, otherwise the biggest, and the job tells you which it took. If a table update changes the table's file name, the backglass is renamed to match.

### Rules

**RULES ▾** opens The Pinball Primer's rules and strategy page for the real machine, and the catalog's instruction cards, in your browser. Nothing is installed.

## Turning the plugins on

The add-ons above need VPX's plugins switched on: PinMAME, Serum, VNI, AltSound, PinUP, FlexDMD and B2S. The app turns them on for VR and desktop play by default. Each group has a switch under **Setup → Playing tables**. They're set in the app's VR and desktop settings files, not your own `VPinballX.ini`. See [VPX settings for VR and desktop play](vpx-settings.md).

## Updates

The app checks for new versions every time it starts, or when you click **CHECK NOW** under **Setup → Updates**.

- **Tables:** when the catalog has a newer version of a table you installed, it shows an update. **SHOW THEM** on My Tables, or the **Updates** filter, lists them. Updating installs the new version into the same folder and removes the old `.vpx`, but keeps the ROM, NVRAM (your high scores), PuP pack, colour DMD, AltSound, backglass and the table's own settings. Its favourite star and play history carry over too.
- **Add-ons:** a newer PuP pack, colorization, AltSound or backglass shows up the same way. **UPDATE ADD-ONS** replaces the installed one.
- **Visual Pinball X and UEVR** are updated from their GitHub releases.

**Update all** does VPX and UEVR first, then every table with an update, one at a time.

## Removing a table

**Remove table…** in the **⋯** menu deletes the table's whole folder, ROM, add-ons and high scores included. It asks first, and it won't while a download or update for that table is still running.
