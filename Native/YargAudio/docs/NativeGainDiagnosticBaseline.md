# Native Gain diagnostic baseline

## Preserved findings

- Forced full GC took roughly 200 ms before and after entering Quickplay.
- Forced GC before Quickplay did not skip Music Library audio.
- Scheduled metronome/crowd one-shot callbacks enabled produced skips in
  Quickplay and after returning to Music Library.
- Disabling scheduled one-shots removed those skips.
- Burst Freeverb alone did not reproduce the skip.
- Burst Gain on the stem mixer reproduced the skip.

These findings isolate callback/runtime attachment, not scalar gain cost, as the
working hypothesis.

## Required Windows proof controls

Run identical mixer topology, render-ahead buffer, song, output device, and
Freeverb state for:

1. Burst Gain baseline.
2. No Gain callback.
3. Native Gain.

Disable scheduled metronome/crowd one-shots. In Quickplay and Music Library,
force repeated full collections. Capture router underrun events/frames, maximum
render time, and loopback/output gaps.

Numerical router captures and exact machine/audio-device details were not part
of retained notes. Record them before removing Burst Gain A/B path.
