# Bug Investigation & Fix Agent

Standalone .NET 8 ASP.NET Core **Blazor Server** application for investigating and fixing bugs from Jira tickets.

This tool is **product-agnostic**. It does not hard-code systems, environments, URLs, usernames, roles, repositories, or credentials. Each run builds context from:

1. The Jira ticket  
2. Runtime user input for **only missing** fields  
3. Secure credential profiles  
4. The workspace path you provide (and any rules/docs found there)

## Workflow

**Enter Jira Ticket → Analyze → Resolve Context → Reproduce → Investigate → Fix → Validate → Re-test**

Final successful state: **LOCAL CHANGES VALIDATED → READY FOR HUMAN REVIEW**

V1 does **not** commit, push, create PRs, trigger CI, or deploy (permissions off by default).

## Architecture

| Concept | Role |
| --- | --- |
| **Rules** | Explicit engineering instructions (app safety + workspace `.cursor/rules`, `AGENTS.md`, etc.) |
| **Skills** | Capability actions (Jira, browser, code, validation, …) gated by permissions |
| **Memory** | Human-validated engineering lessons only |
| **Runtime context** | Per-ticket facts (never secrets) |
| **Credentials** | Encrypted vault; never sent to LLM/logs/reports |
| **IAgentRuntime** | Provider-agnostic AI adapter |

### Projects

```
bug-investigation-agent/
  src/BugInvestigationAgent.App/             Blazor UI host
  src/BugInvestigationAgent.Core/            Orchestrator, context, permissions
  src/BugInvestigationAgent.Skills/          Skill implementations
  src/BugInvestigationAgent.Providers/       OpenAI-compatible, Anthropic, stubs
  src/BugInvestigationAgent.Infrastructure/  SQLite + Data Protection vault
  tests/                                     NUnit tests
```

## Run

```powershell
cd bug-investigation-agent
dotnet run --project src/BugInvestigationAgent.App
```

Open the printed HTTPS/HTTP URL (see `launchSettings.json`).

### Configure (user secrets / env — not source control)

```powershell
cd src/BugInvestigationAgent.App
dotnet user-secrets init
dotnet user-secrets set "Jira:BaseUrl" "https://your-org.atlassian.net"
dotnet user-secrets set "Jira:Email" "you@example.com"
dotnet user-secrets set "Jira:ApiToken" "<token>"
dotnet user-secrets set "LlmProviders:OpenAiCompatible:ApiKey" "<key>"
dotnet user-secrets set "Agent:ActiveProviderId" "openai-compatible"
```

Or set Anthropic:

```powershell
dotnet user-secrets set "LlmProviders:Anthropic:ApiKey" "<key>"
dotnet user-secrets set "Agent:ActiveProviderId" "anthropic"
```

Default `Agent:ActiveProviderId` is `null` (offline placeholder) so the UI pipeline can be exercised without an API key.

### Playwright browsers (reproduction)

```powershell
pwsh src/BugInvestigationAgent.App/bin/Debug/net8.0/playwright.ps1 install chromium
```

### Validation allowlist

Edit `Agent:ValidationCommandAllowlist` in `appsettings.json`. Only listed commands (exact or with extra args) may run.

## Provider adapters

| Id | Status |
| --- | --- |
| `openai-compatible` | V1 implemented |
| `anthropic` | V1 implemented |
| `azure-openai` | Stub (DI-ready) |
| `cursor` | Stub (DI-ready) |
| `null` | Offline placeholder |

Swap providers via `Agent:ActiveProviderId` without changing Core, Skills, Memory, Credentials, or UI.

## Security notes

- Passwords/tokens are stored via ASP.NET Data Protection under `App_Data/`.  
- `ISecretRedactor` strips secrets from logs, LLM prompts, and reports.  
- Credential profile matching uses system + environment + username/role from Jira when available.

## Tests

```powershell
dotnet test BugInvestigationAgent.sln
```
