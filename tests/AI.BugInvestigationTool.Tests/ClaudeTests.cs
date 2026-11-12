using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AI.BugInvestigationTool.Configuration;
using AI.BugInvestigationTool.Services.Claude;

namespace AI.BugInvestigationTool.Tests;

public class ClaudeClientTests
{
    private static readonly ClaudeTool Submit = new()
    {
        Name = "submit_result",
        Description = "done",
        Schema = ClaudeTool.Obj(("answer", ClaudeTool.Str("a", 50), true), ("score", ClaudeTool.Num("s", 0, 1), true)),
    };

    private static ClaudeClient Client(FakeHttp http, ToolOptions? options = null) =>
        new(new HttpClient(http) { BaseAddress = new Uri("https://api.anthropic.com/") }, "sk-ant-test-key-0000000000", options ?? new ToolOptions { ClaudeTimeoutSeconds = 5 });

    [Fact]
    public async Task Sends_well_formed_request_and_returns_validated_submit_input()
    {
        var http = new FakeHttp();
        var script = new ClaudeScript().ThenToolUse("submit_result", new { answer = "42", score = 0.9 });
        script.Register(http);

        var result = await Client(http).RunAsync("system prompt", [ClaudeClient.Text("hi")], [], Submit, null, CancellationToken.None);

        Assert.Equal("42", result.GetProperty("answer").GetString());
        var call = http.Calls.Single();
        Assert.Equal("sk-ant-test-key-0000000000", call.Headers["x-api-key"]);
        Assert.Equal("2026-10-21", call.Headers["anthropic-version"]);
        var req = script.Requests[0];
        Assert.Equal("claude-opus-5-5", req["model"]!.GetValue<string>());
        Assert.Equal("auto", req["tool_choice"]!["type"]!.GetValue<string>());
        Assert.Equal("high", req["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("default", req["fallbacks"]!.GetValue<string>());
        Assert.Null(req["temperature"]);
        Assert.Equal("submit_result", req["tools"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Executes_valid_tool_requests_and_feeds_results_back_with_thinking_preserved()
    {
        var tool = new ClaudeTool { Name = "lookup", Description = "d", Schema = ClaudeTool.Obj(("q", ClaudeTool.Str("q", 20), true)) };
        var http = new FakeHttp();
        var script = new ClaudeScript()
            .ThenToolUse("lookup", new { q = "orders" }, "toolu_1")
            .ThenToolUse("submit_result", new { answer = "found", score = 1 });
        script.Register(http);
        var executed = new List<string>();

        var result = await Client(http).RunAsync("s", [ClaudeClient.Text("go")], [tool], Submit,
            (name, input, _) => { executed.Add(input.GetProperty("q").GetString()!); return Task.FromResult(new ToolOutput("3 results")); },
            CancellationToken.None);

        Assert.Equal(["orders"], executed);
        Assert.Equal("found", result.GetProperty("answer").GetString());
        var second = script.Requests[1]["messages"]!.AsArray();
        Assert.Equal(3, second.Count);
        Assert.Equal("thinking", second[1]!["content"]![0]!["type"]!.GetValue<string>()); // assistant content echoed unchanged
        var toolResult = second[2]!["content"]![0]!;
        Assert.Equal("toolu_1", toolResult["tool_use_id"]!.GetValue<string>());
        Assert.Equal("3 results", toolResult["content"]!.GetValue<string>());
        Assert.False(toolResult["is_error"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Rejects_unknown_tools_and_invalid_input_without_executing_them()
    {
        var tool = new ClaudeTool { Name = "lookup", Description = "d", Schema = ClaudeTool.Obj(("q", ClaudeTool.Str("q", 5), true)) };
        var http = new FakeHttp();
        var script = new ClaudeScript()
            .ThenToolUse("run_shell", new { cmd = "rm -rf /" }, "toolu_a")
            .ThenToolUse("lookup", new { q = "way too long" }, "toolu_b")
            .ThenToolUse("submit_result", new { answer = "ok", score = 2 }, "toolu_c")   // score out of range
            .ThenToolUse("submit_result", new { answer = "ok", score = 0.5 });
        script.Register(http);
        var executions = 0;

        var result = await Client(http).RunAsync("s", [ClaudeClient.Text("go")], [tool], Submit,
            (_, _, _) => { executions++; return Task.FromResult(new ToolOutput("x")); }, CancellationToken.None);

        Assert.Equal(0, executions);
        Assert.Equal(0.5, result.GetProperty("score").GetDouble());
        string ErrorOf(int request) => script.Requests[request]["messages"]!.AsArray()[^1]!["content"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("Unknown tool 'run_shell'", ErrorOf(1));
        Assert.Contains("exceeds 5 characters", ErrorOf(2));
        Assert.Contains("score must be <= 1", ErrorOf(3));
    }

    [Fact]
    public async Task Nudges_when_claude_answers_in_text_and_fails_on_refusal()
    {
        var http = new FakeHttp();
        new ClaudeScript().Then(_ => ClaudeScript.Text("Here is my answer")).ThenToolUse("submit_result", new { answer = "a", score = 0 }).Register(http);
        var ok = await Client(http).RunAsync("s", [ClaudeClient.Text("go")], [], Submit, null, CancellationToken.None);
        Assert.Equal("a", ok.GetProperty("answer").GetString());

        var http2 = new FakeHttp();
        new ClaudeScript().Then(_ => ClaudeScript.Text("", "refusal")).Register(http2);
        var ex = await Assert.ThrowsAsync<ClaudeException>(() => Client(http2).RunAsync("s", [ClaudeClient.Text("go")], [], Submit, null, CancellationToken.None));
        Assert.Contains("refusal", ex.Message);

        var http3 = new FakeHttp();
        new ClaudeScript().Then(_ => ClaudeScript.Text("cut", "max_tokens")).Register(http3);
        await Assert.ThrowsAsync<ClaudeException>(() => Client(http3).RunAsync("s", [ClaudeClient.Text("go")], [], Submit, null, CancellationToken.None));
    }

    [Fact]
    public async Task Retries_overloaded_and_reports_auth_errors()
    {
        var attempts = 0;
        var http = new FakeHttp().On(_ => true, (_, _) => ++attempts < 2
            ? FakeHttp.Json("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", (HttpStatusCode)529)
            : FakeHttp.Json(ClaudeScript.ToolUse("submit_result", new { answer = "a", score = 0 })));
        await Client(http).RunAsync("s", [ClaudeClient.Text("go")], [], Submit, null, CancellationToken.None);
        Assert.Equal(2, attempts);

        var http2 = new FakeHttp().On(_ => true, (_, _) => FakeHttp.Json("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<ClaudeException>(() => Client(http2).RunAsync("s", [ClaudeClient.Text("go")], [], Submit, null, CancellationToken.None));
        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain("sk-ant-test", ex.Message);
    }

    [Fact]
    public async Task Stops_after_the_turn_budget_and_honours_cancellation()
    {
        var tool = new ClaudeTool { Name = "lookup", Description = "d", Schema = ClaudeTool.Obj() };
        var http = new FakeHttp().On(_ => true, (_, _) => FakeHttp.Json(ClaudeScript.ToolUse("lookup", new { })));
        var ex = await Assert.ThrowsAsync<ClaudeException>(() => Client(http).RunAsync("s", [ClaudeClient.Text("go")], [tool], Submit,
            (_, _, _) => Task.FromResult(new ToolOutput("again")), CancellationToken.None, maxTurns: 3));
        Assert.Contains("3 tool turns", ex.Message);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).RunAsync("s", [ClaudeClient.Text("go")], [tool], Submit, null, cts.Token));
    }

    [Fact]
    public void Omits_fallbacks_for_models_that_do_not_support_them()
    {
        var client = Client(new FakeHttp(), new ToolOptions { ClaudeModel = "claude-haiku-4-5", ClaudeEffort = "" });
        var body = client.BuildRequest("s", [], [Submit]);
        Assert.Null(body["fallbacks"]);
        Assert.Null(body["output_config"]);
    }
}

public class ToolValidationTests
{
    [Fact]
    public void Validates_types_required_enums_ranges_and_unknown_properties()
    {
        var t = InvestigationTools.SubmitPrReview;
        var valid = JsonDocument.Parse("""
            {"summary":"s","findings":[{"title":"t","severity":"Critical","category":"correctness","file":"/a.cs","start_line":3,"end_line":4,"detail":"d","recommendation":"r"}],
             "missing_tests":[],"recommendations":[],"overall_assessment":"ok","confidence":0.8}
            """).RootElement;
        Assert.Null(t.Validate(valid));

        var invalid = JsonDocument.Parse("""
            {"summary":"s","findings":[{"title":"t","severity":"Blocker","category":"correctness","file":"/a.cs","start_line":"3","end_line":4,"detail":"d","recommendation":"r","extra":1}],
             "missing_tests":[],"recommendations":[],"confidence":0.8}
            """).RootElement;
        var error = t.Validate(invalid)!;
        Assert.Contains("severity must be one of", error);
        Assert.Contains("start_line must be an integer", error);
        Assert.Contains("extra is not allowed", error);
        Assert.Contains("overall_assessment is required", error);
    }

    [Fact]
    public void Optional_null_values_are_accepted()
    {
        var input = JsonDocument.Parse("""{"url":"/orders"}""").RootElement;
        Assert.Null(InvestigationTools.BrowserNavigate.Validate(input));
        var login = JsonDocument.Parse("""{"username_selector":null}""").RootElement;
        Assert.Null(InvestigationTools.BrowserLogin.Validate(login));
    }

    [Fact]
    public void Browser_press_only_allows_listed_keys()
    {
        Assert.NotNull(InvestigationTools.BrowserPress.Validate(JsonDocument.Parse("""{"key":"Control+Shift+I"}""").RootElement));
        Assert.Null(InvestigationTools.BrowserPress.Validate(JsonDocument.Parse("""{"key":"Enter"}""").RootElement));
    }

    [Fact]
    public async Task Tools_without_a_configured_service_fail_safely()
    {
        var tools = new InvestigationTools();
        var nav = await tools.ExecuteAsync("browser_navigate", JsonDocument.Parse("""{"url":"https://evil.example.net"}""").RootElement, CancellationToken.None);
        Assert.True(nav.IsError);
        var login = await new InvestigationTools(browser: new FakeBrowserSession()).ExecuteAsync("browser_login", JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        Assert.True(login.IsError);
        Assert.Contains("No environment credentials", login.Content);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tools.ExecuteAsync("search_code", JsonDocument.Parse("""{"query":"x"}""").RootElement, CancellationToken.None));
    }

    [Fact]
    public async Task Get_file_returns_numbered_untrusted_source_and_records_inspection()
    {
        var http = new FakeHttp().On(_ => true, (_, _) => FakeHttp.Json(JsonSerializer.Serialize(new { content = string.Join("\n", Enumerable.Range(1, 900).Select(i => $"code {i}")) })));
        var tools = new InvestigationTools(ado: new Services.AzureDevOps.AzureDevOpsClient(new HttpClient(http), "Acme", "Shop", "pat"));

        var output = await tools.ExecuteAsync("get_file", JsonDocument.Parse("""{"repository":"shop-api","path":"src/A.cs","start_line":10,"end_line":2000}""").RootElement, CancellationToken.None);

        Assert.Contains("<untrusted_data source=\"source:shop-api:src/A.cs\">", output.Content);
        Assert.Contains("   10 | code 10", output.Content);
        Assert.Contains("  409 | code 409", output.Content);
        Assert.DoesNotContain("  410 | code 410", output.Content); // 400-line cap
        Assert.True(tools.WasInspected("shop-api", "/src/A.cs", 850));
        Assert.False(tools.WasInspected("shop-api", "/src/A.cs", 901));
        Assert.False(tools.WasInspected("other", "/src/A.cs", 1));
    }
}
