# ASIO Microphone Routing Implementation Plan

## Goal

Support both existing BASS/WDM microphones and ASIO input channels for vocal analysis and monitoring through active output backend.

```text
USB/WDM mic -------------------+
ASIO input channel ------------+--> vocal analysis
                               +--> monitor route
                                      |
                               active output backend
                                  +-- BASS output mixer
                                  +-- ASIO master mixer
```

## MVP boundaries

- `BassAsioOutputBackend` remains sole BASSASIO lifecycle owner.
- Mic devices never call `BassAsio.Init`, `Start`, `Stop`, or `Free`.
- ASIO inputs only exist while matching ASIO output driver is active.
- Profile add/remove and monitor changes never restart ASIO.
- ASIO output driver, rate, or buffer changes may restart ASIO.
- One active profile/lease per ASIO input channel. Inactive saved selections may overlap.
- No independent ASIO input/output drivers.
- No automatic recovery after physical driver removal.
- Missing ASIO selections remain persisted and unresolved.

## Accepted implementation decisions

- ASIO driver/control-panel rate wins. Initialize the driver, read its active rate, then create the
  master mixer and input pool at that rate. Never silently force a different rate.
- An invalid, unavailable, or unsupported active driver rate fails startup with a clear error.
- All root, pump, analysis, and monitor splitters are created before `BassAsio.Start`. Profile
  changes acquire/reset pre-created branches; they do not mutate the native splitter graph.
- Analysis workers exist only while a channel lease is active.
- Every ASIO backend attempt receives a process-unique, monotonically increasing `long` generation
  from `BassAudioOutput`. A newly constructed backend never starts its own generation counter.
- WDM monitor sources are persistent across output backends. ASIO monitor sources are tagged with
  their backend generation and are synchronously removed from the route registry before that
  generation can free its splitter.
- ASIO input push queues are preallocated and explicitly bounded. If the pump stalls, the callback
  drops input, records a discontinuity, and keeps memory bounded rather than growing the queue.
- ASIO monitor audio uses the same existing gain/reverb behavior as BASS/WDM microphones. A new FX
  chain remains out of scope.
- Leaving the matching ASIO output deactivates the live ASIO microphone but preserves its selected
  identity. Returning to that driver automatically reactivates it, including during gameplay.
- Multiple saved profiles may reference the same ASIO channel. Only one active lease is permitted;
  later simultaneous assignments remain selected but unresolved with a clear UI/log reason.
- Driver identity uses a normalized stable driver identifier with name fallback. Enumeration index
  is never persisted.

## Proven mechanics

Completed in commit `ea4516d5` (`test(audio): stabilize ASIO routing test lifecycle`). Hardware tests passed.

Validated graph:

```text
custom ASIO input callback
    |
    v
float decode push root
    +-- clock/pump splitter (non-slave, always in master mixer, volume 0)
    +-- analysis splitter (slave, consumed continuously)
    +-- monitor splitter (slave, attached only while routed)
```

Validated behavior:

- All ASIO inputs can pre-enable before one `BassAsio.Start`.
- Route attach/detach does not restart ASIO.
- Custom callbacks can timestamp input with QPC and push float data reliably.
- Muted pump consumes each root at ASIO output cadence.
- Analysis cannot pull root ahead of monitor.
- Detached monitor splitter lag is bounded by split buffer.
- `BassMix.SplitStreamReset(monitor, 0)` before attach discards stale monitor data.
- Analysis and raw peak continue while monitor is detached.
- Stop/start, multi-channel routing, and callback telemetry pass.

This replaces earlier assumption that every detached splitter needs continuous draining. Root requires continuous consumption; muted pump provides it. Detached slave splitters may lag and must reset before reattachment.

## Ownership model

