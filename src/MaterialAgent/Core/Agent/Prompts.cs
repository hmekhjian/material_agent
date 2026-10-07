using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;

namespace MaterialAgent.Core.Agent
{
    /// <summary>Prompts and response schemas for the two model calls: locate (search only) and analyse (no tools).</summary>
    public static class Prompts
    {
        // ------------------------------------------------------------------ 1. locate

        public const string LocateSystem = @"You find product pages for building materials (laminate, veneer, tile, stone, brick, fabric, paint, metal...).
Use Google Search. Return the official manufacturer product page for the exact product and variant, plus up to 3 other pages about the SAME product that are likely to show a clear texture/swatch image or its dimensions (official texture/decor download pages, major distributors or merchants). Prefer pages, not PDFs.
Only return URLs that appeared in your search results. Do not guess URLs.
Reply with ONLY JSON:
{""product"":{""name"":"""",""code"":"""",""manufacturer"":"""",""page_url"":""""},""other_pages"":[""""],""category"":""""}
category: short, e.g. ""wood decor laminate"", ""porcelain tile"", ""clay facing brick"".";

        public static string LocateUser(string query) => $"Product: {query.Trim()}";

        public static JsonNode LocateSchema() => JsonNode.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""product"": { ""type"": ""object"", ""properties"": {
        ""name"": {""type"": ""string""}, ""code"": {""type"": ""string""},
        ""manufacturer"": {""type"": ""string""}, ""page_url"": {""type"": ""string""} },
      ""required"": [""name"", ""page_url""] },
    ""other_pages"": { ""type"": ""array"", ""items"": {""type"": ""string""} },
    ""category"": {""type"": ""string""}
  },
  ""required"": [""product"", ""category""]
}");

        // ------------------------------------------------------------------ 2. analyse

