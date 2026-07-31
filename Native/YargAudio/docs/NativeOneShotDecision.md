# Native one-shot implementation status

## Decision

Scheduled one-shot rendering uses a dedicated C++ source owned by `yarg_audio`.
No generic native DSP/stream framework was added. The migration is complete;
native one-shot is the only production backend.

Gain migration provides enough Windows evidence to choose the callback
boundary:

- Burst Gain reproduced forced-GC skips.
- Native Gain uses the same BASS render path without a managed/Burst callback
  and passed the Windows Unity smoke test.
- Scheduled metronome/crowd one-shots were the original stronger reproducer;
  disabling them removed skips during Quickplay and after returning to Music
  Library.

This supported moving the existing one-shot stream callback and its state into
C++. It does not justify a general-purpose graph, callback SDK, or Freeverb
migration. Gain diagnostics remain documented separately in
`WindowsGainProof.md` and `MultiplatformGain.md`.

## Implementation status

- Native one-shot is the only managed backend; no runtime backend switch or
  Burst fallback remains.
- Windows Unity smoke test passed with forced F8 GC and no audible hitch.
- Native CTest covers renderer and mocked lifecycle behavior.
- `GainIntegration` now covers real BASS one-shot attach, render, mute, pause,
  resync, detach, mixer replacement, and repeated destruction. Windows passed
  locally; GitHub Actions run
  [`30670089826`](https://github.com/polson/YARG/actions/runs/30670089826)
  passed Linux x64 and macOS universal host integration.
- Unity runtime validation passed with the native backend enabled.
- The former Burst one-shot callback and A/B selection code were removed after
  validation. Native creation failures disable that channel; they never fall
  back to managed callback code.

## Boundary

Keep these operations in managed control-plane code:

- Decode the source sample to interleaved float PCM.
- Read song position and playback speed.
- Coordinate song/output lifecycle and report errors.
- Call create, attach, detach, update, and dispose operations through a
  thin `SafeHandle` wrapper.

Move these operations into `yarg_audio`:

- Own copied sample PCM and sorted scheduled song positions.
- Own the BASS decode stream and native `STREAMPROC` callback.
- Attach/detach the source through BASSmix.
- Mix active and scheduled playbacks.
- Own all callback-visible state and synchronize control updates.

No stream callback pointer or callback context originates in C#, Mono, IL2CPP,
or Burst. Native creation failures disable that one-shot channel and log once;
production never restores a managed callback.

## Dedicated API shape

Use an opaque `yarg_one_shot_stream`. Additive exports remain compatible with
ABI major version 1. Exact names may change during implementation, but API must
support:

```text
create(config, sample PCM, schedule) -> SafeHandle
attach(mixer, anchor, playback speed, paused)
resync(mixer, anchor, playback speed)
set_paused(mixer, paused)
set_gain(float)
detach()
destroy()
```

Creation copies all arrays before returning; native code never retains pinned
managed memory. Config uses a `size` field and fixed-width types. Validate:

- Positive sample rate and channel count.
- Non-empty float sample data containing complete frames.
- Finite, non-negative lead time.
- Finite, ascending schedule positions.
- Finite volume and finite positive playback speed updates.
- Float mixer format and compatible channel count before attachment.

Do not expose the native BASS stream handle. Preventing external pulls gives
the native object one callback/lifetime owner.

## Ownership and lifecycle

```text
BassOneShotChannel
  owns NativeOneShotStream SafeHandle
    owns copied sample and schedule arrays
    owns native callback state
    owns BASS decode stream
    borrows currently attached BASS mixer
```

Attach must lock the target mixer, publish the initial anchor and pause state,
then add the source. This prevents a callback from observing an unanchored
stream. Detach must lock the mixer, remove the source, then unlock it. A stream
may be detached and attached to a replacement output mixer without recreation.

Destroy must detach first, free the BASS stream only after callback removal is
synchronized, then free callback state and immutable arrays. Lock/removal
failure retains the native object and stream rather than risking callback
use-after-free. Parent mixer destruction must occur after explicit one-shot
disposal; finalization remains leak prevention, not the normal lifecycle.

Native code owns BASS calls and locks for this object. Managed callers must not
wrap native attach, detach, anchor, or destroy calls in an additional mixer
lock.

## Audio-thread model

Preserve current behavior:

- Interleaved float output, cleared before mixing.
- Up to 64 overlapping playbacks.
- No clipping.
- Scheduled starts are sample-frame aligned from anchor, speed, and lead time.
- Pause emits silence without advancing the one-shot cursor.
- Re-anchor clears active voices and restarts schedule lookup.

Callback requirements match Gain: no allocation, lock, logging, exception,
BASS call, or managed transition.

Immutable sample/schedule data and audio-thread-only cursor/active-voice state
need no synchronization. Gain and paused state use lock-free atomics. Control
updates run under the BASS mixer lock; the callback never spins on a control
operation.

Require lock-free 32-bit and 64-bit atomics on supported 64-bit desktop
targets. Represent float/double values as atomic integer bits, matching Gain.

## Binding changes

Extend existing boundaries instead of introducing another loader:

- Core BASS: `BASS_StreamCreate`, `BASS_StreamFree`,
  `BASS_ChannelGetInfo`, `BASS_ChannelLock`, and error lookup.
- BASSmix: `BASS_Mixer_StreamAddChannel` and
  `BASS_Mixer_ChannelRemove`.

Resolve already-loaded desktop modules and retain binding tables for process
lifetime. One-shot creation requires core BASS; attachment requires BASSmix.
Missing symbols return dependency errors without affecting Gain.

## Required tests

Cover scheduled renderer behavior in native unit tests:

- Silence, unity volume, disabled, and paused output.
- Manual, scheduled, overlapping, and saturated playbacks.
- Buffer-boundary and in-buffer scheduled starts.
- Positive speed changes, lead time, seek/re-anchor, and clear-active behavior.
- Interleaved channel parity and no clipping.
- Invalid arguments and malformed byte lengths.
- Concurrent volume/enable/pause/anchor updates under ThreadSanitizer where
  supported.

Add mocked lifecycle tests for create failure, attach order, detach order,
reattach, lock failure, remove failure, and safe leak paths. Add real BASS tests
on Windows, Linux, and macOS for sample parity, repeated attach/detach/destroy,
and parent mixer teardown.

Final regression gate uses scheduled metronome/crowd playback with identical
song, device, mixer topology, buffer, and Freeverb state. Compare one-shots
disabled against native one-shot playback while forcing repeated full
collections.

Capture Quickplay and Music Library underrun events/frames, maximum render
time, and output gaps while forcing repeated full collections. Native one-shot
must match the disabled-callback baseline while preserving scheduled audio
timing. Windows Unity validation reported no F8 hitch.

## Rollout gates

1. Native renderer and mocked lifecycle tests landed.
2. Windows native wrapper passed forced-GC Unity validation.
3. Real-BASS integration passed on Windows, Linux x64, and macOS universal.
4. Linux/macOS plugin artifacts were rebuilt and committed.
5. `OneShotProcessor.cs`, `BassNativeStream.cs`, `BassOneShotStream.cs`, and
   backend selection wiring were removed.

Freeverb and unrelated managed callbacks remain out of scope. Revisit shared
native abstractions only after Gain and one-shot lifecycle code expose proven,
repeated structure.
