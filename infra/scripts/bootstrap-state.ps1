<#
.SYNOPSIS
  One-time setup of the Azure Storage account that holds the Terraform state.

.DESCRIPTION
  Terraform cannot create its own backend, so this is the only resource created outside it.
  Run after `az login`. Writes infra/terraform/backend.hcl (git-ignored) for `terraform init -backend-config=backend.hcl`.
  Access is through Entra ID only (shared keys disabled), so no storage key ever exists to leak.

  New subscriptions are often refused in some regions ("region is not accepting new customers"), so the script tries
  the candidate regions in order and uses the first one that accepts a storage account. Safe to re-run: when the
  state storage already exists it is reused. Stops at the first unexpected az failure.

.PARAMETER Location
  Candidate regions, tried in order.
#>
param(
    [string[]]$Location = @('swedencentral', 'northeurope', 'germanywestcentral', 'francecentral', 'polandcentral', 'uksouth'),
    [string]$ResourceGroup = 'rg-shorty-tfstate',
    [string]$StateKey = 'shorty-dev.tfstate'
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
Write-Host "Account      : $($account.user.name)"
Write-Host "Subscription : $($account.name) ($($account.id))  state=$($account.state)"
if ($account.state -ne 'Enabled') {
    throw "The subscription is '$($account.state)', not 'Enabled'. Resources cannot be created. Check its status in the portal (Subscriptions)."
}

$storage = $null
$region = $null

if ((Invoke-Az group exists --name $ResourceGroup) -eq 'true') {
    $storage = Invoke-Az storage account list --resource-group $ResourceGroup --query '[0].name' -o tsv
    if ($storage) {
        $region = Invoke-Az group show --name $ResourceGroup --query location -o tsv
        Write-Host "Reusing existing state storage account $storage ($region)"
    }
    else {
        # Empty leftover of an earlier failed run. An existing group cannot change region, so start clean.
        Invoke-Az group delete --name $ResourceGroup --yes -o none
    }
}

if (-not $storage) {
    foreach ($candidate in $Location) {
        Write-Host "Trying region $candidate ..."
        Invoke-Az group create --name $ResourceGroup --location $candidate -o none
        $name = 'stshortytfstate' + (-join ((97..122) + (48..57) | Get-Random -Count 4 | ForEach-Object { [char]$_ }))
        try {
            Invoke-Az storage account create `
                --name $name --resource-group $ResourceGroup --location $candidate `
                --sku Standard_LRS --min-tls-version TLS1_2 `
                --allow-blob-public-access false --allow-shared-key-access false -o none
            $storage = $name
            $region = $candidate
            break
        }
        catch {
            Write-Warning "Region $candidate refused the storage account. Trying the next one."
            Invoke-Az group delete --name $ResourceGroup --yes -o none
        }
    }
    if (-not $storage) {
        throw "None of these regions accepted a storage account: $($Location -join ', '). Pass other regions with -Location."
    }
}

# Blob versioning gives a way back from a corrupted or accidentally overwritten state file.
Invoke-Az storage account blob-service-properties update --account-name $storage --resource-group $ResourceGroup --enable-versioning true -o none

$me = Invoke-Az ad signed-in-user show --query id -o tsv
$scope = Invoke-Az storage account show --name $storage --resource-group $ResourceGroup --query id -o tsv
Add-RoleIfMissing -ObjectId $me -PrincipalType User -Role 'Storage Blob Data Contributor' -Scope $scope

# Created through the Azure Resource Manager API (control plane), not the blob endpoint: it needs no data-plane
# call, so it also works behind proxies that re-sign TLS for *.blob.core.windows.net.
Invoke-Az storage container-rm create --storage-account $storage --resource-group $ResourceGroup --name tfstate -o none

# UTF-8 *without* BOM: Windows PowerShell's `Set-Content -Encoding utf8` adds one, which HCL rejects.
$backend = Join-Path $PSScriptRoot '..\terraform\backend.hcl'
$content = @"
resource_group_name  = "$ResourceGroup"
storage_account_name = "$storage"
container_name       = "tfstate"
key                  = "$StateKey"
use_azuread_auth     = true
"@
[System.IO.File]::WriteAllText($backend, $content, (New-Object System.Text.UTF8Encoding $false))

Write-Host ''
Write-Host "State storage ready: $storage (region: $region)"
Write-Host "Use this region for the infrastructure too: location = `"$region`" in infra/terraform/dev.tfvars"
Write-Host 'Role assignments can take a few minutes to take effect; if the first terraform init reports 403, wait and retry.'
Write-Host 'Next: cd infra/terraform; terraform init "-backend-config=backend.hcl"'
