<#
.SYNOPSIS
  Runs the real agent (API + worker) on this machine and connects it to YOUR Atlassian developer site through a
  temporary HTTPS tunnel. Nothing here touches the company Jira site or Azure.

.EXAMPLE
  .\scripts\jira-trial\JiraTrial.ps1 -Setup     # once: copies the Forge app to D:\bia-trial\forge and registers it
  .\scripts\jira-trial\JiraTrial.ps1 -Start     # tunnel + forge deploy + API + worker (run again after a reboot)
  .\scripts\jira-trial\JiraTrial.ps1 -Install   # once, after the first -Start: installs the app on your site
  .\scripts\jira-trial\JiraTrial.ps1 -Stop
#>
param([switch]$Setup, [switch]$Start, [switch]$Install, [switch]$Stop)

$ErrorActionPreference = "Stop"
$Repo      = (Resolve-Path "$PSScriptRoot\..\..").Path
$TrialRoot = "D:\bia-trial"
$ForgeDir  = Join-Path $TrialRoot "forge"
$Settings  = Join-Path $TrialRoot "trial-settings.ps1"
$NodeDir   = "D:\bia-tools\node24"
$ApiPort   = 5311
$WorkerPort = 5312
$env:Path  = "$NodeDir;$NodeDir\node_modules\.bin;$env:Path"

