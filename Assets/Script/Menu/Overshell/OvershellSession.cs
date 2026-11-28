using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using YARG.Core;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.Song;
using YARG.Gameplay.Visuals;
using YARG.Input.Bindings;
using YARG.Localization;
using YARG.Menu.DifficultySelect;
using YARG.Menu.ProfileList;
using YARG.Player;

namespace YARG.Menu.Overshell
{
    internal sealed class OvershellColumnModel
    {
        public YargPlayer Player { get; set; }
        public ColumnViewStack ViewStack { get; } = new();

        public bool IsReady { get; set; }
        public bool IsManuallyCollapsed { get; set; }
        public bool IsLocked { get; set; }
        public string WarningMessage { get; set; }

        public List<Modifier> Modifiers { get; } = new();
        public Modifier ExcusableModifiers { get; set; }
        public List<Difficulty> Difficulties { get; } = new();

        public ColumnState State => ViewStack.State;
        public bool IsJoining => State == ColumnState.ChooseProfile;
        public bool IsCollapsed => State == ColumnState.Main && (IsReady || IsManuallyCollapsed);
        public bool IsExpanded => !IsCollapsed;
        public bool IsAtTopLevel => ViewStack.IsAtTopLevel(IsReady);
        public bool HasChosenInstrument => ViewStack.HasChosenInstrument();

        /// <summary>
        /// Raised when this column's screen needs to be built again.
        /// </summary>
        public event Action RebuildRequested;

        public void RequestRebuild() => RebuildRequested?.Invoke();
    }

    internal readonly struct JoinedColumn
    {
        public readonly OvershellColumnModel Column;
        public readonly int ReplacedSlotIndex;

        public JoinedColumn(OvershellColumnModel column, int replacedSlotIndex)
        {
            Column = column;
            ReplacedSlotIndex = replacedSlotIndex;
        }
    }

    internal sealed class OvershellSession
    {
        public const int MAX_PLAYERS = HighwayCameraRendering.MAX_MATRICES;

        private readonly PlayerJoiner _joiner = new();
        private readonly Seating _seating = new();
        private readonly List<InputDevice> _pendingJoins = new();
        private readonly List<Instrument> _instruments = new();
        private readonly List<GameMode> _modeBuffer = new();
        private readonly List<Instrument> _otherInstrumentBuffer = new();

        private Func<bool> _canEngage;
        private Func<bool> _isDialogShowing;
        private Action<YargProfile> _onGameModeChanged;

        public IReadOnlyList<OvershellColumnModel> Slots => _seating.Slots;
        public IReadOnlyList<Instrument> Instruments => _instruments;
        public IReadOnlyList<SongEntry> Songs { get; private set; }
        public SongEntry CurrentSong { get; private set; }
        public bool IsCareer { get; private set; }
        public int MaxHarmonyIndex { get; private set; }

        public bool IsOpen { get; private set; }
        public bool IsEverywhere { get; private set; }
        public bool Hidden { get; private set; }
        public bool IsStartingSong { get; private set; }
        public bool StartPending { get; private set; }
        public int FocusIndex => _seating.FocusIndex;
        public string FooterText { get; private set; } = string.Empty;
        public bool GreenDisabled { get; private set; }

        public bool IsFull => PlayerContainer.Players.Count >= MAX_PLAYERS;
        public bool IsSuppressingPlayerEvents => _joiner.IsSuppressingPlayerEvents;
        public bool CanEngage => _canEngage != null && _canEngage();
        public bool HasPendingProfilePicker { get; private set; }

        public bool CanClickCards =>
            !IsStartingSong
            && (_isDialogShowing == null || !_isDialogShowing())
            && (!IsEverywhere || (!Hidden && CanEngage));

        public bool IsJoinBlocked => !CanClickCards || IsFull || HasPendingProfilePicker;

        public OvershellColumnModel FocusedColumn => _seating.FocusedColumn;

        public event Action ColumnsChanged;

        public void Open(List<SongEntry> songs, SongEntry currentSong, bool isCareer, bool isEverywhere,
            Func<bool> canEngage, Func<bool> isDialogShowing = null, Action<YargProfile> onGameModeChanged = null)
        {
            Songs = songs;
            CurrentSong = currentSong;
            IsCareer = isCareer;
            IsEverywhere = isEverywhere;
            _canEngage = canEngage;
            _isDialogShowing = isDialogShowing;
            _onGameModeChanged = onGameModeChanged;
            Hidden = false;
            HasPendingProfilePicker = false;
            IsOpen = true;
            StartPending = false;
            IsStartingSong = false;
            _pendingJoins.Clear();

            if (!isEverywhere)
            {
                UpdateInstruments();
            }

            var columns = new List<OvershellColumnModel>(PlayerContainer.Players.Count);
            foreach (var player in PlayerContainer.Players)
            {
                var model = new OvershellColumnModel { Player = player };
                InitializeColumnForMode(model);
                columns.Add(model);
            }

            _seating.SeatColumns(columns);
            SyncColumnsAndSlots();
        }

