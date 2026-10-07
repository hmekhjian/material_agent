using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MaterialAgent.Core.Agent
{
    /// <summary>Turns a product name or spec code into a reviewed, downloaded material proposal.</summary>
    public interface IMaterialResolver
    {
        Task<ResolveResult> ResolveAsync(string query, IProgress<string> progress, CancellationToken ct);
    }

    /// <summary>One downloaded candidate image, with what the agent and the vision check said about it.</summary>
    public sealed class CandidateImage
    {
        public string Url { get; set; }
        public FetchedImage Image { get; set; }
        public string Kind { get; set; }
        public bool LikelyTileable { get; set; }
        /// <summary>False if the vision check thought it shows a different product.</summary>
        public bool MatchesProduct { get; set; } = true;
        public string Note { get; set; }
        /// <summary>True if found by reading the product page's HTML rather than suggested by the model.</summary>
        public bool FromPage { get; set; }
        /// <summary>Normal/roughness maps made with the image (patterns know their exact joint relief); null = derive on import.</summary>
        public SurfaceMapFiles Maps { get; set; }
    }

    /// <summary>Paths of normal and roughness map files that belong to a texture.</summary>
    public sealed class SurfaceMapFiles
    {
        public string NormalPath { get; set; }
        public string RoughnessPath { get; set; }
    }

    public sealed class ResolveResult
    {
        public string Query { get; set; }
        public MaterialResolution Resolution { get; set; }
        /// <summary>Downloaded texture candidates (flat swatches/details), best first. Never room or perspective shots.</summary>
        public List<CandidateImage> Candidates { get; set; } = new List<CandidateImage>();
        /// <summary>
        /// Room/perspective/other shots that were filtered out. Not shown as textures, but useful as references
        /// for AI seamless-texture generation when no flat texture exists.
        /// </summary>
        public List<CandidateImage> References { get; set; } = new List<CandidateImage>();
        /// <summary>Brick/block/paver unit size from the page, when the product is one; else null.</summary>
        public Bricks.BrickUnit Brick { get; set; }
        /// <summary>True when the result came from this session's cache (no model calls).</summary>
        public bool FromCache { get; set; }
        /// <summary>How long each stage took, for the status line.</summary>
        public List<KeyValuePair<string, TimeSpan>> Timings { get; set; } = new List<KeyValuePair<string, TimeSpan>>();
        public ScaleDecision Scale { get; set; }
        public GrainAxis Grain { get; set; }
        public MappingKind Mapping { get; set; }
        public Finish Finish { get; set; }
        public bool FinishKnown { get; set; }
        public string Category { get; set; }
        /// <summary>Pages the model's search was grounded on (title, url).</summary>
        public List<KeyValuePair<string, string>> Sources { get; set; } = new List<KeyValuePair<string, string>>();
        public GeminiUsage Usage { get; set; } = new GeminiUsage();
        /// <summary>Gemini requests made (including repair rounds and retries), for the status line.</summary>
        public int ModelCalls { get; set; }
        /// <summary>Non-fatal problems worth showing (e.g. candidates that failed to download).</summary>
        public List<string> Warnings { get; set; } = new List<string>();
        public DateTime ResolvedUtc { get; set; } = DateTime.UtcNow;
    }
}
