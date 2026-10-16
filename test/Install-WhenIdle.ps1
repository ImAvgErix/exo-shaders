# Waits until no game runs (checked every 30 s, then once more after 2 minutes so a
# quick restart of the same game isn't interrupted), then installs the shader add-on
# and the Exo build, and starts Exo.
param([string]$Log = (Join-Path $PSScriptRoot 'install-when-idle.log'))
function Say($t) { Add-Content -Path $Log -Value ("{0:HH:mm:ss} {1}" -f (Get-Date), $t) }
function Gaming { [bool](Get-Process | Where-Object { $_.ProcessName -match '^(VALORANT|VALORANT-Win64-Shipping|League of Legends|cs2)$' -or ($_.Path -and $_.Path -match 'steamapps\\common|Epic Games\\[^L]') }) }
Set-Content $Log ''
Say 'Waiting for games to close.'
while ($true) {
    while (Gaming) { Start-Sleep 30 }
    Start-Sleep 120
    if (-not (Gaming)) { break }
}
Say 'No game running; installing.'
& (Join-Path $PSScriptRoot 'Deploy-Addon.ps1') -Log $Log
Push-Location 'C:\Users\Player\ExoLauncher'
try { pwsh -NoProfile -File tools\swap-dev.ps1 *>> $Log } finally { Pop-Location }
Start-Sleep 5
if (-not (Get-Process ExoLauncher -ErrorAction SilentlyContinue)) {
    Start-Process explorer.exe -ArgumentList "`"$(Join-Path $env:LOCALAPPDATA 'ExoLauncher\app\ExoLauncher.exe')`""
}
Start-Sleep 25
Say ("Running: " + ((Get-Process ExoLauncher, ExoShaders.Service -ErrorAction SilentlyContinue | ForEach-Object ProcessName) -join ', '))
Say 'Finished.'
