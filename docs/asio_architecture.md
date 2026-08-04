# YARG ASIO Architecture

This document explains the ASIO (Audio Stream Input/Output) support added on
the `feature/asio` branch: output transport, latency model, input capture,
microphone routing, and lifecycle. It complements
[`audio_pipeline.md`](audio_pipeline.md) (whole-graph overview, section 6
covers the ASIO split at a glance) and [`song_sync.md`](song_sync.md) (clock
and latency synchronization model).

ASIO is **Windows-only**. Every managed ASIO path is guarded with
`UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN`; native ASIO router entry points are
compiled to unsupported stubs on Linux and macOS.

---

## 1. Goals and Constraints

- **Low latency.** ASIO gives exclusive access to the hardware with a
  user-selectable driver buffer size, bypassing the OS mixer and BASS's normal
  device output path.
- **Realtime safety.** The ASIO driver callback never crosses into managed
  code, Mono, IL2CPP, or Burst. All callback work happens in the native
  `yarg_audio` plugin.
- **BASS stays the graph engine.** Song decoding, stems, tempo, one-shots, and
  live mixing are still BASS. The native plugin owns only the transport: the
  render-ahead worker, ring buffer, and ASIO callback.
- **Backend decoupling.** Normal device output and ASIO output are two
  implementations of one interface, swappable at runtime without touching song
  or sample code.

---

## 2. Component Map

### Managed (C#)

| Component | Responsibility |
| --- | --- |
| [`BassAudioTransport`](../Assets/Script/Audio/Bass/BassAudioTransport.cs) | Transport contract + name→transport factory (the single classification point). |
| [`BassSharedAudioTransport`](../Assets/Script/Audio/Bass/BassSharedAudioTransport.cs) | Shared-mode transport: BASS device + backend, record-device inputs. |
| [`BassAsioAudioTransport`](../Assets/Script/Audio/Bass/Asio/BassAsioAudioTransport.cs) | ASIO transport: no-sound BASS context, ASIO backend, driver inputs, buffer config, control panel, reinit notifications. |
| [`BassOutputDevice`](../Assets/Script/Audio/Bass/BassOutputDevice.cs) | A BASS device context mixers route to; owns a `BassDeviceContextLease`. Carries no driver identity. |
| [`BassDeviceContextLease`](../Assets/Script/Audio/Bass/BassDeviceContextLease.cs) | Ref-counted BASS device context ownership (Default and ASIO share device 0). |
| [`BassAudioOutput`](../Assets/Script/Audio/Bass/BassAudioOutput.cs) | Stable facade; borrows the active transport's backend, reattaches songs/monitors on device change. |
| [`IBassOutputBackend`](../Assets/Script/Audio/Bass/IBassOutputBackend.cs) | Common song/sample/monitor/volume contract shared by both backends. |
| [`BassAsioOutputBackend`](../Assets/Script/Audio/Bass/Asio/BassAsioOutputBackend.cs) | ASIO driver setup, song/live mixers, input pool, native router control, driver notifications. |
| [`BassDeviceOutputBackend`](../Assets/Script/Audio/Bass/BassDeviceOutputBackend.cs) | Normal BASS device output; the non-ASIO counterpart. |
| [`BassAsioMixerRouter`](../Assets/Script/Audio/Bass/Asio/BassAsioMixerRouter.cs) | P/Invoke wrapper for the native `AsioMixerRouter` (ABI structs, stats, clock). |
| [`BassAsioInput`](../Assets/Script/Audio/Bass/Asio/BassAsioInput.cs) | BASS push stream + split streams for one ASIO input channel; lease ownership. |
| [`BassAsioInputLease`](../Assets/Script/Audio/Bass/Asio/BassAsioInput.cs) | Exclusive consumer handle for one input; implements `IBassMicSampleSource`. |
| [`BassAsioMicDevice`](../Assets/Script/Audio/Bass/Asio/BassAsioMicDevice.cs) | Mic-device adapter that owns a lease and feeds the shared analysis pipeline. |
| [`BassAudioManager`](../Assets/Script/Audio/Bass/BassAudioManager.cs) | Transport switch orchestration: resolve, activate, route, roll back. No ASIO vocabulary. |

