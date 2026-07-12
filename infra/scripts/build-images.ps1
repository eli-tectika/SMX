<#
.SYNOPSIS
  Build + push the frontend, backend and orchestrator images in ACR (cloud build, no local
  Docker). Twin of build-images.sh.
.EXAMPLE
  .\build-images.ps1 dev
  .\build-images.ps1 dev -Tag 1.2.3
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string]$Environment,
    # Image tag; defaults to the current short git SHA.
    [string]$Tag,
    # Stream ACR build logs. Off by default: az pipes them through colorama, which encodes to
    # the console codepage, so vite's "OK" checkmark crashes the CLI on a non-UTF-8 console
    # (e.g. cp1255) *after* the cloud build has already succeeded.
    [switch]$Logs
)

. "$PSScriptRoot\lib.ps1"

$envName = Require-EnvArg $Environment
Confirm-Subscription
Require-Command az
Require-Command git

$repoRoot = (Resolve-Path (Join-Path $script:InfraDir '..')).Path
$srcDir = Join-Path $repoRoot 'src'
if (-not $Tag) {
    $Tag = (git -C $repoRoot rev-parse --short HEAD)
    if ($LASTEXITCODE -ne 0) { Die 'git rev-parse failed; pass -Tag explicitly.' }
}

$acr = Get-AcrName $envName
if (-not $Logs) { Write-Warn 'Building with --no-logs (pass -Logs to stream them).' }

# The SPA's build context is its own directory: its Dockerfile COPYs package.json from the
# context root. The two .NET images need the whole src/ tree.
$images = @(
    @{ App = 'frontend';     Dockerfile = "$srcDir\smx-web\Dockerfile";          Context = "$srcDir\smx-web" },
    @{ App = 'backend';      Dockerfile = "$srcDir\Smx.Backend\Dockerfile";      Context = $srcDir },
    @{ App = 'orchestrator'; Dockerfile = "$srcDir\Smx.Orchestrator\Dockerfile"; Context = $srcDir }
)

Write-Log "Building images in $acr (tag $Tag)"
foreach ($i in $images) {
    Write-Log "az acr build $($i.App) -> smx-$($i.App):$Tag"
    $azArgs = @(
        'acr', 'build', '--registry', $acr,
        '--image', "smx-$($i.App):$Tag",
        '--file', $i.Dockerfile
    )
    if (-not $Logs) { $azArgs += '--no-logs' }
    $azArgs += @('-o', 'none', $i.Context)
    Invoke-Native az @azArgs
}

Write-Log 'images:'
foreach ($i in $images) { Write-Log "  $acr.azurecr.io/smx-$($i.App):$Tag" }
Write-Log "roll out with: .\deploy.ps1 $envName -Parameters @('frontendImage=$acr.azurecr.io/smx-frontend:$Tag', ...)"
