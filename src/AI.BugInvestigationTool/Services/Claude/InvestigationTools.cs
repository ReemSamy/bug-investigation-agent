using System.Text;
using System.Text.Json;
using AI.BugInvestigationTool.Models;
using AI.BugInvestigationTool.Services.AzureDevOps;
using AI.BugInvestigationTool.Services.Browser;
using AI.BugInvestigationTool.Services.Jira;
using static AI.BugInvestigationTool.Services.Claude.ClaudeTool;

namespace AI.BugInvestigationTool.Services.Claude;

/// <summary>
/// The approved operations Claude may request, and their execution through the real services.
/// Claude never gets a shell, file system or free URL access: only these named, schema-validated operations.
/// </summary>
public sealed class InvestigationTools
{
    private readonly AzureDevOpsClient? _ado;
    private readonly JiraClient? _jira;
    private readonly IBrowserSession? _browser;
    private readonly Func<string>? _testResults;
    private readonly string? _defaultRepository;
    private readonly string? _defaultVersion;
    private readonly bool _defaultVersionIsCommit;
    private readonly Credentials? _credentials;

    /// <summary>Files Claude actually opened (repo|path → line count) — used to verify code references.</summary>
    public Dictionary<string, int> InspectedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Log { get; } = [];

    public sealed record Credentials(string Username, string Password);

    public InvestigationTools(AzureDevOpsClient? ado = null, JiraClient? jira = null, IBrowserSession? browser = null,
        Func<string>? testResults = null, string? defaultRepository = null, string? defaultVersion = null, bool defaultVersionIsCommit = false,
        Credentials? environmentLogin = null)
    {
        _ado = ado;
        _jira = jira;
        _browser = browser;
        _testResults = testResults;
        _defaultRepository = defaultRepository;
        _defaultVersion = defaultVersion;
        _defaultVersionIsCommit = defaultVersionIsCommit;
        _credentials = environmentLogin;
    }

    // ---------------- tool definitions ----------------

    public static readonly ClaudeTool ListRepositories = new()
    {
        Name = "list_repositories", Description = "List the Azure DevOps repositories in the configured project.", Schema = Obj(),
    };

    public static readonly ClaudeTool SearchCode = new()
    {
        Name = "search_code",
        Description = "Search source code in Azure DevOps (code search syntax, e.g. 'InvoiceService' or 'def:Calculate'). Returns repository and file paths.",
        Schema = Obj(("query", Str("Search text", 200), true), ("repository", Str("Optional repository name filter", 200), false)),
    };

    public static readonly ClaudeTool GetFile = new()
    {
        Name = "get_file",
        Description = "Read a source file with line numbers. Use start_line/end_line to read a region (max 400 lines per call).",
        Schema = Obj(("repository", Str("Repository name", 200), true), ("path", Str("File path, e.g. /src/App/Foo.cs", 500), true),
            ("start_line", Int("First line (1-based)", 1, 1_000_000), false), ("end_line", Int("Last line", 1, 1_000_000), false),
            ("branch", Str("Optional branch name (default branch if omitted)", 200), false)),
    };

    public static readonly ClaudeTool ListCommits = new()
    {
        Name = "list_commits",
        Description = "List recent commits for a repository, optionally limited to a file path.",
        Schema = Obj(("repository", Str("Repository name", 200), true), ("path", Str("Optional file path", 500), false)),
    };

    public static readonly ClaudeTool SearchJira = new()
    {
        Name = "search_jira",
        Description = "Search Jira issues with JQL (read-only), e.g. to find related or duplicate bugs.",
        Schema = Obj(("jql", Str("JQL query", 500), true)),
    };

    public static readonly ClaudeTool GetTestResults = new()
    {
        Name = "get_test_results", Description = "Get the results of the existing tests that were run for this pull request.", Schema = Obj(),
    };

    public static readonly ClaudeTool BrowserNavigate = new()
    {
        Name = "browser_navigate",
        Description = "Navigate to a URL or path within the allowed test environment hosts.",
        Schema = Obj(("url", Str("Absolute URL on an allowed host, or a path relative to the current page", 1000), true)),
    };

    public static readonly ClaudeTool BrowserClick = new()
    {
        Name = "browser_click",
        Description = "Click an element. target is a Playwright selector: CSS (#id, button[name=save]) or text=Visible label.",
        Schema = Obj(("target", Str("Selector", 300), true)),
    };

