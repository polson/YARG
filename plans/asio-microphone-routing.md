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
- Input roots and splitter branches are created before `BassAsio.Start`. Profile changes only
  acquire a lightweight channel lease; they never restart ASIO.
- Phase 3 uses BASSASIO's native `ChannelEnableBass` input bridge. Custom callback timing and the
  processing worker belong to Phase 4, where they are needed.
- ASIO monitoring is backend-local: the lease attaches its monitor splitter directly to the active
  ASIO output mixer. It never enters the persistent WDM route registry.
- ASIO monitor audio uses the same existing gain/reverb behavior as BASS/WDM microphones. A new FX
  chain remains out of scope.
- Leaving the matching ASIO output deactivates the live ASIO microphone but preserves its selected
  identity. Returning to that driver automatically reactivates it, including during gameplay.
- Multiple saved profiles may reference the same ASIO channel. Only one active lease is permitted;
  later simultaneous assignments remain selected but unresolved with a clear UI/log reason.
- Runtime descriptors use the ASIO driver identifier with name fallback. Phase 5 defines
  normalization and persistence. Enumeration index is never persisted.

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
| ASIO analysis read gate | ASIO input lease | Active lease |
| BASS/WDM recording stream | `BassMicDevice` | Active WDM mic |
| WDM monitor decode source | `BassMicDevice` | Active WDM mic |
| ASIO monitor splitter attachment | ASIO input lease/backend | Active lease |
| WDM backend attachment | `BassAudioOutput` route token | Registered source lifetime |
| Vocal processor | Mic device | Active mic |
| Persisted mic selection | `ProfileBindings` | Until user explicitly removes selection |

Raw native ASIO root and branch handles do not escape the backend/input-slot layer. Client code
receives a lease with guarded read and monitor operations. Backend invalidates all leases during
shutdown.

## Thread and mutation model

- Main thread owns backend lifecycle, lease acquisition, and monitor mutation.
- BASSASIO's native input bridge pushes each channel into its BASS root; Phase 3 adds no managed
  input callback or worker.
- Per-input lock serializes lease reads with invalidation and stream teardown.
- Backend shutdown stops ASIO, invalidates leases, frees BASSASIO bindings, then frees input streams.
- Persistent WDM monitor routes keep existing `BassAudioOutput` behavior. ASIO monitor streams never
  leave their owning backend.

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
   them after resume. Phase 3 keeps ASIO input monitoring local to the ASIO backend instead.
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

ASIO inputs do not extend this registry contract. Their monitor splitters attach directly to the
owning ASIO backend and disappear with it; existing WDM push sources keep all behavior above.

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

## Phase 3 - Runtime ASIO input pool and lightweight leases

Suggested commit: `feat(audio): add ASIO input routing`

Primary files:

- `Assets/Script/Audio/Bass/BassAsioOutputBackend.cs`
- `Assets/Script/Audio/Bass/BassAsioInputLease.cs`
- `Assets/Script/Audio/Bass/BassAudioOutput.cs`
- `Assets/Script/Audio/Bass/BassAudioManager.cs`

Keep this phase narrow: open inputs, route them through BASS, and provide exclusive access. Do not
build timing, processing, persistence, or failure-injection frameworks before those features exist.

### Rate-first startup

1. Initialize selected ASIO driver before creating ASIO mixers.
2. Read and validate active control-panel rate. Never assign `BassAsio.Rate`.
3. Create song mixer, output mixer, and render-ahead stream explicitly at active rate.
4. Configure inputs and output, then call `BassAsio.Start` once.

### Input graph

For each physical input channel, create this fixed graph before ASIO starts:

```text
BassAsio.ChannelEnableBass
    -> float mono push root
         +-- non-slave pump splitter -> output mixer at volume 0
         +-- slave analysis splitter -> lease.Read
         +-- slave monitor splitter  -> output mixer while monitoring
```

`ChannelEnableBass` handles callback-to-push-stream transport. Pump keeps root advancing at output
cadence. Monitoring attaches directly to owning backend's output mixer; no generic route token,
generation ID, or cross-backend migration is needed.

Publish immutable runtime descriptors containing driver identifier/name, physical channel index,
channel name/group, active sample rate, and input latency.

### Lease

One lightweight lease per channel:

```text
TryAcquireAsioInput(driverId, channelIndex, out lease)
lease.Descriptor
lease.Read(buffer)
lease.EnableMonitoring(volume)
lease.SetMonitoringLevel(volume)
lease.DisableMonitoring()
lease.Dispose()
```

Acquisition resets analysis branch to live position. Monitoring resets monitor branch before attach.
Per-input lock prevents teardown from racing a native read. Duplicate acquisition returns
`AlreadyInUse`. No worker, callback metadata ring, discontinuity taxonomy, telemetry hierarchy, or
raw native handle escapes in this phase.

### Teardown

1. Stop ASIO callbacks.
2. Invalidate active leases and detach backend-local monitor splitters.
3. Free BASSASIO driver/bindings.
4. Free input splitters and roots, then existing output resources.

Existing WDM monitor routes remain unchanged.

Acceptance:

- Active ASIO driver rate is used without forcing another rate.
- All input channels are enabled before single ASIO start.
- Input roots remain pumped with no active lease.
- Lease acquire/release does not restart ASIO.
- Distinct channels can be leased together; duplicate channel cannot.
- Monitoring toggles by attaching/removing monitor splitter from same output mixer.
- Backend shutdown invalidates leases before freeing their streams.
- Existing ASIO output and WDM microphone behavior remain unchanged.

Deferred deliberately:

