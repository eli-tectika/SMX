<#
.SYNOPSIS
  Ensure the Entra app registration for the regsync Function App and enforce Easy Auth
  (Return401). Twin of configure-auth.sh. Entra app objects are Graph, not ARM - hence a script.
.EXAMPLE
  .\configure-auth.ps1 dev
#>
[CmdletBinding()]
param([Parameter(Mandatory, Position = 0)][string]$Environment)

. "$PSScriptRoot\lib.ps1"

$envName = Require-EnvArg $Environment
Confirm-Subscription

$rg = Get-EnvRg $envName
$app = Get-RegSyncApp $envName
$appRegName = Get-AppRegName $envName

Write-Log "Ensuring Entra app registration '$appRegName'..."
$clientId = az ad app list --display-name $appRegName --query '[0].appId' -o tsv
if ([string]::IsNullOrWhiteSpace($clientId)) {
    $clientId = az ad app create --display-name $appRegName `
        --identifier-uris "api://$appRegName" --query appId -o tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($clientId)) { Die 'Failed to create the app registration.' }
    Write-Log "Created app registration $clientId"
}

$tenantId = az account show --query tenantId -o tsv
Write-Log "Enforcing Easy Auth on $app (audience api://$appRegName)..."
Invoke-Native az webapp auth update -g $rg -n $app `
    --enabled true --action Return401 `
    --aad-allowed-token-audiences "api://$appRegName" `
    --aad-client-id $clientId `
    --aad-token-issuer-url "https://login.microsoftonline.com/$tenantId/v2.0" --output none

Write-Warn "Callers (ACA orchestrator) must present an Entra token for audience api://$appRegName."
Write-Log "Keep Bicep in sync: redeploy with -Parameters @('authClientId=$clientId'), then run harden.ps1."
