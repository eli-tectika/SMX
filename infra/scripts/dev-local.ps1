<#
.SYNOPSIS
  Run the SMX stack locally. Twin of dev-local.sh.
.DESCRIPTION
  Services (ports match vite.config.ts's proxy and launchSettings.json):
    azurite   10000-10002   storage emulator; only Smx.Functions needs it
    backend   5169          dotnet watch
    web       5173          vite; proxies /api -> :5169

  The orchestrator and Smx.Functions are NOT started: both need AI Search + Foundry, which
  harden puts behind private endpoints - unreachable from a laptop. Run them in Azure.
.EXAMPLE
  .\dev-local.ps1 up
  .\dev-local.ps1 logs backend
  .\dev-local.ps1 down
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][ValidateSet('up', 'down', 'status', 'restart', 'logs')][string]$Command = 'up',
    [Parameter(Position = 1)][ValidateSet('azurite', 'backend', 'web')][string]$Service
)

. "$PSScriptRoot\lib.ps1"

$repoRoot = (Resolve-Path (Join-Path $script:InfraDir '..')).Path
$runDir = Join-Path $repoRoot '.dev-local'
$logDir = Join-Path $runDir 'logs'
$pidDir = Join-Path $runDir 'pids'
$services = @('azurite', 'backend', 'web')
$ports = @{ azurite = 10000; backend = 5169; web = 5173 }

$envFile = Join-Path $runDir 'backend.env'

New-Item -ItemType Directory -Path $logDir, $pidDir -Force | Out-Null

# backend.env is plain KEY=value (shared with the .sh twin). Start-Process gives the child a
# copy of THIS process's environment, so setting them here is what hands them to the backend.
function Import-BackendEnv {
    if (-not (Test-Path $envFile)) { return }
    foreach ($line in Get-Content $envFile) {
        if ($line -match '^\s*(#|$)') { continue }
        $k, $v = $line -split '=', 2
        if ($k -and $null -ne $v) { [Environment]::SetEnvironmentVariable($k.Trim(), $v.Trim(), 'Process') }
    }
}

function Get-ServicePid {
    param([string]$Svc)
    $f = Join-Path $pidDir "$Svc.pid"
    if (-not (Test-Path $f)) { return $null }
    $procId = (Get-Content $f -Raw).Trim()
    if (-not $procId) { return $null }
    $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if ($p) { return [int]$procId }
    return $null
}

# An npm global install drops three shims (azurite, azurite.cmd, azurite.ps1). `cmd /c azurite`
# does not reliably resolve them, so hand Start-Process the .cmd's full path.
function Get-Shim {
    param([string]$Name)
    $c = Get-Command $Name -All -ErrorAction SilentlyContinue | Where-Object { $_.Source -like '*.cmd' } | Select-Object -First 1
    if ($c) { return $c.Source }
    return $null
}

function Start-One {
    param([string]$Svc)
    if (Get-ServicePid $Svc) { Write-Log "$Svc already running (pid $(Get-ServicePid $Svc))"; return }

    $log = Join-Path $logDir "$Svc.log"
    # Start-Process cannot redirect stdout and stderr to the same file, so stderr gets its own.
    $errLog = Join-Path $logDir "$Svc.err.log"
    $p = $null

    switch ($Svc) {
        'azurite' {
            $shim = Get-Shim 'azurite'
            if (-not $shim) { Write-Warn 'azurite not installed (npm i -g azurite); skipping.'; return }
            $p = Start-Process -FilePath $shim `
                -ArgumentList '--silent', '--location', (Join-Path $runDir 'azurite') `
                -RedirectStandardOutput $log -RedirectStandardError $errLog `
                -WindowStyle Hidden -PassThru
        }
        'backend' {
            Import-BackendEnv
            # UseAppHost=false: launching the generated Smx.Backend.exe dies with "Access is
            # denied" under AppLocker; without an apphost, `dotnet run` executes the dll directly.
            $p = Start-Process -FilePath 'dotnet' `
                -ArgumentList 'watch', 'run', '--non-interactive', '--property:UseAppHost=false' `
                -WorkingDirectory (Join-Path $repoRoot 'src\Smx.Backend') `
                -RedirectStandardOutput $log -RedirectStandardError $errLog `
                -WindowStyle Hidden -PassThru
        }
        'web' {
            $webDir = Join-Path $repoRoot 'src\smx-web'
            $npm = Get-Shim 'npm'
            if (-not $npm) { Die 'npm not found.' }
            if (-not (Test-Path (Join-Path $webDir 'node_modules'))) {
                Write-Log 'Installing web dependencies...'
                Invoke-Native $npm 'install' '--prefix' $webDir
            }
            $p = Start-Process -FilePath $npm -ArgumentList 'run', 'dev' `
                -WorkingDirectory $webDir `
                -RedirectStandardOutput $log -RedirectStandardError $errLog `
                -WindowStyle Hidden -PassThru
        }
    }
    if ($p) {
        Set-Content -Path (Join-Path $pidDir "$Svc.pid") -Value $p.Id -Encoding ascii
        Write-Log "started $Svc (pid $($p.Id), port $($ports[$Svc]), log .dev-local/logs/$Svc.log)"
    }
}

function Stop-One {
    param([string]$Svc)
    $procId = Get-ServicePid $Svc
    if ($procId) {
        # /T kills the whole tree: cmd.exe -> npm -> node, and dotnet watch -> dotnet run.
        # Killing only the parent leaves the child holding the port.
        & taskkill /PID $procId /T /F 2>$null | Out-Null
        Write-Log "stopped $Svc (pid $procId)"
    }
    Remove-Item (Join-Path $pidDir "$Svc.pid") -ErrorAction SilentlyContinue
}

switch ($Command) {
    'up' {
        if (-not (Test-Path $envFile)) {
            Write-Warn 'No .dev-local\backend.env - run .\dev-local-setup.ps1 first, or the backend starts without Cosmos.'
        }
        foreach ($s in $services) { Start-One $s }
        Write-Log ''
        Write-Log '  web      http://localhost:5173'
        Write-Log '  backend  http://localhost:5169/healthz'
        Write-Log 'Tail logs with: .\dev-local.ps1 logs [azurite|backend|web]'
    }
    'down' { foreach ($s in $services) { Stop-One $s } }
    'restart' {
        foreach ($s in $services) { Stop-One $s }
        foreach ($s in $services) { Start-One $s }
    }
    'status' {
        foreach ($s in $services) {
            $procId = Get-ServicePid $s
            if ($procId) { Write-Host ('  {0,-8} up    (pid {1}, port {2})' -f $s, $procId, $ports[$s]) }
            else { Write-Host ('  {0,-8} down' -f $s) }
        }
    }
    'logs' {
        if (-not $Service) { Die 'logs needs a service: .\dev-local.ps1 logs [azurite|backend|web]' }
        $log = Join-Path $logDir "$Service.log"
        if (-not (Test-Path $log)) { Die "No log yet for '$Service' - is it running?" }
        Get-Content $log -Tail 50 -Wait
    }
}
