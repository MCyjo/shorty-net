<#
.SYNOPSIS
  Lets GitHub Actions deploy to your Azure subscription without any stored secret (OIDC federation).

.DESCRIPTION
  Creates an Entra ID app registration + service principal and trusts tokens that GitHub issues for ONE specific
  workflow context of ONE repository. There is no client secret and no certificate: GitHub proves who is calling
  with a short-lived token, Azure checks the repository and branch inside it, and hands back an access token.

  Run after `az login` (to YOUR Azure account) and after bootstrap-state.ps1.
  Safe to re-run: existing objects are reused. Stops at the first failing az command.

.PARAMETER GitHubRepo
  owner/name of the repository.

.PARAMETER Branch
  Only workflows running on this branch may obtain an Azure token. Works on every GitHub plan.

.PARAMETER Environment
  Alternative to -Branch: trust a GitHub *environment* instead (lets you require a manual approval before deploy).
  Environments on private repositories need a paid GitHub plan, so the default is the branch.

.PARAMETER SetGitHubVariables
  Also store the three non-secret ids as repository variables with the GitHub CLI (`gh` must be logged in).
#>
param(
    [string]$GitHubRepo = 'MCyjo/shorty-net',
    [string]$AppName = 'shorty-github-actions',
    [string]$Branch = 'main',
    [string]$Environment,
    [switch]$SetGitHubVariables
)

$ErrorActionPreference = 'Stop'

# $ErrorActionPreference does not cover native programs, so every az call goes through this wrapper.
function Invoke-Az {
    $output = & az @args
    if ($LASTEXITCODE -ne 0) { throw "'az $($args -join ' ')' failed with exit code $LASTEXITCODE." }
    $output
}

# Idempotent: creating an assignment that already exists is an error in some az versions.
function Add-RoleIfMissing {
    param([string]$ObjectId, [string]$PrincipalType, [string]$Role, [string]$Scope)
    $existing = Invoke-Az role assignment list --assignee $ObjectId --role $Role --scope $Scope --query '[].id' -o tsv
    if (-not $existing) {
        Invoke-Az role assignment create --assignee-object-id $ObjectId --assignee-principal-type $PrincipalType `
            --role $Role --scope $Scope -o none
    }
}

$account = Invoke-Az account show -o json | ConvertFrom-Json
$subscriptionId = $account.id
$tenantId = $account.tenantId
Write-Host "Azure account : $($account.user.name)"
Write-Host "Subscription  : $($account.name) ($subscriptionId)  state=$($account.state)"
Write-Host "GitHub repo   : $GitHubRepo"
if ($account.state -ne 'Enabled') {
    throw "The subscription is '$($account.state)', not 'Enabled'. Fix that first (see the portal, Subscriptions)."
}
$answer = Read-Host 'Is this YOUR personal subscription (not your employer''s)? Type yes to continue'
if ($answer -ne 'yes') { throw 'Aborted. Run `az login` with the right account and try again.' }

# The subject GitHub puts in its token depends on a repository setting: repositories created recently use immutable ids
# ("repo:OWNER@OWNERID/NAME@REPOID:...", AADSTS700213 if you trust the plain name instead), older ones use "repo:OWNER/NAME:...".
# Ask GitHub which prefix it will present rather than guessing.
$prefix = "repo:$GitHubRepo"
$custom = (gh api "repos/$GitHubRepo/actions/oidc/customization/sub" 2>$null) -join ''
if ($LASTEXITCODE -eq 0 -and $custom) {
    $configured = ($custom | ConvertFrom-Json).sub_claim_prefix
    if ($configured) { $prefix = $configured }
}
$subject = if ($Environment) { "${prefix}:environment:$Environment" } else { "${prefix}:ref:refs/heads/$Branch" }
$credentialName = if ($prefix -eq "repo:$GitHubRepo") { 'github-actions' } else { 'github-actions-immutable' }

# --- app registration + service principal (idempotent: reuse when it already exists) ---------------------------
$appId = Invoke-Az ad app list --display-name $AppName --query '[0].appId' -o tsv
if (-not $appId) { $appId = Invoke-Az ad app create --display-name $AppName --query appId -o tsv }
$spId = Invoke-Az ad sp list --filter "appId eq '$appId'" --query '[0].id' -o tsv
if (-not $spId) { $spId = Invoke-Az ad sp create --id $appId --query id -o tsv }

# --- the trust: "tokens from this repo, on this branch/environment, may act as this app" -----------------------
$existing = Invoke-Az ad app federated-credential list --id $appId --query "[?subject=='$subject'].name" -o tsv
if (-not $existing) {
    $body = @{
        name      = $credentialName
        issuer    = 'https://token.actions.githubusercontent.com'
        subject   = $subject
        audiences = @('api://AzureADTokenExchange')
    } | ConvertTo-Json -Compress
    $file = [System.IO.Path]::GetTempFileName()
    try {
        # No BOM: some az versions refuse to parse JSON that starts with one.
        [System.IO.File]::WriteAllText($file, $body, (New-Object System.Text.UTF8Encoding $false))
        Invoke-Az ad app federated-credential create --id $appId --parameters "@$file" -o none
    }
    finally { Remove-Item $file -ErrorAction SilentlyContinue }
}

# --- permissions ---------------------------------------------------------------------------------------------
# None are granted here, on purpose. This identity only pushes images and updates the apps, and Terraform grants it exactly that
# (AcrPush on the registry, Contributor on the one resource group) through var.github_actions_principal_id. It does not run
# Terraform, so it needs no access to the subscription or to the state. Until that apply, a deploy run is refused by Azure.

Write-Host ''
Write-Host 'Done. These three values are identifiers, not secrets:'
Write-Host "  AZURE_CLIENT_ID       = $appId"
Write-Host "  AZURE_TENANT_ID       = $tenantId"
Write-Host "  AZURE_SUBSCRIPTION_ID = $subscriptionId"
Write-Host "Trusted GitHub context : $subject"
Write-Host ''
Write-Host 'Put this in infra/terraform/dev.tfvars so that Terraform lets the identity push images and update the apps:'
Write-Host "  github_actions_principal_id = `"$spId`""

if ($SetGitHubVariables) {
    gh variable set AZURE_CLIENT_ID --body $appId --repo $GitHubRepo
    gh variable set AZURE_TENANT_ID --body $tenantId --repo $GitHubRepo
    gh variable set AZURE_SUBSCRIPTION_ID --body $subscriptionId --repo $GitHubRepo
    Write-Host "Stored as repository variables in $GitHubRepo."
}
