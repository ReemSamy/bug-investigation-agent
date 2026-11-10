using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Investigations;
using BugInvestigationAgent.Core.Jira;
using BugInvestigationAgent.Infrastructure.Investigations;
using BugInvestigationAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BugInvestigationAgent.Api.Tests;

public class InvestigationRecordPersistenceTests
{
    private string _file = null!;

    private sealed class Factory(string file) : IDbContextFactory<AgentDbContext>
    {
        public AgentDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<AgentDbContext>().UseSqlite($"Data Source={file};Default Timeout=60").Options);
    }

    [SetUp]
    public void SetUp() => _file = Path.Combine(Path.GetTempPath(), $"bia-rec-{Guid.NewGuid():N}.db");

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_file); } catch { }
    }

    [Test]
    public async Task Records_and_batches_persist_across_contexts_with_optimistic_concurrency()
    {
        var factory = new Factory(_file);
        await using (var db = factory.CreateDbContext())
            await DatabaseMigrator.MigrateAsync(db);

        var store = new EfInvestigationRecordStore(factory);
        var record = new InvestigationRecord
        {
            InvestigationId = Guid.NewGuid(), IssueKey = "VKMS-7", ExecutionStatus = InvestigationExecutionStatus.Inconclusive,
            Assessment = BugAssessment.NeedsMoreInformation, FlagsCsv = "CriticalCandidate", ReproductionOutcome = ReproductionResult.NotReproduced,
            CommentDraft = "draft", ReportMarkdown = "# report", IssueFingerprint = "FP"
        };
        await store.AddAsync(record);

        // "Restart": a new store instance reads the same row.
        var loaded = (await new EfInvestigationRecordStore(factory).GetAsync(record.Id))!;
        Assert.That(loaded.DraftStatus, Is.EqualTo(DraftReviewStatus.PendingReview), "default state");
        Assert.That(loaded.PublishStatus, Is.EqualTo(CommentPublishStatus.NotPublished));
        Assert.That(loaded.Flags, Is.EqualTo(new[] { "CriticalCandidate" }));
        Assert.That(loaded.ReportMarkdown, Is.EqualTo("# report"));
        Assert.That(await store.FindByInvestigationAsync(record.InvestigationId, DateTimeOffset.UtcNow.AddMinutes(-1)), Is.Not.Null);

        var a = (await store.GetAsync(record.Id))!;
        var b = (await store.GetAsync(record.Id))!;
        a.DraftStatus = DraftReviewStatus.Approved;
        Assert.That(await store.TryUpdateAsync(a), Is.True);
        b.DraftStatus = DraftReviewStatus.Rejected;
        Assert.That(await store.TryUpdateAsync(b), Is.False, "stale copy cannot overwrite");
        Assert.That((await store.GetAsync(record.Id))!.DraftStatus, Is.EqualTo(DraftReviewStatus.Approved));

        var batches = new EfLegacyBatchStore(factory);
        var batch = new LegacyBatch
        {
            FilterId = "11265", Jql = "project = VKMS", EnvironmentId = "KMSQA",
            Items = { new LegacyBatchItem { IssueKey = "VKMS-2", Position = 1 }, new LegacyBatchItem { IssueKey = "VKMS-1", Position = 0 } }
        };
        await batches.AddAsync(batch);
        var item = (await batches.GetAsync(batch.Id))!.Items[0];
        Assert.That(item.IssueKey, Is.EqualTo("VKMS-1"), "ordered by snapshot position");
        item.Status = InvestigationExecutionStatus.Completed;
        item.RecordId = record.Id;
        await batches.UpdateItemAsync(item);
        var reloaded = (await new EfLegacyBatchStore(factory).GetAsync(batch.Id))!;
        Assert.That(reloaded.Items[0].Status, Is.EqualTo(InvestigationExecutionStatus.Completed));
        Assert.That(reloaded.Items[1].Status, Is.EqualTo(InvestigationExecutionStatus.Pending));
    }
}

