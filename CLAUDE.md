# CLAUDE.md: project context

## Goal
A standalone Rhino 8 plugin (C# / RhinoCommon). The user types a real-world product spec code or title (e.g. `Egger H1145 ST10`) into a text box. An AI agent finds the product's texture on the web and works out real-world scale and mapping. The plugin shows a preview (texture thumbnail plus what was found), and on **Import** creates the material and applies it to the selected objects.

## Scope and platform
- **Rhino 8 only.** No Rhino 7 support. Use Rhino 8 APIs freely (including the `PhysicallyBased` material API).
- **Standalone plugin.** Nothing to do with Architools: separate repo, separate plugin, no shared code or dependencies.
- **Visual UI, not command line.** Eto.Forms, because the user needs to see the texture thumbnail before importing.

## UX flow
1. User opens the plugin UI (command, e.g. `MatAgent`, opens the panel).
2. Types a material spec code or title into a text box and hits search/resolve.
3. Loading state while the agent works, with the ability to cancel.
4. **Preview:** texture thumbnail, product name, product page URL (clickable), detected scale (width x height mm) with source and confidence, mapping type, grain axis, and the other candidate images if the agent found several.
5. User can correct scale and swap candidate image before committing.
6. **Import** button: creates the material in the document and applies it to the currently selected objects.

Open design detail: prefer a **modeless / dockable Eto panel** (`Rhino.UI.Panels.RegisterPanel`) over a modal dialog, so the user can keep selecting objects in the viewport while the panel is open. Fall back to a modeless form if the panel route is painful.

## Decisions already made (don't relitigate without good reason)
1. **No manufacturer-specific adapters.** The tool must be an all-encompassing material applier. An LLM agent (web search + web fetch + vision) handles any product.
2. **No external texture cache or library.** Products get discontinued, and the .3dm carries its own materials. Instead:
   - Write provenance into material user strings: product code, source URL, fetch date, scale source, scale confidence.
   - Before fetching, look in the open document for an existing material with the same product code or image URL and reuse it.
   - **TODO verify:** that Rhino actually embeds the texture bitmaps in the .3dm in our save flow (embedded-files behaviour). If not, handle embedding explicitly.
3. **Agent finds, code downloads.** The agent returns structured JSON (schema below) with ranked candidate image URLs. Deterministic C# code downloads the image (this is also what feeds the thumbnail preview). The agent never handles binary data.
4. **Scale uses an evidence ladder, and reports confidence:**
   1. Explicit dimensions stated on the page (high)
   2. A visible feature of known real size, measured in the image (medium), with the maths done in code
   3. Category prior only (low)

   Vision is reliable for: reading text, classifying the image (swatch vs room shot, tileable or not), category priors (brick ~215x65, oak plank 120-190mm wide, etc.), and grain direction. It is NOT reliable for absolute scale from a bare texture crop, or for precise pixel coordinates.
5. **Fast correction beats accuracy.** The preview shows scale source and confidence and lets the user edit the values before import. After import, offer a quick re-scale (live scale control and/or "pick two points + type a distance").
6. **Mapping type** (planar / box / per-face) is chosen by the agent from a small enum, based on material category and object geometry. User can override in the preview.

## Agent output schema (MaterialResolution)
```json
{
  "product":   { "name": "", "code": "", "manufacturer": "", "page_url": "" },
  "candidates": [
    { "url": "", "kind": "swatch|room|detail", "likely_tileable": true, "note": "" }
  ],
  "scale": {
    "width_mm": 0, "height_mm": 0,
    "source": "page_text|image_feature|category_prior",
    "confidence": "high|medium|low",
    "rationale": ""
  },
  "grain_axis": "horizontal|vertical|none",
  "mapping": "planar|box|per_face",
  "category": ""
}
```
Required: product.name, product.page_url, candidates, scale (all fields except rationale), mapping, category.

Optional additions (implemented): `scale.feature` `{ "name", "real_mm", "count_across", "axis": "width|height" }` for the image-feature rung (the model counts, code multiplies), and `finish` (`matt|satin|gloss|polished|textured`, drives roughness).

## Rhino-side approach
- Create a `Material`, set the texture as the diffuse/base colour, wrap in a `RenderMaterial`, add it to the document, and assign it to the selected objects.
- Mapping: `TextureMapping.CreateBoxMapping` / `CreatePlaneMapping` with interval extents equal to the real-world repeat size (mm converted via `RhinoMath.UnitScale`), so one UV cycle equals one texture repeat. Texture wrap = repeat.
- Grain: rotate the mapping plane about its normal so the grain aligns with the object's long axis.
- Rhino 8 `PhysicallyBased` API for roughness etc. (later).

## Known risks
- Product-page images are often not seamless/tileable, or are room shots. Prefer official texture/decor downloads when the agent finds them. The preview is where the user catches a bad pick.
- Scraping may breach site terms. Flag this in the UI and always keep the source URL in provenance.
- Product pages rarely have normal/roughness maps. Derive approximations from albedo, or use per-finish presets (later).
- The agent can pick the wrong image or a wrong product variant. Always show what it chose (product name, page URL, thumbnail) before applying.
- Agent calls are slow: UI must stay responsive (async, off the UI thread, cancellable).

## MVP order
1. Plugin skeleton + Eto panel with manual image URL/path + tile size inputs, thumbnail preview, and an Import button -> material + box mapping at real-world scale on the selection. (Proves the Rhino-side pipeline and the UI shell with no agent.)
2. In-document reuse via provenance user strings.
3. Agent resolver behind an `IMaterialResolver` interface: Gemini API (Flash, for cost) with the Google Search + URL context tools, system prompt enforcing the evidence ladder, JSON-only output matching the schema (structured output when the model supports it; strip code fences before parsing either way). Wire it to the text box -> preview.
4. Preview polish: candidate image picker, confidence display, editable scale/mapping/grain, post-import re-scale tool.
5. Derived roughness/normal maps.

## Conventions
- Target Rhino 8: multi-target `net7.0` + `net48` (Windows/Mac). RhinoCommon and Eto via NuGet with `ExcludeAssets="runtime"`.
- Keep Rhino-dependent code (material creation, mapping) and UI separate from the agent/HTTP layer so the agent logic is unit-testable without Rhino.
- Agent provider: Gemini (`gemini-flash-latest` by default, configurable in the panel). Chosen over Claude/Qwen for cost: Flash is cheap, multimodal, and search + page fetching are built into the API, so no separate search API is needed.
- API key entered in the panel's Settings tab (stored in Rhino plug-in settings); `GEMINI_API_KEY` / `GOOGLE_API_KEY` env var only as a fallback when nothing is saved. Never commit keys.
- WebP is converted at download time with SixLabors.ImageSharp 2.1.x (Apache-2.0; 3.x dropped .NET Framework and changed licence). Lossy → JPEG, lossless/alpha → PNG.
- Enscape (experimental, not yet tested in Rhino): optional conversion to Enscape's material type via `Rhino.Render.Utilities.ChangeContentType(rm, enscapeTypeId, harvestParameters: true)`. The type ID isn't published; `EnscapeSupport` detects it from `RenderContentType.GetAllAvailableTypes()` (internal name contains "enscape" and instantiates as a RenderMaterial), with a manual override in Settings. Falls back to the PBR material on failure. Per Enscape docs/forum: Enscape renders Rhino PBR materials directly and respects Rhino texture mapping (its own texture scale multiplies on top, default 1), so conversion only matters for editing in Enscape's editor; Enscape has no public material API.
- Seamless textures: `SeamlessTile` (ImageSharp, free, deterministic) blends edges with a half-offset copy; `SeamlessTextureGenerator` asks Gemini's image model (Nano Banana 2, `gemini-3.1-flash-image`, configurable) for a flat tileable texture from the found images, then always runs `SeamlessTile` on the result. On demand from the panel, or automatically when nothing tileable is found (setting, off by default: costs per image). Generated images carry provenance `ai-generated:<model>:<reference url>`, keep the requested scale with confidence lowered one step. This is the one place image bytes go to a model and come back; downloading is still done by code.
- RAL colours are built in (`Core/Colors`: `RalCatalog` + generated `RalData.g.cs`, 215 Classic + 1825 Design, from the MIT `ral-colors` npm package's `rgb` values, which match RAL's commonly published sRGB, not its `HEX` field). Queries starting with "RAL" never go to the agent; they make a plain-colour PBR material (no texture/mapping/maps), provenance `matagent.color`. Pearl/aluminium get metalness; fluorescent colours are flagged.
- Import to layer: sets `Layer.RenderMaterial` on chosen layers and maps objects that use the layer material; objects with their own material are left alone. `LayerAutoMapper` (enabled in OnLoad) maps objects later added to or moved onto such layers, deferred to Idle, only when they have no mapping on channel 1 yet.
- Candidates are textures only: after the vision check, room/perspective/"other" shots and images of a different product move to `ResolveResult.References` (never shown in the picker; used as references for AI seamless generation). Without a vision result only the research model's "room" labels can filter. Vision gets downscaled JPEGs (`ImagePrep.ForVision`, 768 px) and minimal thinking. Per-stage timings are shown in the status line.
- Eto layout: never put `null` in a `TableRow` unless a stretchy spacer is wanted; Eto scales null cells, which splits rows into equal-width columns. Tab contents go in `VerticalScroller`, which pins content width to the visible width; otherwise long wrapped labels widen the layout and push buttons off-screen.
- Mapping defaults to `MappingKind.Auto`, resolved per object by `MappingMath.ResolveAuto` (planar when the thinnest side is at most 15% of the middle one, else box): the agent can't see the geometry, so its suggestion is only shown in the tooltip.
- Reuse in the panel: with an image selected, only a material made from that same image counts as existing; product-code matching applies only when no image is loaded (RAL, "use existing" path). Otherwise a different/generated image would be silently ignored.
- Units: tile sizes in mm, converted to model units.
- Don't write RhinoCommon or Eto API calls from memory without checking them; build and test in Rhino as you go.
- License: MIT.

## Code layout
- `src/MaterialAgent/Core/`: no Rhino dependency. Unit tested in `tests/MaterialAgent.Tests`, which compiles these files directly because RhinoCommon cannot load outside Rhino.
  - `Agent/`: `IMaterialResolver`, `GeminiMaterialResolver` (pipeline below), `GeminiClient` (REST, text and image output), `Prompts`, `PageImageHarvester`, `JsonText`, `SeamlessTextureGenerator`.
  - `SeamlessTile`, `WebpConverter`, `ScaleLadder` (evidence ladder + keeping size proportional to image pixels), `Provenance`, `ImageFetcher`, `ImageFormat`, `MappingMath`, `Maps/SurfaceMaps` (normal/roughness from albedo).
- `src/MaterialAgent/Core/Patterns/`: Architextures-style pattern engine. `PatternLayout` (stretcher 1/2, 1/3, 1/4, stack, header, English, Flemish, basketweave, 90-degree herringbone; exact repeats; herringbone/basketweave adjust unit width so the pattern closes and report it), `PatternRenderer` (fills units with crops of the real product texture at true scale, mirrored rather than wrapped, or with the product photo's palette + high-pass surface detail; studio-white backgrounds cropped/ignored; joints recessed in a height map -> normal/roughness), `Palette` (k-means). Tests check every pattern tiles with no gaps/overlaps.
  - `BrickFaces.cs` (`BrickFaceExtractor`): brick product images are often photos of brickwork. Finds the mortar colour (k-means cluster forming long horizontal lines), splits courses at bed joints and bricks at perpends (mortar, or a narrow brightness valley for shadowed joints between dark bricks), cuts inset faces and rejects any with a joint inside. Fill mode `Faces` uses a random real face per unit (cropped to the unit's proportions, flipped, bilinear). The Pattern tab uses it automatically for brickwork photos, sets joint colour to the detected mortar, removes mortar from the palette and measures the photo's real width from the brick length.
- `src/MaterialAgent/Core/Bricks/BrickUnit.cs`: brick size returned by the analysis call; prefills the Pattern tab.
- `src/MaterialAgent/Core/Colors/`: `RalCatalog` (parsing "RAL 9010", "RAL 210 50 15", names), `RalData.g.cs` (generated data; regenerate, don't hand-edit).
- `src/MaterialAgent/RhinoSide/`: RhinoCommon code (PBR material creation, map baking via Eto bitmaps, mapping, provenance store, reuse lookup, settings, `LayerAutoMapper`). Runs on the UI thread.
- `src/MaterialAgent/Commands/`: `MatAgent` (opens the panel), `MatAgentRescale` (pick two points + type real length).
- `src/MaterialAgent/UI/`: the Eto dockable panel with tabs Material | Pattern (`PatternPage`) | RAL (`RalPage`, `SwatchGrid`) | Settings. Pattern sends its result to the Material tab (exact scale, prebaked normal/roughness via `CandidateImage.Maps`) so there is one import path; RAL imports via `MaterialAgentPanel.ImportColor`. Typing "RAL ..." in the Material search jumps to the RAL tab.

Resolver pipeline (rebuilt for speed/cost; the old single research call with URL context took 35-80 s and 30-47k tokens): (1) **locate**: search-only call on the fast model (`gemini-flash-lite-latest`, configurable as "Page finding"; falls back to the main model on 400/404) with Google Search, minimal thinking, small schema, returns the product page + up to 3 alternative pages; (2) **read**: code downloads the pages in parallel (`PageReader`: title, meta, JSON-LD, visible text; scripts/styles/repeated nav stripped, capped at 9k chars), harvests images (`PageImageHarvester`) and downloads them; (3) **analyse**: one call with no tools (minimal thinking) gets page text + downscaled images and returns classification, best texture, scale evidence, grain, finish, mapping and brick unit size; if the pages have almost no readable text (script-rendered), this call gets the URL context tool instead; (4) `ScaleLadder` decides the final size in code. Results are cached per session (30 min).
- Eto and Rhino.UI ship inside the RhinoCommon 8 NuGet package, so there is no separate Eto package reference.
- Build: `dotnet build MaterialAgent.sln` (targets net7.0 + net48, outputs `MaterialAgent.rhp`). Tests: `dotnet test`.