| Resource | Owner | Lifetime |
|---|---|---|
| BASSASIO driver | `BassAsioOutputBackend` | ASIO backend generation |
| ASIO master mixer | `BassAsioOutputBackend` | ASIO backend generation |
| ASIO input callback/root/pump | `BassAsioOutputBackend` | ASIO backend generation |
| ASIO analysis/monitor splitters | `BassAsioOutputBackend` | ASIO backend generation |
| ASIO callback metadata ring | Backend input slot | ASIO backend generation |
| ASIO analysis worker/read gate | ASIO input lease | Active lease |
| BASS/WDM recording stream | `BassMicDevice` | Active WDM mic |
| WDM monitor decode source | `BassMicDevice` | Active WDM mic |
| ASIO monitor source wrapper | ASIO input lease | Active lease/backend generation |
| Backend attachment | `BassAudioOutput` route token | Registered source lifetime |
| Vocal processor | Mic device | Active mic |
| Persisted mic selection | `ProfileBindings` | Until user explicitly removes selection |

Raw native ASIO root and branch handles must not escape the backend/input-slot layer. Client code
receives a generation-bound lease and an opaque monitor source wrapper. Backend can synchronously
invalidate and reclaim every lease during shutdown.

## Thread and mutation model

- Main thread owns backend state, routes, leases, topology notifications, and active mic replacement.
- ASIO callback captures timing metadata, updates lock-free/atomic telemetry, and pushes PCM only.
  It does not allocate, log, wait, run DSP, or mutate lifecycle state.
- Each metadata ring is single-producer/single-consumer. Callback publishes a slot with a final
  sequence stamp; worker never consumes PCM without matching committed metadata.
- One lease-owned analysis worker reads its pre-created analysis branch through a guarded read API
  and sends managed blocks to a capture sink. Phase 3 uses a test sink; Phase 4 supplies the mic
  processor and clock mapping.
- Lease shutdown rejects new reads, cancels and joins its worker, then waits for any in-flight native
  read before backend frees or resets branch streams.
- `BassAudioOutput` detaches all routes and invalidates routes owned by the outgoing backend
  generation before asking that backend to free streams. Persistent WDM routes remain registered.
- Native BASS/BASSASIO calls never occur while managed registry locks are held.
- Backend shutdown first rejects mutations and stops callbacks, then invalidates leases and waits for
  workers before freeing any stream they can reference.
- If `BassAsio.Stop` fails and the driver is still started, shutdown enters a stop-failed/quarantined
  state. Streams, delegates, and backend references remain rooted; output switching aborts safely.

## Phase 1 - Backend-neutral monitor routing (complete)

Suggested commit: `refactor(audio): add monitor routing contract`

Completed in commit `4efcbbcc`.

Primary files:

- `Assets/Script/Audio/Bass/IBassOutputBackend.cs`
- `Assets/Script/Audio/Bass/BassAudioOutput.cs`
- `Assets/Script/Audio/Bass/BassDeviceOutputBackend.cs`
- `Assets/Script/Audio/Bass/BassAsioOutputBackend.cs`
- New monitor route/source types under `Assets/Script/Audio/Bass/`

Implement:

1. Add backend monitor operations: attach, detach, volume update.
2. Expose registration through disposable route token owned by `BassAudioOutput`.
3. Require monitor sources to be float decoding streams. Do not call `ChannelPlay` on source.
4. `BassDeviceOutputBackend` uses dedicated playable, non-stop monitor mixer.
5. `BassAsioOutputBackend` adds monitor source to existing decoding master mixer.
6. `BassAudioOutput` retains persistent registered routes while backend is suspended and reattaches
   them after resume. Phase 3 adds generation-bound routes, which are invalidated instead.
7. Before attaching on another BASS device, move decoding source with `Bass.ChannelSetDevice` while old device remains alive.
8. Failed output switch moves routes back during rollback.
9. Route token detaches synchronously; caller frees source only after token disposal.
10. Route/source abstraction provides backend-neutral `ResetToLive` behavior before reattach:
    - Push source: flush queued data.
    - Split source: `BassMix.SplitStreamReset(stream, 0)`.
11. Never hold managed route locks while calling BASS/BASSMix.

State rules:

```text
register -> reset-to-live -> backend attach
suspend  -> backend detach, route remains registered
resume   -> move device -> reset-to-live -> backend attach
dispose  -> backend detach -> remove registration -> caller may free source
```

Acceptance:

