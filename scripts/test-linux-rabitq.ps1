$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'test-native.ps1') -Rid linux-x64
if ($LASTEXITCODE -ne 0) { throw "Linux x64 native tests failed with exit code $LASTEXITCODE." }
