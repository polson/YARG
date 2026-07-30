# Native ASIO Mixer Router Plan

## Status

Design plan only. API names and signatures below are provisional. First implementation should be a Windows x64 spike behind a fallback switch.

## Objective

Make ASIO output consume the same two logical mixer legs used by normal BASS output:

```text
song mixer  -> buffered/rendered ahead
live mixer  -> immediate
```

C# should create and populate both mixers, then attach them to ASIO with a buffer length per mixer:

```csharp
_asioRouter.AttachMixer(_songMixerHandle, bufferMilliseconds: 30);
_asioRouter.AttachMixer(_liveMixerHandle, bufferMilliseconds: 0);
```

Buffer semantics:

- `bufferMilliseconds > 0`: render mixer on a native worker into a ring buffer.
- `bufferMilliseconds == 0`: pull mixer directly in the ASIO output callback.
- Negative values: invalid.

This does not turn BASS playback streams into ASIO streams. ASIO-side mixers remain BASS decoding mixers. Backend hides that requirement.

## Why

`BASS_ASIO_ChannelEnableBASS` accepts one decoding channel and pulls it synchronously. Pulling the full song graph in the frequent ASIO callback can require stem mixing, tempo/pitch/whammy processing, and DSP. That work can exceed the callback deadline at small ASIO buffer sizes.

Song audio can tolerate controlled render-ahead latency. Drum samples and microphone monitoring cannot. Native router separates those policies while presenting one simple attachment boundary to C#.

## Current ASIO Path

Current implementation in:

- `Assets/Script/Audio/Bass/Asio/BassAsioOutputBackend.cs`
- `Assets/Script/Audio/Bass/Asio/BassRenderAheadStream.cs`

Current graph:

```text
song decode mixer
    -> managed render worker
    -> BASS push stream
    -> output decode mixer <- samples and monitor sources
    -> BASS_ASIO_ChannelEnableBASS
    -> ASIO
```

Current managed render-ahead code owns:

- High-priority managed thread.
- 128-frame render chunks.
- BASS push-stream queue.
- 30 ms target queue.
- Queue polling and wake event.
- Flush locking.
- Generated-frame tracking.
- Smoothed output clock inferred from queue drains.

## Target ASIO Path

```text
                         native render worker
song decode mixer  ------------------------------> SPSC ring --+
                                                               |
live decode mixer  -------------------- direct native pull -----+-> native ASIO callback
                                                                    -> ASIO output
```

For first version, router supports exactly:

- One buffered stereo song mixer.
- One direct stereo live mixer.
- One stereo float ASIO output pair.

API may use generic `AttachMixer` naming, but implementation should reject unsupported topology rather than silently create unbounded workers. Add broader routing only after a concrete need.

## Scope

### In scope

- Native ASIO output callback.
- Native song render worker.
- Fixed-capacity single-producer/single-consumer ring buffer.
- Direct live mixer pull in ASIO callback.
- Mixing buffered and direct legs into ASIO output.
- Render-ahead flush and prefill.
- Underrun and render telemetry.
- Exact native consumption counters and timestamps.
- Heard song-position mapping.
- Thin C# P/Invoke wrapper.
- Windows x64 CMake build and native tests.
- Existing managed implementation retained temporarily as fallback.

### Out of scope

- Replacing normal BASS device output.
- Rewriting ASIO device selection, input discovery, or driver notifications.
- Rewriting microphone input transport unless measurements require it.
- Migrating gain, Freeverb, or other Burst DSP.
- Generic audio bus framework.
- Generic native DSP graph.
- macOS/Linux support.
- Intercepting BASS's normal device output.

## Ownership

```text
BassAsioOutputBackend
  owns song decode mixer
  owns live decode mixer
  owns BassAsioMixerRouter
  owns ASIO initialization/start/stop and input pool

BassAsioMixerRouter (C#)
  owns native router handle
  borrows BASS mixer handles

AsioMixerRouter (C++)
  owns native ASIO output callback registration
  owns buffered-mixer worker and ring
  borrows source mixer handles
  does not free source mixers
```