### Native (`Native/YargAudio`)

| Component | Responsibility |
| --- | --- |
| [`AsioMixerRouter`](../Native/YargAudio/src/AsioMixerRouter.cpp) | ASIO callback, song render-ahead worker, ring buffer, live pull, volume, stats, clock. |
| [`RenderAheadMixer`](../Native/YargAudio/src/RenderAheadMixer.cpp) | Worker thread pulling a BASS mixer into the ring; prefill, flush, low-watermark refill. |
| [`AudioRingBuffer`](../Native/YargAudio/src/AudioRingBuffer.cpp) | SPSC float ring buffer between worker and callback. |
| [`yarg_audio_c_api.cpp`](../Native/YargAudio/src/yarg_audio_c_api.cpp) | C entry points; ABI-versioned; ASIO router stubs on non-Windows. |
| [`yarg_audio.h`](../Native/YargAudio/include/yarg_audio.h) | Public C ABI: config/stats/clock structs and router functions. |

---

## 3. Device Model

`BassOutputDevice` is a plain BASS device context — it carries no driver identity:

```csharp
public readonly int DeviceId;  // BASS device id (streams live here)
```

- `Create(deviceId, name)` — shared path: `Bass.Init(deviceId, ...)`, resolves the dynamic
  "Default" device.
- `CreateAsio(name)` — ASIO path: initializes BASS **device 0** (no-sound device) as the
  stream context. ASIO has no BASS output device; BASS is used purely to create and own
  streams that decode into mixers.

**Context ownership.** Each device holds a `BassDeviceContextLease`, a refcount on its BASS
context. "Default" and ASIO both resolve to device 0, so during a switch both transports
hold leases and the context survives until the last one releases. This replaces the old
`TransferOwnershipTo` ownership hand-off and lets three transports (Default, ASIO, future
WASAPI-exclusive) share one context safely.

**Transports own the driver world.** The device does not know whether it backs ASIO. That
knowledge lives in the transport:

| Transport | BASS context | Backend | Inputs |
| --- | --- | --- | --- |
| `BassSharedAudioTransport` | the chosen BASS device | `BassDeviceOutputBackend` | BASS record devices |
| `BassAsioAudioTransport` | device 0 (no-sound) | `BassAsioOutputBackend` | driver input channels |

`BassAudioTransport.Create(name)` resolves a display name to a transport; the `"ASIO: "`
prefix classification lives there, in one place. The manager never branches on transport
type.

Enumeration (`BassAudioManager.GetAllOutputDevices`) concatenates
`BassSharedAudioTransport.EnumerateDevices()` (BASS devices) with
`BassAsioAudioTransport.EnumerateDevices()` (driver list, prefixed `"ASIO: "` for
display).

**Thread-local device selection.** BASS device selection is thread-local.
`BassOutputDevice.Use()` sets `Bass.CurrentDevice`; the native worker and
callback must set the BASS device id on their own threads (the router receives
`BassDeviceId` in its config and calls `bass.setDevice` before pulling).

---

## 4. Managed Backend Split

The **transport owns its backend**; the facade borrows it:

```csharp
// BassAudioManager.ApplyOutputDevice — the one switch path
_audioOutput.SuspendRoutes();                    // detach monitors + songs (no dispose)
_audioOutput.DetachBackend();                    // drop the borrowed reference
previous.Deactivate();                           // transport disposes its own backend
candidate.Activate(configuration);               // transport creates + initializes backend
_audioOutput.AttachBackend(candidate.Backend, candidate.BassDeviceId);
```

`BassAudioOutput` keeps the song-playback and monitor registries, attaches routes to
whatever backend it is given, and reapplies the cached master volume. It never creates,
selects, or disposes a backend, and it contains no ASIO vocabulary: the backend is chosen
by the transport, and the ASIO input surface lives on `BassAsioAudioTransport`.

`IBassOutputBackend` covers song playback (attach/detach/play/pause/seek/
fade/volume/position/level), monitors, samples, master volume, and three
latency surfaces:

- `HeardLatencyMilliseconds` — latency the player hears.
- `SongMixerRunsContinuously` — whether the song mixer is always running
  (ASIO render-ahead) vs pulled only while playing (normal device).
