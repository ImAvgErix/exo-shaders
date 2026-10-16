# Exo Shaders, an unofficial fork based on SCSKiller. GPL-3.0-or-later.
# Installs the built engine and page as Exo Launcher's shader add-on, for this PC only:
#   %LOCALAPPDATA%\ExoLauncher\addons\shaders\engine   (engine\out\service)
#   %LOCALAPPDATA%\ExoLauncher\addons\shaders\ui       (ui\dist)
# Exo picks it up at its next start. A running engine is asked to stop first.
param(
    [string]$Work = $PSScriptRoot,
    [string]$Local = [Environment]::GetFolderPath('LocalApplicationData'),
    [string]$Log,
    # Only the page: safe while Exo and the engine run; Exo shows it at the next open of Shaders.
    [switch]$PageOnly
)
$ErrorActionPreference = 'Stop'
function Say($text) { if ($Log) { Add-Content -Path $Log -Value $text } else { Write-Output $text } }

$engineSource = Join-Path $Work 'engine\out\service'
$pageSource = Join-Path $Work 'ui\dist'
if (-not (Test-Path (Join-Path $engineSource 'ExoShaders.Service.exe'))) { throw "No built engine in $engineSource. Run engine\build\publish-service.ps1." }
if (-not (Test-Path (Join-Path $pageSource 'index.html'))) { throw "No built page in $pageSource. Run npm run build in ui." }

$root = Join-Path $Local 'ExoLauncher\addons\shaders'
$engine = Join-Path $root 'engine'
$page = Join-Path $root 'ui'

if ($PageOnly) {
    New-Item -ItemType Directory -Force -Path $page | Out-Null
    robocopy $pageSource $page /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying the page failed (robocopy $LASTEXITCODE)." }
    Say "Updated the Exo Shaders page in $page."
    exit 0
}

# An engine Exo started finishes its queue when its input closes; wait for it rather than killing it.
$running = Get-Process -Name 'ExoShaders.Service' -ErrorAction SilentlyContinue
if ($running) {
    Say "Exo Shaders' engine is running (Exo is open). Close Exo Launcher, then run this again."
    exit 4
}

New-Item -ItemType Directory -Force -Path $engine, $page | Out-Null
robocopy $engineSource $engine /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying the engine failed (robocopy $LASTEXITCODE)." }
robocopy $pageSource $page /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying the page failed (robocopy $LASTEXITCODE)." }
Say "Installed Exo Shaders to $root."