Required destruction order:

```text
stop ASIO
-> disable native output callback
-> destroy router/workers
-> free live and song mixers
-> free ASIO device
```

Exact `BASS_ASIO_Free` ordering should be validated in spike. No callback may retain router state after destruction begins.

## Proposed C# Surface

New file:

```text
Assets/Script/Audio/Bass/Asio/BassAsioMixerRouter.cs
```

Provisional API:

```csharp
internal sealed class BassAsioMixerRouter : IDisposable
{
    public static BassAsioMixerRouter? Create(
        int bassDeviceId,
        int sampleRate,
        int channels);

    public bool AttachMixer(int mixerHandle, int bufferMilliseconds);
    public bool EnableOutput(int firstAsioChannel);
    public bool Prefill(int mixerHandle, int timeoutMilliseconds);
    public bool FlushMixer(int mixerHandle);

    public long GetSourcePosition(
        int sourceHandle,
        int hardwareLatencyFrames);

    public AsioMixerRouterStats GetStats();
    public bool SetVolume(double volume);
    public void Dispose();
}
```

Lifecycle contract:

```text
Create
-> AttachMixer(song, 30)
-> AttachMixer(live, 0)
-> Prefill(song)
-> EnableOutput
-> BassAsio.Start
-> running
-> BassAsio.Stop
-> Dispose
```

`AttachMixer` accepts milliseconds because settings and user intent use time. Native code converts once using fixed sample rate:

```text
targetFrames = ceil(bufferMilliseconds * sampleRate / 1000)
```

Minimum target should also account for ASIO callback size. Final rule should be established after profiling, likely:

```text
targetFrames = max(configuredFrames, 2 * callbackFrames)
```

Callback frames may not be known before `BassAsio.Start`; pass preferred/configured callback frames in router config or finalize target during output enable.

## Proposed Native C ABI

New public header:

```text
Native/YargAudio/include/yarg_audio.h
```

Provisional ABI:

```c
#define YARG_AUDIO_ABI_VERSION 1

typedef struct yarg_asio_router yarg_asio_router;

typedef struct yarg_asio_router_config {
    uint32_t size;
    int32_t bass_device_id;
    uint32_t sample_rate;
    uint32_t channels;
    uint32_t callback_frames;
} yarg_asio_router_config;

typedef struct yarg_asio_router_stats {
    uint32_t size;
    uint32_t state;
    int32_t last_error;
    uint32_t queued_frames;
    uint32_t minimum_queued_frames;
    uint64_t produced_frames;
    uint64_t consumed_song_frames;
    uint64_t requested_output_frames;
    uint64_t underrun_frames;
    uint64_t underrun_events;
    uint64_t maximum_render_nanoseconds;
} yarg_asio_router_stats;

YARG_AUDIO_API uint32_t yarg_audio_get_abi_version(void);

YARG_AUDIO_API int32_t yarg_asio_router_create(
    const yarg_asio_router_config* config,
    yarg_asio_router** router);

YARG_AUDIO_API int32_t yarg_asio_router_attach_mixer(
    yarg_asio_router* router,
    uint32_t mixer_handle,
    uint32_t buffer_milliseconds);

YARG_AUDIO_API int32_t yarg_asio_router_prefill(
    yarg_asio_router* router,
    uint32_t mixer_handle,
    uint32_t timeout_milliseconds);

YARG_AUDIO_API int32_t yarg_asio_router_enable_output(
    yarg_asio_router* router,
    uint32_t first_asio_channel);

YARG_AUDIO_API int32_t yarg_asio_router_flush_mixer(
    yarg_asio_router* router,
    uint32_t mixer_handle);

YARG_AUDIO_API int64_t yarg_asio_router_get_source_position(
    yarg_asio_router* router,
    uint32_t source_handle,
    uint32_t output_latency_frames,
    int32_t* error);

YARG_AUDIO_API int32_t yarg_asio_router_get_stats(
    yarg_asio_router* router,
    yarg_asio_router_stats* stats);

YARG_AUDIO_API int32_t yarg_asio_router_set_volume(
    yarg_asio_router* router,
    float volume);

YARG_AUDIO_API void yarg_asio_router_destroy(
    yarg_asio_router* router);
```

