# Overshell and interstitial join flow

Status: draft design. Based on the mockups, the discussion so far, and a read of the current binding and profile code. Nothing here is implemented yet.

## Scope

This covers the difficulty select screen that appears after a song is picked (the interstitial, `DifficultySelectMenu`). It describes how players join, choose an instrument and difficulty, and mark ready before the song starts.

The title screen is not covered yet. The overshell (a persistent frame above the screen stack) is shown on this screen. It shows only the header; the band is shown as player columns.

## Screen layout

- Header: back arrow, "Quickplay" eyebrow, "Select difficulty" title, and the song name and source on the right.
- Body: one column per joined player, plus a Join slot at the end while there is room.
- Footer: a status line ("2 of 3 ready"). There is no Start button; the song starts by itself once everyone is ready.
- Columns share the body width evenly. Columns keep shrinking as players join, up to the 32 player cap below.

### The Join slot

- Exactly one Join slot is on the screen at a time, always after the last player column.
- It is created when a column is removed and it is consumed when a column is added. It never appears twice, and removing two players does not produce two slots.
- It is also the one place a join can be in progress: while a player is being asked for a profile, the slot holds the prompt ("Press a button on your instrument", then the profile picker) instead of adding a second element.
- It disappears while the band is full, and while a column is mid-edit (instrument picker open), so two joins cannot overlap.

### Player cap

- Hard cap: 32 players. This matches the 32 camera slots in gameplay rendering (`HighwayCameraRendering.MAX_MATRICES`). Each player uses one camera, and the vocal track uses one too.
- At 32, the Join slot is replaced with "Full (32 of 32)" and no new player can join.

### Column sizing

- Column width is the body width divided by the column count, capped at 180px.
- Under 110px, difficulty names switch to short labels (BEG, EZ, MED, HRD, EXP, EXP+).
- Under 90px, the player name is hidden.

## What the code does today

- A profile has one game mode (`YargProfile.GameMode`). The game mode sets which instruments the profile can play and which controller family it expects. The profile center is the only screen that changes it after the profile loads (`ProfileCenterPane.cs:496`).
- The instrument choice on the difficulty screen only changes `CurrentInstrument` inside the profile's game mode. It never changes `GameMode` (`DifficultySelectMenu.cs:1108`).
- Bindings are stored per profile, per game mode, per controller family (`PlayerDeviceInfo._preferredBindsByContext`). Lookups use the profile's current game mode.
- A controller belongs to a profile through the profile's saved device list. A press on an unclaimed controller auto-connects its most recently used matching profile (`PlayerContainer.TryConnectProfile`, called from `InputManager.cs:274`). Keyboards, mice, and pens are ignored for this. In practice nothing appears to happen on the earlier screens: a device that belongs to a profile is claimed at launch or from the profiles screen, so by the time the menus are up there is usually nothing left for a press to connect. The press path is only really visible here, which is why this screen is where joining belongs.
- Microphones are assigned to profiles the same way. A mic has no buttons, so a mic cannot start a join.
- One controller or microphone can belong to only one active player.
- Active players persist across menus. `PlayerContainer.Players` is global, and it only shrinks when a player is disposed (disconnect, or a profile swap). The difficulty screen already lists everyone who joined earlier as a column, before any of this is implemented.

## Joining

Anyone who joined earlier, in this session or restored at launch by `AutoConnectProfiles`, is already a column when the screen opens. The steps below only cover adding a player who is not in the band yet. A press from a controller whose saved profile is already in the band is a menu action for that player's column and never opens a picker.

This screen is the only place a press joins. Earlier screens keep their current behaviour, where a press either does nothing or is already handled by auto-connect and the profiles screen.

Joining requires a device that can press a button. Microphones are audio devices (`MicId`, `MicrophoneWatcher`), never input devices, so they produce no button presses and cannot start a join. `InputManager` also ignores keyboards, mice and pens for joins (`InputManager.cs:251`).

A player who cannot press is still supported, as a managed column. A singing profile with a saved microphone joins from the profiles screen without any device prompt, or is restored at launch by `AutoConnectProfiles`, and appears as a column with no controller of its own. The mouse then operates that column (see Per-column menus and the mouse). The singer needs a microphone, not a controller.

Vocal instruments still require a microphone on the column. The picker greys a vocal instrument out with "(needs a microphone)" when the player has none, and Ready is the backstop.

