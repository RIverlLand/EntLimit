param([switch]$MonitorOnly)
$ErrorActionPreference = 'Stop'
$app = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/app'))
$monitor = Join-Path $app 'EntLimit.Monitor.exe'
$desktop = Join-Path $app 'EntLimit.Desktop.exe'
if (-not (Test-Path -LiteralPath $monitor)) { throw '请先运行 scripts/build.ps1。' }
Start-Process -FilePath $monitor -WorkingDirectory $app -WindowStyle Hidden
if (-not $MonitorOnly) { Start-Process -FilePath $desktop -WorkingDirectory $app }
