# Freestyle Solos

This document is a high-level guide to the freestyle solos feature: what each class is
responsible for and how the pieces fit together. It is not a line-by-line description of the
algorithms.

Freestyle solos change a solo phrase from "play these notes or fail" into a short improvisation.
While the phrase runs, the game plays the charted solo for the player, the recorded guitar or bass
stem is muted, and the player's own frets produce a synthesized lead guitar in a tone matched to the
recording. The charted solo still scores normally; the player's extra playing is expression, not
extra score.

---

## 1. The four jobs

The feature is easiest to understand as four jobs, each owned by a different layer.

| Job | What it means in play | Main owner |
| :--- | :--- | :--- |
| **Recognise the solo** | Decide that the next note is inside a solo phrase, start and stop freestyle mode, and mark the solo's notes as engine-played. | [`GuitarEngine`](../YARG.Core/YARG.Core/Engine/Guitar/GuitarEngine.cs) and [`YargFiveFretGuitarEngine`](../YARG.Core/YARG.Core/Engine/Guitar/Engines/YargFiveFretGuitarEngine.cs) |
| **Let the player shred** | Turn strums, taps and held frets into sounding notes, with its own HOPO and tap rules, while owning the stem mute. | [`FreestyleSolo`](../Assets/Script/Gameplay/Player/Freestyle/FreestyleSolo.cs) |
| **Replace the recorded part** | Drop the recorded stem and play a synthesized guitar that follows the player's frets. | [`BassStemReplay`](../Assets/Script/Audio/Bass/BassStemReplay.cs) and [`BassStemSoundboard`](../Assets/Script/Audio/Bass/BassStemSoundboard.cs) |
| **Prepare the material** | Before the song starts, cut the chart's solo notes out of the stem, measure them, and build the synth phrase each solo will play from. | [`BassSoloPhraseLibrary`](../Assets/Script/Audio/Bass/Stem/BassSoloPhraseLibrary.cs) and [`BassSoloPreparation`](../Assets/Script/Audio/Bass/Stem/BassSoloPreparation.cs) |

The gameplay layer never touches audio buffers, and the audio layer never reads the chart
directly; gameplay hands it a list of `GuitarNote`s. The boundary between them is that note
list in one direction and a selected `BassSoloPhrase` in the other.

---

## 2. Class map

### Gameplay and engine

| Class | Responsibility in plain English |
| :--- | :--- |
| [`GuitarEngine`](../YARG.Core/YARG.Core/Engine/Guitar/GuitarEngine.cs) | Owns the freestyle phrase itself. `IsFreestyleActive` says a solo note is being played; `FreestyleSoloTime` gives the phrase's start time so the player and the audio can agree on when the solo begins. Also marks solo notes as `IsAutoPlayed`, which is what hides them from normal hit logic and from the highway. |
| [`YargFiveFretGuitarEngine`](../YARG.Core/YARG.Core/Engine/Guitar/Engines/YargFiveFretGuitarEngine.cs) | The five-fret implementation of that idea. It drives the internal bot hands to the chart's solo notes so they hit automatically, accepts fret presses so the player's hands are tracked, and deliberately swallows strums so improvising cannot overstrum. |
| [`GuitarEngineParameters`](../YARG.Core/YARG.Core/Engine/Guitar/GuitarEngineParameters.cs) | Carries the `FreestyleSolos` flag. It is serialized into replays, so a replay reproduces whether its solos were freestyle. |
| [`FiveFretGuitarPlayer`](../Assets/Script/Gameplay/Player/FiveFretGuitarPlayer.cs) | Builds the `FreestyleSolo` when the soundboard exists, feeds it queued strums/taps/releases, calls its per-frame `Update`, mutes the stem through `SetStemMuteState`, and lights lanes and plays hit animations for the player's notes. |
| [`SixFretGuitarPlayer`](../Assets/Script/Gameplay/Player/SixFretGuitarPlayer.cs) | Reuses the same engine and audio path but suppresses freestyle lane visuals (`ShowsFreestyleLanes` is false) and maps freestyle hits onto the six-fret pad layout. |
| [`TrackPlayer`](../Assets/Script/Gameplay/Player/TrackPlayer.cs) | Uses `IsEnginePlayed` to hide notes and lanes the engine is playing, so a freestyle solo does not show two copies of the same notes. |

