using System.Security.Cryptography;
using System.Text.Json;
using BugInvestigationAgent.Core.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Infrastructure.Settings;

/// <summary>
/// Local app settings (App_Data/local-settings.json). Secrets (the Jira API token and LLM API keys) are stored
/// encrypted with ASP.NET Data Protection (key ring protected by the Windows user's DPAPI key), never in plaintext.
/// Plaintext values from older versions are encrypted in place on first load. Decrypted values exist only in memory.
/// </summary>
public sealed class LocalSettingsService : IJiraConnectionSettings, IWorkspaceDefaults, BugInvestigationAgent.Core.Abstractions.ILlmSettingsSource
{
    public const string ProtectedPrefix = "dp1:";
    private const string Purpose = "BugInvestigationAgent.LocalSettings.Secrets.v1";
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly IDataProtector _protector;
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
        IOptions<LlmProviderOptions> llmFallback,
        IDataProtectionProvider dataProtection)
    {
        _protector = dataProtection.CreateProtector(Purpose);
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
            Persist();
        }
    }

    public void ClearSignedInAccount()
    {
        lock (_gate)
        {
            _doc.JiraSignedInDisplayName = null;
            _doc.JiraSignedInEmail = null;
            _doc.JiraSignedInAtUtc = null;
            Persist();
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

    // ILlmSettingsSource: the investigation workflow uses exactly what the Settings page saved (appsettings as fallback).
    string BugInvestigationAgent.Core.Abstractions.ILlmSettingsSource.ActiveProviderId => GetActiveProviderId();
    LlmProviderOptions BugInvestigationAgent.Core.Abstractions.ILlmSettingsSource.Current => GetEffectiveLlm();
    bool BugInvestigationAgent.Core.Abstractions.ILlmSettingsSource.ExternalDataSharingApproved => Snapshot.LlmDataSharingApprovedAtUtc is not null;

    /// <summary>Names of secrets that could not be decrypted on load (e.g. the key ring was deleted) and must be re-entered.</summary>
    public IReadOnlyList<string> UnreadableSecrets { get; private set; } = Array.Empty<string>();

    public void Reload()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                _doc = new LocalSettingsDocument();
                return;
            }

            LocalSettingsDocument stored;
            try
            {
                stored = JsonSerializer.Deserialize<LocalSettingsDocument>(File.ReadAllText(_path)) ?? new LocalSettingsDocument();
            }
            catch (JsonException)
            {
                stored = new LocalSettingsDocument();
            }

            var unreadable = new List<string>();
            var hadPlaintext = false;
            string Open(string? value, string name)
            {
                if (string.IsNullOrEmpty(value))
                    return "";
                if (!value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
                {
                    hadPlaintext = true; // written by an older version: encrypted below
                    return value;
                }
                try
                {
                    return _protector.Unprotect(value[ProtectedPrefix.Length..]);
                }
                catch (CryptographicException)
                {
                    unreadable.Add(name);
                    return "";
                }
            }

            stored.Jira.ApiToken = Open(stored.Jira.ApiToken, "Jira API token");
            stored.LlmProviders.Anthropic.ApiKey = Open(stored.LlmProviders.Anthropic.ApiKey, "Anthropic API key");
            stored.LlmProviders.OpenAiCompatible.ApiKey = Open(stored.LlmProviders.OpenAiCompatible.ApiKey, "OpenAI-compatible API key");
            _doc = stored;
            UnreadableSecrets = unreadable;
            if (hadPlaintext)
                Persist(); // one-time migration: plaintext secrets are re-written encrypted
        }
    }

    public void Save(LocalSettingsDocument document)
    {
        lock (_gate)
        {
            _doc = Clone(document);
            Persist();
        }
    }

    /// <summary>Writes the document with every secret encrypted and replaces the file atomically. Caller holds the lock.</summary>
    private void Persist()
    {
        var onDisk = Clone(_doc);
        onDisk.Jira.ApiToken = Seal(onDisk.Jira.ApiToken);
        onDisk.LlmProviders.Anthropic.ApiKey = Seal(onDisk.LlmProviders.Anthropic.ApiKey);
        onDisk.LlmProviders.OpenAiCompatible.ApiKey = Seal(onDisk.LlmProviders.OpenAiCompatible.ApiKey);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(onDisk, WriteOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private string Seal(string? secret) =>
        string.IsNullOrEmpty(secret) ? "" : ProtectedPrefix + _protector.Protect(secret);

    private static string First(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred! : (fallback ?? "");

    private static LocalSettingsDocument Clone(LocalSettingsDocument d) => new()
    {
        DefaultWorkspacePath = d.DefaultWorkspacePath,
        ActiveProviderId = d.ActiveProviderId,
        JiraSignedInDisplayName = d.JiraSignedInDisplayName,
        JiraSignedInEmail = d.JiraSignedInEmail,
        JiraSignedInAtUtc = d.JiraSignedInAtUtc,
        LlmDataSharingApprovedBy = d.LlmDataSharingApprovedBy,
        LlmDataSharingApprovedAtUtc = d.LlmDataSharingApprovedAtUtc,
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
    /// <summary>Who recorded that the company approved sending Jira content and source code to the configured LLM provider.</summary>
    public string? LlmDataSharingApprovedBy { get; set; }
    public DateTimeOffset? LlmDataSharingApprovedAtUtc { get; set; }
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
