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

# --- Search Proxy: its OWN app registration, never regsync's. Separate apps, separate identities,
#     separate audiences - sharing an audience would hand the internet-facing proxy a token the
#     corpus-writing app accepts, destroying exactly the boundary the two identities exist to hold. ---
$proxyApp = Get-SearchProxyApp $envName
$proxyRegName = Get-ProxyAppRegName $envName

Write-Log "Ensuring Entra app registration '$proxyRegName'..."
$proxyClientId = az ad app list --display-name $proxyRegName --query '[0].appId' -o tsv
if ([string]::IsNullOrWhiteSpace($proxyClientId)) {
    $proxyClientId = az ad app create --display-name $proxyRegName --query appId -o tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($proxyClientId)) {
        Die "Failed to create the app registration '$proxyRegName'."
    }
    Write-Log "Created app registration $proxyClientId"
}

# The audience is api://<appId>, NOT api://<display-name>: functions.bicep pins the proxy's
# allowedAudiences to 'api://${proxyAuthClientId}' and main.bicep hands the orchestrator that same
# string as SEARCH_PROXY_AUDIENCE. The identifier URI must exist in exactly that form or the token the
# orchestrator asks for is one Entra will not mint. Setting it here keeps the script's state and the
# Bicep's state identical, so the follow-up redeploy is idempotent rather than a flip-flop.
$proxyAudience = "api://$proxyClientId"
Invoke-Native az ad app update --id $proxyClientId --identifier-uris $proxyAudience --output none

Write-Log "Enforcing Easy Auth on $proxyApp (audience $proxyAudience)..."
Invoke-Native az webapp auth update -g $rg -n $proxyApp `
    --enabled true --action Return401 `
    --aad-allowed-token-audiences $proxyAudience `
    --aad-client-id $proxyClientId `
    --aad-token-issuer-url "https://login.microsoftonline.com/$tenantId/v2.0" --output none

Write-Warn "The ACA orchestrator must present a token for audience $proxyAudience (SEARCH_PROXY_AUDIENCE)."
Write-Log "Keep Bicep in sync: redeploy with -Parameters @('proxyAuthClientId=$proxyClientId'). That redeploy is"
Write-Log "not optional: it is also what sets the orchestrator's SEARCH_PROXY_AUDIENCE, empty until you pass it."
