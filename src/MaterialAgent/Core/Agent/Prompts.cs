using System.Text;
using System.Text.Json.Nodes;

namespace MaterialAgent.Core.Agent
{
    /// <summary>Prompts and response schemas for the two model calls.</summary>
    public static class Prompts
    {
        public const string ResearchSystem = @"You are a materials researcher for an architectural 3D modelling tool.
Given a product name or spec code (laminate, veneer, tile, stone, brick, fabric, paint, metal...), find the manufacturer's product page and the best image to use as a repeating texture, and work out the real-world size that image covers.

Use Google Search to find the official product page (prefer the manufacturer's own site, then major distributors). Use URL context to read the page.

Images (""candidates""):
- Only list image URLs you actually saw in fetched page content or search results. Never invent or guess image URLs. If you saw none, return an empty list: the tool also reads images from page_url itself, so getting page_url right matters most.
- Rank best first. Prefer, in order: official decor/texture/swatch downloads, flat close-up swatches, detail shots. Room/installation photos last.
- kind: ""swatch"" (flat, fills the frame), ""detail"" (close-up but not flat or partly cropped), ""room"" (scene with furniture/perspective).
- likely_tileable: true only for flat swatches without borders, labels, shadows or perspective.

Scale = the real-world width and height (mm) that ONE copy of the swatch image covers. Use this evidence ladder and report which rung you used:
1. ""page_text"", confidence ""high"": the page states dimensions that the image represents, e.g. a tile image showing one 600x300 tile, a decor image that is stated to show the full 2800x2070 board, or a stated pattern repeat. Board, sheet or panel sizes do NOT count unless the image clearly shows the whole board.
2. ""image_feature"", confidence ""medium"": the image shows repeating features of known size (planks, bricks, tiles, slats, a stated pattern repeat). Fill scale.feature with the feature name, its real size in mm along the counted axis, and how many span the image along that axis. Also give width_mm/height_mm as your estimate; code recomputes them.
3. ""category_prior"", confidence ""low"": typical size for the category, e.g. wood decor swatches usually show 1000-2800 mm along the grain, brick 215x65 mm with 10 mm joints, oak planks 120-190 mm wide, stone/concrete swatches 600-1200 mm.
Never claim a higher rung than the evidence supports. Put the evidence in rationale (quote the page text if rung 1).

grain_axis: direction the grain or dominant pattern runs in the best image: ""horizontal"" (along the image width), ""vertical"", or ""none"".
mapping: ""planar"" for sheet goods applied to flat panels (laminate, veneer, wallpaper, fabric), ""box"" for solid or chunky materials (stone, brick, concrete, solid wood, tiles on volumes), ""per_face"" only if each face clearly needs its own projection.
finish: matt, satin, gloss, polished or textured, from the product's finish/surface description. Omit if unknown.
category: short, e.g. ""wood decor laminate"", ""porcelain tile"", ""brick"".

Reply with ONLY a JSON object, no prose, no code fences, matching:
{""product"":{""name"":"""",""code"":"""",""manufacturer"":"""",""page_url"":""""},
 ""candidates"":[{""url"":"""",""kind"":""swatch|room|detail"",""likely_tileable"":true,""note"":""""}],
 ""scale"":{""width_mm"":0,""height_mm"":0,""source"":""page_text|image_feature|category_prior"",""confidence"":""high|medium|low"",""rationale"":"""",
            ""feature"":{""name"":"""",""real_mm"":0,""count_across"":0,""axis"":""width|height""}},
 ""grain_axis"":""horizontal|vertical|none"",""mapping"":""planar|box|per_face"",""finish"":""matt|satin|gloss|polished|textured"",""category"":""""}
scale.feature is optional (omit unless source is image_feature). Up to 6 candidates.";

        public static string ResearchUser(string query) =>
            $"Product: {query.Trim()}\nFind it and return the JSON.";

        public static string Repair(string problems) =>
            "That reply could not be used: " + problems + "\nReturn the corrected JSON object only.";

        public const string VisionSystem = @"You check candidate texture images for an architectural 3D modelling tool. The images are numbered in the order given, starting at 0.
For each image decide:
- kind: ""swatch"" (flat, fills the frame, no perspective), ""detail"" (close-up but cropped, angled or with props), ""room"" (scene/installation photo), or ""other"" (logo, packaging, diagram, unrelated).
- likely_tileable: could it repeat as a texture without obvious borders, text, shadows or perspective?
- matches_product: does it look like the described product (colour, pattern, material)?
Then pick best_index: the image most usable as a repeating texture of THIS product (flat swatch first; a wrong-looking product is never best). Use -1 if none is usable.
For the best image only:
- grain_axis: ""horizontal"" if grain/dominant pattern runs along the image width, ""vertical"" along the height, ""none"" if no direction.
- feature: if the image shows repeating features of known real size (planks, bricks plus joint, tiles plus grout, slats), count how many span the image along one axis. Give name, real_mm (size of one feature including joint, along that axis; use the size given in the product info if any, otherwise a typical size), count_across (can be fractional), axis (""width"" or ""height""). Use null if there is nothing countable. Count, don't guess pixel positions.
Reply with ONLY JSON:
{""images"":[{""index"":0,""kind"":"""",""likely_tileable"":true,""matches_product"":true,""note"":""""}],""best_index"":0,""grain_axis"":""none"",""feature"":null}";

        public static string VisionUser(MaterialResolution r)
        {
            var sb = new StringBuilder();
            sb.Append("Product: ").Append(r.Product?.Name);
            if (!string.IsNullOrEmpty(r.Product?.Code)) sb.Append(" (").Append(r.Product.Code).Append(')');
            if (!string.IsNullOrEmpty(r.Product?.Manufacturer)) sb.Append(" by ").Append(r.Product.Manufacturer);
            sb.AppendLine();
            if (!string.IsNullOrEmpty(r.Category)) sb.Append("Category: ").AppendLine(r.Category);
            if (r.Scale != null && !string.IsNullOrEmpty(r.Scale.Rationale)) sb.Append("Size info from the product page: ").AppendLine(r.Scale.Rationale);
            sb.Append("Candidate images follow.");
            return sb.ToString();
        }

        /// <summary>JSON schema for structured output of the research call (Gemini responseJsonSchema).</summary>
        public static JsonNode ResearchSchema() => JsonNode.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""product"": { ""type"": ""object"", ""properties"": {
        ""name"": {""type"": ""string""}, ""code"": {""type"": ""string""},
        ""manufacturer"": {""type"": ""string""}, ""page_url"": {""type"": ""string""} },
      ""required"": [""name"", ""page_url""] },
    ""candidates"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
        ""url"": {""type"": ""string""}, ""kind"": {""type"": ""string"", ""enum"": [""swatch"", ""room"", ""detail""]},
        ""likely_tileable"": {""type"": ""boolean""}, ""note"": {""type"": ""string""} },
      ""required"": [""url"", ""kind"", ""likely_tileable""] } },
    ""scale"": { ""type"": ""object"", ""properties"": {
        ""width_mm"": {""type"": ""number""}, ""height_mm"": {""type"": ""number""},
        ""source"": {""type"": ""string"", ""enum"": [""page_text"", ""image_feature"", ""category_prior""]},
        ""confidence"": {""type"": ""string"", ""enum"": [""high"", ""medium"", ""low""]},
        ""rationale"": {""type"": ""string""},
        ""feature"": { ""type"": ""object"", ""properties"": {
            ""name"": {""type"": ""string""}, ""real_mm"": {""type"": ""number""},
            ""count_across"": {""type"": ""number""}, ""axis"": {""type"": ""string"", ""enum"": [""width"", ""height""]} } } },
      ""required"": [""width_mm"", ""height_mm"", ""source"", ""confidence""] },
    ""grain_axis"": {""type"": ""string"", ""enum"": [""horizontal"", ""vertical"", ""none""]},
    ""mapping"": {""type"": ""string"", ""enum"": [""planar"", ""box"", ""per_face""]},
    ""finish"": {""type"": ""string"", ""enum"": [""matt"", ""satin"", ""gloss"", ""polished"", ""textured""]},
    ""category"": {""type"": ""string""}
  },
  ""required"": [""product"", ""candidates"", ""scale"", ""mapping"", ""category""]
}");

        public static JsonNode VisionSchema() => JsonNode.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""images"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
        ""index"": {""type"": ""integer""}, ""kind"": {""type"": ""string"", ""enum"": [""swatch"", ""detail"", ""room"", ""other""]},
        ""likely_tileable"": {""type"": ""boolean""}, ""matches_product"": {""type"": ""boolean""}, ""note"": {""type"": ""string""} },
      ""required"": [""index"", ""kind"", ""likely_tileable"", ""matches_product""] } },
    ""best_index"": {""type"": ""integer""},
    ""grain_axis"": {""type"": ""string"", ""enum"": [""horizontal"", ""vertical"", ""none""]},
    ""feature"": { ""type"": [""object"", ""null""], ""properties"": {
        ""name"": {""type"": ""string""}, ""real_mm"": {""type"": ""number""},
        ""count_across"": {""type"": ""number""}, ""axis"": {""type"": ""string"", ""enum"": [""width"", ""height""]} } }
  },
  ""required"": [""images"", ""best_index""]
}");
    }
}
