<#
.SYNOPSIS
  Build + zip-deploy Smx.SearchProxy to the searchproxy Function App.
  Twin of publish-searchproxy.sh. Keyless - uses the caller's `az` login.

  A SEPARATE project from Smx.Functions on purpose: this app's identity has NO corpus RBAC, and
  deploying the SDS/Reg code here would drag Cosmos/Bronze/Search onto the internet-facing app.
.EXAMPLE
  .\publish-searchproxy.ps1 dev
#>
[CmdletBinding()]
param([Parameter(Mandatory, Position = 0)][string]$Environment)

. "$PSScriptRoot\lib.ps1"

$envName = Require-EnvArg $Environment
Confirm-Subscription
Require-Command dotnet

$rg = Get-EnvRg $envName
$app = Get-SearchProxyApp $envName
$proj = Join-Path $script:InfraDir '..\src\Smx.SearchProxy'
$out = Join-Path ([IO.Path]::GetTempPath()) ("smx-proxy-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$publishDir = Join-Path $out 'publish'
$zip = Join-Path $out 'search-proxy.zip'

try {
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
    Write-Log "Publishing $proj -> $app ($rg)"
    Invoke-Native dotnet publish $proj -c Release -o $publishDir
    New-DeploymentZip -SourceDir $publishDir -OutZip $zip
    Invoke-Native az functionapp deployment source config-zip -g $rg -n $app --src $zip --output none
}
finally {
    if (Test-Path $out) { Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Log "Published the Search Proxy to $app."
Write-Log 'Next: set-search-key.ps1 (the key) and configure-auth.ps1 (Easy Auth) - then redeploy to wire both in.'
