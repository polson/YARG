# Multiplatform native Gain status

## Build matrix

| Target | Native unit tests | Real BASS integration | Unity package | Runtime validation |
| --- | --- | --- | --- | --- |
| Windows x64 | Passed | Passed | `yarg_audio.dll` | Editor/Mono passed; IL2CPP pending |
| Linux x64 | Passed | Passed | `libyarg_audio.so` | Unity Mono/IL2CPP pending |
| macOS x64/arm64 | Passed | Passed on arm64 runner | Universal `libyarg_audio.dylib` | Unity Mono/IL2CPP and x64 runtime pending |

Linux builds only portable Gain/core-binding sources. macOS builds one universal
x86_64/arm64 dylib. Both platforms export unsupported ASIO stubs so ABI version 1
keeps a consistent C export surface.

## CI evidence

GitHub Actions run
[`30657497104`](https://github.com/polson/YARG/actions/runs/30657497104)
passed Windows x64, Ubuntu x64, and macOS universal jobs on 2026-07-31.
Linux/macOS jobs built native code, passed unit and real-BASS integration tests,
verified exports/architectures, and produced these packaged binaries:

```text
Linux x64 SHA-256: 5e5a177649fd1e24f4b3c44224c743a411ec5d64bbb2cec63e54eed4ff74739e
macOS universal SHA-256: dd08043a09941b83498c16f7eb7a24ef8083fa4b423686221536cee9090f3cc8
```

Follow-up run
[`30657748711`](https://github.com/polson/YARG/actions/runs/30657748711)
rebuilt all targets and verified committed Linux/macOS binaries and metadata
byte-for-byte.

## CI artifacts

Run `.github/workflows/native-audio.yml`. Download:

- `yarg-audio-linux-x64/libyarg_audio.so`
- `yarg-audio-macos-universal/libyarg_audio.dylib`

Copy them to:

```text
Assets/Plugins/YargAudio/Linux/x86_64/libyarg_audio.so
Assets/Plugins/YargAudio/Mac/libyarg_audio.dylib
```

Both artifacts and importer metadata are packaged in the Unity plugin tree.
Importer metadata restricts each library to matching Editor/standalone targets.
Re-run
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
