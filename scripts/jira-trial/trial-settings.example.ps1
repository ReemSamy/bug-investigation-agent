# Copy this file to D:\bia-trial\trial-settings.ps1 and fill it in. NO passwords or API keys here:
# the LLM key is asked for when you start (kept in memory only), KMSQA credentials are entered in Jira's secure page.

# Your Atlassian site (free developer site, NOT the company site) and the Jira project with the test bug.
$JiraSite        = "yourname.atlassian.net"
$JiraProjectKey  = "BIA"

# KMSQA (test environment only).
$KmsqaBaseUrl          = ""        # e.g. the KMSQA login page URL
$KmsqaSuccessSelector  = ""        # CSS selector visible ONLY after a successful login, e.g. "#user-menu"
$KmsqaErrorSelector    = ""        # optional: CSS selector of the "wrong password" message
$AllowUnverifiedLogin  = $false    # $true only if you have no success selector (report will say login was not verified)

# Azure DevOps repository the agent reads (read-only, with the account you are signed into in Visual Studio / az).
$AdoOrganization = ""              # e.g. "visiumkms" from https://dev.azure.com/visiumkms
$AdoProject      = ""
$AdoRepository   = ""
$AdoBranch       = "main"

# LLM used for root-cause analysis: "anthropic", "openai-compatible" or "null" (no analysis).
$LlmProvider = "anthropic"
$LlmModel    = "claude-sonnet-5-5"

# Optional: Forge development environment id (makes the "secure input" link open the page directly).
$ForgeEnvironmentId = ""
