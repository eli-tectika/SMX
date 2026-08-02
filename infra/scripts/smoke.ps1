<#
.SYNOPSIS
  Post-deploy smoke check: ACA apps running, functions present, and the app reachable ONLY privately.
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

# Returns the HTTP status, or 0 when nothing answered (refused / timed out / no DNS) - the twin of
# curl's http_code of 000. Invoke-WebRequest THROWS on any non-2xx, so a real status has to be dug out
# of the exception, and lib.ps1's Set-StrictMode makes a blind $_.Exception.Response reference a
# terminating error on the exception types that carry no Response at all (timeout, refused connection).
function Get-HttpStatus {
    param([Parameter(Mandatory)][string]$Uri, [int]$TimeoutSec = 20)
    try {
        # -UseBasicParsing keeps this working on hosts with no IE engine.
        $res = Invoke-WebRequest -Uri $Uri -TimeoutSec $TimeoutSec -UseBasicParsing
        return [int]$res.StatusCode
    }
    catch {
        $ex = $_.Exception
        while ($null -ne $ex) {
            if ($null -ne $ex.PSObject.Properties['Response'] -and $null -ne $ex.Response) {
                return [int]$ex.Response.StatusCode
            }
            $ex = $ex.InnerException
        }
        return 0
    }
}

$pipName = "pip-$($script:NamePrefix)-$envName-agw-$($script:RegionShort)"
$gwIp = az network public-ip show -g $rg -n $pipName --query ipAddress -o tsv 2>$null
if (-not [string]::IsNullOrWhiteSpace($gwIp)) {
    # The public IP stays ALLOCATED (App Gateway v2 wants a public frontend for its control plane) but no
    # listener binds to it. A response here is a REGRESSION, not a success: it means a listener drifted
    # back onto the public frontend and the app is on the internet again. Short timeout - silence is the
    # expected outcome, so there is nothing worth waiting 20s for.
    Write-Log "Probing http://$gwIp/ - expecting NO response (the public listener must be closed)..."
    $code = Get-HttpStatus -Uri "http://$gwIp/" -TimeoutSec 8
    if ($code -eq 0) {
        Write-Log 'Public frontend closed (no response). OK.'
    }
    else {
        Die "PUBLIC FRONTEND IS ANSWERING (HTTP $('{0:000}' -f $code)) at $gwIp - the app is reachable from the internet."
    }
}
else {
    Write-Warn 'App Gateway public IP not found.'
}

# Deliberately asymmetric with the probe above. Public reachability is a security regression and must
# break the build; private unreachability is almost always just "you are not on the VPN", so it only
# warns - and conflating the two would train the operator to ignore the one that matters.
$agwPrivateIp = Get-EnvOrDefault 'AGW_PRIVATE_IP' '10.0.0.10'
Write-Log "Probing http://$agwPrivateIp/ - requires an established VPN tunnel..."
$code = Get-HttpStatus -Uri "http://$agwPrivateIp/" -TimeoutSec 20
if ($code -eq 200) {
    Write-Log 'Private frontend OK (HTTP 200).'
}
else {
    Write-Warn "Private frontend returned HTTP $('{0:000}' -f $code) - connect the VPN client, or the backend is still warming."
}

Write-Log 'NAT egress IP (Functions controlled outbound):'
$natName = "pip-$($script:NamePrefix)-$envName-nat-$($script:RegionShort)"
$natIp = az network public-ip show -g $rg -n $natName --query ipAddress -o tsv 2>$null
if ([string]::IsNullOrWhiteSpace($natIp)) { Write-Warn 'NAT public IP not found.' } else { Write-Host $natIp }
