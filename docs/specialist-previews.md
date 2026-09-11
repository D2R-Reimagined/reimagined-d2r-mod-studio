# Specialist previews

Opening a supported asset shows a read-only preview in the usual temporary or pinned tab. Decoding runs off the UI thread. Closing or switching away releases its displayed bitmap; returning reloads the file. Use Reload after changes made outside Studio. No external tool or game executable is started.

| Format | Preview |
| --- | --- |
| `.sprite` | SpA1 atlas and individual horizontal frames; RGBA and supported BC compression |
| `.texture` | D2R texture mip selection; RGBA, BC1, BC3 and BC4; initial mip targets at most 1024 pixels when available |
| `.dds` | Base mip of a 2D surface using BCnEncoder; arrays, volumes and cubemaps are explicitly unsupported |
| `.dc6` | Version 6 uncompressed frame/direction selection; transparent pixels; optional 768-byte BGR `pal.dat` palette |
| `.ds1` | Version 16–18 top-down layer occupancy and dependency list; combined or individual wall/floor layers |
| PNG, JPEG, BMP, WebP, GIF | Static image / first animation frame |

DC6 starts with grayscale palette indices because its game palette is external. Load the appropriate palette for the act or UI; Studio does not bundle or guess one. The DS1 diagram shows occupied tiles and explicit unwalkable flags. It is **not** a textured level, a complete collision map, or a gameplay validation. DT1 dependencies are listed but not loaded.

RGBA/RGB/alpha channel views (textures default to RGB so material alpha does not hide their color), Fit and 100/200/400 percent zoom, dark/light/magenta transparency backgrounds, format details, and a hex toggle are available. Hex displays the first 4096 bytes with offsets. Unsupported variants and malformed assets report their limits while retaining hex inspection. Limits are 64 MiB per encoded asset and 16 megapixels per decoded image; oversized mip levels can be replaced by a smaller selected mip. No file is modified by previewing.

This pass does not implement DCC/DT1 art decoding, 3D model/animation rendering, particles/timelines simulation, audio/video playback, save-game inspection, external-tool handoffs, or integrated editing. Those formats retain their existing text/hex handling.

## Implementation references

The bounded D2R texture reader and DS1 layer layout were adapted from Reimagined Level Editor's MIT-licensed `TextureReader` and `Ds1CollisionDocument`; its notice is included in `licenses/D2RLevelEditor.txt`. Format references include [the SpA1 layout](https://github.com/dzik87/d2r-sprites/blob/main/d2r-sprites.py) and [OpenDiablo2's DC6 implementation](https://github.com/OpenDiablo2/OpenDiablo2/blob/master/d2common/d2fileformats/d2dc6/dc6.go). Studio's decoders are read-only implementations; no external editor is bundled. [BCnEncoder.NET](https://github.com/Nominom/BCnEncoder.NET) provides the managed block-compression decoder.

The core tests use synthetic pixel patterns and malformed headers. Optional local UI smoke arguments after the output directory name accept real assets for visual checks; those assets and screenshots must not be included in releases. The CLI `preview-info <file>` reports supported specialist metadata and decodes its initial view without modifying the file.
