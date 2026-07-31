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
[`30670089826`](https://github.com/polson/YARG/actions/runs/30670089826)
passed Windows x64, Ubuntu x64, and macOS universal jobs on 2026-07-31.
Linux/macOS jobs built current native one-shot code, passed unit and real-BASS
Gain/Freeverb/one-shot integration tests, verified exports/architectures, and
produced these committed plugin binaries:

```text
Linux x64 SHA-256: 4820aa4c5ca8c37c9ecc2e7829d8c759c0a5dcfb1de30588ee5fa88480fbc25b
macOS universal SHA-256: a9f1ef254f1a627b76a70ec61c3252e62e4ec3177c3341e16dfda31e626015fe
```

Follow-up run
[`30657748711`](https://github.com/polson/YARG/actions/runs/30657748711)
rebuilt all targets and verified committed Linux/macOS binaries and metadata
byte-for-byte.

## CI artifacts

Run `.github/workflows/native-audio.yml` for validation artifacts, or
`.github/workflows/native-audio-package.yml` for freshly built artifacts.
The package command downloads all three platforms:

~~~text
dotnet run --project scripts/NativeBuild -- package --ref <remote-branch-or-commit>
~~~

Downloaded artifacts contain:

- `yarg-audio-windows-x64/yarg_audio.dll`
- `yarg-audio-linux-x64/libyarg_audio.so`
- `yarg-audio-macos-universal/libyarg_audio.dylib`

Copy them to:

~~~text
Assets/Plugins/YargAudio/Windows/x86_64/yarg_audio.dll
Assets/Plugins/YargAudio/Linux/x86_64/libyarg_audio.so
Assets/Plugins/YargAudio/Mac/libyarg_audio.dylib
~~~

All artifacts and importer metadata are packaged in the Unity plugin tree.
Importer metadata restricts each library to matching Editor/standalone targets.
Run
`dotnet run --project scripts/NativeBuild -- build --verify-committed-plugin`
on each host before committing platform changes.

## Runtime gate

For Linux and macOS, test both Mono and IL2CPP players with normalization enabled:

1. Start/stop several songs and change normalization gain.
2. Force collections during Quickplay and after returning to Music Library.
3. Verify no dependency/ABI/attach errors, output gaps, or teardown crashes.
4. Verify native Gain matches normalization-disabled playback except level.

Packaged binaries and native host integration results are complete. Unity Mono
and IL2CPP runtime validation remains pending.