        public const string AnalyseSystem = @"You prepare a real-world material for an architectural 3D modelling tool. You get the text of the product's web pages and numbered candidate images downloaded from them (starting at 0).

Images: for each, decide
- kind: ""swatch"" (flat, fills the frame, no perspective), ""detail"" (close-up but cropped, angled or with props), ""room"" (room, installation, furniture, building or any perspective/3D scene), ""other"" (logo, packaging, diagram, unrelated).
- likely_tileable: could it repeat as a texture without borders, text, shadows or perspective?
- matches_product: does it show this exact product (colour, pattern, material)?
best_index: the image most usable as a repeating texture of this product (a flat swatch first). -1 if none is a usable texture. Never pick a room shot.

Scale = the real-world width and height (mm) that ONE copy of the best image covers. Use this evidence ladder and report the rung:
1. ""page_text"", confidence ""high"": the page text states dimensions that this image represents (a tile image showing one 600x300 tile, a decor scan stated to show 2800x2070, a stated pattern repeat). Board/sheet sizes do not count unless the image shows the whole board.
2. ""image_feature"", confidence ""medium"": the image shows repeating features of known size (planks, bricks, tiles, slats). Fill scale.feature: name, real_mm (size of one feature including its joint along the counted axis, from the page text if stated, else typical), count_across (how many span the image along that axis, may be fractional), axis (""width"" or ""height""). Count; don't estimate pixel positions.
3. ""category_prior"", confidence ""low"": typical size for the category (wood decor swatches 1000-2800 mm along the grain, brick 215x65 mm plus 10 mm joints, oak planks 120-190 mm wide, stone 600-1200 mm).
Never claim a higher rung than the evidence supports. Quote the page text in rationale for rung 1.

grain_axis of the best image: ""horizontal"" (along image width), ""vertical"" or ""none"".
mapping: ""planar"" for sheet goods on flat panels, ""box"" for solid materials (stone, brick, concrete, solid wood).
finish: matt, satin, gloss, polished or textured, from the finish description; omit if unknown.
brick: only for bricks/blocks/pavers: the unit's length, height and depth in mm from the page text (e.g. 215, 65, 102.5); otherwise null.
Correct the product name/code/manufacturer if the pages show better values.

Reply with ONLY JSON:
{""product"":{""name"":"""",""code"":"""",""manufacturer"":"""",""page_url"":""""},
 ""images"":[{""index"":0,""kind"":"""",""likely_tileable"":true,""matches_product"":true,""note"":""""}],
 ""best_index"":0,
 ""scale"":{""width_mm"":0,""height_mm"":0,""source"":"""",""confidence"":"""",""rationale"":"""",""feature"":null},
 ""grain_axis"":""none"",""mapping"":""box"",""finish"":""matt"",""category"":"""",
 ""brick"":null}";

        public static string AnalyseIntro(string query, ProductInfo product, string category, int imageCount, bool pagesUnreadable)
        {
            var sb = new StringBuilder();
            sb.Append("Searched for: ").AppendLine(query.Trim());
            sb.Append("Product found: ").Append(product?.Name);
            if (!string.IsNullOrEmpty(product?.Code)) sb.Append(" (").Append(product.Code).Append(')');
            if (!string.IsNullOrEmpty(product?.Manufacturer)) sb.Append(" by ").Append(product.Manufacturer);
            sb.AppendLine();
            if (!string.IsNullOrEmpty(category)) sb.Append("Category: ").AppendLine(category);
            if (pagesUnreadable)
                sb.AppendLine("The pages could not be read as plain HTML; use URL context to read them: " + product?.PageUrl);
            sb.AppendLine(imageCount == 0 ? "No candidate images could be downloaded; return images [] and best_index -1." : $"{imageCount} candidate image(s) follow the page text.");
            return sb.ToString();
        }

        public static string PageBlock(int index, string url, string text) => $"--- Page {index + 1}: {url}\n{text}\n";

        public static string Repair(string problems) =>
            "That reply could not be used: " + problems + "\nReturn the corrected JSON object only.";

        public static JsonNode AnalyseSchema() => JsonNode.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""product"": { ""type"": ""object"", ""properties"": {
        ""name"": {""type"": ""string""}, ""code"": {""type"": ""string""},
        ""manufacturer"": {""type"": ""string""}, ""page_url"": {""type"": ""string""} },
      ""required"": [""name""] },
    ""images"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
        ""index"": {""type"": ""integer""}, ""kind"": {""type"": ""string"", ""enum"": [""swatch"", ""detail"", ""room"", ""other""]},
        ""likely_tileable"": {""type"": ""boolean""}, ""matches_product"": {""type"": ""boolean""}, ""note"": {""type"": ""string""} },
      ""required"": [""index"", ""kind"", ""likely_tileable"", ""matches_product""] } },
    ""best_index"": {""type"": ""integer""},
    ""scale"": { ""type"": ""object"", ""properties"": {
        ""width_mm"": {""type"": ""number""}, ""height_mm"": {""type"": ""number""},
        ""source"": {""type"": ""string"", ""enum"": [""page_text"", ""image_feature"", ""category_prior""]},
        ""confidence"": {""type"": ""string"", ""enum"": [""high"", ""medium"", ""low""]},
        ""rationale"": {""type"": ""string""},
        ""feature"": { ""type"": [""object"", ""null""], ""properties"": {
            ""name"": {""type"": ""string""}, ""real_mm"": {""type"": ""number""},
            ""count_across"": {""type"": ""number""}, ""axis"": {""type"": ""string"", ""enum"": [""width"", ""height""]} } } },
      ""required"": [""width_mm"", ""height_mm"", ""source"", ""confidence""] },
    ""grain_axis"": {""type"": ""string"", ""enum"": [""horizontal"", ""vertical"", ""none""]},
    ""mapping"": {""type"": ""string"", ""enum"": [""planar"", ""box"", ""per_face""]},
    ""finish"": {""type"": ""string"", ""enum"": [""matt"", ""satin"", ""gloss"", ""polished"", ""textured""]},
    ""category"": {""type"": ""string""},
    ""brick"": { ""type"": [""object"", ""null""], ""properties"": {
        ""length_mm"": {""type"": ""number""}, ""height_mm"": {""type"": ""number""}, ""depth_mm"": {""type"": ""number""} } }
  },
  ""required"": [""product"", ""images"", ""best_index"", ""scale"", ""mapping"", ""category""]
}");
    }
}
