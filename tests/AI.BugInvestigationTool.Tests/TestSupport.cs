using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AI.BugInvestigationTool.Configuration;
using AI.BugInvestigationTool.Services.Browser;
using AI.BugInvestigationTool.Services.Workflow;
using AI.BugInvestigationTool.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AI.BugInvestigationTool.Tests;

/// <summary>Records every request and answers from an ordered list of routes. Unmatched requests fail loudly.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public sealed record Call(HttpMethod Method, Uri Url, string Body, IReadOnlyDictionary<string, string> Headers);

    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, string, HttpResponseMessage> Respond)> _routes = [];
    public List<Call> Calls { get; } = [];

    public FakeHttp On(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        _routes.Add((match, respond));
        return this;
    }

    public FakeHttp OnJson(HttpMethod method, string urlContains, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(r => r.Method == method && r.RequestUri!.ToString().Contains(urlContains, StringComparison.Ordinal), (_, _) => Json(json, status));

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        lock (Calls) Calls.Add(new Call(request.Method, request.RequestUri!, body, headers));
        foreach (var (match, respond) in _routes)
            if (match(request)) return respond(request, body);
        return new HttpResponseMessage(HttpStatusCode.NotImplemented) { Content = new StringContent($"No fake route for {request.Method} {request.RequestUri}") };
    }
}

public sealed class FakeHttpClientFactory(FakeHttp handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>Scripted Claude Messages API: each request is answered by the next response builder in order.</summary>
public sealed class ClaudeScript
{
    private readonly Queue<Func<JsonObject, string>> _responses = new();
    public List<JsonObject> Requests { get; } = [];

    public ClaudeScript Then(Func<JsonObject, string> respond) { _responses.Enqueue(respond); return this; }
    public ClaudeScript ThenToolUse(string name, object input, string id = "") => Then(_ => ToolUse(name, input, id));

    public void Register(FakeHttp http) => http.On(r => r.RequestUri!.AbsolutePath.EndsWith("/v1/messages", StringComparison.Ordinal), (_, body) =>
    {
        var req = JsonNode.Parse(body)!.AsObject();
        lock (Requests) Requests.Add(req);
        if (_responses.Count == 0) return FakeHttp.Json("""{"type":"error","error":{"type":"invalid_request_error","message":"script exhausted"}}""", HttpStatusCode.BadRequest);
        return FakeHttp.Json(_responses.Dequeue()(req));
    });

    public static string ToolUse(string name, object input, string id = "") => JsonSerializer.Serialize(new
    {
        id = "msg_" + Guid.NewGuid().ToString("N")[..8],
        type = "message",
        role = "assistant",
        stop_reason = "tool_use",
        content = new object[]
        {
            new { type = "thinking", thinking = "", signature = "sig" },
            new { type = "tool_use", id = string.IsNullOrEmpty(id) ? "toolu_" + Guid.NewGuid().ToString("N")[..8] : id, name, input },
        },
    });

    public static string Text(string text, string stop = "end_turn") => JsonSerializer.Serialize(new
    {
        id = "msg_x", type = "message", role = "assistant", stop_reason = stop,
        content = new object[] { new { type = "text", text } },
    });
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bit-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { /* screenshots may still be locked briefly */ } }
}

public static class TestEngine
{
    public static ToolOptions Options(string evidenceRoot) => new()
    {
        EvidenceRoot = evidenceRoot,
        AllowedEnvironmentHosts = ["127.0.0.1", "qa.example.com"],
        ClaudeTimeoutSeconds = 10,
        BrowserTimeoutSeconds = 10,
    };

    public static InvestigationEngine Create(FakeHttp http, string evidenceRoot, IBrowserLauncher? browser = null, ToolOptions? options = null)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(options ?? Options(evidenceRoot));
        return new InvestigationEngine(new InvestigationStore(opts), new FakeHttpClientFactory(http), browser ?? new PlaywrightBrowserLauncher(), opts,
            NullLogger<InvestigationEngine>.Instance);
    }

    public static Credentials Credentials(string? envUrl = "https://qa.example.com/") => new()
    {
        JiraBaseUrl = "https://acme.atlassian.net",
        JiraEmail = "qa@acme.test",
        JiraApiToken = "jira-secret-token-123",
        AdoOrganization = "Acme",
        AdoProject = "Shop",
        AdoPat = "ado-secret-pat-456",
        ClaudeApiKey = "sk-ant-test-key-0000000000",
        EnvironmentUrl = envUrl,
        EnvironmentUsername = "tester",
        EnvironmentPassword = "P@ssw0rd-secret",
    };

    public static string Fixture(string name) => File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