- `PlaybackStartDelay` — startup delay used to anchor song position.

The facade owns the song playback reattachment and monitor route migration with
rollback. On a transport switch it detaches everything, the transport swaps backends,
and the facade restores state against the new backend.

---

## 5. Output Graph: Two Mixer Legs

`BassAsioOutputBackend` creates two shared float **decode** mixers:

**Song mixer** (`_songMixerHandle`)
`BassFlags.Float | MixerNonStop | Decode | MixerPositionEx`
- Tempo streams and native one-shot channels attach here.
- Pulled **ahead of time** by the native render worker.
- `MixerPositionEx` keeps source-position history for `GetSongPosition`.
- `MixerChanPause` on attached sources keeps them silent until played.

**Live mixer** (`_liveMixerHandle`)
`BassFlags.Float | MixerNonStop | Decode`
- SFX, venue, metronome samples, monitor routes, and ASIO input monitor
  branches attach here.
- Pulled **directly by the ASIO callback** for minimum latency.

Why two legs: pulling BASS song audio is variable-cost (decode, tempo, DSP);
it must not run on the driver deadline. Live monitoring and short samples stay
close to the hardware deadline. The split also lets the callback fail song
consumption (underrun) without killing live audio.

```text
tempo streams / one-shots ─▶ song mixer ─▶ native worker ─▶ ring ─┐
                                                                  ├─▶ ASIO callback ─▶ hardware
samples / monitors / input monitors ─▶ live mixer ────────────────┘
```

---

## 6. Native Transport: `AsioMixerRouter`

### 6.1 Creation

`BassAsioMixerRouter.Create` checks the native ABI version
(`yarg_audio_get_abi_version` must equal 1), then calls
`yarg_asio_router_create` with a config struct:

```c
struct yarg_asio_router_config {
    uint32 size;          // ABI size check
    int32  bass_device_id; // BASS device for worker/callback thread-local selection
    uint32 sample_rate;
    uint32 channels;       // 2 (stereo)
    uint32 callback_frames;// ASIO callback size in frames
};
```

### 6.2 Render-ahead worker

`RenderAheadMixer` runs a worker thread that pulls the song mixer through BASS
in bounded **128-frame** chunks into an SPSC `AudioRingBuffer`:

- Target queue: configured render-ahead duration (30 ms managed-side, never
  less than two ASIO callback buffers).
- Low-watermark refill: when the queue drops below the watermark, the worker
  renders ahead back to target.
- `prefill(timeout)` — fills the ring to target before enabling song output
  (blocks up to 2 s).
- `flush` / `clear` — drops queued song frames (used on seek and first play).
- `stop()` — joins the worker before router teardown.

### 6.3 Output callback

```text
callback:
  zero output buffer
  gate on state (STOPPING → return silence)
  read QPC timestamp + consumed-song-frames → publish clock
  if songEnabled:
      consume frames from ring (count underrun if short)
  if frames <= callback_frames:
      pull live mixer directly through BASS into scratch
      add live to song frames
  apply master volume
  return interleaved float stereo
```

Details:

- **Song gating.** `SetSongEnabled(false)` closes the song gate during flush
  and prefill; the callback then outputs live audio only. When the gate is
  closed the callback consumes zero song frames, which is *not* counted as an
  underrun; `songRequested && consumed < frames` is.
- **Live pull.** Pulled via `BassMix.ChannelGetData` on the live mixer with
  the thread-local BASS device set. Callback sizes larger than the configured
  `callback_frames` are rejected.
- **Underruns.** `underrunFrames`/`underrunEvents` accumulate and the state
  machine moves to `STARVED`; a failed worker moves to `SOURCE_FAILED`.
  Either invalidates the clock.
- **Volume.** Atomic float applied per callback; set from managed code via
  `yarg_asio_router_set_volume`.

### 6.4 Stats and state machine

`AsioMixerRouterStats` (returned by `yarg_asio_router_get_stats`, logged at
backend dispose):

```text
state, last_error, queued_frames, minimum_queued_frames,
produced_frames, consumed_song_frames, requested_output_frames,
underrun_frames, underrun_events, maximum_render_nanoseconds
```

