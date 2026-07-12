<#
.SYNOPSIS
  Dry run before a deploy: tooling, Bicep lint, RP registration, what-if. Twin of preflight.sh.
.EXAMPLE
  .\preflight.ps1 dev
#>
[CmdletBinding()]
param([Parameter(Mandatory, Position = 0)][string]$Environment)

. "$PSScriptRoot\lib.ps1"

$envName = Require-EnvArg $Environment

Write-Log 'Checking tooling...'
Ensure-Bicep

Write-Log 'Linting Bicep (offline)...'
Invoke-Native az bicep build --file (Join-Path $script:InfraDir 'main.bicep') --stdout | Out-Null
Write-Log 'Bicep OK.'

Write-Log 'Checking Azure login + subscription...'
Confirm-Subscription

Write-Log 'Registering resource providers...'
$providers = @(
    'Microsoft.Network', 'Microsoft.OperationalInsights', 'Microsoft.Insights',
    'Microsoft.Storage', 'Microsoft.DocumentDB', 'Microsoft.Search',
    'Microsoft.CognitiveServices', 'Microsoft.App', 'Microsoft.ContainerRegistry',
    'Microsoft.Web', 'Microsoft.KeyVault', 'Microsoft.ManagedIdentity'
)
foreach ($rp in $providers) {
    Invoke-Native az provider register --namespace $rp | Out-Null
    Write-Log "  registering $rp"
}

$deployerIp = Get-DeployerIp
$shownIp = if ($deployerIp) { $deployerIp } else { '<unknown>' }
Write-Log "Detected deployer IP: $shownIp"

Write-Log "Running what-if for env '$envName'..."
Invoke-Native az deployment sub what-if `
    --location $script:Location `
    --template-file (Join-Path $script:InfraDir 'main.bicep') `
    --parameters (Join-Path $script:InfraDir "env\$envName.bicepparam") `
    --parameters "deployerIpAddress=$deployerIp"

Write-Log 'Preflight complete.'
