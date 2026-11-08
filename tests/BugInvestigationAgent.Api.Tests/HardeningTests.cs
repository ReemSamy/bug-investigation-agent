using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;
using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Infrastructure.Hosting;
using BugInvestigationAgent.Infrastructure.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BugInvestigationAgent.Api.Tests;

public class VerifiedCallerTests
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new ApiFactory();
        _client = _factory.CreateClient();
        _factory.Jira.GrantUser("tok-alice", "acc-alice", "ABC-1");
        _factory.Jira.GrantUser("tok-bob", "acc-bob", "ABC-1");
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<JsonObject> Rpc(string method, object @params, string? principal, string? userToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/a2a/json-rpc") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params }) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Fit.Mint(principal));
        if (userToken is not null)
            req.Headers.Add("x-forge-oauth-user", userToken);
        var resp = await _client.SendAsync(req);
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsObject();
    }

    [Test]
    public async Task Polling_without_a_signed_principal_requires_proof_of_identity_from_the_callers_own_token()
    {
        var created = await Rpc("SendMessage", new
        {
            message = new { role = "ROLE_USER", messageId = "m1", parts = new object[] { new { data = new { userAccountId = "acc-alice", issue = new { id = "1001", fields = new { key = "ABC-1" } } } } } }
        }, "acc-alice", "tok-alice");
        var taskId = created["result"]!["task"]!["id"]!.GetValue<string>();

        // Same site, no FIT principal: only the owner's own token proves the caller.
        var owner = await Rpc("GetTask", new { id = taskId }, null, "tok-alice");
        Assert.That(owner["result"], Is.Not.Null);

        var otherUser = await Rpc("GetTask", new { id = taskId }, null, "tok-bob");
        Assert.That(otherUser["error"]!["code"]!.GetValue<int>(), Is.EqualTo(-32001));

        var anonymous = await Rpc("GetTask", new { id = taskId }, null, null);
        Assert.That(anonymous["error"]!["code"]!.GetValue<int>(), Is.EqualTo(-32001), "a task id alone never grants access");

        var cancelByOther = await Rpc("CancelTask", new { id = taskId }, null, "tok-bob");
        Assert.That(cancelByOther["error"]!["code"]!.GetValue<int>(), Is.EqualTo(-32001));
    }
}

public class HostedSafetyTests
{
    [Test]
    public void Rollout_permission_mask_disables_code_git_ci_and_deploy_even_if_configured()
    {
        var everything = new PermissionGate((AgentPermission)int.MaxValue);
        var masked = HostedPermissions.Mask(everything);
        foreach (var p in new[] { AgentPermission.ModifyCode, AgentPermission.RunValidation, AgentPermission.GitCommit, AgentPermission.GitPush,
                     AgentPermission.CreatePullRequest, AgentPermission.TriggerCi, AgentPermission.Deploy })
            Assert.That(masked.IsAllowed(p), Is.False, p.ToString());
        Assert.That(masked.IsAllowed(AgentPermission.UseBrowser | AgentPermission.ReadJira | AgentPermission.ReadWorkspace), Is.True);
    }

    [Test]
    public void Hosted_identity_never_falls_back_to_the_os_account()
    {
        var accessor = new HostedOnlyCurrentUserAccessor();
        Assert.Throws<InvalidOperationException>(() => _ = accessor.UserId);

        using (HostedJobScope.Enter(new HostedJobIdentity(Guid.NewGuid(), "t", "cloud-1", null, "acc-1", "1", "A-1")))
            Assert.That(accessor.UserId, Is.EqualTo("atlassian:cloud-1:acc-1"));
    }

    [Test]
    public void Production_configuration_is_rejected_when_insecure()
    {
        var insecure = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Forge:AppId"] = "x" }).Build();
        var errors = HostedConfigurationValidator.Validate(insecure, isDevelopment: false, isApi: true);
        Assert.That(errors, Has.Some.Contains("AllowedCloudIds"));
        Assert.That(errors, Has.Some.Contains("PostgreSQL"));
        Assert.That(errors, Has.Some.Contains("DataProtectionKeyId"));
        Assert.That(errors, Has.Some.Contains("ServiceBusNamespace"));
        Assert.That(errors, Has.Some.Contains("PublicBaseUrl"));
        Assert.Throws<InvalidOperationException>(() => HostedConfigurationValidator.ThrowIfInvalid(insecure, false, true));

        var secure = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forge:AppId"] = "x", ["Forge:AllowedCloudIds:0"] = "c", ["Forge:PublicBaseUrl"] = "https://api.example.test",
            ["Hosting:DatabaseProvider"] = "PostgreSQL", ["Hosting:BlobServiceUri"] = "https://s.blob.core.windows.net",
            ["Hosting:DataProtectionKeyId"] = "https://kv/keys/dp", ["Hosting:ServiceBusNamespace"] = "sb.servicebus.windows.net"
        }).Build();
        Assert.That(HostedConfigurationValidator.Validate(secure, false, true), Is.Empty);
        Assert.That(HostedConfigurationValidator.Validate(insecure, isDevelopment: true, isApi: true), Is.Empty, "local development stays easy");
    }
}

