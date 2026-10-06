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
    }

    public sealed class ResolveResult
    {
        public string Query { get; set; }
        public MaterialResolution Resolution { get; set; }
        /// <summary>Downloaded candidates, best first.</summary>
        public List<CandidateImage> Candidates { get; set; } = new List<CandidateImage>();
        public ScaleDecision Scale { get; set; }
        public GrainAxis Grain { get; set; }
        public MappingKind Mapping { get; set; }
        public Finish Finish { get; set; }
        public bool FinishKnown { get; set; }
        public string Category { get; set; }
        /// <summary>Pages the model's search was grounded on (title, url).</summary>
        public List<KeyValuePair<string, string>> Sources { get; set; } = new List<KeyValuePair<string, string>>();
        public GeminiUsage Usage { get; set; } = new GeminiUsage();
        /// <summary>Non-fatal problems worth showing (e.g. candidates that failed to download).</summary>
        public List<string> Warnings { get; set; } = new List<string>();
        public DateTime ResolvedUtc { get; set; } = DateTime.UtcNow;
    }
}
