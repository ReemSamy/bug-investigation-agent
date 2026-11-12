# AI Bug Investigation Tool (.NET 10)

A small local tool that investigates a **Jira bug** or reviews an **Azure DevOps pull request** with Claude, Playwright,
Jira REST v3 and the Azure DevOps REST API. You can use it in two ways, and both call the same engine:

| Mode | How |
|---|---|
| **A. Claude Code slash command** | `/investigate-bug <url>`, which runs the CLI in the same app |
| **B. Web UI (Razor Pages)** | `dotnet run --project src/AI.BugInvestigationTool --launch-profile http`, then open http://localhost:5180 |

> This repository also contains the earlier, larger `BugInvestigationAgent.*` projects (`BugInvestigationAgent.sln`).
> They are left unchanged. The lightweight tool is `AI.BugInvestigationTool.sln`: one app project and one test project.

## Requirements

- .NET 10 SDK (`dotnet --list-sdks` should show `10.0.x`)
- Chromium for Playwright. The tool uses Microsoft.Playwright 1.49.0, which needs Chromium build 1148. If it is not
  installed yet, build once and then run:
  ```powershell
  pwsh src/AI.BugInvestigationTool/bin/Debug/net10.0/playwright.ps1 install chromium
  ```
- For real investigations you need a Claude API key, a Jira Cloud API token, and an Azure DevOps PAT.

## Build, test, run

```bash
dotnet build AI.BugInvestigationTool.sln
```

```bash
dotnet test AI.BugInvestigationTool.sln
```

```bash
dotnet run --project src/AI.BugInvestigationTool --launch-profile http
```

```bash
dotnet run --project src/AI.BugInvestigationTool --no-launch-profile -- investigate https://your-site.atlassian.net/browse/KEY-123
```

