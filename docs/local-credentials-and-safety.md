# Local app: credentials, LLM approval and read-only safety

## Where credentials live
| Secret | Entered in | Stored |
|---|---|---|
| Jira API token | Sign in / Account → *API token* | `App_Data/local-settings.json`, encrypted (`dp1:` prefix) |
| Anthropic / OpenAI-compatible API key | Sign in / Account → *AI provider* | same file, encrypted |
| Environment logins (KMSQA, …) | Workflow page, "remember" ticked | local database, encrypted per user + environment |

* Encryption: ASP.NET Data Protection. The key ring is in `App_Data/keys` and is itself protected with the
  **Windows user's DPAPI key**, so only your Windows account on this machine can decrypt it.
* Older plaintext values are encrypted automatically the first time the app starts after the upgrade.
* Saved secrets are never sent back to the browser: the page only shows "(saved)". To replace one, paste a new value.
* If the key ring is lost (or another Windows account opens the folder) the page shows which secrets could not be
  decrypted; enter them again.
* Never put tokens or keys in `appsettings*.json`, environment files, chat messages or Git.

## LLM: company approval gate
Selecting a provider and saving its key is **not** enough. Until the checkbox *"The company has approved sending Jira
ticket content and source-code excerpts to the selected provider"* is ticked and saved, investigations run without the
LLM and nothing is sent to it. The approval is recorded with your name and the time.

*Test LLM connection* sends only the fixed text "Reply with the single word OK." — no ticket or code data.

## Read-only rollout
Code modification, validation commands (build/test in the repository), Git commit/push, pull requests, CI and
deployment are removed **at runtime** in every host, whatever the configuration says (`ReadOnlyRollout`). The code
modification and validation skills also refuse to run without the permission if called directly. The agent only reads
the source workspace.

## Integration check
Sign in / Account → *Check integrations* runs read-only checks: source workspace (read-only Git commands), Azure DevOps
mapping, KMS documentation folder, environments and LLM settings.

## Version-aware investigation (legacy bugs)
* **Jira is read-only.** The tool never posts comments, drafts or reports and never changes any Jira field. The suggested
  comment in each record is local text a person may copy.
* **Deployed KMS version** is read after sign-in. Configure it per environment in `appsettings.Local.json`:
  ```json
  "EnvironmentRegistry": { "Environments": { "KMSQA": { "BaseUrl": "https://…", "Type": "QA",
    "Metadata": { "VersionPath": "/About", "VersionSelector": "#version", "VersionPattern": "(\\d+\\.\\d+\\.\\d+)" } } } }
  ```
  All three are optional; without them the signed-in page is searched for text like "Version 12.28.4". When no version is
  found, code is not analysed and no branch is created.
* **Source branch** = the first of `Agent:SourceVersioning:BranchPatterns` that exists in the local clone
  (default `origin/release/{version}`, `origin/release/{major}.{minor}`, …). Remote branches are used as of your last
  `git fetch` — the tool never fetches.
* **Code is read only from that branch** (`git grep` / `git log` on the ref). Your working folder and its checked-out
  branch are never touched or scanned.
* **The only write anywhere:** for a bug reproduced with screenshot evidence, one new local branch
  `Agent:SourceVersioning:NewBranchTemplate` (default `bugfix/{IssueKey}`) is created from the version branch with
  `git branch --no-track`. No checkout, file change, commit or push; an existing branch is never moved. All git commands go
  through an allowlist (read commands + creating a new branch).
* **Confirmed Fixed** requires both: every reproduction step ran automatically without showing the defect, and a commit in
  the version branch mentions the ticket or a resolved linked issue. Anything less is Possibly Fixed or Inconclusive.
