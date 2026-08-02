[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:50000",
    [string]$AudioPath
)

$ErrorActionPreference = "Stop"
$health = Invoke-RestMethod "$BaseUrl/health"
$health | ConvertTo-Json -Depth 5

if ($AudioPath) {
    if (-not (Test-Path -LiteralPath $AudioPath)) { throw "Audio file was not found: $AudioPath" }
    $response = curl.exe -sS -X POST "$BaseUrl/api/v1/asr" -F "files=@$AudioPath" -F "lang=auto" -F "use_itn=true"
    if ($LASTEXITCODE -ne 0) { throw "ASR request failed." }
    $response
}
