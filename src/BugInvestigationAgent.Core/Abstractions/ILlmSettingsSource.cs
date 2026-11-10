using BugInvestigationAgent.Core.Configuration;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Core.Abstractions;

/// <summary>
/// Where the active LLM provider and its settings come from, read on every use so a change made in the
/// local app's Settings page applies to the next investigation without a restart.
/// </summary>
public interface ILlmSettingsSource
{
    string ActiveProviderId { get; }
    LlmProviderOptions Current { get; }
    /// <summary>
    /// True only when sending Jira content and source code to the external provider has been approved. Until then the
    /// pipeline behaves as if no LLM were configured (nothing leaves the machine).
    /// </summary>
    bool ExternalDataSharingApproved { get; }
}

/// <summary>Default (hosted API / worker): appsettings + environment / Key Vault configuration.</summary>
public sealed class OptionsLlmSettingsSource : ILlmSettingsSource
{
    private readonly IOptions<AgentOptions> _agent;
    private readonly IOptions<LlmProviderOptions> _llm;

    public OptionsLlmSettingsSource(IOptions<AgentOptions> agent, IOptions<LlmProviderOptions> llm)
    {
        _agent = agent;
        _llm = llm;
    }

    public string ActiveProviderId => _agent.Value.ActiveProviderId;
    public LlmProviderOptions Current => _llm.Value;
    /// <summary>Hosted deployments: the provider is configured by administrators as part of the approved deployment.</summary>
    public bool ExternalDataSharingApproved => true;
}
