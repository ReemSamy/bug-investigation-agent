namespace BugInvestigationAgent.Core.Configuration;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string ActiveProviderId { get; set; } = "openai-compatible";
    public AgentPermissionOptions Permissions { get; set; } = new();
    public List<string> ValidationCommandAllowlist { get; set; } = new()
    {
        "dotnet build",
        "dotnet test"
    };
    public string DataDirectory { get; set; } = "App_Data";
    public string EvidenceDirectory { get; set; } = "App_Data/evidence";
    /// <summary>Optional default local repo path so users are not asked every run.</summary>
    public string? DefaultWorkspacePath { get; set; }

    /// <summary>Playwright timing for slow portal pages (KMS Assignments spinner, modules, forms).</summary>
    public BrowserTimingOptions Browser { get; set; } = new();
}

public sealed class BrowserTimingOptions
{
    /// <summary>Navigation / goto timeout in ms.</summary>
    public int NavigationTimeoutMs { get; set; } = 120_000;
    /// <summary>Click / fill action timeout in ms.</summary>
    public int ActionTimeoutMs { get; set; } = 45_000;
    /// <summary>Wait after navigation/click for SPA content (ms).</summary>
    public int SettleTimeoutMs { get; set; } = 45_000;
    /// <summary>Extra pause after settle while spinners may still run (ms).</summary>
    public int PostActionDelayMs { get; set; } = 3_000;
    /// <summary>Max wait for loading indicators to disappear (ms).</summary>
    public int SpinnerTimeoutMs { get; set; } = 60_000;
    /// <summary>Short probe timeout when scanning for a button/link (ms). Keep small so missing labels don't freeze a step.</summary>
    public int ProbeTimeoutMs { get; set; } = 2_500;
}

public sealed class AgentPermissionOptions
{
    public bool ReadJira { get; set; } = true;
    public bool UseBrowser { get; set; } = true;
    public bool ReadWorkspace { get; set; } = true;
    public bool ModifyCode { get; set; } = true;
    public bool RunValidation { get; set; } = true;
    public bool ManageCredentials { get; set; } = true;
    public bool ManageMemory { get; set; } = true;
    public bool GitCommit { get; set; }
    public bool GitPush { get; set; }
    public bool CreatePullRequest { get; set; }
    public bool TriggerCi { get; set; }
    public bool Deploy { get; set; }

    public Permissions.AgentPermission ToFlags()
    {
        var flags = Permissions.AgentPermission.None;
        if (ReadJira) flags |= Permissions.AgentPermission.ReadJira;
        if (UseBrowser) flags |= Permissions.AgentPermission.UseBrowser;
        if (ReadWorkspace) flags |= Permissions.AgentPermission.ReadWorkspace;
        if (ModifyCode) flags |= Permissions.AgentPermission.ModifyCode;
        if (RunValidation) flags |= Permissions.AgentPermission.RunValidation;
        if (ManageCredentials) flags |= Permissions.AgentPermission.ManageCredentials;
        if (ManageMemory) flags |= Permissions.AgentPermission.ManageMemory;
        if (GitCommit) flags |= Permissions.AgentPermission.GitCommit;
        if (GitPush) flags |= Permissions.AgentPermission.GitPush;
        if (CreatePullRequest) flags |= Permissions.AgentPermission.CreatePullRequest;
        if (TriggerCi) flags |= Permissions.AgentPermission.TriggerCi;
        if (Deploy) flags |= Permissions.AgentPermission.Deploy;
        return flags;
    }
}

public sealed class JiraOptions
{
    public const string SectionName = "Jira";
    public string BaseUrl { get; set; } = "";
    public string Email { get; set; } = "";
    public string ApiToken { get; set; } = "";
}

public sealed class LlmProviderOptions
{
    public const string SectionName = "LlmProviders";
    public OpenAiCompatibleOptions OpenAiCompatible { get; set; } = new();
    public AnthropicOptions Anthropic { get; set; } = new();
    public AzureOpenAiOptions AzureOpenAi { get; set; } = new();
    public CursorOptions Cursor { get; set; } = new();
}

public sealed class OpenAiCompatibleOptions
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
}

public sealed class AnthropicOptions
{
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-3-5-sonnet-latest";
    public string ApiVersion { get; set; } = "2023-06-01";
}

public sealed class AzureOpenAiOptions
{
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Deployment { get; set; } = "";
    public string ApiVersion { get; set; } = "2024-08-01-preview";
}

public sealed class CursorOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
}