1. A player presses a button on their controller. The Join slot shows "Press a button on your instrument" if nothing else is open.
2. The press identifies the controller. Its family decides which game modes it can use (see Bindings below).
3. Profile picker: lists profile names only. Profiles already in use are greyed out with "(in use)". Cancel returns to the Join slot. If the controller is already saved to a profile that is not in the band, that profile is preselected (see Resolved).
4. Choosing a profile adds the player's column at the position of the Join slot, on the profile's preferred instrument. The Join slot reappears for the next player. No instrument picker runs at join time. The instrument, and the song mode it selects, are changed on the column (see Changing instrument and Game mode is two things).

The join-time instrument picker is dropped. A new column opens on the profile's saved instrument. Back changes it before Ready, so the join-time picker only added a forced confirm. A join is profile then column, with the same default and change path as everyone else.

### The profiles screen is the host path

The profiles screen's Connect button is not a duplicate of the join slot. It is the only path for a profile that has no controller to press:

- Bots have no devices and are added there (`ProfilesMenu.AddBotProfile`).
- Keyboards, mice, pens and microphones cannot start a join.
- A mic-only singer needs it mid-session, because the join slot can never produce them.

Keep both, and give them one rule. Both produce a column, and the column's instrument is chosen per song, seeded from the profile's preferred instrument. Connect must not imply a saved game mode; it only puts the profile in the band. A profile-screen join skips the picker, so its column starts on the preferred instrument and can be changed with the column's back action.
Connect's device prompt lists controllers only, and in practice a singing profile never sees it. The prompt is skipped when the profile already has a device (`ProfileView.cs:494` checks `DeviceInfo.HasNoDevices`, which counts a microphone). A vocal profile with its microphone saved therefore joins with no prompt and no controller, which is exactly the mic-only path this design depends on. Microphones are assigned in the profile center's microphone section (`ProfileCenterPane.cs:336`), where they have always lived.

### Per-column menus and the mouse

Each column is its own menu: the instrument picker replaces the difficulty rows in place, and the column owns its difficulty, modifiers and Ready.

- Directional devices (controllers, keyboard) drive only their own player's column.
- The mouse is positional, so it operates any column regardless of who owns it. That is what makes a managed column possible: a mic-only singer's column is set up and readied with the mouse.
- This requires the column controls to be click targets. The current screen is direction-driven, with rows focused and confirmed, so pointer support on the columns is real UI work rather than a freebie.

### Menu controls

The blocker is that menus have exactly one cursor today, not one per player.

- `Navigator` is a singleton holding a single selected navigatable. `DifficultySelectMenu` does not give each player their own menu; it rebuilds the one menu for whoever `_playerIndex` points at, and back and confirm step that index (`ChangePlayer`). Everyone shares the cursor, so two players pressing directions at once fight over the same selection.
- The plumbing is half there: `NavigationContext` already carries the `YargPlayer` that pressed the button (`Navigator.cs:26`), and screens already read it for per-player behaviour (score screen scrolling, `SettingsMenu` held checks). Routing a context to that player's column is a matter of keeping N selections instead of one.
- Joining is not affected. The joining press is consumed before the menu layer (`InputManager.cs:274` sets `handled`), and an unregistered device has no menu bindings to fire anyway.
- Menu bindings are per family, from the same binding store as everything else. A family with no menu binding set drives its column with the mouse instead, which is the same answer as a player with no controller at all. MIDI drumkits were the one family with no default menu set found, and relying on the mouse there is acceptable.

None of this is a small change. It is the largest single piece of work in this design, and it is a prerequisite for per-column menus rather than a follow-up.

### Before this screen

A player who has not joined is not navigating anything, so the menus before this one are driven by three things:

- **Connected players.** Their bindings are the normal path, and this is why the band has to exist at all: menus are menus for the people in them.
- **The keyboard fallback.** Arrow keys and enter work with no profile connected. `DefaultKeyboardMenuBindings` fires the menu action with a null player when no keyboard profile is connected (`DefaultKeyboardMenuBindings.cs:47`, `InputManager.cs:149`).
- **The mouse**, which is positional and needs no player.

So the first person to reach this screen is either already in the band, or using the keyboard or mouse. A controller that belongs to nobody does nothing on the earlier screens, which matches the behaviour observed in the menus.