### Session audio

| Class | Responsibility in plain English |
| :--- | :--- |
| [`BassStemReplay`](../Assets/Script/Audio/Bass/BassStemReplay.cs) | The session-level owner of freestyle audio for one stem. Creates the soundboards, decodes the copied stem once, runs the pre-render task, pumps voices every frame, resets them on seek, and disposes them with the song. It also decides whether a stem is freestyle at all. |
| [`BassStemSoundboard`](../Assets/Script/Audio/Bass/BassStemSoundboard.cs) | One player's instrument rig. Owns the phrase library, twelve `BassStemVoice`s, the synth mixer bus, amp and reverb. Receives `PlayStrum`, `PlayNote`, `PlayLegato`, held-fret and whammy updates, and mixes the result into the song. |
| [`BassStemVoice`](../Assets/Script/Audio/Bass/BassStemVoice.cs) | One push stream playing one clip. Handles the clip's loop, playback rate, whammy pitch, release samples, volume envelope, decay, pick noise, vibrato and small human pitch variations. |
| [`BassStemChannel`](../Assets/Script/Audio/Bass/BassStemChannel.cs) | The normal song stem channel. It creates a `BassStemReplay` only when `BassStemReplay.IsFreestyleStem` says this stem needs one. |
| [`BassStemPipeline`](../Assets/Script/Audio/Bass/BassStemPipeline.cs) / [`BassSong`](../Assets/Script/Audio/Bass/BassSong.cs) | Normal stem plumbing, with one freestyle addition: when a freestyle stem is added, `BassSong` copies the whole stem stream so the pre-render can decode it separately without disturbing playback. |

### Solo preparation

| Class | Responsibility in plain English |
| :--- | :--- |
| [`BassSoloPhraseLibrary`](../Assets/Script/Audio/Bass/Stem/BassSoloPhraseLibrary.cs) | The thread-safe queue between gameplay and pre-rendering. Gameplay requests the chart's solo notes; the pre-render task prepares them; playback selects the published phrase that covers the current solo time. |
| [`BassSoloPreparation`](../Assets/Script/Audio/Bass/Stem/BassSoloPreparation.cs) | Does the musical analysis for one solo: measures each note's pitch, level and decay, matches the recorded tone and space, chooses the synth note ladder and the anchor notes the fret box follows, and returns a finished `BassSoloPhrase`. |
| [`BassSoloPhrase`](../Assets/Script/Audio/Bass/Stem/BassSoloPhrase.cs) | The finished solo in a playable form: note ladder, amp tone, gain, and the logic that picks which clip sounds for a given fret, including chords and whammy bend targets. |
| [`BassSoloScale`](../Assets/Script/Audio/Bass/Stem/BassSoloScale.cs) | Works out the solo's key. It picks the best minor or major pentatonic from the measured notes and adds up to two frequent outside notes as "colour". |
| [`BassSoloFretBox`](../Assets/Script/Audio/Bass/Stem/BassSoloFretBox.cs) | Maps the five frets onto five nearby rungs of the solo's scale. It keeps a fret sounding the same note within a phrase, keeps adjacent frets distinct, shifts the box as the player moves up or down, and tries to align the root with the backing chord. |
| [`BassSoloPitch`](../Assets/Script/Audio/Bass/Stem/BassSoloPitch.cs) | The YIN pitch detector used to measure note pitches and bass roots. |
| [`BassSynthGuitar`](../Assets/Script/Audio/Bass/Stem/BassSynthGuitar.cs) | Loads the sampled guitar library, builds a full chromatic note ladder from it, and produces the dry DI signal used when matching the recorded tone. Shared and cached per sample format. |
| [`BassStemClip`](../Assets/Script/Audio/Bass/Stem/BassStemClip.cs) | One decoded slice of audio plus the analysis that makes it playable: onset position, clean end, attack end, decay rate, loop points, alternate takes, velocity layers and release samples. |
| [`BassChordTrack`](../Assets/Script/Audio/Bass/Stem/BassChordTrack.cs) | Follows the backing bass during the pre-render and answers "what chord root is under this moment?" so the fret box can stay in harmony. |
| [`BassLeadAmpDsp`](../Assets/Script/Audio/Bass/Effects/BassLeadAmpDsp.cs) / [`BassLeadAmpTone`](../Assets/Script/Audio/Bass/Effects/BassLeadAmpDsp.cs) | Native amp/reverb DSP. `Match` fits the amp and cabinet to the recorded notes; `MatchSpace` fits echo and reverb to the solo's room. |
| [`BassCabinetSet`](../Assets/Script/Audio/Bass/Stem/BassCabinetSet.cs) | Loads the cabinet impulse responses the tone match chooses between. |
| [`BassStemFormat`](../Assets/Script/Audio/Bass/Stem/BassStemFormat.cs) | Small value type holding the shared sample rate and channel count, plus frame/second conversions. |
| [`BassSoloExtensions`](../Assets/Script/Audio/Bass/Stem/BassSoloExtensions.cs) | Chart helpers: find solo spans, get a note's clip duration, detect a sustain, and take a sorted median. |

