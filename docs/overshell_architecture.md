# Overshell & Band Select Architecture

The overshell feature introduces a persistent, per-player card strip (`BandSelectMenu` + `BandColumn`) that lets players join, configure profiles/settings, and ready up across menus or on the difficulty select screen.

```mermaid
flowchart TD
    Settings["SettingsManager.Settings.ShowOvershell (OvershellMode)"] -->|OnChange| MenuMgr["MenuManager"]
    Settings -->|OnChange| MusicLib["MusicLibraryMenu (Sidebar layout)"]
    SettingsMenu["SettingsMenu.OpenStateChanged"] -->|Visibility| MenuMgr
    FiltersMenu["FiltersMenu.OpenStateChanged"] -->|Visibility| MenuMgr
    MenuObj["MenuObject.SupportsOvershell"] -->|OnEnable| MenuMgr

    MenuMgr -->|"Instantiates Overshell.prefab & reparents"| Overshell["BandSelectMenu (Overshell mode)"]
    DiffSel["DifficultySelectMenu"] -->|"ShowOvershell != Off"| Band["BandSelectMenu (Band mode)"]

    InputMgr["InputManager.UnclaimedDevicePressed"] -->|"Button press on unclaimed device"| BandJoin["BandSelectMenu.Join(device)"]
    Nav["Navigator.InputInterceptor"] -->|"Routes active column actions"| Band

    Band -->|"Who is joined, seats, ready"| Session["BandSessionState"]
    Band -->|"One per card"| Card["BandCardController"]
    BandJoin --> Card
    Card -->|"BandCardContent (list of BandMenuItem)"| Col["BandColumn (view)"]
    Card -->|"Cross-player rules"| Session

    Card -->|"Selection / tier / modifier / blocker rules"| Rules["PlayerSelectionRules"]
    Card -->|"Engine / bounds / bot setting rules"| Catalog["ProfileSettingsCatalog"]
    Col -->|"Header icon & fit"| Header["PlayerCardHeader"]
    TrackHUD["TrackPlayerMenu (Gameplay HUD)"] -->|"Reuses"| Header
    Band -->|"All non-sitting-out columns decided"| Gameplay["DifficultySelectMenu.LoadGameplay()"]
```

---

## 1. Overshell Modes & Hosting

