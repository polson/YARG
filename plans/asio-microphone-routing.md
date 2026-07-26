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
| BASS/WDM recording stream | `BassMicDevice` | Active WDM mic |
| Monitor decode source | Mic/lease source object | Active mic |
| Backend attachment | `BassAudioOutput` route token | Registered monitor route |
| Vocal processor | Mic device | Active mic |
| Persisted mic selection | `ProfileBindings` | Until user explicitly removes selection |

Raw native ASIO root handles must not escape backend. Client code receives generation-bound lease. Backend can synchronously invalidate and reclaim every lease during shutdown.

## Thread and mutation model

- Main thread owns backend state, routes, leases, topology notifications, and active mic replacement.
- ASIO callback captures timing metadata, updates lock-free/atomic telemetry, and pushes PCM only.
  It does not allocate, log, wait, run DSP, or mutate lifecycle state.
- One lease-owned analysis worker reads its pre-created analysis branch and runs the mic processor.
- Lease shutdown cancels and joins its worker before backend frees or resets branch streams.
- Native BASS/BASSASIO calls never occur while managed registry locks are held.
- Backend shutdown first rejects mutations and stops callbacks, then invalidates leases and waits for
  workers before freeing any stream they can reference.

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
6. `BassAudioOutput` retains registered routes while backend is suspended and reattaches them after resume.
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

Suggested commit: `feat(audio): add ASIO input pool and leases`

Primary files:

- `Assets/Script/Audio/Bass/BassAsioOutputBackend.cs`
- `Assets/Script/Audio/Bass/BassAudioOutput.cs`
- New ASIO input pool/lease types under `Assets/Script/Audio/Bass/`

Initialization order:

1. Initialize the selected ASIO driver and read its active control-panel rate.
2. Validate that rate; fail clearly rather than silently changing it.
3. Create the ASIO master mixer at the active rate.
4. Query driver input count and channel metadata.
5. Create one float decode push root per usable channel.
6. Create each channel's pump, analysis, and monitor splitters before ASIO starts.
7. Add every non-slave pump permanently to the master mixer at volume 0.
8. Register custom `AsioProcedure` callbacks and set input format/rate.
9. Configure output channels at the same active rate.
10. Call `BassAsio.Start` once.

Log and exclude channels that fail optional setup. Do not fail output solely because one optional
input fails, unless the driver cannot start with the remaining configuration. Any failure after
driver initialization follows complete rollback for resources created up to that stage.

Capture timeline created in this phase:

- Each input callback assigns an absolute start frame, frame count, QPC delivery timestamp, and
  backend generation to the PCM block.
- Store callback ranges in a fixed-capacity, allocation-free metadata ring per input channel.
- Each active analysis branch owns a frame cursor. Lease acquisition/reset aligns it to live audio;
  every read advances it by the exact number of returned frames.
- Worker reads correlate their frame ranges with callback metadata before invoking processing.
- Missing/overwritten metadata or splitter overflow is an explicit discontinuity: reset branch and
  processor state, resync to live, and record telemetry. Never invent a Unity-update timestamp.
- Ring capacity must exceed maximum supported splitter lag and be validated at 44.1/48/96 kHz and
  supported buffer sizes.

Lease API shape:

```text
TryAcquireAsioInput(driverIdentity, channelIndex, out lease)
lease.Metadata
lease.AnalysisSource
lease.MonitorSource
lease.Generation
lease.IsValid
```

Exact native handles remain internal. A lease grants exclusive use of pre-created branches and
resets them to live before use. Lease registry rejects a second active acquisition of the same
channel; persisted selections are not deleted. Backend generation changes on every ASIO rebuild.

Mutation and shutdown state machine:

```text
Running
  -> reject new leases/routes
  -> stop ASIO callbacks
  -> invalidate generation and leases
  -> detach monitor branches
  -> cancel and join analysis workers
  -> reset ASIO input/output callback bindings
  -> free pre-created analysis/monitor branches
  -> free pump splitters and roots
  -> free master mixer
  -> free BASSASIO driver
  -> Stopped
```

Never free stream while ASIO callback can reference it. Backend forcibly reclaims lease resources even if client forgot to dispose.

Acceptance:

- Runtime backend pre-enables all usable inputs before start.
- Mixer, callback channels, and input streams use the active driver/control-panel rate.
- Pump keeps root queues bounded with zero selected profiles.
- Lease add/remove only resets pre-created branches and leaves ASIO start count unchanged.
- Duplicate channel lease is rejected clearly.
- Stop/start creates new generation and invalidates old leases.
- Partial initialization rollback leaves no native handles or callbacks alive.
- Callback/route mutations do not deadlock under stress.
- Repeated worker acquire/release and backend stop during worker reads are safe.
- Callback metadata overflow/discontinuity is detected and recovers to live audio.

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

`captureBlockMetadata` includes sample rate, first sample position, sample count, and capture clock anchor. Processor must never invent ASIO timestamps from Unity update time.

Clock mapping consumes the callback metadata ring created in Phase 3:

1. Anchor QPC seconds to `InputState.currentTime` on main/input thread.
2. Use callback sample positions for continuity; use QPC to anchor, not to inject callback jitter into every frame.
3. Compensate ASIO input latency separately from output latency.
4. Treat callback QPC as buffer-delivery time; derive sample times from callback frame range.
5. Keep mapping monotonic across long runs and reject stale generation data.
6. Validate exact offset with physical loopback before final scoring sign-off.

Analysis consumer:

- ASIO callback only timestamps and pushes samples; no pitch/EQ work in callback.
- Lease-owned worker continuously drains analysis slave splitter and feeds processor.
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
- New microphone descriptor/identity types
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

Use stable ASIO driver field exposed by `AsioDeviceInfo` when available, with explicit name fallback. Never encode identity into display name.

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
    -> backend grants generation-bound channel lease
    -> lease resets/grants pre-created analysis and monitor branches
    -> mic starts lease-owned analysis worker
    -> monitor route registers with BassAudioOutput
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
- Invalid generation makes device unavailable and stops worker safely.
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
- Buffer-size and sample-rate changes.
- Scene transitions and microphone restart paths.
- Profile add/remove during gameplay.
- Driver disconnect/reconnect failure handling.
- Partial route/lease creation failure.
- Editor script reload and play-mode exit.
- Application shutdown.
- Rapid route and volume mutation.
- Long detach/reattach and long-run drift.

Add failure injection stages:

```text
after BASS device init
after master mixer
after ASIO input N
after pump attachment
after output enable
before ASIO start
after ASIO start
while acquiring lease
while attaching monitor
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
- Root queued milliseconds.
- Analysis/monitor splitter lag.
- Callback gap/error count.
- Route mutation errors.
- Lease count and invalidation count.
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
NEXT  Phase 3: ASIO input pool + opaque leases and callback timeline
      Phase 4: shared processor + capture clock
      Phase 5: stable identity + selected/active profile state
      Phase 6: ASIO mic device/UI with correct timestamps
      Phase 7: lifecycle matrix + runtime integration tests
```
