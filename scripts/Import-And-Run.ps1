[CmdletBinding()]
param(
    [ValidateSet("cpu", "gpu")]
    [string]$Mode = "cpu",
    [string]$ArchivePath,
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
$deploymentScript = Join-Path $scriptRoot "..\deployment\Deploy-SenseVoice.ps1"
if (-not (Test-Path -LiteralPath $deploymentScript)) {
    throw "Deployment script was not found: $deploymentScript"
}

$deploymentArguments = @{
    Mode = $Mode
    ImageName = $ImageName
    ContainerName = $ContainerName
    BindAddress = $BindAddress
    Port = $Port
    StartupTimeoutSeconds = $StartupTimeoutSeconds
    Replace = $Replace
}
if (-not [string]::IsNullOrWhiteSpace($ArchivePath)) {
    $deploymentArguments.ImageArchive = $ArchivePath
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedArchiveSha256)) {
    $deploymentArguments.ExpectedArchiveSha256 = $ExpectedArchiveSha256
}

& $deploymentScript @deploymentArguments
exit $LASTEXITCODE