The feature is gated by `OvershellMode` in [SettingsManager.Settings.cs](file:///home/phil/Development/YARG/Assets/Script/Settings/SettingsManager.Settings.cs#L49-L54) and exposed via `MenuManager.UseOvershell` and `MenuManager.NeedsPlayers` ([MenuManager.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/MenuManager.cs)):

- `Off` (default): Classic [DifficultySelectMenu.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/DifficultySelect/DifficultySelectMenu.cs) runs; `MenuManager.NeedsPlayers` shows the no-player warning in `MusicLibraryMenu` when `PlayerContainer.Players.Count <= 0`.
- `DifficultySelect`: [DifficultySelectMenu.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/DifficultySelect/DifficultySelectMenu.cs#L136-L147) hides the classic scroll list and opens its embedded `BandSelectMenu` (`_band`) in band mode. Earlier menus do not show the overshell, and `MenuManager.NeedsPlayers` stays `false` so players can enter a song and join on the band screen.
- `Everywhere`: `MenuManager.UseOvershell` is `true`, opening the overshell across supported menus in addition to band select.

### Single Overlay Hosting (`MenuManager`)

1. **Unified overshell overlay** ([MenuManager.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/MenuManager.cs)): `MenuManager` lazily instantiates a single `_overshell` from `_overshellPrefab` ([Overshell.prefab](file:///home/phil/Development/YARG/Assets/Prefabs/Menu/DifficultySelect/Overshell.prefab)) and reparents it onto the active [MenuObject.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/MenuObject.cs) whenever `MenuManager.UseOvershell` is `true`, `SupportsOvershell` is enabled (`MainMenu`, `MusicLibrary`, `Content`, `GameModes`), and the menu is active in hierarchy.
2. **Event-driven visibility & layout**: `MenuManager` reacts to `SettingsManager.Settings.ShowOvershell.OnChange`, `SettingsMenu.OpenStateChanged`, `FiltersMenu.OpenStateChanged`, and `MenuObject.OnEnable()` rather than polling every frame. [MusicLibraryMenu.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/MusicLibrary/MusicLibraryMenu.cs) independently subscribes to `ShowOvershell.OnChange` only to adjust its sidebar compact layout (`SetOvershellLayout`) and play-button label.
3. **Band mode** ([DifficultySelectMenu.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/DifficultySelect/DifficultySelectMenu.cs)): Keeps its own serialized `_band` instance for song-specific band setup (`Open` / `StartSong`).

### Seat Preservation Across Screens

`BandSessionState` keeps a static `_savedSeats` map (`Dictionary<YargPlayer, int>`) and calls `PlayerContainer.SetPlayerOrder(...)` ([PlayerContainer.cs](file:///home/phil/Development/YARG/Assets/Script/Player/PlayerContainer.cs#L975-L987)) when columns are reordered or closed, so a player's horizontal card slot persists between `MenuManager`'s overshell and `DifficultySelectMenu._band`. `HandleExternalPlayerRemoved` prunes removed players from `_savedSeats`.

---

## 2. Input Routing & Join Flow

The band is split into three layers:

- [BandSelectMenu.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/DifficultySelect/BandSelectMenu.cs): The coordinator MonoBehaviour. Lifecycle (`Open`, `OpenOvershell`, `Close`), the band-mode `NavigationScheme` and overshell `InputInterceptor` (`InterceptOvershellInput`), the "Press to Join" slot cards, sliding cards when players join, leave, or move, and the song start.
- [BandSessionState.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/DifficultySelect/BandSessionState.cs): Plain C# source of truth for who is joined, slot order and saved seats, ready state, instrument blockers, and saving. It raises `ColumnsChanged`, `ColumnRebuildRequested`, `ColumnExpanded`, and `ColumnReadyChanged`.
- [BandCardController.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/DifficultySelect/BandCardController.cs): One per card. Builds what each submenu shows as a `BandCardContent` of [BandMenuItem](file:///home/phil/Development/YARG/Assets/Script/Menu/DifficultySelect/BandMenuItem.cs) rows, and handles that card's confirm, back, up/down, and stepper editing.

### How Input Flows

1. **Unclaimed devices**: [InputManager.cs](file:///home/phil/Development/YARG/Assets/Script/Input/InputManager.cs#L34) exposes `UnclaimedDevicePressed`. While `BandSelectMenu` is open and not blocked (`IsJoinBlocked`), `BandSelectMenu` hooks this callback. Pressing a button on an unclaimed non-keyboard/mouse/pen device triggers `Join(device)`, creating a card in `ChooseProfile` state (preselecting the device's saved profile if available).
2. **Overshell vs. Band Mode Navigation**:
   - In **overshell mode**, `Navigator.Instance.InputInterceptor` ([Navigator.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/Navigation/Navigator.cs#L121)) intercepts `NavigationContext` before the underlying menu's `NavigationScheme` sees it. If a player's column is open (or they press an action when an overshell card is engaged), `InterceptOvershellInput` consumes the event; otherwise normal menu navigation proceeds.
   - In **band mode** (`DifficultySelectMenu`), `BandSelectMenu` pushes its own `NavigationScheme` and routes `Up`, `Down`, `Green` (`Confirm`), `Red` (`Back`), and left/right adjustment actions to the `BandCardController` from `CardFor(context.Player)`.
3. **Mouse / Click Support**: [JoinSlotClick.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/DifficultySelect/JoinSlotClick.cs) opens a device-less profile picker when a "Press to Join" slot is clicked, and [SettingStepClick.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/DifficultySelect/SettingStepClick.cs) detects left/right clicks on numeric stepper rows (`<  value  >`).

---

## 3. Per-Player Cards

Each card is a controller and a view:

- [BandColumnViewStack.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/DifficultySelect/BandColumnViewStack.cs): `BandColumnState` enum and the submenu history (`Push`, `Pop`, `SetRoot`), including focus restore keys and the `SettingOptions` payload. It is stored on `BandColumnModel` because the session reads it for ready and sitting-out checks.
- [BandCardController.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/DifficultySelect/BandCardController.cs): Builds the rows for `ChooseProfile`, `Main`, `Difficulty`, `Instrument`, `Adjustments`, `Modifiers`, `Accessibility`, `OpenLane`, `SittingOut`, `Position`, `Settings`, and `SettingOptions`. Owns stepper edit mode and the `Back()` rules.
- [BandMenuItem.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/DifficultySelect/BandMenuItem.cs): `BandMenuItem` (button, label, toggle, or stepper row) and `BandCardContent` (heading, rows, and which row to select).
- [BandColumn.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/DifficultySelect/BandColumn.cs): The view MonoBehaviour on the card prefab. Draws a `BandCardContent`, moves the selection, fits height and scrolling, draws dividers and avatars, sizes the heading, and plays the rise, collapse, and slide-out animations. It forwards clicks and selection changes to its controller through `Clicked` and `SelectionChanged`.

In band mode, `BandSessionState` checks `IsDecided` across all columns whenever they change; once at least one player is not sitting out and every column is decided, it sets `StartPending`, and `BandSelectMenu` locks input, slides cards out, and invokes `DifficultySelectMenu.LoadGameplay()`.

---

## 4. Shared Rules & Gameplay HUD Reuse

- [PlayerSelectionRules.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/DifficultySelect/PlayerSelectionRules.cs): Extracts shared instrument/difficulty availability (`SongHasInstrument`, `CollectPossibleDifficulties`, `CollectSongInstruments`, `GetMaxHarmonyIndex`, `IsVocalsInstrument`), candidate mode & blocker evaluation (`GetCandidateModes`, `GetInstrumentBlocker`), `PickInstrument`, `ShareVocalModifiers`, `GetTierValues`/`GetTierLabel`, and unified `HasNoRangeShifts`/`SetNoRangeShifts` logic so both classic `DifficultySelectMenu` and `BandSelectMenu`/`BandCardController` obey the same rules. Covered by NUnit tests in [PlayerSelectionRulesTests.cs](file:///home/phil/Development/YARG/Assets/Editor/Tests/PlayerSelectionRulesTests.cs).
- [ProfileSettingsCatalog.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/ProfileList/ProfileSettingsCatalog.cs): Single source of truth for allowed `EnginePreset`s per `GameMode`, `STAR_POWER_ACTIVATION_TYPES`, `OPEN_LANE_DISPLAY_TYPES`, `BOT_IRRELEVANT_SETTINGS`, `ApplyGameModeDefaults`, and `NoteSpeed`/`HighwayLength` bounds shared by [ProfileCenterPane.cs](file:///home/phil/Development/YARG/Assets/Script/Menu/ProfileList/ProfileCenterPane.cs) and `BandCardController`. Covered by NUnit tests in [ProfileSettingsCatalogTests.cs](file:///home/phil/Development/YARG/Assets/Editor/Tests/ProfileSettingsCatalogTests.cs).
- [PlayerCardHeader.cs](file:///home/phil/Development/YARG/Assets/Script/Gameplay/HUD/PlayerCardHeader.cs): Encapsulates Addressables instrument icon loading/release and header text auto-sizing (`Fit`) shared by both `BandColumn` and [TrackPlayerMenu.cs](file:///home/phil/Development/YARG/Assets/Script/Gameplay/HUD/TrackPlayerMenu.cs) using the shared `PlayerCard.prefab`.
