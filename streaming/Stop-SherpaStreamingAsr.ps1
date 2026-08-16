[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$packageRoot = Split-Path -Parent $PSCommandPath
$pidPath = Join-Path $packageRoot "SherpaStreamingAsr.Server.pid"

if (-not (Test-Path -LiteralPath $pidPath)) {
    Write-Host "Sherpa Streaming ASR is not running."
    return
}

$processId = Get-Content -LiteralPath $pidPath -Raw
$process = Get-Process -Id $processId -ErrorAction SilentlyContinue
if ($process) {
    Stop-Process -Id $processId -Force
    Write-Host "Stopped Sherpa Streaming ASR (PID $processId)."
}
Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
