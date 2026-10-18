using System;
using System.Collections.Generic;
using System.Text;

namespace MiniHarness.Coding
{
    // Line-level unified diff without external dependencies. Enough to render a
    // readable, reviewable change (3 lines of context, standard hunk headers) for
    // the small text edits V1 performs. Not a full Git implementation.
    internal static class DiffFormatter
    {
        private const int Context = 3;

        // Resource ceiling for the O(n*m) LCS table. A byte size limit on files
        // does not bound line counts (two 10k-line files would be ~400MB of
        // ints), so the matrix size is gated independently. Above it we degrade
        // to an O(n+m) line-set summary instead of risking an OOM.
        private const int MaxLcsSideLines = 4_000;
        private const long MaxLcsCells = 4_000_000L; // ~16 MB of ints plus arrays
        private const int SummarySampleLines = 40;
        private const int SummaryLineChars = 200;

        public static string Unified(string relativePath, string before, string after, int maxChars)
        {
            string[] a = Split(before);
            string[] b = Split(after);

            if (AboveLcsCeiling(a.Length, b.Length))
                return Cap(Summary(relativePath, a, b), maxChars);

            // Edit script via longest common subsequence.
            bool[] keepA, keepB;
            (keepA, keepB) = CommonSubsequence(a, b);

            // Walk the aligned rows to build raw line events: ' ' context, '-' remove, '+' add.
            var events = new List<(char Kind, int OldLine, int NewLine, string Text)>();
            int ia = 0, ib = 0;
            while (ia < a.Length || ib < b.Length)
            {
                if (ia < a.Length && ib < b.Length && keepA[ia] && keepB[ib] && a[ia] == b[ib])
                {
                    events.Add((' ', ia + 1, ib + 1, a[ia]));
                    ia++; ib++;
                }
                else if (ia < a.Length && !(ib < b.Length && keepB[ib]))
                {
                    events.Add(('-', ia + 1, 0, a[ia]));
                    ia++;
                }
                else
                {
                    events.Add(('+', 0, ib + 1, b[ib]));
                    ib++;
                }
            }

            var sb = new StringBuilder();
            sb.Append("--- a/").Append(relativePath).Append('\n');
            sb.Append("+++ b/").Append(relativePath).Append('\n');

            if (!HasChange(events))
            {
                // Identical content: no hunks, just the headers make the comparison explicit.
                sb.Append("(no content difference)\n");
                return Cap(sb.ToString(), maxChars);
            }

            // Group changed events into hunks with context windows.
            var changeIndices = new List<int>();
            for (int i = 0; i < events.Count; i++)
                if (events[i].Kind != ' ') changeIndices.Add(i);

            int k = 0;
            while (k < changeIndices.Count)
            {
                int first = changeIndices[k];
                int last = first;
                // Merge change windows that are close enough to share context.
                while (k + 1 < changeIndices.Count && changeIndices[k + 1] - last <= 2 * Context + 1)
                {
                    k++;
                    last = changeIndices[k];
                }
                int start = Math.Max(0, first - Context);
                int end = Math.Min(events.Count - 1, last + Context);

                int oldStart = 0, oldCount = 0, newStart = 0, newCount = 0;
                bool oldStarted = false, newStarted = false;
                var hunk = new StringBuilder();
                for (int i = start; i <= end; i++)
                {
                    var e = events[i];
                    if (e.Kind != '+') { if (!oldStarted) { oldStart = e.OldLine; oldStarted = true; } oldCount++; }
                    if (e.Kind != '-') { if (!newStarted) { newStart = e.NewLine; newStarted = true; } newCount++; }
                    hunk.Append(e.Kind).Append(e.Text).Append('\n');
                }
                if (oldCount == 0) oldStart = Math.Max(0, oldStart - 1); // pure insertion point
                if (newCount == 0) newStart = Math.Max(0, newStart - 1);
                sb.Append("@@ -").Append(Range(oldStart, oldCount)).Append(" +").Append(Range(newStart, newCount)).Append(" @@\n");
                sb.Append(hunk);
                k++;
            }

            return Cap(sb.ToString(), maxChars);
        }

