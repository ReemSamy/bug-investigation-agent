using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BugInvestigationAgent.Core.Abstractions;
using BugInvestigationAgent.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace BugInvestigationAgent.Providers.Anthropic;

public sealed class AnthropicRuntime : IAgentRuntime
{
    public const string Id = "anthropic";
    // Current models think adaptively before answering; that thinking counts toward max_tokens, so leave room.
    private const int MinMaxTokens = 16_000;
    private readonly HttpClient _http;
    private readonly ILlmSettingsSource _settings;
    private readonly ILogger<AnthropicRuntime> _logger;

    public AnthropicRuntime(HttpClient http, ILlmSettingsSource settings, ILogger<AnthropicRuntime> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public string ProviderId => Id;

    public async Task<AgentTurnResult> CompleteAsync(AgentTurnRequest request, CancellationToken cancellationToken = default)
    {
        var options = _settings.Current.Anthropic; // read per call: Settings page changes apply immediately
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("Anthropic API key is not configured (Sign in / Account → AI provider).");
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new InvalidOperationException("Anthropic model is not configured (Sign in / Account → AI provider).");
        var messages = request.Messages.Select(m => new { role = m.Role == "assistant" ? "assistant" : "user", content = m.Content }).ToList();
        // No temperature: current Claude models reject sampling parameters.
        var payload = new
        {
            model = options.Model,
            max_tokens = Math.Max(request.MaxTokens ?? MinMaxTokens, MinMaxTokens),
            system = request.SystemPrompt,
            messages
        };

        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/v1/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", options.ApiKey);
        req.Headers.Add("anthropic-version", options.ApiVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Anthropic call failed: {Status}", (int)resp.StatusCode);
            throw new InvalidOperationException($"Anthropic provider returned {(int)resp.StatusCode} {ErrorType(body)} for model '{options.Model}'.");
        }

        using var doc = JsonDocument.Parse(body);
        var stopReason = doc.RootElement.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
        if (stopReason == "refusal")
            throw new InvalidOperationException("Anthropic declined this request (stop_reason=refusal); no analysis was produced.");
        var parts = doc.RootElement.GetProperty("content");
        var text = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var t))
                text.Append(t.GetString());
        }

        return new AgentTurnResult { Content = text.ToString(), FinishReason = stopReason ?? "stop" };
    }

    /// <summary>Only the error type from an API error body (never echoes request content or headers).</summary>
    private static string ErrorType(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("type", out var t) ? $"({t.GetString()})" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
        AgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await CompleteAsync(request, cancellationToken);
        yield return new AgentStreamEvent { Kind = "delta", TextDelta = result.Content };
        yield return new AgentStreamEvent { Kind = "done" };
    }
}