function Say($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Fail($msg) { Write-Host "XX  $msg" -ForegroundColor Red; exit 1 }

New-Item -ItemType Directory -Force $TrialRoot, "$TrialRoot\data", "$TrialRoot\evidence", "$TrialRoot\keys", "$TrialRoot\logs" | Out-Null
if ($Stop) {
    foreach ($f in Get-ChildItem "$TrialRoot\*.pid" -ErrorAction SilentlyContinue) {
        try { Stop-Process -Id ([int](Get-Content $f)) -Force -ErrorAction Stop } catch { }
        Remove-Item $f
    }
    Get-Process BugInvestigationAgent.Api, BugInvestigationAgent.Worker -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Host "==> Stopped" -ForegroundColor Cyan
    exit 0
}
if (-not (Test-Path $Settings)) {
    Copy-Item "$PSScriptRoot\trial-settings.example.ps1" $Settings
    Fail "Fill in $Settings first (it was just created), then run again."
}
. $Settings
if (-not (Get-Command forge -ErrorAction SilentlyContinue)) { Fail "Forge CLI not found in $NodeDir." }

# Forge credentials: environment variables for this run only (works where the OS keychain / registry is blocked).
if ([string]::IsNullOrWhiteSpace($env:FORGE_API_TOKEN)) {
    Write-Host "Forge login (Atlassian API token from https://id.atlassian.com/manage-profile/security/api-tokens)" -ForegroundColor Yellow
    $env:FORGE_EMAIL = Read-Host "Atlassian account email"
    $tok = Read-Host "Atlassian API token (hidden; not saved)" -AsSecureString
    $env:FORGE_API_TOKEN = [System.Net.NetworkCredential]::new("", $tok).Password
    if ([string]::IsNullOrWhiteSpace($env:FORGE_API_TOKEN)) { Fail "No API token entered." }
}
forge whoami | Out-Host
if ($LASTEXITCODE -ne 0) { Remove-Item Env:\FORGE_API_TOKEN -ErrorAction SilentlyContinue; Fail "Forge rejected the email/API token." }

function Get-ForgeAppId {
    $m = Select-String -Path "$ForgeDir\manifest.yml" -Pattern 'ari:cloud:ecosystem::app/([0-9a-f-]{36})'
    if ($m) { return $m.Matches[0].Groups[1].Value }
    return $null
}

function Get-Cloudflared {
    $c = Get-Command cloudflared -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($p in @("${env:ProgramFiles(x86)}\cloudflared\cloudflared.exe", "$env:ProgramFiles\cloudflared\cloudflared.exe")) {
        if (Test-Path $p) { return $p }
    }
    $w = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter cloudflared.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($w) { return $w.FullName }
    Fail "cloudflared not found (winget install Cloudflare.cloudflared)."
}

function Stop-Trial {
    foreach ($f in Get-ChildItem "$TrialRoot\*.pid" -ErrorAction SilentlyContinue) {
        $procId = [int](Get-Content $f)
        try { Stop-Process -Id $procId -Force -ErrorAction Stop; Say "Stopped $($f.BaseName) ($procId)" } catch { }
        Remove-Item $f
    }
    # dotnet run starts the app as a child process
    Get-Process BugInvestigationAgent.Api, BugInvestigationAgent.Worker -ErrorAction SilentlyContinue | Stop-Process -Force
}

if ($Stop) { Stop-Trial; exit 0 }

if ($Setup) {
    Say "Copying the Forge app to $ForgeDir (the repo's manifest stays untouched)"
    New-Item -ItemType Directory -Force $ForgeDir | Out-Null
    Copy-Item "$Repo\forge\manifest.yml", "$Repo\forge\package.json" $ForgeDir -Force
    Copy-Item "$Repo\forge\static" $ForgeDir -Recurse -Force
    if (-not (Test-Path "$ForgeDir\static\settings\build\index.html")) { Fail "Forge UI is not built (npm run build in forge\)." }
    Push-Location $ForgeDir
    try {
        if (-not (Get-ForgeAppId)) {
            Say "Registering the Forge app under YOUR Atlassian account (answer the prompts)"
            forge register "Bug Investigation Agent"
        }
    } finally { Pop-Location }
    $id = Get-ForgeAppId
    if (-not $id) { Fail "Registration did not write an app id to $ForgeDir\manifest.yml" }
    Say "Forge app id: $id. Next: -Start"
    exit 0
}

if ($Start) {
    $appId = Get-ForgeAppId
    if (-not $appId) { Fail "Run -Setup first." }
    if ([string]::IsNullOrWhiteSpace($KmsqaBaseUrl)) { Fail "Set `$KmsqaBaseUrl in $Settings" }
    if ([string]::IsNullOrWhiteSpace($KmsqaSuccessSelector) -and -not $AllowUnverifiedLogin) {
        Fail "Set `$KmsqaSuccessSelector (or `$AllowUnverifiedLogin = `$true) in $Settings"
    }

    Stop-Trial

    Say "Reading the cloud id of $JiraSite"
    $cloudId = (Invoke-RestMethod "https://$JiraSite/_edge/tenant_info").cloudId
    if (-not $cloudId) { Fail "Could not read the cloud id of $JiraSite" }

    $llmKey = ""
    if ($LlmProvider -ne "null") {
        $secure = Read-Host "LLM API key for '$LlmProvider' (kept in memory only; Enter to skip analysis)" -AsSecureString
        $llmKey = [System.Net.NetworkCredential]::new("", $secure).Password
        if ([string]::IsNullOrEmpty($llmKey)) { $LlmProvider = "null" }
    }

    Say "Starting the HTTPS tunnel to http://localhost:$ApiPort"
    $cf = Get-Cloudflared
    $tunnelLog = "$TrialRoot\logs\tunnel.log"
    Remove-Item $tunnelLog -ErrorAction SilentlyContinue
    $t = Start-Process $cf -ArgumentList "tunnel --no-autoupdate --url http://localhost:$ApiPort" `
        -RedirectStandardError $tunnelLog -RedirectStandardOutput "$TrialRoot\logs\tunnel.out.log" -WindowStyle Hidden -PassThru
    Set-Content "$TrialRoot\tunnel.pid" $t.Id
    $publicUrl = $null
    for ($i = 0; $i -lt 60 -and -not $publicUrl; $i++) {
        Start-Sleep 1
        $m = Select-String -Path $tunnelLog -Pattern 'https://[a-z0-9-]+\.trycloudflare\.com' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($m) { $publicUrl = $m.Matches[0].Value }
    }
    if (-not $publicUrl) { Fail "Tunnel did not start; see $tunnelLog" }
    Say "Public URL: $publicUrl"

    # Configuration for API + worker: inherited by the child processes only (never written to disk).
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $env:Agent__DataDirectory = "$TrialRoot\data"
    $env:Agent__EvidenceDirectory = "$TrialRoot\evidence"
    $env:Agent__ActiveProviderId = $LlmProvider
    $env:Hosting__DataProtectionKeyDirectory = "$TrialRoot\keys"
    $env:Forge__AppId = $appId
    $env:Forge__AllowedCloudIds__0 = $cloudId
    $env:Forge__PublicBaseUrl = $publicUrl
    $env:Forge__EnvironmentId = $ForgeEnvironmentId
    $env:EnvironmentRegistry__Environments__KMSQA__BaseUrl = $KmsqaBaseUrl
    if ($KmsqaSuccessSelector) { $env:EnvironmentRegistry__Environments__KMSQA__Login__SuccessSelectors__0 = $KmsqaSuccessSelector }
    if ($KmsqaErrorSelector) { $env:EnvironmentRegistry__Environments__KMSQA__Login__ErrorSelectors__0 = $KmsqaErrorSelector }
    $env:EnvironmentRegistry__Environments__KMSQA__Login__AllowUnverifiedLogin = "$AllowUnverifiedLogin".ToLower()
    $env:Repositories__Mappings__0__JiraProject = $JiraProjectKey
    $env:Repositories__Mappings__0__Organization = $AdoOrganization
    $env:Repositories__Mappings__0__Project = $AdoProject
    $env:Repositories__Mappings__0__Repository = $AdoRepository
    $env:Repositories__Mappings__0__Branch = $AdoBranch
    $env:Worker__MaxConcurrentJobs = "2"
    switch ($LlmProvider) {
        "anthropic"         { $env:LlmProviders__Anthropic__ApiKey = $llmKey; if ($LlmModel) { $env:LlmProviders__Anthropic__Model = $LlmModel } }
        "openai-compatible" { $env:LlmProviders__OpenAiCompatible__ApiKey = $llmKey; if ($LlmModel) { $env:LlmProviders__OpenAiCompatible__Model = $LlmModel } }
    }

    Say "Building"
    dotnet build "$Repo\src\BugInvestigationAgent.Api" -nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "API build failed" }
    dotnet build "$Repo\src\BugInvestigationAgent.Worker" -nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "Worker build failed" }

    Say "Starting API (:$ApiPort) and worker (:$WorkerPort) in their own windows"
    $api = Start-Process powershell -ArgumentList "-NoExit", "-Command", "`$host.UI.RawUI.WindowTitle='BIA API'; dotnet run --no-build --project '$Repo\src\BugInvestigationAgent.Api' --urls http://localhost:$ApiPort" -PassThru
    Set-Content "$TrialRoot\api.pid" $api.Id
    $wrk = Start-Process powershell -ArgumentList "-NoExit", "-Command", "`$host.UI.RawUI.WindowTitle='BIA Worker'; dotnet run --no-build --project '$Repo\src\BugInvestigationAgent.Worker' --urls http://localhost:$WorkerPort" -PassThru
    Set-Content "$TrialRoot\worker.pid" $wrk.Id
    $llmKey = $null
    Remove-Item Env:\LlmProviders__Anthropic__ApiKey, Env:\LlmProviders__OpenAiCompatible__ApiKey -ErrorAction SilentlyContinue

    foreach ($p in @(@{Name = "API"; Url = "http://localhost:$ApiPort/readyz" }, @{Name = "Worker"; Url = "http://localhost:$WorkerPort/readyz" }, @{Name = "Tunnel"; Url = "$publicUrl/readyz" })) {
        $ok = $false
        for ($i = 0; $i -lt 90 -and -not $ok; $i++) {
            try { $r = Invoke-WebRequest $p.Url -UseBasicParsing -TimeoutSec 5; $ok = $r.StatusCode -eq 200 } catch { Start-Sleep 2 }
        }
        if ($ok) { Say "$($p.Name) healthy ($($p.Url))" } else { Fail "$($p.Name) not healthy: $($p.Url) - check its window" }
    }

    Say "Deploying the Forge app with the new URL"
    $manifest = Get-Content "$ForgeDir\manifest.yml" -Raw
    $manifest = $manifest -replace 'baseUrl: https://\S+', "baseUrl: $publicUrl"
    [System.IO.File]::WriteAllText("$ForgeDir\manifest.yml", $manifest)
    Push-Location $ForgeDir
    try {
        forge deploy -e development --non-interactive
        if ($LASTEXITCODE -ne 0) { Fail "forge deploy failed" }
        if (Test-Path "$TrialRoot\installed.flag") {
            Say "Upgrading the installation on $JiraSite (the URL changed)"
            forge install --upgrade --site $JiraSite --product jira -e development --non-interactive
        }
    } finally { Pop-Location }

    Say "Ready. Open a bug in $JiraProjectKey on https://$JiraSite and assign it to 'Bug Investigation Agent'."
    if (-not (Test-Path "$TrialRoot\installed.flag")) { Say "First time: run -Install now." }
    exit 0
}

if ($Install) {
    Push-Location $ForgeDir
    try {
        forge install --site $JiraSite --product jira -e development
        if ($LASTEXITCODE -ne 0) { Fail "forge install failed" }
        Set-Content "$TrialRoot\installed.flag" (Get-Date -Format o)
    } finally { Pop-Location }
    Say "Installed on $JiraSite. The API window should log app.installed."
    exit 0
}

Get-Help $MyInvocation.MyCommand.Path -Examples
