using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AI.BugInvestigationTool.Configuration;

namespace AI.BugInvestigationTool.Services.Claude;

public sealed class ClaudeException(string message) : Exception(message);

/// <summary>
/// The single Claude service: Anthropic Messages API over HttpClient with a controlled tool-use loop.
/// Claude can only call the tools passed in; every call is validated before the executor runs it,
/// and the conversation ends when Claude calls the designated "submit" tool with schema-valid input.
/// </summary>
public sealed class ClaudeClient
{
    private const string ApiVersion = "2026-10-21";
    private static readonly HashSet<string> FallbackModels = ["claude-opus-5-5", "claude-opus-5", "claude-fable-5-1", "claude-fable-5-2", "claude-sonnet-5-5"];

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ToolOptions _options;

    public ClaudeClient(HttpClient http, string apiKey, ToolOptions options)
    {
        _http = http;
        _apiKey = apiKey;
        _options = options;
        if (_http.BaseAddress is null) _http.BaseAddress = new Uri(options.ClaudeBaseUrl);
    }

    public delegate Task<ToolOutput> ToolExecutor(string name, JsonElement input, CancellationToken ct);

    /// <summary>
    /// Runs one tool-use conversation. Returns the validated input of the <paramref name="submitTool"/> call.
    /// </summary>
    public async Task<JsonElement> RunAsync(
        string system,
        JsonArray userContent,
        IReadOnlyList<ClaudeTool> tools,
        ClaudeTool submitTool,
        ToolExecutor? executor,
        CancellationToken ct,
        Func<string, Task>? onToolCall = null,
        int? maxTurns = null)
    {
        var allTools = tools.Append(submitTool).ToList();
        var byName = allTools.ToDictionary(t => t.Name);
        var messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = userContent });
        var nudges = 0;
        var submitErrors = 0;
        var turns = maxTurns ?? _options.MaxToolTurns;

        for (var turn = 0; turn < turns; turn++)
        {
            ct.ThrowIfCancellationRequested();
            using var response = await SendAsync(system, messages, allTools, ct);
            var root = response.RootElement;
            var stop = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;

            if (stop == "refusal")
                throw new ClaudeException("Claude declined the request (stop_reason=refusal).");

            var content = root.GetProperty("content");
            // Append the full assistant content (including thinking blocks) unchanged, as the API requires.
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = JsonNode.Parse(content.GetRawText()) });

            var toolUses = content.EnumerateArray().Where(b => b.GetProperty("type").GetString() == "tool_use").ToList();
            if (toolUses.Count == 0)
            {
                if (stop == "max_tokens") throw new ClaudeException("Claude response was cut off (max_tokens). Increase Tool:ClaudeMaxTokens.");
                if (++nudges > 2) throw new ClaudeException($"Claude did not call {submitTool.Name}.");
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = $"Continue the task. When you are done, you must call the {submitTool.Name} tool with your result.",
                });
                continue;
            }

            var results = new JsonArray();
            foreach (var use in toolUses)
            {
                var id = use.GetProperty("id").GetString()!;
                var name = use.GetProperty("name").GetString()!;
                var input = use.GetProperty("input");

                if (name == submitTool.Name)
                {
                    var invalid = submitTool.Validate(input);
                    if (invalid is null) return input.Clone();
                    if (++submitErrors > 2) throw new ClaudeException($"Claude returned invalid {submitTool.Name} input: {invalid}");
                    results.Add(ToolResult(id, $"Invalid input: {invalid}. Call {submitTool.Name} again with corrected input.", true));
                    continue;
                }

                if (!byName.TryGetValue(name, out var tool) || executor is null)
                {
                    results.Add(ToolResult(id, $"Unknown tool '{name}'. Only the provided tools may be used.", true));
                    continue;
                }

                var error = tool.Validate(input);
                if (error is not null)
                {
                    results.Add(ToolResult(id, $"Invalid input: {error}", true));
                    continue;
                }

                if (onToolCall is not null) await onToolCall(name);
                ToolOutput output;
                try { output = await executor(name, input, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { output = new ToolOutput($"Tool failed: {ex.Message}", true); }
                results.Add(ToolResult(id, Truncate(output.Content, 40000), output.IsError));
            }
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
        }

        throw new ClaudeException($"Claude did not finish within {turns} tool turns.");
    }

    private static JsonObject ToolResult(string id, string content, bool isError) => new()
    {
        ["type"] = "tool_result",
        ["tool_use_id"] = id,
        ["content"] = content,
        ["is_error"] = isError,
    };

    internal JsonObject BuildRequest(string system, JsonArray messages, IEnumerable<ClaudeTool> tools)
    {
        var body = new JsonObject
        {
            ["model"] = _options.ClaudeModel,
            ["max_tokens"] = _options.ClaudeMaxTokens,
            ["system"] = system,
            ["messages"] = messages.DeepClone(),
            ["tools"] = new JsonArray(tools.Select(t => (JsonNode)t.ToApi()).ToArray()),
            ["tool_choice"] = new JsonObject { ["type"] = "auto" },
            ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
        };
        if (!string.IsNullOrWhiteSpace(_options.ClaudeEffort))
            body["output_config"] = new JsonObject { ["effort"] = _options.ClaudeEffort };
        if (FallbackModels.Contains(_options.ClaudeModel))
            body["fallbacks"] = "default"; // server-side model fallback on a safety refusal
        return body;
    }

    private async Task<JsonDocument> SendAsync(string system, JsonArray messages, IEnumerable<ClaudeTool> tools, CancellationToken ct)
    {
        var json = BuildRequest(system, messages, tools).ToJsonString();
        var useFallbacks = FallbackModels.Contains(_options.ClaudeModel);

        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-api-key", _apiKey);
            req.Headers.Add("anthropic-version", ApiVersion);
            if (useFallbacks) req.Headers.Add("anthropic-beta", "server-side-fallback-2026-07-01");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.ClaudeTimeoutSeconds));

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt < 2) continue;
                throw new ClaudeException($"Claude API timed out after {_options.ClaudeTimeoutSeconds}s.");
            }
            catch (HttpRequestException ex)
            {
                if (attempt < 2) { await Task.Delay(Backoff(attempt, null), ct); continue; }
                throw new ClaudeException($"Claude API request failed: {ex.Message}");
            }

            using (resp)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode) return JsonDocument.Parse(text);

                var status = (int)resp.StatusCode;
                var retryable = status is 408 or 409 or 429 or 529 || status >= 500;
                if (retryable && attempt < 2)
                {
                    await Task.Delay(Backoff(attempt, resp.Headers.RetryAfter?.Delta), ct);
                    continue;
                }

                var message = TryErrorMessage(text);
                throw new ClaudeException(status switch
                {
                    (int)HttpStatusCode.Unauthorized => "Claude API key was rejected (401).",
                    (int)HttpStatusCode.Forbidden => $"Claude API access denied (403): {message}",
                    (int)HttpStatusCode.NotFound => $"Claude model '{_options.ClaudeModel}' not found (404): {message}",
                    (int)HttpStatusCode.TooManyRequests => "Claude API rate limit reached (429). Try again later.",
                    _ => $"Claude API error {status}: {message}",
                });
            }
        }
    }

    private static TimeSpan Backoff(int attempt, TimeSpan? retryAfter) =>
        retryAfter is { } ra && ra < TimeSpan.FromSeconds(60) ? ra : TimeSpan.FromSeconds(2 * (attempt + 1));

    private static string TryErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? body;
        }
        catch { return body.Length > 300 ? body[..300] : body; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n…[truncated]";

    // ---- content helpers ----
    public static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    public static JsonObject Image(byte[] png, string mediaType = "image/png") => new()
    {
        ["type"] = "image",
        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = Convert.ToBase64String(png) },
    };
}
