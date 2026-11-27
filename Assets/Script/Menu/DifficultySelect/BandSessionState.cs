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
using YARG.Menu.Filters;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Menu.ProfileList;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    internal sealed class BandColumnModel
    {
        public YargPlayer Player { get; set; }
        public BandColumnViewStack ViewStack { get; } = new();
        public bool Ready { get; set; }
        public bool Collapsed { get; set; }
        public bool Locked { get; set; }
        public string Warning { get; set; }
        public List<Modifier> Modifiers { get; } = new();
        public Modifier ExcusableModifiers { get; set; }
        public List<Difficulty> Difficulties { get; } = new();

        public BandColumnState State => ViewStack.State;
        public bool IsJoining => State == BandColumnState.ChooseProfile;
        public bool IsCollapsed => State == BandColumnState.Main && (Ready || Collapsed);
        public bool IsExpanded => !IsCollapsed;
        public bool IsAtTopLevel => ViewStack.IsAtTopLevel(Ready);
        public bool HasChosenInstrument => ViewStack.HasChosenInstrument();
    }

    internal sealed class BandSessionState
    {
        public const int MAX_PLAYERS = HighwayCameraRendering.MAX_MATRICES;
        public const int MIN_SLOTS = 4;

        private static readonly Dictionary<YargPlayer, int> _savedSeats = new();

        private readonly BandPlayerJoiner _joiner = new();
        private readonly List<BandColumnModel> _columns = new();
        private readonly List<YargPlayer> _slotAssignments = new();
        private readonly List<Instrument> _instruments = new();
        private readonly List<GameMode> _modeBuffer = new();
        private readonly List<Instrument> _otherInstrumentBuffer = new();

        private Func<bool> _canEngage;

        public IReadOnlyList<BandColumnModel> Columns => _columns;
        public IReadOnlyList<YargPlayer> SlotAssignments => _slotAssignments;
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

        public bool CanClickCards
        {
            get
            {
                if (IsStartingSong || DialogManager.Instance.IsDialogShowing)
                {
                    return false;
                }

                if (!IsOvershell)
                {
                    return true;
                }

                if (Hidden)
                {
                    return false;
                }

                return CanEngage;
            }
        }

        public bool IsJoinBlocked => !CanClickCards || IsFull;

        public BandColumnModel FocusedColumn =>
            _columns.Count > 0 ? _columns[Math.Clamp(FocusIndex, 0, _columns.Count - 1)] : null;

        public event Action ColumnsChanged;
        public event Action<BandColumnModel> ColumnRebuildRequested;

        public event Action<BandColumnModel> ColumnExpanded;

        public event Action<BandColumnModel> ColumnReadyChanged;

        public void Open(List<SongEntry> songs, bool isOvershell, Func<bool> canEngage)
        {
            Songs = songs;
            IsOvershell = isOvershell;
            _canEngage = canEngage;
            Hidden = false;
            IsOpen = true;
            StartPending = false;
            IsStartingSong = false;
            FocusIndex = 0;

            if (!isOvershell)
            {
                UpdateInstruments();
            }

            var seats = new Dictionary<YargPlayer, int>(_savedSeats);
            _columns.Clear();
            foreach (var player in PlayerContainer.Players)
            {
                var model = new BandColumnModel { Player = player };
                if (isOvershell)
                {
                    ResetForOvershell(model);
                }
                else
                {
                    InitializeForSong(model);
                }

                _columns.Add(model);
            }

            int initialJoinSlots = Math.Max(MIN_SLOTS, _columns.Count + 1) - _columns.Count;
            AssignSavedSeats(seats, PlayerContainer.Players, initialJoinSlots, _slotAssignments);
            SortColumnsBySlotOrder();
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
            var cancelled = _joiner.SnapshotJoiningPlayers();
            foreach (var player in cancelled)
            {
                _joiner.ReleaseJoin(player);
                _slotAssignments.Remove(player);
            }

            _columns.Clear();
            _slotAssignments.Clear();
            return cancelled;
        }
        public bool SetOvershellHidden(bool hidden, List<BandColumnModel> cancelledJoins,
            List<BandColumnModel> collapsedColumns)
        {
            if (!IsOpen || !IsOvershell || hidden == Hidden)
            {
                return false;
            }

            Hidden = hidden;
            if (!hidden)
            {
                return true;
            }

            for (int columnIndex = _columns.Count - 1; columnIndex >= 0; columnIndex--)
            {
                var column = _columns[columnIndex];
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
            return column.State == BandColumnState.SittingOut ||
                (column.State == BandColumnState.Instrument && !HasPlayableInstrument(column));
        }
        public bool IsDecided(BandColumnModel column)
        {
            return IsSittingOut(column) || (column.State == BandColumnState.Main && column.Ready);
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
            foreach (var otherColumn in _columns)
            {
                if (otherColumn != column && otherColumn.HasChosenInstrument)
                {
                    _otherInstrumentBuffer.Add(otherColumn.Player.Profile.CurrentInstrument);
                }
            }

            return PlayerSelectionRules.GetInstrumentBlocker(GlobalVariables.State.CurrentSong, column.Player,
                instrument, _otherInstrumentBuffer, out mode, _modeBuffer);
        }
        public void ResetForOvershell(BandColumnModel column)
        {
            column.Ready = false;
            column.Warning = null;
            column.Locked = false;
            column.Collapsed = true;
            column.ViewStack.SetRoot(BandColumnState.Main);
        }
        public void InitializeForSong(BandColumnModel column)
        {
            column.Player.Profile.RestoreSavedModifiers();
            column.Ready = false;
            column.Warning = null;
            column.Locked = false;
            column.Collapsed = false;

            if (GlobalVariables.State.CurrentCareer != null && column.Player.Profile.IsBot)
            {
                column.Locked = true;
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
            if (column.State != BandColumnState.Main || column.Ready || !column.Collapsed)
            {
                return false;
            }

            column.Collapsed = false;
            ColumnExpanded?.Invoke(column);
            return true;
        }
        public bool CollapseColumn(BandColumnModel column)
        {
            if (column.State != BandColumnState.Main || column.Ready || column.Collapsed)
            {
                return false;
            }

            column.Collapsed = true;
            return true;
        }
        public void PushView(BandColumnModel column, BandColumnState state, string focusKey = null,
            string settingTitle = null, List<(string Label, bool Current, Action Pick)> settingOptions = null)
        {
            column.ViewStack.Push(state, focusKey, settingTitle, settingOptions);
            column.Collapsed = false;
            ColumnRebuildRequested?.Invoke(column);
            SyncColumnsAndSlots();
        }
        public bool PopView(BandColumnModel column)
        {
            if (!column.ViewStack.Pop())
            {
                return false;
            }

            column.Collapsed = false;
            ColumnRebuildRequested?.Invoke(column);
            SyncColumnsAndSlots();
            return true;
        }
        public void SetRootState(BandColumnModel column, BandColumnState state)
        {
            column.ViewStack.SetRoot(state);
            column.Collapsed = false;
            ColumnRebuildRequested?.Invoke(column);
            SyncColumnsAndSlots();
        }
        public void OpenPicker(BandColumnModel column, bool canCancel)
        {
            if (column.Locked)
            {
                return;
            }

            column.Ready = false;
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
            column.Ready = false;
            column.Warning = null;
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
            FiltersMenu.ResetIntensityFiltersForProfile(profile);
        }
        public bool ToggleReady(BandColumnModel column)
        {
            if (!column.Ready)
            {
                if (column.Player.IsMissingMicrophone)
                {
                    column.Warning = Localize.Key("Menu.DifficultySelect.Band.NoMicrophone");
                    ColumnRebuildRequested?.Invoke(column);
                    return false;
                }

                if (column.Player.IsMissingController)
                {
                    column.Warning = Localize.Key("Menu.DifficultySelect.Band.NoController");
                    ColumnRebuildRequested?.Invoke(column);
                    return false;
                }
            }

            column.Warning = null;
            column.Collapsed = false;
            column.Ready = !column.Ready;
            ColumnReadyChanged?.Invoke(column);
            SyncColumnsAndSlots();
            return true;
        }
        public bool FocusColumn(BandColumnModel column)
        {
            int columnIndex = _columns.IndexOf(column);
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
            if (_columns.Count == 0)
            {
                return false;
            }

            int nextFocusIndex = Math.Clamp(FocusIndex + direction, 0, _columns.Count - 1);
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
            int currentSlotIndex = _slotAssignments.IndexOf(column.Player);
            int targetSlotIndex = currentSlotIndex + direction;
            return currentSlotIndex >= 0 && targetSlotIndex >= 0 && targetSlotIndex < _slotAssignments.Count;
        }
        public bool MoveColumn(BandColumnModel column, int direction)
        {
            int currentSlotIndex = _slotAssignments.IndexOf(column.Player);
            int targetSlotIndex = currentSlotIndex + direction;
            bool isValidMove = currentSlotIndex >= 0
                && targetSlotIndex >= 0
                && targetSlotIndex < _slotAssignments.Count;
            if (!isValidMove)
            {
                return false;
            }

            var focusedColumn = FocusedColumn;
            (_slotAssignments[currentSlotIndex], _slotAssignments[targetSlotIndex]) =
                (_slotAssignments[targetSlotIndex], _slotAssignments[currentSlotIndex]);
            SortColumnsBySlotOrder();
            FocusIndex = focusedColumn != null ? Math.Max(0, _columns.IndexOf(focusedColumn)) : 0;

            SyncColumnsAndSlots();
            PlayerContainer.SaveProfiles();

            foreach (var otherColumn in _columns)
            {
                if (otherColumn != column && otherColumn.State == BandColumnState.Position)
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
                && slotIndex < _slotAssignments.Count
                && _slotAssignments[slotIndex] == null;
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

                int slotIndex = _slotAssignments.IndexOf(oldPlayer);
                if (slotIndex >= 0)
                {
                    _slotAssignments[slotIndex] = newPlayer;
                }

                column.Player = newPlayer;
            }

            _joiner.CompleteJoin(column.Player);
            if (IsOvershell)
            {
                ResetForOvershell(column);
            }
            else
            {
                InitializeForSong(column);
            }

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
            if (IsOvershell)
            {
                ResetForOvershell(model);
            }
            else
            {
                InitializeForSong(model);
            }

            replacedSlotIndex = PlacePlayerInSlot(player, targetSlotIndex: -1);
            int columnIndex = CountPrecedingColumns(player);
            _columns.Insert(columnIndex, model);
            if (_columns.Count > 1 && columnIndex <= FocusIndex)
            {
                FocusIndex++;
            }

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
            int slotIndex = _slotAssignments.IndexOf(column.Player);
            _columns.Remove(column);
            _joiner.UntrackJoin(column.Player);

            if (slotIndex >= 0)
            {
                if (_columns.Count + 1 < MIN_SLOTS)
                {
                    _slotAssignments[slotIndex] = null;
                }
                else
                {
                    _slotAssignments.RemoveAt(slotIndex);
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

            var playerOrder = new List<YargPlayer>(_columns.Count);
            bool anyJoining = false;
            foreach (var column in _columns)
            {
                playerOrder.Add(column.Player);
                if (column.IsJoining)
                {
                    anyJoining = true;
                }
            }

            PlayerContainer.SetPlayerOrder(playerOrder);

            int emptyJoinSlotCount = anyJoining ? 0 : 1;
            int targetSlotCount = Math.Max(MIN_SLOTS, _columns.Count + emptyJoinSlotCount);
            AdjustJoinSlotsToCount(targetSlotCount);

            SaveSeats();
            FocusIndex = Math.Clamp(FocusIndex, 0, Math.Max(0, _columns.Count - 1));
            ComputeStatus();
            ColumnsChanged?.Invoke();
        }
        public bool AnyOvershellColumnOpen()
        {
            foreach (var column in _columns)
            {
                if (column.IsExpanded && !IsSittingOut(column) && column.Player.DeviceInfo.HasControllerAssigned)
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

            foreach (var column in _columns)
            {
                if (column.Player == player)
                {
                    return column;
                }
            }

            return null;
        }
        public void MarkStartingSong()
        {
            IsStartingSong = true;
            var players = new List<YargPlayer>(_columns.Count);
            YargPlayer vocalLeader = null;
            foreach (var column in _columns)
            {
                column.Player.SittingOut = IsSittingOut(column);
                players.Add(column.Player);
                bool isActiveVocalist = !column.Player.SittingOut &&
                    column.Player.Profile.GameMode == GameMode.Vocals;
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

        private BandColumnModel InsertJoiningColumn(YargPlayer player, BandPlayerJoiner.PendingJoin join,
            int targetSlotIndex, out int replacedSlotIndex)
        {
            var model = new BandColumnModel { Player = player };
            model.Ready = false;
            model.ViewStack.SetRoot(BandColumnState.ChooseProfile);

            replacedSlotIndex = PlacePlayerInSlot(player, targetSlotIndex);
            int columnIndex = CountPrecedingColumns(player);
            _columns.Insert(columnIndex, model);
            if (_columns.Count > 1 && columnIndex <= FocusIndex)
            {
                FocusIndex++;
            }

            _joiner.TrackJoin(player, join);
            return model;
        }

        private int PlacePlayerInSlot(YargPlayer player, int targetSlotIndex)
        {
            if (targetSlotIndex >= 0 && targetSlotIndex < _slotAssignments.Count &&
                _slotAssignments[targetSlotIndex] == null)
            {
                _slotAssignments[targetSlotIndex] = player;
                return targetSlotIndex;
            }

            for (int slotIndex = 0; slotIndex < _slotAssignments.Count; slotIndex++)
            {
                if (_slotAssignments[slotIndex] == null)
                {
                    _slotAssignments[slotIndex] = player;
                    return slotIndex;
                }
            }

            _slotAssignments.Add(player);
            return -1;
        }

        private int CountPrecedingColumns(YargPlayer player)
        {
            int targetSlotIndex = _slotAssignments.IndexOf(player);
            int precedingCount = 0;
            foreach (var column in _columns)
            {
                if (_slotAssignments.IndexOf(column.Player) < targetSlotIndex)
                {
                    precedingCount++;
                }
            }

            return precedingCount;
        }

        private void SaveSeats()
        {
            _savedSeats.Clear();
            for (int slotIndex = 0; slotIndex < _slotAssignments.Count; slotIndex++)
            {
                if (_slotAssignments[slotIndex] != null)
                {
                    _savedSeats[_slotAssignments[slotIndex]] = slotIndex;
                }
            }
        }

        private static void AssignSavedSeats(
            Dictionary<YargPlayer, int> seats,
            IReadOnlyList<YargPlayer> players,
            int joinSlotCount,
            List<YargPlayer> slotAssignments)
        {
            int slotCount = players.Count + joinSlotCount;
            slotAssignments.Clear();
            for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
            {
                slotAssignments.Add(null);
            }

            var unseated = new List<YargPlayer>();
            foreach (var player in players)
            {
                bool hasFreeSavedSeat = seats.TryGetValue(player, out int seat) && seat >= 0 && seat < slotCount &&
                    slotAssignments[seat] == null;
                if (hasFreeSavedSeat)
                {
                    slotAssignments[seat] = player;
                }
                else
                {
                    unseated.Add(player);
                }
            }

            int nextFreeSlotIndex = 0;
            foreach (var player in unseated)
            {
                while (slotAssignments[nextFreeSlotIndex] != null)
                {
                    nextFreeSlotIndex++;
                }

                slotAssignments[nextFreeSlotIndex] = player;
            }
        }

        private void SortColumnsBySlotOrder()
        {
            _columns.Sort((leftColumn, rightColumn) =>
                _slotAssignments.IndexOf(leftColumn.Player).CompareTo(_slotAssignments.IndexOf(rightColumn.Player)));
        }

        private void AdjustJoinSlotsToCount(int targetSlotCount)
        {
            while (_slotAssignments.Count > targetSlotCount)
            {
                int lastEmptySlotIndex = _slotAssignments.LastIndexOf(null);
                if (lastEmptySlotIndex < 0)
                {
                    break;
                }

                _slotAssignments.RemoveAt(lastEmptySlotIndex);
            }

            while (_slotAssignments.Count < targetSlotCount)
            {
                _slotAssignments.Add(null);
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

            int readyPlayerCount = 0;
            bool allDecided = _columns.Count > 0;
            foreach (var column in _columns)
            {
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
            GreenDisabled = _columns.Count == 0;
        }

        private bool TryAutoSelectInstrument(BandColumnModel column)
        {
            var profile = column.Player.Profile;
            foreach (var candidate in new[] { profile.PreferredInstrument, profile.CurrentInstrument })
            {
                if (!ContainsInstrument(_instruments, candidate))
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

        private static bool ContainsInstrument(IReadOnlyList<Instrument> instruments, Instrument target)
        {
            for (int instrumentIndex = 0; instrumentIndex < instruments.Count; instrumentIndex++)
            {
                if (instruments[instrumentIndex] == target)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