ABI requirements:

- `extern "C"` exports.
- Explicit calling convention.
- Fixed-width primitive types.
- `size` field on input/output structs.
- No STL, exceptions, strings, or references across boundary.
- No exception may escape export.
- ABI version checked by C# before router creation.
- Error state stored per router; no global mutable error string.

## Proposed Native Layout

```text
Native/YargAudio/
  CMakeLists.txt
  CMakePresets.json
  include/
    yarg_audio.h
  src/
    AsioMixerRouter.cpp
    AsioMixerRouter.h
    RenderAheadMixer.cpp
    RenderAheadMixer.h
    AudioRingBuffer.cpp
    AudioRingBuffer.h
    yarg_audio_c_api.cpp
  tests/
    AudioRingBufferTests.cpp
    RenderAheadMixerTests.cpp
    RouterLifecycleTests.cpp
```

Unity output:

```text
Assets/Plugins/YargAudio/Windows/x86_64/yarg_audio.dll
Assets/Plugins/YargAudio/Windows/x86_64/yarg_audio.dll.meta
```

## Native Components

### `AsioMixerRouter`

Responsibilities:

- Validate format and topology.
- Register native BASSASIO output callback.
- Own direct and buffered mixer attachment records.
- Zero and mix output blocks.
- Apply master volume after combining both legs.
- Record exact callback request counts/timestamps.
- Signal buffered producer when queue falls below low watermark.
- Disable callback safely during shutdown.

### `RenderAheadMixer`

Responsibilities:

- Own one render worker.
- Set correct BASS device on worker thread.
- Pull source decode mixer with `BASS_ChannelGetData`.
- Render fixed-size chunks into preallocated scratch storage.
- Fill ring to high watermark.
- Sleep on event/condition while queue is healthy.
- Handle prefill, flush generation, EOF, source errors, and shutdown.
- Serialize source rendering with source-position queries.

### `AudioRingBuffer`

Responsibilities:

- Fixed-size stereo-float SPSC storage.
- Producer-only write index.
- Consumer-only read index.
- Acquire/release atomic publication.
- Partial read/write and wraparound.
- No allocation, lock, wait, or logging during reads/writes.

Initial implementation can copy through contiguous spans. Optimize only after profiling.

## ASIO Callback Contract

Callback hot path:

1. Validate router still running through stable native state.
2. Zero output block.
3. Read available song frames from ring and mix them into output.
4. Zero-fill missing song frames implicitly by leaving output unchanged.
5. Pull direct live mixer into preallocated scratch storage or output-compatible buffer.
6. Mix live samples into output.
7. Apply master volume and necessary clipping policy.
8. Increment requested, consumed, and underrun counters atomically.
9. Capture callback timestamp/frame anchor.
10. Signal producer if queue crossed low watermark.
11. Return requested byte count.

Callback must never:

- Allocate or free memory.
- Take a contended mutex.
- Wait for worker or C#.
- Log.
- Throw.
- Perform seek/flush.
- Render buffered song source as fallback.

Temporary song underrun emits silence for missing song frames while live audio continues. Persistent source failure is reported to C# outside callback.

Exact BASSASIO joined-channel callback layout must be verified with SDK docs and a minimal test before implementing sample mixing. Do not assume interleaved stereo solely from current managed callback behavior.

## Ring and Watermarks

Initial defaults:

- Render chunk: 128 frames.
- Target/high watermark: configured buffer, at least two ASIO callbacks.
- Low watermark: target minus one or two render chunks.
- Ring capacity: target plus at least two render chunks, rounded for wrap logic.

Worker behavior:

