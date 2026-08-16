[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$SkipDownloads
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $PSCommandPath
$projectRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
$serviceRoot = Join-Path $projectRoot "streaming"
$stagingRoot = Join-Path $serviceRoot "staging"
$modelArchive = Join-Path $stagingRoot "sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23.tar.bz2"
$modelRoot = Join-Path $stagingRoot "models"
$modelDirectoryName = "sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23"
$modelDirectory = Join-Path $modelRoot $modelDirectoryName
$publishRoot = Join-Path $stagingRoot "publish"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $serviceRoot "dist\SherpaStreamingAsr-windows-x64"
}

function Test-RequiredModelFiles([string]$Directory) {
    foreach ($name in @("encoder-epoch-99-avg-1.int8.onnx", "decoder-epoch-99-avg-1.int8.onnx", "joiner-epoch-99-avg-1.int8.onnx", "tokens.txt")) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $name))) { return $false }
    }
    return $true
}

New-Item -ItemType Directory -Force -Path $stagingRoot, $modelRoot | Out-Null
if (-not $SkipDownloads) {
    if (-not (Test-Path -LiteralPath $modelArchive)) {
        Invoke-WebRequest -Uri "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23.tar.bz2" -OutFile $modelArchive
    }
    if (-not (Test-RequiredModelFiles $modelDirectory)) {
        & tar.exe -xjf $modelArchive -C $modelRoot
        if ($LASTEXITCODE -ne 0) { throw "Could not extract the streaming ASR model archive." }
    }
}

if (-not (Test-RequiredModelFiles $modelDirectory)) {
    throw "Required streaming model files were not found in: $modelDirectory"
}

& dotnet publish (Join-Path $serviceRoot "server\SherpaStreamingAsr.Server.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -o $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "Could not publish the self-contained Sherpa Streaming ASR server."
}

foreach ($stalePath in @(
    (Join-Path $OutputDirectory "models"),
    (Join-Path $OutputDirectory "wwwroot"),
    (Join-Path $OutputDirectory "logs"),
    (Join-Path $OutputDirectory "SherpaStreamingAsr.Server.exe"),
    (Join-Path $OutputDirectory "SherpaStreamingAsr.Server.pdb"),
    (Join-Path $OutputDirectory "SherpaStreamingAsr.Server.pid"),
    (Join-Path $OutputDirectory "Deploy-SherpaStreamingAsr.ps1"),
    (Join-Path $OutputDirectory "Stop-SherpaStreamingAsr.ps1"),
    (Join-Path $OutputDirectory "Start-SherpaStreamingAsr.cmd"),
    (Join-Path $OutputDirectory "README.md"),
    (Join-Path $OutputDirectory "THIRD_PARTY_NOTICES.md"),
    (Join-Path $OutputDirectory "SHA256SUMS.txt")
)) {
    Remove-Item -LiteralPath $stalePath -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force -Path $OutputDirectory, (Join-Path $OutputDirectory "models") | Out-Null
Get-ChildItem -LiteralPath $publishRoot -File | Copy-Item -Destination $OutputDirectory -Force
$packageModelDirectory = Join-Path (Join-Path $OutputDirectory "models") $modelDirectoryName
New-Item -ItemType Directory -Force -Path $packageModelDirectory | Out-Null
foreach ($name in @("encoder-epoch-99-avg-1.int8.onnx", "decoder-epoch-99-avg-1.int8.onnx", "joiner-epoch-99-avg-1.int8.onnx", "tokens.txt")) {
    Copy-Item -LiteralPath (Join-Path $modelDirectory $name) -Destination $packageModelDirectory -Force
}
Copy-Item -LiteralPath (Join-Path $serviceRoot "server\wwwroot") -Destination (Join-Path $OutputDirectory "wwwroot") -Recurse -Force
Copy-Item -LiteralPath (Join-Path $serviceRoot "Deploy-SherpaStreamingAsr.ps1"), (Join-Path $serviceRoot "Stop-SherpaStreamingAsr.ps1"), (Join-Path $serviceRoot "Start-SherpaStreamingAsr.cmd"), (Join-Path $serviceRoot "README.md"), (Join-Path $serviceRoot "THIRD_PARTY_NOTICES.md") -Destination $OutputDirectory -Force

$manifestPath = Join-Path $OutputDirectory "SHA256SUMS.txt"
$hashLines = Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | ForEach-Object {
    $relativePath = $_.FullName.Substring($OutputDirectory.Length).TrimStart('\\') -replace '\\', '/'
    "$( (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() ) *$relativePath"
}
Set-Content -LiteralPath $manifestPath -Value $hashLines -Encoding ascii

$archivePath = "$OutputDirectory.zip"
Remove-Item -LiteralPath $archivePath, "$archivePath.sha256" -Force -ErrorAction SilentlyContinue
Compress-Archive -LiteralPath $OutputDirectory -DestinationPath $archivePath -CompressionLevel Optimal
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$archivePath.sha256" -Value "$archiveHash *$(Split-Path -Leaf $archivePath)" -Encoding ascii

Write-Host "Created Sherpa Streaming ASR package: $OutputDirectory"
Get-ChildItem -LiteralPath $OutputDirectory -Recurse | Measure-Object -Property Length -Sum | ForEach-Object {
    Write-Host "Package bytes: $($_.Sum)"
}
Write-Host "Archive: $archivePath"
Write-Host "Archive SHA256: $archiveHash"
