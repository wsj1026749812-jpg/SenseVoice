[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $PSCommandPath
$projectRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot "tts\dist\PiperTtsLite-test-data"
}

foreach ($stalePath in @(
    (Join-Path $OutputDirectory "piper-tts-test-cases.json"),
    (Join-Path $OutputDirectory "README.txt")
)) {
    Remove-Item -LiteralPath $stalePath -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$escapedCases = @'
[
  {"id":"short-zh","text":"\u60a8\u597d\uff0c\u6b22\u8fce\u4f7f\u7528\u672c\u5730\u4e2d\u6587\u8bed\u97f3\u5408\u6210\u670d\u52a1\u3002"},
  {"id":"number-mix","text":"\u4eca\u5929\u662f\u4e8c\u96f6\u4e8c\u516d\u5e74\u516b\u6708\u516d\u65e5\uff0c\u8ba2\u5355\u7f16\u53f7\u4e3a SV-1024\uff0c\u91d1\u989d\u4e00\u767e\u4e8c\u5341\u516b\u70b9\u4e94\u5143\u3002"},
  {"id":"paragraph-zh","text":"\u672c\u6d4b\u8bd5\u5728\u4e0d\u4f7f\u7528 GPU\u3001Docker \u6216\u5916\u90e8\u7f51\u7edc\u670d\u52a1\u7684\u6761\u4ef6\u4e0b\u8fd0\u884c\u3002\u5b83\u8bb0\u5f55\u7aef\u5230\u7aef\u5408\u6210\u8017\u65f6\u3001\u8fdb\u7a0b CPU \u65f6\u95f4\u3001\u751f\u6210\u97f3\u9891\u65f6\u957f\u548c\u5b9e\u65f6\u7cfb\u6570\uff0c\u65b9\u4fbf\u6bd4\u8f83\u4e0d\u540c\u7535\u8111\u7684\u672c\u5730\u63a8\u7406\u6027\u80fd\u3002"},
  {"id":"slow-zh","text":"\u8fd9\u6761\u7528\u4f8b\u4f7f\u7528\u8f83\u6162\u8bed\u901f\uff0c\u7528\u4e8e\u89c2\u5bdf\u97f3\u9891\u65f6\u957f\u53d8\u5316\u3002","length_scale":1.3}
]
'@
$casesJson = [regex]::Replace($escapedCases, '\\u([0-9a-fA-F]{4})', {
    param($match)
    [char][Convert]::ToInt32($match.Groups[1].Value, 16)
})
Set-Content -LiteralPath (Join-Path $OutputDirectory "piper-tts-test-cases.json") -Value $casesJson -Encoding utf8

@"
Piper TTS Lite test data

Contents
--------
piper-tts-test-cases.json  Chinese text benchmark cases for the browser page.

Use
---
1. Start Piper TTS Lite and open http://127.0.0.1:50100.
2. Select "导入 JSON 测试集" and choose piper-tts-test-cases.json.
3. Click "开始测试", optionally listen and assign manual quality scores.
4. Export the JSON or CSV report.

The performance report includes CPU, elapsed time, generated-audio duration,
character throughput, and real-time factor. TTS has no automatic recognition
accuracy field; manual listener quality scores are exported separately.
"@ | Set-Content -LiteralPath (Join-Path $OutputDirectory "README.txt") -Encoding utf8

$archivePath = "$OutputDirectory.zip"
Remove-Item -LiteralPath $archivePath, "$archivePath.sha256" -Force -ErrorAction SilentlyContinue
Compress-Archive -LiteralPath $OutputDirectory -DestinationPath $archivePath -CompressionLevel Optimal
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$archivePath.sha256" -Value "$archiveHash *$(Split-Path -Leaf $archivePath)" -Encoding ascii

Write-Host "Created Piper TTS test data package: $archivePath"
Write-Host "Archive SHA256: $archiveHash"
