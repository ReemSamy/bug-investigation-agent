# Deployment & pilot runbook — AI Bug Investigation Agent (Jira team service)

> **Nothing in this document has been executed against company accounts.** No Forge app was registered, nothing was
> deployed to Azure, no Azure DevOps permissions were changed, no real Jira issue was touched. Every external step
> below needs the named administrator and explicit approval.

## 0. What was verified, and how

| Capability | How it was verified |
|---|---|
| FIT validation (signature/JWKS, issuer, audience, expiry, site allow-list) | Unit tests; real HTTP rejection of forged tokens against Atlassian's live JWKS (smoke test) |
| Verified caller on every task call (no task-id-only access) | API tests over the real ASP.NET pipeline (simulated Jira) |
| Durable jobs, versioned migrations, advisory-lock concurrency, leases, optimistic concurrency | **Real PostgreSQL 16.4** (local server) + SQLite WAL tests |
| Worker slot released while waiting; resume from checkpoint on any worker | Real worker service + DB queue with **1 slot** for 2 users (simulated externals) and **full stack with real API, real worker, real PostgreSQL, real Chromium** |
| MFA (code typed into a real MFA page), bounded MFA hold | Full-stack test against a real local login portal; lifecycle tests |
| Crash recovery (expired lease → re-queue → resume, no duplicate report) | Lifecycle tests (simulated externals) |
| Service Bus complete / abandon / dead-letter | **Simulated** (mocked Azure SDK receiver — the emulator needs Docker, unavailable) |
| Production read-only guard (network level) | **Real Chromium**: POST form and fetch DELETE never reach the server; GET still works; QA unaffected |
| Login success/failure/MFA detection with configured selectors; "needs setup" without selectors | Real Chromium + unit tests |
| Report structure, idempotent publication, attachments only for non-Production | Simulated Jira (HTTP handler) |
| Azure DevOps provider (MI token, shallow clone, code search, commits) | **Simulated** Azure DevOps HTTP + process runner |
| Bicep | `bicep build` + `bicep lint` (v0.31.92): **0 errors, 0 warnings**, 32 resources; **not deployed** |
| Forge manifest | Atlassian's `@forge/manifest` full validation (Forge CLI 15.0.0, offline): **0 errors, 0 warnings**. `forge lint` itself needs `forge login` → not run |
| Forge Custom UI | `npm install` + `npm run build` with Node 24.22.2: bundle built (39 KB) |
| Docker images | `dotnet publish -c Release` of API and worker succeed; **images not built** (no Docker on the dev machine) |

Not verified against real services (blocked): Jira Cloud site, Forge deployment/installation, Azure resources,
Azure DevOps organization, KMSRC/KMSQA, Service Bus, Key Vault, Blob Storage, App Insights.

## 1. Administrator approvals required

| Who | Approval / action |
|---|---|
| **Jira site admin** | Install the Forge app; consent to scopes `read:jira-work`, `read:jira-user`, `write:jira-work`, `read:app-system-token`, `read:app-user-token`; accept that the agent posts report comments + screenshots as the app user. |
| **Atlassian app owner** (developer with Forge access) | `forge register` / `deploy` / `install`; owns the app's Forge environments. Non-production Forge environments only work for app contributors — add pilot users as contributors or use the production environment. |
| **Azure subscription owner** | Create the resource group, deploy `infra/main.bicep` (creates RBAC assignments → needs `Owner` or `User Access Administrator`). |
| **Entra ID admin** | Create the PostgreSQL admin group; approve the managed identity. |
| **DBA (PostgreSQL Entra admin group member)** | Create the managed-identity database role and grants (step 3.4). |
| **Azure DevOps organization admin** | Add the managed identity as a user with **Reader** on the mapped repositories only. |
| **KMS environment owners** | Provide KMSRC/KMSQA base URLs, login selectors (success/error/MFA), and **test accounts** for pilot users. |
| **Security** | Review §6 risks; sign off on the pilot (public network mode) and on private networking before production. |

## 2. Placeholders to fill

| Where | Placeholder |
|---|---|
| `forge/manifest.yml` | `REPLACE_WITH_FORGE_APP_ID` (set by `forge register`), `REPLACE_WITH_API_HOST` (API Container App FQDN) |
| `infra/main.parameters.json` | `forgeAppId`, `forgeEnvironmentId`, `allowedJiraCloudIds`, `kmsrcBaseUrl`, `kmsqaBaseUrl`, `jiraProjectKey`, `adoOrganization`, `adoProject`, `adoRepository`, `adoBranch`, `postgresAdminGroupObjectId`, `postgresAdminGroupName`, `networkMode` |
| Key Vault secrets | `LlmProviders--Anthropic--ApiKey` (or `LlmProviders--OpenAiCompatible--ApiKey`), `Agent--ActiveProviderId` |
| Environment login selectors | `EnvironmentRegistry:Environments:KMSRC:Login:SuccessSelectors` (+ `ErrorSelectors`, `MfaSelectors`) and the same for KMSQA — set as Container App env vars (`EnvironmentRegistry__Environments__KMSRC__Login__SuccessSelectors__0=…`) or Key Vault secrets. **Without a success selector the agent reports "environment needs setup" instead of claiming a verified login.** |

