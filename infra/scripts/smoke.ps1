<#
.SYNOPSIS
  Post-deploy smoke check: ACA apps running, functions present, gateway reachable.
  Twin of smoke.sh.
.EXAMPLE
  .\smoke.ps1 dev
#>
[CmdletBinding()]
param([Parameter(Mandatory, Position = 0)][string]$Environment)

. "$PSScriptRoot\lib.ps1"

$envName = Require-EnvArg $Environment
Confirm-Subscription
$rg = Get-EnvRg $envName

Write-Log 'ACA apps:'
az containerapp list -g $rg --query '[].{name:name, running:properties.runningStatus}' -o table

Write-Log 'Function apps:'
az functionapp list -g $rg --query '[].{name:name, state:state}' -o table

$pipName = "pip-$($script:NamePrefix)-$envName-agw-$($script:RegionShort)"
$gwIp = az network public-ip show -g $rg -n $pipName --query ipAddress -o tsv 2>$null
if (-not [string]::IsNullOrWhiteSpace($gwIp)) {
    Write-Log "App Gateway public IP: $gwIp - probing http://$gwIp/ ..."
    try {
        # -UseBasicParsing keeps this working on hosts with no IE engine.
        $res = Invoke-WebRequest -Uri "http://$gwIp/" -TimeoutSec 20 -UseBasicParsing
        if ($res.StatusCode -eq 200) { Write-Log "Gateway OK (HTTP 200)." }
        else { Write-Warn "Gateway returned HTTP $($res.StatusCode) (backend may still be warming)." }
    }
    catch {
        Write-Warn "Gateway probe failed: $($_.Exception.Message)"
    }
}
else {
    Write-Warn 'App Gateway public IP not found.'
}

Write-Log 'NAT egress IP (Functions controlled outbound):'
$natName = "pip-$($script:NamePrefix)-$envName-nat-$($script:RegionShort)"
$natIp = az network public-ip show -g $rg -n $natName --query ipAddress -o tsv 2>$null
if ([string]::IsNullOrWhiteSpace($natIp)) { Write-Warn 'NAT public IP not found.' } else { Write-Host $natIp }
