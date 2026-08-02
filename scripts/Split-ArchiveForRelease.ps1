[CmdletBinding()]
param(
    [string]$ArchivePath,
    [ValidateRange(1, 2047)]
    [int]$PartSizeMiB = 1900
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $PSCommandPath
if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    $ArchivePath = Join-Path $scriptRoot "..\dist\sensevoice-offline-rootfs.tar"
}
if (-not (Test-Path -LiteralPath $ArchivePath)) {
    throw "Archive was not found: $ArchivePath"
}

$partSizeBytes = [Int64]$PartSizeMiB * 1MB
$buffer = New-Object byte[] (8MB)
$source = [System.IO.File]::OpenRead($ArchivePath)
try {
    $partIndex = 1
    while ($source.Position -lt $source.Length) {
        $partPath = "{0}.part{1:D3}" -f $ArchivePath, $partIndex
        if (Test-Path -LiteralPath $partPath) {
            throw "Refusing to overwrite existing release part: $partPath"
        }

        $remainingInPart = [Math]::Min($partSizeBytes, $source.Length - $source.Position)
        $destination = [System.IO.File]::Open($partPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            while ($remainingInPart -gt 0) {
                $toRead = [Math]::Min([Int64]$buffer.Length, $remainingInPart)
                $read = $source.Read($buffer, 0, [int]$toRead)
                if ($read -le 0) {
                    throw "Unexpected end of archive while splitting: $ArchivePath"
                }
                $destination.Write($buffer, 0, $read)
                $remainingInPart -= $read
            }
        }
        finally {
            $destination.Dispose()
        }

        Write-Host "Created $(Split-Path -Leaf $partPath)"
        $partIndex++
    }
}
finally {
    $source.Dispose()
}

Write-Host "Created $($partIndex - 1) release parts. Upload all of them with Deploy-SenseVoice.ps1 and SHA256SUMS.txt."
