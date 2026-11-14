# VR lobby: playfield depth and cabinet art

How the VR lobby turns a `.vpx` into what you see: a playfield with real depth, and a cabinet dressed in the table's own art. VPX has no field that says "this picture is the side art" or "this part is the back board", so all of this is worked out from names, shapes and where things stand. The code is in `VrTableManager.Core/Services/VpxCabinet.cs` and `VpxImages.cs` (reading the table) and `VrLobby/scripts/` (showing it).

Units: VPX measures in its own units (50 units = 1 1/16 inch, about 0.54 mm each). VPX space has x to the right, y towards the player and z up, with the playfield's back edge at `Top` and its front at `Bottom`. The lobby's space has x right, y up and z towards the player.

## 1. Reading the table

`VpxCabinet.Read` opens the compound file and reads the BIFF records of every primitive, VPX wall and VPX ramp: name, visibility, transform, picture, material, part group, and the decoded mesh (zlib-compressed vertex and index arrays). Walls become meshes of their sides. Collections (named groups the script switches together) and materials are read too, along with the playfield rectangle (`LEFT`/`TOP`/`RGHT`/`BOTM`) and its slope range (`SLOP`/`SLPX`).

Parts in a part group placed in cabinet or room space (`SpaceOf(group) != 0`) are never treated as playfield parts.

**Script-shown parts.** Many tables save parts hidden and show them from the script. A part that the script sets `.visible = 1` and never sets `.visible = 0` is treated as shown, but only if it stands round the playfield (inside its outline and up to 40 cm above it). Elvira's printed inner walls (`Cab_Black`) are found this way. Anything the script also hides somewhere is shown only in one mode or by an option, so it keeps the file's visibility.

## 2. Playfield height map (depth for the playfield video)

The lobby plays a flat top-down video of each playfield. To make ramps and toys stand up, it uses a height map of the playfield and a parallax shader.

### Building it: `VpxCabinet.HeightMap`

1. **What goes in**:
   - Every visible 3D primitive in playfield space. Cabinet, room, glass, reflection and shadow parts are left out by name (`HeightMapSkip`: pincab, cabinet, blade, side rails, lockdown bar, backbox, backglass, room, floor, glass, shadow…), as are VPW lightmap overlays (`LM_…`) and post bolts.
   - VPX's own ramps (`RampTriangles`): their floors and walls, built the way VPX builds them.
   - VPX walls are **not** included, so older tables built from walls come out mostly flat.
2. **Projection**: each vertex goes through the part's full VPX transform (scale, rotate, translate, position). x and y are then mapped onto a 384 × 768 grid covering the playfield rectangle, with row 0 at the back like the video. z is kept as height above the playfield.
3. **Pass 1, the tops** (`Raster` with no underside buffer): every triangle is scan-converted with barycentric weights at pixel centres, and each pixel keeps the **highest** z that covers it.
   - Triangles seen edge-on (a wall seen from above) mark the pixels they pass over with their top edge, but only when they are at most a couple of pixels wide in one direction.
   - Triangles entirely above 1.5 × the top limit (a backbox or topper) are skipped.
4. **Pass 2, the undersides**: the same triangles are drawn again. For each pixel this keeps the **lowest** z that still belongs to the top layer, meaning within `HeightMapLayer` (30 units, about 1.6 cm) of that pixel's top.
   - A ramp floor or plastic is a few millimetres thick, so the playfield shows under it.
   - A solid toy becomes a shell, dark underneath.
5. **Normalising**: both maps are divided by `HeightMapTop` (400 units, about 22 cm, roughly glass height) and clamped to 0–1. The underside is never allowed above the top.

### Storing it: `PlayfieldDepth.Load`

- The map is saved as an RGB PNG (red = tops, green = undersides) as `heights-v3.png` next to the table's lobby video frames. Re-recording the video therefore drops it too.
- If less than 3% of the playfield has anything on it (`Coverage < 0.03`), there's nothing worth showing. A `heights-v3.none` marker holding the table file's size and date stops it being read again until the table changes.
- The file is written to a temporary name and renamed into place, because the background warm-up and the selected table can build the same map at once.

### Showing it: the `PlayfieldDepth` shader

