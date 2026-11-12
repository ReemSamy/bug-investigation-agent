# AI Bug Investigation Tool: architecture

## Shape

```
Claude Code ── /investigate-bug ──► dotnet run … -- investigate <url> ─┐
                                                                        ├─► InvestigationEngine ─► JiraClient        (Jira REST v3)
Browser ─► Razor Pages ─► InvestigationRunner (background, in-memory) ─┘                       ├─► AzureDevOpsClient (ADO REST 7.1, code search)
                                                                                                ├─► ClaudeClient      (Messages API + tool loop)
                                                                                                ├─► IBrowserSession   (Playwright)
                                                                                                ├─► TestRunner        (dotnet test, fixed command)
                                                                                                └─► InvestigationStore (Evidence/<id>/*.json|md|png)
```

The solution has one ASP.NET Core project and one xUnit project. The CLI and the UI resolve the same
`InvestigationEngine` from the same DI container, and neither one contains workflow logic. Dependencies: Microsoft.Playwright
(app); xunit and Microsoft.NET.Test.Sdk (tests). Everything else is the .NET base library: HttpClient, System.Text.Json and DI.

There are two interfaces, `IBrowserLauncher` and `IBrowserSession`. They exist so the workflow can be tested with a fake
browser while separate tests drive the real Playwright implementation. The HTTP clients are concrete classes, tested
through a fake `HttpMessageHandler`.

## Workflows

**Jira bug** (`InvestigationStep`): JiraDataCollection → ContextValidation → (WaitingForInput) → BrowserInvestigation →
CodeInvestigation → ClaudeAnalysis → ReportGeneration → Completed → JiraPublishing (approval only).
Failures end in `Failed` and cancellation ends in `Cancelled`. After each step the state is written to `investigation.json`,
which the UI polls.

**Azure DevOps PR:** PullRequestCollection → TestExecution → ClaudeAnalysis → ReportGeneration → Completed. A PR
comment is posted only on approval.

## Claude integration

- There is one `ClaudeClient`, which calls `POST /v1/messages` through HttpClient. It uses `anthropic-version: 2026-10-21`, a
  configurable model (default `claude-opus-5-5`), `output_config.effort`, server-side refusal fallbacks on models that
  support them, and retries for 408/409/429/5xx/529.
- Every structured result arrives as a call to a "submit" tool, such as `submit_investigation_report`. Its input is
  validated against the tool's JSON schema (types, required fields, enums, ranges, no unknown properties). If the input
  is invalid, the errors go back to Claude to correct, up to 2 times.
- There are five prompts in `Prompts.cs`: ticket understanding, browser reproduction (investigation planning and
  execution), code investigation, final root-cause analysis, and PR review.
- Controlled tool use: Claude can request only named operations: `search_code`, `get_file`, `list_repositories`,
  `list_commits`, `search_jira`, `get_test_results` and the `browser_*` set. The tool loop rejects unknown tools and
  schema-invalid input before anything runs. Claude has no shell, file system, arbitrary URL or password access.
  `browser_login` fills the credentials on the server side.

## Security model

| Risk | Control |
|---|---|
| Secret leakage | Credentials are held in memory (UI) or read from environment variables / user-secrets (CLI). They are never serialized: `Investigation` contains none. `SecretRedactor` scrubs errors, progress, reports and comments. HttpClient logging is filtered. Secret form fields are never sent back to the browser. |
| Sending tokens to the wrong host | The Jira host must match `AllowedJiraHosts` and the configured base URL. The ADO organization must match the PAT's organization. Jira attachments are downloaded only from the Jira host. |
| SSRF / arbitrary navigation | The environment URL must be on an allowed host. A Playwright route blocks top-level navigation to other hosts (returns 204) and requests to private or loopback addresses unless allow-listed. `browser_navigate` validates the URL before Playwright sees it. Kestrel binds to `localhost`, and `AllowedHosts` is `localhost`. |
| Prompt injection | All external text (tickets, comments, attachments, PR text, diffs, source, web pages) is wrapped in `<untrusted_data>`, and the wrapper cannot be closed from inside. The system prompts mark it as data. The outcome and transition rules run in code, after Claude. |
| Unwanted writes | Jira comments, attachments and transitions, and PR comments, happen only through explicit approval paths: a UI checkbox or the CLI `publish-*` commands. Closing transitions are refused for INCONCLUSIVE and BLOCKED outcomes. There are no PR votes, completion or merges, and no source changes. |
| Screenshot exposure | Password inputs and `ScreenshotRedactSelectors` are masked before capture. Evidence is served only from inside the investigation folder, with a path-traversal check. |
| Cross-investigation contamination | Each run uses a new browser and a new isolated context: no shared cookies or storage, and downloads are disabled. |

## Outcome rules (`OutcomeClassifier`)

| Claude proposes | Accepted only when | Otherwise |
|---|---|---|
| RESOLVED | The browser ran, the login worked, the defect did **not** reproduce, the **expected behaviour was positively observed**, and confidence ≥ 0.7 | NOT REPRODUCIBLE / INCONCLUSIVE / UNRESOLVED |
| UNRESOLVED | The defect was reproduced | INCONCLUSIVE |
| NOT REPRODUCIBLE | The steps were completed and the defect was not seen | UNRESOLVED (if seen) / INCONCLUSIVE |
| any definitive outcome | The environment was actually tested | BLOCKED |

Root cause is "confirmed" only when a code reference that Claude actually opened supports it. Code references to files
that Claude never opened are marked unverified. PR findings outside the changed hunks (±3 lines) that were never opened
are marked ⚠ unverified.

## Implemented features

- Jira and ADO PR URL parsing, including the `selectedIssue=` and `visualstudio.com` forms.
- Full Jira extraction with pagination, custom fields by display name, sprint, links, subtasks, parent, history, and attachments (text and image).
- Missing information detection (fixed rules plus Claude's suggestions), with a pause-and-resume flow in both UI and CLI.
- Playwright login (one-step and two-step forms), validated browser tools, masked screenshots, and console and network capture.
- Azure DevOps code search, file retrieval with line numbers, commit history, and repository listing.
- PR metadata, Files-tab diffs computed with real line numbers, linked work items and Jira keys, a test run in a local clone, and a review with severity, line validation and an advisory verdict.
- Structured report: Markdown for the CLI and `report.md`, HTML for the UI, and an ADF Jira comment.
- Approval-gated Jira comments, screenshot attachments and transitions, and PR comment threads.
- Cancellation (UI button / Ctrl+C) and clear failure states.
- 98 automated tests, including real-Playwright tests and an end-to-end scenario against a local fixture site; 3 opt-in real-service tests.

## Known limitations

- Not verified against live Jira, Azure DevOps or Claude services during development: there were no credentials. The
  integration tests are ready to run once credentials are available.
- Jira Cloud only (REST v3 and ADF). Jira Server/Data Center would need v2 endpoints.
- Code search needs the Azure DevOps Code Search extension in the organization.
- PR test execution runs `dotnet test` on whatever is checked out in `LocalRepositoryPath`. The report states whether
  that matches the PR commit. The tool never checks out branches.
- Generic login automation covers standard username/password forms. SSO, MFA and CAPTCHA flows are not automated.
  Use `noLoginRequired` with a pre-authenticated environment, or provide selectors through `browser_login`.
- Screenshot redaction masks password fields and configured selectors only. Other sensitive data on screen needs
  `ScreenshotRedactSelectors`.
- Credentials entered in the UI are shared by everyone using that local instance. The app is designed for one developer on localhost.
- The UI loads Bootstrap 5 from the jsDelivr CDN.
