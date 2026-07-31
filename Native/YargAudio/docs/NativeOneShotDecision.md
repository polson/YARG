# Native one-shot follow-up decision

## Decision

Migrate scheduled one-shot rendering to a dedicated C++ source owned by
`yarg_audio`. Do not add a generic native DSP/stream framework and do not begin
the migration until native Gain packaging and runtime validation are complete
on Linux and macOS.

Gain migration provides enough Windows evidence to choose the callback
boundary:

- Burst Gain reproduced forced-GC skips.
- Native Gain uses the same BASS render path without a managed/Burst callback
  and passed the Windows Unity smoke test.
- Scheduled metronome/crowd one-shots were the original stronger reproducer;
  disabling them removed skips during Quickplay and after returning to Music
  Library.

This supports moving the existing one-shot stream callback and its state into
C++, but does not justify a general-purpose graph, callback SDK, or Freeverb
migration. Quantitative Gain A/B results and Unity Unix runtime results remain
missing; see `WindowsGainProof.md` and `MultiplatformGain.md`.

## Implementation status

- Native one-shot backend is selected by the explicit managed backend switch.
- Windows Unity smoke test passed with forced F8 GC and no audible hitch.
- Native CTest covers renderer and mocked lifecycle behavior.
- `GainIntegration` now covers real BASS one-shot attach, render, mute, pause,
  resync, detach, mixer replacement, and repeated destruction. Windows passed
  locally; GitHub Actions run
  [`30670089826`](https://github.com/polson/YARG/actions/runs/30670089826)
  passed Linux x64 and macOS universal host integration.
- Burst one-shot code remains available for controlled A/B comparison. It is
  not an automatic runtime fallback.

## Boundary

Keep these operations in managed control-plane code:

- Decode the source sample to interleaved float PCM.
- Read song position and playback speed.
- Coordinate song/output lifecycle and report errors.
- Call create, attach, detach, play, update, and dispose operations through a
  thin `SafeHandle` wrapper.

Move these operations into `yarg_audio`:

- Own copied sample PCM and sorted scheduled song positions.
- Own the BASS decode stream and native `STREAMPROC` callback.
- Attach/detach the source through BASSmix.
- Mix active and scheduled playbacks.
- Own all callback-visible state and synchronize control updates.

No stream callback pointer or callback context may originate in C#, Mono,
IL2CPP, or Burst after rollout. Native creation failures disable that one-shot
channel and log once; production must not silently restore the Burst callback.

## Dedicated API shape

Use an opaque `yarg_one_shot_stream`. Additive exports remain compatible with
ABI major version 1. Exact names may change during implementation, but API must
support:

```text
create(config, sample PCM, schedule) -> SafeHandle
attach(mixer, anchor, paused)
detach()
play()
set_volume(float)
set_enabled(bool)
set_paused(bool)
set_anchor(output frame, song position, speed, clear active)
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
- Direct `Play()` requests saturate at the active-playback limit.
- Scheduled starts are sample-frame aligned from anchor, speed, and lead time.
- Pause emits silence without advancing the one-shot cursor.
- Re-anchor can clear active and pending playbacks.

Callback requirements match Gain: no allocation, lock, logging, exception,
BASS call, or managed transition.

Immutable sample/schedule data and audio-thread-only cursor/active-playback
state need no synchronization. Volume, enabled, paused, and pending-play count
use lock-free atomics. Anchor publication must use atomic payload fields plus a
release/acquire generation; one callback attempt reads a coherent snapshot or
defers it to the next buffer. Do not use non-atomic payload fields behind a
seqlock because that is a C++ data race. Do not spin on the audio thread if a
control update is in progress.

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

Port current processor behavior into native unit tests before managed wiring:

- Silence, unity volume, disabled, and paused output.
- Manual, scheduled, overlapping, and saturated playbacks.
- Buffer-boundary and in-buffer scheduled starts.
- Positive speed changes, lead time, seek/re-anchor, and clear-active behavior.
- Interleaved channel parity and no clipping.
- Invalid arguments and malformed byte lengths.
- Concurrent play/volume/enable/pause/anchor updates under ThreadSanitizer where
  supported.

Add mocked lifecycle tests for create failure, attach order, detach order,
reattach, lock failure, remove failure, and safe leak paths. Add real BASS tests
on Windows, Linux, and macOS for sample parity, repeated attach/detach/destroy,
and parent mixer teardown.

Final regression gate uses scheduled metronome/crowd playback with identical
song, device, mixer topology, buffer, and Freeverb state:

1. Burst one-shot baseline.
2. One-shots disabled.
3. Native one-shot replacement.

Capture Quickplay and Music Library underrun events/frames, maximum render
time, and output gaps while forcing repeated full collections. Native one-shot
must match disabled-callback behavior while preserving scheduled audio timing.

## Rollout gates

1. Finish Gain Phase 5: committed Linux/macOS binaries, real-BASS CI, and Mono
   plus IL2CPP runtime checks.
2. Land native processor and mocked lifecycle tests without managed use.
3. Add Windows wrapper behind explicit development selection; retain Burst only
   for controlled A/B.
4. Pass Windows forced-GC and timing parity tests.
5. Package/test Linux x64 and macOS universal implementations.
6. Remove `OneShotProcessor.cs`, `BassNativeStream.cs`, and Gain-independent
   Burst one-shot wiring only after all desktop gates pass.

Freeverb and unrelated managed callbacks remain out of scope. Revisit shared
native abstractions only after Gain and one-shot lifecycle code expose proven,
repeated structure.
