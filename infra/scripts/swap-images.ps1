<#
.SYNOPSIS
  Repoint a Container App at a real image. Twin of swap-images.sh.
.DESCRIPTION
  STOPGAP ONLY. This mutates the live Container App; it does not touch Bicep, so the next
  deploy.ps1 reconciles the app back to whatever <app>Image parameter it is given (the
  placeholder, if none). Prefer: deploy.ps1 <env> -Parameters @('frontendImage=...').
.EXAMPLE
  .\swap-images.ps1 dev frontend acrsmxdevlmxnb.azurecr.io/smx-frontend:abc1234
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string]$Environment,
    [Parameter(Mandatory, Position = 1)][ValidateSet('frontend', 'backend', 'orchestrator')][string]$App,
    [Parameter(Mandatory, Position = 2)][string]$Image
)

. "$PSScriptRoot\lib.ps1"

$envName = Require-EnvArg $Environment
Confirm-Subscription

$rg = Get-EnvRg $envName
$appName = Get-ContainerApp $envName $App

Write-Log "Updating $appName -> $Image"
Invoke-Native az containerapp update --resource-group $rg --name $appName --image $Image --output none
Write-Log 'Done. New revision is rolling out.'
Write-Warn "Live-only change: the next deploy.ps1 will revert it unless you pass -Parameters @('$($App)Image=$Image')."
