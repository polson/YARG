param(
    [ValidateSet("Debug", "Release", "RelWithDebInfo")]
    [string] $Configuration = "Release",
    [switch] $NoCopy,
    [switch] $VerifyCommittedPlugin
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

    dotnet run --project tests/GainIntegration/GainIntegration.csproj `
        --configuration Release `
        -p:YargAudioPlatform=Windows `
        -p:YargAudioNativeConfiguration=$Configuration
    if ($LASTEXITCODE -ne 0) { throw "Gain integration test failed" }

    if (!$NoCopy) {
        New-Item -ItemType Directory -Force $plugin | Out-Null
        $builtDll = Join-Path $native "build/windows-x64/$Configuration/yarg_audio.dll"
        $committedDll = Join-Path $plugin "yarg_audio.dll"
        if ($VerifyCommittedPlugin) {
            if (!(Test-Path $committedDll)) {
                throw "Committed yarg_audio.dll is missing. Run scripts/build-native.ps1 and commit the DLL."
            }

            # Compiler/toolchain updates can change a valid PE byte-for-byte. Run the
            # complete ABI, SafeHandle, parity, priority, and lifecycle probe against
            # the committed plugin instead of comparing non-portable build hashes.
            dotnet run --project tests/GainIntegration/GainIntegration.csproj `
                --configuration Release `
                -p:YargAudioPlatform=Windows `
                -p:YargAudioNativeConfiguration=$Configuration `
                -p:YargAudioLibraryPath=$committedDll
            if ($LASTEXITCODE -ne 0) { throw "Committed Gain plugin integration test failed" }
            Write-Host "Committed yarg_audio.dll passed native Gain integration"
        }
        else {
            Copy-Item $builtDll $plugin -Force
            Write-Host "Copied yarg_audio.dll to $plugin"
        }
    }
}
finally {
    Pop-Location
}
