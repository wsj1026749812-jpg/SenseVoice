[CmdletBinding()]
param(
    [string]$ImageName = "sensevoice:offline",
    [string]$ArchivePath,
    [switch]$SkipModelPreload
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $PSCommandPath
if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    $ArchivePath = Join-Path $scriptRoot "..\dist\sensevoice-offline-rootfs.tar"
}
$projectRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
$archiveDirectory = Split-Path -Parent $ArchivePath
$deploymentScript = Join-Path $projectRoot "deployment\Deploy-SenseVoice.ps1"

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker CLI was not found. Install and start Docker Desktop in Linux containers mode first."
}
if (-not (Test-Path -LiteralPath $deploymentScript)) {
    throw "Deployment script was not found: $deploymentScript"
}

docker version | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Docker Desktop is not running or is not reachable."
}

New-Item -ItemType Directory -Force -Path $archiveDirectory | Out-Null
$preloadModel = if ($SkipModelPreload) { "false" } else { "true" }

Push-Location $projectRoot
$exportContainer = "sensevoice-export-$PID"
$exportContainerCreated = $false
try {
    & docker build --pull --build-arg "PRELOAD_MODEL=$preloadModel" --tag $ImageName .
    if ($LASTEXITCODE -ne 0) { throw "Docker image build failed." }

    & docker create --name $exportContainer $ImageName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not create export container." }
    $exportContainerCreated = $true

    & docker export --output $ArchivePath $exportContainer
    if ($LASTEXITCODE -ne 0) { throw "Docker rootfs export failed." }

    Copy-Item -LiteralPath $deploymentScript -Destination (Join-Path $archiveDirectory "Deploy-SenseVoice.ps1") -Force
}
finally {
    if ($exportContainerCreated) {
        & docker rm --force $exportContainer | Out-Null
    }
    Pop-Location
}

Write-Host "Created offline image archive: $ArchivePath"
Write-Host "Copy the .tar archive and $archiveDirectory\Deploy-SenseVoice.ps1 to the target Windows computer."
