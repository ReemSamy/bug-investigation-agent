using System.Net;
using System.Text;
using BugInvestigationAgent.Core.Abstractions;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Orchestration;
using BugInvestigationAgent.Infrastructure.Settings;
using BugInvestigationAgent.Providers.Anthropic;
using BugInvestigationAgent.Providers.Stubs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Skills.Tests;

public class SecureSettingsAndLlmConfigurationTests
{
    private string _root = "";
    private string SettingsFile => Path.Combine(_root, "App_Data", "local-settings.json");

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "bia-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "App_Data"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private LocalSettingsService Service(IDataProtectionProvider protection) =>
        new(new ContentRootAccessor(_root), Options.Create(new JiraOptions()), Options.Create(new AgentOptions { ActiveProviderId = "null" }),
            Options.Create(new LlmProviderOptions()), protection);

    [Test]
    public void Plaintext_secrets_from_an_older_version_are_encrypted_on_first_load()
    {
        File.WriteAllText(SettingsFile, """
            {"Jira":{"BaseUrl":"https://jira.example.test","Email":"me@example.test","ApiToken":"ATATT-plain-jira-token"},
             "LlmProviders":{"Anthropic":{"ApiKey":"sk-ant-plain-key","Model":"claude-opus-5-5"},"OpenAiCompatible":{"ApiKey":""}}}
            """);
        var protection = new EphemeralDataProtectionProvider();

        var settings = Service(protection);

        var onDisk = File.ReadAllText(SettingsFile);
        Assert.That(onDisk, Does.Not.Contain("ATATT-plain-jira-token").And.Not.Contain("sk-ant-plain-key"));
        Assert.That(onDisk, Does.Contain(LocalSettingsService.ProtectedPrefix));
        Assert.That(settings.GetEffective().ApiToken, Is.EqualTo("ATATT-plain-jira-token"), "usable in memory");
        Assert.That(settings.GetEffectiveLlm().Anthropic.ApiKey, Is.EqualTo("sk-ant-plain-key"));

        // Restart with the same key ring: still readable; nothing re-written in plaintext.
        var again = Service(protection);
        Assert.That(again.GetEffective().ApiToken, Is.EqualTo("ATATT-plain-jira-token"));
        Assert.That(again.UnreadableSecrets, Is.Empty);
    }

    [Test]
    public void Saved_secrets_are_never_written_in_plaintext_and_a_foreign_key_ring_cannot_read_them()
    {
        var settings = Service(new EphemeralDataProtectionProvider());
        var doc = settings.Snapshot;
        doc.Jira.ApiToken = "ATATT-new-token";
        doc.LlmProviders.OpenAiCompatible.ApiKey = "sk-openai-key";
        settings.Save(doc);
        settings.SetSignedInAccount("Reem", "me@example.test"); // a second write path

        var onDisk = File.ReadAllText(SettingsFile);
        Assert.That(onDisk, Does.Not.Contain("ATATT-new-token").And.Not.Contain("sk-openai-key"));
        Assert.That(File.Exists(SettingsFile + ".tmp"), Is.False, "atomic replace leaves no temp file");

        var other = Service(new EphemeralDataProtectionProvider()); // another Windows account / lost key ring
        Assert.That(other.GetEffective().ApiToken, Is.Empty);
        Assert.That(other.UnreadableSecrets, Does.Contain("Jira API token").And.Contain("OpenAI-compatible API key"));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"content\":[{\"type\":\"text\",\"text\":\"OK\"}],\"stop_reason\":\"end_turn\"}", Encoding.UTF8, "application/json")
            };
        }
    }

    [Test]
    public async Task Settings_page_values_drive_provider_and_model_and_nothing_is_used_before_approval()
    {
        var settings = Service(new EphemeralDataProtectionProvider());
        var doc = settings.Snapshot;
        doc.ActiveProviderId = "anthropic";
        doc.LlmProviders.Anthropic.ApiKey = "sk-ant-test-key";
        doc.LlmProviders.Anthropic.Model = "claude-opus-5-5";
        settings.Save(doc);

        var handler = new CapturingHandler();
        var anthropic = new AnthropicRuntime(new HttpClient(handler), settings, NullLogger<AnthropicRuntime>.Instance);
        var resolver = new AgentRuntimeResolver(new IAgentRuntime[] { new NullAgentRuntime(), anthropic }, settings);

        // Without the recorded company approval, investigations get the offline runtime: no data leaves the machine.
        Assert.That(resolver.GetCurrent().ProviderId, Is.EqualTo("null"));
        Assert.That(resolver.LlmUnavailableReason, Does.Contain("approval"));

        doc = settings.Snapshot;
        doc.LlmDataSharingApprovedBy = "Reem";
        doc.LlmDataSharingApprovedAtUtc = DateTimeOffset.UtcNow;
        settings.Save(doc);
        Assert.That(resolver.GetCurrent().ProviderId, Is.EqualTo("anthropic"), "no restart needed");
        Assert.That(resolver.LlmUnavailableReason, Is.Null);

        await resolver.GetCurrent().CompleteAsync(new AgentTurnRequest { Messages = [new AgentMessage { Role = "user", Content = "hi" }], Temperature = 0.1 });
        Assert.That(handler.Body, Does.Contain("\"model\":\"claude-opus-5-5\"").And.Not.Contain("temperature"));
        Assert.That(handler.Last!.Headers.GetValues("x-api-key").Single(), Is.EqualTo("sk-ant-test-key"));
    }

    [Test]
    public void Missing_or_invalid_llm_configuration_produces_clear_errors_without_the_key()
    {
        var settings = Service(new EphemeralDataProtectionProvider());
        var doc = settings.Snapshot;
        doc.ActiveProviderId = "anthropic";
        doc.LlmDataSharingApprovedAtUtc = DateTimeOffset.UtcNow;
        settings.Save(doc);
        var anthropic = new AnthropicRuntime(new HttpClient(new CapturingHandler()), settings, NullLogger<AnthropicRuntime>.Instance);
        var resolver = new AgentRuntimeResolver(new IAgentRuntime[] { new NullAgentRuntime(), anthropic, new CursorRuntime() }, settings);

        Assert.That(resolver.LlmUnavailableReason, Does.Contain("No API key"));
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => anthropic.CompleteAsync(new AgentTurnRequest { Messages = [] }));
        Assert.That(ex!.Message, Does.Contain("API key is not configured"));

        doc = settings.Snapshot;
        doc.ActiveProviderId = "cursor";
        settings.Save(doc);
        Assert.That(resolver.LlmUnavailableReason, Does.Contain("not implemented"));
        doc.ActiveProviderId = "does-not-exist";
        settings.Save(doc);
        Assert.That(resolver.LlmUnavailableReason, Does.Contain("does not exist"));
    }
}