```text
queue below low watermark -> wake
-> render until high watermark
-> sleep
```

Do not busy-poll every 2 ms. Callback signals only when crossing low watermark to avoid event traffic every block.

Thread scheduling:

- Start with dedicated native high-priority thread.
- Do not use time-critical priority.
- Measure before adding MMCSS `Pro Audio` registration.
- Never let producer outrank or starve ASIO driver callback.

## Mixing Rules

Expected format for v1:

- IEEE float.
- Stereo.
- Router sample rate equals ASIO active sample rate.
- Both mixer outputs match router format.

Mixing should match BASS float-mixer behavior closely enough to avoid audible regressions. Establish whether output should clamp before ASIO submission or rely on float output headroom/driver conversion. Add deterministic tests for silence, unity gain, summed signals, and master volume.

Master volume currently applies to `_outputMixerHandle`. Once native router removes final BASS mixer, router must apply master volume after buffered and direct legs are combined. Otherwise master volume would affect only live audio.

Per-song, per-sample, monitor, pan, and speaker flags remain on BASS source/mixer channels.

## Seeking and Flush

Flush must prevent old audio leaking after seek.

Required sequence at backend level:

```text
pause/gate output as required
-> tell router to begin flush
-> stop producer from publishing old generation
-> clear ring
-> seek/reset source graph
-> increment generation
-> prefill new generation
-> resume/gate open
```

Generation rule:

- Worker snapshots generation before rendering.
- Flush increments generation.
- Worker discards rendered chunk if generation changed before publication.
- Consumer sees only current-generation ring contents.

Current code calls source seek before `ResetSongAfterSeek`, so exact ordering through `BassSongPlayback` and `BassAudioOutput` must be traced before final API implementation. Router may need separate `BeginFlush`/`EndFlushAndPrefill` operations if one `FlushMixer` call cannot safely surround external source seek.

Do not lock or clear ring directly from ASIO callback.

## Pause and Commands

Define these semantics before integration:

- Song pause should become silent immediately or only after queued song audio drains.
- Producer should freeze, retain queue, or flush queue while paused.
- Resume should use retained audio or prefill from current source position.
- Volume/fade commands before render-ahead become audible after queued duration.

Likely gameplay policy:

- Pause: immediate output gate plus source/producer pause.
- Seek: invalidate queue and prefill.
- Continuous whammy/tempo controls: accept render-ahead command delay and account for it.
- Master output volume: apply after router mix for immediate response.

Validate against existing playback behavior rather than changing semantics during native migration.

## Position and Latency

Track distinct positions:

```text
render edge: song frames generated into ring
consumed edge: real song frames copied from ring into ASIO output
requested edge: all ASIO output frames requested, including underrun silence
heard edge: consumed/submitted edge minus ASIO hardware latency
```

Counters required:

- Produced song frames.
- Consumed real song frames.
- Requested ASIO frames.
- Underrun silence frames.
- Current queued frames.
- Last callback timestamp and block start.

Frequent underruns break a simple `produced - requested` position calculation because output time advances through silence while song content does not. Count consumed song frames separately.

Current `BassRenderAheadStream.GetSourcePosition` locks rendering and calls `BASS_Mixer_ChannelGetPosition` with delayed bytes. Native replacement should preserve this behavior:

1. Snapshot exact native callback clock.
2. Calculate heard frame using ASIO hardware latency.
3. Calculate delay from current song render edge.
4. Under source-render synchronization, call `BASS_Mixer_ChannelGetPosition`/`BASS_Mixer_ChannelGetPositionEx` equivalent.
5. Fall back for insufficient mixer history exactly as current implementation does.

Song mixer must retain:

```csharp
BassFlags.MixerPositionEx
```

Song source must retain position-history buffering flags required by BASSmix.

Telemetry should distinguish:

- Render-ahead command delay.
- ASIO output latency.
- Total heard song latency.
- Live-path latency, which excludes render-ahead.

## BASS Threading and Device Context

Before coding, verify in BASS/BASSmix documentation or with Un4seen:

