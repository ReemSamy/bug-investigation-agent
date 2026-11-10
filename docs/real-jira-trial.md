# Trying the agent in a REAL Jira site (without touching company Jira or Azure)

This connects the real Forge remote agent to a **free Atlassian developer site you own**, with the agent's API and
worker running **on your machine** behind a temporary HTTPS tunnel. It verifies what local tests cannot: real
Forge Invocation Tokens, real Jira user/app tokens, real Rovo agent invocation, a real comment on a real issue.

> These steps use **your own** Atlassian account and a developer site you create — run them yourself.
> Do not install on the company site without the Jira admin's approval (see `deployment-and-pilot-runbook.md`).

## 0. Prerequisites
* Node.js **≥ 24.22.1** (or ≥ 22.23.4) — the Forge CLI refuses older versions.
* .NET 8 SDK; Playwright Chromium (`pwsh src/BugInvestigationAgent.Worker/bin/Debug/net8.0/playwright.ps1 install chromium`).
* A tunnel tool that gives a public HTTPS URL to localhost, e.g. Microsoft Dev Tunnels: `winget install Microsoft.devtunnel`.
* An LLM key is optional (without it, root-cause analysis is skipped and the report says so).

## 1. Create a free developer site
1. Go to <https://go.atlassian.com/cloud-dev> and create a site (e.g. `yourname-bia.atlassian.net`) with Jira.
2. Create a project (e.g. key `BIA`) and a **Bug** in it with a description like:
   `Steps to reproduce: 1. Incidents` and summary containing `Cannot read properties of undefined`.
3. Check that **Rovo / agents** are available on the site (Jira → the Rovo / agents panel). The remote agent module
   is invoked through Rovo; if the site has no Rovo, the agent cannot be invoked there.
4. Note the cloud id: open `https://yourname-bia.atlassian.net/_edge/tenant_info`.

## 2. Environment to investigate
Use the real KMSQA environment with a KMSQA **test** account (never Production). The worker opens the URL from
**your machine**, so the machine must be able to reach KMSQA.

## 3. Start the API and worker locally (shared database + key ring)
PowerShell, two terminals, same settings:
```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Agent__DataDirectory = "D:\bia-trial\data"                       # shared SQLite
$env:Agent__EvidenceDirectory = "D:\bia-trial\evidence"
$env:Hosting__DataProtectionKeyDirectory = "D:\bia-trial\keys"         # shared key ring (API ↔ worker)
$env:Forge__AppId = "<app uuid from step 4>"
$env:Forge__AllowedCloudIds__0 = "<cloud id from step 1>"
$env:Forge__PublicBaseUrl = "https://<your-tunnel-host>"
$env:EnvironmentRegistry__Environments__KMSQA__BaseUrl = "<real KMSQA URL>"
$env:EnvironmentRegistry__DefaultLogin__SuccessSelectors__0 = "<CSS selector visible only after KMSQA login>"
$env:Worker__MaxConcurrentJobs = "2"
```
```bash
dotnet run --project src/BugInvestigationAgent.Api --urls http://localhost:5311
dotnet run --project src/BugInvestigationAgent.Worker --urls http://localhost:5312
```
Check: `curl http://localhost:5311/readyz` and `curl http://localhost:5312/readyz` → Healthy.

## 4. Public HTTPS tunnel to the API
```bash
devtunnel user login
devtunnel host -p 5311 --allow-anonymous
```
`--allow-anonymous` is required because Atlassian calls the URL; every endpoint still rejects requests without a
valid Forge Invocation Token. Note the tunnel host (e.g. `abc123-5311.euw.devtunnels.ms`) → `Forge__PublicBaseUrl`.

## 5. Register, deploy and install the Forge app (your account, your dev site)
```bash
cd forge
npm install && npm run build
npm install -g @forge/cli
forge login                    # API token from https://id.atlassian.com/manage-profile/security/api-tokens
forge register                 # writes app.id into manifest.yml → copy the UUID to Forge__AppId and restart API/worker
# manifest.yml: replace REPLACE_WITH_API_HOST with the tunnel host
forge lint
forge deploy -e development
forge install --site yourname-bia.atlassian.net --product jira -e development
forge environments list        # optional: the development environment id → Forge__EnvironmentId (settings links)
```
The API log/audit should now show `app.installed`.

## 6. Run the investigation in Jira
1. Open the bug → assign it to **Bug Investigation Agent** (or `@Bug Investigation Agent` in a comment, or ask in
   the Rovo panel “Investigate BIA-1”).
2. The agent asks for the environment → reply `KMSQA`.
3. Open the link it gives (Personal settings → Apps → Bug Investigation Agent), enter the environment
   credentials (your KMSQA test account), then reply `continue`. If KMSQA asks for MFA, the agent asks for the code.
4. The task completes and the issue gets an **AI Bug Investigation** comment with screenshots attached.

## 7. What this proves, and what to report back
| Check | Expected |
|---|---|
| FIT validation with Atlassian's real keys | requests accepted (no 401 in the API log) |
| User identity | job `InvokingAccountId` = your Atlassian account id |
| GetTask polling identity | progress updates appear in Jira (if they don't, the poll carried no user identity — tell me) |
| Credentials channel | the settings page loads pending items and submits (CORS from the Forge CDN) |
| Report | exactly one comment + screenshots on the issue |

If anything differs from the table, capture the API/worker console output (it contains no secrets) — real Forge
behaviour is the main remaining unknown.

## Clean up
`forge uninstall --site yourname-bia.atlassian.net --product jira -e development`, stop the tunnel, delete
`D:\bia-trial`.
