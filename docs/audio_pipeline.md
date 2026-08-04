# YARG Audio Pipeline

This document describes current BASS audio architecture: song playback,
native DSP effects, scheduled one-shots, normal device output, and Windows
ASIO output. [Song Playback Synchronization](song_sync.md) covers clocks,
latency compensation, and tempo control in more detail.

---

## 1. Pipeline at a Glance

YARG keeps graph construction and lifecycle control in C#. Native code owns the
realtime callbacks introduced for Gain, Freeverb, scheduled one-shots, and
ASIO; those callbacks do not cross into Mono, IL2CPP, or Burst.

```text
Song file
  -> BASS source stream
  -> split streams for each stem and optional reverb branch
  -> BASS pitch/EQ effects and stem matrices
  -> float/decode stem mixer
  -> native Gain DSP when normalization is enabled
  -> BASS tempo stream
  -> output backend
       -> standard BASS device mixer -> device output
       -> Windows ASIO song mixer -> native render-ahead ring -> ASIO callback

Scheduled sample
  -> managed one-time decode to interleaved float PCM
  -> native scheduled one-shot source
  -> song output mixer

Samples and monitor routes
  -> standard sample/monitor mixer, or ASIO live mixer
  -> output backend
```

The native plugin is `yarg_audio`. It contains Gain, Freeverb, the scheduled
one-shot source, and the Windows ASIO mixer router. It does not replace BASS or
the whole managed audio graph.

---

## 2. Managed Control Plane

| Component | Responsibility |
| --- | --- |
| [`BassAudioManager`](../Assets/Script/Audio/Bass/BassAudioManager.cs) | Initializes BASS, orchestrates transport switching, creates song mixers, and reloads samples during device changes. |
| [`BassAudioTransport`](../Assets/Script/Audio/Bass/BassAudioTransport.cs) | Transport contract; owns backend + inputs; name→transport factory. |
| [`BassSharedAudioTransport`](../Assets/Script/Audio/Bass/BassSharedAudioTransport.cs) | Shared-mode BASS device transport with record-device inputs. |
| [`BassAsioAudioTransport`](../Assets/Script/Audio/Bass/Asio/BassAsioAudioTransport.cs) | ASIO driver transport: buffer config, control panel, driver inputs, reinit notifications. |
| [`BassAudioOutput`](../Assets/Script/Audio/Bass/BassAudioOutput.cs) | Stable facade for songs, samples, monitors; borrows the active transport's backend and reattaches routes on output replacement. |
| [`BassDeviceOutputBackend`](../Assets/Script/Audio/Bass/BassDeviceOutputBackend.cs) | Routes audio to a normal BASS playback device. |
| [`BassAsioOutputBackend`](../Assets/Script/Audio/Bass/Asio/BassAsioOutputBackend.cs) | Windows ASIO setup, shared song/live mixers, input routes, and native router control. |
| [`BassStemMixer`](../Assets/Script/Audio/Bass/BassStemMixer.cs) | Builds per-song source, stem, effect, tempo, and synchronization state. |
| [`BassSongPlayback`](../Assets/Script/Audio/Bass/BassSongPlayback.cs) | Owns one tempo stream's output state and its native one-shot channels. |
| [`BassGainDsp`](../Assets/Script/Audio/Bass/Effects/BassGainDsp.cs) | SafeHandle wrapper for native Gain. |
| [`BassFreeverbDsp`](../Assets/Script/Audio/Bass/Effects/BassFreeverbDsp.cs) | SafeHandle wrapper for native Freeverb. |
| [`BassNativeOneShotStream`](../Assets/Script/Audio/Bass/Effects/BassNativeOneShotStream.cs) | SafeHandle wrapper for native scheduled playback. |

Managed code performs setup, control, position reads, and disposal. It does not
provide a realtime sample callback for native Gain, Freeverb, one-shots, or
ASIO output.

---

## 3. Song Graph

### 3.1 Source and stem construction

[`BassAudioManager`](../Assets/Script/Audio/Bass/BassAudioManager.cs) creates a
stereo float decode mixer for each song and attaches the existing compressor.
[`BassStemMixer`](../Assets/Script/Audio/Bass/BassStemMixer.cs) then:

1. Creates an asynchronous BASS source stream for the song file.
2. Creates split streams for requested stem channel maps.
3. Creates a normal and a reverb stream for each stem.
4. Applies optional BASS pitch-shift processing for whammy-enabled stems.
5. Applies stem volume and panning matrices.
6. Adds stream and reverb branches to the float stem mixer.
7. Adds delay needed to align pitch-effect branches with unaffected stems.

The reverb branch is silent until reverb is enabled. When enabled,
[`BassStemChannel`](../Assets/Script/Audio/Bass/BassStemChannel.cs) adds EQ and
native Freeverb, then fades the wet branch in. Seeking resets native Freeverb
state so old delay-line contents do not leak into the new position.

### 3.2 Tempo and synchronization

The stem mixer is wrapped by a BASS_FX tempo stream. The output backend attaches
that tempo stream to its output mixer. Position and speed changes are coordinated
by [`BufferedPlaybackTimeline`](../Assets/Script/Audio/Bass/BufferedPlaybackTimeline.cs)
and `BassStemMixer`.

