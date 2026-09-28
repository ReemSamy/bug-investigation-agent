using System.Text.Json;
using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Abstractions;
using BugInvestigationAgent.Core.Orchestration;

namespace BugInvestigationAgent.Skills.Common;

internal static class LlmHelpers
{
    public static bool IsUsableLlm(IAgentRuntime runtime) =>
        !string.Equals(runtime.ProviderId, "null", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(runtime.ProviderId, "cursor", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(runtime.ProviderId, "azure-openai", StringComparison.OrdinalIgnoreCase);

    public static bool IsUsableLlm(IAgentRuntimeResolver resolver) =>
        IsUsableLlm(resolver.GetCurrent());

    public static string? ExtractJsonObject(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var fenced = Regex.Match(content, @"```(?:json)?\s*(\{[\s\S]*?\})\s*```", RegexOptions.IgnoreCase);
        if (fenced.Success)
            return fenced.Groups[1].Value;

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        return content[start..(end + 1)];
    }

    public static JsonDocument? TryParseJson(string content)
    {
        var json = ExtractJsonObject(content);
        if (json is null)
            return null;
        try
        {
            return JsonDocument.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    public static IList<string> ReadStringArray(JsonElement root, string name)
    {
        var list = new List<string>();
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in el.EnumerateArray())
        {
            var s = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s!);
        }
        return list;
    }

    public static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var el))
            {
                var s = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }
        return null;
    }
}
