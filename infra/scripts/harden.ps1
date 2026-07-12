<#
.SYNOPSIS
  Switch the env's data/AI services to private-endpoint-only access. Twin of harden.sh.
.DESCRIPTION
  Run LAST. A redeploy re-enables public access (the Bicep default), so re-run this after
  any deploy.
.EXAMPLE
  .\harden.ps1 dev
#>
[CmdletBinding()]
param([Parameter(Mandatory, Position = 0)][string]$Environment)

. "$PSScriptRoot\lib.ps1"

$envName = Require-EnvArg $Environment
Confirm-Subscription
$rg = Get-EnvRg $envName

Write-Log "Hardening '$envName': switching data/AI services to private-endpoint-only access..."

function Update-Ids {
    param([string[]]$Ids, [string[]]$Settings)
    foreach ($id in $Ids) {
        if ([string]::IsNullOrWhiteSpace($id)) { continue }
        Invoke-Native az resource update --ids $id --set @Settings --output none
        Write-Log "  locked: $($id.Split('/')[-1])"
    }
}

function Get-Ids { param([string[]]$AzArgs) @(az @AzArgs --query '[].id' -o tsv) }

# Storage (ADLS Gen2): no public access, no shared-key auth.
Update-Ids (Get-Ids @('storage', 'account', 'list', '-g', $rg)) `
    @('properties.publicNetworkAccess=Disabled', 'properties.allowSharedKeyAccess=false', 'properties.networkAcls.defaultAction=Deny')

# Cosmos DB: no public access, Entra-only.
Update-Ids (Get-Ids @('cosmosdb', 'list', '-g', $rg)) `
    @('properties.publicNetworkAccess=Disabled', 'properties.disableLocalAuth=true')

# Azure AI Search: no public access (this RP wants the lowercase value). Local auth is already
# disabled at creation in ai.bicep and cannot be toggled here while authOptions is set.
Update-Ids (Get-Ids @('search', 'service', 'list', '-g', $rg)) `
    @('properties.publicNetworkAccess=disabled')

# AI Foundry (Cognitive Services): no public access, Entra-only.
Update-Ids (Get-Ids @('cognitiveservices', 'account', 'list', '-g', $rg)) `
    @('properties.publicNetworkAccess=Disabled', 'properties.networkAcls.defaultAction=Deny', 'properties.disableLocalAuth=true')

# Key Vault: no public access.
Update-Ids (Get-Ids @('keyvault', 'list', '-g', $rg)) `
    @('properties.publicNetworkAccess=Disabled', 'properties.networkAcls.defaultAction=Deny')

# Function Apps (Search Proxy + Regulatory Sync): no public inbound. Their runtime storage is
# already keyless + private-endpoint (functions.bicep), so the storage lockdown above is safe.
Update-Ids (Get-Ids @('functionapp', 'list', '-g', $rg)) `
    @('properties.publicNetworkAccess=Disabled')

Write-Log 'Hardening complete - storage, Cosmos, Search, Foundry, Key Vault, and Functions are private-endpoint only.'
Write-Warn 'Re-running deploy re-enables public access (Bicep default); re-run harden after any redeploy.'