/// <summary>SIMULATED Service Bus: the Azure SDK receiver/sender are mocked (no emulator available without Docker).</summary>
public class ServiceBusSettlementTests
{
    private static (ServiceBusInvestigationQueue Queue, Mock<ServiceBusReceiver> Receiver) Queue(string body, int deliveryCount = 1)
    {
        var receiver = new Mock<ServiceBusReceiver>();
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString(body), deliveryCount: deliveryCount);
        receiver.Setup(r => r.ReceiveMessageAsync(It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>())).ReturnsAsync(message);
        return (new ServiceBusInvestigationQueue(new Mock<ServiceBusSender>().Object, receiver.Object), receiver);
    }

    [Test]
    public async Task Poison_message_is_dead_lettered()
    {
        var (queue, receiver) = Queue("not-a-job-id");
        var msg = (await queue.DequeueAsync(TimeSpan.FromSeconds(1)))!;
        Assert.That(msg.JobId, Is.Null);
        await QueueMessageProcessor.ProcessAsync(msg, null!, "w", NullLogger.Instance, CancellationToken.None);
        receiver.Verify(r => r.DeadLetterMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), "invalid-message", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        receiver.Verify(r => r.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Transient_failure_before_the_job_is_owned_abandons_for_redelivery()
    {
        var (queue, receiver) = Queue(Guid.NewGuid().ToString("N"), deliveryCount: 3);
        var msg = (await queue.DequeueAsync(TimeSpan.FromSeconds(1)))!;
        Assert.That(msg.DeliveryCount, Is.EqualTo(3));

        // A runner whose database is down: the lease cannot even be attempted.
        using var db = new TestDb();
        var jobs = new Mock<IInvestigationJobStore>();
        jobs.Setup(j => j.TryAcquireLeaseAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("database unavailable"));
        var runner = RunnerFactory.Create(jobs.Object);

        await QueueMessageProcessor.ProcessAsync(msg, runner, "w", NullLogger.Instance, CancellationToken.None);
        receiver.Verify(r => r.AbandonMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), null, It.IsAny<CancellationToken>()), Times.Once);
        receiver.Verify(r => r.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Message_for_a_job_owned_elsewhere_or_finished_is_completed_without_running()
    {
        var (queue, receiver) = Queue(Guid.NewGuid().ToString("N"));
        var msg = (await queue.DequeueAsync(TimeSpan.FromSeconds(1)))!;
        var jobs = new Mock<IInvestigationJobStore>();
        jobs.Setup(j => j.TryAcquireLeaseAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var outcome = await QueueMessageProcessor.ProcessAsync(msg, RunnerFactory.Create(jobs.Object), "w", NullLogger.Instance, CancellationToken.None);
        Assert.That(outcome, Is.EqualTo(JobRunOutcome.NotLeased));
        receiver.Verify(r => r.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}

internal static class RunnerFactory
{
    public static InvestigationJobRunner Create(IInvestigationJobStore jobs) => new(
        jobs, Mock.Of<IJobSecretStore>(), Mock.Of<IAuditLog>(), new Core.Sessions.InMemoryAgentSessionStore(),
        Mock.Of<Core.Orchestration.IAgentOrchestrator>(), Mock.Of<Core.Environments.IEnvironmentSelectionService>(), Mock.Of<IEvidenceStore>(),
        null!, new Core.Security.SecretRedactor(), Mock.Of<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
        Microsoft.Extensions.Options.Options.Create(new WorkerOptions()), Microsoft.Extensions.Options.Options.Create(new Core.Configuration.AgentOptions()),
        Microsoft.Extensions.Options.Options.Create(new Core.Repositories.RepositoryMappingOptions()),
        Microsoft.Extensions.Options.Options.Create(new Infrastructure.Forge.ForgeOptions()), NullLogger<InvestigationJobRunner>.Instance,
        new JobInputService(jobs, Mock.Of<IInvestigationQueue>(), Mock.Of<IJobSecretStore>()));
}
