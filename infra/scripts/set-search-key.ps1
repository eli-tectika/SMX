<#
.SYNOPSIS
  Put the search provider API key in Key Vault and print the secret URI to feed back into Bicep.
  Twin of set-search-key.sh.

  The key is NEVER a plaintext app setting: the proxy reads it through a Key Vault reference
  (functions.bicep sets PROXY_SEARCH_API_KEY=@Microsoft.KeyVault(SecretUri=...)), resolved by the
  proxy's own UAMI - which is granted read on THIS ONE SECRET, not on the vault.
.EXAMPLE
  .\set-search-key.ps1 dev '<brave-api-key>'
.EXAMPLE
  $env:SEARCH_PROVIDER_KEY = '<brave-api-key>'; .\set-search-key.ps1 dev
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string]$Environment,
    # Omit it and the script reads SEARCH_PROVIDER_KEY, which keeps the key out of your shell history.
    [Parameter(Position = 1)][string]$Key = ''
)

. "$PSScriptRoot\lib.ps1"

$envName = Require-EnvArg $Environment
if ([string]::IsNullOrWhiteSpace($Key)) { $Key = Get-EnvOrDefault 'SEARCH_PROVIDER_KEY' '' }
if ([string]::IsNullOrWhiteSpace($Key)) {
    Die 'usage: set-search-key.ps1 <env> <search-provider-api-key>   (or set $env:SEARCH_PROVIDER_KEY)'
}
Confirm-Subscription

$rg = Get-EnvRg $envName
$kv = Get-KeyVaultName $envName

# Must match functions.bicep's searchKeySecretName default: the secret-scoped role assignment is built on it.
$secret = 'search-provider-key'

Write-Log "Storing the search provider key in $kv/$secret..."
$id = az keyvault secret set --vault-name $kv --name $secret --value $Key --query id -o tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($id)) {
    Die "Could not write $secret to $kv. Run this BEFORE harden.ps1 - harden closes the vault's public access."
}

# Drop the version segment. An unversioned SecretUri lets App Service pick a rotated key up on its own, so
# rotating the key is a re-run of THIS script - not another Bicep redeploy.
$uri = $id.Substring(0, $id.LastIndexOf('/'))

Write-Log "Secret URI: $uri"
Write-Warn 'Wire it in on the next deploy:'
Write-Warn "  .\deploy.ps1 $envName -Parameters @('proxySearchKeySecretUri=$uri', 'deploySearchKeyRbac=true')"
Write-Warn 'deploySearchKeyRbac=true is what grants the proxy read on this one secret. It defaults to false because'
Write-Warn 'on a fresh subscription the secret does not exist yet, and a grant scoped to a missing secret fails the deploy.'
Write-Log 'Re-run harden.ps1 afterwards: a deploy re-opens what harden closed.'
