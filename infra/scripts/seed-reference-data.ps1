<#
.SYNOPSIS
  Upload the reference workbooks to Bronze (lineage) and invoke SeedReferenceData.
  Twin of seed-reference-data.sh.
.DESCRIPTION
  Run AFTER publish-functions + configure-auth, BEFORE harden - it needs public reach to the
  storage account and the Function App. The caller needs "Storage Blob Data Contributor".
.EXAMPLE
  .\seed-reference-data.ps1 dev
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string]$Environment,
    [string]$DatasetVersion
)

. "$PSScriptRoot\lib.ps1"

$envName = Require-EnvArg $Environment
Confirm-Subscription
Require-Command az

$rg = Get-EnvRg $envName
$app = Get-RegSyncApp $envName
$appRegName = Get-AppRegName $envName
$dataDir = Join-Path $script:InfraDir '..\data'
if (-not $DatasetVersion) { $DatasetVersion = Get-EnvOrDefault 'DATASET_VERSION' '2026-07' }

# The env RG holds three storage accounts (bronze medallion + two Flex-Consumption app
# storages); the bronze one is the only with a hierarchical namespace, so select on that.
$storage = az storage account list -g $rg --query '[?isHnsEnabled].name | [0]' -o tsv
if ([string]::IsNullOrWhiteSpace($storage)) { Die "No HNS (bronze) storage account found in $rg" }

Write-Log "Uploading reference workbooks to bronze ($storage) ..."
$uploads = @(
    @{ File = 'SMX Marker Compatibility Knowledge Base.xlsx'; Blob = "reference/compatibility/$DatasetVersion.xlsx" },
    @{ File = 'SMX Marker Suppliers - Comprehensive.xlsx';    Blob = "reference/suppliers/$DatasetVersion.xlsx" }
)
foreach ($u in $uploads) {
    Invoke-Native az storage blob upload --account-name $storage --auth-mode login -c bronze `
        -f (Join-Path $dataDir $u.File) -n $u.Blob --overwrite --output none
}

Write-Log "Invoking SeedReferenceData on $app ..."
$token = az account get-access-token --resource "api://$appRegName" --query accessToken -o tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) { Die 'Failed to acquire an Entra token.' }

try {
    Invoke-RestMethod -Method Post -Uri "https://$app.azurewebsites.net/api/reference/seed" `
        -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' | Out-Null
}
catch {
    Die "Seed call failed: $($_.Exception.Message)"
}

Write-Log 'Reference data seeded. (Cosmos ref-* containers + smx-reference index populated.)'
