# Closes Exo normally (never killed) and installs the built add-on (engine + page).
# Exo is started again by the caller (after its own install), or here with -Start.
param([string]$Log = (Join-Path $PSScriptRoot 'deploy.log'), [switch]$Start)
$ErrorActionPreference = 'Stop'
function Say($t) { Add-Content -Path $Log -Value ("{0:HH:mm:ss} {1}" -f (Get-Date), $t) }
Remove-Item $Log -ErrorAction SilentlyContinue
try {
    foreach ($p in @(Get-Process ExoLauncher -ErrorAction SilentlyContinue)) {
        $p.Refresh()
        if (-not $p.CloseMainWindow()) { throw 'Exo has no main window to close.' }
        if (-not $p.WaitForExit(60000)) { throw 'Exo did not exit in 60 s; not killed.' }
    }
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline -and (Get-Process ExoShaders.Service -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }
    if (Get-Process ExoShaders.Service -ErrorAction SilentlyContinue) { throw 'The engine is still finishing; try again in a minute.' }
    Say 'Exo closed.'
    & (Join-Path $PSScriptRoot '..\install-addon.ps1') -Log $Log
} catch { Say "ERROR: $($_.Exception.Message)" }
finally {
    if ($Start) { Start-Process explorer.exe -ArgumentList "`"$(Join-Path $env:LOCALAPPDATA 'ExoLauncher\app\ExoLauncher.exe')`"" }
    Say 'Done.'
}