The synchronizer distinguishes:

- **Heard position:** position reaching the listener after queued output.
- **Control position:** predicted position after commands already sent but not
  yet reflected by BASS.

See [`song_sync.md`](song_sync.md) for the control model, startup compensation,
tempo-buffer dead time, and seek/resume behavior.

### 3.3 Normalization and Gain

[`BassNormalizer`](../Assets/Script/Audio/Bass/BassNormalizer.cs) clones source
data into a decode-only analysis graph and calculates RMS gain on a background
worker. It only computes the value. The realtime application point is native:

```text
BassNormalizer gain value
  -> BassGainDsp.SetGain
  -> yarg_gain_dsp_set_gain
  -> native BASS DSP on the stem mixer
```

If native Gain cannot attach, normalization is disabled for that mixer. There
is no managed or Burst callback fallback.

---

## 4. Native Effects and Sources

All native handles use ABI version 1 and are exposed through the C API in
[`yarg_audio.h`](../Native/YargAudio/include/yarg_audio.h). C# owns SafeHandles;
native code owns callback state and data needed by callbacks.

### Native Gain

- Entry points: `yarg_gain_dsp_attach`, `yarg_gain_dsp_set_gain`,
  `yarg_gain_dsp_destroy`.
- Attached to the song stem mixer.
- Receives gain updates atomically from managed control code.
- Runs as a native BASS DSP callback with no managed transition.

### Native Freeverb

- Entry points: `yarg_freeverb_dsp_attach`, `yarg_freeverb_dsp_reset`,
  `yarg_freeverb_dsp_destroy`.
- Used by stem reverb branches and microphone monitoring branches.
- Stem reverb uses BASS EQ before native Freeverb and a wet-only output branch.
- Microphone monitoring keeps effects off the raw analysis branch.
- Reset requests clear delay/filter state after seeks or input resets.

### Native scheduled one-shots

[`BassOneShotChannel`](../Assets/Script/Audio/Bass/BassOneShotChannel.cs) is a
managed coordinator only:

1. Decode the source sample once through a temporary BASS float mixer.
2. Copy interleaved PCM and sorted scheduled song positions into native code.
3. Attach the native source to the song output mixer.
4. Send pause, gain, seek, speed, attach, detach, and dispose updates.

The native source owns copied PCM, schedule, BASS stream, and stream callback.
It mixes scheduled and overlapping voices in native code. Playback state is
re-anchored after pause, seek, speed, and output changes. Native creation
failure disables that one-shot channel; it never restores managed callback code.

### Native ASIO transport

The ASIO router is transport, not an effect. It owns the Windows ASIO callback,
song render-ahead worker, ring buffer, direct live pull, master volume, and
underrun statistics.

---

## 5. Standard Device Output

For a normal BASS output device, `BassDeviceOutputBackend`:

1. Starts the selected BASS device.
2. Creates one float, non-stop output mixer per song playback.
3. Adds the song tempo stream to that mixer.
4. Creates sample and monitor mixers on demand.
5. Adds SFX, venue, metronome, microphone monitor, and other short-lived
   sources to the appropriate mixer.
6. Lets BASS pull the mixers through its normal device output thread.

Native one-shots attach to the song output mixer. Ordinary samples attach to the
shared sample mixer. Monitor routes attach to the monitor mixer. This keeps
scheduled song-relative playback separate from transient sample playback.

---

## 6. Windows ASIO Split

ASIO is Windows-only. Non-Windows builds export the ASIO API as unsupported
stubs while retaining the common ABI surface.

### 6.1 Why ASIO uses two mixer legs

[`BassAsioOutputBackend`](../Assets/Script/Audio/Bass/Asio/BassAsioOutputBackend.cs)
creates two shared float decode mixers:

- **Song mixer:** tempo streams and scheduled song audio. Pulled ahead of time
  by a native worker.
- **Live mixer:** samples, monitor routes, and ASIO input monitor branches.
  Pulled directly by the ASIO callback for low latency.

The split keeps potentially variable-cost BASS song pulls off the driver
callback, while live monitoring and short samples remain close to the hardware
deadline.

### 6.2 Native router flow

[`BassAsioMixerRouter`](../Assets/Script/Audio/Bass/Asio/BassAsioMixerRouter.cs)
controls `AsioMixerRouter` in native code:

```text
BASS song mixer
  -> RenderAheadMixer worker
  -> single-producer/single-consumer AudioRingBuffer

ASIO callback
  -> clear output buffer
  -> consume song frames from ring
  -> pull live mixer directly through BASS
  -> add live frames to song frames
  -> apply master volume
  -> return interleaved float stereo
```

The worker pulls BASS in bounded 128-frame chunks. Its target queue is the
configured render-ahead duration, never less than two ASIO callback buffers.
The callback uses preallocated scratch storage and records queued frames,
minimum queue depth, render time, and underrun counters.

The native router also:

- Prefills the song queue before enabling song output.
- Gates song consumption during a flush, while live audio continues.
- Computes source position using queued frames and hardware latency.
- Stops ASIO callbacks before destroying router state.
- Sets the BASS device on the render worker and ASIO callback because BASS
  device selection is thread-local.

