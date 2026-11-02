# Third-party notices

VR Table Manager is MIT licensed (see [LICENSE](LICENSE)). It is built with, includes or downloads the following
third-party work, each under its own license.

## Libraries included in the release

| Component | License | Source |
| --- | --- | --- |
| Avalonia (Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter) | MIT | https://github.com/AvaloniaUI/Avalonia |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Playwright (with its bundled Node.js driver) | MIT | https://github.com/microsoft/playwright-dotnet |
| NAudio.Wasapi | MIT | https://github.com/naudio/NAudio |
| OpenMcdf | MPL-2.0 | https://github.com/ironfede/openmcdf |
| SharpCompress | MIT | https://github.com/adamhathcock/sharpcompress |
| SkiaSharp | MIT | https://github.com/mono/SkiaSharp |
| .NET runtime (the release is self-contained) | MIT | https://github.com/dotnet/runtime |

OpenMcdf is used unmodified. Its source code is available at the link above, under the
[Mozilla Public License 2.0](https://www.mozilla.org/MPL/2.0/).

## Fonts

Archivo, Big Shoulders Display, Doto and JetBrains Mono are under the SIL Open Font License 1.1. Their license texts
ship beside the fonts in `VrTableManager/Assets/Fonts/` and `VrLobby/fonts/`. Inter (through Avalonia.Fonts.Inter) is
also under the SIL Open Font License 1.1.

## Data downloaded at run time

- **Pinball Memory Maps** (https://github.com/tomlogic/pinball-memory-maps): this program makes use of content from the
  Pinball Memory Maps project. The maps are made available under the
  [Open Database License (ODbL) v1.0](http://opendatacommons.org/licenses/odbl/1.0/), with individual contents under the
  [Database Contents License (DbCL) v1.0](http://opendatacommons.org/licenses/dbcl/1.0/). The app downloads them
  unmodified into its data folder and uses them to read high scores; they aren't part of this repository or the release.
- **Virtual Pinball Spreadsheet** (https://virtualpinballspreadsheet.github.io/): the table catalog. The app doesn't host
  tables; downloads come from the sites the catalog links to, under their authors' terms.
- **The Pinball Primer** (https://pinballprimer.github.io/): rules pages are linked, not copied (the game list is cached
  to match tables to its pages).

## Programs the app downloads or starts

Visual Pinball X (GPL-3.0), PinMAME and its plugins, UEVR (MIT) and the Godot engine (MIT) are downloaded from their
official releases or found on your PC, and run as separate programs. They aren't included in this repository or the
release, and their own licenses apply.
