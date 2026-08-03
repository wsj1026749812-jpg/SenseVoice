[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$SkipDownloads
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $PSCommandPath
$projectRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
$liteRoot = Join-Path $projectRoot "lite"
$stagingRoot = Join-Path $liteRoot "staging"
$runtimeRoot = Join-Path $stagingRoot "runtime"
$modelRoot = Join-Path $stagingRoot "models"
$publishRoot = Join-Path $stagingRoot "publish"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $liteRoot "dist\SenseVoiceLite-windows-x64"
}

$runtimeZip = Join-Path $stagingRoot "funasr-llamacpp-windows-x64.zip"
$runtimeUrl = "https://github.com/QwenAudio/SenseVoice/releases/download/runtime-llamacpp-v0.1.9/funasr-llamacpp-windows-x64.zip"
$runnerPath = Join-Path $runtimeRoot "llama-funasr-sensevoice.exe"
$modelPath = Join-Path $modelRoot "sensevoice-small-q8.gguf"
$vadPath = Join-Path $modelRoot "fsmn-vad.gguf"

New-Item -ItemType Directory -Force -Path $stagingRoot, $runtimeRoot, $modelRoot | Out-Null
if (-not $SkipDownloads) {
    if (-not (Test-Path -LiteralPath $runtimeZip)) {
        Invoke-WebRequest -Uri $runtimeUrl -OutFile $runtimeZip
    }
    if (-not (Test-Path -LiteralPath $runnerPath)) {
        Expand-Archive -LiteralPath $runtimeZip -DestinationPath $runtimeRoot -Force
    }
    if (-not (Test-Path -LiteralPath $modelPath)) {
        Invoke-WebRequest -Uri "https://huggingface.co/FunAudioLLM/SenseVoiceSmall-GGUF/resolve/main/sensevoice-small-q8.gguf?download=true" -OutFile $modelPath
    }
    if (-not (Test-Path -LiteralPath $vadPath)) {
        Invoke-WebRequest -Uri "https://huggingface.co/FunAudioLLM/fsmn-vad-GGUF/resolve/main/fsmn-vad.gguf?download=true" -OutFile $vadPath
    }
}

foreach ($requiredPath in @($runnerPath, $modelPath, $vadPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required Lite package input was not found: $requiredPath"
    }
}

& dotnet publish (Join-Path $liteRoot "server\SenseVoiceLite.Server.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -o $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "Could not publish the self-contained SenseVoice Lite server."
}

$stalePaths = @(
    (Join-Path $OutputDirectory "runtime"),
    (Join-Path $OutputDirectory "models"),
    (Join-Path $OutputDirectory "wwwroot"),
    (Join-Path $OutputDirectory "logs"),
    (Join-Path $OutputDirectory "SenseVoiceLite.Server.exe"),
    (Join-Path $OutputDirectory "SenseVoiceLite.Server.pid"),
    (Join-Path $OutputDirectory "Deploy-SenseVoiceLite.ps1"),
    (Join-Path $OutputDirectory "Stop-SenseVoiceLite.ps1"),
    (Join-Path $OutputDirectory "Start-SenseVoiceLite.cmd"),
    (Join-Path $OutputDirectory "README.md"),
    (Join-Path $OutputDirectory "THIRD_PARTY_NOTICES.md"),
    (Join-Path $OutputDirectory "SHA256SUMS.txt")
)
foreach ($stalePath in $stalePaths) {
    Remove-Item -LiteralPath $stalePath -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force -Path $OutputDirectory, (Join-Path $OutputDirectory "runtime"), (Join-Path $OutputDirectory "models") | Out-Null
Copy-Item -LiteralPath (Join-Path $publishRoot "SenseVoiceLite.Server.exe") -Destination $OutputDirectory -Force
Copy-Item -LiteralPath $runnerPath -Destination (Join-Path $OutputDirectory "runtime") -Force
Copy-Item -LiteralPath $modelPath, $vadPath -Destination (Join-Path $OutputDirectory "models") -Force
Copy-Item -LiteralPath (Join-Path $liteRoot "server\wwwroot") -Destination (Join-Path $OutputDirectory "wwwroot") -Recurse -Force
Copy-Item -LiteralPath (Join-Path $liteRoot "Deploy-SenseVoiceLite.ps1"), (Join-Path $liteRoot "Stop-SenseVoiceLite.ps1"), (Join-Path $liteRoot "Start-SenseVoiceLite.cmd"), (Join-Path $liteRoot "README.md"), (Join-Path $liteRoot "THIRD_PARTY_NOTICES.md") -Destination $OutputDirectory -Force

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

Write-Host "Created SenseVoice Lite package: $OutputDirectory"
Get-ChildItem -LiteralPath $OutputDirectory -Recurse | Measure-Object -Property Length -Sum | ForEach-Object {
    Write-Host "Package bytes: $($_.Sum)"
}
Write-Host "Archive: $archivePath"
Write-Host "Archive SHA256: $archiveHash"
