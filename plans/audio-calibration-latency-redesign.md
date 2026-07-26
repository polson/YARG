# Audio Calibration Latency Redesign Plan

## Goal

Make manual audio calibration measure the user's actual audible output delay. Remove hidden
hardware-latency compensation from the user-facing calibration value while preserving startup
latency modeling needed by playback scheduling.

Target semantics:

```text
StartupLatency   = internal stream/control-clock startup model
AudioCalibration = empirically measured command-to-heard audio offset
                  (signed, milliseconds)
InputCalibration = separate per-profile input offset
```

`AccountForHardwareLatency` should no longer change gameplay timing or calibration results. The
setting should be migrated, then removed from the UI and runtime path.

## Problem summary

Current code mixes two different latency concepts:

1. `StartupLatency` models delay between `Play()` and BASS playback-position advancement.
2. `PlaybackLatency` models detected output-device latency.
3. `AudioCalibration` stores a user calibration value, but its reference zero is shifted by the
   estimated startup latency.

Current calibrator path:

```csharp
_audioStartTime = InputManager.CurrentInputTime + BassLatencyProvider.StartupLatency;
...
calibration = median(input.Time - _audioStartTime);
if (AccountForHardwareLatency)
    calibration -= GlobalAudioHandler.PlaybackLatency;
```

Current gameplay path:

```csharp
audioCalibrationMs = Settings.AudioCalibration.Value;
if (AccountForHardwareLatency)
    audioCalibrationMs += GlobalAudioHandler.PlaybackLatency;
```

Consequences:

- Toggle off does not remove `StartupLatency` from calibration zero.
- `StartupLatency` is an estimate, not a per-playback measurement.
- A fixed model error produces a fixed calibration error, such as the reported `-30 ms`.
- Users with the toggle enabled store a residual value, not the full empirical value.
- Deleting the toggle without migration changes existing users' effective audio timing.

Relevant files:

- `Assets/Script/Menu/Calibrator/Calibrator.cs`
- `Assets/Script/Playback/SongRunner.cs`
- `Assets/Script/Audio/Bass/BassLatencyProvider.cs`
- `Assets/Script/Audio/Bass/BassDeviceOutputBackend.cs`
- `Assets/Script/Audio/Bass/BassStemMixer.cs`
- `Assets/Script/Audio/Bass/BufferedPlaybackTimeline.cs`
- `Assets/Script/Settings/SettingsManager.cs`
- `Assets/Script/Settings/SettingsManager.Settings.cs`
- `Assets/StreamingAssets/lang/en-US.json`
- `docs/song_sync.md`

## Design decisions

### 1. Keep startup latency model

Do not remove `BassLatencyProvider.StartupLatency` from playback scheduling. It is needed by
`BufferedPlaybackTimeline.Play()` and output-transition handling to predict when BASS control
position begins advancing.

Startup modeling remains internal. It must not silently define the user's manual calibration zero.

### 2. Make manual calibration empirical

Calibrator should measure the complete audible offset from playback command to the heard tick. Do
not subtract `BassLatencyProvider.StartupLatency` from the calibrator's reference timestamp.

The first implementation should use the timestamp immediately surrounding `_mixer.Play()` as the
command reference. Preserve high-resolution input timestamps and input-event age correction.

Before finalizing the exact formula, verify whether BASS `GetPosition()` and
`BufferedPlaybackTimeline` already include any portion of this delay. The invariant is:

```text
AudioCalibration is applied exactly once to heard-audio positioning.
StartupLatency is applied exactly once to control-clock startup prediction.
```

Do not solve the negative value by clamping calibration to zero. Negative empirical values are
valid when the measured output path is early relative to the selected reference.

### 3. Remove automatic hardware compensation

After migration:

- `SongRunner.UpdateCalibration()` uses `Settings.AudioCalibration.Value` directly.
- `Calibrator.CalculateAudioLatency()` stores the empirical result directly.
- No `PlaybackLatency` addition/subtraction occurs in gameplay or calibration.
- `GlobalAudioHandler.PlaybackLatency` remains available for diagnostics and backend telemetry.

### 4. Remove toggle only after migration

Keep the legacy setting long enough to detect old profiles. Do not immediately delete its serialized
field, because absence of the field loses whether the stored calibration had hardware latency
removed.

Use a settings schema/version marker. Old files without the marker are treated as pre-migration.
Run migration immediately after `SettingContainer` deserialization and before callbacks execute.
Save migrated settings once.

## Migration math

Let:

```text
C_old = stored AudioCalibration before migration
H     = current detected PlaybackLatency
S     = StartupLatency used by old calibrator reference
T     = old AccountForHardwareLatency value
```

Current old calibrator semantics approximately produce:

```text
C_old = empirical command-to-heard value - S - (T ? H : 0)
```

If new calibration stores the full empirical command-to-heard value, migrate with:

```text
C_new = C_old + S + (T ? H : 0)
```

However, this formula must be validated against the final playback-position equations before code
lands. If the engine retains a separate output-start compensation that intentionally excludes `S`,
only add the terms that were previously removed from the stored value. Do not blindly migrate by a
constant without an A/B timing test.

Migration rules:

1. Read legacy toggle state before removing/hiding it.
2. Capture the current output device and measured `PlaybackLatency`.
3. Capture the old startup estimate using the same backend/platform formula used by the old build.
4. Apply conversion once.
5. Mark settings schema as migrated.
6. Never apply conversion again on later launches.
7. Log old value, correction terms, new value, toggle state, output device, and migration version.
8. If output latency cannot be read, do not guess. Preserve value and request recalibration.