The consequence is worth stating: a machine with only a gamepad attached and no connected profile has no menu input at all, because an unregistered device produces no menu actions. Something has to break that loop before the first join can happen, and today it is `AutoConnectProfiles` at launch (`LoadingScreen.cs:76`). The new join flow does not change that, and it should not be expected to solve it.

### Instrument list source

Today the list comes from the profile's game mode, filtered to instruments that every song in the show has (`DifficultySelectMenu.cs:1108`). The new flow lists instruments across all modes, filtered the same way. The mockup's fixed list of four is only a placeholder.

Each instrument has one native game mode (`ToNativeGameMode`, `InstrumentEnums.cs:195`). For example, keys map to five-fret guitar, pro drums map to four-lane drums, and harmony maps to vocals.

Some instruments can also be played in another mode. Six-fret mode plays five-fret instruments, and elite drums mode plays four-lane and pro drums (`PossibleInstrumentsForSong`, `InstrumentEnums.cs:307`). So the mode for a song is chosen like this:
- Candidates are the native mode plus any mode that lists the instrument in `PossibleInstrumentsForSong`.
- Use the native mode if this controller has a binding for it.
- Otherwise use the first candidate that has a binding for this controller family.
- The picker enables an instrument only when at least one candidate has a binding.

## Bindings and the controller family

- A binding is checked per controller family and per game mode. It is not checked per instrument.
- Guitar, bass, rhythm, co-op guitar, and keys all use the five-fret guitar game mode. They are allowed or greyed out together.
- Defaults: a native five-fret guitar has binding sets only for the five-fret guitar mode (and the menu). A generic gamepad has defaults for every game mode, including drums and vocals.
- Any controller can have a binding for any game mode. The defaults are only a starting point. Some bindings are discouraged, but they still count.
- The check for "(no binding)" is: a binding set exists for this controller and the instrument's game mode, either a saved preference or a default.
- The greyed-out label should point to the profile's binding setup, for example "(no binding, set one up)".

### Game mode is two things

The profile's saved game mode should be treated as an input context, not as a statement about what the profile can play. Splitting it in two keeps the existing binding store and removes the "locked in" reading:

- **Input context (saved).** Chooses which binding set the profile center shows and edits, and which instrument is preselected for a song. It is a filter over the per-(mode, family) binding store. Gamepads already have defaults for every mode. It follows the mode of the profile's last deliberate instrument pick, so it always matches the preferred instrument.
- **Song mode (per player, per song).** What the player is actually playing. The instrument picker selects it together with the instrument. It drives track prefab selection (`GameManager.Loading.cs:490`), the note engines (`TrackPlayer.cs:529`, `DrumsPlayer.cs:225`), device validation (`YargPlayer.IsMissingMicrophone`, `IsMissingController`), modifiers (`YargProfile.ApplyModifiers`), scoring, and replays.

Do not re-derive the song mode from the instrument at read time. `ToNativeGameMode` collapses some pairs: pro drums played on an elite drums profile is `(EliteDrums, ProDrums)`, which a derivation would report as `(FourLaneDrums, ProDrums)`. Store the chosen mode next to the instrument.

The cost is profile settings, which are mode-shaped today. `PossibleProfileSettings` (`GameModeExtensions.cs:37`) lists lefty flip, highway orderings, cymbal models, drum star power activation, open-lane display, range markers and microphones per mode. Of these, only lefty flip has to change. See below.

### Lefty flip

Lefty flip is not a `Modifier`. It is a profile setting shown in the difficulty screen's Accessibility submenu (`DifficultySelectMenu.cs:766`), next to the modifier toggles, and also edited in the profile center and in the drums highway configuration menu.

- It persists. Setting it per song would mean the player sets it again for every song.
- It is stored per profile, per controller family. One profile can now play a guitar in one song and drums in the next, and a lefty guitarist who drums right-handed needs opposite values. A single profile-wide value cannot hold both, and a per-song value would not survive the song.
- Keying it by `(mode, family)` costs nothing if it sits next to the binding store, and it covers a gamepad played across modes. Do not build UI for that case.
- Not stored on the device. A friend who borrows the controller joins under their own profile, because a profile is a person.
- The difficulty screen row needs no change: the column's controller is already known, so the row edits that bucket without asking.
- The profile center toggle moves into the controller entries, which already host the per-controller binding editors. Migration copies the current bool into every family, so no existing setup changes on load.
- The other instrument-shaped settings do not need this. Star power activation is drum-only, cymbal models are five-lane-only, and open-lane display and range markers are both five-lane, so one profile-wide value is acceptable for them.

