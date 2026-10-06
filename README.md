# material_agent
Rhino 8 plugin that turns a real product name into an applied, correctly scaled material. An AI agent finds the texture on the product page, estimates real-world scale and mapping, and applies it to your objects.

## Status
MVP step 1 and 2 (no agent yet):

- `MatAgent` command opens a dockable **Material Agent** panel.
- Load a texture from an image URL or a local file; see a thumbnail, pixel size and format. Loading runs off the UI thread and can be cancelled.
- Enter product name, code, manufacturer and page URL (stored as provenance).
- Set the real-world repeat size in mm, mapping (box / planar), grain direction and an optional 90° turn.
- **Import to selection** creates a render material with the texture (wrap = repeat) and assigns it to selected surfaces, polysurfaces, extrusions, meshes and SubDs, with a texture mapping where one UV cycle equals one real-world repeat. Undoable.
- **Re-apply scale to selection** updates only the mapping, for quick correction after import.
- Provenance (product code, source URL, image URL, fetch date, scale source/confidence, size, mapping, grain) is written to the material's user strings and mirrored in the render material's Notes. If the document already has a material for the same product code or image URL, the panel offers to reuse it.
- The product search box is in place but disabled until the agent lands.

## Build
Requires the .NET 8 SDK (builds on Windows, macOS or Linux).

```
dotnet build MaterialAgent.sln -c Release
dotnet test
```

The plug-in is `src/MaterialAgent/bin/Release/net7.0/MaterialAgent.rhp` (Rhino 8 default runtime) or `.../net48/MaterialAgent.rhp` (Rhino 8 on Windows set to .NET Framework). Drag it onto Rhino, or install it with `PluginManager`, then run `MatAgent`.

## Open items to check in Rhino
- Whether texture bitmaps get embedded in the .3dm on save. Downloaded images are written to `%LOCALAPPDATA%/MaterialAgent/textures` (`~/.local/share/...` on macOS) only because Rhino textures reference a file on disk.
- Whether material-table user strings survive Rhino's render-material sync. Reuse lookup also reads the Notes copy, so it keeps working either way.
- Box mapping on non-square repeats: side faces parallel to world X/Y share the plane's U/V sizes, so on one pair of sides the repeat is width × height and on the other it is height × height. Use planar mapping for panels.
- WebP images are rejected for now (convert to PNG/JPEG).
