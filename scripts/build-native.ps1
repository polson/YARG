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

    dotnet run --project tests/WindowsGainIntegration/WindowsGainIntegration.csproj `
        --configuration Release `
        -p:YargAudioNativeConfiguration=$Configuration
    if ($LASTEXITCODE -ne 0) { throw "Windows Gain integration test failed" }

    if (!$NoCopy) {
        New-Item -ItemType Directory -Force $plugin | Out-Null
        $builtDll = Join-Path $native "build/windows-x64/$Configuration/yarg_audio.dll"
        $committedDll = Join-Path $plugin "yarg_audio.dll"
        if ($VerifyCommittedPlugin) {
            if (!(Test-Path $committedDll) -or
                (Get-FileHash $builtDll).Hash -ne (Get-FileHash $committedDll).Hash) {
                throw "Committed yarg_audio.dll does not match native source. Run scripts/build-native.ps1 and commit the DLL."
            }
            Write-Host "Committed yarg_audio.dll matches native build"
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