        private static bool AboveLcsCeiling(int n, int m) =>
            n > MaxLcsSideLines || m > MaxLcsSideLines || (long)n * m > MaxLcsCells;

        // O(n+m) degraded rendering for files too large for the LCS matrix. It
        // reports the scale of the change (removed/added line multisets) with
        // bounded samples, so a huge file still yields a reviewable summary
        // without allocating the quadratic table.
        private static string Summary(string relativePath, string[] a, string[] b)
        {
            var countsA = LineCounts(a);
            var countsB = LineCounts(b);
            var removed = new List<string>();
            var added = new List<string>();
            foreach (var kv in countsA)
            {
                countsB.TryGetValue(kv.Key, out int other);
                int delta = kv.Value - other;
                for (int i = 0; i < delta; i++) removed.Add(kv.Key);
            }
            foreach (var kv in countsB)
            {
                countsA.TryGetValue(kv.Key, out int other);
                int delta = kv.Value - other;
                for (int i = 0; i < delta; i++) added.Add(kv.Key);
            }

            var sb = new StringBuilder();
            sb.Append("--- a/").Append(relativePath).Append('\n');
            sb.Append("+++ b/").Append(relativePath).Append('\n');
            if (removed.Count == 0 && added.Count == 0)
            {
                sb.Append("(no content difference)\n");
                return sb.ToString();
            }
            sb.Append("[large diff: line-aligned LCS skipped to bound memory; showing a multiset summary]\n");
            sb.Append("old lines: ").Append(a.Length)
              .Append(", new lines: ").Append(b.Length)
              .Append(", removed-only: ").Append(removed.Count)
              .Append(", added-only: ").Append(added.Count).Append('\n');
            AppendSamples(sb, '-', "removed", removed);
            AppendSamples(sb, '+', "added", added);
            return sb.ToString();
        }

        private static Dictionary<string, int> LineCounts(string[] lines)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var line in lines) map[line] = map.TryGetValue(line, out int c) ? c + 1 : 1;
            return map;
        }

        private static void AppendSamples(StringBuilder sb, char marker, string label, List<string> lines)
        {
            if (lines.Count == 0) return;
            int shown = Math.Min(SummarySampleLines, lines.Count);
            sb.Append(marker).Append(' ').Append(label).Append(" sample (").Append(shown)
              .Append(" of ").Append(lines.Count).Append("):\n");
            for (int i = 0; i < shown; i++)
            {
                string text = lines[i];
                if (text.Length > SummaryLineChars) text = text.Substring(0, SummaryLineChars) + "...";
                sb.Append(marker).Append(text).Append('\n');
            }
            if (lines.Count > shown)
                sb.Append(marker).Append(" ... ").Append(lines.Count - shown).Append(" more\n");
        }

        private static string Range(int start, int count) =>
            count == 1 ? start.ToString() : start + "," + count;

        private static bool HasChange(List<(char Kind, int, int, string)> events)
        {
            foreach (var e in events) if (e.Kind != ' ') return true;
            return false;
        }

        private static string Cap(string text, int max)
        {
            if (text.Length <= max) return text;
            return text.Substring(0, Math.Max(0, max - 32)) + "\n[diff truncated by output limit]\n";
        }

        private static string[] Split(string text)
        {
            if (text.Length == 0) return Array.Empty<string>();
            // Keep line bodies without terminators, consistent with read_file numbering.
            return WorkspaceAccess.SplitLines(text);
        }

        // Classic LCS table marking which rows of each side belong to a common
        // subsequence. Inputs are small (bounded file sizes), O(n*m) is acceptable.
        private static (bool[] KeepA, bool[] KeepB) CommonSubsequence(string[] a, string[] b)
        {
            int n = a.Length, m = b.Length;
            var lcs = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

            var keepA = new bool[n];
            var keepB = new bool[m];
            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (a[x] == b[y]) { keepA[x] = keepB[y] = true; x++; y++; }
                else if (lcs[x + 1, y] >= lcs[x, y + 1]) x++;
                else y++;
            }
            return (keepA, keepB);
        }
    }
}
