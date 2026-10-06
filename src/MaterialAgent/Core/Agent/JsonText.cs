using System;

namespace MaterialAgent.Core.Agent
{
    /// <summary>Pulls the JSON object out of a model reply (code fences, leading prose, trailing notes).</summary>
    public static class JsonText
    {
        /// <summary>Returns the first balanced top-level {...} in <paramref name="text"/>, or null.</summary>
        public static string ExtractObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = StripFences(text);

            int start = text.IndexOf('{');
            while (start >= 0)
            {
                int end = FindObjectEnd(text, start);
                if (end > start) return text.Substring(start, end - start + 1);
                start = text.IndexOf('{', start + 1);
            }
            return null;
        }

        /// <summary>Removes ```json ... ``` fences, keeping the fenced content if any.</summary>
        public static string StripFences(string text)
        {
            var t = text.Trim();
            int open = t.IndexOf("```", StringComparison.Ordinal);
            if (open < 0) return t;
            int lineEnd = t.IndexOf('\n', open);
            if (lineEnd < 0) return t;
            int close = t.IndexOf("```", lineEnd, StringComparison.Ordinal);
            return close < 0 ? t.Substring(lineEnd + 1) : t.Substring(lineEnd + 1, close - lineEnd - 1);
        }

        static int FindObjectEnd(string s, int start)
        {
            int depth = 0;
            bool inString = false, escape = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return i;
            }
            return -1;
        }
    }
}
