# Multiplatform native Gain status

## Build matrix

| Target | Native unit tests | Real BASS integration | Unity package | Runtime validation |
| --- | --- | --- | --- | --- |
| Windows x64 | Passed | Passed | `yarg_audio.dll` | Editor/Mono passed |
| Linux x64 | CI configured | CI configured | CI artifact pending | Unity Mono/IL2CPP pending |
| macOS x64/arm64 | CI configured | CI configured | CI artifact pending | Unity Mono/IL2CPP pending |

Linux builds only portable Gain/core-binding sources. macOS builds one universal
x86_64/arm64 dylib. Both platforms export unsupported ASIO stubs so ABI version 1
keeps a consistent C export surface.

## CI artifacts

Run `.github/workflows/native-audio.yml`. Download:

- `yarg-audio-linux-x64/libyarg_audio.so`
- `yarg-audio-macos-universal/libyarg_audio.dylib`

Copy them to:

```text
Assets/Plugins/YargAudio/Linux/x86_64/libyarg_audio.so
Assets/Plugins/YargAudio/Mac/libyarg_audio.dylib
```

Each artifact includes importer metadata restricting its library to matching
Editor/standalone targets. Re-run
`scripts/build-native.sh --verify-committed-plugin` on each host before
committing both files.

## Runtime gate

For Linux and macOS, test both Mono and IL2CPP players with normalization enabled:

1. Start/stop several songs and change normalization gain.
2. Force collections during Quickplay and after returning to Music Library.
3. Verify no dependency/ABI/attach errors, output gaps, or teardown crashes.
4. Verify native Gain matches normalization-disabled playback except level.

Phase 5 completes only after packaged binaries and platform runtime results are
recorded above.
