param(
    [Parameter(Mandatory)]
    [string] $SourceDirectory,
    [string] $DestinationDirectory = (Join-Path $PSScriptRoot '..\artifacts\native')
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path $SourceDirectory).Path
$destinationRoot = [System.IO.Path]::GetFullPath($DestinationDirectory)
$manifestPath = Join-Path $source 'SHA256SUMS.txt'
if (-not (Test-Path $manifestPath)) { throw "Checksum manifest not found: $manifestPath" }

$assets = @{
    'win-x64' = 'zvec_c_api.dll'
    'linux-x64' = 'libzvec_c_api.so'
    'linux-arm64' = 'libzvec_c_api.so'
    'osx-arm64' = 'libzvec_c_api.dylib'
}
$manifest = Get-Content $manifestPath

foreach ($rid in $assets.Keys) {
    $archiveName = "zvec-native-$rid.zip"
    $archive = Join-Path $source $archiveName
    if (-not (Test-Path $archive)) { throw "Release asset not found: $archiveName" }

    $matchingLines = @($manifest | Where-Object { $_ -match "^[0-9a-fA-F]{64}\s+$([regex]::Escape($archiveName))$" })
    if ($matchingLines.Count -ne 1) { throw "Expected one checksum entry for $archiveName; found $($matchingLines.Count)." }
    $expectedHash = ($matchingLines[0] -split '\s+')[0]
    $actualHash = (Get-FileHash $archive -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) { throw "Checksum mismatch for $archiveName. Expected $expectedHash; found $actualHash." }

    $destination = Join-Path $destinationRoot $rid
    Remove-Item $destination -Recurse -Force -ErrorAction Ignore
    New-Item -ItemType Directory -Force $destination | Out-Null
    Expand-Archive $archive $destination
    $expectedLibrary = Join-Path $destination $assets[$rid]
    if (-not (Test-Path $expectedLibrary)) { throw "$archiveName does not contain $($assets[$rid])." }
}
