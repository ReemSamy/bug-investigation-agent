using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AI.BugInvestigationTool.Configuration;
using AI.BugInvestigationTool.Models;
using AI.BugInvestigationTool.Services.AzureDevOps;
using AI.BugInvestigationTool.Services.Browser;
using AI.BugInvestigationTool.Services.Claude;
using AI.BugInvestigationTool.Services.Jira;
using AI.BugInvestigationTool.Storage;
using Microsoft.Extensions.Options;

namespace AI.BugInvestigationTool.Services.Workflow;

public sealed record JiraPublishRequest(bool PostComment, string? CommentText, bool AttachScreenshots, string? TransitionName);

/// <summary>
/// The one investigation engine. The CLI (slash command) and the Razor Pages UI both call these methods;
/// there is no other implementation of the workflow.
/// </summary>
public sealed class InvestigationEngine(
    InvestigationStore store,
    IHttpClientFactory httpFactory,
    IBrowserLauncher browserLauncher,
    IOptions<ToolOptions> toolOptions,
    ILogger<InvestigationEngine> logger)
{
    private readonly ToolOptions _options = toolOptions.Value;

    /// <summary>Optional observer (the CLI prints progress to stderr with it).</summary>
    public Action<Investigation, ProgressEntry>? OnProgress { get; set; }

    public InvestigationStore Store => store;

    // ======================= entry points =======================

    public async Task<Investigation> CreateAsync(string url, CancellationToken ct = default)
    {
        var target = UrlParser.Parse(url);
        var inv = new Investigation { Id = InvestigationStore.NewId(target), Target = target };
        await store.SaveAsync(inv, ct);
        return inv;
    }

    /// <summary>Create + run in one call (CLI).</summary>
    public async Task<Investigation> InvestigateAsync(string url, Credentials credentials, IDictionary<string, string>? userInfo, CancellationToken ct)
    {
        var inv = await CreateAsync(url, ct);
        return await RunAsync(inv.Id, credentials, userInfo, ct);
    }

    /// <summary>Runs (or resumes, after the user supplied missing information) an investigation.</summary>
    public async Task<Investigation> RunAsync(string id, Credentials credentials, IDictionary<string, string>? userInfo, CancellationToken ct)
    {
        var inv = await store.LoadAsync(id, ct) ?? throw new ArgumentException($"Investigation '{id}' not found.");
        foreach (var (k, v) in userInfo ?? new Dictionary<string, string>())
            if (!string.IsNullOrWhiteSpace(v)) inv.UserInfo[k] = v.Trim();

        var redactor = new SecretRedactor(credentials.SecretValues());
        inv.Error = null;
        inv.Report = null;
        inv.MissingInfo = [];
        inv.Browser = null;
        inv.CodeFindings = null;
        inv.Tests = null;
        inv.JiraCommentDraft = null;
        inv.Evidence = [];

        try
        {
            if (inv.Target.Kind == TargetKind.JiraIssue) await RunJiraWorkflowAsync(inv, credentials, redactor, ct);
            else await RunPullRequestWorkflowAsync(inv, credentials, redactor, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            inv.Error = "Investigation was cancelled.";
            await StepAsync(inv, InvestigationStep.Cancelled, "Cancelled by the user.", redactor, CancellationToken.None);
        }
        catch (Exception ex)
        {
            inv.Error = redactor.Redact(ex is ExternalServiceException or ClaudeException or InvalidOperationException or ArgumentException
                ? ex.Message : $"Unexpected error ({ex.GetType().Name}): {ex.Message}");
            logger.LogWarning("Investigation {Id} failed: {Error}", inv.Id, inv.Error);
            await StepAsync(inv, InvestigationStep.Failed, inv.Error, redactor, CancellationToken.None);
        }

        await store.WriteTextAsync(inv.Id, "report.md", redactor.Redact(ReportFormatter.ToMarkdown(inv)), CancellationToken.None);
        return inv;
    }

    // ======================= Workflow A: Jira bug =======================

    private async Task RunJiraWorkflowAsync(Investigation inv, Credentials creds, SecretRedactor redactor, CancellationToken ct)
    {
        var claude = CreateClaude(creds);
        var jira = CreateJira(creds, inv.Target);
        var folder = store.FolderOf(inv.Id);

        // Step 1: Jira data collection
        await StepAsync(inv, InvestigationStep.JiraDataCollection, $"Loading {inv.Target.JiraKey} from Jira (fields, comments, changelog, attachments).", redactor, ct);
        inv.Issue = await jira.GetIssueAsync(inv.Target.JiraKey!, ct);
        await DownloadAttachmentsAsync(inv, jira, folder, ct);
        await StepAsync(inv, InvestigationStep.JiraDataCollection,
            $"Loaded {inv.Issue.Key}: {inv.Issue.Comments.Count} comments, {inv.Issue.History.Count} history items, {inv.Issue.Attachments.Count} attachments.", redactor, ct);

        // Step 2: ticket understanding + missing information
        if (inv.Understanding is null || inv.UserInfo.Count == 0)
        {
            await StepAsync(inv, InvestigationStep.ContextValidation, "Claude: reading the ticket.", redactor, ct);
            inv.Understanding = await UnderstandTicketAsync(claude, inv, folder, ct);
        }
        var ctx = MissingInfoDetector.Resolve(inv.Issue, inv.Understanding, inv.UserInfo, creds);
        var allowedHosts = AllowedHosts(creds);
        inv.MissingInfo = MissingInfoDetector.Detect(ctx, inv.Understanding, inv.UserInfo, creds, allowedHosts);
        if (inv.MissingInfo.Any(m => m.Required))
        {
            await StepAsync(inv, InvestigationStep.WaitingForInput,
                "Required information is missing: " + string.Join(", ", inv.MissingInfo.Where(m => m.Required).Select(m => m.Key)) + ". Browser testing was not started.", redactor, ct);
            return;
        }
        await StepAsync(inv, InvestigationStep.ContextValidation, "Context complete.", redactor, ct);

        // Step 3: browser investigation
        await StepAsync(inv, InvestigationStep.BrowserInvestigation, $"Opening {StripQuery(ctx.EnvironmentUrl!)} in an isolated browser context.", redactor, ct);
        inv.Browser = await RunBrowserAsync(inv, claude, ctx, creds, allowedHosts, folder, redactor, ct);
        if (inv.Browser.ClarificationNeeded is { Length: > 0 } question && !inv.UserInfo.ContainsKey("uiClarification"))
        {
            inv.MissingInfo = [new() { Key = "uiClarification", Required = true, Question = question }];
            await StepAsync(inv, InvestigationStep.WaitingForInput, "Claude could not reliably identify the UI and needs clarification.", redactor, ct);
            return;
        }
        await StepAsync(inv, InvestigationStep.BrowserInvestigation,
            inv.Browser.Error is not null ? $"Browser investigation failed: {inv.Browser.Error}"
                : $"Reproduced: {inv.Browser.Reproduced}; {inv.Browser.ConsoleErrors.Count} console errors; {inv.Evidence.Count(e => e.Kind == "screenshot")} screenshots.", redactor, ct);

        // Step 4: code investigation (read-only)
        InvestigationTools? codeTools = null;
        if (creds.HasAdo)
        {
            await StepAsync(inv, InvestigationStep.CodeInvestigation, "Claude: searching Azure DevOps for related code.", redactor, ct);
            codeTools = new InvestigationTools(ado: CreateAdo(creds, null, null), jira: jira);
            var findings = await claude.RunAsync(Prompts.CodeInvestigation, CodeInvestigationInput(inv, ctx),
                InvestigationTools.CodeTools(includeJira: true), InvestigationTools.SubmitCodeFindings, codeTools.ExecuteAsync, ct,
                name => StepAsync(inv, InvestigationStep.CodeInvestigation, $"tool: {name}", redactor, ct));
            inv.CodeFindings = findings.GetRawText();
        }
        else
        {
            await StepAsync(inv, InvestigationStep.CodeInvestigation, "Azure DevOps is not configured — code investigation skipped.", redactor, ct);
        }

        // Step 5: Claude analysis
        await StepAsync(inv, InvestigationStep.ClaudeAnalysis, "Claude: correlating ticket, browser evidence and code.", redactor, ct);
        var report = await claude.RunAsync(Prompts.FinalAnalysis, FinalAnalysisInput(inv, ctx, folder), [], InvestigationTools.SubmitInvestigationReport, null, ct);

        // Step 6: result + Jira comment draft
        await StepAsync(inv, InvestigationStep.ReportGeneration, "Applying outcome rules and generating the report.", redactor, ct);
        inv.Report = MapBugReport(report, codeTools);
        if (!creds.HasAdo) inv.Report.Limitations.Add("Azure DevOps was not configured, so no source code was inspected.");
        OutcomeClassifier.Apply(inv.Report, inv.Browser, ctx.RequiresLogin);
        RedactReport(inv.Report, redactor);
        inv.JiraCommentDraft = redactor.Redact(ReportFormatter.JiraCommentText(inv));
        await StepAsync(inv, InvestigationStep.Completed, $"Outcome: {ReportFormatter.OutcomeLabel(inv.Report.Outcome)}. Jira updates await your approval.", redactor, ct);
    }

    private async Task DownloadAttachmentsAsync(Investigation inv, JiraClient jira, string folder, CancellationToken ct)
    {
        var dir = Path.Combine(folder, "attachments");
        foreach (var att in inv.Issue!.Attachments.Take(_options.MaxAttachments))
        {
            var isImage = att.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            var isText = att.MimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || att.MimeType is "application/json" or "application/xml" ||
                         Path.GetExtension(att.FileName).ToLowerInvariant() is ".log" or ".txt" or ".csv" or ".json" or ".xml";
            if (!isImage && !isText) continue;

            var bytes = await jira.DownloadAttachmentAsync(att, _options.MaxAttachmentBytes, ct);
            if (bytes is null) continue;
            Directory.CreateDirectory(dir);
            var safe = $"{att.Id}-{string.Concat(att.FileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))}";
            await File.WriteAllBytesAsync(Path.Combine(dir, safe), bytes, ct);
            att.LocalFile = $"attachments/{safe}";
            if (isText) att.Text = JiraClient.Truncate(Encoding.UTF8.GetString(bytes), 8000);
            inv.Evidence.Add(new EvidenceItem { Kind = "attachment", Label = att.FileName, File = att.LocalFile });
        }
    }

    private async Task<TicketUnderstanding> UnderstandTicketAsync(ClaudeClient claude, Investigation inv, string folder, CancellationToken ct)
    {
        var content = new JsonArray(ClaudeClient.Text(Untrusted.Wrap("jira-ticket", TicketText(inv.Issue!), 60000)));
        foreach (var att in inv.Issue!.Attachments.Where(a => a.LocalFile is not null && a.MimeType is "image/png" or "image/jpeg" or "image/gif" or "image/webp").Take(4))
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(folder, att.LocalFile!), ct);
            if (bytes.Length > 3_700_000) continue;
            content.Add(ClaudeClient.Text($"Attachment image from the ticket (untrusted data): {att.FileName}"));
            content.Add(ClaudeClient.Image(bytes, att.MimeType));
        }
        if (inv.UserInfo.Count > 0)
            content.Add(ClaudeClient.Text("Information supplied by the user (trusted):\n" + string.Join("\n", inv.UserInfo.Select(kv => $"{kv.Key}: {kv.Value}"))));
        content.Add(ClaudeClient.Text("Call submit_ticket_understanding now."));

        var r = await claude.RunAsync(Prompts.TicketUnderstanding, content, [], InvestigationTools.SubmitTicketUnderstanding, null, ct);
        return new TicketUnderstanding
        {
            Summary = Get(r, "summary"),
            ExpectedBehavior = Get(r, "expected_behavior"),
            ActualBehavior = Get(r, "actual_behavior"),
            ReproductionSteps = GetList(r, "reproduction_steps"),
            EnvironmentUrl = Get(r, "environment_url") is { Length: > 0 } u ? u : null,
            RequiresLogin = r.TryGetProperty("requires_login", out var rl) && rl.ValueKind == JsonValueKind.True,
            CodeSearchTerms = GetList(r, "code_search_terms"),
            MissingInformation = r.TryGetProperty("missing_information", out var mi) && mi.ValueKind == JsonValueKind.Array
                ? mi.EnumerateArray().Select(m => new MissingInfoItem
                {
                    Key = Get(m, "key"), Question = Get(m, "question"),
                    Required = m.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True,
                }).ToList()
                : [],
        };
    }

    private async Task<BrowserResult> RunBrowserAsync(Investigation inv, ClaudeClient claude, EffectiveContext ctx, Credentials creds,
        IReadOnlyCollection<string> allowedHosts, string folder, SecretRedactor redactor, CancellationToken ct)
    {
        var result = new BrowserResult();
        if (!HostPolicy.TryValidateEnvironmentUrl(ctx.EnvironmentUrl, allowedHosts, out var startUrl, out var urlError))
        {
            result.Error = urlError;
            return result;
        }

        IBrowserSession session;
        try
        {
            session = await browserLauncher.LaunchAsync(new BrowserLaunchOptions(startUrl!, allowedHosts, folder, _options.BrowserHeadless,
                _options.BrowserTimeoutSeconds, _options.ScreenshotRedactSelectors), ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Playwright.PlaywrightException or TimeoutException)
        {
            result.Error = redactor.Redact(ex.Message);
            return result;
        }

        await using (session)
        {
            result.Attempted = true;
            try
            {
                await session.ScreenshotAsync("start page", ct);
                if (ctx.RequiresLogin && creds.HasEnvironmentLogin)
                {
                    await StepAsync(inv, InvestigationStep.BrowserInvestigation, "Signing in with the supplied environment credentials.", redactor, ct);
                    result.LoginSucceeded = await session.LoginAsync(creds.EnvironmentUsername!, creds.EnvironmentPassword!, null, null, null, ct);
                    await session.ScreenshotAsync(result.LoginSucceeded ? "after login" : "login failed", ct);
                }
                else
                {
                    result.LoginSucceeded = !ctx.RequiresLogin;
                }

                if (ctx.RequiresLogin && !result.LoginSucceeded)
                {
                    result.Notes = "Sign-in did not succeed, so the reproduction steps were not executed.";
                }
                else
                {
                    var tools = new InvestigationTools(browser: session,
                        environmentLogin: creds.HasEnvironmentLogin ? new(creds.EnvironmentUsername!, creds.EnvironmentPassword!) : null);
                    var repro = await claude.RunAsync(Prompts.BrowserInvestigation, BrowserInput(inv, ctx, session.CurrentUrl),
                        InvestigationTools.BrowserTools, InvestigationTools.SubmitReproduction, tools.ExecuteAsync, ct,
                        name => StepAsync(inv, InvestigationStep.BrowserInvestigation, $"browser: {name}", redactor, ct),
                        _options.MaxBrowserActions);
                    result.Reproduced = Get(repro, "reproduced") is { Length: > 0 } rep ? rep : "unclear";
                    result.ExpectedBehaviorObserved = repro.TryGetProperty("expected_behavior_observed", out var eo) && eo.ValueKind == JsonValueKind.True;
                    result.ActualBehavior = Get(repro, "actual_behavior");
                    result.StepsExecuted = GetList(repro, "steps_executed");
                    result.Notes = Get(repro, "notes");
                    result.ClarificationNeeded = Get(repro, "clarification_needed") is { Length: > 0 } q ? q : null;
                }
                await session.ScreenshotAsync("final state", ct);
            }
            catch (Exception ex) when (ex is Microsoft.Playwright.PlaywrightException or TimeoutException)
            {
                result.Error = redactor.Redact(ex.Message.Split('\n')[0]);
            }
            finally
            {
                result.ConsoleErrors = session.ConsoleErrors.Select(redactor.Redact).ToList();
                result.NetworkFailures = session.NetworkFailures.Select(redactor.Redact).ToList();
                inv.Evidence.AddRange(session.Screenshots);
                if (result.ConsoleErrors.Count > 0)
                    inv.Evidence.Add(new EvidenceItem { Kind = "console", Label = $"{result.ConsoleErrors.Count} browser console error(s)", Detail = result.ConsoleErrors[0] });
                if (result.NetworkFailures.Count > 0)
                    inv.Evidence.Add(new EvidenceItem { Kind = "network", Label = $"{result.NetworkFailures.Count} failed request(s)", Detail = result.NetworkFailures[0] });
            }
        }
        return result;
    }

    // ======================= Workflow B: Azure DevOps PR =======================

    private async Task RunPullRequestWorkflowAsync(Investigation inv, Credentials creds, SecretRedactor redactor, CancellationToken ct)
    {
        var claude = CreateClaude(creds);
        var t = inv.Target;
        var ado = CreateAdo(creds, t.AdoOrganization, t.AdoProject);
        var folder = store.FolderOf(inv.Id);

        await StepAsync(inv, InvestigationStep.PullRequestCollection, $"Loading PR {t.PullRequestId} from {t.AdoRepository} (metadata, iterations, changed files, diffs).", redactor, ct);
        var pr = await ado.GetPullRequestAsync(t.AdoRepository!, t.PullRequestId, _options.MaxPullRequestFiles, _options.MaxDiffFileLines, ct);
        inv.PullRequest = pr;
        await StepAsync(inv, InvestigationStep.PullRequestCollection, $"{pr.Files.Count} changed files (+{pr.Additions}/-{pr.Deletions}); linked: {(pr.LinkedJiraKeys.Count + pr.LinkedWorkItems.Count == 0 ? "none" : string.Join(", ", pr.LinkedJiraKeys.Concat(pr.LinkedWorkItems.Select(w => "#" + w))))}.", redactor, ct);

        JiraClient? jira = null;
        var linked = new StringBuilder();
        if (creds.HasJira && pr.LinkedJiraKeys.Count > 0)
        {
            jira = CreateJira(creds, null);
            foreach (var key in pr.LinkedJiraKeys.Take(3))
            {
                try
                {
                    var issue = await jira.GetIssueAsync(key, ct);
                    linked.AppendLine($"{issue.Key} [{issue.Status}] {issue.Summary}\n{JiraClient.Truncate(issue.Description, 3000)}");
                }
                catch (ExternalServiceException ex) { linked.AppendLine($"{key}: could not be loaded ({ex.Message})"); }
            }
        }

        await StepAsync(inv, InvestigationStep.TestExecution, "Running related existing tests (if a local clone is configured).", redactor, ct);
        inv.Tests = await TestRunner.RunAsync(_options.LocalRepositoryPath, pr, folder, _options.TestTimeoutSeconds, ct);
        if (inv.Tests.OutputFile is not null) inv.Evidence.Add(new EvidenceItem { Kind = "test-output", Label = "dotnet test output", File = inv.Tests.OutputFile });
        await StepAsync(inv, InvestigationStep.TestExecution, inv.Tests.Summary, redactor, ct);

        await StepAsync(inv, InvestigationStep.ClaudeAnalysis, "Claude: reviewing the diff.", redactor, ct);
        var tools = new InvestigationTools(ado: ado, jira: jira, testResults: () => inv.Tests?.Summary ?? "Not run.",
            defaultRepository: t.AdoRepository, defaultVersion: pr.SourceCommit, defaultVersionIsCommit: true);
        var (input, omitted) = PullRequestInput(pr, linked.ToString(), inv.Tests);
        var review = await claude.RunAsync(Prompts.PullRequestReview, input,
            [InvestigationTools.GetFile, InvestigationTools.SearchCode, InvestigationTools.ListCommits, InvestigationTools.GetTestResults],
            InvestigationTools.SubmitPrReview, tools.ExecuteAsync, ct,
            name => StepAsync(inv, InvestigationStep.ClaudeAnalysis, $"tool: {name}", redactor, ct));

        await StepAsync(inv, InvestigationStep.ReportGeneration, "Validating file/line references and generating the report.", redactor, ct);
        inv.Report = MapPrReport(review, pr, tools);
        if (omitted.Count > 0) inv.Report.Limitations.Add($"Diffs not sent to Claude (size limit): {string.Join(", ", omitted)}");
        foreach (var f in pr.Files.Where(f => f.SkippedReason is not null)) inv.Report.Limitations.Add($"{f.Path}: {f.SkippedReason}");
        if (!inv.Tests.Executed) inv.Report.Limitations.Add($"Tests not executed: {inv.Tests.Summary}");
        RedactReport(inv.Report, redactor);
        await StepAsync(inv, InvestigationStep.Completed, $"Review complete: {inv.Report.Findings.Count} findings; advisory verdict {inv.Report.PrVerdict}. Nothing was posted to the PR.", redactor, ct);
    }

    // ======================= approvals (explicit user action only) =======================

    public async Task<List<JiraTransition>> GetJiraTransitionsAsync(string id, Credentials creds, CancellationToken ct)
    {
        var inv = await LoadJiraInvestigationAsync(id, ct);
        return await CreateJira(creds, inv.Target).GetTransitionsAsync(inv.Target.JiraKey!, ct);
    }

    public async Task<Investigation> PublishJiraAsync(string id, Credentials creds, JiraPublishRequest request, CancellationToken ct)
    {
        var inv = await LoadJiraInvestigationAsync(id, ct);
        var report = inv.Report ?? throw new InvalidOperationException("The investigation has no report to publish.");
        var redactor = new SecretRedactor(creds.SecretValues());
        var jira = CreateJira(creds, inv.Target);
        var key = inv.Target.JiraKey!;
        await StepAsync(inv, InvestigationStep.JiraPublishing, "Publishing approved Jira updates.", redactor, ct);

        if (request.PostComment)
        {
            var text = redactor.Redact(string.IsNullOrWhiteSpace(request.CommentText) ? inv.JiraCommentDraft ?? ReportFormatter.JiraCommentText(inv) : request.CommentText);
            await Record(inv, "Jira comment", async () => $"comment {await jira.AddCommentAsync(key, ReportFormatter.TextToAdf(text), ct)} added");
            if (request.AttachScreenshots)
            {
                var files = inv.Evidence.Where(e => e.Kind == "screenshot" && e.File is not null)
                    .Select(e => store.ResolveEvidenceFile(inv.Id, e.File!)).Where(p => p is not null)
                    .Select(p => (Path.GetFileName(p!), File.ReadAllBytes(p!))).ToList();
                if (files.Count > 0)
                    await Record(inv, "Jira attachments", async () => $"uploaded {string.Join(", ", await jira.AddAttachmentsAsync(key, files, ct))}");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.TransitionName))
        {
            await Record(inv, $"Jira transition '{request.TransitionName}'", async () =>
            {
                var closing = IsClosingTransition(request.TransitionName);
                if (closing && report.Outcome is InvestigationOutcome.Inconclusive or InvestigationOutcome.Blocked)
                    throw new InvalidOperationException($"Refusing to close an issue whose outcome is {ReportFormatter.OutcomeLabel(report.Outcome)}.");
                var transitions = await jira.GetTransitionsAsync(key, ct);
                var match = transitions.FirstOrDefault(t => t.Name.Equals(request.TransitionName, StringComparison.OrdinalIgnoreCase) || t.Id == request.TransitionName)
                    ?? throw new InvalidOperationException($"Transition '{request.TransitionName}' is not available. Available: {string.Join(", ", transitions.Select(t => t.Name))}.");
                await jira.TransitionAsync(key, match.Id, ct);
                return $"issue moved to '{match.ToStatus}'";
            });
        }

        await StepAsync(inv, InvestigationStep.Completed, "Publishing finished: " + string.Join("; ", inv.Publishing.TakeLast(3).Select(p => $"{p.Action} {(p.Success ? "OK" : "FAILED")}")), redactor, ct);
        await store.WriteTextAsync(inv.Id, "report.md", redactor.Redact(ReportFormatter.ToMarkdown(inv)), ct);
        return inv;
    }

    public async Task<Investigation> PublishPullRequestCommentAsync(string id, Credentials creds, string? text, CancellationToken ct)
    {
        var inv = await store.LoadAsync(id, ct) ?? throw new ArgumentException($"Investigation '{id}' not found.");
        if (inv.Target.Kind != TargetKind.PullRequest || inv.Report is null) throw new InvalidOperationException("Not a completed PR investigation.");
        var redactor = new SecretRedactor(creds.SecretValues());
        var body = redactor.Redact(string.IsNullOrWhiteSpace(text) ? ReportFormatter.PrCommentMarkdown(inv) : text);
        var ado = CreateAdo(creds, inv.Target.AdoOrganization, inv.Target.AdoProject);
        await Record(inv, "PR comment", async () => $"thread {await ado.PostPullRequestCommentAsync(inv.Target.AdoRepository!, inv.Target.PullRequestId, body, ct)} created");
        await store.SaveAsync(inv, ct);
        await store.WriteTextAsync(inv.Id, "report.md", redactor.Redact(ReportFormatter.ToMarkdown(inv)), ct);
        return inv;
    }

    internal static bool IsClosingTransition(string name) =>
        new[] { "close", "done", "resolve", "complete", "verified" }.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    private async Task<Investigation> LoadJiraInvestigationAsync(string id, CancellationToken ct)
    {
        var inv = await store.LoadAsync(id, ct) ?? throw new ArgumentException($"Investigation '{id}' not found.");
        if (inv.Target.Kind != TargetKind.JiraIssue) throw new InvalidOperationException("Not a Jira investigation.");
        return inv;
    }

    private static async Task Record(Investigation inv, string action, Func<Task<string>> work)
    {
        try { inv.Publishing.Add(new PublishRecord { Action = action, Success = true, Detail = await work() }); }
        catch (Exception ex) when (ex is ExternalServiceException or InvalidOperationException)
        {
            inv.Publishing.Add(new PublishRecord { Action = action, Success = false, Detail = ex.Message });
        }
    }

    // ======================= Claude inputs =======================

    internal static string TicketText(JiraIssue i)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Key: {i.Key} (id {i.Id})  Type: {i.IssueType}  Status: {i.Status}  Priority: {i.Priority}");
        sb.AppendLine($"Summary: {i.Summary}");
        if (i.AffectedVersions.Count > 0) sb.AppendLine($"Affects versions: {string.Join(", ", i.AffectedVersions)}");
        if (i.FixVersions.Count > 0) sb.AppendLine($"Fix versions: {string.Join(", ", i.FixVersions)}");
        if (i.Components.Count > 0) sb.AppendLine($"Components: {string.Join(", ", i.Components)}");
        if (i.Labels.Count > 0) sb.AppendLine($"Labels: {string.Join(", ", i.Labels)}");
        if (i.Sprints.Count > 0) sb.AppendLine($"Sprints: {string.Join(", ", i.Sprints)}");
        if (i.Assignee is not null) sb.AppendLine($"Assignee: {i.Assignee}");
        if (i.Reporter is not null) sb.AppendLine($"Reporter: {i.Reporter}");
        if (i.Parent is not null) sb.AppendLine($"Parent: {i.Parent}");
        if (i.Subtasks.Count > 0) sb.AppendLine($"Subtasks: {string.Join("; ", i.Subtasks)}");
        if (i.LinkedIssues.Count > 0) sb.AppendLine($"Linked issues: {string.Join("; ", i.LinkedIssues)}");
        if (i.Environment.Length > 0) sb.AppendLine($"Environment:\n{i.Environment}");
        sb.AppendLine($"Description:\n{i.Description}");
        foreach (var (k, v) in i.CustomFields) sb.AppendLine($"{k}: {v}");
        if (i.Comments.Count > 0)
        {
            sb.AppendLine("Comments:");
            foreach (var c in i.Comments.TakeLast(30)) sb.AppendLine($"- {c.Created:yyyy-MM-dd} {c.Author}: {JiraClient.Truncate(c.Body, 1500)}");
        }
        if (i.History.Count > 0)
        {
            sb.AppendLine("Recent history:");
            foreach (var h in i.History.TakeLast(25)) sb.AppendLine($"- {h}");
        }
        foreach (var a in i.Attachments)
            sb.AppendLine($"Attachment: {a.FileName} ({a.MimeType}, {a.Size} bytes){(a.Text is null ? "" : "\n" + a.Text)}");
        return sb.ToString();
    }

    private static string ContextText(EffectiveContext ctx, Investigation inv) =>
        $"Expected behaviour: {ctx.ExpectedBehavior}\nReported actual behaviour: {ctx.ActualBehavior}\nReproduction steps:\n" +
        string.Join("\n", ctx.ReproductionSteps.Select((s, i) => $"{i + 1}. {s}")) +
        (inv.UserInfo.Count > 0 ? "\nAdditional information from the user:\n" + string.Join("\n", inv.UserInfo.Select(kv => $"{kv.Key}: {kv.Value}")) : "");

    private static JsonArray BrowserInput(Investigation inv, EffectiveContext ctx, string currentUrl) =>
    [
        ClaudeClient.Text(Untrusted.Wrap("jira-ticket", $"{inv.Issue!.Key}: {inv.Issue.Summary}\n{JiraClient.Truncate(inv.Issue.Description, 6000)}")),
        ClaudeClient.Text($"Reproduction context (from the user and ticket):\n{ContextText(ctx, inv)}\n\nThe browser is currently at {StripQuery(currentUrl)}. Begin.")
    ];

    private static JsonArray CodeInvestigationInput(Investigation inv, EffectiveContext ctx) =>
    [
        ClaudeClient.Text(Untrusted.Wrap("jira-ticket", $"{inv.Issue!.Key}: {inv.Issue.Summary}\nComponents: {string.Join(", ", inv.Issue.Components)}\n{JiraClient.Truncate(inv.Issue.Description, 6000)}")),
        ClaudeClient.Text($"{ContextText(ctx, inv)}\nSuggested code search terms: {string.Join(", ", inv.Understanding?.CodeSearchTerms ?? [])}\n" +
            $"Browser result: reproduced={inv.Browser?.Reproduced}; actual={inv.Browser?.ActualBehavior}\nConsole errors: {string.Join(" | ", inv.Browser?.ConsoleErrors.Take(10) ?? [])}\n" +
            $"Network failures: {string.Join(" | ", inv.Browser?.NetworkFailures.Take(10) ?? [])}")
    ];

    private static JsonArray FinalAnalysisInput(Investigation inv, EffectiveContext ctx, string folder)
    {
        var b = inv.Browser;
        var content = new JsonArray(
            ClaudeClient.Text(Untrusted.Wrap("jira-ticket", TicketText(inv.Issue!), 40000)),
            ClaudeClient.Text($"{ContextText(ctx, inv)}"),
            ClaudeClient.Text(b is null ? "Browser investigation: not performed." :
                $"Browser investigation: attempted={b.Attempted}, loginSucceeded={b.LoginSucceeded}, reproduced={b.Reproduced}, expectedBehaviorObserved={b.ExpectedBehaviorObserved}, error={b.Error ?? "none"}\n" +
                Untrusted.Wrap("browser-observations", $"Actual: {b.ActualBehavior}\nSteps:\n{string.Join("\n", b.StepsExecuted)}\nNotes: {b.Notes}\nConsole errors:\n{string.Join("\n", b.ConsoleErrors.Take(20))}\nNetwork failures:\n{string.Join("\n", b.NetworkFailures.Take(20))}")),
            ClaudeClient.Text(inv.CodeFindings is null ? "Code investigation: not performed." : Untrusted.Wrap("code-findings", inv.CodeFindings)),
            ClaudeClient.Text("Evidence files: " + string.Join(", ", inv.Evidence.Select(e => e.File ?? e.Label))));

        foreach (var shot in inv.Evidence.Where(e => e.Kind == "screenshot" && e.File is not null).TakeLast(3))
        {
            var path = Path.Combine(folder, shot.File!);
            if (!File.Exists(path) || new FileInfo(path).Length > 3_700_000) continue;
            content.Add(ClaudeClient.Text($"Screenshot {shot.File}: {shot.Label}"));
            content.Add(ClaudeClient.Image(File.ReadAllBytes(path)));
        }
        content.Add(ClaudeClient.Text("Call submit_investigation_report now."));
        return content;
    }

    internal static (JsonArray Content, List<string> Omitted) PullRequestInput(PullRequestData pr, string linkedIssues, TestRunResult? tests, int budget = 150_000)
    {
        var header = $"PR #{pr.Id}: {pr.Title}\nAuthor: {pr.Author}\nSource: {pr.SourceBranch} → Target: {pr.TargetBranch}\nDescription:\n{pr.Description}\n" +
                     $"Linked Jira: {string.Join(", ", pr.LinkedJiraKeys)}  Linked work items: {string.Join(", ", pr.LinkedWorkItems)}";
        var diffs = new StringBuilder();
        var omitted = new List<string>();
        foreach (var f in pr.Files)
        {
            if (f.SkippedReason is not null) { diffs.AppendLine($"### {f.Path} ({f.ChangeType}) — not diffed: {f.SkippedReason}"); continue; }
            if (diffs.Length + f.Diff.Length > budget) { omitted.Add(f.Path); continue; }
            diffs.AppendLine($"### {f.Path} ({f.ChangeType}, +{f.Additions}/-{f.Deletions})").AppendLine(f.Diff);
        }
        var content = new JsonArray(
            ClaudeClient.Text(Untrusted.Wrap("pull-request", header, 20000)),
            ClaudeClient.Text("Diff (format: op oldLine newLine | text):\n" + Untrusted.Wrap("diff", diffs.ToString(), budget + 20000)));
        if (linkedIssues.Length > 0) content.Add(ClaudeClient.Text(Untrusted.Wrap("linked-jira", linkedIssues)));
        content.Add(ClaudeClient.Text($"Existing test results: {tests?.Summary ?? "not run"}"));
        if (omitted.Count > 0) content.Add(ClaudeClient.Text($"Diffs omitted for size (use get_file if needed): {string.Join(", ", omitted)}"));
        content.Add(ClaudeClient.Text("Call submit_pr_review when done."));
        return (content, omitted);
    }

    // ======================= mapping + validation =======================

    internal static InvestigationReport MapBugReport(JsonElement r, InvestigationTools? codeTools)
    {
        var report = new InvestigationReport
        {
            Outcome = Enum.TryParse<InvestigationOutcome>(Get(r, "outcome"), true, out var o) ? o : InvestigationOutcome.Inconclusive,
            Summary = Get(r, "summary"),
            ExpectedBehavior = Get(r, "expected_behavior"),
            ActualBehavior = Get(r, "actual_behavior"),
            ReproductionResult = Get(r, "reproduction_result"),
            RootCause = Get(r, "root_cause"),
            RootCauseStatus = Get(r, "root_cause_status") == "confirmed" ? "confirmed" : "hypothesis",
            Recommendations = GetList(r, "recommendations"),
            Limitations = GetList(r, "limitations"),
            Confidence = r.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? Math.Clamp(c.GetDouble(), 0, 1) : 0,
            ProposedJiraAction = Get(r, "proposed_jira_action") is { Length: > 0 } a && a != "none" ? a : null,
        };
        if (r.TryGetProperty("code_references", out var refs) && refs.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in refs.EnumerateArray())
            {
                var cr = new CodeReference
                {
                    Repository = Get(x, "repository"), Path = Get(x, "path"), Note = Get(x, "note"),
                    StartLine = x.TryGetProperty("start_line", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null,
                    EndLine = x.TryGetProperty("end_line", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null,
                };
                if (codeTools is null || !codeTools.WasInspected(cr.Repository, cr.Path, cr.StartLine))
                {
                    cr.Note = "[unverified: file/line was not inspected by the tool] " + cr.Note;
                    report.Limitations.Add($"Code reference {cr} could not be verified against inspected source.");
                }
                report.CodeReferences.Add(cr);
            }
        }
        if (report.RootCauseStatus == "confirmed" && !report.CodeReferences.Any(r => !r.Note.StartsWith("[unverified", StringComparison.Ordinal)))
        {
            report.RootCauseStatus = "hypothesis";
            report.Limitations.Add("Root cause downgraded to hypothesis: no verified code reference supports it.");
        }
        return report;
    }

    internal static InvestigationReport MapPrReport(JsonElement r, PullRequestData pr, InvestigationTools tools)
    {
        var report = new InvestigationReport
        {
            Outcome = InvestigationOutcome.Inconclusive,
            Summary = Get(r, "summary"),
            MissingTests = GetList(r, "missing_tests"),
            Recommendations = GetList(r, "recommendations"),
            OverallAssessment = Get(r, "overall_assessment"),
            Confidence = r.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? Math.Clamp(c.GetDouble(), 0, 1) : 0,
        };
        var seen = new HashSet<string>();
        foreach (var x in r.GetProperty("findings").EnumerateArray())
        {
            var path = InvestigationTools.Normalize(Get(x, "file"));
            var start = x.GetProperty("start_line").GetInt32();
            var end = Math.Max(start, x.GetProperty("end_line").GetInt32());
            if (!seen.Add($"{path}:{start}:{Get(x, "title")}")) continue;

            var file = pr.Files.FirstOrDefault(f => InvestigationTools.Normalize(f.Path).Equals(path, StringComparison.OrdinalIgnoreCase));
            var inDiff = file is not null && file.ChangedRanges.Any(cr => start <= cr.End + 3 && end >= cr.Start - 3);
            var verified = inDiff || tools.WasInspected(null, path, start);
            report.Findings.Add(new Finding
            {
                Title = Get(x, "title"),
                Severity = Get(x, "severity"),
                Detail = $"[{Get(x, "category")}] {Get(x, "detail")}",
                Recommendation = Get(x, "recommendation"),
                Reference = new CodeReference { Path = path, StartLine = start, EndLine = end },
                ReferenceUnverified = !verified,
            });
        }
        report.Findings = report.Findings.OrderBy(f => f.Severity switch { "Critical" => 0, "Medium" => 1, _ => 2 }).ToList();
        if (report.Findings.Any(f => f.ReferenceUnverified))
            report.Limitations.Add("Some finding line references are outside the changed hunks and were not inspected; verify them manually (marked ⚠).");
        report.PrVerdict = OutcomeClassifier.PrVerdict(report.Findings);
        return report;
    }

    private static void RedactReport(InvestigationReport report, SecretRedactor redactor)
    {
        report.Summary = redactor.Redact(report.Summary);
        report.RootCause = redactor.Redact(report.RootCause);
        report.ActualBehavior = redactor.Redact(report.ActualBehavior);
        report.ReproductionResult = redactor.Redact(report.ReproductionResult);
        report.OverallAssessment = redactor.Redact(report.OverallAssessment);
        report.Recommendations = report.Recommendations.Select(redactor.Redact).ToList();
        foreach (var f in report.Findings) { f.Detail = redactor.Redact(f.Detail); f.Recommendation = redactor.Redact(f.Recommendation); }
    }

    // ======================= clients (credential + host validation) =======================

    private ClaudeClient CreateClaude(Credentials creds)
    {
        if (!creds.HasClaude) throw new InvalidOperationException("Claude API key is not configured (ANTHROPIC_API_KEY or the Settings page).");
        return new ClaudeClient(httpFactory.CreateClient("claude"), creds.ClaudeApiKey!, _options);
    }

    private JiraClient CreateJira(Credentials creds, InvestigationTarget? target)
    {
        if (string.IsNullOrWhiteSpace(creds.JiraEmail) || string.IsNullOrWhiteSpace(creds.JiraApiToken))
            throw new InvalidOperationException("Jira email and API token are not configured.");
        var baseUrl = target?.JiraBaseUrl ?? creds.JiraBaseUrl ?? throw new InvalidOperationException("Jira base URL is not configured.");
        var host = new Uri(baseUrl).Host;
        if (!HostPolicy.IsAllowed(host, _options.AllowedJiraHosts))
            throw new InvalidOperationException($"Jira host '{host}' is not in Tool:AllowedJiraHosts; the API token will not be sent there.");
        if (!string.IsNullOrWhiteSpace(creds.JiraBaseUrl) && !new Uri(creds.JiraBaseUrl).Host.Equals(host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The ticket is on '{host}' but the Jira credentials are for '{new Uri(creds.JiraBaseUrl).Host}'.");
        return new JiraClient(httpFactory.CreateClient("jira"), baseUrl, creds.JiraEmail!, creds.JiraApiToken!);
    }

    private AzureDevOpsClient CreateAdo(Credentials creds, string? organization, string? project)
    {
        if (string.IsNullOrWhiteSpace(creds.AdoPat)) throw new InvalidOperationException("Azure DevOps PAT is not configured.");
        var org = organization ?? creds.AdoOrganization ?? throw new InvalidOperationException("Azure DevOps organization is not configured.");
        if (!string.IsNullOrWhiteSpace(creds.AdoOrganization) && !creds.AdoOrganization.Equals(org, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The PR is in organization '{org}' but the PAT is configured for '{creds.AdoOrganization}'.");
        var proj = project ?? creds.AdoProject ?? throw new InvalidOperationException("Azure DevOps project is not configured.");
        return new AzureDevOpsClient(httpFactory.CreateClient("ado"), org, proj, creds.AdoPat!);
    }

    private IReadOnlyCollection<string> AllowedHosts(Credentials creds) =>
        _options.AllowedEnvironmentHosts.Concat(creds.AllowedEnvironmentHosts).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ======================= helpers =======================

    private async Task StepAsync(Investigation inv, InvestigationStep step, string message, SecretRedactor redactor, CancellationToken ct)
    {
        var entry = new ProgressEntry { Step = step, Message = redactor.Redact(message) };
        lock (inv.Progress)
        {
            inv.Step = step;
            inv.Progress.Add(entry);
        }
        OnProgress?.Invoke(inv, entry);
        await store.SaveAsync(inv, CancellationToken.None);
        ct.ThrowIfCancellationRequested();
    }

    internal static string Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static List<string> GetList(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    private static string StripQuery(string url) => url.Split('?')[0];
}
