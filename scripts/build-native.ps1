param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64', 'osx-arm64')]
    [string] $Rid
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$zvecRoot = Join-Path $repositoryRoot 'core/zvec'
$bindingRoot = Join-Path $repositoryRoot 'bindings/native'
$buildDirectory = Join-Path $repositoryRoot "artifacts/build/$Rid"
$outputDirectory = Join-Path $repositoryRoot "artifacts/native/$Rid"
$expectedCommit = 'ec8a78ee08b14a0b8c94158ffc1de42cd3f97f6d'

if ((git -C $zvecRoot rev-parse HEAD).Trim() -ne $expectedCommit) {
    throw "core/zvec must be checked out at $expectedCommit."
}
if ((git -C $zvecRoot status --short).Count -ne 0) {
    throw 'core/zvec must be clean before building.'
}

Remove-Item $buildDirectory -Recurse -Force -ErrorAction Ignore
Remove-Item $outputDirectory -Recurse -Force -ErrorAction Ignore
New-Item -ItemType Directory -Force $buildDirectory, $outputDirectory | Out-Null

$nativeCmake = Join-Path $zvecRoot 'src/binding/c/CMakeLists.txt'
$extensionHeader = Join-Path $zvecRoot 'src/binding/c/zvec_rabitq_extension.h'
$extensionSource = Join-Path $zvecRoot 'src/binding/c/zvec_rabitq_extension.cc'

try {
    Copy-Item (Join-Path $bindingRoot 'zvec_rabitq_extension.h') $extensionHeader
    Copy-Item (Join-Path $bindingRoot 'zvec_rabitq_extension.cc') $extensionSource

    $cmake = [System.IO.File]::ReadAllText($nativeCmake)
    $sourcePattern = 'set\(ZVEC_C_API_SOURCES\r?\n\s+c_api\.cc\r?\n\)'
    if ([regex]::Matches($cmake, $sourcePattern).Count -ne 1) {
        throw 'Could not uniquely locate ZVEC_C_API_SOURCES in the pinned ZVec CMake file.'
    }
    $cmake = [regex]::Replace($cmake, $sourcePattern, "set(ZVEC_C_API_SOURCES`n    c_api.cc`n    zvec_rabitq_extension.cc`n)")
    if ($Rid -eq 'linux-x64') {
        $cmake += @'

if(TARGET core_knn_diskann_static)
    target_link_libraries(zvec_c_api PRIVATE
        -Wl,--whole-archive
        $<TARGET_FILE:core_knn_diskann_static>
        -Wl,--no-whole-archive
    )
endif()
'@
    }
    [System.IO.File]::WriteAllText($nativeCmake, $cmake)

    $arguments = @(
        '-S', $zvecRoot,
        '-B', $buildDirectory,
        '-G', 'Ninja',
        '-DCMAKE_BUILD_TYPE=Release',
        '-DBUILD_C_BINDINGS=ON',
        '-DBUILD_PYTHON_BINDINGS=OFF',
        '-DBUILD_TESTING=OFF',
        '-DBUILD_TOOLS=OFF'
    )
    if ($Rid -eq 'osx-arm64') {
        $arguments += '-DCMAKE_OSX_ARCHITECTURES=arm64'
        $arguments += '-DCMAKE_OSX_DEPLOYMENT_TARGET=11.0'
    }
    if ($Rid -eq 'win-x64') {
        $arguments += '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded'
    }

    & cmake @arguments
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed with exit code $LASTEXITCODE." }
    & cmake --build $buildDirectory --target zvec_c_api --config Release --parallel
    if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE." }

    $libraryName = if ($Rid -eq 'win-x64') { 'zvec_c_api.dll' } elseif ($Rid -eq 'osx-arm64') { 'libzvec_c_api.dylib' } else { 'libzvec_c_api.so' }
    $library = Get-ChildItem $buildDirectory -Recurse -File -Filter $libraryName | Select-Object -First 1
    if ($null -eq $library) { throw "$libraryName was not produced." }
    Copy-Item $library.FullName (Join-Path $outputDirectory $libraryName)

    $hash = Get-FileHash (Join-Path $outputDirectory $libraryName) -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $libraryName" | Set-Content (Join-Path $outputDirectory 'SHA256SUMS.txt')
    Write-Host "Produced $($libraryName) for $Rid"
    Write-Host "SHA-256: $($hash.Hash)"
}
finally {
    git -C $zvecRoot checkout -- src/binding/c/CMakeLists.txt
    Remove-Item $extensionHeader, $extensionSource -Force -ErrorAction Ignore
}