        public List<YargPlayer> Close()
        {
            if (!IsOpen)
            {
                return new List<YargPlayer>();
            }

            IsOpen = false;
            StartPending = false;
            HasPendingProfilePicker = false;
            _pendingJoins.Clear();
            var cancelledPlayers = _joiner.SnapshotJoiningPlayers();
            foreach (var player in cancelledPlayers)
            {
                _joiner.ReleaseJoin(player);
            }

            _seating.Clear();
            return cancelledPlayers;
        }

        public bool SetHidden(bool hidden, List<OvershellColumnModel> cancelledJoins)
        {
            bool shouldUpdateVisibility = IsOpen && IsEverywhere && hidden != Hidden;
            if (!shouldUpdateVisibility)
            {
                return false;
            }

            Hidden = hidden;
            if (!hidden)
            {
                return true;
            }

            _pendingJoins.Clear();
            HasPendingProfilePicker = false;

            var slots = _seating.Slots;
            for (int slotIndex = slots.Count - 1; slotIndex >= 0; slotIndex--)
            {
                var column = slots[slotIndex];
                if (column == null)
                {
                    continue;
                }

                if (column.IsJoining)
                {
                    cancelledJoins.Add(column);
                }
                else if (column.IsExpanded)
                {
                    ResetForEverywhere(column);
                    column.RequestRebuild();
                }
            }

            return true;
        }

        public bool IsSittingOut(OvershellColumnModel column)
        {
            bool isInstrumentMissingPlayable = column.State == ColumnState.Instrument && !HasPlayableInstrument(column);
            return column.State == ColumnState.SittingOut || isInstrumentMissingPlayable;
        }

        public bool IsDecided(OvershellColumnModel column)
        {
            bool isReadyOnMain = column.State == ColumnState.Main && column.IsReady;
            return IsSittingOut(column) || isReadyOnMain;
        }

        public bool HasPlayableInstrument(OvershellColumnModel column)
        {
            foreach (var instrument in _instruments)
            {
                if (GetInstrumentBlocker(column, instrument, out _) == null)
                {
                    return true;
                }
            }

            return false;
        }

        public string GetInstrumentBlocker(OvershellColumnModel column, Instrument instrument, out GameMode mode)
        {
            _otherInstrumentBuffer.Clear();
            foreach (var otherColumn in _seating.Slots)
            {
                if (otherColumn != null && otherColumn != column && otherColumn.HasChosenInstrument)
                {
                    _otherInstrumentBuffer.Add(otherColumn.Player.Profile.CurrentInstrument);
                }
            }

            return PlayerSelectionRules.GetInstrumentBlocker(CurrentSong, column.Player,
                instrument, _otherInstrumentBuffer, out mode, _modeBuffer);
        }

        public void ResetForEverywhere(OvershellColumnModel column)
        {
            column.IsReady = false;
            column.WarningMessage = null;
            column.IsLocked = false;
            column.IsManuallyCollapsed = true;
            column.ViewStack.SetRoot(ColumnState.Main);
        }

        public void InitializeForSong(OvershellColumnModel column)
        {
            column.Player.Profile.RestoreSavedModifiers();
            column.IsReady = false;
            column.WarningMessage = null;
            column.IsLocked = false;
            column.IsManuallyCollapsed = false;

            if (IsCareer && column.Player.Profile.IsBot)
            {
                column.IsLocked = true;
                column.ViewStack.SetRoot(ColumnState.SittingOut);
                return;
            }

            if (TryAutoSelectInstrument(column))
            {
                CollectOptions(column);
                column.ViewStack.SetRoot(ColumnState.Main);
                return;
            }

            column.ViewStack.SetRoot(ColumnState.Instrument);
        }

        public bool ExpandColumn(OvershellColumnModel column)
        {
            bool canExpand = column.State == ColumnState.Main && !column.IsReady && column.IsManuallyCollapsed;
            if (!canExpand)
            {
                return false;
            }

            column.IsManuallyCollapsed = false;
            return true;
        }

        public bool CollapseColumn(OvershellColumnModel column)
        {
            bool canCollapse = column.State == ColumnState.Main && !column.IsReady && !column.IsManuallyCollapsed;
            if (!canCollapse)
            {
                return false;
            }

            column.IsManuallyCollapsed = true;
            return true;
        }

