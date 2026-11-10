using System.Text;
using System.Text.Json;

namespace BugInvestigationAgent.Skills.Jira;

/// <summary>
/// Converts Jira Cloud's Atlassian Document Format (ADF) to plain text that keeps the structure people wrote:
/// one paragraph per line, numbered lists as "1. ", bullet lists as "- ". Without this, a description's
/// "Steps to reproduce" list collapses into one comma-separated line and no step can be recognised.
/// </summary>
public static class AdfText
{
    public static bool IsAdf(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String &&
        el.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array;

    public static string ToPlainText(JsonElement doc)
    {
        var sb = new StringBuilder();
        WriteBlock(doc, sb, listPrefix: null);
        // Collapse runs of blank lines.
        var lines = sb.ToString().Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd());
        var result = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0 && (result.Count == 0 || result[^1].Length == 0))
                continue;
            result.Add(line);
        }
        return string.Join("\n", result).Trim();
    }

    private static void WriteBlock(JsonElement node, StringBuilder sb, string? listPrefix)
    {
        var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "orderedList":
            {
                var n = node.TryGetProperty("attrs", out var attrs) && attrs.TryGetProperty("order", out var o) && o.TryGetInt32(out var start) ? start : 1;
                foreach (var item in Children(node))
                    WriteBlock(item, sb, $"{n++}. ");
                return;
            }
            case "bulletList":
                foreach (var item in Children(node))
                    WriteBlock(item, sb, "- ");
                return;
            case "listItem":
            {
                var first = true;
                foreach (var child in Children(node))
                {
                    if (child.TryGetProperty("type", out var ct) && ct.GetString() is "orderedList" or "bulletList")
                    {
                        WriteBlock(child, sb, null);
                        continue;
                    }
                    sb.Append(first ? listPrefix ?? "" : "   ");
                    sb.Append(Inline(child)).Append('\n');
                    first = false;
                }
                return;
            }
            case "paragraph":
            case "heading":
            case "blockquote":
            case "codeBlock":
            case "panel":
                if (node.TryGetProperty("content", out _) && Children(node).Any(c => c.TryGetProperty("type", out var ct) && ct.GetString() is "paragraph" or "orderedList" or "bulletList"))
                {
                    foreach (var child in Children(node))
                        WriteBlock(child, sb, null);
                    return;
                }
                sb.Append(Inline(node)).Append('\n');
                return;
            case "rule":
                sb.Append('\n');
                return;
            default:
                if (node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in content.EnumerateArray())
                        WriteBlock(child, sb, listPrefix);
                    return;
                }
                var text = Inline(node);
                if (text.Length > 0)
                    sb.Append(text).Append('\n');
                return;
        }
    }

    private static string Inline(JsonElement node)
    {
        var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "text":
                return node.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
            case "hardBreak":
                return "\n";
            case "mention":
            case "emoji":
            case "status":
                return node.TryGetProperty("attrs", out var a) && a.TryGetProperty("text", out var at) ? at.GetString() ?? "" : "";
            case "inlineCard":
            case "blockCard":
                return node.TryGetProperty("attrs", out var ca) && ca.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "";
        }
        var sb = new StringBuilder();
        foreach (var child in Children(node))
            sb.Append(Inline(child));
        return sb.ToString();
    }

    private static IEnumerable<JsonElement> Children(JsonElement node) =>
        node.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray()
            : Enumerable.Empty<JsonElement>();
}
