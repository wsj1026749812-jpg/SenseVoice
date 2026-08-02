[CmdletBinding()]
param(
    [ValidateSet("cpu", "gpu")]
    [string]$Mode = "cpu",
    [string]$ImageArchive,
    [string]$ImageName = "sensevoice:offline",
    [string]$ContainerName = "sensevoice",
    [string]$BindAddress = "127.0.0.1",
    [ValidateRange(1, 65535)]
    [int]$Port = 50000,
    [ValidateRange(30, 900)]
    [int]$StartupTimeoutSeconds = 300,
    [string]$ExpectedArchiveSha256,
    [switch]$Replace
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $PSCommandPath
if ([string]::IsNullOrWhiteSpace($ImageArchive)) {
    $ImageArchive = Join-Path $scriptRoot "sensevoice-offline-rootfs.tar"
}

function Invoke-Docker {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Docker command failed: docker $($Arguments -join ' ')"
    }
}

function Join-ArchiveParts {
    param([Parameter(Mandatory = $true)][string]$ArchivePath)

    $parts = @(Get-ChildItem -Path "${ArchivePath}.part*" -File | Sort-Object Name)
    if ($parts.Count -eq 0) {
        throw "Offline image archive was not found: $ArchivePath. Download the archive or every matching .partNNN file into this directory."
    }

    $temporaryPath = "${ArchivePath}.assembling"
    Write-Host "Assembling $($parts.Count) offline image archive parts..."

    $destination = [System.IO.File]::Open($temporaryPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        foreach ($part in $parts) {
            Write-Host "  Adding $($part.Name)"
            $source = [System.IO.File]::OpenRead($part.FullName)
            try {
                $source.CopyTo($destination, 8MB)
            }
            finally {
                $source.Dispose()
            }
        }
    }
    finally {
        $destination.Dispose()
    }

    Move-Item -LiteralPath $temporaryPath -Destination $ArchivePath -Force
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker CLI was not found. Install and start Docker Desktop in Linux containers mode first."
}
if (-not (Test-Path -LiteralPath $ImageArchive)) {
    Join-ArchiveParts -ArchivePath $ImageArchive
}
if (-not (Test-Path -LiteralPath $ImageArchive)) {
    throw "Offline image archive was not found: $ImageArchive"
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedArchiveSha256)) {
    $actualHash = (Get-FileHash -LiteralPath $ImageArchive -Algorithm SHA256).Hash
    if ($actualHash -ne $ExpectedArchiveSha256.ToUpperInvariant()) {
        throw "Offline image archive checksum mismatch. Expected $ExpectedArchiveSha256, got $actualHash."
    }
}

Invoke-Docker -Arguments @("version")

$existing = & docker ps -a --filter "name=^/$ContainerName$" --format "{{.ID}}"
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect existing Docker containers."
}
if ($existing) {
    if (-not $Replace) {
        throw "Container '$ContainerName' already exists. Use -Replace to recreate it."
    }
    Invoke-Docker -Arguments @("rm", "--force", $ContainerName)
}

# Docker Desktop's containerd image store can emit incomplete `docker save`
# archives on some Windows hosts. This package uses a flattened rootfs archive
# and recreates the small amount of image configuration during import.
$imageChanges = @(
    "ENV PATH=/usr/local/nvidia/bin:/usr/local/cuda/bin:/opt/conda/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
    "ENV NVIDIA_VISIBLE_DEVICES=all",
    "ENV NVIDIA_DRIVER_CAPABILITIES=compute,utility",
    "ENV LD_LIBRARY_PATH=/usr/local/nvidia/lib:/usr/local/nvidia/lib64",
    "ENV PYTORCH_VERSION=2.5.1",
    "ENV DEBIAN_FRONTEND=noninteractive",
    "ENV PYTHONUNBUFFERED=1",
    "ENV PIP_NO_CACHE_DIR=1",
    "ENV SENSEVOICE_DEVICE=auto",
    "ENV SENSEVOICE_MODEL=/models/sensevoice-small",
    "ENV SENSEVOICE_MODEL_ID=iic/SenseVoiceSmall",
    "ENV SENSEVOICE_VAD_MODEL=/models/fsmn-vad",
    "ENV SENSEVOICE_VAD_MODEL_ID=iic/speech_fsmn_vad_zh-cn-16k-common-pytorch",
    "ENV MODEL_CACHE=/models/cache",
    "ENV MODELSCOPE_CACHE=/models/cache",
    "ENV HF_HOME=/models/huggingface",
    "ENV TMPDIR=/tmp",
    "WORKDIR /app",
    "EXPOSE 50000",
    'ENTRYPOINT [\"/usr/local/bin/sensevoice-entrypoint\"]',
    'CMD [\"uvicorn\", \"app.main:app\", \"--host\", \"0.0.0.0\", \"--port\", \"50000\"]'
)
$importArguments = @("import")
foreach ($change in $imageChanges) {
    $importArguments += @("--change", $change)
}
$importArguments += @($ImageArchive, $ImageName)
Invoke-Docker -Arguments $importArguments

$volumeName = "$ContainerName-models"
Invoke-Docker -Arguments @("volume", "create", $volumeName)

$runArguments = @(
    "run", "--detach", "--name", $ContainerName,
    "--restart", "unless-stopped",
    "--publish", "${BindAddress}:${Port}:50000",
    "--mount", "type=volume,src=$volumeName,dst=/models",
    "--env", "MAX_CONCURRENT_REQUESTS=1",
    "--env", "BATCH_SIZE_S=60"
)

if ($Mode -eq "gpu") {
    $runArguments += @("--gpus", "all", "--env", "SENSEVOICE_DEVICE=cuda:0")
}
else {
    $runArguments += @("--env", "SENSEVOICE_DEVICE=cpu")
}
$runArguments += $ImageName
Invoke-Docker -Arguments $runArguments

$healthHost = if ($BindAddress -eq "0.0.0.0") { "127.0.0.1" } else { $BindAddress }
$healthUrl = "http://${healthHost}:${Port}/health"
$deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
do {
    try {
        $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 5
        if ($health.status -eq "ok") {
            Write-Host "SenseVoice is ready on $($health.device)."
            Write-Host "API docs: http://${healthHost}:${Port}/docs"
            exit 0
        }
    }
    catch {
        Start-Sleep -Seconds 3
    }
} while ((Get-Date) -lt $deadline)

Write-Error "SenseVoice did not become ready within $StartupTimeoutSeconds seconds. Recent container logs:"
& docker logs --tail 120 $ContainerName
exit 1
