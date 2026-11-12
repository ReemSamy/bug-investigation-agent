using System.Text.Json;
using System.Text.Json.Nodes;

namespace AI.BugInvestigationTool.Services.Claude;

/// <summary>
/// A predefined operation Claude may request. Every request is validated against <see cref="Schema"/>
/// by <see cref="Validate"/> before the .NET side executes it through an approved service.
/// </summary>
public sealed class ClaudeTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject Schema { get; init; }

    public JsonObject ToApi() => new()
    {
        ["name"] = Name,
        ["description"] = Description,
        ["input_schema"] = Schema.DeepClone(),
    };

    /// <summary>
    /// Validates input against the small JSON-schema subset used by this tool:
    /// object, properties, required, additionalProperties=false, type, enum, maxLength, minimum, maximum, items.
    /// Returns null when valid, otherwise a message suitable to send back to Claude.
    /// </summary>
    public string? Validate(JsonElement input)
    {
        var errors = new List<string>();
        ValidateNode(Schema, input, "input", errors);
        return errors.Count == 0 ? null : string.Join("; ", errors.Take(10));
    }

    private static void ValidateNode(JsonObject schema, JsonElement value, string path, List<string> errors)
    {
        var type = schema["type"]?.GetValue<string>();
        switch (type)
        {
            case "object":
                if (value.ValueKind != JsonValueKind.Object) { errors.Add($"{path} must be an object"); return; }
                var props = schema["properties"] as JsonObject ?? [];
                var required = (schema["required"] as JsonArray ?? []).Select(r => r!.GetValue<string>()).ToHashSet();
                foreach (var name in required)
                {
                    if (!value.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) errors.Add($"{path}.{name} is required");
                }
                foreach (var p in value.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Null && !required.Contains(p.Name)) continue; // optional and omitted
                    if (props[p.Name] is JsonObject ps) ValidateNode(ps, p.Value, $"{path}.{p.Name}", errors);
                    else if (schema["additionalProperties"] is JsonValue ap && !ap.GetValue<bool>()) errors.Add($"{path}.{p.Name} is not allowed");
                }
                break;
            case "array":
                if (value.ValueKind != JsonValueKind.Array) { errors.Add($"{path} must be an array"); return; }
                if (schema["maxItems"] is JsonValue mi && value.GetArrayLength() > mi.GetValue<int>()) errors.Add($"{path} has too many items");
                if (schema["items"] is JsonObject items)
                {
                    var i = 0;
                    foreach (var item in value.EnumerateArray()) ValidateNode(items, item, $"{path}[{i++}]", errors);
                }
                break;
            case "string":
                if (value.ValueKind != JsonValueKind.String) { errors.Add($"{path} must be a string"); return; }
                var s = value.GetString()!;
                if (schema["maxLength"] is JsonValue ml && s.Length > ml.GetValue<int>()) errors.Add($"{path} exceeds {ml.GetValue<int>()} characters");
                if (schema["enum"] is JsonArray en && !en.Any(e => e!.GetValue<string>() == s)) errors.Add($"{path} must be one of: {string.Join(", ", en.Select(e => e!.GetValue<string>()))}");
                break;
            case "integer":
            case "number":
                if (value.ValueKind != JsonValueKind.Number || (type == "integer" && !value.TryGetInt64(out _))) { errors.Add($"{path} must be {(type == "integer" ? "an integer" : "a number")}"); return; }
                var d = value.GetDouble();
                if (schema["minimum"] is JsonValue min && d < Number(min)) errors.Add($"{path} must be >= {min}");
                if (schema["maximum"] is JsonValue max && d > Number(max)) errors.Add($"{path} must be <= {max}");
                break;
            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) errors.Add($"{path} must be a boolean");
                break;
        }
    }

    private static double Number(JsonValue v) => double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    // ---- small schema builders to keep tool definitions readable ----
    public static JsonObject Obj(params (string Name, JsonObject Schema, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema, isRequired) in props)
        {
            properties[name] = schema.Parent is null ? schema : schema.DeepClone();
            if (isRequired) required.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
    }

    public static JsonObject Str(string description, int maxLength = 4000, params string[] allowed)
    {
        var o = new JsonObject { ["type"] = "string", ["description"] = description, ["maxLength"] = maxLength };
        if (allowed.Length > 0) o["enum"] = new JsonArray(allowed.Select(a => (JsonNode)a!).ToArray());
        return o;
    }

    public static JsonObject Int(string description, int min, int max) =>
        new() { ["type"] = "integer", ["description"] = description, ["minimum"] = min, ["maximum"] = max };

    public static JsonObject Num(string description, double min, double max) =>
        new() { ["type"] = "number", ["description"] = description, ["minimum"] = min, ["maximum"] = max };

    public static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject Arr(JsonObject items, string description, int maxItems = 50) =>
        new() { ["type"] = "array", ["description"] = description, ["items"] = items.Parent is null ? items : items.DeepClone(), ["maxItems"] = maxItems };
}

/// <summary>Result of executing one tool request.</summary>
public sealed record ToolOutput(string Content, bool IsError = false);
