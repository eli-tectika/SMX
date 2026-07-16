<#
.SYNOPSIS
  Build + push the frontend, backend and orchestrator images in ACR (cloud build, no local
  Docker). Twin of build-images.sh.
.DESCRIPTION
  Entra SPA auth (optional): Vite inlines import.meta.env.VITE_* at build time, so the SPA
  client id + API scope must be baked into the frontend image's `npm run build`, not passed at
  container runtime. Export these before running this script - SPA_CLIENT_ID and API_CLIENT_ID
  are echoed by configure-auth.ps1, ENTRA_TENANT_ID is your tenant id (az account show --query
  tenantId):
    $env:SPA_CLIENT_ID = '<spa app id>'
    $env:ENTRA_TENANT_ID = '<tenant id>'
    $env:VITE_API_SCOPE = 'api://<api app id>/access_as_user'
  Left unset, all three default to empty and the frontend image builds in today's open mode.
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

# Entra SPA client id + API scope, baked into the frontend image only. Empty values are valid
# --build-arg args: the image builds in open mode, matching today's behavior.
$frontendBuildArgs = @(
    '--build-arg', "VITE_ENTRA_CLIENT_ID=$env:SPA_CLIENT_ID",
    '--build-arg', "VITE_ENTRA_TENANT_ID=$env:ENTRA_TENANT_ID",
    '--build-arg', "VITE_API_SCOPE=$env:VITE_API_SCOPE"
)

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

# The frontend carries both sets: the Entra SPA args (always, empty => open mode) and, only
# for a demo image, ENABLE_DEMO=true.
$images = @(
    @{ App = 'frontend';     Tag = $frontendTag; BuildArgs = ($frontendBuildArgs + $frontendArgs); Dockerfile = "$srcDir\smx-web\Dockerfile";          Context = "$srcDir\smx-web" },
    @{ App = 'backend';      Tag = $Tag;         BuildArgs = @();                                  Dockerfile = "$srcDir\Smx.Backend\Dockerfile";      Context = $srcDir },
    @{ App = 'orchestrator'; Tag = $Tag;         BuildArgs = @();                                  Dockerfile = "$srcDir\Smx.Orchestrator\Dockerfile"; Context = $srcDir }
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
