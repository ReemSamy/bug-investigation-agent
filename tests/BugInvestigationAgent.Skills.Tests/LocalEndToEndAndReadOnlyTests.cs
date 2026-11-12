using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BugInvestigationAgent.Core;
using BugInvestigationAgent.Core.Abstractions;
using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Browser;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Investigations;
using BugInvestigationAgent.Core.Jira;
using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Core.Memory;
using BugInvestigationAgent.Core.Orchestration;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Rules;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Sessions;
using BugInvestigationAgent.Core.Skills;
using BugInvestigationAgent.Core.Workflow;
using BugInvestigationAgent.Infrastructure.Investigations;
using BugInvestigationAgent.Infrastructure.Jobs;
using BugInvestigationAgent.Infrastructure.Persistence;
using BugInvestigationAgent.Infrastructure.Repositories;
using BugInvestigationAgent.Skills.Code;
using BugInvestigationAgent.Skills.Evidence;
using BugInvestigationAgent.Skills.Validation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BugInvestigationAgent.Skills.Tests;

/// <summary>A throw-away Git repository standing in for the KMS source repository.</summary>
internal sealed class TempGitRepo : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bia-repo-" + Guid.NewGuid().ToString("N"));

    public TempGitRepo()
    {
        Directory.CreateDirectory(System.IO.Path.Combine(Path, "src"));
        File.WriteAllText(System.IO.Path.Combine(Path, "src", "Badgermail.cs"),
            "public class Badgermail\n{\n    public bool ShouldRequeue(System.DateTime oldDate, System.DateTime newDate) => false;\n}\n");
        File.WriteAllText(System.IO.Path.Combine(Path, "README.md"), "# kms-suite (test copy)\n");
        Git("init", "-q", "-b", "main");
        Git("add", ".");
        Git("-c", "user.email=t@example.test", "-c", "user.name=test", "commit", "-q", "-m", "init");
        File.AppendAllText(System.IO.Path.Combine(Path, "README.md"), "Alerts re-queue when dates change.\n");
        Git("-c", "user.email=t@example.test", "-c", "user.name=test", "commit", "-q", "-am", "VKMS-4000: re-queue alerts when the expiration date changes");
        Git("branch", "release/14.2");
    }

    /// <summary>Everything except the branch list: files, HEAD, checked-out branch, working-tree status.</summary>
    public string WorkTreeState()
    {
        var full = Fingerprint();
        return string.Join('\n', full.Split('\n').Where(l => !l.StartsWith("branches ", StringComparison.Ordinal)));
    }

    public IReadOnlyList<string> Branches() =>
        Git("for-each-ref", "--format=%(refname:short) %(objectname)", "refs/heads").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(Path);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {output}");
        return output.Trim();
    }

    /// <summary>Content hashes of every file outside .git, HEAD, current branch, all branches and working-tree status.</summary>
    public string Fingerprint()
    {
        var sb = new StringBuilder();
        foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(System.IO.Path.DirectorySeparatorChar + ".git" + System.IO.Path.DirectorySeparatorChar))
                     .OrderBy(f => f, StringComparer.Ordinal))
            sb.Append(System.IO.Path.GetRelativePath(Path, f)).Append(':').Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))).Append('\n');
        sb.Append("HEAD ").Append(Git("rev-parse", "HEAD")).Append('\n');
        sb.Append("branch ").Append(Git("rev-parse", "--abbrev-ref", "HEAD")).Append('\n');
        sb.Append("branches ").Append(string.Join(",", Git("branch", "--list").Split('\n').Select(b => b.Trim()))).Append('\n');
        sb.Append("status ").Append(Git("status", "--porcelain")).Append('\n');
        return sb.ToString();
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal); // git objects are read-only on Windows
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Jira REST fake: records every request; the issue's description can be edited to simulate a ticket change.</summary>
internal sealed class FakeJiraServer : HttpMessageHandler
{
    public readonly List<string> Requests = new();
    public readonly List<string> PostedComments = new();
    public string Description = "{\"type\":\"doc\",\"version\":1,\"content\":[]}";
    public List<string> FilterIssues = new() { "VKMS-1", "VKMS-2", "VKMS-3" };

