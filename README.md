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

WebP images (what most product sites serve) are converted automatically when downloaded: lossy WebP to JPEG, lossless or transparent WebP to PNG. Provenance keeps the original WebP URL. AVIF is not supported yet.

## Cost
Default model is `gemini-flash-latest` (change it in the Settings tab; `gemini-flash-lite-latest` is cheaper and less careful). A lookup is two calls: a research call that uses Google Search and reads the product page, and a vision call over up to 6 candidate images. Expect a few cents or less per lookup. Google Search grounding includes a monthly free allowance, then is billed per search. The panel shows the tokens used after each lookup.

## What it does
- **Agent** (`Core/Agent`): Gemini with Google Search + URL context finds the product page and size evidence, and returns the schema in CLAUDE.md. Code downloads images (also harvesting them from the product page HTML, since models often guess image URLs), then a vision call ranks them, reads grain direction and counts planks/bricks/tiles. Scale follows the evidence ladder (page text, then counted features, then category prior) and is always kept proportional to the image so textures never stretch.
- **Material**: Rhino 8 Physically Based material with the image as base colour, roughness from the finish, and optional normal + roughness maps derived from the image. Texture mapping (box or planar) where one UV cycle is one real-world repeat, rotated so grain follows each object's long side. Undoable.
- **Provenance**: product code/name, manufacturer, page URL, image URL, fetch date, scale source/confidence, size, mapping, grain, finish, stored in material user strings and mirrored in the render material Notes. Used for reuse and later re-scales.

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
- The derived normal map in the PBR bump slot is detected as a normal map (not treated as a height map).
- The API key is stored in plain text in Rhino's plug-in settings file, like other Rhino settings.
- Material-table user strings survive Rhino's render-material sync (reuse also reads the Notes copy).
- Box mapping on non-square repeats: on one pair of side faces the repeat is height × height. Prefer planar for panels.
- The bundled SixLabors.ImageSharp (and, on the .NET Framework runtime, its System.* dependencies) load without version conflicts inside Rhino.