        public void PushView(OvershellColumnModel column, ColumnState state, string focusKey = null,
            string settingTitle = null, List<SettingOption> settingOptions = null)
        {
            column.ViewStack.Push(state, focusKey, settingTitle, settingOptions);
            column.IsManuallyCollapsed = false;
            column.RequestRebuild();
            SyncColumnsAndSlots();
        }

        public bool PopView(OvershellColumnModel column)
        {
            if (!column.ViewStack.Pop())
            {
                return false;
            }

            column.IsManuallyCollapsed = false;
            column.RequestRebuild();
            SyncColumnsAndSlots();
            return true;
        }

        public void SetRootState(OvershellColumnModel column, ColumnState state)
        {
            column.ViewStack.SetRoot(state);
            column.IsManuallyCollapsed = false;
            column.RequestRebuild();
            SyncColumnsAndSlots();
        }

        public void OpenPicker(OvershellColumnModel column, bool canCancel)
        {
            if (column.IsLocked)
            {
                return;
            }

            column.IsReady = false;
            if (canCancel)
            {
                PushView(column, ColumnState.Instrument, nameof(ColumnState.Instrument));
            }
            else
            {
                SetRootState(column, ColumnState.Instrument);
            }
        }

        public bool PickInstrument(OvershellColumnModel column, Instrument instrument)
        {
            if (GetInstrumentBlocker(column, instrument, out var mode) != null)
            {
                column.RequestRebuild();
                return false;
            }

            SetGameMode(column.Player, mode);
            PlayerSelectionRules.PickInstrument(column.Player.Profile, instrument, _instruments, MaxHarmonyIndex);
            CollectOptions(column);
            column.IsReady = false;
            column.WarningMessage = null;
            SetRootState(column, ColumnState.Main);
            return true;
        }

        public void ConfirmDifficulty(OvershellColumnModel column, Difficulty difficulty)
        {
            column.Player.Profile.CurrentDifficulty = column.Player.Profile.DifficultyFallback = difficulty;
            SetRootState(column, ColumnState.Main);
        }

        public void CycleHarmonyPart(OvershellColumnModel column)
        {
            var profile = column.Player.Profile;
            profile.HarmonyIndex = (byte) ((profile.HarmonyIndex + 1) % MaxHarmonyIndex);
            column.RequestRebuild();
        }

        public void ChangeInputMode(OvershellColumnModel column, GameMode mode)
        {
            var profile = column.Player.Profile;
            if (profile.GameMode == mode)
            {
                return;
            }

            ProfileSettingsCatalog.ApplyGameModeDefaults(profile, mode);
            SetGameMode(column.Player, mode);
            _onGameModeChanged?.Invoke(profile);
        }

        public bool ToggleReady(OvershellColumnModel column)
        {
            if (!column.IsReady && !HasRequiredInputDevice(column))
            {
                column.RequestRebuild();
                return false;
            }

            column.WarningMessage = null;
            column.IsManuallyCollapsed = false;
            column.IsReady = !column.IsReady;
            SyncColumnsAndSlots();
            return true;
        }

        public bool QueuePendingJoin(InputDevice device)
        {
            if (IsJoinBlocked)
            {
                return true;
            }

            if (!_pendingJoins.Contains(device))
            {
                _pendingJoins.Add(device);
            }

            return false;
        }

        public void DrainPendingJoins(List<JoinedColumn> joined)
        {
            joined.Clear();
            if (_pendingJoins.Count == 0)
            {
                return;
            }

            foreach (var device in _pendingJoins)
            {
                if (!IsJoinBlocked && TryStartJoinWithDevice(device, out var column, out int replacedSlotIndex))
                {
                    joined.Add(new JoinedColumn(column, replacedSlotIndex));
                }
            }

            _pendingJoins.Clear();
        }

        public bool FocusColumn(OvershellColumnModel column)
        {
            if (!_seating.Focus(column))
            {
                return false;
            }

            SyncColumnsAndSlots();
            return true;
        }

        public bool MoveFocus(int direction)
        {
            if (!_seating.MoveFocus(direction))
            {
                return false;
            }

            SyncColumnsAndSlots();
            return true;
        }

        public bool CanMoveColumn(OvershellColumnModel column, int direction)
        {
            return _seating.CanMove(column, direction);
        }

        public bool MoveColumn(OvershellColumnModel column, int direction)
        {
            if (!_seating.Move(column, direction))
            {
                return false;
            }

            SyncColumnsAndSlots();
            PlayerContainer.SaveProfiles();

            foreach (var otherColumn in _seating.Slots)
            {
                if (otherColumn != null && otherColumn != column && otherColumn.State == ColumnState.Position)
                {
                    otherColumn.RequestRebuild();
                }
            }

            return true;
        }

