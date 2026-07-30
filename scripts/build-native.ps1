param(
    [ValidateSet("Debug", "Release", "RelWithDebInfo")]
    [string] $Configuration = "Release",
    [switch] $NoCopy
)

$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot
$native = Join-Path $repository "Native/YargAudio"
$plugin = Join-Path $repository "Assets/Plugins/YargAudio/Windows/x86_64"

Push-Location $native
try {
    cmake --preset windows-x64
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed" }

    cmake --build --preset windows-x64-release --config $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Native build failed" }

    ctest --preset windows-x64-release -C $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Native tests failed" }

    if (!$NoCopy) {
        New-Item -ItemType Directory -Force $plugin | Out-Null
        Copy-Item "build/windows-x64/$Configuration/yarg_audio.dll" $plugin -Force
        Write-Host "Copied yarg_audio.dll to $plugin"
    }
}
finally {
    Pop-Location
}
