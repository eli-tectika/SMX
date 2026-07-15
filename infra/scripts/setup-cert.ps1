<#
.SYNOPSIS
  Guided, re-runnable checklist for wiring KeyVault-Acmebot (github.com/shibayan/keyvault-acmebot) to
  this environment's Key Vault + Azure DNS zone, so it can issue/auto-renew a free Let's Encrypt cert
  via DNS-01 and the App Gateway (Task A3) can auto-rotate from the vault. Twin of setup-cert.sh.
  Acmebot itself is deployed separately (its own ARM template + web UI) - this script is NOT a
  substitute for that. It resolves this env's resource names, prints the ordered steps, and runs the
  verification/extraction commands once you have enough of the puzzle in hand. Safe to re-run.
.PARAMETER Environment
  'dev' or 'prod'.
.PARAMETER AcmebotApp
  Function App name you deployed Acmebot as (you choose it in Acmebot's Deploy-to-Azure form; there
  is no fixed naming convention to derive it from). Needed from Step 2 onward.
.PARAMETER CertHost
  FQDN to issue the cert for, e.g. dev.smxmarkers.io. Needed from Step 3 onward.
.PARAMETER CertName
  Certificate name inside Key Vault (Acmebot's UI shows/lets you set this). Needed for Step 5.
.EXAMPLE
  .\setup-cert.ps1 dev
.EXAMPLE
  .\setup-cert.ps1 dev func-smx-dev-acmebot-swc dev.smxmarkers.io dev-smxmarkers-io
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string]$Environment,
    [Parameter(Position = 1)][string]$AcmebotApp = '',
    [Parameter(Position = 2)][string]$CertHost = '',
    [Parameter(Position = 3)][string]$CertName = ''
)

. "$PSScriptRoot\lib.ps1"

$envName = Require-EnvArg $Environment
Confirm-Subscription

$rg = Get-EnvRg $envName
$kv = Get-KeyVaultName $envName

Write-Log "=== KeyVault-Acmebot cert setup checklist: $envName ==="
Write-Log "Resource group: $rg"
Write-Log "Key Vault:      $kv"
Write-Host ''

Write-Log "Step 1 - Deploy KeyVault-Acmebot (github.com/shibayan/keyvault-acmebot) if you haven't already."
Write-Log "  Use its README's Deploy-to-Azure button / ARM template. Point it at:"
Write-Log "    - Key Vault:  $kv"
Write-Log "    - DNS zone:   the Azure DNS zone backing appDomainName (see infra/env/$envName.bicepparam)"
Write-Log "  Start on Let's Encrypt STAGING - its rate limits are generous, production's are not."
Write-Host ''

Write-Log "Step 2 - Grant Acmebot's identity the role assignments this task added to Bicep."
if ([string]::IsNullOrWhiteSpace($AcmebotApp)) {
    Write-Warn "  No -AcmebotApp given - re-run: .\setup-cert.ps1 $envName <acmebot-function-app-name>"
    Write-Log "  Then this step reads its principal id and prints the redeploy command for you."
}
else {
    Write-Log "  Reading identity of $AcmebotApp..."
    $principalId = az functionapp identity show -g $rg -n $AcmebotApp --query principalId -o tsv 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($principalId)) {
        Write-Warn "  Could not read a principal id for $AcmebotApp in $rg (not deployed yet, or no system-assigned identity)."
        Write-Log "  Manual check:  az functionapp identity show -g $rg -n $AcmebotApp --query principalId -o tsv"
    }
    else {
        Write-Log "  Principal id: $principalId"
        Write-Log "  Redeploy so the DNS Zone Contributor + Key Vault Certificates Officer grants apply:"
        Write-Log "    .\deploy.ps1 $envName -Parameters @('acmebotPrincipalId=$principalId')"
    }
}
Write-Host ''

$acmebotAppShown = if ([string]::IsNullOrWhiteSpace($AcmebotApp)) { '<acmebot-app>' } else { $AcmebotApp }
$hostShown = if ([string]::IsNullOrWhiteSpace($CertHost)) { '<host, e.g. dev.smxmarkers.io>' } else { $CertHost }
Write-Log "Step 3 - Issue the certificate via the Acmebot web UI (https://$acmebotAppShown.azurewebsites.net/)."
Write-Log "  Target host: $hostShown"
Write-Log "  Acmebot writes the DNS-01 TXT challenge into the zone (Step 2's grant) and stores the cert in $kv."
Write-Host ''

Write-Log "Step 4 - Once the STAGING cert issues cleanly, switch Acmebot's Let's Encrypt endpoint app setting"
Write-Log "  to PRODUCTION and re-issue the cert for the same host. Staging certs are not browser-trusted."
Write-Host ''

Write-Log "Step 5 - Verify the cert and extract the versionless secret id for certKeyVaultSecretId."
if ([string]::IsNullOrWhiteSpace($CertName)) {
    Write-Warn "  No -CertName given - re-run: .\setup-cert.ps1 $envName $acmebotAppShown $hostShown <cert-name>"
    Write-Log "  Manual verification:"
    Write-Log "    az keyvault certificate show --vault-name $kv --name <cert-name>"
    Write-Log "  Manual versionless-id extraction:"
    Write-Log "    (az keyvault secret show --vault-name $kv --name <cert-name> --query id -o tsv) -replace '/[^/]+`$', ''"
}
else {
    Write-Log "  Verifying $CertName in $kv..."
    az keyvault certificate show --vault-name $kv --name $CertName -o table 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "  Certificate '$CertName' not found in $kv yet - complete Steps 1-4 first, then re-run this script."
    }
    else {
        $secretId = az keyvault secret show --vault-name $kv --name $CertName --query id -o tsv
        $versionlessId = $secretId.Substring(0, $secretId.LastIndexOf('/'))
        Write-Log "  Versionless secret id: $versionlessId"
        Write-Log "  Paste it in and redeploy:"
        Write-Log "    .\deploy.ps1 $envName -Parameters @('certKeyVaultSecretId=$versionlessId')"
    }
}