        private bool TryStartJoinWithDevice(InputDevice device, out OvershellColumnModel column, out int replacedSlotIndex)
        {
            column = null;
            replacedSlotIndex = -1;
            if (PlayerContainer.IsControllerTaken(device))
            {
                return false;
            }

            if (!_joiner.TryAttachWithDevice(device, out var player, out var join))
            {
                return false;
            }

            column = InsertJoiningColumn(player, join, targetSlotIndex: -1, out replacedSlotIndex);
            return true;
        }

        public bool TryStartJoinWithoutDevice(int slotIndex, YargProfile chosen, out OvershellColumnModel column,
            out bool needsProfilePicker, out int replacedSlotIndex)
        {
            column = null;
            needsProfilePicker = false;
            replacedSlotIndex = -1;

            if (IsJoinBlocked || !_seating.IsEmptySlot(slotIndex))
            {
                return false;
            }

            var profile = chosen ?? PlayerJoiner.FindAvailableProfile();
            if (profile == null)
            {
                needsProfilePicker = true;
                HasPendingProfilePicker = true;
                return false;
            }

            if (!_joiner.TryAttachWithoutDevice(profile, out var player, out var join))
            {
                return false;
            }

            column = InsertJoiningColumn(player, join, slotIndex, out replacedSlotIndex);
            return true;
        }

        public void CloseProfilePicker()
        {
            HasPendingProfilePicker = false;
        }

        public bool ChooseJoinProfile(OvershellColumnModel column, YargProfile profile)
        {
            if (profile != column.Player.Profile)
            {
                var oldPlayer = column.Player;
                if (!_joiner.TryReattachProfile(oldPlayer, profile, out var newPlayer))
                {
                    RemoveColumn(column);
                    return false;
                }

                column.Player = newPlayer;
            }

            _joiner.CompleteJoin(column.Player);
            InitializeColumnForMode(column);

            SyncColumnsAndSlots();
            PlayerContainer.SaveProfiles();
            BindingsContainer.SaveBindings();
            return true;
        }

        public int CancelJoin(OvershellColumnModel column)
        {
            _joiner.ReleaseJoin(column.Player);
            return RemoveColumn(column);
        }

        public int Disconnect(OvershellColumnModel column)
        {
            if (column.IsJoining)
            {
                return CancelJoin(column);
            }

            _joiner.DisposePlayer(column.Player);
            return RemoveColumn(column);
        }

        public OvershellColumnModel HandleExternalPlayerAdded(YargPlayer player, out int replacedSlotIndex)
        {
            var model = new OvershellColumnModel { Player = player };
            InitializeColumnForMode(model);
            replacedSlotIndex = _seating.Insert(model, targetSlotIndex: -1);
            return model;
        }

        public int HandleExternalPlayerRemoved(YargPlayer player, out OvershellColumnModel removed)
        {
            _seating.ForgetSeat(player);
            if (_joiner.IsSuppressingPlayerEvents)
            {
                removed = null;
                return -1;
            }

            removed = FindColumn(player);
            return removed != null ? RemoveColumn(removed) : -1;
        }

        public int RemoveColumn(OvershellColumnModel column)
        {
            int slotIndex = _seating.Remove(column);
            _joiner.UntrackJoin(column.Player);
            SyncColumnsAndSlots();
            return slotIndex;
        }

        public void SyncColumnsAndSlots()
        {
            if (!IsOpen)
            {
                return;
            }

            var playerOrder = new List<YargPlayer>(_seating.OccupiedCount);
            bool anyJoining = false;
            foreach (var column in _seating.Slots)
            {
                if (column == null)
                {
                    continue;
                }

                playerOrder.Add(column.Player);
                if (column.IsJoining)
                {
                    anyJoining = true;
                }
            }

            PlayerContainer.SetPlayerOrder(playerOrder);
            _seating.Settle(keepEmptyJoinSlot: !anyJoining);
            ComputeStatus();
            ColumnsChanged?.Invoke();
        }

        public bool AnyEverywhereColumnOpen()
        {
            foreach (var column in _seating.Slots)
            {
                if (column == null)
                {
                    continue;
                }

                bool hasActiveControllerColumn = column.IsExpanded
                    && !IsSittingOut(column)
                    && column.Player.DeviceInfo.HasControllerAssigned;
                if (hasActiveControllerColumn)
                {
                    return true;
                }
            }

            return false;
        }

