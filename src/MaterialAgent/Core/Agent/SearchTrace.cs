using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MaterialAgent.Core.Agent
{
    /// <summary>
    /// A timestamped record of one search: every model call, page and image download, with durations and token
    /// counts. Written to the search log so slow or failed searches can be diagnosed from a paste.
    /// </summary>
    public sealed class SearchTrace
    {
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly List<string> _lines = new List<string>();

        public SearchTrace(string query, string model, string locateModel)
        {
            Add($"Search \"{query}\" at {DateTime.Now:yyyy-MM-dd HH:mm:ss}, models: analysis {model}, page finding {locateModel}, plug-in {typeof(SearchTrace).Assembly.GetName().Version}");
        }

        public TimeSpan Elapsed => _clock.Elapsed;

        public void Add(string line)
        {
            lock (_lines) _lines.Add($"[{_clock.Elapsed.TotalSeconds,6:0.0}s] {line}");
        }

        public override string ToString()
        {
            lock (_lines) return string.Join(Environment.NewLine, _lines);
        }
    }

    /// <summary>Search log file: %LOCALAPPDATA%\MaterialAgent\search.log, newest last, kept to a few hundred KB.</summary>
    public static class SearchLog
    {
        const int MaxBytes = 300 * 1024;

        public static string PathOverride { get; set; }

        public static string FilePath => PathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MaterialAgent", "search.log");

        public static void Append(SearchTrace trace)
        {
            if (trace == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.AppendAllText(FilePath, trace + Environment.NewLine + new string('-', 60) + Environment.NewLine, Encoding.UTF8);
                var info = new FileInfo(FilePath);
                if (info.Length > MaxBytes)
                {
                    // Keep the newest half.
                    var text = File.ReadAllText(FilePath, Encoding.UTF8);
                    File.WriteAllText(FilePath, text.Substring(text.Length - MaxBytes / 2), Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never break a search.
            }
        }

        public static string Read()
        {
            try { return File.Exists(FilePath) ? File.ReadAllText(FilePath, Encoding.UTF8) : ""; }
            catch (Exception ex) { return "Couldn't read the log: " + ex.Message; }
        }

        public static void Clear()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }
    }
}