### 6.3 ASIO inputs

When an ASIO input is selected, `BassAsioInput` creates a native BASS push
stream. It splits that stream into:

- A monitor branch routed to the live mixer, with optional native Freeverb.
- A raw analysis branch consumed by the managed microphone analysis pipeline.

Monitoring effects never contaminate pitch or level analysis.

---

## 7. Output and Resource Lifecycle

Output changes are coordinated by `BassAudioManager` (transport switch
orchestration) and `BassAudioOutput` (route reattachment against the borrowed
backend):

```text
capture active playback state
  -> detach one-shots and song/monitor routes (SuspendRoutes)
  -> drop the borrowed backend reference (DetachBackend)
  -> old transport deactivates: stop ASIO before destroying native router, dispose backend
  -> candidate transport activates: device + backend + mixers
  -> move BASS channels to replacement device
  -> reattach songs and monitor routes (AttachBackend)
  -> reattach and re-anchor one-shots
  -> restore volume, buffer, output-channel, and playing state
```

Native ownership rules:

- A native one-shot must detach before its BASS stream or callback state is
  destroyed.
- A native DSP handle must be destroyed before its borrowed BASS channel.
- The ASIO callback must be disabled and quiescent before router destruction.
- Parent mixers outlive sources attached to them.
- Finalization is leak prevention, not the normal audio lifecycle.

The normal path performs these operations from managed control code. Realtime
callbacks do not perform Unity calls, managed callbacks, logging, allocation,
or lifecycle mutation.

---

## 8. Thread Model

| Thread/context | Work |
| --- | --- |
| Unity/main control thread | Builds graphs, changes settings, seeks, pauses, swaps devices, and disposes handles. |
| BASS decode/mixer threads | Decode and mix normal device graphs, BASS FX, and native DSP callbacks. |
| Normal BASS output thread | Consumes the standard device output mixer. |
| Normalization worker | Reads the decode-only analysis mixer and updates the target Gain value. |
| Native ASIO render worker | Pulls the ASIO song mixer into the render-ahead ring. |
| ASIO driver callback | Consumes song frames, pulls the live mixer, mixes stereo output, and records transport stats. |
| Microphone analysis worker | Reads the raw microphone analysis branch without changing the monitor branch. |

The C# layer may issue control operations from the main thread, but native
callback-visible state stays native. BASS calls that depend on device selection
must run with the correct thread-local BASS device selected.

---

## 9. Native Boundary and Platform Loading

`yarg_audio` resolves BASS symbols from already-loaded modules when possible.
This avoids passing BASS handles between independent BASS instances. Core BASS,
BASSmix, and BASSASIO bindings are separate; missing dependencies disable only
features that require them.

Unity plugins are stored at:

```text
Assets/Plugins/YargAudio/Windows/x86_64/yarg_audio.dll
Assets/Plugins/YargAudio/Linux/x86_64/libyarg_audio.so
Assets/Plugins/YargAudio/Mac/libyarg_audio.dylib
```

The native library is built for 64-bit desktop targets. Gain, Freeverb, and
scheduled one-shots are portable across supported desktop platforms. The ASIO
router is implemented on Windows; other platforms return unsupported for ASIO
router calls.

---

## 10. Code Map and Checks

Managed graph and output code lives under
[`Assets/Script/Audio/Bass`](../Assets/Script/Audio/Bass). Native ABI and
implementation live under [`Native/YargAudio`](../Native/YargAudio).

Useful entry points:

- C API: [`yarg_audio.h`](../Native/YargAudio/include/yarg_audio.h) and
  [`yarg_audio_c_api.cpp`](../Native/YargAudio/src/yarg_audio_c_api.cpp)
- Native DSP: [`GainDsp.cpp`](../Native/YargAudio/src/dsp/GainDsp.cpp) and
  [`FreeverbDsp.cpp`](../Native/YargAudio/src/dsp/FreeverbDsp.cpp)
- Native one-shot: [`NativeOneShotStream.cpp`](../Native/YargAudio/src/one_shot/NativeOneShotStream.cpp)
- ASIO transport: [`AsioMixerRouter.cpp`](../Native/YargAudio/src/AsioMixerRouter.cpp)
- Render-ahead queue: [`RenderAheadMixer.cpp`](../Native/YargAudio/src/RenderAheadMixer.cpp)

Build current-host native code and integration checks with:

```bash
dotnet run --project scripts/NativeBuild -- build
```

For direct native test execution, configure the platform preset, build
`yarg_audio_tests`, then run CTest. Runtime behavior and synchronization rules
belong in code/tests; this document records architecture, not individual CI run
results.

---

## 11. Boundaries

- [`song_sync.md`](song_sync.md) is the source for clock and latency
  synchronization behavior.
- This document is the source for graph ownership, native boundaries, effects,
  output routing, and ASIO transport.
- Native migration does not imply migrating all BASS FX, decoding, microphone
  analysis, or the entire audio graph.
- New native callbacks must preserve the same ownership and no-managed-transition
  rules before being added to the realtime path.