- Decode monitor source works through normal BASS output.
- Same source works through ASIO master mixer.
- BASS -> ASIO -> BASS preserves route and stream validity.
- Failed switch rollback restores route.
- Long backend suspension does not replay stale queued audio.
- Volume persists across backend switches.
- Duplicate disposal and partial attach failure are safe.

Phase 3 extends this contract with source lifetime. Existing WDM push sources remain persistent and
keep all behavior above. Backend-owned ASIO split sources never migrate or survive their generation.

## Phase 2 - Route existing BASS/WDM microphones (complete)

Suggested commit: `fix(audio): route BASS microphones through active output`

Completed in commit `0dcc0e8b`.

Primary files:

- `Assets/Script/Audio/Bass/BassMicDevice.cs`
- `Assets/Script/Audio/Bass/BassAudioManager.cs`
- Monitor source helper from phase 1

Implement:

1. Change `MonitorPlaybackHandle` from playable push stream to `Float | Decode` push source.
2. Preserve reverb and gain DSP on monitor source.
3. Inject/register source with `BassAudioOutput`; remove direct `Bass.ChannelPlay` ownership.
4. Route monitoring level through route token.
5. Keep recording callback fan-out to monitor source and existing analysis path unchanged.
6. Dispose in strict order: unregister route, stop recording callback, dispose DSP, free monitor source, free recording device.
7. Reset push queue and reverb before route reattachment or mic reset.

Acceptance:

- USB mic capture and vocal analysis unchanged on normal output.
- USB mic monitoring works through normal output and ASIO output.
- USB mic remains alive across ASIO <-> normal output switches.
- Monitoring volume/reverb remain correct.
- Long detach/reconnect has no accumulated latency.
- Output switch rollback does not lose microphone.

This phase resolves USB/WDM monitoring through ASIO before adding ASIO inputs.

## Phase 3 - Runtime ASIO input pool and opaque leases

Suggested commits:

- `refactor(audio): initialize ASIO graph at driver rate`
- `feat(audio): add ASIO capture timeline`
- `feat(audio): add ASIO input leases`

Primary files:

- `Assets/Script/Audio/Bass/BassAsioOutputBackend.cs`
- `Assets/Script/Audio/Bass/BassAudioOutput.cs`
- `Assets/Script/Audio/Bass/BassAudioManager.cs`
- `Assets/Script/Audio/Bass/BassMonitorRoute.cs`
- `Assets/Script/Audio/Bass/IBassOutputBackend.cs`
- New ASIO input pool/lease types under `Assets/Script/Audio/Bass/`
- New managed timeline, lifecycle, and failure-injection tests

Implement as three independently buildable subphases. Do not land the native graph, lock-free
timeline, lease registry, and shutdown refactor as one large change.

### Phase 3A - Rate-first transactional graph

Current production order must be inverted: `BassAsioOutputBackend` currently creates mixers from
`Bass.Info.SampleRate` before `BassAsio.Init`. ASIO initialization and active-rate validation must
happen first. The no-sound BASS device may remain initialized at 44.1 kHz; every stream in the ASIO
pipeline receives the active ASIO rate explicitly.

Initialization order:

1. Allocate a process-unique backend generation in `BassAudioOutput` and construct the backend with
   it. Failed initialization attempts still consume a generation; generations are never reused.
2. Initialize the selected ASIO driver, select its thread-local device context, and read driver
   identity, `AsioInfo`, and active control-panel rate.
3. Validate that rate as finite, positive, supported by BASS stream creation, and representable by
   the integer stream-rate APIs. Fail clearly; never assign `BassAsio.Rate` to force another rate.
4. Capture the effective `BassMix.SplitBufferLength` before creating the first splitter. Do not
   change it mid-generation; remember that changing it is process-global and does not resize
   existing splitter buffers.
5. Create the song mixer, output/master mixer, render-ahead push stream, and all associated stream
   state explicitly at the active driver rate.
6. Query input count and immutable channel metadata. Keep physical channel indices even when the
   usable pool is sparse.
7. For each candidate channel, create a mono float decode push root. Preallocate its queue before
   callbacks can run.
8. Create its pump, analysis, and monitor splitters. Add the non-slave pump permanently to the
   output/master mixer with volume 0.
