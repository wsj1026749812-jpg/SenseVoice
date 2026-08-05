$ErrorActionPreference = "Stop"
$pidPath = Join-Path (Split-Path -Parent $PSCommandPath) "PiperTtsLite.Server.pid"
if (-not (Test-Path -LiteralPath $pidPath)) {
    Write-Host "Piper TTS Lite is not running."
    return
}

$processId = Get-Content -LiteralPath $pidPath -Raw
$process = Get-Process -Id $processId -ErrorAction SilentlyContinue
if ($process) {
    Stop-Process -Id $processId -Force
    Write-Host "Stopped Piper TTS Lite (PID $processId)."
}
Remove-Item -LiteralPath $pidPath -Force