Find the Jira cloud id: `https://<site>.atlassian.net/_edge/tenant_info`.

## 3. Deployment (do not run without approval)

### 3.1 Validate locally
```bash
bicep build infra/main.bicep && bicep lint infra/main.bicep
dotnet build BugInvestigationAgent.sln -c Release
dotnet test BugInvestigationAgent.sln
```
Optional real PostgreSQL integration tests: start PostgreSQL 16 locally and set
`BIA_IT_POSTGRES="Host=127.0.0.1;Port=5432;Username=postgres;Database=postgres"` before `dotnet test`
(without it, those tests are reported as skipped, never faked).

### 3.2 Azure infrastructure
```bash
az login
az account set --subscription <subscription-id>
az group create -n rg-bug-agent -l <region>
az deployment group what-if -g rg-bug-agent -f infra/main.bicep -p infra/main.parameters.json
az deployment group create  -g rg-bug-agent -f infra/main.bicep -p infra/main.parameters.json
```
Pilot: `networkMode=public`. Production: `networkMode=private` (VNet, private PostgreSQL, private endpoints for Key
Vault / Blob / Service Bus Premium; only the API ingress is public because Forge must reach it).

### 3.3 Images
```bash
ACR=$(az deployment group show -g rg-bug-agent -n main --query properties.outputs.registryLoginServer.value -o tsv)
az acr build -r ${ACR%%.*} -t bia-api:$(git rev-parse --short HEAD)    -f deploy/docker/Dockerfile.api .
az acr build -r ${ACR%%.*} -t bia-worker:$(git rev-parse --short HEAD) -f deploy/docker/Dockerfile.worker .
az containerapp update -g rg-bug-agent -n bia-api    --image $ACR/bia-api:<tag>
az containerapp update -g rg-bug-agent -n bia-worker --image $ACR/bia-worker:<tag>
```

### 3.4 Database role (DBA, once)
```sql
-- connected to database "bugagent" as a member of the PostgreSQL Entra admin group
SELECT * FROM pgaadauth_create_principal('<managedIdentityName>', false, false);
GRANT CONNECT ON DATABASE bugagent TO "<managedIdentityName>";
GRANT USAGE, CREATE ON SCHEMA public TO "<managedIdentityName>";
```
Migrations run automatically at startup (versioned, `__SchemaVersion`, advisory lock — safe with several replicas).

### 3.5 Secrets
```bash
az keyvault secret set --vault-name <kv> -n LlmProviders--Anthropic--ApiKey --value <key>
az keyvault secret set --vault-name <kv> -n Agent--ActiveProviderId --value anthropic
```

### 3.6 Verify the service (no Jira involved)
```bash
curl https://<api-fqdn>/healthz        # Healthy (liveness, no dependencies)
curl https://<api-fqdn>/readyz         # database (schema v3), key-ring, evidence-storage, queue → Healthy
curl -X POST https://<api-fqdn>/a2a/json-rpc -d '{}' -i   # must be 401 (no Forge token)
az containerapp logs show -g rg-bug-agent -n bia-worker --tail 50   # "Worker … started", /readyz browser Healthy
```

### 3.7 Azure DevOps (organization admin)
1. Organization settings → Users → Add the managed identity (by its client id / name), access level **Basic**.
2. Project → Permissions: add it to **Readers** of the mapped project(s) only (no Contributors, no build/release rights).
3. Code Search extension must be installed for search correlation (otherwise the agent continues without it).
4. Verify read-only access from a worker console session: `git ls-remote` must succeed; `git push` must fail.

### 3.8 Forge (app owner + Jira admin)
```bash
cd forge
npm install && npm run build                 # Node >= 22.23.4 or 24.22.1 for the Forge CLI
npm install -g @forge/cli
forge login                                  # Atlassian API token of the app owner
forge register                               # writes app.id into manifest.yml
# edit manifest.yml: REPLACE_WITH_API_HOST → <api-fqdn>
forge lint
forge deploy -e development
forge install --site <site>.atlassian.net --product jira -e development
forge environments list                      # note the environment id → Forge__EnvironmentId
```
After install, the API audit table must contain `app.installed` for the site.

### 3.9 Rollback
* **Application:** `az containerapp revision list -g rg-bug-agent -n bia-api -o table` then
  `az containerapp ingress traffic set -g rg-bug-agent -n bia-api --revision-weight <previous>=100`;
  worker: `az containerapp update -n bia-worker --image $ACR/bia-worker:<previous-tag>`.
  Migrations are additive (new tables/nullable columns only) — older images keep working on a newer schema.
* **Disable for users immediately:** `forge uninstall` (or remove the agent from the site in Jira admin).
  Running jobs then fail closed (FIT validation) and suspended jobs time out.
* **Infrastructure:** redeploy the previous template version; data stores are not deleted by a redeploy.

## 4. Pilot checklist