The tests need no credentials and no network. They use local fixtures, plus a local fixture web app that the real
Playwright browser drives. The real-service tests are opt-in (see [Integration mode](#integration-mode)).

## Configuration

Non-secret settings live in `src/AI.BugInvestigationTool/appsettings.json` under `Tool`:

| Setting | Default | Meaning |
|---|---|---|
| `ClaudeModel` | `claude-opus-5-5` | Claude model ID |
| `ClaudeEffort` | `high` | `low` / `medium` / `high` / `xhigh` / `max` |
| `AllowedEnvironmentHosts` | `[]` | Hosts the browser may open (`qa.example.com`, `*.staging.example.com`). **Browser testing is refused until this is set.** |
| `AllowedJiraHosts` | `["*.atlassian.net"]` | Hosts that may receive the Jira token |
| `LocalRepositoryPath` | `null` | Local clone used to run existing tests in the PR workflow |
| `EvidenceRoot` | `Evidence` | Investigation folders (JSON, report.md, screenshots). Relative paths resolve to the solution folder |
| `BrowserHeadless`, `MaxBrowserActions`, `ScreenshotRedactSelectors`, `MaxToolTurns`, … | | See `Configuration/ToolOptions.cs` |

Machine-local overrides go in `src/AI.BugInvestigationTool/appsettings.Local.json`. Git ignores this file. See
`appsettings.Local.example.json` for an example.

### Credentials

The tool never hardcodes credentials, and never writes them to investigation files, logs, URLs or reports.

- **Web UI:** enter them on the **Credentials & configuration** page. They stay in server memory only and are cleared
  when the app restarts. Secret fields are never sent back to the browser.
- **CLI / slash command:** set environment variables, or use `dotnet user-secrets` (stored outside the repository):

| Purpose | Environment variable | user-secrets key |
|---|---|---|
| Claude API key | `ANTHROPIC_API_KEY` | `Credentials:ClaudeApiKey` |
| Jira base URL / email / API token | `BIT_JIRA_BASE_URL`, `BIT_JIRA_EMAIL`, `BIT_JIRA_API_TOKEN` | `Credentials:JiraBaseUrl`, `…:JiraEmail`, `…:JiraApiToken` |
| Azure DevOps org / project / PAT | `BIT_ADO_ORGANIZATION`, `BIT_ADO_PROJECT`, `BIT_ADO_PAT` | `Credentials:AdoOrganization`, `…:AdoProject`, `…:AdoPat` |
| Test environment URL / user / password | `BIT_ENV_URL`, `BIT_ENV_USERNAME`, `BIT_ENV_PASSWORD` | `Credentials:EnvironmentUrl`, `…:EnvironmentUsername`, `…:EnvironmentPassword` |
| Extra allowed environment hosts | `BIT_ALLOWED_HOSTS` (comma separated) | `Credentials:AllowedEnvironmentHosts` |

```bash
dotnet user-secrets --project src/AI.BugInvestigationTool set "Credentials:ClaudeApiKey" "<key>"
```

Use the smallest permissions that work:
- **Jira:** browse projects and add comments. Transition and attachment permissions are needed only if you approve those actions.
- **Azure DevOps PAT:** *Code (Read)*. Use *Code (Read & write)* only if you want the tool to post PR comments.
- **Test environment:** a dedicated QA account, not a personal or production account.

## Slash command (Mode A)

The command is defined in `.claude/commands/investigate-bug.md`. Open Claude Code at the repository root and run:

```
/investigate-bug https://your-site.atlassian.net/browse/KEY-123
/investigate-bug https://dev.azure.com/EHSQ/VisiumKMS/_git/kms-suite/pullrequest/3140
```

The command runs `dotnet run --project src/AI.BugInvestigationTool --no-launch-profile -- investigate <url>` and
presents the report. If information is missing (exit code 2), it asks you and then runs `resume <id> --info key=value`.
It runs `publish-jira` or `publish-pr` only after you approve in the chat.

CLI reference (`… -- help`):

| Command | Purpose |
|---|---|
| `investigate <url> [--info k=v]… [--json]` | Run an investigation |
| `resume <id> [--info k=v]…` | Continue after supplying missing information |
| `show <id> [--json]`, `list` | Read saved investigations |
| `transitions <id>` | List the Jira transitions available for the ticket (read-only) |
| `publish-jira <id> [--comment] [--attach-screenshots] [--transition "<name>"]` | **Approval required.** Posts the comment and/or changes the status |
| `publish-pr <id>` | **Approval required.** Posts one summary comment thread on the PR |

Exit codes: `0` completed, `2` needs input, `1` failed, `130` cancelled.

## Web UI (Mode B)

1. Run `dotnet run --project src/AI.BugInvestigationTool --launch-profile http` and open http://localhost:5180.
2. On **Credentials & configuration**, enter the Jira, Azure DevOps, Claude and environment values and the allowed hosts.
3. On the **Dashboard**, paste the URL and click **Start Investigation**.
4. Watch the steps progress live. If information is missing, a form asks for it; browser testing does not start until you answer.
5. Review the collected Jira data, the findings, code references, screenshots, console errors and network failures.
6. Edit the drafted Jira comment if needed, choose an optional transition, tick the approval box, and click **Publish**.

## What it does

**Jira bug workflow:**
1. Loads the ticket: all fields, custom fields resolved by display name, paginated comments and changelog, links,
   subtasks, sprint, and text and image attachments.
2. Claude reads the ticket. Missing information is detected by fixed rules: environment URL on an allowed host, login,
   expected behaviour, reproduction steps. If anything required is missing, the run stops and asks.
3. Opens the environment in an isolated Playwright context, signs in (Claude never sees the password), and Claude
   follows the reproduction steps through validated browser tools. Screenshots are taken with password fields masked.
   Console errors and failed requests are recorded.
4. Claude searches and reads the related code in Azure DevOps (read-only code search and file retrieval).
5. Claude correlates the ticket, browser evidence and code into a structured report. Evidence rules then check the
   proposed outcome. For example, a bug is never marked RESOLVED just because it did not reproduce.
6. Drafts a Jira comment. Publishing the comment, screenshots or a status change requires your approval.

**PR workflow:** loads the PR, the iterations, the changed files with computed diffs (real line numbers) and linked work
items. It runs related existing tests if `LocalRepositoryPath` is configured. Claude then reviews the diff (severity
Critical, Medium or Low, with file and line). The tool checks the line references against the diff, gives an
advisory verdict, and posts a comment only on approval. It never votes, approves, completes or merges a PR.

## Integration mode

Real-service tests are skipped unless you set `BIT_INTEGRATION=1` and the credentials:

```powershell
$env:BIT_INTEGRATION="1"; $env:ANTHROPIC_API_KEY="..."; $env:BIT_JIRA_EMAIL="..."; $env:BIT_JIRA_API_TOKEN="..."
$env:BIT_TEST_JIRA_URL="https://your-site.atlassian.net/browse/KEY-1"; $env:BIT_ADO_PAT="..."
$env:BIT_TEST_PR_URL="https://dev.azure.com/org/project/_git/repo/pullrequest/1"
dotnet test AI.BugInvestigationTool.sln --filter "FullyQualifiedName~RealServiceIntegrationTests"
```

These tests only read from Jira and Azure DevOps.

## Documentation

- [docs/ai-bug-investigation-tool-architecture.md](docs/ai-bug-investigation-tool-architecture.md): architecture, security model, implemented features and known limitations.
