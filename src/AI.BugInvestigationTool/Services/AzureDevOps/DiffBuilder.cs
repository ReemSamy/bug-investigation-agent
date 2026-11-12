using System.Text;
using AI.BugInvestigationTool.Models;

namespace AI.BugInvestigationTool.Services.AzureDevOps;

/// <summary>
/// Line-based unified diff (LCS) with real old/new line numbers. Azure DevOps has no unified-diff
/// endpoint, so the PR workflow computes it from the base and source versions of each file.
/// </summary>
public static class DiffBuilder
{
    public sealed record Result(string Text, int Additions, int Deletions, List<LineRange> ChangedRanges, string? SkippedReason);

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".pdf", ".zip", ".dll", ".exe", ".woff", ".woff2", ".ttf", ".eot", ".snk", ".pfx", ".mp4", ".xlsx", ".docx",
    };

    public static bool IsLikelyBinary(string path) => BinaryExtensions.Contains(Path.GetExtension(path));

    public static Result Build(string path, string oldText, string newText, int maxLines = 4000, int context = 3)
    {
        var a = Split(oldText);
        var b = Split(newText);
        if (a.Length > maxLines || b.Length > maxLines)
            return new Result("", 0, 0, [], $"file has more than {maxLines} lines; diff skipped");

        // Trim common prefix/suffix to keep the LCS table small.
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;

        var ops = new List<(char Op, int OldIdx, int NewIdx)>();
        for (var i = 0; i < prefix; i++) ops.Add((' ', i, i));
        ops.AddRange(Lcs(a, b, prefix, a.Length - suffix, prefix, b.Length - suffix));
        for (var i = 0; i < suffix; i++) ops.Add((' ', a.Length - suffix + i, b.Length - suffix + i));

        var additions = ops.Count(o => o.Op == '+');
        var deletions = ops.Count(o => o.Op == '-');
        if (additions == 0 && deletions == 0) return new Result("", 0, 0, [], null);

        // Group into hunks with context.
        var sb = new StringBuilder();
        sb.Append("--- a/").Append(path.TrimStart('/')).Append('\n').Append("+++ b/").Append(path.TrimStart('/')).Append('\n');
        var changed = new List<LineRange>();
        var idx = 0;
        while (idx < ops.Count)
        {
            if (ops[idx].Op == ' ') { idx++; continue; }
            var start = Math.Max(0, idx - context);
            var end = idx;
            var lastChange = idx;
            while (end < ops.Count && (ops[end].Op != ' ' || end - lastChange <= context * 2))
            {
                if (ops[end].Op != ' ') lastChange = end;
                end++;
            }
            end = Math.Min(ops.Count, lastChange + context + 1);

            var hunk = ops.GetRange(start, end - start);
            var oldStart = hunk.FirstOrDefault(o => o.Op != '+').OldIdx + 1;
            var newStart = hunk.FirstOrDefault(o => o.Op != '-').NewIdx + 1;
            sb.Append($"@@ -{oldStart},{hunk.Count(o => o.Op != '+')} +{newStart},{hunk.Count(o => o.Op != '-')} @@\n");
            foreach (var (op, oi, ni) in hunk)
            {
                var line = op == '-' ? a[oi] : b[ni];
                var number = op == '-' ? $"{oi + 1,5}      " : op == '+' ? $"      {ni + 1,5}" : $"{oi + 1,5} {ni + 1,5}";
                sb.Append(op).Append(' ').Append(number).Append(" | ").Append(line).Append('\n');
            }

            var newLines = hunk.Where(o => o.Op != '-').Select(o => o.NewIdx + 1).ToList();
            if (newLines.Count > 0) changed.Add(new LineRange(newLines.Min(), newLines.Max()));
            idx = end;
        }

        return new Result(sb.ToString(), additions, deletions, changed, null);
    }

    private static string[] Split(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    private static List<(char, int, int)> Lcs(string[] a, string[] b, int a0, int a1, int b0, int b1)
    {
        var n = a1 - a0;
        var m = b1 - b0;
        var result = new List<(char, int, int)>(n + m);
        if (n == 0) { for (var j = 0; j < m; j++) result.Add(('+', a0, b0 + j)); return result; }
        if (m == 0) { for (var i = 0; i < n; i++) result.Add(('-', a0 + i, b0)); return result; }

        var table = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                table[i, j] = a[a0 + i] == b[b0 + j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[a0 + x] == b[b0 + y]) { result.Add((' ', a0 + x, b0 + y)); x++; y++; }
            else if (table[x + 1, y] >= table[x, y + 1]) { result.Add(('-', a0 + x, b0 + y)); x++; }
            else { result.Add(('+', a0 + x, b0 + y)); y++; }
        }
        while (x < n) { result.Add(('-', a0 + x, b0 + Math.Min(y, m - 1))); x++; }
        while (y < m) { result.Add(('+', a0 + Math.Min(x, n - 1), b0 + y)); y++; }
        return result;
    }
}
