# YargAudio native audio library

Portable 64-bit desktop native Gain, Freeverb, and scheduled one-shot source
plus Windows x64 ASIO mixer router.
Non-Windows builds preserve ASIO C exports as unsupported stubs.

Managed code reaches them through the C ABI and `SafeHandle` wrappers.
Scheduled one-shots keep their PCM, schedule, BASS stream callback, and
lifecycle state native; the former Burst callback backend has been removed
after runtime and real-BASS platform validation.
It accepts float BASS channels, or channels processed as float through
`BASS_CONFIG_FLOATDSP`. Failed native attachment disables normalization for that
mixer; there is no Burst fallback.

Core BASS symbols resolve from already-loaded `bass.dll`, `libbass.so`, or
`libbass.dylib`. This prevents channel handles from being passed to a second
BASS instance. Missing required symbols fail attachment.

Gain state uses atomic bit storage. Freeverb preserves managed topology:
sample-rate-scaled comb/all-pass delay lines, stereo spread, wet/dry mixing,
callback-safe reset, channel locking, and safe DSP removal.

The audio graph, native DSP boundary, scheduled one-shot source, and ASIO split
are documented in [`docs/audio_pipeline.md`](../../docs/audio_pipeline.md).

## ASIO runtime invariants

- Joined BASSASIO channels invoke one callback with interleaved samples.
- Callback `length` and return value cover full joined buffer in bytes.
- `BASS_ASIO_FORMAT_FLOAT` is value 19; stereo frame is 8 bytes.
- `BASS_SetDevice` is thread-local. Render worker and ASIO callback set captured
  BASS device before decode pulls.
- One worker exclusively pulls buffered mixer. ASIO callback exclusively pulls
  direct mixer.
- Mixer mutation, seek, source removal, and source freeing must be serialized
  against decode pulls. The native render-ahead worker stops before its ring is
  cleared; output lifecycle code detaches sources before freeing parent mixers.
- Callback may not call `BASS_ASIO_Stop` or `BASS_ASIO_Free`.

Sources: official Un4seen `ASIOPROC`, `BASS_ASIO_ChannelEnable`,
`BASS_ASIO_ChannelJoin`, `BASS_ASIO_ChannelSetFormat`, `BASS_ChannelGetData`,
`BASS_SetDevice`, and `BASS_ChannelLock` documentation.

## Build

Install CMake 3.25+ and platform C++ tools.

```bash
cmake --preset linux-x64
cmake --build --preset linux-x64-release
ctest --preset linux-x64-release
```

Use `windows-x64-release` or `macos-universal-release` on those platforms.

## Compatibility

Linux binaries are built inside an Ubuntu 20.04 container (glibc 2.31,
gcc 10) to keep the glibc floor at 2.31, matching Unity 6's Ubuntu 20.04
support. Toolchains linked against glibc >= 2.34 emit `dlopen@GLIBC_2.34`
and produce a plugin that fails to load on Ubuntu 20.04, Debian 11, and
RHEL 8. CI must not move to newer containers or runners without re-evaluating
this.
