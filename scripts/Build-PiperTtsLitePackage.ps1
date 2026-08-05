[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$SkipDownloads
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $PSCommandPath
$projectRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
$ttsRoot = Join-Path $projectRoot "tts"
$stagingRoot = Join-Path $ttsRoot "staging"
$pythonArchive = Join-Path $stagingRoot "python-3.14.3-embed-amd64.zip"
$pythonRoot = Join-Path $stagingRoot "python"
$pythonSitePackages = Join-Path $pythonRoot "Lib\site-packages"
$modelRoot = Join-Path $stagingRoot "models"
$publishRoot = Join-Path $stagingRoot "publish"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $ttsRoot "dist\PiperTtsLite-windows-x64"
}

$modelPath = Join-Path $modelRoot "zh_CN-huayan-medium.onnx"
$modelConfigPath = Join-Path $modelRoot "zh_CN-huayan-medium.onnx.json"
$pythonPath = Join-Path $pythonRoot "python.exe"
$pthPath = Join-Path $pythonRoot "python314._pth"

function Test-MinimumFileSize([string]$Path, [long]$MinimumBytes) {
    return (Test-Path -LiteralPath $Path) -and ((Get-Item -LiteralPath $Path).Length -ge $MinimumBytes)
}

New-Item -ItemType Directory -Force -Path $stagingRoot, $modelRoot | Out-Null
if (-not $SkipDownloads) {
    if (-not (Test-MinimumFileSize $pythonArchive 10000000)) {
        Invoke-WebRequest -Uri "https://www.python.org/ftp/python/3.14.3/python-3.14.3-embed-amd64.zip" -OutFile $pythonArchive
    }
    if (-not (Test-Path -LiteralPath $pythonPath)) {
        New-Item -ItemType Directory -Force -Path $pythonRoot | Out-Null
        Expand-Archive -LiteralPath $pythonArchive -DestinationPath $pythonRoot -Force
    }
    if (-not (Test-MinimumFileSize $modelPath 60000000)) {
        Invoke-WebRequest -Uri "https://huggingface.co/rhasspy/piper-voices/resolve/main/zh/zh_CN/huayan/medium/zh_CN-huayan-medium.onnx?download=true" -OutFile $modelPath
    }
    if (-not (Test-MinimumFileSize $modelConfigPath 1000)) {
        Invoke-WebRequest -Uri "https://huggingface.co/rhasspy/piper-voices/resolve/main/zh/zh_CN/huayan/medium/zh_CN-huayan-medium.onnx.json?download=true" -OutFile $modelConfigPath
    }
    if (-not (Test-Path -LiteralPath (Join-Path $pythonSitePackages "piper\voice.py"))) {
        New-Item -ItemType Directory -Force -Path $pythonSitePackages | Out-Null
        & python -m pip install --disable-pip-version-check --only-binary=:all: --target $pythonSitePackages "piper-tts==1.5.0"
        if ($LASTEXITCODE -ne 0) {
            throw "Could not install Piper into the portable runtime."
        }
    }
}

foreach ($requiredPath in @($pythonPath, $modelPath, $modelConfigPath, (Join-Path $pythonSitePackages "piper"))) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required Piper TTS Lite package input was not found: $requiredPath"
    }
}

@"
python314.zip
.
Lib/site-packages
"@ | Set-Content -LiteralPath $pthPath -Encoding ascii

& dotnet publish (Join-Path $ttsRoot "server\PiperTtsLite.Server.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -o $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "Could not publish the self-contained Piper TTS Lite server."
}

foreach ($stalePath in @(
    (Join-Path $OutputDirectory "runtime"),
    (Join-Path $OutputDirectory "models"),
    (Join-Path $OutputDirectory "wwwroot"),
    (Join-Path $OutputDirectory "output"),
    (Join-Path $OutputDirectory "logs"),
    (Join-Path $OutputDirectory "PiperTtsLite.Server.exe"),
    (Join-Path $OutputDirectory "PiperTtsLite.Server.pid"),
    (Join-Path $OutputDirectory "Deploy-PiperTtsLite.ps1"),
    (Join-Path $OutputDirectory "Stop-PiperTtsLite.ps1"),
    (Join-Path $OutputDirectory "Start-PiperTtsLite.cmd"),
    (Join-Path $OutputDirectory "README.md"),
    (Join-Path $OutputDirectory "THIRD_PARTY_NOTICES.md"),
    (Join-Path $OutputDirectory "SHA256SUMS.txt")
)) {
    Remove-Item -LiteralPath $stalePath -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force -Path $OutputDirectory, (Join-Path $OutputDirectory "runtime"), (Join-Path $OutputDirectory "models") | Out-Null
Copy-Item -LiteralPath (Join-Path $publishRoot "PiperTtsLite.Server.exe") -Destination $OutputDirectory -Force
Copy-Item -LiteralPath $pythonRoot -Destination (Join-Path $OutputDirectory "runtime\python") -Recurse -Force
Copy-Item -LiteralPath (Join-Path $ttsRoot "runtime\run_tts.py") -Destination (Join-Path $OutputDirectory "runtime") -Force
Copy-Item -LiteralPath $modelPath, $modelConfigPath -Destination (Join-Path $OutputDirectory "models") -Force
Copy-Item -LiteralPath (Join-Path $ttsRoot "server\wwwroot") -Destination (Join-Path $OutputDirectory "wwwroot") -Recurse -Force
Copy-Item -LiteralPath (Join-Path $ttsRoot "Deploy-PiperTtsLite.ps1"), (Join-Path $ttsRoot "Stop-PiperTtsLite.ps1"), (Join-Path $ttsRoot "Start-PiperTtsLite.cmd"), (Join-Path $ttsRoot "README.md"), (Join-Path $ttsRoot "THIRD_PARTY_NOTICES.md") -Destination $OutputDirectory -Force

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

Write-Host "Created Piper TTS Lite package: $OutputDirectory"
Get-ChildItem -LiteralPath $OutputDirectory -Recurse | Measure-Object -Property Length -Sum | ForEach-Object {
    Write-Host "Package bytes: $($_.Sum)"
}
Write-Host "Archive: $archivePath"
Write-Host "Archive SHA256: $archiveHash"
