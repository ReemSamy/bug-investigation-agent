using System.Text.Json;
using BugInvestigationAgent.Core.Configuration;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Infrastructure.Settings;

public sealed class LocalSettingsService : IJiraConnectionSettings, IWorkspaceDefaults
{
    private readonly string _path;
    private readonly IOptions<JiraOptions> _jiraFallback;
    private readonly IOptions<AgentOptions> _agentFallback;
    private readonly IOptions<LlmProviderOptions> _llmFallback;
    private readonly object _gate = new();
    private LocalSettingsDocument _doc = new();

    public LocalSettingsService(
        IHostEnvironmentAccessor env,
        IOptions<JiraOptions> jiraFallback,
        IOptions<AgentOptions> agentFallback,
        IOptions<LlmProviderOptions> llmFallback)
    {
        _jiraFallback = jiraFallback;
        _agentFallback = agentFallback;
        _llmFallback = llmFallback;
        var dataDir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "local-settings.json");
        Reload();
    }

    public LocalSettingsDocument Snapshot
    {
        get { lock (_gate) return Clone(_doc); }
    }

    public bool IsConfigured
    {
        get
        {
            var j = GetEffective();
            return !string.IsNullOrWhiteSpace(j.BaseUrl)
                   && !string.IsNullOrWhiteSpace(j.Email)
                   && !string.IsNullOrWhiteSpace(j.ApiToken);
        }
    }

    public bool IsSignedIn
    {
        get
        {
            var s = Snapshot;
            return IsConfigured
                   && !string.IsNullOrWhiteSpace(s.JiraSignedInDisplayName)
                   && s.JiraSignedInAtUtc is not null;
        }
    }

    public string? SignedInDisplayName => Snapshot.JiraSignedInDisplayName;
    public string? SignedInEmail => Snapshot.JiraSignedInEmail ?? GetEffective().Email;

    public void SetSignedInAccount(string? displayName, string? email)
    {
        lock (_gate)
        {
            _doc.JiraSignedInDisplayName = displayName;
            _doc.JiraSignedInEmail = email;
            _doc.JiraSignedInAtUtc = DateTimeOffset.UtcNow;
            File.WriteAllText(_path, JsonSerializer.Serialize(_doc, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public void ClearSignedInAccount()
    {
        lock (_gate)
        {
            _doc.JiraSignedInDisplayName = null;
            _doc.JiraSignedInEmail = null;
            _doc.JiraSignedInAtUtc = null;
            File.WriteAllText(_path, JsonSerializer.Serialize(_doc, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public string? DefaultWorkspacePath
    {
        get
        {
            var local = Snapshot.DefaultWorkspacePath;
            return !string.IsNullOrWhiteSpace(local) ? local : _agentFallback.Value.DefaultWorkspacePath;
        }
    }

    public JiraOptions GetEffective()
    {
        var fallback = _jiraFallback.Value;
        var local = Snapshot.Jira;
        return new JiraOptions
        {
            BaseUrl = JiraUrlNormalizer.NormalizeBaseUrl(First(local.BaseUrl, fallback.BaseUrl)),
            Email = First(local.Email, fallback.Email),
            ApiToken = First(local.ApiToken, fallback.ApiToken)
        };
    }

    public LlmProviderOptions GetEffectiveLlm()
    {
        var fallback = _llmFallback.Value;
        var local = Snapshot.LlmProviders;
        return new LlmProviderOptions
        {
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                BaseUrl = First(local.OpenAiCompatible.BaseUrl, fallback.OpenAiCompatible.BaseUrl),
                ApiKey = First(local.OpenAiCompatible.ApiKey, fallback.OpenAiCompatible.ApiKey),
                Model = First(local.OpenAiCompatible.Model, fallback.OpenAiCompatible.Model)
            },
            Anthropic = new AnthropicOptions
            {
                BaseUrl = First(local.Anthropic.BaseUrl, fallback.Anthropic.BaseUrl),
                ApiKey = First(local.Anthropic.ApiKey, fallback.Anthropic.ApiKey),
                Model = First(local.Anthropic.Model, fallback.Anthropic.Model),
                ApiVersion = First(local.Anthropic.ApiVersion, fallback.Anthropic.ApiVersion)
            },
            AzureOpenAi = fallback.AzureOpenAi,
            Cursor = fallback.Cursor
        };
    }

    public string GetActiveProviderId() =>
        First(Snapshot.ActiveProviderId, _agentFallback.Value.ActiveProviderId) ?? "null";

    public void Reload()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                _doc = new LocalSettingsDocument();
                return;
            }

            try
            {
                _doc = JsonSerializer.Deserialize<LocalSettingsDocument>(File.ReadAllText(_path)) ?? new LocalSettingsDocument();
            }
            catch
            {
                _doc = new LocalSettingsDocument();
            }
        }
    }

    public void Save(LocalSettingsDocument document)
    {
        lock (_gate)
        {
            _doc = Clone(document);
            File.WriteAllText(_path, JsonSerializer.Serialize(_doc, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static string First(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred! : (fallback ?? "");

    private static LocalSettingsDocument Clone(LocalSettingsDocument d) => new()
    {
        DefaultWorkspacePath = d.DefaultWorkspacePath,
        ActiveProviderId = d.ActiveProviderId,
        JiraSignedInDisplayName = d.JiraSignedInDisplayName,
        JiraSignedInEmail = d.JiraSignedInEmail,
        JiraSignedInAtUtc = d.JiraSignedInAtUtc,
        Jira = new JiraOptions
        {
            BaseUrl = d.Jira.BaseUrl,
            Email = d.Jira.Email,
            ApiToken = d.Jira.ApiToken
        },
        LlmProviders = new LlmProviderOptions
        {
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                BaseUrl = d.LlmProviders.OpenAiCompatible.BaseUrl,
                ApiKey = d.LlmProviders.OpenAiCompatible.ApiKey,
                Model = d.LlmProviders.OpenAiCompatible.Model
            },
            Anthropic = new AnthropicOptions
            {
                BaseUrl = d.LlmProviders.Anthropic.BaseUrl,
                ApiKey = d.LlmProviders.Anthropic.ApiKey,
                Model = d.LlmProviders.Anthropic.Model,
                ApiVersion = d.LlmProviders.Anthropic.ApiVersion
            }
        }
    };
}

public sealed class LocalSettingsDocument
{
    public string? DefaultWorkspacePath { get; set; }
    public string? ActiveProviderId { get; set; }
    public string? JiraSignedInDisplayName { get; set; }
    public string? JiraSignedInEmail { get; set; }
    public DateTimeOffset? JiraSignedInAtUtc { get; set; }
    public JiraOptions Jira { get; set; } = new();
    public LlmProviderOptions LlmProviders { get; set; } = new();
}

public interface IHostEnvironmentAccessor
{
    string ContentRootPath { get; }
}

public sealed class ContentRootAccessor : IHostEnvironmentAccessor
{
    public ContentRootAccessor(string contentRootPath) => ContentRootPath = contentRootPath;
    public string ContentRootPath { get; }
}
