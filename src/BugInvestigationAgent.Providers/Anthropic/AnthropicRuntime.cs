using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BugInvestigationAgent.Core.Abstractions;
using BugInvestigationAgent.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Providers.Anthropic;

public sealed class AnthropicRuntime : IAgentRuntime
{
    public const string Id = "anthropic";
    private readonly HttpClient _http;
    private readonly AnthropicOptions _options;
    private readonly ILogger<AnthropicRuntime> _logger;

    public AnthropicRuntime(HttpClient http, IOptions<LlmProviderOptions> options, ILogger<AnthropicRuntime> logger)
    {
        _http = http;
        _options = options.Value.Anthropic;
        _logger = logger;
    }

    public string ProviderId => Id;

    public async Task<AgentTurnResult> CompleteAsync(AgentTurnRequest request, CancellationToken cancellationToken = default)
    {
        var messages = request.Messages.Select(m => new { role = m.Role == "assistant" ? "assistant" : "user", content = m.Content }).ToList();
        var payload = new
        {
            model = _options.Model,
            max_tokens = request.MaxTokens ?? 4096,
            temperature = request.Temperature,
            system = request.SystemPrompt,
            messages
        };

        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/v1/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", _options.ApiKey);
        req.Headers.Add("anthropic-version", _options.ApiVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Anthropic call failed: {Status}", (int)resp.StatusCode);
            throw new InvalidOperationException($"Anthropic provider returned {(int)resp.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(body);
        var parts = doc.RootElement.GetProperty("content");
        var text = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var t))
                text.Append(t.GetString());
        }

        return new AgentTurnResult { Content = text.ToString(), FinishReason = "stop" };
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
