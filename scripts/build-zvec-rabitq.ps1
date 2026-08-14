$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-native.ps1') -Rid linux-x64
if ($LASTEXITCODE -ne 0) { throw "Linux x64 native build failed with exit code $LASTEXITCODE." }