States: `Created → Attached → Prefilling → Ready → Running` with terminal
`Starved`, `SourceFailed`, and teardown `Stopping → Stopped`.

### 6.5 Clock

Each callback publishes a `yarg_asio_router_clock` sample:

```c
struct yarg_asio_router_clock {
    uint32 size;
    uint32 valid;
    uint32 sample_rate;
    uint32 callback_frames;
    int64  performance_frequency;
    int64  callback_timestamp;      // QPC at callback start
    uint64 consumed_song_frames;    // frames consumed from the ring so far
    uint64 requested_output_frames;
    uint32 queued_frames;
    uint32 generation;              // bumped when clock invalidated
};
```

Written with a seqlock-style sequence counter so readers can detect torn
samples. Managed access is `BassAsioMixerRouter.TryGetClock`. The clock is
invalidated on underrun, source failure, flush, and output disable.

### 6.6 Source position

`yarg_asio_router_get_source_position(router, source, output_latency_frames)`
reconstructs a tempo stream's heard position from the song mixer's
`MixerPositionEx` history minus render-ahead and hardware latency. It is the
backbone of `BassAsioOutputBackend.GetSongPosition`:

```csharp
return _mixerRouter?.GetSourcePosition(tempoStreamHandle, _latencyFrames) ?? -1;
```

BASSmix owns source-position semantics (tempo, scheduled delays, seek
boundaries); reconstructing from the output-frame clock alone loses that
information. The mixer's `ChannelAttribute.MixerLatency` is set to
`CommandLatencyFrames / sampleRate` so BASSmix's own history matches the
transport.

---

## 7. Latency and Synchronization Model

| Quantity | Definition |
| --- | --- |
| `_latencyFrames` | `BassAsio.GetLatency(false)` — ASIO driver/hardware output latency. |
| `RenderAheadFrames` | `ceil(rate * 30ms)`, never below `2 * callback_frames`. |
| `CommandLatencyFrames` | `_latencyFrames + RenderAheadFrames` — delay between a BASS command and it being heard. |
| `HeardLatencyMilliseconds` | `_latencyFrames` in ms; surfaced via facade for the latency display. |
| `PlaybackStartDelay` | `_latencyFrames / rate` — startup delay for position anchoring. |
| `GetTempoCommandDelay` | `CommandLatencyFrames / rate` — feeds the song-sync control model. |

Song sync uses `BassSongPlayback.GetPosition()` (→ `GetSongPosition`, native
source position) and `GetLatency()` (→ `GetTempoCommandDelay`). See
[`song_sync.md`](song_sync.md) for how heard vs control position are
reconciled; the ASIO backend supplies both inputs without knowing the sync
model.

**Prefill and seeks.** The first `PlaySong` after attach must discard the
initial transport prefill (silence, because songs attach after transport
start). `_songNeedsPrefill` tracks this:

1. `PlaySong`: `FlushMixer` (drop stale silence) → unpause source →
   `Prefill` (render real audio into ring) → `SetSongEnabled(true)`.
2. `PrepareSongForSeek` on a started song: flush song buffer, re-arm
   `_songNeedsPrefill`; unstarted previews never flush (they share the mixer
   with a fading song).
3. `ResetSongAfterSeek` resets the shared song-mixer position only for started
   songs, so `MixerPositionEx` history does not retain pre-seek offsets.
4. `PauseSong` pauses the source; `UpdateNativeSongEnabled` closes the song
   gate when nothing is playing so the ring drains.

---

## 8. ASIO Inputs

### 8.1 Input pool

During initialization, `CreateInputPool` walks `BassAsio.Info.Inputs` and
creates one `BassAsioInput` per channel:

- Root handle: BASS **push stream** (`Float | Decode`, mono, driver sample
  rate) — the sink that `BassAsio.ChannelEnableBass` writes into.
- `AsioInputDescriptor` per channel: driver id, driver name, channel index,
  channel name, group, sample rate. Cached in `_inputDescriptors` (sorted by
  channel index) and exposed via `GetInputDescriptors`.

### 8.2 Activation

