# AI Bug Investigation Agent — Jira Cloud team integration

Status: **implemented and tested against simulated Jira, browser and Azure DevOps. Not yet deployed.**
Do not announce Jira availability to the team until the pilot checklist at the end has been completed on the real
Jira site.

## 1. Architecture

```
Jira Cloud (Rovo)                         Azure (Container Apps environment)
┌──────────────────────────┐   FIT +      ┌────────────────────┐   Service Bus   ┌──────────────────────────────┐
│ Assign / @mention / chat │  user+app    │ bia-api            │ ─────────────▶ │ bia-worker (Playwright, git) │
│ rovo:agentConnector (A2A)│ ───tokens──▶ │ /a2a/json-rpc      │                 │ InvestigationJobRunner       │
│ GetTask polling          │              │ /atlassian/installed│◀── PostgreSQL ─▶│ AgentOrchestrator + skills   │
│ Personal settings page   │──invokeRemote▶│ /forge/credentials │   (jobs, audit, │ Azure DevOps (read-only)     │
│ (Custom UI) ─direct POST─┼─────────────▶ │ /api/jobs/{id}/…   │    secrets)     │ Blob evidence, Jira report   │
└──────────────────────────┘              └────────────────────┘                 └──────────────────────────────┘
                                         Key Vault · Blob Storage · App Insights · one managed identity
```

* **Forge app** (`forge/`): `rovo:agentConnector` (A2A 1.0 JSON-RPC, non-streaming) → Forge Remote endpoint on the
  API. A `jira:personalSettingsPage` Custom UI is the secure input page for credentials and MFA codes.
* **API** (`src/BugInvestigationAgent.Api`): validates every Forge Invocation Token (FIT), creates durable jobs,
  answers `GetTask` polls, handles follow-up replies and cancellation, and runs the credential channel.
* **Worker** (`src/BugInvestigationAgent.Worker`): leases jobs and runs the existing pipeline (ticket analysis, history
  search, KMS docs, environment selection, Playwright sign-in, reproduction, code investigation, report). It runs
  several jobs at once, each with its own session, browser context, credentials and identity.
* **Standalone Blazor app** (`src/BugInvestigationAgent.App`): unchanged single-user admin and fallback UI (local only).

## 2. Jira invocation and authorization flow

1. A developer assigns the agent to a bug, @mentions it, or asks in Rovo chat. Jira calls `SendMessage` on
   `/a2a/json-rpc` with a FIT in `Authorization`, plus `x-forge-oauth-user` and `x-forge-oauth-system`.
2. The FIT is validated: RS256 signature against Atlassian's JWKS (cached, refreshed on key rotation), issuer
   `forge/invocation-token`, audience `ari:cloud:ecosystem::app/{Forge:AppId}`, lifetime, and an optional cloud-id
   allow-list. Tenant = `app.installationId`; site = `context.cloudId` or the recorded installation.
3. The user is identified by calling `/rest/api/3/myself` **with the user's own token**. The result must match the FIT
   principal and the body's `userAccountId`, or the request is rejected.
4. The issue is read **with the user's token**. HTTP 403/404 means the task is rejected and no job is created, so Jira
   enforces the user's permissions.