Because output devices and buffer sizes can change, migration cannot guarantee perfect timing. Show
a one-time notice recommending recalibration after migration. Users with manually entered values or
missing legacy metadata should be offered recalibration rather than an aggressive conversion.

## Implementation phases

### Phase 0: Instrument current behavior

- Add temporary/debug logging around calibration:
  - `Play()` command timestamp.
  - `_audioStartTime`.
  - `BassLatencyProvider.StartupLatency`.
  - `GlobalAudioHandler.PlaybackLatency`.
  - raw median relative to `Play()`.
  - current final calibration.
  - output device and backend.
- Confirm reported `-30 ms` equals the reference/model discrepancy.
- Test normal BASS output only; ASIO is not required for this change.
- Remove or gate noisy diagnostics before release.

### Phase 1: Define and document timing equations

- Trace `SongRunner.AudioTime`, `SongTime`, `AudioCalibration`, and
  `BassStemMixer.GetPlaybackStartOffset()`.
- Confirm how `BufferedPlaybackTimeline.OutputLatency` affects start, resume, seek, and sync.
- Decide whether the new empirical value represents command-to-heard delay or only output-buffer
  delay after startup control modeling.
- Write the final equations in `docs/song_sync.md` before changing implementation.
- Add a short comment beside each compensation site naming the clock it compensates.

### Phase 2: Change calibrator semantics

- Replace the `Play() + StartupLatency` reference with the chosen command timestamp/reference.
- Keep input-event timestamp handling and signed median calculation.
- Remove hardware-latency subtraction.
- Display signed values unchanged, including negative values.
- Add enough diagnostic information to debug repeated fixed offsets.

### Phase 3: Change runtime semantics

- Remove `AccountForHardwareLatency` branch from `SongRunner.UpdateCalibration()`.
- Keep `StartupLatency` handling in `BufferedPlaybackTimeline` and backend playback scheduling.
- Verify `SetOutputLatency(AudioCalibration)` receives the new empirical value once.
- Verify start, resume, seek, rewind, practice-speed changes, and output-device changes do not
  double-count startup or empirical output latency.

### Phase 4: Migrate settings

- Add a settings schema/version field to `SettingContainer`.
- Add one-time migration in `SettingsManager.LoadSettings()` after deserialization, before setting
  callbacks.
- Convert legacy calibration using validated migration terms.
- Persist migration marker and converted value through `SaveSettings()`.
- Keep legacy toggle property readable but hidden/obsolete for one compatibility release, or use a
  migration DTO if the property must be removed from the live setting container.
- Remove toggle from `DisplayedSettingsTabs` and localization after compatibility handling exists.
- Add migration logging and one-time recalibration notification.

### Phase 5: Documentation and cleanup

- Update `docs/song_sync.md` to separate startup/control latency from empirical audible latency.
- Update English localization text; remove advice that says to disable hardware compensation when
  calibration is negative.
- Update translated strings through the normal localization workflow; do not hand-edit generated
  translations unless project convention requires it.
- Remove stale comments describing `AudioCalibration` as hardware-adjusted.
- Remove temporary diagnostics and obsolete `PlaybackLatency` compensation branches.

## Validation matrix

### Calibration behavior

- Toggle legacy value true, migrate, compare effective timing before/after.
- Toggle legacy value false, migrate, compare effective timing before/after.
- Existing positive calibration.
- Existing negative calibration.
- Zero calibration.
- Missing/corrupt settings file.
- Missing output device or unavailable latency estimate.
- Repeated calibration runs on same device: stable offset, no cumulative migration.
- Output device/buffer change followed by recalibration.

### Playback behavior

- Song start from pre-roll.
- Resume from pause near song start.
- Resume mid-song.
- Practice restart/seek.
- Rewind and playback restart.
- In-place practice speed change.
- Output-device switch while paused and active.
- Audio/video calibration changes from pause settings.
- Hardware with small, large, and unavailable reported latency.

### Regression assertions

- No code path adds `PlaybackLatency` to `AudioCalibration` after migration.
- No code path subtracts `PlaybackLatency` during calibration.
- `StartupLatency` remains present only where control-clock startup prediction requires it.
- Calibration values are not clamped to nonnegative.
- Settings migration executes once and is idempotent.
- Scores/replays use same effective timing before and after migration within measurement tolerance.

## Acceptance criteria

- Toggle absent from user settings after compatibility migration.
- Existing users do not receive a silent fixed timing shift.
- New calibration result reflects measured audible output path, not hidden hardware subtraction or
  an unverified startup estimate.
- Stable fixed offsets are explainable from logged timing values.
- Normal BASS output start/resume/seek remains synchronized.
- Documentation names each latency term and its owner.
- Automated tests cover migration math, idempotence, signed values, and compensation ownership.

## Open questions before implementation

1. Does BASS `GetPosition()` represent decoded, device-buffered, or heard position on the active
   normal output path?
2. Should empirical calibration include command-to-first-audible-sample startup delay, or should
   startup remain separately modeled and only residual output delay be stored?
3. Can the old startup estimate be reproduced exactly during migration after device settings have
   changed? If not, prefer recalibration over a guessed conversion.
4. Where should the one-time migration notice appear: settings menu, toast, or calibration screen?
5. Should a hidden legacy property remain for one release, or should migration deserialize a raw
   compatibility DTO and then remove the property immediately?
