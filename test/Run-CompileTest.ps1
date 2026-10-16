# Closes Exo normally (never killed), relaunches it with a loopback CDP port, runs
# compile-test.mjs, then closes it and starts it again normally.
param([int]$Port = 9341, [string]$Script = 'compile-test.mjs')
$ErrorActionPreference = 'Stop'
$Exe = Join-Path $env:LOCALAPPDATA 'ExoLauncher\app\ExoLauncher.exe'
function Get-Exo { @(Get-Process ExoLauncher -ErrorAction SilentlyContinue) }
function Close-Exo {
    foreach ($p in Get-Exo) {
        $p.Refresh()
        if (-not $p.CloseMainWindow()) { throw "Exo has no main window to close." }
        if (-not $p.WaitForExit(60000)) { throw "Exo did not exit within 60 s. Not killed." }
    }
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and @(Get-Process msedgewebview2, ExoShaders.Service -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq 'ExoShaders.Service' -or $_.Path -like '*ExoLauncher*' }).Count) { Start-Sleep -Milliseconds 500 }
}
Close-Exo
$psi = [Diagnostics.ProcessStartInfo]::new($Exe)
$psi.UseShellExecute = $false
$psi.WorkingDirectory = Split-Path $Exe
$psi.Environment['WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS'] = "--remote-debugging-port=$Port --remote-debugging-address=127.0.0.1"
[void][Diagnostics.Process]::Start($psi)
try {
    node (Join-Path $PSScriptRoot $Script) $Port
    $code = $LASTEXITCODE
} finally {
    Close-Exo
    Start-Process explorer.exe -ArgumentList "`"$Exe`""
}
exit $code
