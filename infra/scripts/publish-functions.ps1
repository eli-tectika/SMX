<#
.SYNOPSIS
  Build + zip-deploy Smx.Functions (SDS / Reg / Reference) to the regsync Function App.
  Twin of publish-functions.sh. Keyless - uses the caller's `az` login.
.EXAMPLE
  .\publish-functions.ps1 dev
#>
[CmdletBinding()]
param([Parameter(Mandatory, Position = 0)][string]$Environment)

. "$PSScriptRoot\lib.ps1"

$envName = Require-EnvArg $Environment
Confirm-Subscription
Require-Command dotnet

$rg = Get-EnvRg $envName
$app = Get-RegSyncApp $envName
$proj = Join-Path $script:InfraDir '..\src\Smx.Functions'
$out = Join-Path ([IO.Path]::GetTempPath()) ("smx-publish-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$publishDir = Join-Path $out 'publish'
$zip = Join-Path $out 'sds-functions.zip'

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

Write-Log "Published SDS functions to $app. (Run configure-auth.ps1 next, then harden.ps1.)"