    public IEnumerable<string> Writes => Requests.Where(r => !r.StartsWith("GET ") && r != "POST /rest/api/3/search/jql");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests) Requests.Add($"{request.Method} {path}");

        var issue = System.Text.RegularExpressions.Regex.Match(path, @"^/rest/api/3/issue/([A-Z]+-\d+)(/comment)?$");
        if (issue.Success && request.Method == HttpMethod.Get && !issue.Groups[2].Success)
            return Json($"{{\"key\":\"{issue.Groups[1].Value}\",\"fields\":{{\"summary\":\"Badgermail does not re-trigger\",\"description\":{Description}," +
                        "\"status\":{\"name\":\"Backlog\"},\"resolution\":null,\"priority\":{\"name\":\"Low\"},\"created\":\"2024-01-01T00:00:00.000+0000\"," +
                        "\"updated\":\"2026-11-10T00:00:00.000+0000\",\"labels\":[]}}");
        if (issue.Success && request.Method == HttpMethod.Get)
        {
            lock (PostedComments)
            {
                var comments = new JsonArray(PostedComments.Select((c, i) => (JsonNode)new JsonObject { ["id"] = $"{i + 1}", ["body"] = JsonNode.Parse(c)!["body"]!.DeepClone() }).ToArray());
                return Json(new JsonObject { ["total"] = PostedComments.Count, ["comments"] = comments }.ToJsonString());
            }
        }
        if (issue.Success && request.Method == HttpMethod.Post)
        {
            lock (PostedComments)
            {
                PostedComments.Add(body);
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent($"{{\"id\":\"{PostedComments.Count}\"}}", Encoding.UTF8, "application/json") };
            }
        }
        if (path == "/rest/api/3/filter/11265")
            return Json("{\"jql\":\"project = VKMS AND status = Backlog\"}");
        if (path == "/rest/api/3/search/jql")
            return Json(new JsonObject { ["issues"] = new JsonArray(FilterIssues.Select(k => (JsonNode)new JsonObject { ["key"] = k }).ToArray()), ["isLast"] = true }.ToJsonString());
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/json") };
}

public class LocalEndToEndAndReadOnlyTests
{
    private sealed class Skill(string id, Func<SkillExecutionRequest, Task>? run = null) : ISkill
    {
        public string SkillId => id;
        public string DisplayName => id;
        public AgentPermission RequiredPermissions => AgentPermission.None;
        public bool CanExecute(RuntimeContext context, IPermissionGate permissions) => true;
        public async Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
        {
            if (run is not null) await run(request);
            return SkillResult.Ok();
        }
    }

    private sealed class Workspace(string path) : IWorkspaceDefaults { public string? DefaultWorkspacePath => path; }

    private sealed class Jira : IJiraConnectionSettings
    {
        public bool IsConfigured => true;
        public bool IsSignedIn => true;
        public string? SignedInDisplayName => "Tester";
        public string? SignedInEmail => "tester@example.test";
        public JiraOptions GetEffective() => new() { BaseUrl = "https://jira.example.test", Email = "tester@example.test", ApiToken = "x" };
    }

    private sealed class Access : IJiraAccessProvider
    {
        public Task<JiraAccessResult> GetReadAccessAsync(RuntimeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(JiraAccessResult.Ok(new JiraAccess { BaseAddress = new Uri("https://jira.example.test/"), Authorization = new AuthenticationHeaderValue("Basic", "eA=="), Description = "test" }));
    }

    private sealed class Clients(HttpMessageHandler h) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(h, disposeHandler: false); }

