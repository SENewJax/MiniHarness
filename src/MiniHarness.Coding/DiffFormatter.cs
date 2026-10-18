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

        public static string Unified(string relativePath, string before, string after, int maxChars)
        {
            string[] a = Split(before);
            string[] b = Split(after);

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