    public static readonly ClaudeTool BrowserFill = new()
    {
        Name = "browser_fill",
        Description = "Type a value into an input or textarea (not password fields).",
        Schema = Obj(("target", Str("Selector", 300), true), ("value", Str("Value to type", 2000), true)),
    };

    public static readonly ClaudeTool BrowserSelect = new()
    {
        Name = "browser_select",
        Description = "Choose an option in a <select> element by value or label.",
        Schema = Obj(("target", Str("Selector", 300), true), ("value", Str("Option value or label", 300), true)),
    };

    public static readonly ClaudeTool BrowserPress = new()
    {
        Name = "browser_press",
        Description = "Press a keyboard key on the focused element.",
        Schema = Obj(("key", Str("Key", 20, "Enter", "Tab", "Escape", "ArrowDown", "ArrowUp", "Space"), true)),
    };

    public static readonly ClaudeTool BrowserReadPage = new()
    {
        Name = "browser_read_page", Description = "Read the current page: URL, title, interactive elements with selectors, and visible text.", Schema = Obj(),
    };

    public static readonly ClaudeTool BrowserScreenshot = new()
    {
        Name = "browser_screenshot",
        Description = "Capture a full-page screenshot as evidence (password fields are masked automatically).",
        Schema = Obj(("label", Str("Short description of what the screenshot shows", 120), true)),
    };

    public static readonly ClaudeTool BrowserErrors = new()
    {
        Name = "browser_get_errors", Description = "Get browser console errors and failed network requests recorded so far.", Schema = Obj(),
    };

    public static readonly ClaudeTool BrowserLogin = new()
    {
        Name = "browser_login",
        Description = "Sign in with the user-supplied environment credentials (you never see them). Optionally pass selectors if the form is unusual.",
        Schema = Obj(("username_selector", Str("Optional selector", 300), false), ("password_selector", Str("Optional selector", 300), false),
            ("submit_selector", Str("Optional selector", 300), false)),
    };

    public static IReadOnlyList<ClaudeTool> CodeTools(bool includeJira) =>
        includeJira ? [ListRepositories, SearchCode, GetFile, ListCommits, SearchJira] : [ListRepositories, SearchCode, GetFile, ListCommits];

    public static IReadOnlyList<ClaudeTool> BrowserTools =>
        [BrowserNavigate, BrowserClick, BrowserFill, BrowserSelect, BrowserPress, BrowserReadPage, BrowserScreenshot, BrowserErrors, BrowserLogin];

    // ---------------- submit (structured result) tools ----------------

    private static readonly System.Text.Json.Nodes.JsonObject CodeRefSchema = Obj(
        ("repository", Str("Repository", 200), false), ("path", Str("File path", 500), true),
        ("start_line", Int("Start line", 1, 1_000_000), false), ("end_line", Int("End line", 1, 1_000_000), false),
        ("note", Str("Why this location matters", 1000), true));

    public static readonly ClaudeTool SubmitTicketUnderstanding = new()
    {
        Name = "submit_ticket_understanding",
        Description = "Submit the structured understanding of the ticket.",
        Schema = Obj(
            ("summary", Str("One-paragraph summary of the problem", 3000), true),
            ("expected_behavior", Str("Expected behaviour, empty if not stated", 3000), true),
            ("actual_behavior", Str("Actual behaviour, empty if not stated", 3000), true),
            ("reproduction_steps", Arr(Str("Step", 500), "Ordered steps", 30), true),
            ("environment_url", Str("Environment URL named in the ticket, or empty", 500), false),
            ("requires_login", Bool("Whether the app requires sign-in"), true),
            ("code_search_terms", Arr(Str("Term", 100), "Code search terms", 10), true),
            ("missing_information", Arr(Obj(("key", Str("camelCase key", 50), true), ("question", Str("Question for the user", 300), true),
                ("required", Bool("Required to reproduce"), true)), "Missing information", 10), true)),
    };

    public static readonly ClaudeTool SubmitReproduction = new()
    {
        Name = "submit_reproduction",
        Description = "Submit the result of the reproduction attempt.",
        Schema = Obj(
            ("reproduced", Str("Whether the defect was observed", 10, "yes", "no", "unclear"), true),
            ("expected_behavior_observed", Bool("True only if the expected behaviour was positively observed"), true),
            ("actual_behavior", Str("What actually happened", 3000), true),
            ("steps_executed", Arr(Str("Step and result", 500), "Steps executed", 40), true),
            ("notes", Str("Additional observations", 3000), false),
            ("clarification_needed", Str("Question for the user if the UI could not be identified", 500), false)),
    };