ASIO input channels are enabled lazily on first acquisition, because enabling
changes the driver's channel configuration:

```text
BassAsio.Stop()
ChannelEnableBass(input)
ChannelSetFormat(Float)
ChannelSetRate(driver sample rate)
AttachToOutputMixer(live mixer)
BassAsio.Start(bufferLength, 1 thread)
```

If configuration fails, the channel is reset
(`ChannelReset(Enable|Format|Rate)`) and the driver restarted.

### 8.3 Per-input graph: split streams

`BassAsioInput.AttachToOutputMixer` builds:

```text
ASIO input channel
  └─ root BASS push stream
       ├─ monitor split stream  ─▶ native Freeverb (optional) ─▶ live mixer (audible)
       └─ analysis split stream (SplitSlave) ─▶ managed mic analysis pipeline
```

- The **monitor branch** carries reverb and feeds the live mixer, so
  monitoring stays entirely inside the native ASIO graph.
- The **analysis branch** is `SplitSlave`: it only consumes audio the output
  has already pulled, so analysis can never stall monitoring. It feeds
  `BassMicAnalysisPipeline` for pitch/level analysis — effects never
  contaminate the raw signal.
- Both branches reset independently (`ResetMonitorToLive` vs analysis reset).

### 8.4 Leases

`BassAsioInput` grants exclusive access via `BassAsioInputLease`
(`TryAcquire`): one lease per input; second acquisition returns
`AlreadyInUse`. The lease implements `IBassMicSampleSource`
(`Read`, `GetBacklogBytes`, `Reset`, monitoring control) and is:

- released by the consumer (`Dispose`),
- invalidated by the backend on shutdown (`Invalidate` → all reads fail),
- guarded by a lock; `OwnsLease` checks keep stale leases out.

Acquire results: `Success`, `NoAsioBackend`, `DriverMismatch` (lease requested
for a different driver than the active one), `UnavailableChannel`,
`AlreadyInUse`.

### 8.5 Microphone integration

Microphones come from the **active transport**. The manager's input overrides
are pure delegation:

```csharp
GetInputDevice(name)                      → _currentTransport?.CreateInputByName(name)
GetAllInputDevices()                      → _currentTransport.GetInputs()  // descriptors
CreateInputDevice(deviceId, name)         → _currentTransport?.CreateInputByChannel(deviceId, name)
```

`BassAsioAudioTransport.GetInputs` walks the backend's input descriptors and
builds one `AudioInputDescriptor` per channel with the display name
`"ASIO: <driver> - <channel>: <name>"` (ChannelId = channel index; the name
remains the serialized mic identity). `CreateInputByName` resolves the
descriptor and hands it to `BassAsioMicDevice.Create(transport, descriptor,
name)`, which acquires the lease through the transport:

```csharp
var result = transport.TryAcquireInput(descriptor.DriverId,
    descriptor.ChannelIndex, out var lease);
```

`BassAsioMicDevice` owns the lease, runs the shared `BassMicAnalysisPipeline`
(input level metering included — the same path as any mic), applies the
vocal-monitoring setting through the lease (native-graph monitoring — no BASS
record device involved), and on dispose joins the analysis worker before
releasing the lease. ASIO mic names are only valid while the owning ASIO
driver is active.

The manager carries no ASIO vocabulary here: with a shared transport active
the same three overrides resolve BASS record devices via
`BassSharedAudioTransport`.

### 8.6 Driver notifications

The backend registers `BassAsio.SetNotify` for driver notifications. `Reset`
and `Rate` (sample-rate or settings change) trigger a reinitialization:

```text
driver callback ─▶ queue flag (Interlocked, coalesced)
                ─▶ UnityMainThreadCallback (HandleAsioNotification)
                ─▶ backend ctor callback (_asioReinitializeRequested)
                ─▶ transport raises AudioTransport.ReinitializeRequested
                ─▶ manager OnTransportReinitializeRequested ─▶ ReinitializeOutput(_bufferLength)
```

The backend's notification callback is a constructor-injected `Action`; the
transport supplies it (`NotifyReinitializeRequested`) and forwards to the
`AudioTransport.ReinitializeRequested` event, which the manager subscribes
per active transport. Reinit runs on the Unity main thread (never inside the
driver callback) and reuses the normal device-switch path: suspend backend,
recreate, restore.

