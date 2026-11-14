# Custom artwork, videos and sounds

VR Table Manager picks pictures, videos and sounds for every table by itself. You can replace any of them with your own, per table:

| What | Where it shows | Picked by default | Your own |
|---|---|---|---|
| Card picture | The table's card in **My Tables** | The catalog's (VPS) picture | `artwork\card.*` |
| Playfield picture | The cabinet's playfield in the VR lobby | The playfield inside the `.vpx`, else the catalog's picture | `artwork\playfield.*` |
| Backglass picture | The cabinet's backglass in the VR lobby | The table's own VR cabinet backglass, else the picture in its installed `.directb2s`, else the backglass picture inside the `.vpx`, else the catalog's | `artwork\backglass.*` |
| Wheel art | The game's logo over the selected lobby cabinet | None until you get one (catalog wheel art or media pack) | `artwork\wheel.*` |
| Playfield video | Moving playfields on the lobby cabinets | None until you get one | A media pack, a VPX recording, or your own video |
| Loading video | A big screen in the VR lobby while the table starts | None until you get one | A media pack, or your own video (one table, or every table) |
| Attract sound | Plays from the selected lobby cabinet | A music file in the table's folder, else the table's ROM, recorded once | `music\attract.*` |

Your own files always win over what the app picks. Remove them and the defaults come back.

## Pictures

### From the app

