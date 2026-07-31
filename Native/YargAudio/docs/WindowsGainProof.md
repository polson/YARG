# Windows native Gain proof

## Automated evidence

Validated on Windows x64 on 2026-07-31 with the packaged BASS DLL and a freshly
built `yarg_audio.dll`:

- Direct `out SafeHandle` P/Invoke marshaling attaches and disposes correctly.
- Unity, zero, above-one, and negative gains match scalar float multiplication
  bit-for-bit, including negative zero and negative input samples.
- Gain updates affect the next decoded buffer.
- Higher BASS DSP priority runs native Gain before a lower-priority test DSP.
- 1,000 attach/update/dispose cycles complete before parent stream destruction.
- Native unit and mocked lifecycle tests pass.

Run all automated Windows checks with:

```powershell
./scripts/build-native.ps1 -NoCopy
```

Production source audit found no `FunctionPointer`, `BurstCompiler`,
`MonoPInvokeCallback`, managed DSP delegate, or `BASS_ChannelSetDSP` call in
`BassGainDsp.cs` or `BassStemMixer.cs`. Gain attachment enters native code through
`yarg_gain_dsp_attach`; BASS receives the C++ callback from `GainDsp.cpp`.

## Forced-GC gate

Status: native Gain Unity smoke test passed; quantitative three-way A/B was not
captured.

User validation after the automated probe reported normal playback and
normalization behavior with the prescribed Unity test. This verifies Windows
Editor/Mono `SafeHandle` loading and disposal. Preserve this distinction:
underrun counters, loopback capture, and a wired Burst baseline were not
recorded.

The disconnected Burst implementation was removed when Phase 5 began. Its last
version remains available in Git history at commit `31969adc`.

Use controls from `NativeGainDiagnosticBaseline.md`:

1. Keep song, output device, mixer topology, render-ahead buffer, and Freeverb
   state identical.
2. Disable scheduled metronome/crowd one-shots.
3. Record Burst Gain baseline, no-Gain, and native Gain runs.
4. In each run, enter Quickplay and press F8 repeatedly during playback.
5. Return to Music Library and repeat forced collections.
6. Capture router underrun events/frames, maximum render time, and output gaps.
7. Record machine, driver, buffer, and song details with results below.

| Run | Quickplay underrun events/frames | Library underrun events/frames | Max render time | Output gaps |
| --- | --- | --- | --- | --- |
| Burst Gain | pending | pending | pending | pending |
| No Gain | pending | pending | pending | pending |
| Native Gain | not captured | not captured | not captured | none reported |

Pass requires native Gain to match no-Gain behavior and not reproduce Burst
Gain skips. Also verify normalization level changes remain correct.