### 4.1 One developer
1. `/readyz` healthy on API and worker; `app.installed` audited.
2. Developer opens a **test** bug in a pilot project and assigns it to *Bug Investigation Agent* (or `@mentions` it).
3. Jira shows the task as submitted → the agent asks for the environment (input required). Reply `KMSQA`.
4. Follow the link to **Personal settings → Apps → Bug Investigation Agent**; enter the KMSQA **test** account; reply `continue`.
5. If the environment uses MFA: enter the code on the same page (or approve on the device and reply `approved`) within 5 minutes.
6. Task completes; the issue gets exactly one *AI Bug Investigation* comment (+ screenshots for non-Production).
7. Verify: audit trail (`investigation.created`, `environment.selected`, `credentials.submitted`, `authentication.attempt`,
   `report.published`); no password in App Insights logs (`traces | where message contains "<test password>"` → 0 rows).
8. Negative checks: a user **without** access to the issue's project invokes the agent → rejected; wrong password → re-prompt.

### 4.2 Two developers concurrently
1. Developer A investigates issue X on KMSRC, developer B issue Y on KMSQA, started within the same minute.
2. While A waits for input, B's investigation must keep progressing (worker slot released).
3. Each sees only their own task; each issue gets only its own report; screenshots are named `<ISSUE>_<ENV>_*.png`.
4. Developer B cancels; developer A's investigation is unaffected.
5. Kill a worker replica during an investigation (`az containerapp revision restart`) → the job resumes and posts **one** report.

## 5. Configuration reference (per service)

| Setting | API | Worker | Notes |
|---|---|---|---|
| `Forge__AppId`, `Forge__AllowedCloudIds__0` | ✔ | ✔ | Startup fails outside Development if missing |
| `Forge__PublicBaseUrl` | ✔ | | Direct credential submission target |
| `Forge__EnvironmentId` | | ✔ | Settings-page links |
| `Hosting__*` (DB, Key Vault, Service Bus, Blob, DataProtectionKeyId, MI client id) | ✔ | ✔ | Startup fails outside Development if insecure |
| `EnvironmentRegistry__Environments__<ID>__BaseUrl` / `__Login__SuccessSelectors__0` | ✔ | ✔ | Central environment registry |
| `Worker__MaxConcurrentJobs`, `__JobTimeoutMinutes` (55), `__AwaitingInputTimeoutMinutes` (30), `__MfaHoldMinutes` (5), `__MaxJobLifetimeMinutes` (230) | | ✔ | |
| `Repositories__Mappings__N__*` | | ✔ | Jira project/component → ADO repo/branch |
| `ReportPublishing__AttachScreenshots` | | ✔ | Never for Production environments |

Hosted mode forces ModifyCode, RunValidation, GitCommit, GitPush, CreatePullRequest, TriggerCi and Deploy **off**
regardless of configuration.

## 6. Security assessment (remaining risks)

| # | Risk | Severity | Blocks pilot? | Blocks production? | Mitigation / next step |
|---|---|---|---|---|---|
| 1 | Real Forge behaviour unverified (FIT `principal` presence on GetTask, token headers, A2A details) | High | **Yes, until the pilot run** | Yes | Pilot step 4.1 verifies; code fails closed (no principal & no valid user token → task not found) |
| 2 | Public network mode: PostgreSQL reachable from any Azure tenant IP (Entra auth only, no passwords) | Medium | No | **Yes** | Deploy with `networkMode=private` |
| 3 | Screenshots of non-Production environments are posted to Jira issues (visible to issue viewers) | Medium | No (test data) | Review | Configure `MaskSelectors` for personal data; set `AttachScreenshots=false` if needed |
| 4 | Credentials transit memory as .NET strings (cannot be reliably wiped); encrypted at rest ≤15 min, deleted on use | Low | No | No | Inherent to .NET; minimised lifetime |
| 5 | Read-only guard is network-level for non-GET requests; GET requests with side effects (rare anti-pattern) are not blocked | Medium | No (non-Production pilot) | Review per app | Use read-only Production accounts; keep Production disabled until reviewed |
| 6 | Login success requires configured selectors per environment; heuristics only with explicit local opt-in | — | Setup needed | Setup needed | KMS owners provide selectors |
| 7 | Report comment is authored by the app (system token) — anyone who can view the issue sees it | Low | No | No | By design (issue-scoped) |
| 8 | Chat replies are stored in the job until consumed; a user could paste a secret despite warnings (heuristic refusal) | Low | No | No | Users are told to use the settings page; refusal of password-like text |
| 9 | Admin Blazor UI has no authentication | Medium | No (not deployed) | Do not deploy it publicly | Keep local-only |
| 10 | ACR Basic has no private endpoint | Low | No | Review | Premium ACR + private endpoint if required |
| 11 | Data retention: evidence 90 days (Blob lifecycle), job rows kept indefinitely | Low | No | Define policy | Add a retention job if required |
| 12 | Forge non-production environments only usable by app contributors | — | Setup | No | Use the Forge production environment for the team |