    private sealed class DbFactory(string file) : IDbContextFactory<AgentDbContext>
    {
        public AgentDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AgentDbContext>().UseSqlite($"Data Source={file};Default Timeout=60").Options);
    }

    private sealed class Llm(string provider, bool approved) : ILlmSettingsSource
    {
        public string ActiveProviderId => provider;
        public LlmProviderOptions Current { get; } = new();
        public bool ExternalDataSharingApproved => approved;
    }

    /// <summary>LLM stand-in that proposes a code change (so the test proves the change is never applied).</summary>
    private sealed class ProposingLlm : IAgentRuntime
    {
        public int Calls;
        public string ProviderId => "anthropic";
        public Task<AgentTurnResult> CompleteAsync(AgentTurnRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new AgentTurnResult
            {
                Content = """
                {"rootCause":"ShouldRequeue always returns false, so a changed expiration date never re-queues the alert.",
                 "evidenceReasoning":"src/Badgermail.cs","potentialImpact":"Missed notifications",
                 "proposedFixSummary":"Return true when the date changed.","proposedFiles":["src/Badgermail.cs"],
                 "proposedDiff":"--- a/src/Badgermail.cs\n+++ b/src/Badgermail.cs\n@@ -3,1 +3,1 @@\n-    public bool ShouldRequeue(System.DateTime oldDate, System.DateTime newDate) => false;\n+    public bool ShouldRequeue(System.DateTime oldDate, System.DateTime newDate) => oldDate != newDate;\n"}
                """
            });
        }
        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(AgentTurnRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class Harness : IDisposable
    {
        public readonly TempGitRepo Repo = new();
        public readonly FakeJiraServer JiraServer = new();
        public readonly string Db = Path.Combine(Path.GetTempPath(), $"bia-e2e-{Guid.NewGuid():N}.db");
        public readonly string Evidence = Path.Combine(Path.GetTempPath(), $"bia-e2e-ev-{Guid.NewGuid():N}");
        public readonly InMemoryAgentSessionStore Sessions = new();
        public readonly ProposingLlm LlmRuntime = new();
        public IInvestigationRecordStore Records = null!;
        public ILegacyBatchStore Batches = null!;
        public IJiraIssueGateway Gateway = null!;
        public CommentReviewService Review = null!;
        public AgentOrchestrator Orchestrator = null!;
        public IPermissionGate Gate = null!;
        public EnvironmentSelectionService Environments = null!;
        public ReproductionOutcome Outcome = ReproductionOutcome.Ambiguous;
        public string? DeployedVersion = "14.2";

        public Harness(bool llmApproved)
        {
            var factory = new DbFactory(Db);
            using (var db = factory.CreateDbContext())
                DatabaseMigrator.MigrateAsync(db).GetAwaiter().GetResult();
            Records = new EfInvestigationRecordStore(factory);
            Batches = new EfLegacyBatchStore(factory);
            Gateway = new JiraIssueGateway(new Access(), new Clients(JiraServer));
            var redactor = new SecretRedactor();
            var audit = new EfAuditLog(factory, redactor);
            Review = new CommentReviewService(Records, audit, redactor);

            // Configuration that WOULD allow code changes — the runtime mask must still forbid them.
            Gate = ReadOnlyRollout.Mask(new PermissionGate(new AgentPermissionOptions { ModifyCode = true, RunValidation = true, GitCommit = true }.ToFlags() | AgentPermission.ReadWorkspace));
            Environments = new EnvironmentSelectionService(new ConfigurationEnvironmentRegistry(new EnvironmentRegistryOptions
            {
                Environments = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["KMSQA"] = new() { DisplayName = "KMSQA", ApplicationId = "KMS", BaseUrl = "https://kmsqa.example.test", Type = EnvironmentType.QA },
                    ["KMSPROD"] = new() { DisplayName = "KMS Production", ApplicationId = "KMS", BaseUrl = "https://kms.example.test", Type = EnvironmentType.Production }
                }
            }));
            var credentials = new InMemorySessionCredentialStore();
            var resolver = new AgentRuntimeResolver(new IAgentRuntime[] { new BugInvestigationAgent.Providers.Stubs.NullAgentRuntime(), LlmRuntime }, new Llm("anthropic", llmApproved));
            var memory = new Mock<IEngineeringMemoryStore>();
            memory.Setup(m => m.QueryRelevantAsync(It.IsAny<string?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<EngineeringLesson>());
            var vault = new Mock<ICredentialVault>();
            vault.Setup(v => v.GetProfileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => new CredentialProfile { Id = id, Label = "KMSQA test", EnvironmentId = "KMSQA", OwnerId = "me", Username = "qa_user" });

            var services = new ServiceCollection();
            services.AddScoped<ISkill>(_ => new Skill(SkillIds.Jira, r =>
            {
                r.Context.JiraDetailsLoaded = true;
                r.Context.ExpectedResult = "A new BadgerMail is sent.";
                r.Context.Summary = "Badgermail does not re-trigger after the expiration date changes";
                r.Context.Description = "Steps to reproduce:\n1. Create a new record.\n2. Wait 30 minutes.";
                r.Context.ReproductionSteps = new List<string> { "Create a new record.", "Wait 30 minutes." };
                return Task.CompletedTask;
            }));
            foreach (var id in new[] { SkillIds.TicketUnderstanding, SkillIds.HistoricalJiraSearch, SkillIds.HistoricalReproductionReuse, SkillIds.KmsDocumentation })
                services.AddScoped<ISkill>(_ => new Skill(id));
            services.AddScoped<ISkill>(_ => new Skill(SkillIds.EnvironmentDiscovery, r => { Environments.ApplySuggestion(r.Context); return Task.CompletedTask; }));
            services.AddScoped<ISkill>(_ => new Skill(SkillIds.BrowserAutomation, r =>
            {
                r.Context.AuthenticationStatus = AuthenticationStatus.Succeeded;
                r.Context.DeployedVersion = DeployedVersion;
                r.Context.DeployedVersionSource = DeployedVersion is null ? "no version text on the page" : "text on the signed-in page";
                return Task.CompletedTask;
            }));
            services.AddScoped<ISkill>(_ => new Skill(SkillIds.Reproduction, r =>
            {
                r.Context.ReproductionOutcome = Outcome;
                if (Outcome == ReproductionOutcome.Reproduced)
                    r.Context.Evidence.Add(EvidenceItem.For(r.Context, "screenshot", Path.Combine(Evidence, "x.png"), "defect visible", EvidenceCategories.FailureState));
                return Task.CompletedTask;
            }));
            // REAL skills for everything that touches the repository, the report and the code.
            var git = new GitSourceVersionControl(new ProcessRunner());
            var agentOptions = Options.Create(new AgentOptions());
            services.AddScoped<ISkill>(_ => new CodeInvestigationSkill(resolver, new DefaultRulesProvider(), memory.Object, redactor, git));
            services.AddScoped<ISkill>(_ => new SourceVersionSkill(git, agentOptions));
            services.AddScoped<ISkill>(_ => new LocalBranchSkill(git, agentOptions));
            services.AddScoped<ISkill>(_ => new CodeModificationSkill());
            services.AddScoped<ISkill>(_ => new EvidenceReportingSkill(redactor, Options.Create(new AgentOptions { EvidenceDirectory = Evidence })));
            services.AddScoped(_ => vault.Object);
            services.AddScoped(_ => memory.Object);
            services.AddSingleton<IInvestigationCompletionSink>(new InvestigationRecorder(Records, Gateway, redactor, audit, NullLogger<InvestigationRecorder>.Instance));
            var provider = services.BuildServiceProvider();

            Orchestrator = new AgentOrchestrator(provider.GetRequiredService<IServiceScopeFactory>(), Sessions, redactor,
                new MissingInfoResolver(new Workspace(Repo.Path), new Jira(), Environments, credentials), Gate, new Workspace(Repo.Path),
                Mock.Of<IBrowserSessionCloser>(), Environments, credentials, NullLogger<AgentOrchestrator>.Instance);
        }

        public async Task<(AgentSession Session, InvestigationRecord Record)> InvestigateAsync(string key)
        {
            var session = Sessions.Create();
            await Orchestrator.StartAsync(session.Id, key, null);
            await WaitUntil(() => session.State == AgentState.AwaitingMissingInfo, "environment prompt");
            await Orchestrator.SubmitMissingInfoAsync(session.Id, new Dictionary<string, string> { ["environmentId"] = "KMSQA" },
                new CredentialSecret { Username = "qa_user", Password = "Secret-Pw-1!" }, saveCredentialProfile: false);
            await WaitUntil(() => session.State is AgentState.Completed or AgentState.Failed or AgentState.Blocked || session.State == AgentState.AwaitingApproval, "end of run");
            InvestigationRecord? record = null;
            await WaitUntil(() => (record = Records.FindByInvestigationAsync(session.Id, DateTimeOffset.MinValue).GetAwaiter().GetResult()) is not null, "record");
            return (session, record!);
        }

        public void Dispose()
        {
            Repo.Dispose();
            SqliteConnection.ClearAllPools();
            try { File.Delete(Db); } catch (IOException) { }
            try { Directory.Delete(Evidence, true); } catch (IOException) { }
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out waiting for: " + what);
            await Task.Delay(25);
        }
    }

    [Test]
    public async Task Inconclusive_reproduction_reads_code_from_the_version_branch_creates_no_branch_and_writes_nothing()
    {
        using var h = new Harness(llmApproved: true);
        var stateBefore = h.Repo.WorkTreeState();
        var branchesBefore = h.Repo.Branches();

        var (session, record) = await h.InvestigateAsync("VKMS-5102");

        Assert.That(session.State, Is.EqualTo(AgentState.Completed));
        Assert.That(record.KmsVersion, Is.EqualTo("14.2"));
        Assert.That(record.SourceBranch, Is.EqualTo("release/14.2"));
        Assert.That(h.LlmRuntime.Calls, Is.EqualTo(1), "code analysed from the version branch although reproduction was inconclusive");
        Assert.That(session.Context.StatusLog.SafeSnapshot(), Has.Some.Contains("Reading code from release/14.2"));
        Assert.That(record.ReproductionOutcome, Is.EqualTo(ReproductionResult.Inconclusive));
        Assert.That(record.RootCauseConfirmed, Is.False);
        Assert.That(record.CreatedBranch, Is.Null, "no branch unless reproduced");
        var report = BugInvestigationAgent.Core.Reporting.InvestigationReport.FromJson(record.ReportJson)!;
        Assert.That(report.MissingForVerification, Is.Not.Empty, "explains what prevented verification");
        Assert.That(File.Exists(record.ReportJsonPath) && File.Exists(record.ReportMarkdownPath), Is.True);
        Assert.That(record.ReportMarkdown + record.CommentDraft, Does.Not.Contain("Secret-Pw-1!"));

        Assert.That(h.JiraServer.Writes, Is.Empty, "nothing written to Jira");
        Assert.That(h.Repo.WorkTreeState(), Is.EqualTo(stateBefore));
        Assert.That(h.Repo.Branches(), Is.EqualTo(branchesBefore));
    }

    [Test]
    public async Task Still_reproducible_bug_gets_one_local_branch_from_the_version_branch_and_nothing_else_changes()
    {
        using var h = new Harness(llmApproved: true);
        h.Outcome = ReproductionOutcome.Reproduced;
        var stateBefore = h.Repo.WorkTreeState();
        var branchesBefore = h.Repo.Branches();
        var versionCommit = h.Repo.Git("rev-parse", "release/14.2");

        var (session, record) = await h.InvestigateAsync("VKMS-5102");

        Assert.That(record.Assessment, Is.EqualTo(BugAssessment.StillExists));
        Assert.That(record.CreatedBranch, Is.EqualTo("bugfix/VKMS-5102"));
        Assert.That(h.Repo.Git("rev-parse", "bugfix/VKMS-5102"), Is.EqualTo(versionCommit), "based on the version branch");
        Assert.That(h.Repo.Branches(), Is.EquivalentTo(branchesBefore.Append($"bugfix/VKMS-5102 {versionCommit}")), "exactly one new branch");
        Assert.That(h.Repo.WorkTreeState(), Is.EqualTo(stateBefore), "no checkout, no file change, no commit");
        Assert.That(record.ProposedFix, Is.Not.Null);
        Assert.That(record.ReportMarkdown, Does.Contain("Local branch created: bugfix/VKMS-5102").And.Contain("Suggested code change"));
        Assert.That(File.ReadAllText(Path.Combine(h.Repo.Path, "src", "Badgermail.cs")), Does.Contain("=> false;"), "fix proposed, never applied");
        Assert.That(session.Context.StatusLog.SafeSnapshot(), Has.None.Contains("Applying approved"));
        Assert.That(h.JiraServer.Writes, Is.Empty);

        // Investigating again never moves the existing branch.
        h.Repo.Git("-c", "user.email=t@example.test", "-c", "user.name=test", "commit", "-q", "--allow-empty", "-m", "later work on main");
        var (_, again) = await h.InvestigateAsync("VKMS-5102");
        Assert.That(again.BranchNote, Does.Contain("already exists"));
        Assert.That(h.Repo.Git("rev-parse", "bugfix/VKMS-5102"), Is.EqualTo(versionCommit));
    }

    [Test]
    public async Task Not_reproduced_with_every_step_automated_and_a_fix_commit_in_the_version_is_confirmed_fixed_without_a_branch()
    {
        using var h = new Harness(llmApproved: false);
        h.Outcome = ReproductionOutcome.NotReproduced;
        var branchesBefore = h.Repo.Branches();

        var (_, record) = await h.InvestigateAsync("VKMS-4000");

        Assert.That(record.Assessment, Is.EqualTo(BugAssessment.ConfirmedFixed), record.AssessmentReason);
        Assert.That(record.AssessmentReason, Does.Contain("VKMS-4000: re-queue alerts"));
        Assert.That(record.CreatedBranch, Is.Null);
        Assert.That(h.Repo.Branches(), Is.EqualTo(branchesBefore));
        Assert.That(h.JiraServer.Writes, Is.Empty);
    }

    [Test]
    public async Task Unknown_deployed_version_means_no_code_analysis_and_no_branch()
    {
        using var h = new Harness(llmApproved: true);
        h.Outcome = ReproductionOutcome.Reproduced;
        h.DeployedVersion = null;
        var branchesBefore = h.Repo.Branches();

        var (_, record) = await h.InvestigateAsync("VKMS-5102");

        Assert.That(h.LlmRuntime.Calls, Is.EqualTo(0), "the working folder is never analysed instead");
        Assert.That(record.CreatedBranch, Is.Null);
        Assert.That(h.Repo.Branches(), Is.EqualTo(branchesBefore));
        var report = BugInvestigationAgent.Core.Reporting.InvestigationReport.FromJson(record.ReportJson)!;
        Assert.That(report.MissingForVerification, Has.Some.Contains("version could not be identified"));
        Assert.That(report.MissingForVerification.Count(m => m.Contains("version", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1), "reported once");
        Assert.That(record.ExecutionDetail ?? "", Does.Not.Contain("LLM not configured"), "the real reason is the unknown version");
    }

    [TestCase("12.28.4", "Release-12.28.4")]
    [TestCase("14.1.0", "release-v14.1.0")]
    [TestCase("14.1", "release-v14.1.0")]
    [TestCase("12.28.4.117", "Release-12.28.4")] // build number on top of the patch release
    public async Task Default_patterns_match_the_kms_suite_release_branch_names(string version, string? expectedSuffix)
    {
        using var repo = new TempGitRepo();
        // Same shapes as the real clone's remote branches (created as local refs under refs/remotes/origin).
        foreach (var name in new[] { "Release-12.28.4", "Release-12.28.5", "release-v14.1.0", "Release-14.0.0" })
            repo.Git("update-ref", "refs/remotes/origin/release/" + name, "HEAD");

        var match = await new GitSourceVersionControl(new ProcessRunner())
            .ResolveVersionRefAsync(repo.Path, version, new AgentOptions().SourceVersioning.BranchPatterns);

        Assert.That(match?.RefName, Is.EqualTo(expectedSuffix is null ? null : "origin/release/" + expectedSuffix));
    }

    [TestCase("checkout", "main")]
    [TestCase("switch", "release/14.2")]
    [TestCase("commit", "-m", "x")]
    [TestCase("push", "origin", "main")]
    [TestCase("fetch")]
    [TestCase("reset", "--hard")]
    [TestCase("branch", "-f", "main", "release/14.2")]
    [TestCase("branch", "-D", "main")]
    [TestCase("branch", "--no-track", "-f", "x")]
    [TestCase("worktree", "add", "x")]
    [TestCase("log", "--output=x.txt")]
    public void Git_allowlist_refuses_anything_that_could_change_the_repository(params string[] args) =>
        Assert.Throws<InvalidOperationException>(() => GitSourceVersionControl.EnsureAllowed(args));

    [Test]
    public void Git_allowlist_accepts_reads_and_new_branch_creation_only()
    {
        Assert.DoesNotThrow(() => GitSourceVersionControl.EnsureAllowed(new[] { "log", "release/14.2", "--grep=VKMS-1" }));
        Assert.DoesNotThrow(() => GitSourceVersionControl.EnsureAllowed(new[] { "grep", "-n", "-e", "x", "release/14.2" }));
        Assert.DoesNotThrow(() => GitSourceVersionControl.EnsureAllowed(new[] { "branch", "--no-track", "bugfix/VKMS-1", "origin/release/12.28" }));
    }

    [Test]
    public void Jira_links_history_and_expected_actual_are_parsed()
    {
        var ctx = new RuntimeContext();
        BugInvestigationAgent.Skills.Jira.JiraSkill.ParseLinksAndHistory(ctx, """
            {"key":"VKMS-2601","fields":{"status":{"name":"Done"},"resolution":{"name":"Fixed"},"resolutiondate":"2026-09-17T10:00:00.000+0000","issuelinks":[
              {"type":{"outward":"relates to","inward":"is related to"},"outwardIssue":{"key":"VKMS-2600","fields":{"summary":"Pre-MOC dates","status":{"name":"Done"}}}},
              {"type":{"outward":"blocks","inward":"is blocked by"},"inwardIssue":{"key":"VKMS-10","fields":{"summary":"Other","status":{"name":"Backlog"}}}}]},
             "changelog":{"histories":[{"created":"2026-02-24T10:00:00.000+0000","author":{"displayName":"Reem"},
               "items":[{"field":"status","fromString":"To Do","toString":"Backlog"},{"field":"description","fromString":"a","toString":"b"}]}]}}
            """);
        Assert.That(ctx.LinkedIssues, Is.EqualTo(new[] { "relates to VKMS-2600 (Done): Pre-MOC dates", "is blocked by VKMS-10 (Backlog): Other" }));
        Assert.That(ctx.ResolvedLinkedIssueKeys, Is.EqualTo(new[] { "VKMS-2600" }));
        Assert.That(ctx.IssueHistory, Is.EqualTo(new[] { "2026-02-24 Reem: status To Do → Backlog" }));
        Assert.That(ctx.JiraStatus, Is.EqualTo("Done"));
        Assert.That(ctx.JiraResolution, Is.EqualTo("Fixed"));
        Assert.That(ctx.JiraResolvedAt?.Year, Is.EqualTo(2026));

        var (expected, actual) = BugInvestigationAgent.Skills.Jira.JiraSkill.ExtractExpectedActual(
            "Steps to reproduce:\n1. Open Pre-MOC\nExpected result: The date is displayed.\nActual result: The date is empty.\nEnvironment: QA");
        Assert.That(expected, Is.EqualTo("The date is displayed."));
        Assert.That(actual, Is.EqualTo("The date is empty."));
    }

    // kms14-portal About dialog (2026-11-11): the build number carries the exact release; "KMS Version: 14.0" is rounded.
    [TestCase("KMS Version: 14.0\nBuild Number: kms14-suite-release-v14.1.0-20261110.3\nDatabase Name: KMSv12_RC", null, "14.1.0")]
    [TestCase("KMS Version 12.28.4 © 2026", null, "12.28.4")]
    [TestCase("Build v14.2.0.117", null, "14.2.0.117")]
    [TestCase("Updated 2026-03-04, 10 items", null, null)]
    [TestCase("Release: R12.28-hotfix", @"R(\d+\.\d+)", "12.28")]
    public void Version_text_is_matched_only_when_it_is_clearly_a_version(string text, string? pattern, string? expected) =>
        Assert.That(BugInvestigationAgent.Skills.Browser.DeployedVersionDetector.Match(text, pattern), Is.EqualTo(expected));

    [Test]
    public async Task Reproduced_bug_with_a_proposed_diff_never_reaches_code_modification()
    {
        using var h = new Harness(llmApproved: true);
        h.Outcome = ReproductionOutcome.Reproduced;
        var repoBefore = h.Repo.Fingerprint();

        var (session, record) = await h.InvestigateAsync("VKMS-5102");

        Assert.That(h.Gate.IsAllowed(AgentPermission.ModifyCode), Is.False, "configured ON, forced OFF at runtime");
        Assert.That(h.Gate.IsAllowed(AgentPermission.RunValidation | AgentPermission.GitCommit), Is.False);
        Assert.That(session.State, Is.EqualTo(AgentState.Completed), "never waits for a fix approval");
        Assert.That(session.Context.StatusLog.SafeSnapshot(), Has.None.Contains("Applying approved"));
        Assert.That(record.Assessment, Is.EqualTo(BugAssessment.StillExists));
        Assert.That(repoBefore.Split('\n').Where(l => !l.StartsWith("branches ")), Is.EqualTo(h.Repo.Fingerprint().Split('\n').Where(l => !l.StartsWith("branches "))),
            "files, HEAD, current branch and status unchanged");
    }

    [Test]
    public async Task Code_changing_skills_refuse_to_run_even_when_called_directly()
    {
        using var repo = new TempGitRepo();
        var before = repo.Fingerprint();
        var gate = ReadOnlyRollout.Mask(new PermissionGate((AgentPermission)int.MaxValue));
        var ctx = new RuntimeContext
        {
            WorkspacePath = repo.Path, FixApproved = true,
            ProposedDiff = "--- a/README.md\n+++ b/README.md\n@@ -1,1 +1,1 @@\n-# kms-suite (test copy)\n+# CHANGED\n"
        };

        var modify = await new CodeModificationSkill().ExecuteAsync(new SkillExecutionRequest
        { Context = ctx, Permissions = gate, Inputs = new Dictionary<string, string> { ["apply"] = "true" } });
        var validate = await new ValidationSkill(Options.Create(new AgentOptions { ValidationCommandAllowlist = ["git status"] }), new SecretRedactor())
            .ExecuteAsync(new SkillExecutionRequest { Context = ctx, Permissions = gate });

        Assert.That(modify.Succeeded, Is.False);
        Assert.That(validate.Succeeded, Is.False);
        Assert.That(new CodeModificationSkill().CanExecute(ctx, gate), Is.False);
        Assert.That(repo.Fingerprint(), Is.EqualTo(before));

        // Control: without the runtime mask the same call DOES change the file, so the fingerprint check is meaningful.
        using var control = new TempGitRepo();
        var controlBefore = control.Fingerprint();
        ctx.WorkspacePath = control.Path;
        var unmasked = await new CodeModificationSkill().ExecuteAsync(new SkillExecutionRequest
        { Context = ctx, Permissions = new PermissionGate((AgentPermission)int.MaxValue), Inputs = new Dictionary<string, string> { ["apply"] = "true" } });
        Assert.That(unmasked.Succeeded, Is.True);
        Assert.That(control.Fingerprint(), Is.Not.EqualTo(controlBefore));
    }

    [Test]
    public async Task Without_recorded_approval_no_ticket_or_code_data_reaches_the_llm()
    {
        using var h = new Harness(llmApproved: false);

        var (_, record) = await h.InvestigateAsync("VKMS-5102");

        Assert.That(h.LlmRuntime.Calls, Is.EqualTo(0));
        Assert.That(record.RootCause, Is.Null);
        Assert.That(record.ReportMarkdown, Does.Contain("approval"));
    }

    [Test]
    public async Task Batch_resumes_after_a_restart_from_the_database_and_never_writes_to_jira()
    {
        using var h = new Harness(llmApproved: false);
        var service = new LegacyBatchService(h.Batches, h.Records, h.Gateway, h.Orchestrator, h.Sessions, h.Environments,
            new LegacyBatchOptions { PollInterval = TimeSpan.FromMilliseconds(25), RecordWait = TimeSpan.FromSeconds(10) }, NullLogger<LegacyBatchService>.Instance);

        Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateSnapshotAsync("11265", "KMSPROD", null, Guid.NewGuid(), "Reem"));
        var batch = await service.CreateSnapshotAsync("11265", "KMSQA", null, Guid.NewGuid(), "Reem");
        await service.RunAsync(batch.Id, retryUnsuccessful: false, maxItems: 1);

        // Simulated crash while VKMS-2 was running, then a new process (new service instance, same database).
        var persisted = (await h.Batches.GetAsync(batch.Id))!;
        persisted.Items[1].Status = InvestigationExecutionStatus.Running;
        await h.Batches.UpdateItemAsync(persisted.Items[1]);
        var restarted = new LegacyBatchService(new EfLegacyBatchStore(new DbFactory(h.Db)), new EfInvestigationRecordStore(new DbFactory(h.Db)),
            h.Gateway, h.Orchestrator, h.Sessions, h.Environments,
            new LegacyBatchOptions { PollInterval = TimeSpan.FromMilliseconds(25), RecordWait = TimeSpan.FromSeconds(10) }, NullLogger<LegacyBatchService>.Instance);
        await restarted.RunAsync(batch.Id, retryUnsuccessful: false);

        var final = (await h.Batches.GetAsync(batch.Id))!;
        Assert.That(final.Items.Select(i => i.Status), Has.All.Not.EqualTo(InvestigationExecutionStatus.Pending).And.All.Not.EqualTo(InvestigationExecutionStatus.Running));
        Assert.That(final.Items[0].Attempts, Is.EqualTo(1), "the finished ticket was not investigated again");
        Assert.That((await h.Records.ListAsync(batch.Id)).Count, Is.EqualTo(3));
        Assert.That(h.JiraServer.Writes, Is.Empty, "batches never publish");
    }
}