- The playfield quad is lifted to glass level. `DepthOf` sets the depth from glass level down to the playfield: the height that 99.95% of pixels stay under, so one tall back decoration doesn't sink the whole table.
- For each pixel, each eye's view ray is stepped 64 times from glass level down through the map, then refined with a 6-step binary search. It stops at the first point that is inside something: below a pixel's top and above its underside. If it hits nothing, it stops at the playfield.
- **Shading**, because the video only ever saw things from above:
  - tops are drawn slightly brightened;
  - sides (a ray that entered below the tallest neighbour, or a steep slope) are drawn dark;
  - playfield seen under a ramp or plastic is drawn in shadow.
- Four rays per pixel are averaged to smooth edges. Both eyes are traced separately (`EYE_OFFSET`), which gives real stereo depth and parallax as your head moves.

## 3. The table's own VR cabinet: `VpxCabinet.Load`

### Picking cabinet parts: `Pick`

1. **Candidates by name** (`LooksLikeCabinetPart`): pincab, cabinet, cab, backbox, backglass, lockdown, side rail, legs, coin door, grilles, speakers, DMD, buttons, blades, apron, plunger and so on, or "Cab" as a camel-case word (`TAFCabLeft`). Excluded:
   - room parts (floor, wall, poster, lamp…);
   - unlit copies (`…_off`, `…gioff`);
   - VLM lightmaps;
   - the playfield glass.
2. **VR cabinet collection**: if the table lists its cabinet in a collection named like `VRCab`, `VR_Cabinet`, `VRCabinet` or `VR_Table`, a hidden part outside it is taken as a desktop or cabinet-mode part and left out (blades always count). This only applies if the cabinet body survives the filter.
3. **The body anchors everything**: the biggest part named like a cabinet body (`cabinet`, `cab`, `cabmain`, `cab_body`). Candidates must lie within 60 cm of it.
   - Parts lying on the playfield itself must have a strong cabinet name (pincab, apron, blade, side rail, lockdown).
4. **Parts by place** (`ByPlace`): anything shown within 10 cm of the body's outline that isn't on the playfield, whatever its name. Very detailed parts (over 10,000 vertices) are skipped, and only one of each set of script-swapped copies is kept.
5. **The table's own walls** (`PlayfieldWalls`), found by shape. A simple, opaque, visible part (at most 2,000 vertices) counts if it is one of:
   - a side wall: runs along at least 80% of the playfield, reaches both sides, and stands up to about rail height;
   - a back board: stands across the back, within 10% of the length from the back edge (`BackZone`), reaches both sides, and is at rail height;
   - a low wooden board: an inch-high strip along one edge.

   Seen from above, a side wall must keep to the edges, so a guide wall running through the middle is rejected. A back board skips that test, since a flat upright picture such as Dungeons & Dragons's `Backdrop` covers nothing seen from above.