---

## 3. The flow in play

### Before the song starts

1. `FiveFretGuitarPlayer` sees `EngineParams.FreestyleSolos` and asks
   `GameManager.CreateStemSoundboard` for a soundboard.
2. `GameManager` reaches the stem's `BassStemChannel.Replay`. If the toggle is off, or the stem
   is not guitar, bass or rhythm, this is null and freestyle is simply unavailable.
3. The player creates `FreestyleSolo` and passes it the chart's expert notes. That call becomes
   `BassSoloPhraseLibrary.RequestPhrases`, which splits the notes into one render job per solo
   span.
4. When the song channels are added, `BassSong` copies the stem stream. After loading,
   `GameManager.StartStemPrerenders` calls `BassStemReplay.StartPrerender` on every freestyle
   channel.
5. `BassStemReplay` collects frame windows from all chord tracks and soundboards, decodes the
   copied stream once, analyzes the bass roots, and asks each soundboard to publish its phrases.
   This is a one-shot pass; the copied bytes are released afterwards.

### During a solo

1. `GuitarEngine` notices that the next note, or an active sustain, belongs to a solo. It records
   `FreestyleSoloTime` as the phrase's first note time and tells the engine subclass.
2. `YargFiveFretGuitarEngine` starts moving the bot hands to the chart's notes. The notes auto-hit
   and are marked engine-played, so normal hit logic and note visuals skip them.
3. `FreestyleSolo.Update` runs every frame. It asks the engine for the solo time, applies a
   0.1-second lead so the stem is muted just before the phrase, and selects the matching
   `BassSoloPhrase` through the soundboard.
4. If a phrase is available, the player mutes the recorded stem through `SetStemMuteState` and
   the soundboard starts sounding the player's input. If the phrase is not ready, the stem stays
   audible and the feature degrades to the normal solo rather than going silent.
5. Input arrives through `QueueStrum`, `QueueTap` and `ReleaseFrets`. In `Update`,
   `FreestyleSolo` decides whether the input is a strum, a hammer-on or a legato note, using a
   short HOPO leniency window, a 0.25-second tap window and a re-pick guard. It calls
   `PlayStrum`, `PlayNote` or `PlayLegato`, then tells the player to animate the hit.
