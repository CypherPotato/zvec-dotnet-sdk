param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64', 'osx-arm64')]
    [string] $Rid
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$nativeDirectory = Join-Path $repositoryRoot "artifacts/native/$Rid"
$libraryName = if ($Rid -eq 'win-x64') { 'zvec_c_api.dll' } elseif ($Rid -eq 'osx-arm64') { 'libzvec_c_api.dylib' } else { 'libzvec_c_api.so' }
$library = Join-Path $nativeDirectory $libraryName

if (-not (Test-Path $library)) { throw "Native runtime not found: $library" }

if ($Rid -eq 'win-x64') {
    $exports = (& dumpbin /exports $library | Out-String)
} elseif ($Rid -like 'linux-*') {
    $exports = (& nm -D -g $library | Out-String)
} else {
    $exports = (& nm -g $library | Out-String)
}
foreach ($symbol in @(
    'zvec_get_version_major',
    'openindexer_zvec_rabitq_extension_api_version',
    'openindexer_zvec_rabitq_zvec_commit',
    'openindexer_zvec_rabitq_is_supported'
)) {
    if (-not $exports.Contains($symbol)) { throw "Required export is missing from ${libraryName}: $symbol" }
}

$env:ZVEC_LIBRARY_PATH = $library
try {
    dotnet test (Join-Path $repositoryRoot 'tests/OpenIndexer.ZVec.Tests/OpenIndexer.ZVec.Tests.csproj') --configuration Release --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) { throw ".NET tests failed with exit code $LASTEXITCODE." }
}
finally {
    Remove-Item Env:ZVEC_LIBRARY_PATH -ErrorAction Ignore
}
