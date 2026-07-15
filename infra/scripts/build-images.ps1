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
    [switch]$Logs,
    # Build the STAKEHOLDER-DEMO frontend: it ships the fixture proj-demo (see
    # src/smx-web/Dockerfile). Tagged '-demo' so it can never be mistaken for a real image, and it
    # must be served from its own origin — never production (MSW registers a service worker at the
    # origin scope; see src/smx-web/src/mocks/demo.ts). Backend/orchestrator images are unaffected.
    [switch]$FrontendDemo
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
# context root. The two .NET images need the whole src/ tree. Tag/BuildArgs are per-image so the
# demo frontend can carry its own '-demo' tag and --build-arg without touching the others.
$frontendTag = $Tag
$frontendArgs = @()
if ($FrontendDemo) {
    Write-Warn "FrontendDemo: building a DEMO frontend (ships fixture proj-demo) as smx-frontend:$Tag-demo. Never serve it from a production origin."
    $frontendTag = "$Tag-demo"
    $frontendArgs = @('--build-arg', 'ENABLE_DEMO=true')
}

$images = @(
    @{ App = 'frontend';     Tag = $frontendTag; BuildArgs = $frontendArgs; Dockerfile = "$srcDir\smx-web\Dockerfile";          Context = "$srcDir\smx-web" },
    @{ App = 'backend';      Tag = $Tag;         BuildArgs = @();           Dockerfile = "$srcDir\Smx.Backend\Dockerfile";      Context = $srcDir },
    @{ App = 'orchestrator'; Tag = $Tag;         BuildArgs = @();           Dockerfile = "$srcDir\Smx.Orchestrator\Dockerfile"; Context = $srcDir }
)

Write-Log "Building images in $acr (tag $Tag)"
foreach ($i in $images) {
    Write-Log "az acr build $($i.App) -> smx-$($i.App):$($i.Tag)"
    $azArgs = @(
        'acr', 'build', '--registry', $acr,
        '--image', "smx-$($i.App):$($i.Tag)",
        '--file', $i.Dockerfile
    )
    $azArgs += $i.BuildArgs
    if (-not $Logs) { $azArgs += '--no-logs' }
    $azArgs += @('-o', 'none', $i.Context)
    Invoke-Native az @azArgs
}

Write-Log 'images:'
foreach ($i in $images) { Write-Log "  $acr.azurecr.io/smx-$($i.App):$($i.Tag)" }
Write-Log "roll out with: .\deploy.ps1 $envName -Parameters @('frontendImage=$acr.azurecr.io/smx-frontend:$frontendTag', ...)"
