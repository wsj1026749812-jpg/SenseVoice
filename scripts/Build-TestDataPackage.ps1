[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$SkipDownloads
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $PSCommandPath
$projectRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
$stagingRoot = Join-Path $projectRoot "lite\staging"
$sourceRoot = Join-Path $stagingRoot "benchmark-audio"
$sourceSample = Join-Path $stagingRoot "sample.wav"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot "lite\dist\SenseVoiceLite-test-data"
}

$officialSamples = @(
    @{ Name = "en.mp3"; Url = "https://huggingface.co/FunAudioLLM/SenseVoiceSmall/resolve/main/example/en.mp3?download=true" },
    @{ Name = "ja.mp3"; Url = "https://huggingface.co/FunAudioLLM/SenseVoiceSmall/resolve/main/example/ja.mp3?download=true" },
    @{ Name = "yue.mp3"; Url = "https://huggingface.co/FunAudioLLM/SenseVoiceSmall/resolve/main/example/yue.mp3?download=true" },
    @{ Name = "zh.mp3"; Url = "https://huggingface.co/FunAudioLLM/SenseVoiceSmall/resolve/main/example/zh.mp3?download=true" }
)

if (-not (Test-Path -LiteralPath $sourceSample)) {
    throw "The validated SenseVoice sample was not found: $sourceSample"
}

New-Item -ItemType Directory -Force -Path $sourceRoot | Out-Null
if (-not $SkipDownloads) {
    foreach ($sample in $officialSamples) {
        $destination = Join-Path $sourceRoot $sample.Name
        if (-not (Test-Path -LiteralPath $destination)) {
            Invoke-WebRequest -Uri $sample.Url -OutFile $destination
        }
    }
}

foreach ($sample in $officialSamples) {
    $sourcePath = Join-Path $sourceRoot $sample.Name
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "Official test audio was not found: $sourcePath"
    }
}

$audioDirectory = Join-Path $OutputDirectory "audio"
foreach ($stalePath in @($audioDirectory, (Join-Path $OutputDirectory "references.json"), (Join-Path $OutputDirectory "README.txt"))) {
    Remove-Item -LiteralPath $stalePath -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $audioDirectory | Out-Null

Copy-Item -LiteralPath $sourceSample -Destination (Join-Path $audioDirectory "sample.wav") -Force
foreach ($sample in $officialSamples) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot $sample.Name) -Destination $audioDirectory -Force
}

$escapedReferences = @'
{"sample.wav":"\u751a\u81f3\u51fa\u73b0\u4ea4\u6613\u51e0\u4e4e\u505c\u6ede\u7684\u60c5\u51b5\u3002"}
'@
$referencesJson = [regex]::Replace($escapedReferences, '\\u([0-9a-fA-F]{4})', {
    param($match)
    [char][Convert]::ToInt32($match.Groups[1].Value, 16)
})
Set-Content -LiteralPath (Join-Path $OutputDirectory "references.json") -Value $referencesJson -Encoding utf8

@"
SenseVoice Lite benchmark test data

Contents
--------
audio\sample.wav    Validated Chinese WAV with a human reference transcript.
audio\en.mp3        Official SenseVoiceSmall English example.
audio\ja.mp3        Official SenseVoiceSmall Japanese example.
audio\yue.mp3       Official SenseVoiceSmall Cantonese example.
audio\zh.mp3        Official SenseVoiceSmall Chinese example.
references.json      Reference manifest for sample.wav.

Use
---
1. Start SenseVoice Lite and open http://127.0.0.1:50000.
2. In Batch Benchmark, choose the audio directory and import references.json.
3. Run the benchmark, then export CSV or JSON.

The four MP3 examples are supplied for public functional and performance testing.
They do not have bundled human reference transcripts, so their accuracy is blank
in the generated report. Add your own entries to references.json when you have
ground-truth text for them.

Sources
-------
Official SenseVoiceSmall examples:
https://huggingface.co/FunAudioLLM/SenseVoiceSmall/tree/main/example
"@ | Set-Content -LiteralPath (Join-Path $OutputDirectory "README.txt") -Encoding utf8

$archivePath = "$OutputDirectory.zip"
Remove-Item -LiteralPath $archivePath, "$archivePath.sha256" -Force -ErrorAction SilentlyContinue
Compress-Archive -LiteralPath $OutputDirectory -DestinationPath $archivePath -CompressionLevel Optimal
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$archivePath.sha256" -Value "$archiveHash *$(Split-Path -Leaf $archivePath)" -Encoding ascii

Write-Host "Created benchmark data package: $archivePath"
Write-Host "Archive SHA256: $archiveHash"
