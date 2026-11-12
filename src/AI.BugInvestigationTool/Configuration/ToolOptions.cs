namespace AI.BugInvestigationTool.Configuration;

/// <summary>Non-secret settings, bound from the "Tool" section of appsettings.json.</summary>
public sealed class ToolOptions
{
    public const string Section = "Tool";

    /// <summary>Folder for investigation JSON, reports and screenshots. Relative paths resolve against the current directory.</summary>
    public string EvidenceRoot { get; set; } = "Evidence";

    public string ClaudeModel { get; set; } = "claude-opus-5-5";
    /// <summary>low | medium | high | xhigh | max</summary>
    public string ClaudeEffort { get; set; } = "high";
    public int ClaudeMaxTokens { get; set; } = 16000;
    public string ClaudeBaseUrl { get; set; } = "https://api.anthropic.com/";
    public int ClaudeTimeoutSeconds { get; set; } = 300;
    /// <summary>Upper bound on tool-use turns in one Claude conversation.</summary>
    public int MaxToolTurns { get; set; } = 25;

    /// <summary>Hosts (exact, or "*.example.com") that the browser may navigate to. Empty = environment URL host only.</summary>
    public List<string> AllowedEnvironmentHosts { get; set; } = [];
    /// <summary>Hosts that may serve the Jira site. "*.atlassian.net" covers Jira Cloud.</summary>
    public List<string> AllowedJiraHosts { get; set; } = ["*.atlassian.net"];

    public bool BrowserHeadless { get; set; } = true;
    public int BrowserTimeoutSeconds { get; set; } = 30;
    public int MaxBrowserActions { get; set; } = 30;
    /// <summary>Extra CSS selectors masked in every screenshot (password fields are always masked).</summary>
    public List<string> ScreenshotRedactSelectors { get; set; } = [];

    public int MaxAttachmentBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxAttachments { get; set; } = 10;
    public int MaxPullRequestFiles { get; set; } = 60;
    public int MaxDiffFileLines { get; set; } = 4000;

    /// <summary>Optional local clone used to run existing tests in the PR workflow. Tests never run anywhere else.</summary>
    public string? LocalRepositoryPath { get; set; }
    public int TestTimeoutSeconds { get; set; } = 600;
}

/// <summary>
/// Credentials for one investigation. Held in memory only; never serialized into investigation files,
/// logs, URLs or reports. <see cref="Services.SecretRedactor"/> scrubs any accidental echo.
/// </summary>
public sealed class Credentials
{
    public string? JiraBaseUrl { get; set; }
    public string? JiraEmail { get; set; }
    public string? JiraApiToken { get; set; }
    public string? AdoOrganization { get; set; }
    public string? AdoProject { get; set; }
    public string? AdoPat { get; set; }
    public string? ClaudeApiKey { get; set; }
    public string? EnvironmentUrl { get; set; }
    public string? EnvironmentUsername { get; set; }
    public string? EnvironmentPassword { get; set; }
    /// <summary>Extra allowed environment hosts entered at runtime (merged with Tool:AllowedEnvironmentHosts). Not a secret.</summary>
    public List<string> AllowedEnvironmentHosts { get; set; } = [];

    public bool HasJira => !string.IsNullOrWhiteSpace(JiraBaseUrl) && !string.IsNullOrWhiteSpace(JiraEmail) && !string.IsNullOrWhiteSpace(JiraApiToken);
    public bool HasAdo => !string.IsNullOrWhiteSpace(AdoOrganization) && !string.IsNullOrWhiteSpace(AdoPat);
    public bool HasClaude => !string.IsNullOrWhiteSpace(ClaudeApiKey);
    public bool HasEnvironmentLogin => !string.IsNullOrWhiteSpace(EnvironmentUsername) && !string.IsNullOrEmpty(EnvironmentPassword);

    public IEnumerable<string> SecretValues()
    {
        foreach (var s in new[] { JiraApiToken, AdoPat, ClaudeApiKey, EnvironmentPassword })
            if (!string.IsNullOrEmpty(s) && s.Length >= 4) yield return s;
    }

    public Credentials Clone()
    {
        var c = (Credentials)MemberwiseClone();
        c.AllowedEnvironmentHosts = [.. AllowedEnvironmentHosts];
        return c;
    }

    /// <summary>
    /// CLI source: environment variables, then the "Credentials" configuration section
    /// (user-secrets / appsettings.Local.json, both outside source control).
    /// </summary>
    public static Credentials FromConfiguration(IConfiguration config)
    {
        string? Get(string env, string key) =>
            System.Environment.GetEnvironmentVariable(env) is { Length: > 0 } v ? v : config[$"Credentials:{key}"];

        return new Credentials
        {
            JiraBaseUrl = Get("BIT_JIRA_BASE_URL", nameof(JiraBaseUrl)),
            JiraEmail = Get("BIT_JIRA_EMAIL", nameof(JiraEmail)),
            JiraApiToken = Get("BIT_JIRA_API_TOKEN", nameof(JiraApiToken)),
            AdoOrganization = Get("BIT_ADO_ORGANIZATION", nameof(AdoOrganization)),
            AdoProject = Get("BIT_ADO_PROJECT", nameof(AdoProject)),
            AdoPat = Get("BIT_ADO_PAT", nameof(AdoPat)),
            ClaudeApiKey = Get("ANTHROPIC_API_KEY", nameof(ClaudeApiKey)),
            EnvironmentUrl = Get("BIT_ENV_URL", nameof(EnvironmentUrl)),
            EnvironmentUsername = Get("BIT_ENV_USERNAME", nameof(EnvironmentUsername)),
            EnvironmentPassword = Get("BIT_ENV_PASSWORD", nameof(EnvironmentPassword)),
            AllowedEnvironmentHosts = (Get("BIT_ALLOWED_HOSTS", "AllowedEnvironmentHosts") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        };
    }
}