---

## 9. Buffer-Length Configuration

`BassAsioAudioTransport.GetBufferInfo` (ASIO only) reads driver capabilities:

- `MinBufferLength` / `MaxBufferLength` / `BufferLengthGranularity`.
- Granularity `-1` → powers of two; `> 0` → fixed step; `0` → single size.
- `PreferredBufferLength` always included if in range.
- `IsDriverControlled` = granularity `0` and min == max (no choice offered).

`OutputBufferSizeSetting` renders a dropdown (value `0` = driver preferred).
Per-device buffer lengths persist in settings
(`SettingsManager.GetAsioBufferLength(outputDevice)`). Changing the value
calls `ReinitializeOutput`, which rebuilds the active transport with the new
buffer length (used as `_callbackFrames`; `BassAsio.Start` receives it).

---

## 10. Lifecycle

### 10.1 Initialization order

```text
BassAsio.Init(deviceId, AsioInitFlags.Thread)   // dedicated processing thread
validate active sample rate (BassAsio.Rate)
CreateOutputMixers(rate)                        // song + live decode mixers
CreateInputPool()                               // one BassAsioInput per channel
ConfigureOutputTransport()
  ├─ BassAsioMixerRouter.Create(bassDeviceId, rate, 2, callbackFrames)
  ├─ AttachMixer(songMixer, 30ms)               // render-ahead target
  ├─ AttachMixer(liveMixer, 0)                  // direct, no buffering
  ├─ SetVolume, Prefill(songMixer, 2s), EnableOutput(0)
StartAsio(bufferLength, 1 processing thread)
CacheInputDescriptors()
RegisterForDriverNotifications()
ConfigureOutputLatency()                        // read latency, set MixerLatency
```

### 10.2 Teardown order (Dispose)

```text
StopAsio()            // unregister notify, BassAsio.Stop — callbacks quiescent
InvalidateInputs()    // leases break immediately
Dispose router        // safe: ASIO stopped, no active callbacks
BassAsio.Free()       // only if this backend initialized the driver
FreeInputs()          // free push/split streams and reverb
DetachTrackedChannels()
FreeMixers()
```

The native router must be destroyed **after** ASIO is stopped and **before**
the mixers it borrowed are freed. Managed `BassAsioMixerRouter.Dispose` is
explicit; finalization is only a leak backstop.

### 10.3 Device switching

`ApplyOutputDevice` (in `BassAudioManager`) — one generic path for device
switches, buffer changes, and driver-initiated reinitialization:

```text
capture venue samples
unload SFX/drum/vox/metronome/venue samples
resolve candidate transport (BassAudioTransport.Create — no native state)
_audioOutput.SuspendRoutes()           // detach monitors, prepare+detach songs
_audioOutput.DetachBackend()           // drop old backend reference
candidate.Activate(configuration)      // device + backend, owned by transport
MoveActiveMixersTo(candidate.MixerDevice)
_audioOutput.AttachBackend(candidate.Backend, candidate.BassDeviceId)
  └─ AttachSongPlaybacks → AttachMonitorRoutes → RestoreSongPlaybacks
previous.Deactivate(); previous.Dispose()
  └─ transport disposes backend, device lease; context survives via refcount
on failure: move mixers back, re-attach previous backend (never deactivated), restore buffer
reload samples
```

The previous transport stays fully alive (backend included) until the candidate
is confirmed, so rollback is a symmetric re-attach. A failed ASIO initialization
falls back to the Default output (`RestoreDefaultOutput`) with a toast. ASIO
driver settings changes (sample rate) arrive on the backend's notify callback,
hop to the Unity main thread, and raise `AudioTransport.ReinitializeRequested`,
which runs the same path via `ReinitializeOutput`.

---

## 11. Design Notes