    public static readonly ClaudeTool SubmitCodeFindings = new()
    {
        Name = "submit_code_findings",
        Description = "Submit what the code investigation found.",
        Schema = Obj(
            ("summary", Str("What the relevant code does", 4000), true),
            ("suspected_root_cause", Str("Suspected root cause (hypothesis unless proven)", 4000), true),
            ("references", Arr(CodeRefSchema, "Code references actually inspected", 30), true)),
    };

    public static readonly ClaudeTool SubmitInvestigationReport = new()
    {
        Name = "submit_investigation_report",
        Description = "Submit the final investigation report.",
        Schema = Obj(
            ("outcome", Str("Outcome", 20, "Resolved", "Unresolved", "NotReproducible", "Inconclusive", "Blocked"), true),
            ("summary", Str("Investigation summary", 4000), true),
            ("expected_behavior", Str("Expected behaviour", 3000), true),
            ("actual_behavior", Str("Actual behaviour observed", 3000), true),
            ("reproduction_result", Str("Reproduction result", 3000), true),
            ("root_cause", Str("Root cause analysis", 5000), true),
            ("root_cause_status", Str("confirmed or hypothesis", 12, "confirmed", "hypothesis"), true),
            ("code_references", Arr(CodeRefSchema, "Relevant code references", 30), true),
            ("recommendations", Arr(Str("Specific change", 1500), "Recommended changes", 20), true),
            ("evidence_references", Arr(Str("Evidence file or item", 300), "Evidence used", 30), false),
            ("confidence", Num("Confidence 0..1", 0, 1), true),
            ("limitations", Arr(Str("Limitation", 500), "Limitations", 20), true),
            ("proposed_jira_action", Str("Proposed Jira action", 10, "none", "Reopen", "Close"), true)),
    };

    public static readonly ClaudeTool SubmitPrReview = new()
    {
        Name = "submit_pr_review",
        Description = "Submit the pull request review.",
        Schema = Obj(
            ("summary", Str("PR summary and purpose of the change", 4000), true),
            ("findings", Arr(Obj(
                ("title", Str("Short title", 200), true),
                ("severity", Str("Severity", 10, "Critical", "Medium", "Low"), true),
                ("category", Str("Category", 30, "correctness", "security", "null-safety", "error-handling", "performance", "regression", "test-coverage", "maintainability"), true),
                ("file", Str("Repo-relative path as in the diff", 500), true),
                ("start_line", Int("Start line (new file)", 1, 1_000_000), true),
                ("end_line", Int("End line (new file)", 1, 1_000_000), true),
                ("detail", Str("What is wrong and the potential root cause/impact", 3000), true),
                ("recommendation", Str("Concrete fix", 2000), true)), "Findings", 40), true),
            ("missing_tests", Arr(Str("Missing test", 500), "Missing tests", 20), true),
            ("recommendations", Arr(Str("Recommendation", 1000), "General recommendations", 20), true),
            ("overall_assessment", Str("Merge readiness in plain language", 3000), true),
            ("confidence", Num("Confidence 0..1", 0, 1), true)),
    };

    // ---------------- execution ----------------