## Cases

### Case 1: guitar controller wants to play drums with no binding

- By default, the drums option is greyed out with "(no binding)" for a native guitar. The player can add a drums binding, and then drums are enabled.
- A gamepad-type guitar can choose drums by default, because a default drums binding exists for gamepads.
- Choosing drums picks the drums game mode for this song. Bindings are looked up per game mode, so the controller needs a drums binding. The picker only enables drums when that binding exists.
- MVP: yes, drums can be picked on a guitar profile, but only when the controller has a drums binding. The player does not see a game mode at any point.
- The profile keeps its bindings for each game mode. Picking a part decides the mode for that song, and the deliberate pick also becomes the saved mode (the input context).

### Case 2: one person wants to sing and play guitar at the same time

- Today one profile is one game mode, so it is one instrument. A second player for the same profile is refused.
- Vocal profiles need a mic. Other profiles need a controller. The game checks this per mode (`YargPlayer.IsMissingMicrophone`, `IsMissingController`).
- Each player gets exactly one track in gameplay. `GameManager.CreatePlayers` builds either a highway track or a vocal track per player, never both (`GameManager.Loading.cs`). So one slot cannot sing and play guitar with the current engine.
- Two slots already work. Two profiles can play one song, and each gets its own track.
- Goal: a profile is a person. One person should have one column and one score record per song.
- MVP (Option A): two profiles. The person joins once from the guitar, then joins again from a second device that can press a button and has a vocal binding (a gamepad). The mic is assigned to the vocal profile. A mic alone cannot start the join. Known limitation: the screen shows two columns and the scores are kept per profile.
- Future feature (Option B): one player with vocals and guitar in one song, as one profile with two tracks. This touches gameplay setup, scoring, and bindings. Moving existing scores from the vocal profile to the merged profile needs a migration plan. The size is my estimate; I have not scoped it.

### Case 3: how this works with bindings and connected profiles

- Auto-connect versus the picker: resolved (see Resolved). A press on a controller saved to a profile that is not in the band preselects that profile in the profile picker. A profile already in the band gets a menu action, not a join.
- Unsaved controller: no profile matches, so today a press does nothing. The new flow needs a path. The player picks a profile, and the controller is then saved to that profile.
- Profile in use: greyed out, the same as the profile list.
- Profile with several controllers: only the pressed controller joins. Other saved controllers stay as they are.
- Profile with a saved binding for a mode: that binding is used. Otherwise the family default is used.

## Player column

- Header: player name, highlighted blue once the player is ready.
- Instrument label under the header. While the player is not ready, pressing back on their instrument opens the instrument picker (see below).
- Difficulty rows, six levels (Beginner to Expert +). The selected row is highlighted.
- Ready button at the bottom. Its label switches to "Unready" once pressed.
- The column can be disconnected at any time, before or after Ready. Removing it returns the single Join slot and lowers the ready count.

### Instrument and difficulty on entry

- Every column opens prefilled with the player's last instrument and difficulty, shown on the column as in the mockup. No picker opens on entry.
- Instrument: the profile's saved instrument (`PreferredInstrument`, the last deliberate pick), then `CurrentInstrument`, then the first instrument the song allows. This is the order the screen already resolves in (`DifficultySelectMenu.cs:1129`), so nothing new is invented; the difference is that the result is now shown on the column instead of being hidden.
- Difficulty: the profile's last-used difficulty for that instrument, with the existing `DifficultyFallback` behaviour when the song lacks it.
- The column is usable immediately. There is no confirm step, and pressing Ready is not the same as accepting the instrument.
- If the saved instrument is in the song but this controller has no binding for it, the column opens with its instrument picker showing rather than silently switching the player to something else.
- If the saved instrument is not in the song at all, the column opens in the instrument picker too. See below.

### When the instrument is not in the song

The player is asked again, at the top of the screen, before any difficulty is touched.