5. A job is stored (status `Queued`) and both tokens are kept encrypted. The user token is used only for that user's
   reads (issue, comments, attachments, history search). It expires at 3.5h (Forge's max TTL is 4h) and is refreshed
   on every poll by the same user.
6. The app (system) token is used **only** to publish the report comment and the masked screenshots. That is the
   app-owned operation the admin consents to through `write:jira-work`.

User isolation: `GetTask`/`CancelTask`/follow-ups require same installation **and** same account (when Jira identifies
the caller); conversations are looked up by (installation, contextId, accountId).

## 3. Job lifecycle

`Queued → Running ⇄ Awaiting User Input → Completed | Failed | Cancelled | Timed Out`

| A2A state (Jira) | Job status |
|---|---|
| `TASK_STATE_SUBMITTED` | Queued |
| `TASK_STATE_WORKING` | Running |
| `TASK_STATE_INPUT_REQUIRED` | Awaiting User Input (environment, credentials, MFA code / approval) |
| `TASK_STATE_COMPLETED` | Completed (markdown report in the task, structured comment on the issue) |
| `TASK_STATE_FAILED` | Failed / Timed Out |
| `TASK_STATE_CANCELED` | Cancelled (or cancel requested) |

* **Environment:** the agent lists the registered environments (KMSRC, KMSQA, …) and suggests one from the ticket.
  The user replies with a name in chat.
* **Credentials / MFA:** the agent links to the personal settings page. That page gets one-time tickets through a
  FIT-authenticated call: SHA-256 hash only, 10 minutes, single use, bound to user, job and environment. The browser
  then posts the secret **directly** to `/api/jobs/{id}/credentials` or `/mfa-code` (CORS allows only the Forge CDN).
  Passwords never pass through Rovo chat or Forge functions. Chat messages that look like passwords are refused and
  not stored.
* **Durability:** workers hold a renewable DB lease (atomic conditional UPDATE). If a worker dies, the recovery
  sweeper re-queues the job (`RetryCount`, max 2). Job timeout is 55 min (Jira's work-item trigger allows 60).
  Awaiting-input timeout is 30 min.
* **Reports:** `InvestigationReport` separates **confirmed findings** (backed by evidence) from **hypotheses**
  (root cause from code analysis is always a hypothesis unless a fix and retest proved it). It is published once.
  The comment carries the property `bug-investigation-agent-report = {investigationId}`, and retries look for it
  first, so a retry never posts a duplicate. Screenshots are attached only for non-Production environments.

## 4. Database schema (EF Core; SQLite locally, PostgreSQL hosted)

| Table | Purpose |
|---|---|
| `InvestigationJobs` | Id, TenantId (installation), CloudId, SiteUrl, ApiBaseUrl, IssueId, IssueKey, InvokingAccountId, InvocationType, ContextId, ForgeEnvironmentId, ApplicationId, EnvironmentId, AllowStateChangingActions, Status, StatusMessage, AwaitingInputKind/Prompt, PendingUserReply, CancelRequested, Created/Started/Completed/UpdatedAt, NotBeforeUnixMs, LeaseOwner, LeaseExpiresUnixMs, RetryCount, MaxRetries, ReproductionOutcome, ResultSummary, ReportJson, EvidenceReference, ErrorDetails, ReportCommentId, ReportPublishedAt, Version (concurrency) |
| `InvestigationJobEvents` | Redacted progress events per job |
| `JobSecrets` | (JobId, Kind) → Data Protection–encrypted value + expiry (user/app tokens, env credentials, MFA code, ticket hash) |
| `AuditEntries` | investigation created / cancelled / completed / failed, environment selected, credentials submitted / saved, authentication attempts, report published, access denied, app installed (redacted, no secrets) |
| `JiraInstallations` | installationId → cloudId, site URL, installer |
| `CredentialProfiles` | opt-in saved credentials, owner `atlassian:{cloudId}:{accountId}` × environment |

There are no EF migrations: `EnsureCreated` plus `SchemaUpgrader` add new tables and columns idempotently on SQLite and
PostgreSQL. Switch to EF migrations before the schema changes again.

## 5. Azure DevOps (read-only)

* `ISourceRepositoryProvider` → `AzureDevOpsRepositoryProvider`, using an Entra token for the Azure DevOps resource
  (`499b84ac-…/.default`) from the managed identity. No personal access tokens.
* Mapping: `Repositories:Mappings` (Jira project → optional component → application → org/project/repo/branch; most
  specific match wins).
* Shallow `git clone --depth 1` into a per-job folder, deleted after the job. The token goes in through
  `GIT_CONFIG_*` environment variables, so it is not in process arguments or `.git/config`.
* Correlation: commits on the branch mentioning the Jira key, plus Code Search hits for the ticket's keywords.
* No push, branch or PR operations exist. The worker runs with `ModifyCode`, `RunValidation`, `GitCommit`, `GitPush`,
  `CreatePullRequest`, `TriggerCi` and `Deploy` all off; the fix proposal is reported for human review.

## 6. Administrator setup (required before the pilot)

### Azure (subscription owner), deploy only with approval
1. Create a resource group, fill `infra/main.parameters.json`, then run
   `az deployment group create -g <rg> -f infra/main.bicep -p infra/main.parameters.json`.
2. Build and push images:
   `az acr build -r <acr> -t bia-api:latest -f deploy/docker/Dockerfile.api .` and the same with `Dockerfile.worker`.
3. PostgreSQL: as a member of the Entra admin group, create the identity's database role:
   `SELECT * FROM pgaadauth_create_principal('<managedIdentityName>', false, false);` then
   `GRANT CONNECT ON DATABASE bugagent ...; GRANT CREATE, USAGE ON SCHEMA public ...` (least privilege).
4. Key Vault secrets (names map to configuration keys): `LlmProviders--Anthropic--ApiKey` (or the OpenAI key) and
   `Agent--ActiveProviderId`. Environment URLs are plain configuration, not secrets.
5. Hardening for production: VNet-integrate the Container Apps environment, use private endpoints for PostgreSQL,
   Key Vault, Storage and Service Bus, and remove the `AllowAzureServices` firewall rule.

### Azure DevOps (organization admin)
* Add the managed identity as a user (Basic or Stakeholder + Code access). Grant it **Reader** on the mapped projects
  and repositories only.

### Jira / Forge (Jira admin + app owner)
1. `cd forge && npm install && npm run build` (builds the settings page).
2. `forge register`, then put the app id and API host into `manifest.yml` (`REPLACE_WITH_…`).
3. `forge deploy -e production`, then `forge install --site <site>.atlassian.net --product jira -e production`. The
   admin consents to the scopes: `read:jira-work`, `read:jira-user`, `write:jira-work`, and app system/user tokens.
4. Set `Forge:AppId`, `Forge:AllowedCloudIds` and `Forge:EnvironmentId` on the API and worker.
5. Non-production Forge environments only work for app contributors (Atlassian limitation). Pilot users must be added
   as contributors, or use the production environment.

## 7. Team onboarding (once deployed)

1. Open a bug in Jira → assign it to **Bug Investigation Agent**, @mention it, or open Rovo chat and say
   “Investigate ABC-123”.
2. Reply with the environment (e.g. `KMSQA`).
3. When asked, open the linked **personal settings page** (Jira → Personal settings → Apps → Bug Investigation
   Agent), enter that environment's username and password (optionally “remember”), then reply `continue`.
   Never type passwords in the chat.
4. If the environment asks for MFA, enter the code on the same page, or approve on your device and reply `approved`.
5. Follow progress in the agent task. The final report appears in the task and as a comment on the issue.
6. `cancel` stops your investigation; `rerun` starts a new one in the same conversation.

## 8. Implemented vs. needs external setup

| Capability | State |
|---|---|
| FIT validation (JWKS, iss, aud, exp, cloud allow-list) | Implemented, unit-tested; rejects forged tokens against Atlassian's real JWKS (smoke test) |
| A2A SendMessage / GetTask / CancelTask, follow-ups, rerun | Implemented, integration-tested with simulated Jira |
| User-token reads, permission enforcement, identity cross-checks | Implemented, tested (403/404 → rejected; spoofed ids rejected) |
| Durable jobs, leases, retries, timeouts, cancellation, audit | Implemented, tested (SQLite; same EF code on PostgreSQL) |
| Concurrent multi-user execution, per-user/env credential isolation | Implemented, end-to-end tested (2 users concurrently) |
| Secure credential/MFA channel (tickets, CORS) | Backend implemented and tested; **Custom UI page not built/linted (no Node.js on the dev machine)** |
| Structured report + idempotent Jira comment + screenshots | Implemented, tested against simulated Jira |
| Azure DevOps provider (MI token, clone, search, commits) | Implemented, tested with simulated Azure DevOps; **needs real org/repo access** |
| Service Bus queue, Blob evidence, Key Vault, PostgreSQL MI auth, App Insights | Implemented; **only exercisable in Azure** (local runs use DB queue, local files, SQLite) |
| Bicep / Dockerfiles | Written; **not validated or deployed** (no Azure CLI/Docker here, and no authorization to deploy) |
| Forge app (manifest, agent connector, settings page) | Written against current Atlassian docs; **not registered, linted or deployed** |
| Playwright sign-in on KMSRC/KMSQA | Implemented earlier; **needs real URLs + test accounts** |

## 9. Pilot acceptance checklist (on the real site — not yet done)

1. Install the Forge app and verify the `installed` event reaches the API (audit `app.installed`).
2. Developer A assigns the agent to a bug, picks KMSRC, signs in, and gets a report comment on the issue.
3. Developer B starts another investigation at the same time; both complete independently.
4. A user without access to a restricted project invokes the agent on that project's issue and is rejected.
5. Retry and cancel paths; no duplicate comments after a worker restart (kill a worker mid-run).
6. Review App Insights and the audit table: no tokens or passwords anywhere.
