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

    /// <summary>Historical Jira search limits and toggles.</summary>
    public HistoricalJiraOptions HistoricalJira { get; set; } = new();

    /// <summary>Local Visium KMS markdown documentation for RAG retrieval.</summary>
    public KmsDocsOptions KmsDocs { get; set; } = new();

    public SourceVersioningOptions SourceVersioning { get; set; } = new();

    public LlmReproductionOptions LlmReproduction { get; set; } = new();
}

public sealed class HistoricalJiraOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxCandidates { get; set; } = 15;
    public int MaxRankedResults { get; set; } = 5;
    /// <summary>Minimum LLM confidence (0-1) to adopt historical reproduction steps.</summary>
    public double MinStepAdoptionConfidence { get; set; } = 0.7;
    /// <summary>Minimum semantic score to keep an LLM-ranked candidate (exclude same-parent noise).</summary>
    public double MinSemanticScore { get; set; } = 0.55;
    /// <summary>Minimum keyword-overlap score when LLM ranking is unavailable.</summary>
    public double MinKeywordScore { get; set; } = 0.25;
}

public sealed class KmsDocsOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Path to cloned ADO markdown documentation root.</summary>
    public string? RootPath { get; set; }
    public int MaxChunks { get; set; } = 6;
    public int ChunkSizeChars { get; set; } = 1200;
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

    /// <summary>
    /// Deterministic = existing Playwright rules (default).
    /// Adaptive = reserved for Playwright MCP observe/act loop.
    /// Hybrid = try adaptive after deterministic nav failures (future).
    /// </summary>
    public string Mode { get; set; } = "Deterministic";

    /// <summary>
    /// Run Chromium without a window (default). Set false to watch the run and to complete MFA / manual
    /// sign-in steps yourself in the opened browser window.
    /// </summary>
    public bool Headless { get; set; } = true;
}

public sealed class AgentPermissionOptions
{
    public bool ReadJira { get; set; } = true;
    public bool UseBrowser { get; set; } = true;
    public bool ReadWorkspace { get; set; } = true;
    // Off by default; also forced off at runtime by Permissions.ReadOnlyRollout.
    public bool ModifyCode { get; set; }
    public bool RunValidation { get; set; }
    public bool ManageCredentials { get; set; } = true;
    public bool ManageMemory { get; set; } = true;
    public bool GitCommit { get; set; }
    public bool GitPush { get; set; }
    public bool CreatePullRequest { get; set; }
    public bool TriggerCi { get; set; }
    public bool Deploy { get; set; }
    /// <summary>Allow creating a new local branch for a still-reproducible bug (never checkout/commit/push).</summary>
    public bool CreateLocalBranch { get; set; } = true;

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
        if (CreateLocalBranch) flags |= Permissions.AgentPermission.CreateLocalBranch;
        return flags;
    }
}

/// <summary>LLM-guided reproduction: used when an approved LLM is configured; otherwise the rule-based steps run.</summary>
public sealed class LlmReproductionOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Upper bound of browser actions per investigation (each action is one LLM call).</summary>
    public int MaxActions { get; set; } = 30;
}

/// <summary>How the deployed KMS version maps to a source branch, and how investigation branches are named.</summary>
public sealed class SourceVersioningOptions
{
    /// <summary>
    /// Candidate branch names tried in order; the first that EXISTS in the local clone is used. Placeholders:
    /// {version} (e.g. 12.28.4), {major}.{minor} (12.28), {major}.{minor}.{patch}.
    /// </summary>
    public List<string> BranchPatterns { get; set; } = new()
    {
        // kms-suite convention (observed 2026-11): origin/release/Release-12.28.4, origin/release/release-v14.1.0
        "origin/release/Release-{version}", "origin/release/release-v{version}",
        "origin/release/Release-{major}.{minor}.{patch}", "origin/release/release-v{major}.{minor}.{patch}",
        "origin/release/Release-{major}.{minor}.0", "origin/release/release-v{major}.{minor}.0", "origin/release/Release-{major}.{minor}",
        "release/Release-{version}", "release/release-v{version}",
        "origin/release/{version}", "origin/release/{major}.{minor}.{patch}", "origin/release/{major}.{minor}",
        "origin/releases/{version}", "origin/releases/{major}.{minor}",
        "release/{version}", "release/{major}.{minor}.{patch}", "release/{major}.{minor}"
    };
    /// <summary>Name of the local branch created for a still-reproducible bug. {IssueKey} is required.</summary>
    public string NewBranchTemplate { get; set; } = "bugfix/{IssueKey}";
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