    public async Task<ToolOutput> ExecuteAsync(string name, JsonElement input, CancellationToken ct)
    {
        string S(string p) => input.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        int? I(string p) => input.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        Log.Add($"{name} {Summarize(input)}");

        switch (name)
        {
            case "list_repositories":
                RequireAdo();
                var repos = await _ado!.ListRepositoriesAsync(ct);
                return new(string.Join("\n", repos.Select(r => $"{r.Name} (default branch: {r.DefaultBranch})")));

            case "search_code":
                RequireAdo();
                var hits = await _ado!.SearchCodeAsync(S("query"), NullIfEmpty(S("repository")), 25, ct);
                return new(hits.Count == 0 ? "No results." : string.Join("\n", hits.Select(h => $"{h.Repository}: {h.Path}")));

            case "get_file":
            {
                RequireAdo();
                var repo = S("repository");
                var path = S("path");
                var branch = NullIfEmpty(S("branch"));
                var useDefaultVersion = branch is null && _defaultVersion is not null && repo.Equals(_defaultRepository, StringComparison.OrdinalIgnoreCase);
                var text = await _ado!.GetFileAsync(repo, path, useDefaultVersion ? _defaultVersion : branch, useDefaultVersion && _defaultVersionIsCommit, ct);
                if (text is null) return new($"File not found: {repo}:{path}", true);
                var lines = text.Replace("\r\n", "\n").Split('\n');
                var start = Math.Max(1, I("start_line") ?? 1);
                var end = Math.Min(lines.Length, I("end_line") ?? start + 399);
                if (end - start > 399) end = start + 399;
                InspectedFiles[$"{repo}|{Normalize(path)}"] = lines.Length;
                var sb = new StringBuilder($"{repo}:{path} (lines {start}-{end} of {lines.Length})\n");
                for (var i = start; i <= end; i++) sb.Append(i.ToString().PadLeft(5)).Append(" | ").Append(lines[i - 1]).Append('\n');
                return new(Untrusted.Wrap($"source:{repo}:{path}", sb.ToString(), 60000));
            }

            case "list_commits":
                RequireAdo();
                var commits = await _ado!.GetCommitsAsync(S("repository"), NullIfEmpty(S("path")), 15, ct);
                return new(Untrusted.Wrap("commits", string.Join("\n", commits)));

            case "search_jira":
                if (_jira is null) return new("Jira is not configured.", true);
                var issues = await _jira.SearchAsync(S("jql"), 20, ct);
                return new(Untrusted.Wrap("jira-search", issues.Count == 0 ? "No issues." : string.Join("\n", issues)));

            case "get_test_results":
                return new(_testResults?.Invoke() ?? "No tests were run.");
        }

        if (name.StartsWith("browser_", StringComparison.Ordinal))
        {
            if (_browser is null) return new("No browser session is available.", true);
            switch (name)
            {
                case "browser_navigate": return Result(await _browser.NavigateAsync(S("url"), ct));
                case "browser_click": return Result(await _browser.ClickAsync(S("target"), ct));
                case "browser_fill": return Result(await _browser.FillAsync(S("target"), S("value"), ct));
                case "browser_select": return Result(await _browser.SelectAsync(S("target"), S("value"), ct));
                case "browser_press": return Result(await _browser.PressAsync(S("key"), ct));
                case "browser_read_page": return new(Untrusted.Wrap("web-page", await _browser.ReadPageAsync(ct), 14000));
                case "browser_screenshot":
                    var shot = await _browser.ScreenshotAsync(S("label"), ct);
                    return new($"Saved screenshot {shot.File}");
                case "browser_get_errors":
                    return new(Untrusted.Wrap("browser-errors",
                        $"Console errors:\n{string.Join("\n", _browser.ConsoleErrors.TakeLast(30))}\nNetwork failures:\n{string.Join("\n", _browser.NetworkFailures.TakeLast(30))}"));
                case "browser_login":
                    if (_credentials is null) return new("No environment credentials were supplied by the user.", true);
                    var ok = await _browser.LoginAsync(_credentials.Username, _credentials.Password,
                        NullIfEmpty(S("username_selector")), NullIfEmpty(S("password_selector")), NullIfEmpty(S("submit_selector")), ct);
                    return new(ok ? "Login appears successful (no password field visible)." : "Login did not succeed (password field still visible).", !ok);
            }
        }

        return new($"Unknown tool '{name}'.", true);
    }

    /// <summary>True when Claude actually opened this file (and the line is within it).</summary>
    public bool WasInspected(string? repository, string path, int? line)
    {
        var norm = Normalize(path);
        foreach (var (key, count) in InspectedFiles)
        {
            var parts = key.Split('|');
            if (!parts[1].Equals(norm, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(repository) && !parts[0].Equals(repository, StringComparison.OrdinalIgnoreCase)) continue;
            return line is null || line <= count;
        }
        return false;
    }

    private static ToolOutput Result(string s) => new(s, s.StartsWith("FAILED", StringComparison.Ordinal) || s.StartsWith("Rejected", StringComparison.Ordinal));
    private void RequireAdo() { if (_ado is null) throw new InvalidOperationException("Azure DevOps is not configured."); }
    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    internal static string Normalize(string path) => "/" + path.Replace('\\', '/').TrimStart('/');

    private static string Summarize(JsonElement input)
    {
        var raw = input.GetRawText();
        return raw.Length > 200 ? raw[..200] + "…" : raw;
    }
}