- Capture QPC/timestamp contract and processing worker -> Phase 4.
- Persisted driver/channel identity and profile re-resolution -> Phase 5.
- User-facing ASIO mic device -> Phase 6.
- Failure injection, stop-failure quarantine, stress telemetry, and rare-driver hardening -> Phase 7.

## Phase 4 - Shared processing and capture-time contract

Suggested commit: `refactor(audio): share microphone processing and capture clock`

Primary files:

- New `Assets/Script/Audio/Bass/BassMicProcessor.cs`
- Small ASIO capture queue/clock helper under `Assets/Script/Audio/Bass/`
- `Assets/Script/Audio/Bass/BassMicDevice.cs`
- `Assets/Script/Audio/Bass/BassAsioOutputBackend.cs`
- Tests for processor and clock mapping

Extract existing conversion, EQ, amplitude/hit detection, pitch tracking, frame queueing, reset, and
disposal behavior from `BassMicDevice` into `BassMicProcessor`.

Processor input is explicit:

```text
ProcessSamples(samples, captureTime)
```

### ASIO capture handoff

Replace Phase 3's `ChannelEnableBass` input bridge with one small custom callback per backend:

1. Capture QPC at callback entry.
2. Push float PCM into existing BASS root for pumping and monitoring.
3. If channel has active analysis consumer, copy same PCM into fixed preallocated SPSC block queue.
4. Queue item carries exact PCM block, capture start frame, frame count, callback QPC, and one
   `DiscontinuityBefore` bit.
5. Full queue drops analysis block and sets discontinuity bit for next accepted block. Callback does
   not allocate, log, wait, or run pitch processing.

Samples and timing travel in same queue item. Do not build a second metadata timeline or correlate
BASS splitter byte positions back to callback records.

One worker per active ASIO lease drains queue and calls `BassMicProcessor`. Lease invalidation stops
and joins worker before backend frees queue storage. WDM capture keeps existing callback flow but
uses same processor.

### Clock mapping

1. Anchor QPC seconds to `InputState.currentTime` on main/input thread.
2. Treat callback QPC as buffer-delivery time and derive sample time from frame offset.
3. Compensate ASIO input latency.
4. Keep emitted times monotonic.
5. Reset processor and clock state after queue discontinuity.
6. Validate final sign/offset with physical loopback before scoring sign-off.

Tests:

- Empty and partial buffers.
- 16-bit WDM and float ASIO processing equivalence.
- 44.1/48/96 kHz.
- Reset, queue clear, pitch, and amplitude state.
- Queue wrap/full behavior and discontinuity reset.
- Timestamp monotonicity and callback block offsets.
- Input-latency compensation and QPC/InputSystem offset/drift.
- Lease invalidation while worker is active.

Acceptance:

- Existing WDM output remains behaviorally equivalent.
- ASIO callback performs bounded copy/push work only.
- Every processed ASIO sample block carries its own capture timing.
- No Unity update-time timestamp fallback.
- Worker stops before callback queue or backend streams are freed.

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

Normalize Phase 3 runtime `DriverId`: stable ASIO driver field when available, explicit normalized
name fallback otherwise. Never encode identity into display name or persist enumeration index.

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
    -> backend grants channel lease or typed unresolved reason
    -> mic starts Phase 4 capture worker with shared processor
    -> lease enables backend-local monitoring when requested
    -> no ASIO restart
```

Device behavior:

- Feed shared processor from Phase 4 capture queue using callback timing.
- Apply the same existing monitor gain/reverb behavior as BASS/WDM mics; do not introduce a new FX
  chain in this phase.
- Expose raw input level through backend level query.
- Monitoring level controls backend-local monitor splitter without stopping analysis.
- Monitoring off does not disable root, analysis, or raw level.
- Dispose lease after stopping worker; lease detaches monitoring.
- Backend invalidation makes device unavailable and stops worker before freeing capture state.
- Never call BASSASIO lifecycle APIs.

Acceptance:

- ASIO mic monitoring and vocal frames work during gameplay.
- Profile add/remove does not change ASIO start count.
- Monitor off: raw peak and analysis continue, monitor stops.
- Long monitor detach/reattach returns live with stable latency.
- Multiple distinct channels operate independently.
- Duplicate channel assignment fails clearly.
- Switching away from ASIO deactivates device but retains selection.
- Switching back re-creates device against current backend.
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
- Persistent WDM route migration versus backend-local ASIO monitor cleanup.
- ASIO stop/free failures and safe cleanup behavior.
- Buffer-size and sample-rate changes.
- Scene transitions and microphone restart paths.
- Profile add/remove during gameplay.
- Driver disconnect/reconnect failure handling.
- Partial route/lease creation failure.
- Editor script reload and play-mode exit.
- Application shutdown.
- Rapid route and volume mutation.
- Long detach/reattach and long-run drift.

Add focused failure seams only where hardware tests show value. Cover profile/device orchestration
stages such as:

```text
after profile resolves identity
after lease acquisition, before worker start
after worker start, before monitoring enable
while invalidating an active lease
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

- ASIO start count.
- Capture queue depth, dropped-block count, and callback errors.
- Lease count and monitor attach state.
- Input sample/QPC drift.
- No stale native handles after shutdown.

## Commit discipline

Each phase must:

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
DONE  Phase 3: rate-first ASIO input pool + lightweight leases
NEXT  Phase 4: shared processor + capture clock
      Phase 5: stable identity + selected/active profile state
      Phase 6: ASIO mic device/UI with correct timestamps
      Phase 7: lifecycle matrix + runtime integration tests
```
