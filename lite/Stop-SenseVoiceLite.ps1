$ErrorActionPreference = "Stop"
$pidPath = Join-Path (Split-Path -Parent $PSCommandPath) "SenseVoiceLite.Server.pid"
if (-not (Test-Path -LiteralPath $pidPath)) {
    Write-Host "SenseVoice Lite is not running."
    return
}

$processId = Get-Content -LiteralPath $pidPath -Raw
$process = Get-Process -Id $processId -ErrorAction SilentlyContinue
if ($process) {
    Stop-Process -Id $processId -Force
    Write-Host "Stopped SenseVoice Lite (PID $processId)."
}
Remove-Item -LiteralPath $pidPath -Force