        public OvershellColumnModel ColumnFor(YargPlayer player)
        {
            return player == null ? FocusedColumn : FindColumn(player);
        }

        public OvershellColumnModel FindColumn(YargPlayer player)
        {
            return _seating.Find(player);
        }

        public void MarkStartingSong()
        {
            IsStartingSong = true;
            var players = new List<YargPlayer>(_seating.OccupiedCount);
            YargPlayer vocalLeader = null;
            foreach (var column in _seating.Slots)
            {
                if (column == null)
                {
                    continue;
                }

                column.Player.SittingOut = IsSittingOut(column);
                players.Add(column.Player);
                bool isActiveVocalist = !column.Player.SittingOut
                    && column.Player.Profile.GameMode == GameMode.Vocals;
                if (vocalLeader == null && isActiveVocalist)
                {
                    vocalLeader = column.Player;
                }
            }

            if (vocalLeader != null)
            {
                PlayerSelectionRules.ShareVocalModifiers(vocalLeader, players);
            }
        }

        public void ConsumeStartPending()
        {
            StartPending = false;
        }

        public void CollectOptions(OvershellColumnModel column)
        {
            column.ExcusableModifiers =
                PlayerSelectionRules.CollectPossibleModifiers(column.Player.Profile, column.Modifiers);
            PlayerSelectionRules.CollectPossibleDifficulties(column.Player.Profile, Songs, column.Difficulties);
        }

        private void InitializeColumnForMode(OvershellColumnModel column)
        {
            if (IsEverywhere)
            {
                ResetForEverywhere(column);
            }
            else
            {
                InitializeForSong(column);
            }
        }

        private static bool HasRequiredInputDevice(OvershellColumnModel column)
        {
            if (column.Player.IsMissingMicrophone)
            {
                column.WarningMessage = Localize.Key("Menu.DifficultySelect.Band.NoMicrophone");
                return false;
            }

            if (column.Player.IsMissingController)
            {
                column.WarningMessage = Localize.Key("Menu.DifficultySelect.Band.NoController");
                return false;
            }

            return true;
        }

        private OvershellColumnModel InsertJoiningColumn(YargPlayer player, PlayerJoiner.PendingJoin join,
            int targetSlotIndex, out int replacedSlotIndex)
        {
            var model = new OvershellColumnModel { Player = player };
            model.IsReady = false;
            model.ViewStack.SetRoot(ColumnState.ChooseProfile);

            replacedSlotIndex = _seating.Insert(model, targetSlotIndex);
            _joiner.TrackJoin(player, join);
            return model;
        }

        private void ComputeStatus()
        {
            if (IsEverywhere)
            {
                FooterText = string.Empty;
                StartPending = false;
                GreenDisabled = false;
                return;
            }

            int columnCount = _seating.OccupiedCount;
            int readyPlayerCount = 0;
            bool allDecided = columnCount > 0;
            foreach (var column in _seating.Slots)
            {
                if (column == null)
                {
                    continue;
                }

                if (!IsDecided(column))
                {
                    allDecided = false;
                }
                else if (!IsSittingOut(column))
                {
                    readyPlayerCount++;
                }
            }

            FooterText = allDecided && readyPlayerCount == 0
                ? Localize.Key("Menu.DifficultySelect.Band.NobodyPlaying")
                : string.Empty;
            StartPending = allDecided && readyPlayerCount > 0;
            GreenDisabled = columnCount == 0;
        }

        private bool TryAutoSelectInstrument(OvershellColumnModel column)
        {
            var profile = column.Player.Profile;
            foreach (var candidate in new[] { profile.PreferredInstrument, profile.CurrentInstrument })
            {
                if (!_instruments.Contains(candidate))
                {
                    continue;
                }

                if (GetInstrumentBlocker(column, candidate, out var mode) != null)
                {
                    continue;
                }

                SetGameMode(column.Player, mode);
                profile.CurrentInstrument = candidate;
                profile.ResolveHarmonyIndex(MaxHarmonyIndex);
                return true;
            }

            return false;
        }

        private static void SetGameMode(YargPlayer player, GameMode mode)
        {
            var profile = player.Profile;
            if (profile.GameMode == mode)
            {
                return;
            }

            profile.GameMode = mode;
            player.DeviceInfo.RefreshGameMode();
            PlayerContainer.NotifyProfileChanged(profile);
        }

        private void UpdateInstruments()
        {
            var song = CurrentSong;
            MaxHarmonyIndex = PlayerSelectionRules.GetMaxHarmonyIndex(song, Songs);
            PlayerSelectionRules.CollectSongInstruments(song, Songs, _instruments);
        }
    }
}