6. **Baked back board** (`BakedBackBoard`): VPW tables bake the board into a `BM_` mesh. If no back board was found, its upright triangles across the back become a part of their own.
7. **Back-board mounts** (`BackBoardMounts`): textured parts no more than 2 cm deep that hang on the face of a back board, within its width and height and from its front face to 2 cm in front of it. Board art often has black windows where these hang (Elvira's Monster Slide and Dead Heads).
8. **Cleanup**: unlit copies are dropped when a lit one was picked, and near-identical copies in the same place are dropped.

### After picking

- **Pictures the script sets** (`ScriptImages`): a part with no picture in the file takes the first plain `Part.image = "name"` in the script, unless the script can hide that part.
- **Back boards in the same place** (`DropHiddenBackBoards`):
  - An untextured board that a textured one covers is dropped.
  - A board shut wholly inside another is dropped.
  - A VR cabinet collection's board within 1 cm of the table's own art board is dropped, and the art stays (Elvira's dark `Pincab_Backwall` over its bats).
  - A board that only reaches past the art stays.
- **Edge parts** (`EdgePrims`): the table's other visible, textured parts that stand along the playfield's sides (outer eighth, at least 40% of its length) or across its back (back eighth, at least half its width), 2–40 cm tall, and that weren't considered as cabinet parts. Parts the cabinet pass dropped don't come back here. Edge parts are kept apart from the cabinet: they don't count towards its size or levelling, and the lobby uses them only as a source of wall art (below).
- **Placing it**:
  - Meshes are moved into lobby space.
  - Back boards whose art starts up to 3 cm above the playfield are stretched down to meet it.
  - The cabinet is tipped forward until its front and back legs stand on the same floor.
  - The playfield is sloped at the table's own `SLOP`/`SLPX` (falling back to the legs' slope).
  - The result is moved so the feet are at y = 0, it's centred across, and its front is at the lobby's front line.
- **Cache**: `VrLobby/scripts/CabinetCache.cs`, under `%APPDATA%\VrTableManager\vr-lobby\cabinets`. Its `Version` goes up whenever any of these rules changes, so every table is read again.

## 4. Dressing the default cabinet with a table's art

A table without its own VR cabinet, or with **Generic cabinet** chosen, is shown on the default cabinet: Medieval Madness's VR cabinet, shipped as `VrLobby/cabinet/default.cab` with its pictures blanked. `Cabinet.ShowReal(template, borrowed: true, artFrom: the table's own cabinet)` dresses it from what the table has.

### Backbox and backglass

Backglass picture, first found wins (`Main.BackglassFor`, `LobbyArt.BackglassFor`):

1. your own picture;
2. the installed `.directb2s`;
3. the table file's own backglass (`VpxImages.PickBackglass`): named like a backglass or translite, 0.9–1.9 wide per tall, at least 600 px, biggest wins. It's rejected if it's see-through (`ArtLoader.ClearShare` over 5%) or cut out in the middle (`HoleInMiddle`);
4. the catalog picture.

How it's shown:

- **Whole front**: a catalog picture, or anything under 1.18 wide per tall, is taken as a photo of the whole backbox front (glass plus speaker panel).
  - The template's grilles and DMD are hidden, so speakers aren't doubled.
  - The picture is stretched over the backbox's front opening.
  - A wide picture makes the backbox shorter, to between 75% and 100% of its height.
- **Glass only**: the picture goes in the backglass, and a `.directb2s` grille is cropped off.

### Body and blades: the community kit sheets

Many VR tables use the community PinCab kit, whose sheets are mapped the same way on every table: `PinCab_Cabinet` (square), `PinCab_Backbox` (1:2) and `PinCab_Blades`.

- A table's kit sheet is put on the template only if `RealCabinet.SameSheetLayout` agrees that the table's parts use the sheet like the template's do: for each face direction, the texture rectangles overlap by more than half. A sheet that only borrows the kit's names but is laid out differently (Fish Tales) would come out scrambled, so it's skipped.

### Inner walls above the playfield and the back panel

**The table's own walls, transplanted** (`RealCabinet.OwnWallsOn`), which both lists below use:

- Take every textured face from the table's cabinet and edge parts that stands upright (|normal.y| ≤ 0.5), lies inside the playfield's outline, and is from 6 cm below to 30 cm above the playfield.
- Sides: faces facing inwards, in the outer quarter of the width.
- Back: faces facing forwards, in the back 7.5% of the length.
- Each face keeps its own material and texture coordinates. It is mapped from the table's playfield onto the template's (across, along and up from each playfield), so it lands in the same place on the default cabinet.
- Sides count only if both walls have art along at least 70% of the length. The back counts only if its art spans at least 70% of the width and isn't black (average lightness over 0.025).

**Side walls**, first found wins:

1. the table's own walls, transplanted;
2. the kit blade sheet (`PinCab_Blades`), if it passes the layout test;
3. blade strips from the table file (`VpxImages.PickBlade`: named for a blade or side wall, long and narrow unless it names its side; lit versions win over unlit);
4. plain dark wood.

**Back panel**, first found wins:

1. the table file's back panel picture (`VpxImages.PickBackPanel`: named back panel / back wall / backboard, 1.8–4.5 wide per tall, at least 400 px; not a VR room's walls);
2. the table's own back board, transplanted;
3. its side art carried round the back, as on many real machines (`Cabinet.BackPanelArt`): its own blade strip, or the left wall's part of its kit sheet, cut out and turned with `SideWallStrip`;
4. plain dark wood.

The apron and the template's instruction cards are hidden, since they'd show Medieval Madness's.

### Glass

Every cabinet gets a glass sheet. On a VR cabinet it lies along the side rails' tops, from the front rail's top edge to the back one's (`RealCabinet.RailTops`), 8 cm longer than the playfield run, at the rails' slope.

## Caches

Everything slow is read once and cached under `%APPDATA%\VrTableManager\vr-lobby\`:

- `cabinets\` holds the parsed cabinets;
- `pictures\` holds the pictures extracted from table files;
- `attract-audio\` holds attract-mode sound.

**PREPARE TABLES** fills all of them ahead of time (`VrLobby/scripts/PrepareTables.cs`). Height maps sit with each table's lobby video, as above.