1. Go to **My Tables**.
2. Open the table's **⋯** menu (or **MORE ▾** on Large cards) and choose **My artwork**.
3. Pick one:
   - **Card picture in My Tables…**
   - **VR lobby playfield picture…**
   - **VR lobby backglass picture…**
   - **VR lobby wheel art (the game's logo)…**
4. Choose a `.png`, `.jpg`, `.jpeg`, `.webp` or `.bmp` file.

The app copies your file into the table's `artwork` folder, so you can delete or move the original afterwards. A new card picture shows straight away. Lobby pictures show the next time the VR lobby opens.

**My artwork → Remove my pictures** deletes all of them and puts the defaults back.

### By hand

**My artwork → Open the artwork folder** opens the right folder. Put files there named after what they're for:

```
<Tables>\<table folder>\
    artwork\
        card.jpg          the card in My Tables
        playfield.png     the lobby cabinet's playfield
        backglass.png     the lobby cabinet's backglass
        wheel.png         the logo over the selected lobby cabinet
```

- Any of `.png`, `.jpg`, `.jpeg`, `.webp` or `.bmp` works. If there are two (`card.png` and `card.jpg`), the newer one is used.
- Press **RESCAN** in My Tables to see a card picture you added by hand.
- Tables without a folder of their own keep their pictures in `%APPDATA%\VrTableManager\artwork\<table name>-<code>\` instead. That covers Future Pinball, Pinball FX and Pinball M, and a `.vpx` sitting loose in your Tables folder. The **Open the artwork folder** menu item always opens the right place.

### Picture tips

- **Card:** portrait, about 3:4 (for example 600 × 800). The app crops it to fill the card, so keep the important part in the middle.
- **Playfield:** the playfield seen straight from above, top of the playfield at the top, about 1:2 (for example 1024 × 2048). The lobby shows it at up to 2048 pixels on the long side.
- **Backglass:** the backglass picture, landscape, about 4:3 (for example 1280 × 1024). It also replaces the backglass a table's own VR cabinet brings, which helps when that one is blank or drawn by the table's script.
- **Wheel art:** the game's logo on a transparent background (`.png`), landscape, like a front end's wheel image (for example 800 × 300). The lobby fits it into about 75 × 28 cm over the title.

If a table's own VR cabinet looks wrong in the lobby, its **⋯** menu → **My artwork** → **Use the generic cabinet in the VR lobby** stands it on the plain cabinet instead, still wearing its playfield and backglass. **Setup → VR lobby → Cabinet style** does this for every table.

## All the VR lobby media at once

A table's media pack in the catalog usually has its playfield video, loading video, music and wheel art. You can get all of them in one go instead of one menu item at a time. Only what's missing is added: nothing the table already has, or that you chose yourself, is replaced.

- **One table:** its **⋯** menu → **Get all VR lobby media**. Media packs are big, so it downloads one at most once and keeps every missing piece from it. When only the wheel art or music is missing, it tries the catalog's small wheel and sound files first, and often doesn't need a pack at all. If there's still no playfield video at the end, it offers to record one in VPX, once the table has its ROM. If there's still no music, a ROM table gets its music recorded from the ROM in the background.
- **Many tables:** **◆ GET LOBBY MEDIA** at the top of **My Tables** does the same for every table shown, one after another, so search or filter first if you only want some. You choose whether tables still without a playfield video get recorded in VPX afterwards, about half a minute each in a small window. ROM tables still without music get it recorded from their ROMs at the end, and the question says how long that takes. Tables missing their ROM are skipped.

  While it runs, the button turns into **■ STOP**, which ends the run. Cancelling one table's job in the Downloads list skips just that table. You can't launch the VR lobby while tables are being recorded.

Downloads from the table sites happen in your own browser: each download page opens, and you click **Download** there. Some sites make you wait on a timer first. The app keeps what it needs from each download and deletes the rest.

## Playfield videos (VR lobby)

With a lobby video, a cabinet's playfield moves in the VR lobby. Every cabinet you can see plays its own. VPX tables only. From the table's **⋯** menu:

- **Get a playfield video for the VR lobby** uses the playfield video from the table's media pack in the catalog. When there is none, it offers to record the attract mode in VPX instead: a small VPX window opens for about half a minute with the sound off, then closes by itself.
- **Record lobby video in VPX** records even when a media pack exists.
- **Use my own lobby video…** turns a video file you have into the lobby video. `.mp4` (H.264) works best; `.m4v`, `.mov`, `.wmv`, `.avi` and `.mkv` work when Windows can play them. The first 12 seconds loop. A sideways video is turned to match the table's playfield picture.
- **Remove lobby video** deletes it.

The video is stored as frames in `<table folder>\medias\lobby-video\`.

## Wheel art (VR lobby)

The selected cabinet in the VR lobby shows the game's logo over its title, the picture front ends such as PinUP Popper and PinballX show on their selection wheel. VPX tables in their own folder. From the table's **⋯** menu:

- **Get wheel art for the VR lobby** downloads the catalog's wheel art for the game, or its media pack when there's none, and keeps just the wheel picture. If the download has wheels for lots of games, it picks the one for this game.
- Getting a playfield video, attract audio or loading video from a media pack also keeps the pack's wheel, when the table has none yet.
- **Remove wheel art** deletes the downloaded one.

It's stored as `<table folder>\medias\(Wheel) <table folder>.png`. A picture of your own (**My artwork → VR lobby wheel art…**) wins over it, and works for every kind of table.

## Loading video (VR lobby)

While a table starts, the VR lobby's loading screen can play a video on a big screen where the cabinet stood, with its sound, like the loading videos front ends show on a cabinet. It loops if the table takes longer, and stops when the table takes over the headset.

- **Get a loading video for the VR lobby** (the table's **⋯** menu) uses the loading video from the table's media pack in the catalog (PinUP Popper's `Loading` video). VPX tables in their own folder.
- **Use my own loading video…** turns a video file you have into the table's loading video.
- **Setup → VR lobby → Starting tables → CHOOSE A VIDEO…** sets one loading video for every table that has none of its own, such as one of the generic loading videos front-end users share.
- **Remove loading video** (the **⋯** menu) or **REMOVE** (Setup) deletes it.

When you choose your own video, the app asks whether to turn it a quarter, for a video made sideways for a cabinet's screen. To turn one you already have, use **Rotate loading video…** in the table's **⋯** menu, or **ROTATE…** next to the one for every table on Setup. If the video you chose is still where it was, it's made again from that, so nothing's lost; otherwise its frames are turned.

The first 30 seconds are used, at the video's own shape, with its sound. `.mp4` (H.264) works best; `.m4v`, `.mov`, `.wmv`, `.avi`, `.mkv` and `.f4v` work when Windows can play them. A table's own is stored in `<table folder>\medias\loading-video\`, the one for every table in `%APPDATA%\VrTableManager\loading-video\`.

It plays only with **Show a loading screen in the VR lobby while a table starts** on. Turn off **Play a loading video on the loading screen** to keep the plain loading screen.

## Attract sound (VR lobby)

When you settle on a cabinet in the lobby, the table's music plays from its backbox, for 10 seconds unless you change **Music length** under **Setup → VR lobby → Music**. It comes from the first of these:

1. An `.ogg`, `.mp3` or `.wav` file in the table's folder or its `music` folder. Names containing "attract", "intro" or "theme" are picked first, so `music\attract.mp3` is the safest name. **Get attract audio for the VR lobby** in the table's **⋯** menu saves one there from the table's media pack.
2. The table's ROM: its start-up music, recorded once and kept as a small MP3. **GENERATE ATTRACT MUSIC** under **Setup → VR lobby → Music** records every ROM table without music, several at a time. GET LOBBY MEDIA, Get all VR lobby media and PREPARE TABLES can do it too. Any table you haven't recorded gets recorded by the lobby itself when you stop on it, unless you turn that off. [Attract music](vr-lobby.md#attract-music) has the details.

To use your own, drop a file named `attract.mp3` (or `.ogg` / `.wav`) into the table's `music` folder. **Remove attract audio** in the **⋯** menu deletes the one there.

## Card size

The **SIZE** buttons next to **SORT** in My Tables switch between **Large**, **Medium**, **Small** and **Text** (no pictures). The app remembers your choice. Below Large, each card shows only **Play** and **⋯**; everything else is in the **⋯** menu.
