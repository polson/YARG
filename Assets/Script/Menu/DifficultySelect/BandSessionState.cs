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
using YARG.Menu.ProfileList;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    internal sealed class BandColumnModel
    {
        public YargPlayer Player { get; set; }
        public BandColumnViewStack ViewStack { get; } = new();

        public bool IsReady { get; set; }
        public bool IsManuallyCollapsed { get; set; }
        public bool IsLocked { get; set; }
        public string WarningMessage { get; set; }

        public List<Modifier> Modifiers { get; } = new();
        public Modifier ExcusableModifiers { get; set; }
        public List<Difficulty> Difficulties { get; } = new();

        public BandColumnState State => ViewStack.State;
        public bool IsJoining => State == BandColumnState.ChooseProfile;
        public bool IsCollapsed => State == BandColumnState.Main && (IsReady || IsManuallyCollapsed);
        public bool IsExpanded => !IsCollapsed;
        public bool IsAtTopLevel => ViewStack.IsAtTopLevel(IsReady);
        public bool HasChosenInstrument => ViewStack.HasChosenInstrument();
    }

    internal readonly struct JoinedColumn
    {
        public readonly BandColumnModel Column;
        public readonly int ReplacedSlotIndex;

        public JoinedColumn(BandColumnModel column, int replacedSlotIndex)
        {
            Column = column;
            ReplacedSlotIndex = replacedSlotIndex;
        }
    }

    internal sealed class BandSessionState
    {
        public const int MAX_PLAYERS = HighwayCameraRendering.MAX_MATRICES;
        public const int MIN_SLOTS = 4;

        private static readonly Dictionary<YargPlayer, int> _savedSeats = new();

        private readonly BandPlayerJoiner _joiner = new();
        private readonly List<BandColumnModel> _slots = new();
        private readonly List<InputDevice> _pendingJoins = new();
        private readonly List<Instrument> _instruments = new();
        private readonly List<GameMode> _modeBuffer = new();
        private readonly List<Instrument> _otherInstrumentBuffer = new();

        private Func<bool> _canEngage;
        private Func<bool> _hasPendingProfilePicker;
        private Func<bool> _isDialogShowing;
        private Action<YargProfile> _onGameModeChanged;

        public IReadOnlyList<BandColumnModel> Slots => _slots;
        public IReadOnlyList<Instrument> Instruments => _instruments;
        public IReadOnlyList<SongEntry> Songs { get; private set; }
        public int MaxHarmonyIndex { get; private set; }

        public bool IsOpen { get; private set; }
        public bool IsOvershell { get; private set; }
        public bool Hidden { get; private set; }
        public bool IsStartingSong { get; private set; }
        public bool StartPending { get; private set; }
        public int FocusIndex { get; private set; }
        public string FooterText { get; private set; } = string.Empty;
        public bool GreenDisabled { get; private set; }

        public bool IsFull => PlayerContainer.Players.Count >= MAX_PLAYERS;
        public bool IsSuppressingPlayerEvents => _joiner.IsSuppressingPlayerEvents;
        public bool CanEngage => _canEngage != null && _canEngage();
        public bool HasPendingProfilePicker => _hasPendingProfilePicker != null && _hasPendingProfilePicker();

        public bool CanClickCards =>
            !IsStartingSong
            && (_isDialogShowing == null || !_isDialogShowing())
            && (!IsOvershell || (!Hidden && CanEngage));

        public bool IsJoinBlocked => !CanClickCards || IsFull || HasPendingProfilePicker;

        public BandColumnModel FocusedColumn
        {
            get
            {
                int count = OccupiedColumnCount;
                return count > 0 ? GetOccupiedColumn(Math.Clamp(FocusIndex, 0, count - 1)) : null;
            }
        }

        public event Action ColumnsChanged;
        public event Action<BandColumnModel> ColumnRebuildRequested;
        public event Action<BandColumnModel> ColumnExpanded;
        public event Action<BandColumnModel> ColumnReadyChanged;

        public void Open(List<SongEntry> songs, bool isOvershell, Func<bool> canEngage,
            Func<bool> hasPendingProfilePicker = null, Func<bool> isDialogShowing = null,
            Action<YargProfile> onGameModeChanged = null)
        {
            Songs = songs;
            IsOvershell = isOvershell;
            _canEngage = canEngage;
            _hasPendingProfilePicker = hasPendingProfilePicker;
            _isDialogShowing = isDialogShowing;
            _onGameModeChanged = onGameModeChanged;
            Hidden = false;
            IsOpen = true;
            StartPending = false;
            IsStartingSong = false;
            FocusIndex = 0;
            _pendingJoins.Clear();

            if (!isOvershell)
            {
                UpdateInstruments();
            }

            var savedSeatsCopy = new Dictionary<YargPlayer, int>(_savedSeats);
            var columns = new List<BandColumnModel>(PlayerContainer.Players.Count);
            foreach (var player in PlayerContainer.Players)
            {
                var model = new BandColumnModel { Player = player };
                InitializeColumnForMode(model);
                columns.Add(model);
            }

            int initialJoinSlots = Math.Max(MIN_SLOTS, columns.Count + 1) - columns.Count;
            AssignSavedSeats(savedSeatsCopy, columns, initialJoinSlots, _slots);
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
            _pendingJoins.Clear();
            var cancelledPlayers = _joiner.SnapshotJoiningPlayers();
            foreach (var player in cancelledPlayers)
            {
                _joiner.ReleaseJoin(player);
            }

            _slots.Clear();
            return cancelledPlayers;
        }

        public bool SetOvershellHidden(bool hidden, List<BandColumnModel> cancelledJoins,
            List<BandColumnModel> collapsedColumns)
        {
            bool shouldUpdateVisibility = IsOpen && IsOvershell && hidden != Hidden;
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

            for (int slotIndex = _slots.Count - 1; slotIndex >= 0; slotIndex--)
            {
                var column = _slots[slotIndex];
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
                    ResetForOvershell(column);
                    collapsedColumns.Add(column);
                }
            }

            return true;
        }

        public bool IsSittingOut(BandColumnModel column)
        {
            bool isInstrumentMissingPlayable = column.State == BandColumnState.Instrument && !HasPlayableInstrument(column);
            return column.State == BandColumnState.SittingOut || isInstrumentMissingPlayable;
        }

        public bool IsDecided(BandColumnModel column)
        {
            bool isReadyOnMain = column.State == BandColumnState.Main && column.IsReady;
            return IsSittingOut(column) || isReadyOnMain;
        }

        public bool HasPlayableInstrument(BandColumnModel column)
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

        public string GetInstrumentBlocker(BandColumnModel column, Instrument instrument, out GameMode mode)
        {
            _otherInstrumentBuffer.Clear();
            foreach (var otherColumn in _slots)
            {
                if (otherColumn != null && otherColumn != column && otherColumn.HasChosenInstrument)
                {
                    _otherInstrumentBuffer.Add(otherColumn.Player.Profile.CurrentInstrument);
                }
            }

            return PlayerSelectionRules.GetInstrumentBlocker(GlobalVariables.State.CurrentSong, column.Player,
                instrument, _otherInstrumentBuffer, out mode, _modeBuffer);
        }

        public void ResetForOvershell(BandColumnModel column)
        {
            column.IsReady = false;
            column.WarningMessage = null;
            column.IsLocked = false;
            column.IsManuallyCollapsed = true;
            column.ViewStack.SetRoot(BandColumnState.Main);
        }

        public void InitializeForSong(BandColumnModel column)
        {
            column.Player.Profile.RestoreSavedModifiers();
            column.IsReady = false;
            column.WarningMessage = null;
            column.IsLocked = false;
            column.IsManuallyCollapsed = false;

            if (GlobalVariables.State.CurrentCareer != null && column.Player.Profile.IsBot)
            {
                column.IsLocked = true;
                column.ViewStack.SetRoot(BandColumnState.SittingOut);
                return;
            }

            if (TryAutoSelectInstrument(column))
            {
                CollectOptions(column);
                column.ViewStack.SetRoot(BandColumnState.Main);
                return;
            }

            column.ViewStack.SetRoot(BandColumnState.Instrument);
        }

        public bool ExpandColumn(BandColumnModel column)
        {
            bool canExpand = column.State == BandColumnState.Main && !column.IsReady && column.IsManuallyCollapsed;
            if (!canExpand)
            {
                return false;
            }

            column.IsManuallyCollapsed = false;
            ColumnExpanded?.Invoke(column);
            return true;
        }

        public bool CollapseColumn(BandColumnModel column)
        {
            bool canCollapse = column.State == BandColumnState.Main && !column.IsReady && !column.IsManuallyCollapsed;
            if (!canCollapse)
            {
                return false;
            }

            column.IsManuallyCollapsed = true;
            return true;
        }

        public void PushView(BandColumnModel column, BandColumnState state, string focusKey = null,
            string settingTitle = null, List<BandSettingOption> settingOptions = null)
        {
            column.ViewStack.Push(state, focusKey, settingTitle, settingOptions);
            column.IsManuallyCollapsed = false;
            ColumnRebuildRequested?.Invoke(column);
            SyncColumnsAndSlots();
        }

        public bool PopView(BandColumnModel column)
        {
            if (!column.ViewStack.Pop())
            {
                return false;
            }

            column.IsManuallyCollapsed = false;
            ColumnRebuildRequested?.Invoke(column);
            SyncColumnsAndSlots();
            return true;
        }

        public void SetRootState(BandColumnModel column, BandColumnState state)
        {
            column.ViewStack.SetRoot(state);
            column.IsManuallyCollapsed = false;
            ColumnRebuildRequested?.Invoke(column);
            SyncColumnsAndSlots();
        }

        public void OpenPicker(BandColumnModel column, bool canCancel)
        {
            if (column.IsLocked)
            {
                return;
            }

            column.IsReady = false;
            if (canCancel)
            {
                PushView(column, BandColumnState.Instrument, nameof(BandColumnState.Instrument));
            }
            else
            {
                SetRootState(column, BandColumnState.Instrument);
            }
        }

        public bool PickInstrument(BandColumnModel column, Instrument instrument)
        {
            if (GetInstrumentBlocker(column, instrument, out var mode) != null)
            {
                ColumnRebuildRequested?.Invoke(column);
                return false;
            }

            SetGameMode(column.Player, mode);
            PlayerSelectionRules.PickInstrument(column.Player.Profile, instrument, _instruments, MaxHarmonyIndex);
            CollectOptions(column);
            column.IsReady = false;
            column.WarningMessage = null;
            SetRootState(column, BandColumnState.Main);
            return true;
        }

        public void ConfirmDifficulty(BandColumnModel column, Difficulty difficulty)
        {
            column.Player.Profile.CurrentDifficulty = column.Player.Profile.DifficultyFallback = difficulty;
            SetRootState(column, BandColumnState.Main);
        }

        public void CycleHarmonyPart(BandColumnModel column)
        {
            var profile = column.Player.Profile;
            profile.HarmonyIndex = (byte) ((profile.HarmonyIndex + 1) % MaxHarmonyIndex);
            ColumnRebuildRequested?.Invoke(column);
        }

        public void ChangeInputMode(BandColumnModel column, GameMode mode)
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

        public bool ToggleReady(BandColumnModel column)
        {
            if (!column.IsReady && !HasRequiredInputDevice(column))
            {
                ColumnRebuildRequested?.Invoke(column);
                return false;
            }

            column.WarningMessage = null;
            column.IsManuallyCollapsed = false;
            column.IsReady = !column.IsReady;
            ColumnReadyChanged?.Invoke(column);
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

        public bool FocusColumn(BandColumnModel column)
        {
            int columnIndex = IndexOfOccupiedColumn(column);
            if (columnIndex < 0 || columnIndex == FocusIndex)
            {
                return false;
            }

            FocusIndex = columnIndex;
            SyncColumnsAndSlots();
            return true;
        }

        public bool MoveFocus(int direction)
        {
            int count = OccupiedColumnCount;
            if (count == 0)
            {
                return false;
            }

            int nextFocusIndex = Math.Clamp(FocusIndex + direction, 0, count - 1);
            if (nextFocusIndex == FocusIndex)
            {
                return false;
            }

            FocusIndex = nextFocusIndex;
            SyncColumnsAndSlots();
            return true;
        }

        public bool CanMoveColumn(BandColumnModel column, int direction)
        {
            int currentSlotIndex = _slots.IndexOf(column);
            int targetSlotIndex = currentSlotIndex + direction;
            return currentSlotIndex >= 0 && targetSlotIndex >= 0 && targetSlotIndex < _slots.Count;
        }

        public bool MoveColumn(BandColumnModel column, int direction)
        {
            if (!CanMoveColumn(column, direction))
            {
                return false;
            }

            int currentSlotIndex = _slots.IndexOf(column);
            int targetSlotIndex = currentSlotIndex + direction;

            var focusedColumn = FocusedColumn;
            (_slots[currentSlotIndex], _slots[targetSlotIndex]) =
                (_slots[targetSlotIndex], _slots[currentSlotIndex]);
            FocusIndex = focusedColumn != null ? Math.Max(0, IndexOfOccupiedColumn(focusedColumn)) : 0;

            SyncColumnsAndSlots();
            PlayerContainer.SaveProfiles();

            foreach (var otherColumn in _slots)
            {
                if (otherColumn != null && otherColumn != column && otherColumn.State == BandColumnState.Position)
                {
                    ColumnRebuildRequested?.Invoke(otherColumn);
                }
            }

            return true;
        }

        public bool TryStartJoinWithDevice(InputDevice device, out BandColumnModel column, out int replacedSlotIndex)
        {
            column = null;
            replacedSlotIndex = -1;
            if (IsFull || PlayerContainer.IsControllerTaken(device))
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

        public bool TryStartJoinWithoutDevice(int slotIndex, YargProfile chosen, out BandColumnModel column,
            out bool needsProfilePicker, out int replacedSlotIndex)
        {
            column = null;
            needsProfilePicker = false;
            replacedSlotIndex = -1;

            bool isValidEmptySlot = slotIndex >= 0
                && slotIndex < _slots.Count
                && _slots[slotIndex] == null;
            if (IsJoinBlocked || !isValidEmptySlot)
            {
                return false;
            }

            var profile = chosen ?? BandPlayerJoiner.FindAvailableProfile();
            if (profile == null)
            {
                needsProfilePicker = true;
                return false;
            }

            if (!_joiner.TryAttachWithoutDevice(profile, out var player, out var join))
            {
                return false;
            }

            column = InsertJoiningColumn(player, join, slotIndex, out replacedSlotIndex);
            return true;
        }

        public bool ChooseJoinProfile(BandColumnModel column, YargProfile profile)
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

        public int CancelJoin(BandColumnModel column)
        {
            _joiner.ReleaseJoin(column.Player);
            return RemoveColumn(column);
        }

        public int Disconnect(BandColumnModel column)
        {
            if (column.IsJoining)
            {
                return CancelJoin(column);
            }

            _joiner.DisposePlayer(column.Player);
            return RemoveColumn(column);
        }

        public BandColumnModel HandleExternalPlayerAdded(YargPlayer player, out int replacedSlotIndex)
        {
            var model = new BandColumnModel { Player = player };
            InitializeColumnForMode(model);
            replacedSlotIndex = InsertColumnInSlotOrder(model, targetSlotIndex: -1);
            return model;
        }

        public int HandleExternalPlayerRemoved(YargPlayer player, out BandColumnModel removed)
        {
            _savedSeats.Remove(player);
            if (_joiner.IsSuppressingPlayerEvents)
            {
                removed = null;
                return -1;
            }

            removed = FindColumn(player);
            return removed != null ? RemoveColumn(removed) : -1;
        }

        public int RemoveColumn(BandColumnModel column)
        {
            int slotIndex = _slots.IndexOf(column);
            _joiner.UntrackJoin(column.Player);

            if (slotIndex >= 0)
            {
                int remainingColumns = OccupiedColumnCount - 1;
                if (remainingColumns + 1 < MIN_SLOTS)
                {
                    _slots[slotIndex] = null;
                }
                else
                {
                    _slots.RemoveAt(slotIndex);
                }
            }

            SyncColumnsAndSlots();
            return slotIndex;
        }

        public void SyncColumnsAndSlots()
        {
            if (!IsOpen)
            {
                return;
            }

            int columnCount = OccupiedColumnCount;
            var playerOrder = new List<YargPlayer>(columnCount);
            bool anyJoining = false;
            foreach (var column in _slots)
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

            int emptyJoinSlotCount = anyJoining ? 0 : 1;
            int targetSlotCount = Math.Max(MIN_SLOTS, columnCount + emptyJoinSlotCount);
            AdjustJoinSlotsToCount(targetSlotCount);

            SaveSeats();
            FocusIndex = Math.Clamp(FocusIndex, 0, Math.Max(0, columnCount - 1));
            ComputeStatus();
            ColumnsChanged?.Invoke();
        }

        public bool AnyOvershellColumnOpen()
        {
            foreach (var column in _slots)
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

        public BandColumnModel ColumnFor(YargPlayer player)
        {
            return player == null ? FocusedColumn : FindColumn(player);
        }

        public BandColumnModel FindColumn(YargPlayer player)
        {
            if (player == null)
            {
                return null;
            }

            foreach (var column in _slots)
            {
                if (column != null && column.Player == player)
                {
                    return column;
                }
            }

            return null;
        }

        public void MarkStartingSong()
        {
            IsStartingSong = true;
            var players = new List<YargPlayer>(OccupiedColumnCount);
            YargPlayer vocalLeader = null;
            foreach (var column in _slots)
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

        public void CollectOptions(BandColumnModel column)
        {
            column.ExcusableModifiers =
                PlayerSelectionRules.CollectPossibleModifiers(column.Player.Profile, column.Modifiers);
            PlayerSelectionRules.CollectPossibleDifficulties(column.Player.Profile, Songs, column.Difficulties);
        }

        private void InitializeColumnForMode(BandColumnModel column)
        {
            if (IsOvershell)
            {
                ResetForOvershell(column);
            }
            else
            {
                InitializeForSong(column);
            }
        }

        private static bool HasRequiredInputDevice(BandColumnModel column)
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

        private int OccupiedColumnCount
        {
            get
            {
                int count = 0;
                foreach (var slot in _slots)
                {
                    if (slot != null)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        private BandColumnModel GetOccupiedColumn(int targetColumnIndex)
        {
            int columnIndex = 0;
            foreach (var slot in _slots)
            {
                if (slot == null)
                {
                    continue;
                }

                if (columnIndex == targetColumnIndex)
                {
                    return slot;
                }

                columnIndex++;
            }

            return null;
        }

        private int IndexOfOccupiedColumn(BandColumnModel target)
        {
            int columnIndex = 0;
            foreach (var slot in _slots)
            {
                if (slot == null)
                {
                    continue;
                }

                if (slot == target)
                {
                    return columnIndex;
                }

                columnIndex++;
            }

            return -1;
        }

        private BandColumnModel InsertJoiningColumn(YargPlayer player, BandPlayerJoiner.PendingJoin join,
            int targetSlotIndex, out int replacedSlotIndex)
        {
            var model = new BandColumnModel { Player = player };
            model.IsReady = false;
            model.ViewStack.SetRoot(BandColumnState.ChooseProfile);

            replacedSlotIndex = InsertColumnInSlotOrder(model, targetSlotIndex);
            _joiner.TrackJoin(player, join);
            return model;
        }

        private int InsertColumnInSlotOrder(BandColumnModel model, int targetSlotIndex)
        {
            int replacedSlotIndex = PlaceColumnInSlot(model, targetSlotIndex);
            int columnIndex = IndexOfOccupiedColumn(model);
            if (OccupiedColumnCount > 1 && columnIndex <= FocusIndex)
            {
                FocusIndex++;
            }

            return replacedSlotIndex;
        }

        private int PlaceColumnInSlot(BandColumnModel model, int targetSlotIndex)
        {
            bool isTargetSlotEmpty = targetSlotIndex >= 0
                && targetSlotIndex < _slots.Count
                && _slots[targetSlotIndex] == null;
            if (isTargetSlotEmpty)
            {
                _slots[targetSlotIndex] = model;
                return targetSlotIndex;
            }

            for (int slotIndex = 0; slotIndex < _slots.Count; slotIndex++)
            {
                if (_slots[slotIndex] == null)
                {
                    _slots[slotIndex] = model;
                    return slotIndex;
                }
            }

            _slots.Add(model);
            return -1;
        }

        private void SaveSeats()
        {
            _savedSeats.Clear();
            for (int slotIndex = 0; slotIndex < _slots.Count; slotIndex++)
            {
                if (_slots[slotIndex] != null)
                {
                    _savedSeats[_slots[slotIndex].Player] = slotIndex;
                }
            }
        }

        private static void AssignSavedSeats(
            Dictionary<YargPlayer, int> savedSeats,
            IReadOnlyList<BandColumnModel> columns,
            int joinSlotCount,
            List<BandColumnModel> slots)
        {
            int slotCount = columns.Count + joinSlotCount;
            slots.Clear();
            for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
            {
                slots.Add(null);
            }

            var unseatedColumns = new List<BandColumnModel>();
            foreach (var column in columns)
            {
                bool hasFreeSavedSeat = savedSeats.TryGetValue(column.Player, out int savedSlotIndex)
                    && savedSlotIndex >= 0
                    && savedSlotIndex < slotCount
                    && slots[savedSlotIndex] == null;
                if (hasFreeSavedSeat)
                {
                    slots[savedSlotIndex] = column;
                }
                else
                {
                    unseatedColumns.Add(column);
                }
            }

            int nextFreeSlotIndex = 0;
            foreach (var column in unseatedColumns)
            {
                while (slots[nextFreeSlotIndex] != null)
                {
                    nextFreeSlotIndex++;
                }

                slots[nextFreeSlotIndex] = column;
            }
        }

        private void AdjustJoinSlotsToCount(int targetSlotCount)
        {
            while (_slots.Count > targetSlotCount)
            {
                int lastEmptySlotIndex = _slots.LastIndexOf(null);
                if (lastEmptySlotIndex < 0)
                {
                    break;
                }

                _slots.RemoveAt(lastEmptySlotIndex);
            }

            while (_slots.Count < targetSlotCount)
            {
                _slots.Add(null);
            }
        }

        private void ComputeStatus()
        {
            if (IsOvershell)
            {
                FooterText = string.Empty;
                StartPending = false;
                GreenDisabled = false;
                return;
            }

            int columnCount = OccupiedColumnCount;
            int readyPlayerCount = 0;
            bool allDecided = columnCount > 0;
            foreach (var column in _slots)
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

        private bool TryAutoSelectInstrument(BandColumnModel column)
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
            var song = GlobalVariables.State.CurrentSong;
            MaxHarmonyIndex = PlayerSelectionRules.GetMaxHarmonyIndex(song, Songs);
            PlayerSelectionRules.CollectSongInstruments(song, Songs, _instruments);
        }
    }
}
