using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BugInvestigationAgent.Core.Abstractions;
using BugInvestigationAgent.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace BugInvestigationAgent.Providers.OpenAi;

public sealed class OpenAiCompatibleRuntime : IAgentRuntime
{
    public const string Id = "openai-compatible";
    private readonly HttpClient _http;
    private readonly ILlmSettingsSource _settings;
    private readonly ILogger<OpenAiCompatibleRuntime> _logger;

    public OpenAiCompatibleRuntime(HttpClient http, ILlmSettingsSource settings, ILogger<OpenAiCompatibleRuntime> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    // Read per call: Settings page changes apply to the next request.
    private OpenAiCompatibleOptions Options => _settings.Current.OpenAiCompatible;

    public string ProviderId => Id;

    public async Task<AgentTurnResult> CompleteAsync(AgentTurnRequest request, CancellationToken cancellationToken = default)
    {
        var payload = BuildPayload(request, stream: false);
        using var req = CreateRequest(payload);
        using var resp = await _http.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenAI-compatible call failed: {Status}", (int)resp.StatusCode);
            throw new InvalidOperationException($"OpenAI-compatible provider returned {(int)resp.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return new AgentTurnResult { Content = content, FinishReason = "stop" };
    }

    public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
        AgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await CompleteAsync(request, cancellationToken);
        yield return new AgentStreamEvent { Kind = "delta", TextDelta = result.Content };
        yield return new AgentStreamEvent { Kind = "done" };
    }

    private HttpRequestMessage CreateRequest(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var req = new HttpRequestMessage(HttpMethod.Post, Combine(Options.BaseUrl, "chat/completions"))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(Options.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.ApiKey);
        return req;
    }

    private object BuildPayload(AgentTurnRequest request, bool stream)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new { role = "system", content = request.SystemPrompt });
        foreach (var m in request.Messages)
            messages.Add(new { role = m.Role, content = m.Content });

        return new
        {
            model = Options.Model,
            messages,
            temperature = request.Temperature,
            max_tokens = request.MaxTokens,
            stream
        };
    }

    private static string Combine(string baseUrl, string path)
    {
        return $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
    }
}
