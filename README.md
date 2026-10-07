# material_agent
Rhino 8 plugin that turns a real product name into an applied, correctly scaled material. An AI agent finds the texture on the product page, estimates real-world scale and mapping, and applies it to your objects.

## Use
1. Run `MatAgent` to open the dockable **Material Agent** panel.
2. First time: open the panel's **Settings** tab, paste a Gemini API key ([get one](https://aistudio.google.com/apikey)), press **Save**, then **Test key**. The key is remembered in Rhino's plug-in settings. (A `GEMINI_API_KEY` environment variable also works as a fallback, but isn't needed.)
3. Type a product name or code (e.g. `Egger H1145 ST10`) and press **Find**. If the document already has that product, the panel offers to reuse it instead.
4. Check the preview: product, page link, candidate images (click to swap), repeat size with its source and confidence, mapping, grain and finish. Correct anything.
5. Select objects and press **Import to selection**.
6. To adjust: tick **Live** and edit the size, use **Re-apply to selection**, or **Measure in viewport…** (`MatAgentRescale`): pick two points on a feature of the texture and type its real length.

You can skip the agent with **Use your own image** (URL or file).

**Pattern tab (brickwork, tiles, planks):** like Architextures' Create tool, but with the real product. Take the Material tab's image (or load one), choose a pattern (stretcher, third or quarter bond, stack, Flemish, English, header, basketweave, herringbone), unit size (prefilled from the product page for bricks), joint width, colour and depth, and variation. Units are filled with crops of the product texture at true scale (planks keep real grain) or with colours from the product photo (bricks; click a colour to leave it out). **Use this texture** sends it to the Material tab with its exact size and joint relief maps, ready to import.

**RAL tab:** a swatch grid of the built-in colours (filter by code or name), with finish and import. Typing a RAL code in the Material search jumps there.

**RAL colours (built in, offline, free):** type `RAL 9010`, `ral7016`, `RAL 210 50 15` (RAL Design), or a name such as `RAL anthracite grey`, and press **Find**. No web search or API key is used. You get a plain-colour material with the chosen finish (Satin by default for RAL Classic, Matt for RAL Design); pearl and aluminium colours (e.g. RAL 9006, 1035) get some metalness, and fluorescent ones (e.g. RAL 2005) are flagged because screens can't show them. Name searches that match several colours offer the others in a drop-down. All 215 RAL Classic and 1,825 RAL Design colours are included; values are sRGB screen approximations of the physical samples, so check a real RAL fan for colour-critical work.

**Import to layer…** sets the material on the layers you pick (Rhino's layer material), so everything on them using the layer material gets it, including objects you draw there later. Textured materials get real-world mapping on the current objects, and new objects added to (or moved onto) those layers are mapped automatically. Objects with their own object material keep it; the status line says how many.

**When no seamless texture exists:** tick **Preview tiled 2×2** to see seams. **Blend edges** (free) makes the selected image tile by blending its borders with a half-shifted copy of itself; good for swatches that almost tile. **Generate seamless (AI)** sends the selected image (plus up to two other matching images) to Gemini's image model, Nano Banana 2 (`gemini-3.1-flash-image`, set in Settings), asking for a flat, evenly lit, tileable texture covering the current width × height; code then blends the edges anyway, because image models don't reliably wrap. The generated image keeps the scale it was asked for, with confidence lowered one step, so check it (e.g. Measure in viewport). Roughly $0.05–0.10 per image at 1K. Settings can also generate one automatically when a search finds no tileable image (off by default).

**Enscape:** Enscape renders the standard material this plug-in creates (base colour, normal map, roughness) and respects its real-world texture mapping, so nothing extra is needed to render. Enscape's own texture scale multiplies on top; leave it at 1. *Experimental, unverified:* only if you want to edit the material in the Enscape Material Editor, tick **Create as Enscape material** to convert the new material to Enscape's material type (Rhino's "Change Type, copy similar settings"), so it shows in the Enscape Material Editor. The checkbox is disabled when Enscape isn't detected; if conversion fails you get a standard Rhino material and a warning. If detection fails, Settings → Enscape → **List material types** prints every material type ID to the command line; paste Enscape's ID there.

WebP images (what most product sites serve) are converted automatically when downloaded: lossy WebP to JPEG, lossless or transparent WebP to PNG. Provenance keeps the original WebP URL. AVIF is not supported yet.

## Cost
Default model is `gemini-flash-latest` (change it in the Settings tab; `gemini-flash-lite-latest` is cheaper and less careful). A lookup is two calls: a research call that uses Google Search and reads the product page, and a vision call over up to 6 candidate images. Expect a few cents or less per lookup. Google Search grounding includes a monthly free allowance, then is billed per search. The panel shows the tokens used after each lookup.

## What it does
- **Agent** (`Core/Agent`): Gemini with Google Search + URL context finds the product page and size evidence, and returns the schema in CLAUDE.md. Code downloads images (also harvesting them from the product page HTML, since models often guess image URLs), then a vision call ranks them, reads grain direction and counts planks/bricks/tiles. Scale follows the evidence ladder (page text, then counted features, then category prior) and is always kept proportional to the image so textures never stretch.
- **Material**: Rhino 8 Physically Based material with the image as base colour, roughness from the finish, and optional normal + roughness maps derived from the image. Texture mapping (box or planar) where one UV cycle is one real-world repeat, rotated so grain follows each object's long side. Undoable.
- **Provenance**: product code/name, manufacturer, page URL, image URL, fetch date, scale source/confidence, size, mapping, grain, finish, stored in material user strings and mirrored in the render material Notes. Used for reuse and later re-scales.

## Credits
RAL colour values: [ral-colors](https://github.com/ieskudero/ral-colors) by Ibon Eskudero (MIT), see `src/MaterialAgent/Core/Colors/RalData.LICENSE.txt`. RAL is a trademark of RAL gGmbH; this plug-in is not affiliated with RAL.

## Download (no .NET needed)
A pre-built package is in [`dist/`](dist/): `MaterialAgent-0.1.5-rhino8.zip`. On Windows, unblock the zip first (right-click > Properties > Unblock), extract it, then drag `MaterialAgent/MaterialAgent.rhp` onto Rhino 8 and run `MatAgent`. If Rhino runs on .NET Framework (`SetDotNetRuntime` shows NETFramework), use `MaterialAgent-netframework/` instead or switch Rhino to NETCore. `INSTALL.txt` inside has the details.

## Build
Requires the .NET 8 SDK (builds on Windows, macOS or Linux).

```
dotnet build MaterialAgent.sln -c Release
dotnet test
```

The plug-in is `src/MaterialAgent/bin/Release/net7.0/MaterialAgent.rhp` (Rhino 8 default runtime) or `.../net48/MaterialAgent.rhp` (Rhino 8 on Windows set to .NET Framework). Drag it onto Rhino, or install it with `PluginManager`, then run `MatAgent`.

## Not yet verified in Rhino
Everything compiles against RhinoCommon 8 and the Rhino-free parts are unit tested, but nothing has been run inside Rhino yet. Check:
- Texture bitmaps get embedded in the .3dm on save. Downloaded images and generated maps live in `%LOCALAPPDATA%\MaterialAgent\textures` only because Rhino textures reference files on disk.
- The image model ID: `gemini-3.1-flash-image` is from third-party write-ups of Nano Banana 2; if Google uses a different ID (e.g. with `-preview`), change it in Settings.
- Enscape conversion: whether detection finds Enscape's type, and which settings carry over (Enscape's docs don't say). Also whether Enscape uses the generated roughness *map* or only the roughness value from the standard material.
- The API key is stored in plain text in Rhino's plug-in settings file, like other Rhino settings.
- Material-table user strings survive Rhino's render-material sync (reuse also reads the Notes copy).
- Box mapping on non-square repeats: on one pair of side faces the repeat is height × height. Prefer planar for panels.
- The bundled SixLabors.ImageSharp (and, on the .NET Framework runtime, its System.* dependencies) load without version conflicts inside Rhino.
