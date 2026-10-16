# Closes Exo normally, uninstalls SCSKiller's app with its own uninstaller (Velopack),
# then starts Exo again. SCSKiller's data folder (%LOCALAPPDATA%\SCSKiller) is kept.
param([string]$Log = (Join-Path $PSScriptRoot 'uninstall.log'))
$ErrorActionPreference = 'Stop'
function Say($t) { Add-Content -Path $Log -Value ("{0:HH:mm:ss} {1}" -f (Get-Date), $t) }
Remove-Item $Log -ErrorAction SilentlyContinue
$Exe = Join-Path $env:LOCALAPPDATA 'ExoLauncher\app\ExoLauncher.exe'
$update = Join-Path $env:LOCALAPPDATA 'SCSKiller.App\Update.exe'
try {
    foreach ($p in @(Get-Process ExoLauncher -ErrorAction SilentlyContinue)) {
        $p.Refresh()
        if (-not $p.CloseMainWindow()) { throw 'Exo has no main window to close.' }
        if (-not $p.WaitForExit(60000)) { throw 'Exo did not exit in 60 s; not killed.' }
    }
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and (Get-Process ExoShaders.Service -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }
    Say "Exo closed; engine running: $([bool](Get-Process ExoShaders.Service -ErrorAction SilentlyContinue))"
    if (Get-Process SCSKiller -ErrorAction SilentlyContinue) { throw 'SCSKiller is running; not uninstalling.' }
    if (Test-Path $update) {
        $u = Start-Process -FilePath $update -ArgumentList '--uninstall', '--silent' -PassThru -Wait
        Say "SCSKiller uninstaller exit code: $($u.ExitCode)"
        Start-Sleep 3
        Say "SCSKiller.App folder left: $(Test-Path (Join-Path $env:LOCALAPPDATA 'SCSKiller.App\current'))"
    } else { Say 'SCSKiller is not installed.' }
    Say "Data folder kept: $(Test-Path (Join-Path $env:LOCALAPPDATA 'SCSKiller\games.json'))"
} catch { Say "ERROR: $($_.Exception.Message)" }
finally {
    Start-Process explorer.exe -ArgumentList "`"$Exe`""
    Say 'Exo started.'
}
