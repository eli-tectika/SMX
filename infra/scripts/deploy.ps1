<#
.SYNOPSIS
  Deploy an SMX environment (subscription-scoped Bicep). PowerShell twin of deploy.sh.
.EXAMPLE
  .\deploy.ps1 dev
  .\deploy.ps1 dev -Parameters @('frontendImage=acr....azurecr.io/smx-frontend:abc1234')
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string]$Environment,
    # Extra Bicep parameters as 'name=value' - e.g. the three container image tags.
    [string[]]$Parameters = @()
)

. "$PSScriptRoot\lib.ps1"

$envName = Require-EnvArg $Environment
Ensure-Bicep
Confirm-Subscription

$deployerIp = Require-DeployerIp
$deployName = "smx-$envName-$(Get-Date -Format 'yyyyMMddHHmmss')"
Write-Log "Deploying env '$envName' to $($script:Location) (deployer IP: $deployerIp)..."

$azArgs = @(
    'deployment', 'sub', 'create',
    '--name', $deployName,
    '--location', $script:Location,
    '--template-file', (Join-Path $script:InfraDir 'main.bicep'),
    '--parameters', (Join-Path $script:InfraDir "env\$envName.bicepparam"),
    '--parameters', "deployerIpAddress=$deployerIp"
)
foreach ($p in $Parameters) { $azArgs += @('--parameters', $p) }

Invoke-Native az @azArgs

Write-Log "Deploy '$deployName' complete."
az deployment sub show --name $deployName --query 'properties.outputs' -o json
