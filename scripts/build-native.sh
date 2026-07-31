#!/usr/bin/env bash
set -euo pipefail

no_copy=false
verify_committed=false
for argument in "$@"; do
    case "$argument" in
        --no-copy) no_copy=true ;;
        --verify-committed-plugin) verify_committed=true ;;
        *) echo "Unknown argument: $argument" >&2; exit 2 ;;
    esac
done

repository="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
native="$repository/Native/YargAudio"

case "$(uname -s)" in
    Linux)
        preset="linux-x64-release"
        platform="Linux"
        built="$native/build/linux-x64/libyarg_audio.so"
        plugin="$repository/Assets/Plugins/YargAudio/Linux/x86_64/libyarg_audio.so"
        metadata="$native/packaging/Linux/x86_64/libyarg_audio.so.meta"
        ;;
    Darwin)
        preset="macos-universal-release"
        platform="MacOS"
        built="$native/build/macos-universal/libyarg_audio.dylib"
        plugin="$repository/Assets/Plugins/YargAudio/Mac/libyarg_audio.dylib"
        metadata="$native/packaging/Mac/libyarg_audio.dylib.meta"
        ;;
    *)
        echo "Unsupported host: $(uname -s)" >&2
        exit 2
        ;;
esac

cd "$native"
cmake --preset "${preset%-release}"
cmake --build --preset "$preset"
ctest --preset "$preset"
dotnet run --project tests/GainIntegration/GainIntegration.csproj \
    --configuration Release \
    -p:YargAudioPlatform="$platform"

if [[ "$platform" == "MacOS" ]]; then
    lipo "$built" -verify_arch x86_64 arm64
fi

if [[ "$no_copy" == false ]]; then
    mkdir -p "$(dirname "$plugin")"
    if [[ "$verify_committed" == true ]]; then
        if [[ ! -f "$plugin" ]] || ! cmp -s "$built" "$plugin" ||
            [[ ! -f "$plugin.meta" ]] || ! cmp -s "$metadata" "$plugin.meta"; then
            echo "Committed $(basename "$plugin") does not match native source." >&2
            echo "Run scripts/build-native.sh and commit the library." >&2
            exit 1
        fi
        echo "Committed $(basename "$plugin") matches native build"
    else
        cp "$built" "$plugin"
        cp "$metadata" "$plugin.meta"
        echo "Copied $(basename "$plugin") to $(dirname "$plugin")"
    fi
fi