- Concurrent mixer source mutation while another thread calls `BASS_ChannelGetData`.
- Required locking around `BASS_Mixer_ChannelGetPositionEx`.
- Behavior when freeing or removing a source during a decode pull.
- Correct device context for decoding mixer calls.
- BASSASIO callback registration and joined stereo format.

Worker must call `BASS_SetDevice`/equivalent for captured BASS device ID before any BASS operation. Never rely on C# caller thread's current device.

If BASS does not guarantee safe concurrent graph mutation, route mutations through native commands or stop/pause producer around C# mixer changes.

## Failure Model

Proposed states:

```text
Created
Attached
Prefilling
Ready
Running
Starved
SourceFailed
Stopping
Stopped
```

Rules:

- Temporary underrun: emit song silence, preserve live leg, increment stats.
- Direct mixer read failure: emit silence for that leg, store error.
- Persistent buffered source failure: enter `SourceFailed`, surface to C#.
- Callback never logs or reinitializes.
- C# polls stats or receives backend-level failure outside callback.
- Destroy has bounded behavior; investigate cancellation if BASS pull can block indefinitely.
- Do not depend on finalizers for orderly shutdown.

## Changes to `BassAsioOutputBackend`

### Fields

Replace:

```csharp
private BassRenderAheadStream? _renderAheadStream;
private int _outputMixerHandle;
```

With:

```csharp
private BassAsioMixerRouter? _mixerRouter;
private int _liveMixerHandle;
```

Keep `_songMixerHandle`.

### Mixer creation

Create both as decode mixers:

```csharp
_songMixerHandle = BassMix.CreateMixerStream(
    frequency,
    2,
    BassFlags.Float |
    BassFlags.MixerNonStop |
    BassFlags.Decode |
    BassFlags.MixerPositionEx);

_liveMixerHandle = BassMix.CreateMixerStream(
    frequency,
    2,
    BassFlags.Float |
    BassFlags.MixerNonStop |
    BassFlags.Decode);
```

### Routing

- `AttachSong` adds tempo stream to `_songMixerHandle`.
- `PlaySample` adds sample to `_liveMixerHandle`.
- `AttachMonitor` adds monitor source to `_liveMixerHandle`.
- ASIO input activation attaches monitor to `_liveMixerHandle`.

### Startup

Replace render-ahead push stream creation, final-mixer attachment, and `ChannelEnableBass` with:

```csharp
_mixerRouter = BassAsioMixerRouter.Create(...);
_mixerRouter.AttachMixer(_songMixerHandle, bufferMilliseconds: 30);
_mixerRouter.AttachMixer(_liveMixerHandle, bufferMilliseconds: 0);
_mixerRouter.Prefill(_songMixerHandle, timeoutMilliseconds: 2000);
_mixerRouter.EnableOutput(firstAsioChannel: 0);
```

C# continues calling `BassAsio.Start` and managing ASIO inputs/notifications.

### Position and command delay

Replace managed queue clock calls with native router position/stats calls. `HeardLatencyMilliseconds`, `PlaybackStartDelay`, and `GetTempoCommandDelay` should use:

```text
native queued song frames + ASIO output latency
```

### Volume

Replace output-mixer volume attribute with router master volume. Preserve cached `_volume` across device reinitialization.

### Teardown

Stop ASIO before router disposal. Dispose router before freeing either mixer.

## Normal BASS Path

Do not change `BassDeviceOutputBackend` in first implementation.

Normal BASS remains:

```text
song playback mixer -> BASS playback buffer -> BASS device
sample mixer with zero buffer -------------> BASS device
monitor mixer with zero buffer ------------> BASS device
```

Shared conceptual model is mixer plus buffering intent. Exact mixer ownership can remain backend-specific until broader pipeline refactoring proves useful.

## Build Pipeline

### Repository placement

Keep source in this repository for atomic C++/C#/ABI changes.

### Initial platform

