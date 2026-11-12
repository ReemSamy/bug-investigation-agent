using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AI.BugInvestigationTool.Services.AzureDevOps;
using AI.BugInvestigationTool.Services.Jira;

namespace AI.BugInvestigationTool.Tests;

public class JiraClientTests
{
    private static (JiraClient Client, FakeHttp Http) Create(Action<FakeHttp>? extra = null)
    {
        var http = new FakeHttp();
        extra?.Invoke(http);
        http.OnJson(HttpMethod.Get, "/rest/api/3/issue/SHOP-42?", TestEngine.Fixture("jira-issue.json"))
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("/comment"), (r, _) =>
            {
                // 3 pages of comments: 100 + 100 + 5 (total 205)
                var start = int.Parse(System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)["startAt"]!);
                var count = Math.Min(100, 205 - start);
                var comments = Enumerable.Range(start, count).Select(i => new
                {
                    author = new { displayName = $"User {i}" }, created = "2026-01-01T10:00:00.000+0000",
                    body = new { type = "doc", version = 1, content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text = $"comment {i}" } } } } },
                });
                return FakeHttp.Json(JsonSerializer.Serialize(new { startAt = start, maxResults = 100, total = 205, comments }));
            })
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("/changelog"), (r, _) =>
            {
                // 2 pages with isLast
                var start = int.Parse(System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)["startAt"]!);
                var values = new[] { new { created = $"2026-01-0{start + 1}T10:00:00", author = new { displayName = "Bot" }, items = new[] { new { field = "status", fromString = "Open", toString = $"S{start}" } } } };
                return FakeHttp.Json(JsonSerializer.Serialize(new { startAt = start, isLast = start >= 1, values }));
            });
        return (new JiraClient(new HttpClient(http), "https://acme.atlassian.net", "qa@acme.test", "tok"), http);
    }

    [Fact]
    public async Task Extracts_all_relevant_ticket_fields()
    {
        var (client, http) = Create();
        var issue = await client.GetIssueAsync("SHOP-42", CancellationToken.None);

        Assert.Equal("10042", issue.Id);
        Assert.Equal("SHOP-42", issue.Key);
        Assert.Equal("https://acme.atlassian.net/browse/SHOP-42", issue.Url);
        Assert.Equal("Saving an order shows a server error", issue.Summary);
        Assert.Contains("- Click Save", issue.Description);
        Assert.Contains("Expected result: the order is saved", issue.Description);
        Assert.Contains("https://qa.example.com/app", issue.Environment);
        Assert.Equal(("High", "Ready for QA", "Bug"), (issue.Priority, issue.Status, issue.IssueType));
        Assert.Equal(["2.3.0"], issue.AffectedVersions);
        Assert.Equal(["2.3.1"], issue.FixVersions);
        Assert.Equal(["Orders"], issue.Components);
        Assert.Equal(["regression", "orders"], issue.Labels);
        Assert.Equal(("Dev One", "QA Two"), (issue.Assignee, issue.Reporter));
        Assert.Equal("SHOP-1 Orders epic", issue.Parent);
        Assert.Equal(["SHOP-43 Add test [To Do]"], issue.Subtasks);
        Assert.Equal(["blocks SHOP-50 Release 2.3.1", "is duplicated by SHOP-12 Order save fails"], issue.LinkedIssues);
        Assert.Equal(["Sprint 14 (active)"], issue.Sprints);
        // Custom fields resolved through the names map, not hard-coded IDs; null fields skipped.
        Assert.Equal("High", issue.CustomFields["Customer Impact"]);
        Assert.Equal("API, Database", issue.CustomFields["Root Cause Area"]);
        Assert.False(issue.CustomFields.ContainsKey("Empty Field"));
        Assert.Equal(2, issue.Attachments.Count);

        // Basic auth header with email:token, never in the URL.
        var call = http.Calls[0];
        Assert.StartsWith("Basic ", call.Headers["Authorization"]);
        Assert.DoesNotContain("tok", call.Url.ToString());
    }

    [Fact]
    public async Task Follows_pagination_for_comments_and_changelog()
    {
        var (client, http) = Create();
        var issue = await client.GetIssueAsync("SHOP-42", CancellationToken.None);

        Assert.Equal(205, issue.Comments.Count);
        Assert.Equal("comment 204", issue.Comments[^1].Body);
        Assert.Equal(3, http.Calls.Count(c => c.Url.AbsolutePath.EndsWith("/comment")));
        Assert.Equal(2, issue.History.Count);
        Assert.Equal(2, http.Calls.Count(c => c.Url.AbsolutePath.EndsWith("/changelog")));
    }

    [Fact]
    public async Task Downloads_attachments_only_from_the_jira_host()
    {
        var (client, http) = Create(h => h.On(r => r.RequestUri!.AbsolutePath.Contains("/attachment/content/900"),
            (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ERROR NullReference at OrderService.Save") }));
        var issue = await client.GetIssueAsync("SHOP-42", CancellationToken.None);

        var ok = await client.DownloadAttachmentAsync(issue.Attachments[0], 1000, CancellationToken.None);
        var evil = await client.DownloadAttachmentAsync(issue.Attachments[1], 1000, CancellationToken.None);
        var tooBig = await client.DownloadAttachmentAsync(issue.Attachments[0], 10, CancellationToken.None);

        Assert.NotNull(ok);
        Assert.Null(evil);
        Assert.Null(tooBig);
        Assert.DoesNotContain(http.Calls, c => c.Url.Host == "evil.example.net");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "API token")]
    [InlineData(HttpStatusCode.NotFound, "cannot see it")]
    public async Task Maps_http_errors_to_clear_messages(HttpStatusCode status, string hint)
    {
        var http = new FakeHttp().OnJson(HttpMethod.Get, "/issue/", "{\"errorMessages\":[\"x\"]}", status);
        var client = new JiraClient(new HttpClient(http), "https://acme.atlassian.net", "a", "b");
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => client.GetIssueAsync("SHOP-1", CancellationToken.None));
        Assert.Contains(hint, ex.Message);
        Assert.Equal((int)status, ex.StatusCode);
    }

    [Fact]
    public async Task Html_response_is_reported_as_wrong_base_url()
    {
        var http = new FakeHttp().On(_ => true, (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") });
        var client = new JiraClient(new HttpClient(http), "https://acme.atlassian.net", "a", "b");
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => client.GetIssueAsync("SHOP-1", CancellationToken.None));
        Assert.Contains("base URL", ex.Message);
    }

    [Fact]
    public void Converts_adf_with_lists_code_and_mentions()
    {
        var adf = JsonDocument.Parse("""
            {"type":"doc","content":[
              {"type":"heading","content":[{"type":"text","text":"Title"}]},
              {"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"one"},{"type":"mention","attrs":{"text":"@Ann"}}]}]}]},
              {"type":"codeBlock","content":[{"type":"text","text":"var x = 1;"}]}
            ]}
            """).RootElement;
        var text = Adf.ToText(adf);
        Assert.Contains("Title", text);
        Assert.Contains("- one@Ann", text);
        Assert.Contains("```\nvar x = 1;\n```", text);
    }
}

public class AzureDevOpsClientTests
{
    [Fact]
    public async Task Code_search_posts_filtered_query_and_parses_hits()
    {
        var http = new FakeHttp().OnJson(HttpMethod.Post, "almsearch.dev.azure.com/Acme/Shop/_apis/search/codesearchresults", """
            {"count":2,"results":[
              {"fileName":"OrderService.cs","path":"/src/Orders/OrderService.cs","repository":{"name":"shop-api"}},
              {"fileName":"OrderController.cs","path":"/src/Api/OrderController.cs","repository":{"name":"shop-api"}}]}
            """);
        var ado = new AzureDevOpsClient(new HttpClient(http), "Acme", "Shop", "pat");

        var hits = await ado.SearchCodeAsync("SaveOrder", "shop-api", 10, CancellationToken.None);

        Assert.Equal(2, hits.Count);
        Assert.Equal(("shop-api", "/src/Orders/OrderService.cs"), (hits[0].Repository, hits[0].Path));
        var body = JsonNode.Parse(http.Calls[0].Body)!;
        Assert.Equal("SaveOrder", body["searchText"]!.GetValue<string>());
        Assert.Equal("shop-api", body["filters"]!["Repository"]![0]!.GetValue<string>());
        Assert.Equal("Basic " + Convert.ToBase64String(":pat"u8.ToArray()), http.Calls[0].Headers["Authorization"]);
    }

    [Fact]
    public async Task Gets_file_content_at_a_version_and_returns_null_when_missing()
    {
        var http = new FakeHttp()
            .On(r => r.RequestUri!.Query.Contains("path=%2Fsrc%2FA.cs"), (_, _) => FakeHttp.Json("""{"content":"line1\nline2"}"""))
            .OnJson(HttpMethod.Get, "/items", "{}", HttpStatusCode.NotFound);
        var ado = new AzureDevOpsClient(new HttpClient(http), "Acme", "Shop", "pat");

        Assert.Equal("line1\nline2", await ado.GetFileAsync("shop-api", "/src/A.cs", "abc123", true, CancellationToken.None));
        Assert.Contains("versionDescriptor.versionType=commit", http.Calls[0].Url.Query);
        Assert.Null(await ado.GetFileAsync("shop-api", "/src/Missing.cs", null, false, CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_pat_html_signin_page_is_reported_as_auth_failure()
    {
        var http = new FakeHttp().On(_ => true, (_, _) => new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation) { Content = new StringContent("<html>Sign in</html>") });
        var ado = new AzureDevOpsClient(new HttpClient(http), "Acme", "Shop", "bad");
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => ado.ListRepositoriesAsync(CancellationToken.None));
        Assert.Contains("PAT", ex.Message);
    }

    public static FakeHttp PullRequestFake()
    {
        const string repo = "dev.azure.com/Acme/Shop/_apis/git/repositories/shop-api";
        return new FakeHttp()
            .OnJson(HttpMethod.Get, $"{repo}/pullrequests/77/iterations/2/changes", """
                {"changeEntries":[
                  {"changeType":"edit","item":{"path":"/src/Orders/OrderService.cs","gitObjectType":"blob"}},
                  {"changeType":"add","item":{"path":"/tests/OrderServiceTests.cs","gitObjectType":"blob"}},
                  {"changeType":"edit","item":{"path":"/docs/logo.png","gitObjectType":"blob"}},
                  {"changeType":"edit","item":{"path":"/src","gitObjectType":"tree"}}]}
                """)
            .OnJson(HttpMethod.Get, $"{repo}/pullrequests/77/iterations?", """
                {"value":[{"id":1},{"id":2,"sourceRefCommit":{"commitId":"src222"},"commonRefCommit":{"commitId":"base111"}}]}
                """)
            .OnJson(HttpMethod.Get, $"{repo}/pullrequests/77/workitems", """{"value":[{"id":"5001"}]}""")
            .OnJson(HttpMethod.Get, $"{repo}/pullrequests/77?", """
                {"pullRequestId":77,"title":"SHOP-42 Fix order save","description":"Handles null customer","status":"active",
                 "createdBy":{"displayName":"Dev One"},"sourceRefName":"refs/heads/bugfix/SHOP-42","targetRefName":"refs/heads/main",
                 "lastMergeSourceCommit":{"commitId":"x"},"lastMergeTargetCommit":{"commitId":"y"},"repository":{"id":"r1"}}
                """)
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("/items") && r.RequestUri.Query.Contains("OrderService.cs") && r.RequestUri.Query.Contains("base111"),
                (_, _) => FakeHttp.Json("""{"content":"public class OrderService\n{\n    public void Save(Order o)\n    {\n        db.Save(o.Customer.Id);\n    }\n}"}"""))
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("/items") && r.RequestUri.Query.Contains("OrderService.cs") && r.RequestUri.Query.Contains("src222"),
                (_, _) => FakeHttp.Json("""{"content":"public class OrderService\n{\n    public void Save(Order o)\n    {\n        var id = o.Customer?.Id;\n        db.Save(id.Value);\n    }\n}"}"""))
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("/items") && r.RequestUri.Query.Contains("OrderServiceTests.cs"),
                (_, _) => FakeHttp.Json("""{"content":"public class OrderServiceTests { }"}"""));
    }

    [Fact]
    public async Task Loads_pull_request_with_changed_files_diffs_and_links()
    {
        var http = PullRequestFake();
        var ado = new AzureDevOpsClient(new HttpClient(http), "Acme", "Shop", "pat");

        var pr = await ado.GetPullRequestAsync("shop-api", 77, 50, 4000, CancellationToken.None);

        Assert.Equal(("bugfix/SHOP-42", "main"), (pr.SourceBranch, pr.TargetBranch));
        Assert.Equal(("src222", "base111"), (pr.SourceCommit, pr.BaseCommit)); // iteration commits = Files tab
        Assert.Equal(["SHOP-42"], pr.LinkedJiraKeys);
        Assert.Equal(["5001"], pr.LinkedWorkItems);
        Assert.Equal(3, pr.Files.Count); // tree entry ignored

        var svc = pr.Files[0];
        Assert.Equal(2, svc.Additions);
        Assert.Equal(1, svc.Deletions);
        Assert.Matches(@"(?m)^\+ +6 \|         db\.Save\(id\.Value\);$", svc.Diff);
        Assert.Contains(svc.ChangedRanges, r => r.Start <= 5 && r.End >= 6);
        Assert.Equal(1, pr.Files[1].Additions); // added file: no request for the old version
        Assert.DoesNotContain(http.Calls, c => c.Url.Query.Contains("OrderServiceTests.cs") && c.Url.Query.Contains("base111"));
        Assert.Equal("binary file", pr.Files[2].SkippedReason);
    }
}