9. Allocate the fixed metadata ring and callback state, then register the custom input callback and
   set float format plus device-rate/no-resampling configuration.
10. Configure output channels and output callback at the same active rate.
11. Call `BassAsio.Start` once. After success, read actual input/output latency, register
   notifications, increment start telemetry, and publish the backend as `Running`.

Each input slot is a local transaction. Driver/channel-specific metadata, enable, format, or rate
failure may exclude that channel after resetting every setting that was applied and freeing its
slot resources. Failure to reset a callback binding cannot be treated as optional. BASS allocation,
splitter creation, pump attachment, metadata allocation, or other shared/resource failure aborts the
whole initialization rather than silently degrading after an out-of-memory or corrupt-state error.
Zero usable inputs is valid; ASIO output still starts.

Any failure after driver initialization unwinds a resource ledger in reverse order. If callbacks
were ever started, rollback first stops ASIO and verifies `BassAsio.IsStarted == false`. Before
freeing a root, rollback resets every ASIO channel that can still reference it. A start failure is a
full initialization failure; runtime profile operations never retry or restart ASIO.

Input push queues must remain bounded even if the output pump stalls. Reserve queue storage with
`Bass.StreamPutData(root, IntPtr.Zero, reserveBytes)` before enabling callbacks. Callback checks
queued frames before each push. Exceeding the configured cap drops that block, marks a pending
discontinuity for the next accepted block, and updates atomic telemetry without logging or waiting.

### Phase 3B - Capture timeline and discontinuity protocol

Each usable channel owns two monotonic frame domains:

- Capture frame: advances for every well-formed hardware callback block, including dropped blocks.
- Source frame: advances only for PCM successfully accepted by the BASS push root. Splitter BASS
  byte positions map to this domain.

One committed metadata record describes accepted PCM:

```text
Sequence
Generation
SourceStartFrame
CaptureStartFrame
FrameCount
CallbackDeliveryQpc
DiscontinuityBefore
PublishedSequence
```

Producer protocol in the ASIO callback:

1. Capture QPC immediately on entry and validate callback direction, channel, and whole-float frame
   length.
2. Advance capture-frame accounting for a valid hardware block.
3. Check the root queue cap. A cap hit or failed `StreamPutData` advances no source frames and sets a
   pending-discontinuity flag.
4. For accepted PCM, populate the next ring slot, including any pending discontinuity, then publish
   it with a final `Volatile.Write`/interlocked sequence stamp. The callback never waits for the
   consumer and may overwrite old records while recording overwrite telemetry.
5. Only after successful push and slot publication advance source-frame and producer-sequence state.

PCM can become visible to BASS just before its metadata commit. Consumer therefore looks up and
validates committed metadata before reading the analysis splitter. A not-yet-committed next record
is a transient producer race and is retried; it is not immediately classified as data loss. Record
sequence mismatch, generation mismatch, overwritten metadata, impossible source position, queue
drop, or splitter overflow is an explicit discontinuity.

Lease acquisition aligns analysis to live data as follows:

1. `BassMix.SplitStreamReset(analysis, 0)`.
2. Read the analysis splitter's BASS byte position and convert it to the exact source-frame cursor.
   Do not initialize from the latest callback total, which can include unconsumed root data.
3. Wait for a committed metadata range containing that cursor or beginning at its next source
   frame. Never synthesize a Unity-update timestamp while waiting.

Worker requests at most the remaining frames in one committed callback range, so a delivered block
never silently crosses metadata anchors. Partial native reads advance both cursor and adjusted
metadata by the exact returned frame count. Splitter position is checked against the expected source
cursor around reads. On discontinuity, worker notifies its sink, resets the analysis branch to live,
re-establishes cursor mapping, and increments reason-specific telemetry. Phase 4 makes the mic
processor reset in response to this notification.

Ring capacity is calculated in records from maximum supported splitter lag, active sample rate, and
minimum supported ASIO callback frame count, with explicit safety margin. Validate total allocation
across every input channel before callbacks are enabled. Cover 44.1/48/96 kHz, every supported
buffer size, wraparound, and worst-case splitter lag. Steady-state callback and worker paths use
preallocated storage.