- The column shows its instrument picker in place of the difficulty rows, with the saved instrument listed as "(not in this song)" and disabled.
- The picker also offers Sit Out, so the player can decline rather than substitute an instrument they do not want.
- Picking another instrument returns the column to its difficulty rows with the new instrument and the usual difficulty fallback.
- Sitting out marks the column "Sitting Out": no difficulty rows, no instrument controls, and it counts as decided rather than blocking Start. This is the one exemption from re-readying every song.
- Back on a sitting-out column reopens the picker, so sitting out is reversible until the song starts.
- The existing guard stays: the song cannot start with every player sitting out (`DifficultySelectMenu.cs:1058`).
- Only reached when the song changes. A setlist filter (`DifficultySelectMenu.cs:1113`) keeps every instrument in the show playable for the whole run, so this never interrupts mid-show.

### Changing instrument

- Allowed at any time before the player is ready. After that the column is locked.
- Pressing back on their instrument opens the instrument picker in their own column, in place of the difficulty rows. The header shows "Choose instrument".
- Choosing an instrument returns the column to its difficulty rows, keeping the previous difficulty selection.
- Cancel returns to the difficulty rows without changing the instrument.
- The picker follows the same binding rule as joining: unbound instruments are greyed out.

### Ready

- Pressing Ready locks the column: difficulty and instrument can no longer change.
- Pressing Unready unlocks it.
- Ready does not carry over between songs. Every column starts unready on every visit to this screen, so the band re-confirms after any instrument or difficulty change.
- A column whose player has no pressable device is readied with the mouse. There is no auto-ready and no exception to the re-ready rule.
- The footer status line updates to show how many players are ready.

## Starting the song

- There is no Start button. The song starts by itself as soon as every player is ready, or sitting out.
- Ready and Unready are the only controls that start or hold the song. Any player who is not ready keeps it from starting.
- A player who disconnects counts as gone, so if everyone who remains is ready, the song starts.
- The existing guard stays: the song cannot start with every player sitting out (`DifficultySelectMenu.cs:1058`).

## Resolved

- **Initial instrument and difficulty:** A column opens prefilled with the profile's last instrument and last difficulty, and is changed with back until Ready. If that instrument is not in the song, the column opens in the instrument picker. No confirm step.
- **Ready on entry:** Everyone presses Ready for every song. A sitting-out column is the one exemption, because it has nothing to confirm.
- **Default difficulty:** Last-used per profile, not a band average. An average would move a player's level without them touching anything.
- **Disconnect:** Allowed at any time, before or after Ready. The single Join slot comes back. A disconnect counts as gone, so if everyone left is ready, the song starts.
- **Instrument missing from the new song:** The player is asked again rather than swapped or sat out behind their back (see When the instrument is not in the song).
- **Profiles screen:** Connect and Disconnect stay. The device prompt lists controllers only; a saved microphone already counts as a device, so a singing profile never sees the prompt.
- **Game mode split:** The saved mode is the input context and follows the last deliberate instrument pick; the played mode is stored per player, per song (see Game mode is two things and Lefty flip).
- **What a press does:** A press from a controller whose profile is already in the band is a menu action for that player's column, and never opens a picker. A press from a saved controller whose profile is not in the band preselects that profile in the profile picker; global auto-connect does not run on this screen. A join is profile then column, with no instrument picker. The column opens on the profile's last pick and last difficulty, so later songs need no reselection, unless the instrument is not in the song. Ready is still needed every song.
- **Overshell content:** The overshell shows only the header. The band is shown as player columns, so the overshell adds nothing beyond the header.
- **Start:** No Start button. The song starts by itself once every player is ready or sitting out. A disconnect that leaves only ready players also starts it.
- **Saved game mode field:** Kept, and it follows the profile's last deliberate instrument pick, so it always matches the preferred instrument. A change the game makes on its own (for example, when the preferred instrument is not in a song) does not change it. This keeps the preferred instrument valid on load, with no change to the load check. There is no profile version bump and no replay format change, because replays already carry `CurrentInstrument`. The played mode is stored per player, per song (see Game mode is two things).

## Open questions

None.

## Deferred

- **Late joins:** Joining a song that is already playing. Wanted, not MVP, so nothing on this screen blocks on it. When it is built it needs to decide whether the new player starts from the beginning of the chart or from the current position, how a partial score is recorded, and whether their column appears in the pause menu. Until then the rule stays conservative: joining is a menu-screen activity, and auto-connect must not add players during gameplay.
