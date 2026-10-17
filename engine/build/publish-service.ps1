# Exo Shaders - unofficial fork of SCSKiller, based on SCSKiller. GPL-3.0-or-later (see LICENSE, LICENSE-EXCEPTION.txt).
#
# Publishes the engine service self-contained (win-x64) to engine\out\service, in the layout ScsKiller expects next to
# its exe:
#   ExoShaders.Service.exe (+ .NET runtime)
#   zlib-ng2.dll                    Codecs seed it from AppContext.BaseDirectory into <data>\codecs
#   native\d3d12.dll                the recorder (NativeTools.Find: <exe dir>\native\<file>)
#   native\amd_ags_x64.dll          AGS app-name registration for AMD compiles
#   native\scskiller_ags.dll        the recorder for games whose D3D12 device AGS creates (AMD)
#   native\scskiller_warm.exe       the compile process
#   native\segheap\scskiller_warm.exe
#   scskiller.exe, scskillerw.exe   beside the service, sharing its one .NET runtime: ScheduledTask.TaskExe (the
#                                   "re-warm after a driver update" task) and Elevated.CliExe (cache size / Auto
#                                   Shader Compilation, run as administrator)
# The native binaries aren't built from this repo: they're taken from SCSKiller 1.3.0-beta.2 (prebuilt\SOURCE.md).
#
#   pwsh build\publish-service.ps1 [-NativeFrom <SCSKiller.App\current>] [-Out <dir>] [-SkipCli]

[CmdletBinding()]
param(
    # The beta's own native files (they can't be rebuilt from source), kept in prebuilt\.
    [string]$NativeFrom = (Join-Path $PSScriptRoot '..\prebuilt'),
    [string]$Out,
    [switch]$SkipCli
)

$ErrorActionPreference = 'Stop'
$engine = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $engine 'out\service' }
$Out = [IO.Path]::GetFullPath($Out)

$natives = @(
    'native\d3d12.dll',
    'native\amd_ags_x64.dll',
    'native\scskiller_ags.dll',
    'native\scskiller_warm.exe',
    'native\segheap\scskiller_warm.exe'
)
$zlibCandidates = @('zlib-ng2.dll', 'cli\zlib-ng2.dll')

foreach ($n in $natives) {
    if (-not (Test-Path (Join-Path $NativeFrom $n))) { throw "missing $n in $NativeFrom (pass -NativeFrom <installed SCSKiller.App\current>)" }
}
$zlib = $zlibCandidates | ForEach-Object { Join-Path $NativeFrom $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $zlib) { throw "zlib-ng2.dll not found in $NativeFrom" }

function Invoke-Dotnet([string[]]$Arguments) {
    Write-Host "dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed ($LASTEXITCODE)" }
}

if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force $Out | Out-Null

# The service
Invoke-Dotnet @('publish', (Join-Path $engine 'src\ExoShaders.Service\ExoShaders.Service.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $Out, '-p:DebugType=none', '-p:PublishReadyToRun=true')

# The CLI the engine starts for the scheduled task and elevated driver settings: published into the same folder, so
# all three programs run on one copy of the .NET runtime (same version, same SCSKiller.Core).
if (-not $SkipCli) {
    $cliProj = Join-Path $engine 'src\SCSKiller.Cli\SCSKiller.Cli.csproj'
    Invoke-Dotnet @('publish', $cliProj, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $Out, '-p:DebugType=none', '-p:PublishReadyToRun=true')
    # scskillerw.exe: the same program without a console window (what the scheduled task runs), same dependencies
    Invoke-Dotnet @('publish', (Join-Path $engine 'src\SCSKiller.CliW\SCSKiller.CliW.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $Out, '-p:DebugType=none', '-p:PublishReadyToRun=true')
}

# Native files, in the app's layout
foreach ($n in $natives) {
    $to = Join-Path $Out $n
    New-Item -ItemType Directory -Force (Split-Path -Parent $to) | Out-Null
    Copy-Item (Join-Path $NativeFrom $n) $to -Force
}
Copy-Item $zlib (Join-Path $Out 'zlib-ng2.dll') -Force

# Licence and notices (GPL-3.0 with SCSKiller's additional permission), and the protocol contract
foreach ($f in 'LICENSE', 'LICENSE-EXCEPTION.txt', 'THIRD-PARTY-NOTICES.md', 'CONTRACT.md') {
    Copy-Item (Join-Path $engine $f) (Join-Path $Out $f) -Force
}

$size = (Get-ChildItem -Recurse -File $Out | Measure-Object -Sum Length).Sum
Write-Host ("published to {0} ({1:N0} MB)" -f $Out, ($size / 1MB))