### Phase 3C - Opaque leases, route lifetime, and failure-aware shutdown

Introduce runtime-only identity primitives here:

- `AsioDriverIdentity`: normalized `AsioDeviceInfo.Driver`, with explicit normalized-name fallback.
- `AsioInputDescriptor`: driver identity, physical channel index, name, group, active rate, input
  latency, and backend generation.
- `AsioInputAcquireResult`: success, no ASIO backend, driver mismatch, unavailable channel, already
  leased, shutting down, or internal failure.

Phase 5 persists these identities and migrates profiles; it does not redefine runtime identity.

Lease API shape:

```text
GetAsioInputDescriptors()
TryAcquireAsioInput(driverIdentity, channelIndex, out lease) -> AsioInputAcquireResult
lease.Descriptor
lease.MonitorSource
lease.Generation
lease.IsValid
lease.StartAnalysis(captureSink)
lease.Dispose()
```

`MonitorSource` is opaque to mic/profile code and tagged `BackendGeneration(lease.Generation)`.
Only `BassAudioOutput` and low-level BASS helpers can access its native handle. No analysis handle is
exposed. `StartAnalysis` starts one lease-owned worker with a preallocated buffer and guarded native
read entry. Capture sink receives synchronous managed sample blocks plus metadata and discontinuity
events. A test sink exercises this contract in Phase 3; `BassMicProcessor` implements it in Phase 4.

A lease grants exclusive use of pre-created branches and resets them to live before use. Registry
rejects a second active acquisition of the same channel with a typed reason; persisted selections
are not deleted. `IsValid` is informational only, not synchronization: every native read must enter
the lease read gate, recheck generation/state, and leave the gate in `finally`.

Extend `BassMonitorSource`/route registration with source lifetime:

```text
Persistent                         -- WDM push source; migrate across output backends
BackendGeneration(generation)      -- ASIO split source; never migrate or survive generation
```

`BassAudioOutput` removes and invalidates generation-bound route tokens after callbacks are
confirmed stopped but before backend frees monitor splitters. Persistent WDM routes remain in the
registry and keep existing rollback behavior. A stale ASIO route must never reach
`AttachMonitorRoutes`, `GetDevice`, `MoveToDevice`, or `ResetToLive` after its generation ends.

Shutdown must be failure-aware rather than relying only on `IDisposable`. Add an internal staged
shutdown result/API so `BassAudioOutput.Suspend` and `BassAudioManager.ApplyOutputDevice` can abort
an output switch when the outgoing backend cannot stop safely. `Dispose` uses the same state machine.

Mutation and shutdown state machine:

```text
Running
  -> Stopping: reject new leases/routes and detach active routes
  -> unregister/suppress ASIO notifications
  -> request BassAsio.Stop and verify BassAsio.IsStarted == false
       -> if still started: StopFailed/Quarantined
          retain backend, delegates, streams, leases, and native ownership for retry/process exit
          abort output switch; initialize no replacement ASIO backend
  -> callbacks stopped
  -> invalidate/remove routes tagged with outgoing generation
  -> invalidate generation and leases
  -> reject new lease reads, cancel/join analysis workers, wait for in-flight reads
  -> reset ASIO input/output callback bindings
  -> free pre-created analysis/monitor branches
  -> free pump splitters and roots
  -> stop/free render-ahead stream and free output/song mixers
  -> free BASSASIO driver
  -> Stopped
```

Never free a stream while an ASIO callback, output callback, analysis read, render worker, or route
can reference it. Backend forcibly invalidates and reclaims leases even if client forgot to dispose,
but it never claims successful cleanup after a failed stop. Do not clear callback delegates or drop
the final managed backend reference while native code may still call them.

Phase 3 tests include a minimal injectable native/lifecycle seam for every initialization stage
introduced here. Keep standalone hardware routing tests separate from deterministic managed tests.
Failure injection now covers rate validation, input N setup, pump attachment, output binding, before
start, after successful start, lease acquisition, active read invalidation, route invalidation, stop
failure, and callback reset failure. Phase 7 adds cross-feature failures rather than deferring these
ownership tests.

Acceptance:

