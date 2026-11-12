# AI.BugInvestigationTool

This is the single application project. It is a Razor Pages web UI, and the same executable is also a CLI when the
first argument is a command (`investigate`, `resume`, `show`, `list`, `transitions`, `publish-jira`, `publish-pr`, `help`).

| Folder | Contents |
|---|---|
| `Pages/` | Dashboard, Credentials & configuration, Investigation (progress, context review, report, approvals) |
| `Services/Workflow/` | `InvestigationEngine` (the one workflow), missing-info rules, outcome rules, report/comment formatting, test runner, UI background runner |
| `Services/Jira/` | Jira REST v3 client + ADF conversion |
| `Services/AzureDevOps/` | Azure DevOps REST client + diff builder |
| `Services/Claude/` | Messages API client with a controlled tool loop, tool definitions/validation, prompts |
| `Services/Browser/` | Playwright session (isolated context, host allow-list, masked screenshots) |
| `Services/UrlParser.cs`, `Services/Security.cs` | URL parsing, host policy (SSRF), secret redaction, untrusted-content wrapping |
| `Storage/` | JSON-file persistence under `Evidence/<id>/` |
| `Cli.cs`, `Program.cs` | Entry points |

The repository root [README](../../README.md) covers setup and usage.
