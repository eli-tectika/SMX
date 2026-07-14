# Shared helpers for the SMX infra scripts - PowerShell twin of lib.sh.
# Dot-source it:  . "$PSScriptRoot\lib.ps1"

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Log  { param([string]$Message) Write-Host "[smx] $Message" -ForegroundColor Blue }
function Write-Warn { param([string]$Message) Write-Host "[smx][warn] $Message" -ForegroundColor Yellow }
function Write-Err  { param([string]$Message) Write-Host "[smx][err] $Message" -ForegroundColor Red }
function Die        { param([string]$Message) Write-Err $Message; exit 1 }

# Absolute path to infra/ (parent of scripts/).
$script:InfraDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Get-EnvOrDefault {
    param([string]$Name, [string]$Default)
    $v = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($v)) { return $Default }
    return $v
}

# Naming tokens - same env-var overrides as lib.sh.
$script:NamePrefix  = Get-EnvOrDefault 'NAME_PREFIX'  'smx'
$script:RegionShort = Get-EnvOrDefault 'REGION_SHORT' 'swc'
$script:Location    = Get-EnvOrDefault 'LOCATION'     'swedencentral'

# Subscription the SMX estate lives in. `az account set` is global and sticky, so a
# subscription switched in another shell silently follows you here.
$script:SmxSubscriptionId = Get-EnvOrDefault 'SMX_SUBSCRIPTION_ID' '98c6dba9-5088-4d2b-aadc-31b629a308de'

# $ErrorActionPreference='Stop' does NOT trap a native exe's non-zero exit code, so every
# az call that must succeed goes through here.
function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$Exe,
        [Parameter(ValueFromRemainingArguments)][string[]]$Arguments
    )
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { Die "Command failed ($LASTEXITCODE): $Exe $($Arguments -join ' ')" }
}

function Require-Command {
    param([Parameter(Mandatory)][string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) { Die "Required command not found: $Name" }
}

function Require-EnvArg {
    param([string]$Value)
    if ($Value -notin @('dev', 'prod')) {
        $shown = if ([string]::IsNullOrWhiteSpace($Value)) { '<none>' } else { $Value }
        Die "Usage: expected environment 'dev' or 'prod', got '$shown'"
    }
    return $Value
}

function Ensure-Bicep {
    Require-Command az
    az bicep version 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Log 'Installing Bicep...'; Invoke-Native az bicep install }
}

function Confirm-Subscription {
    Require-Command az
    $subId = az account show --query id -o tsv 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($subId)) { Die 'Not logged in. Run: az login' }
    $subName = az account show --query name -o tsv
    if ($subId -ne $script:SmxSubscriptionId) {
        Die @"
Wrong subscription: active is '$subName' ($subId), expected $($script:SmxSubscriptionId).
     Fix with: az account set --subscription $($script:SmxSubscriptionId)
     (or set SMX_SUBSCRIPTION_ID to deploy a different estate on purpose)
"@
    }
    Write-Log "Target subscription: $subName ($subId)"
}

function Get-DeployerIp {
    try {
        $ip = (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 15).ToString().Trim()
        # Azure ipRules reject an IPv6 literal.
        if ($ip -match '^\d{1,3}(\.\d{1,3}){3}$') { return $ip }
        return ''
    }
    catch { return '' }
}

# An empty deployerIpAddress is not a no-op: the Bicep reads it as `empty() ? [] : [rule]`,
# so it strips the firewall allowlist from Cosmos/Storage/Search/Foundry and locks the
# operator out of the data plane. Refuse to deploy on a silent detection failure.
function Require-DeployerIp {
    $ip = if ($env:DEPLOYER_IP) { $env:DEPLOYER_IP } else { Get-DeployerIp }
    if ([string]::IsNullOrWhiteSpace($ip)) {
        Die @'
Could not detect this machine's public IPv4 (needed for the service firewall allowlist).
     Deploying with an empty IP would REMOVE the existing allowlist. Pass it explicitly:
       $env:DEPLOYER_IP = '<your.ip.v4>'
'@
    }
    return $ip
}

# Resource names, derived exactly as the Bicep does.
function Get-EnvRg       { param([string]$Env) "rg-$($script:NamePrefix)-$Env-$($script:RegionShort)" }
function Get-HubRg       { "rg-$($script:NamePrefix)-hub-$($script:RegionShort)" }
function Get-RegSyncApp  { param([string]$Env) "func-$($script:NamePrefix)-$Env-regsync-$($script:RegionShort)" }
function Get-AppRegName  { param([string]$Env) "$($script:NamePrefix)-$Env-regsync-auth" }
function Get-ContainerApp { param([string]$Env, [string]$App) "ca-$($script:NamePrefix)-$Env-$App-$($script:RegionShort)" }

# The Search Proxy is a SEPARATE app with a SEPARATE identity and its OWN app registration - never
# regsync's. Sharing an audience would hand the internet-facing proxy a token the corpus-writing app
# accepts, which is exactly the boundary the two identities exist to hold.
function Get-SearchProxyApp   { param([string]$Env) "func-$($script:NamePrefix)-$Env-searchproxy-$($script:RegionShort)" }
function Get-ProxyAppRegName  { param([string]$Env) "$($script:NamePrefix)-$Env-searchproxy-auth" }

# The Key Vault name carries a Bicep uniqueString() suffix (security.bicep:
# kv-${namePrefix}-${env}-${uniqueSuffix}), seeded from the subscription id here and the resource-group
# id in the single-rg variant - so discover it rather than reconstruct it.
function Get-KeyVaultName {
    param([Parameter(Mandatory)][string]$Env)
    $rg = Get-EnvRg $Env
    $prefix = "kv-$($script:NamePrefix)-$Env-"
    $name = az keyvault list -g $rg --query "[?starts_with(name, '$prefix')].name | [0]" -o tsv
    if ([string]::IsNullOrWhiteSpace($name)) { Die "No Key Vault found in $rg with prefix '$prefix' (is env '$Env' deployed?)" }
    return $name
}

# ACR name carries a Bicep uniqueString() suffix, so discover it rather than reconstruct it.
function Get-AcrName {
    param([Parameter(Mandatory)][string]$Env)
    $prefix = "acr$($script:NamePrefix)$Env"
    $name = az acr list --query "[?starts_with(name, '$prefix')].name | [0]" -o tsv
    if ([string]::IsNullOrWhiteSpace($name)) { Die "No ACR found with prefix '$prefix' (is env '$Env' deployed?)" }
    return $name
}

# Package a publish folder for zip-deploy. .NET's ZipFile/Compress-Archive writes entry
# names with backslashes on Windows PowerShell - off-spec, and Kudu then cannot recreate
# nested directories - so shell out to the bsdtar in System32, which writes '/'.
function New-DeploymentZip {
    param([Parameter(Mandatory)][string]$SourceDir, [Parameter(Mandatory)][string]$OutZip)
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path $tar)) { Die "bsdtar not found at $tar; cannot package the app." }
    # '.' rather than a '*' glob: it keeps the hidden .azurefunctions/ directory.
    Invoke-Native $tar '-a' '-c' '-f' $OutZip '-C' $SourceDir '.'
    if (-not (Test-Path $OutZip) -or (Get-Item $OutZip).Length -eq 0) {
        Die "Packaging produced an empty archive: $OutZip"
    }
}
