<#
.SYNOPSIS
  Ensure the Entra app registrations this app needs (regsync, Search Proxy, and the frontend's SPA +
  API) and enforce Easy Auth (Return401) on the Function Apps. Twin of configure-auth.sh. Entra app
  objects are Graph, not ARM - hence a script.
.PARAMETER Environment
  'dev' or 'prod'.
.PARAMETER AppHost
  FQDN the SPA signs in against, e.g. dev.smxmarkers.io (or set the APP_HOST environment variable
  instead). Only the frontend SPA + API section at the bottom needs it - the regsync/searchproxy
  sections below run fine without it. NOT named -Host: that shadows PowerShell's read-only automatic
  $Host variable.
.EXAMPLE
  .\configure-auth.ps1 dev
.EXAMPLE
  .\configure-auth.ps1 dev dev.smxmarkers.io
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string]$Environment,
    [Parameter(Position = 1)][string]$AppHost = ''
)

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

# =====================================================================================================
# Frontend Entra app registrations (Task B1): the SPA (React web app) that signs the operator in, and
# the API (the audience the backend validates). Two SEPARATE app registrations - same reasoning as the
# Search Proxy block above: the SPA acquires a token whose AUDIENCE is the API, never itself, so the
# SPA's client id can never be replayed as a bearer token the backend accepts. AzureADMyOrg = single-
# tenant (this org only) - there is exactly one operator and no cross-tenant use case. The SPA is added
# to the API's preAuthorizedApplications so the operator is not prompted for consent every sign-in
# (Grant admin consent, below, still has to run once).
# =====================================================================================================
$appHostValue = if ($AppHost) { $AppHost } elseif ($env:APP_HOST) { $env:APP_HOST } else { '' }
if ([string]::IsNullOrWhiteSpace($appHostValue)) {
    Die @"
Missing host for the SPA redirect URI. Usage: .\configure-auth.ps1 $envName <host>  (or set the APP_HOST environment variable)
 Example: .\configure-auth.ps1 $envName dev.smxmarkers.io
 Refusing to guess: a wrong/placeholder redirect URI silently breaks login instead of erroring loudly.
"@
}

# --- API app registration: exposes the delegated scope the backend validates as its JwtBearer audience. ---
$apiAppName = "$($script:NamePrefix)-$envName-api"
Write-Log "Ensuring Entra app registration '$apiAppName'..."
$apiId = az ad app list --display-name $apiAppName --query '[0].appId' -o tsv
if ([string]::IsNullOrWhiteSpace($apiId)) {
    $apiId = az ad app create --display-name $apiAppName --sign-in-audience AzureADMyOrg --query appId -o tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($apiId)) { Die "Failed to create the app registration '$apiAppName'." }
    Write-Log "Created app registration $apiId"
}
# api://<appId>, not api://<display-name> - same reasoning as $proxyAudience above: the backend's
# JwtBearer audience (API_CLIENT_ID via apiClientId, Task B3) keys off the literal appId.
Invoke-Native az ad app update --id $apiId --identifier-uris "api://$apiId" --output none

# Expose the delegated scope access_as_user, idempotently. Read back any EXISTING scope id first, via a
# null-safe JMESPath filter (works whether 'api' is absent or its oauth2PermissionScopes is null, empty,
# or already populated) rather than counting entries - a length()-style emptiness check throws a hard
# error on a null collection, which would abort the script on a freshly created app whose 'api' object
# has not been populated yet. A freshly generated guid must NEVER be used once the scope already exists:
# on a re-run that would pre-authorize the SPA (below) for a scope id the API was never actually
# assigned, and the backend would then reject every token as an invalid scope.
$scopeId = az ad app show --id $apiId --query "api.oauth2PermissionScopes[?value=='access_as_user'].id | [0]" -o tsv
if ([string]::IsNullOrWhiteSpace($scopeId)) {
    $scopeId = [guid]::NewGuid().ToString()
    Write-Log "Exposing scope access_as_user ($scopeId) on $apiAppName..."
    # Build via ConvertTo-Json (not a hand-escaped string): PowerShell double-quoted strings need each
    # embedded '"' backslash-escaped, which is exactly the trap the .sh twin sidesteps with bash's `\"`.
    # -InputObject (not the pipeline) avoids ConvertTo-Json's well-known single-element-array collapse.
    $scopeObj = @{
        oauth2PermissionScopes = @(
            @{
                id                      = $scopeId
                value                   = 'access_as_user'
                type                    = 'User'
                isEnabled               = $true
                adminConsentDisplayName = 'Access SMX API'
                adminConsentDescription = 'Allow the SMX web app to call the SMX API as the signed-in operator'
                userConsentDisplayName  = 'Access SMX API'
                userConsentDescription  = 'Allow the SMX web app to call the SMX API on your behalf'
            }
        )
    }
    $scopeJson = ConvertTo-Json -InputObject $scopeObj -Compress -Depth 5
    Invoke-Native az ad app update --id $apiId --set "api=$scopeJson" --output none
}
else {
    Write-Log "Scope access_as_user already exposed on $apiAppName ($scopeId)."
}

# --- SPA app registration: the React app; pre-authorized on the API so sign-in needs no consent prompt. ---
$spaAppName = "$($script:NamePrefix)-$envName-web"
Write-Log "Ensuring Entra app registration '$spaAppName'..."
$spaId = az ad app list --display-name $spaAppName --query '[0].appId' -o tsv
if ([string]::IsNullOrWhiteSpace($spaId)) {
    $spaId = az ad app create --display-name $spaAppName --sign-in-audience AzureADMyOrg --query appId -o tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($spaId)) { Die "Failed to create the app registration '$spaAppName'." }
    Write-Log "Created app registration $spaId"
}

# SPA-platform redirect URI (auth code + PKCE), root path on the gateway host. Always (re)set to the
# given host so a later domain change is corrected on the next run rather than silently left stale.
Write-Log "Setting SPA redirect URI to https://$appHostValue/ ..."
$spaObj = @{ redirectUris = @("https://$appHostValue/") }
$spaJson = ConvertTo-Json -InputObject $spaObj -Compress -Depth 5
Invoke-Native az ad app update --id $spaId --set "spa=$spaJson" --output none

# Pre-authorize the SPA for access_as_user so the operator sees no separate per-app consent prompt.
# Uses the READ-BACK $scopeId from above, never a freshly generated one - see the comment there.
Write-Log "Pre-authorizing $spaAppName on $apiAppName's access_as_user scope..."
$preAuthObj = @(
    @{
        appId                  = $spaId
        delegatedPermissionIds = @($scopeId)
    }
)
$preAuthJson = ConvertTo-Json -InputObject $preAuthObj -Compress -Depth 5
Invoke-Native az ad app update --id $apiId --set "api.preAuthorizedApplications=$preAuthJson" --output none

Write-Host "API_CLIENT_ID=$apiId"
Write-Host "SPA_CLIENT_ID=$spaId"
Write-Warn "Set in dev.bicepparam: apiClientId='$apiId'"
Write-Warn "Rebuild the frontend image with VITE_ENTRA_CLIENT_ID=$spaId (SPA), VITE_API_SCOPE=api://$apiId/access_as_user, VITE_ENTRA_TENANT_ID=$tenantId"
Write-Warn "Grant admin consent for the SPA: az ad app permission admin-consent --id $spaId  (needs a directory admin)"