- Windows x64 only.
- Visual Studio 2022 generator through checked-in CMake preset.
- Build runtime choice (`/MT` versus `/MD`) decided explicitly; prefer no new runtime installer requirement.

### Contributor workflow

Normal contributors:

```text
clone -> open Unity -> build
```

Commit release DLL and Unity `.meta` so C++ tools are not required.

Native contributors:

```powershell
./scripts/build-native.ps1
```

Script should:

```text
configure CMake
-> build Release
-> run native tests
-> copy DLL into Assets/Plugins/YargAudio/Windows/x86_64
```

### CI

Native CI stage:

```text
configure
-> compile
-> test
-> copy plugin
-> verify committed DLL freshness or package artifact
-> Unity build
```

Store PDBs as CI/release debugging artifacts even if not shipped in player.

### BASS dependencies

Resolve before implementation:

- Licensing for BASS, BASSmix, and BASSASIO headers/import libraries in repository.
- Whether CI may download pinned official SDK archives.
- Exact runtime DLL lookup beside Unity plugin.

Preferred order:

1. Vendor permitted pinned headers/import libraries.
2. Download pinned official SDK packages in native build bootstrap.
3. Dynamic symbol lookup only if licensing prevents import-library use.

Dynamic lookup adds runtime validation and failure paths, so avoid unless necessary.

## Testing Strategy

### Native unit tests

`AudioRingBuffer`:

- Empty/full behavior.
- Partial writes and reads.
- Wraparound.
- Exact capacity boundaries.
- Concurrent SPSC stress.
- Clear/reset generation behavior.
- Long-running counter wrap assumptions.

`RenderAheadMixer` with fake source abstraction where possible:

- Initial prefill.
- Producer stalls.
- Partial source reads.
- EOF.
- Source errors.
- Flush during render.
- Generation changes before publication.
- Repeated start/stop.
- Destroy while producer sleeps.

Router/mixer tests:

- Buffered-only output.
- Direct-only output.
- Buffered plus direct summation.
- Song underrun while direct clicks continue.
- Master volume applies equally to both legs.
- Callback sizes smaller/larger than render chunk.
- Unsupported format/topology rejection.

### Integration harness

Create synthetic sources:

- Expensive song decoder/FX simulator.
- Immediate impulse/click source.
- Optional injected 5-20 ms producer stalls.

Exercise ASIO at 32, 64, 128, and 256 frames where driver supports them.

Verify:

- Live click latency remains ASIO-buffer limited.
- Song remains continuous while worker meets target.
- Song underrun does not block live output.
- Counters match injected stalls.
- No callback allocations or managed callbacks.
- No stale audio after seek.
- Position remains monotonic and tracks heard song.
- Driver reset/reinitialize works repeatedly.
- Enter/exit Play Mode and domain reload modes do not leak router instances.

### Regression comparison

Run current managed and new native paths against same source graph:

- Compare rendered song output within float tolerance.
- Compare seek/pause/resume behavior.
- Compare reported song position over time.
- Compare whammy/tempo command delay.
- Compare sample and monitor routing/channel flags.

## Telemetry

Expose enough data for logs/debug UI outside callback:

- Configured target buffer ms/frames.
- Current and minimum queue frames.
- Produced and consumed song frames.
- Requested output frames.
- Underrun events and frames.
- Maximum render-chunk duration.
- Last BASS/BASSASIO error.
- Router state.
- Active native ABI version.

Log summary on ASIO stop or when underrun count changes. Do not log every callback or render chunk.

## Rollout

Keep both implementations temporarily:

```csharp
private const bool USE_NATIVE_ASIO_MIXER_ROUTER = false;
```

Stages:

1. Native DLL loads and reports ABI.
2. Synthetic test scene/harness passes.
3. Router enabled manually for development.
4. Collect telemetry across several ASIO drivers and buffer sizes.
5. Make native router default on Windows x64.
6. Retain managed fallback for one release cycle.
7. Remove `BassRenderAheadStream.cs` after parity and soak period.