- Runtime backend pre-enables all usable inputs before start.
- Song/output mixers, render-ahead stream, callback channels, and input streams use the active
  driver/control-panel rate without assigning a different `BassAsio.Rate`.
- ASIO output starts and works with zero usable inputs or one excluded input.
- Pump keeps root queues bounded with zero selected profiles; simulated pump stall cannot grow them
  beyond configured cap and produces a discontinuity.
- Lease add/remove only resets pre-created branches and leaves ASIO start count unchanged.
- Duplicate channel lease returns a typed conflict reason.
- Stop/start and failed rebuild attempts consume unique generations and invalidate old leases.
- Generation-bound monitor routes are removed before their source is freed and are never migrated;
  persistent WDM routes still survive successful switch and rollback.
- Partial initialization rollback leaves no native handles or callbacks alive.
- Failed `BassAsio.Stop` leaves all callback-reachable resources alive and blocks replacement backend
  initialization.
- Callback/route mutations do not deadlock under stress.
- Repeated worker acquire/release and backend stop during worker reads are safe.
- Callback metadata publication races, wrap/overwrite, queue drops, splitter overflow, and cursor
  mismatch are detected, reported by reason, and recover to live audio.

## Phase 4 - Shared processing and capture-time contract

Suggested commit: `refactor(audio): share microphone processing and capture clock`

Primary files:

- New `Assets/Script/Audio/Bass/BassMicProcessor.cs`
- New ASIO/QPC clock helper under `Assets/Script/Audio/Bass/`
- `Assets/Script/Audio/Bass/BassMicDevice.cs`
- Tests for processor and clock mapping

Extract from `BassMicDevice`:

- Sample conversion and buffering.
- EQ-fed sample handling.
- Amplitude and hit detection.
- Pitch tracking.
- `MicOutputFrame` queueing.
- Reset, sample-rate change, queue clear, and disposal state.

Processor contract must receive timing explicitly:

```text
ProcessSamples(samples, captureBlockMetadata)
```

`captureBlockMetadata` includes sample rate, generation, source-frame position, capture-frame
position, sample count, callback-delivery QPC, and offset within the original callback range.
Processor must never invent ASIO timestamps from Unity update time.

Clock mapping consumes the callback metadata ring created in Phase 3:

1. Anchor QPC seconds to `InputState.currentTime` on main/input thread.
2. Use capture-frame positions for clock continuity and source-frame positions only to correlate
   BASS reads. Use QPC to anchor, not to inject callback jitter into every frame.
3. Compensate ASIO input latency separately from output latency.
4. Treat callback QPC as buffer-delivery time; derive sample times from callback frame range.
5. Keep mapping monotonic across long runs and reject stale generation data.
6. Validate exact offset with physical loopback before final scoring sign-off.

Analysis consumer:

- ASIO callback only timestamps and pushes samples; no pitch/EQ work in callback.
- Phase 3 lease-owned worker remains the sole analysis reader. Phase 4 supplies
  `BassMicProcessor` as its capture sink; mic code does not receive a native analysis handle.
- Discontinuity events reset processor buffering, pitch/amplitude state, and output queues before
  the first resynchronized block is accepted.
- Worker wake/cadence keeps lag bounded well below split buffer.
- Worker shutdown completes before branches are freed.

Tests:

- Empty/partial buffers.
- 16-bit WDM and float ASIO equivalence.
- 44.1/48/96 kHz sample-rate changes.
- Reset and queue clearing.
- Pitch/amplitude state reset.
- Timestamp monotonicity and block boundaries.
- Input-latency compensation.
- QPC/InputSystem clock offset and drift.
- Lease invalidation while worker is active.
- Callback metadata wrap, loss, discontinuity, and live resynchronization.

Acceptance:

- Existing WDM output remains behaviorally equivalent.
- Processor has no dependency on arbitrary Unity frame time for supplied samples.
- ASIO timing contract is complete before scoreable ASIO mic exists.

## Phase 5 - Stable identity and selected-vs-active profiles

Suggested commit: `feat(audio): persist backend-specific microphone identity`

Primary files:

