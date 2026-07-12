<#
.SYNOPSIS
  Delete an SMX environment resource group (and optionally the shared hub). Twin of teardown.sh.
.EXAMPLE
  .\teardown.ps1 dev
#>
[CmdletBinding()]
param([Parameter(Mandatory, Position = 0)][string]$Environment)

. "$PSScriptRoot\lib.ps1"

$envName = Require-EnvArg $Environment
Confirm-Subscription

$envRg = Get-EnvRg $envName
$hubRg = Get-HubRg
$hubVnet = "vnet-$($script:NamePrefix)-hub-$($script:RegionShort)"
$spokeVnet = "vnet-$($script:NamePrefix)-$envName-$($script:RegionShort)"

# This environment's hub-side peering and per-env private DNS zone links live in the shared hub
# RG. Deleting the spoke RG does NOT remove them, so they must be cleaned up whenever the hub is
# retained, or they dangle pointing at a deleted VNet.
function Remove-HubSideLinks {
    az network vnet peering delete --resource-group $hubRg --vnet-name $hubVnet `
        --name "peer-to-$spokeVnet" 2>$null | Out-Null
    $zones = @(az network private-dns zone list --resource-group $hubRg --query '[].name' -o tsv 2>$null)
    foreach ($zone in $zones) {
        if ([string]::IsNullOrWhiteSpace($zone)) { continue }
        az network private-dns link vnet delete --resource-group $hubRg --zone-name $zone `
            --name "link-$($script:NamePrefix)-$envName" --yes 2>$null | Out-Null
    }
    Write-Log "Cleaned up hub-side peering and DNS links for '$envName'."
}

Write-Warn "This will DELETE resource group: $envRg"
$reply = Read-Host "Type the environment name '$envName' to confirm"
if ($reply -ne $envName) { Die 'Confirmation failed; aborting.' }

Invoke-Native az group delete --name $envRg --yes
Write-Log "Deleted $envRg."

$remaining = @(az group list --query "[?starts_with(name, 'rg-$($script:NamePrefix)-') && name != '$hubRg'].name" -o tsv) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

if ($remaining.Count -eq 0) {
    Write-Warn 'No environment resource groups remain.'
    $hubReply = Read-Host "Delete the shared hub '$hubRg' too? [y/N]"
    if ($hubReply -match '^[yY]$') {
        Invoke-Native az group delete --name $hubRg --yes
        Write-Log "Deleted $hubRg."
    }
    else {
        Remove-HubSideLinks
        Write-Log 'Keeping hub.'
    }
}
else {
    Remove-HubSideLinks
    Write-Log "Keeping hub; still referenced by: $($remaining -join ', ')"
}