Fallback should be explicit and logged. ABI mismatch or DLL load failure should either select managed path or fail ASIO initialization with a clear message; never crash later in callback.

## Implementation Phases

### Phase 0: Validate assumptions

- Read complete BASS/BASSmix/BASSASIO SDK docs for threading, joined callbacks, and position APIs.
- Ask Un4seen whether a supported buffered decode/playback-to-ASIO facility already exists.
- Resolve SDK headers/import-library licensing.
- Trace exact current seek, pause, mixer mutation, and teardown order.
- Capture baseline telemetry from managed render-ahead path.

Deliverable: short design update confirming callback format, locking requirements, and dependency strategy.

### Phase 1: Build skeleton

- Add CMake project and preset.
- Export ABI version and no-op create/destroy.
- Add C# P/Invoke loading and ABI check.
- Add build script and Unity plugin metadata.
- Add CI-native test job.

Deliverable: Unity loads native DLL; CI builds same binary from source.

### Phase 2: Ring and buffered renderer

- Implement/test SPSC ring.
- Implement worker and fake-source tests.
- Integrate BASS decode mixer pull.
- Add prefill, watermarks, stats, source-failure handling.

Deliverable: native worker continuously renders a decode mixer without ASIO.

### Phase 3: Native ASIO router

- Register native output callback.
- Pull direct mixer.
- Consume/mix buffered ring.
- Add master volume and underrun semantics.
- Validate stereo joined-channel format.

Deliverable: synthetic song plus immediate click plays through ASIO.

### Phase 4: Position, seek, and lifecycle

- Add exact callback frame/QPC anchors.
- Implement source-position mapping.
- Implement flush generation and prefill.
- Test pause, seek, repeated reset, and bounded destruction.

Deliverable: behavior matches current backend under stress.

### Phase 5: Backend integration

- Add `BassAsioMixerRouter.cs`.
- Refactor `_outputMixerHandle` into `_liveMixerHandle`.
- Route samples and monitors unchanged into live mixer.
- Replace managed render-ahead startup/position/volume calls.
- Keep old path behind switch.

Deliverable: game can select native router path with no higher-level playback changes.

### Phase 6: Production hardening

- Test supported ASIO devices/drivers.
- Tune watermarks and thread priority from telemetry.
- Validate Unity lifecycle and crash diagnostics.
- Enable native default.
- Remove fallback after soak period.

## Open Decisions

1. **SDK distribution:** vendor import libraries or pinned CI download?
2. **Callback layout:** exact joined stereo buffer format and length semantics?
3. **Mixer mutation:** can C# mutate source mixers concurrently with native decode pull?
4. **Pause policy:** retain queue, flush queue, or immediate gate plus freeze?
5. **Seek API:** single flush or begin/end transaction around external seek?
6. **Master mixing:** clipping/saturation behavior matching current BASS output mixer?
7. **Buffer target:** fixed 30 ms, user setting, or callback-aware calculated minimum?
8. **Failure fallback:** managed router fallback or explicit ASIO initialization failure?
9. **Native dependency runtime:** static import linkage versus dynamic export resolution?
10. **Topology:** enforce one buffered plus one direct mixer in ABI v1 or permit bounded multiple attachments?

## Acceptance Criteria

- C# attaches song and live decode mixers with `30 ms` and `0 ms` respectively.
- No managed callback or managed render worker exists in native path.
- Expensive song graph never renders in ASIO callback.
- Live mixer remains directly pulled and low latency.
- Callback performs no allocation, logging, blocking, or contended locking.
- Song underrun emits silence without interrupting live audio.
- Seek cannot output pre-seek queued audio.
- Heard song position remains monotonic and accurate within one ASIO callback under normal operation.
- Master volume affects song and live legs equally.
- Driver reset and repeated initialization/destruction do not leak or hang.
- Native unit/integration tests pass in CI.
- Normal Unity contributors do not need CMake or Visual Studio C++ installed.
- Normal BASS device backend behavior remains unchanged.
