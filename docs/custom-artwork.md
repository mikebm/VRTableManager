# Custom artwork, videos and sounds

VR Table Manager picks pictures, videos and sounds for every table by itself. You can replace any of them with your own, per table:

| What | Where it shows | Picked by default | Your own |
|---|---|---|---|
| Card picture | The table's card in **My Tables** | The catalog's (VPS) picture | `artwork\card.*` |
| Playfield picture | The cabinet's playfield in the VR lobby | The playfield inside the `.vpx`, else the catalog's picture | `artwork\playfield.*` |
| Backglass picture | The cabinet's backglass in the VR lobby | The table's own VR cabinet backglass, else the catalog's backglass | `artwork\backglass.*` |
| Playfield video | Moving playfield on the selected lobby cabinet | None until you get one | A media pack, a VPX recording, or your own video |
| Attract sound | Plays from the selected lobby cabinet | The table's ROM, recorded once | `music\attract.*` |

Your own files always win over what the app picks. Remove them and the defaults come back.

## Pictures

### From the app

1. Go to **My Tables**.
2. Open the table's **⋯** menu (or **MORE ▾** on Large cards) and choose **My artwork**.
3. Pick one:
   - **Card picture in My Tables…**
   - **VR lobby playfield picture…**
   - **VR lobby backglass picture…**
4. Choose a `.png`, `.jpg`, `.jpeg`, `.webp` or `.bmp` file.

The app copies your file into the table's `artwork` folder, so you can delete or move the original afterwards. A new card picture shows straight away. Lobby pictures show the next time the VR lobby opens.

**My artwork → Remove my pictures** deletes all three and puts the defaults back.

### By hand

**My artwork → Open the artwork folder** opens the right folder. Put files there named after what they're for:

```
<Tables>\<table folder>\
    artwork\
        card.jpg          the card in My Tables
        playfield.png     the lobby cabinet's playfield
        backglass.png     the lobby cabinet's backglass
```

- Any of `.png`, `.jpg`, `.jpeg`, `.webp` or `.bmp` works. If there are two (`card.png` and `card.jpg`), the newer one is used.
- Restart the app to see a card picture you added by hand.
- Tables without a folder of their own keep their pictures in `%APPDATA%\VrTableManager\artwork\<table name>-<code>\` instead. That covers Future Pinball, Pinball FX and Pinball M, and a `.vpx` sitting loose in your Tables folder. The **Open the artwork folder** menu item always opens the right place.

### Picture tips

- **Card:** portrait, about 3:4 (for example 600 × 800). The app crops it to fill the card, so keep the important part in the middle.
- **Playfield:** the playfield seen straight from above, top of the playfield at the top, about 1:2 (for example 1024 × 2048). The lobby shows it at up to 2048 pixels on the long side.
- **Backglass:** the backglass picture, landscape, about 4:3 (for example 1280 × 1024). It also replaces the backglass a table's own VR cabinet brings, which helps when that one is blank or drawn by the table's script.

## Playfield videos (VR lobby)

The selected cabinet in the VR lobby can play a moving playfield. VPX tables only. From the table's **⋯** menu:

- **Get a playfield video for the VR lobby** uses the playfield video from the table's media pack in the catalog. When there is none, it offers to record the attract mode in VPX instead: a small VPX window opens for about half a minute with the sound off, then closes by itself.
- **Record lobby video in VPX** records even when a media pack exists.
- **Use my own lobby video…** turns a video file you have into the lobby video. `.mp4` (H.264) works best; `.m4v`, `.mov`, `.wmv`, `.avi` and `.mkv` work when Windows can play them. The first 12 seconds loop. A sideways video is turned to match the table's playfield picture.
- **Remove lobby video** deletes it.

The video is stored as frames in `<table folder>\medias\lobby-video\`.

## Attract sound (VR lobby)

When you settle on a cabinet in the lobby, about 10 seconds of the table's music plays from its backbox. It comes from the first of these:

1. An `.ogg`, `.mp3` or `.wav` file in the table's folder or its `music` folder. Names containing "attract", "intro" or "theme" are picked first, so `music\attract.mp3` is the safest name. **Get attract audio for the VR lobby** in the table's **⋯** menu saves one there from the table's media pack.
2. The table's ROM: the first time you settle on a ROM table, the lobby records its start-up music once in the background.

To use your own, drop a file named `attract.mp3` (or `.ogg` / `.wav`) into the table's `music` folder. **Remove attract audio** in the **⋯** menu deletes the one there.

## Card size

The **SIZE** buttons next to **SORT** in My Tables switch between **Large**, **Medium**, **Small** and **Text** (no pictures). The app remembers your choice. Below Large, each card shows only **Play** and **⋯**; everything else is in the **⋯** menu.