- `YARG.Core/YARG.Core/Audio/SerializedMic.cs`
- `YARG.Core/YARG.Core/Audio/AudioManager.cs`
- `YARG.Core/YARG.Core/Audio/GlobalAudioHandler.cs`
- Persisted microphone identity types and adapters for Phase 3 runtime ASIO descriptors
- `Assets/Script/Input/Bindings/ProfileBindings.cs`
- New binding serialization version file
- `Assets/Script/Audio/Bass/BassAudioManager.cs`
- `Assets/Script/Menu/ProfileList/ProfileView.cs`

Persist identity independently from display text:

```text
BackendKind: Bass or Asio
DriverIdentity: ASIO driver identifier; empty for WDM
ChannelIndex: ASIO channel; -1 for WDM
DeviceIdentity/Name: WDM compatibility identity
DisplayName: UI fallback only
```

Backend generation is runtime liveness data and is never persisted.

Reuse Phase 3 `AsioDriverIdentity` normalization: stable ASIO driver field when available, explicit
normalized-name fallback otherwise. Never encode identity into display name or persist enumeration
index.

Profile model:

```text
SelectedMicrophoneIdentity  -- persisted user choice
ActiveMicrophone            -- nullable live MicDevice
```

Rules:

- Explicit user removal clears selected identity and disposes active mic.
- Backend loss/output switch disposes only active mic; selection remains.
- Matching topology return re-resolves selection automatically.
- Missing selection remains visible as unresolved, not deleted.
- Output topology change emits notification; profile bindings re-resolve after backend reaches stable running state.
- WDM devices remain enumerable regardless of ASIO output.
- ASIO descriptors appear only for usable channels on matching active ASIO backend.
- Already leased ASIO channels show unavailable/disabled in selection UI.
- Multiple saved profiles may retain the same channel identity, but only the profile holding the
  active lease resolves. Others show the active-conflict reason without losing selection.

Live replacement lifecycle:

1. Backend publishes topology change only after reaching a stable running/stopped state.
2. Main thread stops the old `MicInputContext` before disposing its active mic.
3. Profile binding resolves selected identity against the new stable topology.
4. If resolution succeeds, create the new mic and restart its context, including during gameplay.
5. If resolution fails, retain selection and expose null/unresolved active state.

Gameplay code must not retain a disposed direct `MicDevice` reference across this replacement.

Serialization:

- Add new binding format version. Do not modify historical version files.
- Migrate old name-only mics to `BackendKind.Bass`.
- Round-trip unresolved ASIO identities.
- Existing profiles migrate without user action.

Acceptance:

- Existing WDM profiles load unchanged.
- ASIO selection survives switch to normal output as unresolved.
- Switching matching ASIO output back reactivates selection.
- Explicit remove prevents later reactivation.
- A second simultaneous ASIO activation remains unresolved with a clear UI/log message; its saved
  selection remains intact.

## Phase 6 - ASIO microphone device with correct timestamps

Suggested commit: `feat(audio): add ASIO microphone devices`

Primary files:

- New `Assets/Script/Audio/Bass/BassAsioMicDevice.cs`
- `Assets/Script/Audio/Bass/BassAsioOutputBackend.cs`
- `Assets/Script/Audio/Bass/BassAudioOutput.cs`
- `Assets/Script/Audio/Bass/BassAudioManager.cs`
- Profile mic UI

Creation flow:

```text
profile resolves selected ASIO identity
    -> backend grants generation-bound channel lease or typed unresolved reason
    -> lease resets/grants pre-created analysis and monitor branches
    -> mic supplies shared processor as capture sink and starts lease-owned worker
    -> generation-bound monitor route registers with BassAudioOutput
    -> monitor reset-to-live before mixer attach
    -> no ASIO restart
```

Device behavior:

- Feed shared processor from analysis worker using callback sample metadata.
- Apply the same existing monitor gain/reverb behavior as BASS/WDM mics; do not introduce a new FX
  chain in this phase.
- Expose raw input level through lease/backend telemetry.
- Monitoring level controls route without stopping analysis.
- Route deselection/reset does not disable root or raw level.
- Dispose route before releasing monitor branch and lease.
- Backend invalidation removes the generation-bound route before freeing its source, makes device
  unavailable, and stops worker safely.
