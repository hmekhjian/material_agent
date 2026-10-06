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
3. Agent resolver behind an `IMaterialResolver` interface: Anthropic Messages API with web search + web fetch tools, system prompt enforcing the evidence ladder, JSON-only output matching the schema (strip code fences before parsing). Wire it to the text box -> preview.
4. Preview polish: candidate image picker, confidence display, editable scale/mapping/grain, post-import re-scale tool.
5. Derived roughness/normal maps.

## Conventions
- Target Rhino 8: multi-target `net7.0` + `net48` (Windows/Mac). RhinoCommon and Eto via NuGet with `ExcludeAssets="runtime"`.
- Keep Rhino-dependent code (material creation, mapping) and UI separate from the agent/HTTP layer so the agent logic is unit-testable without Rhino.
- API key from the `ANTHROPIC_API_KEY` env var or Rhino settings. Never commit keys.
- Units: tile sizes in mm, converted to model units.
- Don't write RhinoCommon or Eto API calls from memory without checking them; build and test in Rhino as you go.
- License: MIT.

## Code layout
- `src/MaterialAgent/Core/`: no Rhino dependency (schema, provenance, image download, mapping maths). Unit tested in `tests/MaterialAgent.Tests`, which compiles these files directly because RhinoCommon cannot load outside Rhino.
- `src/MaterialAgent/RhinoSide/`: RhinoCommon code (material creation, mapping, reuse lookup). Runs on the UI thread.
- `src/MaterialAgent/UI/`: the Eto dockable panel.
- Eto and Rhino.UI ship inside the RhinoCommon 8 NuGet package, so there is no separate Eto package reference.
- Build: `dotnet build MaterialAgent.sln` (targets net7.0 + net48, outputs `MaterialAgent.rhp`). Tests: `dotnet test`.