public class JiraIssueGatewayTests
{
    private sealed class Access : IJiraAccessProvider
    {
        public Task<JiraAccessResult> GetReadAccessAsync(RuntimeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(JiraAccessResult.Ok(new JiraAccess
            {
                BaseAddress = new Uri("https://jira.example.test/"),
                Authorization = new AuthenticationHeaderValue("Basic", "dGVzdA=="),
                Description = "test"
            }));
    }

    private sealed class Jira : HttpMessageHandler
    {
        public readonly List<string> Requests = new();
        public string Description = "{\"type\":\"doc\",\"content\":[]}";
        public string Updated = "2026-11-10T10:00:00.000+0000";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests) Requests.Add($"{request.Method} {path}");
            if (path == "/rest/api/3/filter/11265")
                return Json("{\"id\":\"11265\",\"jql\":\"project = VKMS AND status = Backlog\"}");
            if (path == "/rest/api/3/search/jql")
            {
                var token = JsonNode.Parse(body)!["nextPageToken"]?.GetValue<string>();
                return token is null
                    ? Json("{\"issues\":[{\"key\":\"VKMS-1\"},{\"key\":\"VKMS-2\"}],\"nextPageToken\":\"p2\"}")
                    : Json("{\"issues\":[{\"key\":\"VKMS-3\"},{\"key\":\"VKMS-2\"}],\"isLast\":true}");
            }
            if (path == "/rest/api/3/issue/VKMS-7" && request.Method == HttpMethod.Get)
                return Json($"{{\"key\":\"VKMS-7\",\"fields\":{{\"summary\":\"S\",\"description\":{Description},\"status\":{{\"name\":\"Backlog\"}},\"resolution\":null,\"priority\":{{\"name\":\"Low\"}},\"created\":\"2021-08-17T10:00:00.000+0000\",\"updated\":\"{Updated}\",\"labels\":[\"legacy\"]}}}}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/json") };
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Test]
    public async Task Filter_snapshot_reads_every_page_without_duplicates()
    {
        var jira = new Jira();
        var (jql, keys) = await new JiraIssueGateway(new Access(), new Clients(jira)).GetFilterIssuesAsync("11265");
        Assert.That(jql, Is.EqualTo("project = VKMS AND status = Backlog"));
        Assert.That(keys, Is.EqualTo(new[] { "VKMS-1", "VKMS-2", "VKMS-3" }));
        Assert.That(jira.Requests.Count(r => r == "POST /rest/api/3/search/jql"), Is.EqualTo(2));
    }

    [Test]
    public async Task Fingerprint_ignores_updated_timestamp_but_detects_material_changes()
    {
        var jira = new Jira();
        var gateway = new JiraIssueGateway(new Access(), new Clients(jira));
        var first = (await gateway.GetSnapshotAsync("VKMS-7"))!;
        Assert.That(first.Priority, Is.EqualTo("Low"));
        Assert.That(first.Created!.Value.Year, Is.EqualTo(2021));

        jira.Updated = "2026-11-11T10:00:00.000+0000"; // e.g. someone commented
        Assert.That((await gateway.GetSnapshotAsync("VKMS-7"))!.Fingerprint, Is.EqualTo(first.Fingerprint));

        jira.Description = "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\"}]}"; // description edited
        Assert.That((await gateway.GetSnapshotAsync("VKMS-7"))!.Fingerprint, Is.Not.EqualTo(first.Fingerprint));
    }

    [Test]
    public async Task Gateway_only_reads_jira()
    {
        var jira = new Jira();
        var gateway = new JiraIssueGateway(new Access(), new Clients(jira));
        await gateway.GetSnapshotAsync("VKMS-7");
        await gateway.GetFilterIssuesAsync("11265");

        Assert.That(jira.Requests.Where(r => !r.StartsWith("GET ")), Is.All.EqualTo("POST /rest/api/3/search/jql"),
            "only reads (JQL search is a POST that changes nothing)");
        Assert.That(typeof(JiraIssueGateway).GetMethods().Select(m => m.Name), Has.None.Contains("Comment"));
    }
}