6. `BassStemSoundboard` picks a free voice and starts a `BassStemClip`. Sustains loop while the
   fret is held; whammy bends notes toward the nearest scale tone; releases play a release sample.
7. `GameManager` calls `PumpStemSoundboards` every frame, which keeps each voice's 250 ms buffer
   fed. This is separate from the song's own read-ahead buffer.

### Leaving the solo

`GuitarEngine` clears `FreestyleSoloTime` when no solo note remains. `FreestyleSolo.Update`
then stops all voices, restores the stem if it muted it, and zeroes the fret and whammy state. The
engine hands control back to the player's held frets.

### Seeking, pausing and lifetime

`BassStemReplay.ResetAfterSeek` resets every voice, so a seek cannot leave a stuck note.
`FreestyleSolo.Reset` does the same for input and mute state around practice resets, drop-outs and
difficulty changes. `BassStemReplay.Dispose` cancels the pre-render and tears down every
soundboard with the song.

---

## 4. How a note becomes sound

This is the part worth knowing when reading the audio classes.

- `BassSoloPhraseLibrary` turns each chart solo into a `RenderJob`: the notes, a time span, and
  the raw sample windows needed from the stem.
- `BassStemReplay` decodes those windows into `BassStemClip`s.
- `BassSoloPreparation` measures the clips, matches the recorded amp tone and room, and asks
  `BassSynthGuitar` for a chromatic ladder of synthesized notes.
- `BassSoloScale` chooses the key from the measured notes. `BassSoloFretBox` maps the five
  frets onto that key.
- At play time, `BassSoloPhrase` picks a clip for the fret the player sounded. It prefers the
  exact fret, falls back to a lower fret in the same chord, then to any available note. Open notes
  pick a random available fret.
- `BassStemSoundboard` turns that clip into one or more voices (a chord can sound root, fifth and
  octave), applies the matched tone and gain, and mixes it into the song.
- `BassStemVoice` loops sustains, bends pitch, applies vibrato and humanization, and fades voices
  in and out so repeated notes do not click.

---

## 5. What decides whether it runs

Three separate switches must agree:

1. `SettingsManager.Settings.FreestyleSolos` must be on for a normal session.
2. For a replay, `GlobalVariables.State.ReplayFreestyleSolos` is set from the recorded frame's
   `GuitarEngineParameters.FreestyleSolos`, and that decides instead of the live setting.
3. The stem must be one of `AudioHelpers.PitchBendAllowedStems`: guitar, bass or rhythm. Drums,
   vocals, keys and the backing track do not get freestyle audio.

If any of these fail, `BassStemReplay.IsFreestyleStem` returns false, no replay/soundboard is
created, and `FiveFretGuitarPlayer` logs that stem replay is unavailable.

---

## 6. Boundaries and known limits

- **Freestyle is audio plus autoplay, not a scoring mode.** Solo notes are hit by the engine and
  score normally. Player input during the solo affects sound and visuals, not judgment.
- **Five-fret lanes are the main presentation.** Six-fret uses the same engine and audio path, but
  does not light freestyle lanes and maps hits onto its pads instead.
- **The pre-render is one-shot.** It runs once from the copied stem bytes. There is no second pass
  if the chart changes later.
- **Changing difficulty does not rebuild the phrase library.** `FiveFretGuitarPlayer.ResetDifficulty`
  replaces the engine chart and resets `FreestyleSolo`, but the phrases were requested from the
  chart that was loaded when the player was initialised. A difficulty change can therefore leave
  the audio looking for solos at the old chart's times.
- **The synth needs its samples.** `BassSynthGuitar` loads the `guitarSamples` folder and
  `BassCabinetSet` loads `guitarCabinet`. If the synth cannot load, preparation publishes no
  phrases and the feature quietly falls back to the recorded stem.
- **One backing chord source.** When the player is not on bass, the soundboard follows the bass
  stem's `BassChordTrack`. On bass itself there is no separate backing chord track, so the fret
  box has no chord root to align to.