- **Transports absorbed the ASIO coupling.** The manager, the facade, and the
  Core control plane contain no ASIO vocabulary; ASIO lives in
  `BassAsioAudioTransport`, the backend, the mic, and the native router. Device
  identity is transport-owned (no `IsAsio` flags, no `TransferOwnershipTo`),
  and buffer config/control panel/inputs/reinit are transport capabilities.
  Remaining residue: the manager's generic `_bufferLength` plumbing and the
  `"ASIO: "` prefix, which serves as display name and as the single
  classification point in `BassAudioTransport.Create`/`GetBackend`.
- **Inputs come from the active transport.** Mic enumeration/creation routes
  through `_currentTransport`; a transport only ever offers its own inputs.
  BASS-record mics with an active ASIO output are therefore unavailable — same
  as before, now structural. WASAPI loopback will be the forward input path.
- **Mic identity is the display name.** `SerializedMic`/profile binding
  resolution is name-based (`GetInputDevice(name)`), so `AudioInputDescriptor`
  ids mirror the legacy names. A future descriptor-based serialization can
  switch without changing the transport surface.
- **Settings classification.** `OutputDeviceSetting` and `IsAsioOutput` query
  `GlobalAudioHandler.GetOutputBackend(name)`, which delegates to the transport
  factory — one source of truth for name→family.
- **Clock API** (`TryGetClock`, `AsioMixerRouterClock`) is published by the
  native router and exposed by the wrapper; the current sync model consumes
  `GetSourcePosition` instead. The clock remains the fallback/reference for
  transport-accurate timing.
- **ABI.** All native handles are ABI version 1; `BassAsioMixerRouter` verifies
  `yarg_audio_get_abi_version` and rejects mismatches. Struct sizes are
  static-asserted on the native side and size-checked at the boundary.

---

## 12. Code Map

Managed:

- [`BassAudioTransport.cs`](../Assets/Script/Audio/Bass/BassAudioTransport.cs) — transport contract, name→transport factory, backend family classification.
- [`BassSharedAudioTransport.cs`](../Assets/Script/Audio/Bass/BassSharedAudioTransport.cs) — shared BASS device transport, record-device inputs.
- [`BassAsioAudioTransport.cs`](../Assets/Script/Audio/Bass/Asio/BassAsioAudioTransport.cs) — ASIO transport: driver inputs, buffer info, control panel, reinit.
- [`BassOutputDevice.cs`](../Assets/Script/Audio/Bass/BassOutputDevice.cs) — BASS device context (no driver identity).
- [`BassDeviceContextLease.cs`](../Assets/Script/Audio/Bass/BassDeviceContextLease.cs) — ref-counted context ownership for overlapping transports.
- [`BassAudioOutput.cs`](../Assets/Script/Audio/Bass/BassAudioOutput.cs) — facade, borrows backend, route reattachment.
- [`BassAsioOutputBackend.cs`](../Assets/Script/Audio/Bass/Asio/BassAsioOutputBackend.cs) — ASIO driver, mixers, inputs, notifications, latency.
- [`BassAsioMixerRouter.cs`](../Assets/Script/Audio/Bass/Asio/BassAsioMixerRouter.cs) — native router P/Invoke, stats/clock structs.
- [`BassAsioInput.cs`](../Assets/Script/Audio/Bass/Asio/BassAsioInput.cs) — input pool, split streams, leases, descriptors.
- [`BassAsioMicDevice.cs`](../Assets/Script/Audio/Bass/Asio/BassAsioMicDevice.cs) — mic adapter over a lease.
- [`BassAudioManager.cs`](../Assets/Script/Audio/Bass/BassAudioManager.cs) — transport switch orchestration, fallback, reinit.

Native:

- [`AsioMixerRouter.cpp`](../Native/YargAudio/src/AsioMixerRouter.cpp) — transport, callback, clock, stats.
- [`RenderAheadMixer.cpp`](../Native/YargAudio/src/RenderAheadMixer.cpp) — worker, prefill, flush.
- [`AudioRingBuffer.cpp`](../Native/YargAudio/src/AudioRingBuffer.cpp) — SPSC ring.
- [`yarg_audio_c_api.cpp`](../Native/YargAudio/src/yarg_audio_c_api.cpp) — C ABI, non-Windows stubs.
- [`yarg_audio.h`](../Native/YargAudio/include/yarg_audio.h) — public structs and entry points.
