[CmdletBinding()]
param(
    [string]$BindAddress = "127.0.0.1",
    [ValidateRange(1, 65535)]
    [int]$Port = 50200,
    [ValidateRange(10, 180)]
    [int]$StartupTimeoutSeconds = 60,
    [switch]$Replace
)

$ErrorActionPreference = "Stop"
$packageRoot = Split-Path -Parent $PSCommandPath
$server = Join-Path $packageRoot "SherpaStreamingAsr.Server.exe"
$pidPath = Join-Path $packageRoot "SherpaStreamingAsr.Server.pid"
$logDirectory = Join-Path $packageRoot "logs"
$stdoutLog = Join-Path $logDirectory "server.stdout.log"
$stderrLog = Join-Path $logDirectory "server.stderr.log"

if (-not (Test-Path -LiteralPath $server)) {
    throw "Sherpa Streaming ASR server was not found: $server"
}

if (Test-Path -LiteralPath $pidPath) {
    $existingPid = Get-Content -LiteralPath $pidPath -Raw
    $existingProcess = Get-Process -Id $existingPid -ErrorAction SilentlyContinue
    if ($existingProcess) {
        if (-not $Replace) {
            throw "Sherpa Streaming ASR is already running (PID $existingPid). Use -Replace to restart it."
        }
        Stop-Process -Id $existingPid -Force
    }
    Remove-Item -LiteralPath $pidPath -Force
}

$portListener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if ($portListener) {
    throw "Port $Port is already in use by process ID $($portListener.OwningProcess). Choose another port with -Port."
}

New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$url = "http://${BindAddress}:${Port}"
$process = Start-Process -FilePath $server -ArgumentList "--url `"$url`"" -WorkingDirectory $packageRoot -WindowStyle Hidden -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -PassThru
Set-Content -LiteralPath $pidPath -Value $process.Id -NoNewline

$healthHost = if ($BindAddress -eq "0.0.0.0") { "127.0.0.1" } else { $BindAddress }
$healthUrl = "http://${healthHost}:${Port}/health"
$deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
do {
    try {
        $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 3
        if ($health.status -eq "ok") {
            Write-Host "Sherpa Streaming ASR is ready on CPU: $healthUrl"
            return
        }
    }
    catch {
        Start-Sleep -Seconds 1
    }
} while ((Get-Date) -lt $deadline)

if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) {
    Stop-Process -Id $process.Id -Force
}
Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
throw "Sherpa Streaming ASR did not become ready. See $stderrLog"