- Never call BASSASIO lifecycle APIs.

Acceptance:

- ASIO mic monitoring and vocal frames work during gameplay.
- Profile add/remove does not change ASIO start count.
- Monitor off: raw peak and analysis continue, monitor stops.
- Long monitor detach/reattach returns live with stable latency.
- Multiple distinct channels operate independently.
- Duplicate channel assignment fails clearly.
- Switching away from ASIO deactivates device but retains selection.
- Switching back re-creates device with correct generation.
- Timestamp tests pass before merge; no Unity-update-time fallback.

## Phase 7 - Lifecycle hardening and integration coverage

Suggested commits:

- `fix(audio): harden ASIO microphone lifecycle`
- `test(audio): add runtime ASIO integration coverage`

Primary files:

- `BassAudioOutput.cs`
- `BassAudioManager.cs`
- `BassAsioOutputBackend.cs`
- Both mic devices and route/lease helpers
- Editor runtime test tabs and managed fake-backend tests

Cover:

- ASIO <-> normal output.
- ASIO driver switch.
- Output switch failure and rollback.
- Persistent WDM route migration versus ASIO generation-route invalidation.
- Stop-failed/quarantined backend behavior and later cleanup retry.
- Buffer-size and sample-rate changes.
- Scene transitions and microphone restart paths.
- Profile add/remove during gameplay.
- Driver disconnect/reconnect failure handling.
- Partial route/lease creation failure.
- Editor script reload and play-mode exit.
- Application shutdown.
- Rapid route and volume mutation.
- Long detach/reattach and long-run drift.

Reuse Phase 3 low-level failure injection in integration scenarios; do not first introduce those
ownership checks here. Extend it with profile/device orchestration stages:

```text
after profile resolves identity
after lease acquisition, before worker start
after worker start, before monitor registration
while invalidating an active generation route
after outgoing backend stop, before replacement initialization
after replacement failure, during rollback/re-resolution
while scene/gameplay context holds an active mic
```

Runtime hardware matrix:

- USB mic -> normal output.
- USB mic -> ASIO output.
- ASIO input -> ASIO output.
- Multiple ASIO channels.
- Profile route changes with invariant ASIO start count.
- 44.1/48/96 kHz.
- Several ASIO buffer sizes.
- 10-30 minute drift run.
- Optional physical click loopback for timestamp/latency validation.

Required telemetry/assertions:

- ASIO generation and start count.
- Root queued milliseconds, queue high-water mark, and dropped-block count.
- Analysis/monitor splitter lag.
- Callback gap/error count, metadata overwrite count, and discontinuities by reason.
- Route mutation errors and persistent/generation-bound route counts.
- Lease count and invalidation count.
- Input sample/QPC drift.
- Shutdown state and stop-failure/quarantine count.
- No stale native handles after successful shutdown; injected stop failure intentionally retains all
  callback-reachable handles until cleanup retry/process exit.

## Commit discipline

Each phase and Phase 3 subphase must:

1. Build independently.
2. Preserve normal BASS output and existing WDM microphone behavior.
3. Include rollback and teardown for resources introduced in that phase.
4. Add tests for newly introduced ownership boundaries.
5. Avoid landing a scoreable ASIO mic before timestamp mapping is correct.
6. Keep low-level smoke tests, standalone routing mechanics, and production integration tests separate.

Do not defer callback safety, ownership rollback, or lease invalidation to final hardening phase. Phase 7 handles cross-feature edge cases, not missing fundamentals.

## Recommended execution order

```text
DONE  Phase 0: standalone lifecycle/fan-out/timestamp proof
DONE  Phase 1: monitor route contract
DONE  Phase 2: existing WDM mic migration
NEXT  Phase 3: ASIO input pool + opaque leases and callback timeline
        3A: rate-first transactional graph
        3B: capture timeline + discontinuity protocol
        3C: leases + route lifetime + failure-aware shutdown
      Phase 4: shared processor + capture clock
      Phase 5: stable identity + selected/active profile state
      Phase 6: ASIO mic device/UI with correct timestamps
      Phase 7: lifecycle matrix + runtime integration tests
```
