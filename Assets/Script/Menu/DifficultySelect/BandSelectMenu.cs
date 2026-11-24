using System;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.Song;
using YARG.Gameplay.Visuals;
using YARG.Helpers;
using YARG.Input;
using YARG.Input.Bindings;
using YARG.Localization;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Menu.ProfileList;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    public sealed class BandSelectMenu : MonoBehaviour
    {
        internal static readonly Color READY_COLOR = new Color32(0x00, 0xD3, 0xFF, 0xFF);

        private const int MAX_PLAYERS = HighwayCameraRendering.MAX_MATRICES;
        private const int MIN_SLOTS = 4;

        private const float MAX_COLUMN_WIDTH = 460f;
        internal const float SLIDE_DISTANCE = 900f;
        internal const float JOIN_STRIP_HEIGHT = 72f;
        internal const float SLIDE_SECONDS = 0.3f;
        private const int OPEN_CARD_SORTING_ORDER = 107;

        private static readonly GameMode[] NEW_PROFILE_MODES =
        {
            GameMode.FiveFretGuitar,
            GameMode.FourLaneDrums,
            GameMode.FiveLaneDrums,
            GameMode.EliteDrums,
            GameMode.SixFretGuitar,
            GameMode.ProKeys,
            GameMode.ProGuitar,
            GameMode.Vocals,
        };

        [SerializeField]
        private RectTransform _band;
        [SerializeField]
        private LayoutElement _joinSlot;
        [SerializeField]
        private RectTransform _joinCard;
        [SerializeField]
        private TextMeshProUGUI _footer;
        [SerializeField]
        private RectTransform _footerBackground;
        [SerializeField]
        private BandColumn _columnPrefab;

        private static readonly Dictionary<YargPlayer, int> _seats = new();

        private readonly List<BandColumn> _columns = new();
        private readonly List<Instrument> _instruments = new();
        private readonly Dictionary<RectTransform, Tween> _shifts = new();
        private readonly List<LayoutElement> _joinSlots = new();
        private readonly Dictionary<BandColumn, (InputDevice Device, bool Owned, YargProfile Created)> _joins = new();

        private List<SongEntry> _songs;
        private Action _startSong;
        private int _focusIndex;
        private bool _startPending;
        private bool _started;
        private IDisposable _startInputBlocker;
        private Tween _startDelay;
        private Canvas _bandCanvas;
        private int _bandSortingOrder;
        private bool _suppressPlayerEvents;
        private readonly List<InputDevice> _pendingJoins = new();
        private Func<bool> _canEngage;
        private NavigationScheme _scheme;
        private bool _hidden;

        internal IReadOnlyList<SongEntry> Songs => _songs;
        internal IReadOnlyList<Instrument> Instruments => _instruments;
        internal int MaxHarmonyIndex { get; private set; }
        internal DifficultyRing RingTemplate { get; private set; }

        public bool IsOpen { get; private set; }
        public bool IsOvershell { get; private set; }

        private bool IsFull => PlayerContainer.Players.Count >= MAX_PLAYERS;

        private bool IsJoinBlocked => _started || DialogManager.Instance.IsDialogShowing || IsFull ||
            (IsOvershell && (_hidden || !_canEngage()));

        public void DiscardCopiedContent()
        {
            foreach (Transform child in _band)
            {
                if (child != _joinSlot.transform)
                {
                    Destroy(child.gameObject);
                }
            }
        }

        public void Open(List<SongEntry> songs, DifficultyRing ringTemplate, Action startSong)
        {
            _songs = songs;
            RingTemplate = ringTemplate;
            _startSong = startSong;
            UpdateInstruments();

            IsOvershell = false;
            OpenBand();

            _scheme = new NavigationScheme(new()
            {
                new NavigationScheme.Entry(MenuAction.Up, "Menu.Common.Up",
                    ctx => ColumnFor(ctx.Player)?.Nav.SelectPrevious(ctx.IsRepeat)),
                new NavigationScheme.Entry(MenuAction.Down, "Menu.Common.Down",
                    ctx => ColumnFor(ctx.Player)?.Nav.SelectNext(ctx.IsRepeat)),
                new NavigationScheme.Entry(MenuAction.Green, "Menu.Common.Confirm", OnConfirm),
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", OnBack),
            }, allowsMusicPlayer: false);
            _ = Navigator.Instance.PushScheme(_scheme);
            Navigator.Instance.NavigationEvent += OnNavigationEvent;

            ListenForPlayers();
            OnColumnChanged();
        }

        public void OpenOvershell(Func<bool> canEngage)
        {
            _canEngage = canEngage;
            _hidden = false;

            IsOvershell = true;
            OpenBand();
            Navigator.Instance.InputInterceptor = InterceptOvershellInput;

            ListenForPlayers();
            OnColumnChanged();
        }

        private void OpenBand()
        {
            IsOpen = true;
            _startPending = false;
            _started = false;
            _focusIndex = 0;

            _joinSlot.gameObject.SetActive(false);
            _band.gameObject.SetActive(true);
            _footer.gameObject.SetActive(!IsOvershell);
            ShowFooterBackground(true);

            var seats = new Dictionary<YargPlayer, int>(_seats);
            foreach (var player in PlayerContainer.Players.ToList())
            {
                var column = InsertColumn(player, before: null);
                if (!IsOvershell)
                {
                    column.RiseFrom(-SLIDE_DISTANCE);
                }

                column.Open();
            }

            SetJoinSlotCount(Mathf.Max(MIN_SLOTS, _columns.Count + 1) - _columns.Count);
            ApplySeats(seats);
        }

        private void ApplySeats(Dictionary<YargPlayer, int> seats)
        {
            int count = _columns.Count + _joinSlots.Count;
            var seated = new Transform[count];
            var unseated = new List<BandColumn>();

            foreach (var column in _columns)
            {
                if (seats.TryGetValue(column.Player, out int seat) && seat < count && seated[seat] == null)
                {
                    seated[seat] = column.transform;
                }
                else
                {
                    unseated.Add(column);
                }
            }

            int next = 0;
            foreach (var column in unseated)
            {
                while (seated[next] != null)
                {
                    next++;
                }

                seated[next] = column.transform;
            }

            var joinSlots = new Queue<LayoutElement>(_joinSlots.OrderBy(slot => slot.transform.GetSiblingIndex()));
            for (int i = 0; i < count; i++)
            {
                seated[i] ??= joinSlots.Dequeue().transform;
                seated[i].SetAsLastSibling();
            }

            _columns.Sort((a, b) => a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));
        }

        private void RecordSeats()
        {
            var slots = OrderedSlots();
            _seats.Clear();
            foreach (var column in _columns)
            {
                _seats[column.Player] = slots.IndexOf(column.transform);
            }
        }

        private void ListenForPlayers()
        {
            InputManager.UnclaimedDevicePressed = OnUnclaimedDevicePressed;
            PlayerContainer.PlayerAdded += OnPlayerAdded;
            PlayerContainer.PlayerRemoved += OnPlayerRemoved;
        }

        public void SetOvershellHidden(bool hidden)
        {
            bool overshellOpen = IsOpen && IsOvershell;
            if (!overshellOpen || hidden == _hidden)
            {
                return;
            }

            _hidden = hidden;
            if (hidden)
            {
                foreach (var column in _columns.ToList())
                {
                    if (column.IsJoining)
                    {
                        CancelJoin(column);
                    }
                    else if (column.IsExpanded)
                    {
                        column.Collapse();
                    }
                }
            }

            _band.gameObject.SetActive(!hidden);
            ShowFooterBackground(!hidden);
        }

        public void Close()
        {
            if (!IsOpen)
            {
                return;
            }

            IsOpen = false;
            _pendingJoins.Clear();
            _startDelay?.Kill();
            _startDelay = null;
            ReleaseStartInputBlocker();
            Unsubscribe();

            foreach (var column in _joins.Keys.ToList())
            {
                CancelJoin(column);
            }

            foreach (var column in _columns)
            {
                Destroy(column.gameObject);
            }

            _columns.Clear();

            _band.gameObject.SetActive(false);
            _footer.gameObject.SetActive(false);
            ShowFooterBackground(false);

            if (!IsOvershell)
            {
                Navigator.Instance.PopScheme();
            }
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        private void Unsubscribe()
        {
            if (InputManager.UnclaimedDevicePressed == OnUnclaimedDevicePressed)
            {
                InputManager.UnclaimedDevicePressed = null;
            }

            PlayerContainer.PlayerAdded -= OnPlayerAdded;
            PlayerContainer.PlayerRemoved -= OnPlayerRemoved;

            var navigator = Navigator.Instance;
            if (navigator == null)
            {
                return;
            }

            navigator.NavigationEvent -= OnNavigationEvent;
            if (navigator.InputInterceptor == InterceptOvershellInput)
            {
                navigator.InputInterceptor = null;
            }
        }

        private void Update()
        {
            if (!IsOpen)
            {
                return;
            }

            if (_pendingJoins.Count > 0)
            {
                var devices = _pendingJoins.ToList();
                _pendingJoins.Clear();

                foreach (var device in devices)
                {
                    if (!IsJoinBlocked)
                    {
                        Join(device);
                    }
                }
            }

            if (_startPending)
            {
                _startPending = false;
                StartSong();
            }

            UpdateSortingOrder();
        }

        private void UpdateSortingOrder()
        {
            if (_bandCanvas == null)
            {
                if (!_band.TryGetComponent(out _bandCanvas))
                {
                    return;
                }

                _bandSortingOrder = _bandCanvas.sortingOrder;
            }

            bool raise = IsOvershell && _columns.Any(c => c.IsExpanded);
            _bandCanvas.sortingOrder = raise ? OPEN_CARD_SORTING_ORDER : _bandSortingOrder;
        }

        internal void OnColumnChanged()
        {
            if (!IsOpen)
            {
                return;
            }

            PlayerContainer.SetPlayerOrder(_columns.Select(c => c.Player).ToList());

            int slots = Mathf.Max(MIN_SLOTS, _columns.Count + 1);
            SetJoinSlotCount(slots - _columns.Count);
            RecordSeats();

            string joinText = IsFull
                ? Localize.KeyFormat("Menu.DifficultySelect.Band.Full", MAX_PLAYERS)
                : Localize.Key("Menu.DifficultySelect.Band.JoinPrompt");

            float width = GetColumnWidth(slots);
            foreach (var slot in _joinSlots)
            {
                slot.preferredWidth = width;
                var card = JoinCard(slot);
                card.sizeDelta = new Vector2(width, card.sizeDelta.y);
                slot.GetComponentInChildren<TextMeshProUGUI>(true).text = joinText;
            }

            _focusIndex = Mathf.Clamp(_focusIndex, 0, Mathf.Max(0, _columns.Count - 1));
            for (int i = 0; i < _columns.Count; i++)
            {
                _columns[i].SetWidth(width);
                _columns[i].SetFocused(IsOvershell ||
                    (i == _focusIndex && !PlayerContainer.HasConnectedKeyboardProfile()));
            }

            UpdateStatus();
        }

        private void SetJoinSlotCount(int count)
        {
            while (_joinSlots.Count > count)
            {
                DestroyJoinSlot(_joinSlots.OrderBy(slot => slot.transform.GetSiblingIndex()).Last());
            }

            while (_joinSlots.Count < count)
            {
                CreateJoinSlot(_band.childCount);
            }
        }

        private LayoutElement CreateJoinSlot(int siblingIndex)
        {
            var slot = Instantiate(_joinSlot, _band);
            slot.transform.SetSiblingIndex(siblingIndex);
            JoinCard(slot).anchoredPosition = Vector2.zero;
            slot.gameObject.SetActive(true);
            _joinSlots.Add(slot);
            return slot;
        }

        private void DestroyJoinSlot(LayoutElement slot)
        {
            var card = JoinCard(slot);
            _joinSlots.Remove(slot);
            _shifts.Remove(card);
            card.DOKill();
            slot.gameObject.SetActive(false);
            Destroy(slot.gameObject);
        }

        private static RectTransform JoinCard(LayoutElement slot)
        {
            return (RectTransform) slot.transform.GetChild(0);
        }

        internal void FocusColumn(BandColumn column)
        {
            int index = _columns.IndexOf(column);
            if (index < 0 || index == _focusIndex)
            {
                return;
            }

            _focusIndex = index;
            OnColumnChanged();
        }

        internal string GetInstrumentBlocker(BandColumn column, Instrument instrument, out GameMode mode)
        {
            var player = column.Player;
            var profile = player.Profile;

            var candidates = GetCandidateModes(profile, instrument);
            mode = candidates[0];

            if (instrument is Instrument.Vocals or Instrument.Harmony or Instrument.PartyVocals)
            {
                foreach (var other in _columns)
                {
                    if (other == column || !other.HasChosenInstrument)
                    {
                        continue;
                    }

                    var otherInstrument = other.Player.Profile.CurrentInstrument;
                    if (otherInstrument is Instrument.Vocals or Instrument.Harmony or Instrument.PartyVocals &&
                        otherInstrument != instrument)
                    {
                        return Localize.Key("Menu.DifficultySelect.Band.SingersMustMatch");
                    }
                }

                mode = GameMode.Vocals;
                if (profile.IsBot || player.DeviceInfo.HasMicrophoneAssigned)
                {
                    return null;
                }

                return Localize.Key("Menu.DifficultySelect.Band.NeedsMicrophone");
            }

            if (profile.IsBot)
            {
                return null;
            }

            if (!player.DeviceInfo.HasControllerAssigned)
            {
                return Localize.Key("Menu.DifficultySelect.Band.NeedsController");
            }

            foreach (var candidate in candidates)
            {
                if (player.DeviceInfo.HasGameplayBindingsForMode(candidate))
                {
                    mode = candidate;
                    return null;
                }
            }

            return Localize.Key("Menu.DifficultySelect.Band.NoBinding");
        }

        internal void ChooseJoinProfile(BandColumn column, YargProfile profile)
        {
            if (profile != column.Player.Profile)
            {
                var device = _joins[column].Device;
                ReleaseJoin(column);

                var player = AttachPlayer(profile, device, out _);
                if (player is null)
                {
                    RemoveColumn(column);
                    return;
                }

                column.Player = player;
            }

            _joins.Remove(column);
            column.CompleteJoin();
            OnColumnChanged();
            PlayerContainer.SaveProfiles();
            BindingsContainer.SaveBindings();
            RefreshProfilePickers(column);
        }

        private void RefreshProfilePickers(BandColumn changed)
        {
            foreach (var column in _columns)
            {
                if (column != changed && column.State == BandColumnState.ChooseProfile)
                {
                    column.Rebuild();
                }
            }
        }

        internal void CancelJoin(BandColumn column)
        {
            ReleaseJoin(column);
            RemoveColumn(column);
        }

        private void ReleaseJoin(BandColumn column)
        {
            var (device, owned, created) = _joins[column];
            var player = column.Player;

            if (!owned)
            {
                player.DisableInputs();
                player.DeviceInfo.RemoveController(device);
            }

            DisposePlayer(player);

            if (created is not null)
            {
                PlayerContainer.RemoveProfile(created);
            }
        }

        private void DisposePlayer(YargPlayer player)
        {
            _suppressPlayerEvents = true;
            try
            {
                PlayerContainer.DisposePlayer(player);
            }
            finally
            {
                _suppressPlayerEvents = false;
            }
        }

        internal void Disconnect(BandColumn column)
        {
            if (column.IsJoining)
            {
                CancelJoin(column);
                return;
            }

            DisposePlayer(column.Player);
            RemoveColumn(column);
        }

        private void OnConfirm(NavigationContext context)
        {
            if (_started)
            {
                return;
            }

            var column = ColumnFor(context.Player);
            if (column is not null && !column.Expand())
            {
                column.Nav.ConfirmSelection();
            }
        }

        private void OnBack(NavigationContext context)
        {
            if (_started)
            {
                return;
            }

            var column = ColumnFor(context.Player);
            if (column is null || column.IsAtTopLevel)
            {
                MenuManager.Instance.PopMenu();
            }
            else
            {
                column.Back();
            }
        }

        private void OnNavigationEvent(NavigationContext context)
        {
            int direction = context.Action switch
            {
                MenuAction.Left  => -1,
                MenuAction.Right => 1,
                _                => 0,
            };

            bool movesFocus = direction != 0 && context.Player is null && _columns.Count > 0;
            if (!movesFocus)
            {
                return;
            }

            _focusIndex = Mathf.Clamp(_focusIndex + direction, 0, _columns.Count - 1);
            OnColumnChanged();
        }

        private bool InterceptOvershellInput(NavigationContext context)
        {
            if (_hidden || !_canEngage())
            {
                return false;
            }

            var column = FindColumn(context.Player);
            if (column is null)
            {
                return IsMenuExitAction(context.Action) && AnyColumnOpen();
            }

            if (context.Action == MenuAction.Start)
            {
                if (column.IsJoining)
                {
                    return true;
                }

                if (column.IsExpanded)
                {
                    column.Back();
                }
                else if (_canEngage())
                {
                    column.Expand();
                }

                return true;
            }

            if (!column.IsExpanded)
            {
                return IsMenuExitAction(context.Action) && AnyColumnOpen();
            }

            switch (context.Action)
            {
                case MenuAction.Up:
                    column.Nav.SelectPrevious(context.IsRepeat);
                    break;
                case MenuAction.Down:
                    column.Nav.SelectNext(context.IsRepeat);
                    break;
                case MenuAction.Green:
                    column.Nav.ConfirmSelection();
                    break;
                case MenuAction.Red:
                    column.Back();
                    break;
            }

            return true;
        }

        private static bool IsMenuExitAction(MenuAction action)
        {
            return action is MenuAction.Green or MenuAction.Red;
        }

        private bool AnyColumnOpen()
        {
            return _columns.Any(c => c.IsExpanded && !c.IsSittingOut && c.Player.DeviceInfo.HasControllerAssigned);
        }

        private BandColumn ColumnFor(YargPlayer player)
        {
            return player is null ? FocusedColumn : FindColumn(player);
        }

        private BandColumn FindColumn(YargPlayer player)
        {
            return player is null ? null : _columns.FirstOrDefault(c => c.Player == player);
        }

        private BandColumn FocusedColumn =>
            _columns.Count > 0 ? _columns[Mathf.Clamp(_focusIndex, 0, _columns.Count - 1)] : null;

        private bool OnUnclaimedDevicePressed(InputDevice device)
        {
            if (PlayerContainer.IsControllerTaken(device))
            {
                return false;
            }

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

        private void Join(InputDevice device)
        {
            if (IsFull || PlayerContainer.IsControllerTaken(device))
            {
                return;
            }

            var profile = PlayerContainer.GetProfileForDevice(device) ?? PlayerContainer.Profiles
                .Where(p => !p.IsBot && !PlayerContainer.IsProfileTaken(p))
                .OrderByDescending(p => p.LastUsed)
                .FirstOrDefault();

            YargProfile created = null;
            if (profile is null)
            {
                created = CreateProfileFor(device);
                profile = created;
            }

            var player = AttachPlayer(profile, device, out bool owned);
            if (player is null)
            {
                if (created is not null)
                {
                    PlayerContainer.RemoveProfile(created);
                }

                return;
            }

            var before = CaptureCardPositions();
            var column = InsertColumn(player, before);
            _joins[column] = (device, owned, created);
            column.BeginJoin();
            OnColumnChanged();
            ShiftCards(before, delay: SLIDE_SECONDS, riseDelay: SLIDE_SECONDS * 2f);

            LayoutRebuilder.ForceRebuildLayoutImmediate(column.Card);
            column.RiseFrom(_joinCard.rect.height - column.Card.rect.height);
        }

        private YargPlayer AttachPlayer(YargProfile profile, InputDevice device, out bool owned)
        {
            var deviceInfo = BindingsContainer.GetDeviceInfoForProfile(profile);
            owned = deviceInfo.MatchesController(device) || deviceInfo.ContainsController(device);

            YargPlayer player;
            _suppressPlayerEvents = true;
            try
            {
                player = PlayerContainer.CreatePlayerFromProfile(profile, resolveDevices: false);
            }
            finally
            {
                _suppressPlayerEvents = false;
            }

            if (player is null)
            {
                return null;
            }

            player.DisableInputs();
            player.DeviceInfo.AddController(device);
            player.DeviceInfo.ResolveMicrophones();
            player.EnableInputs();
            return player;
        }

        private static YargProfile CreateProfileFor(InputDevice device)
        {
            var family = LayoutHelper.InputDeviceToControllerFamily(device);
            var mode = NEW_PROFILE_MODES.FirstOrDefault(m =>
                BindingsContainer.GetBindingSetsForControllerInMode(family, m).Count > 0);

            var instrument = mode.PossibleInstruments()[0];
            var profile = new YargProfile
            {
                Name = ProfilesMenu.GetUniqueProfileName("New Profile"),
                NoteSpeed = 5,
                HighwayLength = 1,
                GameMode = mode,
                CurrentInstrument = instrument,
                PreferredInstrument = instrument,
            };

            PlayerContainer.AddProfile(profile);
            return profile;
        }

        private void OnPlayerAdded(YargPlayer player)
        {
            if (_suppressPlayerEvents || FindColumn(player) is not null)
            {
                return;
            }

            var before = CaptureCardPositions();
            var column = InsertColumn(player, before);
            column.RiseFrom(-SLIDE_DISTANCE);
            column.Open();
            OnColumnChanged();
            ShiftCards(before, delay: 0f, riseDelay: null);
        }

        private void OnPlayerRemoved(YargPlayer player)
        {
            if (_suppressPlayerEvents)
            {
                return;
            }

            var column = FindColumn(player);
            if (column is not null)
            {
                RemoveColumn(column);
            }
        }

        private BandColumn InsertColumn(YargPlayer player, Dictionary<RectTransform, float> before)
        {
            var column = Instantiate(_columnPrefab, _band);
            column.Initialize(this, player);

            var target = _joinSlots.OrderBy(slot => slot.transform.GetSiblingIndex()).FirstOrDefault();
            if (target is not null)
            {
                column.transform.SetSiblingIndex(target.transform.GetSiblingIndex());
                if (before is not null && before.Remove(JoinCard(target), out float position))
                {
                    before[column.Card] = position;
                }

                DestroyJoinSlot(target);
            }

            int siblingIndex = column.transform.GetSiblingIndex();
            int index = _columns.Count(c => c.transform.GetSiblingIndex() < siblingIndex);
            _columns.Insert(index, column);
            if (_columns.Count > 1 && index <= _focusIndex)
            {
                _focusIndex++;
            }

            RefreshProfilePickers(column);
            return column;
        }

        internal bool CanMoveColumn(BandColumn column, int direction)
        {
            return SlotBeside(column, direction) != null;
        }

        private Transform SlotBeside(BandColumn column, int direction)
        {
            var slots = OrderedSlots();
            int index = slots.IndexOf(column.transform);
            int target = index + direction;
            bool inRange = index >= 0 && target >= 0 && target < slots.Count;
            return inRange ? slots[target] : null;
        }

        internal bool MoveColumn(BandColumn column, int direction)
        {
            var other = SlotBeside(column, direction);
            if (other == null)
            {
                return false;
            }

            var focused = FocusedColumn;
            var before = CaptureCardPositions();

            int siblingIndex = column.transform.GetSiblingIndex();
            column.transform.SetSiblingIndex(other.GetSiblingIndex());
            other.SetSiblingIndex(siblingIndex);

            _columns.Sort((a, b) => a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));
            _focusIndex = _columns.IndexOf(focused);

            OnColumnChanged();
            PlayerContainer.SaveProfiles();
            ShiftCards(before, delay: 0f, riseDelay: null);

            foreach (var c in _columns)
            {
                if (c != column && c.State == BandColumnState.Position)
                {
                    c.Rebuild();
                }
            }

            return true;
        }

        private List<Transform> OrderedSlots()
        {
            var slots = new List<Transform>();
            foreach (Transform child in _band)
            {
                if (_columns.Any(c => c.transform == child) || _joinSlots.Any(s => s.transform == child))
                {
                    slots.Add(child);
                }
            }

            return slots;
        }

        private void RemoveColumn(BandColumn column)
        {
            var before = CaptureCardPositions();
            int siblingIndex = column.transform.GetSiblingIndex();

            _columns.Remove(column);
            _joins.Remove(column);
            _shifts.Remove(column.Card);
            column.DismissAndDestroy();

            if (_columns.Count + 1 < MIN_SLOTS)
            {
                CreateJoinSlot(siblingIndex);
            }

            RefreshProfilePickers(column);
            OnColumnChanged();
            ShiftCards(before, delay: SLIDE_SECONDS, riseDelay: SLIDE_SECONDS);
        }

        private Dictionary<RectTransform, float> CaptureCardPositions()
        {
            var positions = new Dictionary<RectTransform, float>();
            foreach (var column in _columns)
            {
                positions[column.Card] = CardPosition(column.Card);
            }

            foreach (var slot in _joinSlots)
            {
                var card = JoinCard(slot);
                positions[card] = CardPosition(card);
            }

            return positions;
        }

        private void ShiftCards(Dictionary<RectTransform, float> before, float delay, float? riseDelay)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(_band);

            foreach (var column in _columns)
            {
                ShiftCard(column.Card, before, delay);
            }

            foreach (var slot in _joinSlots)
            {
                var card = JoinCard(slot);
                if (before.ContainsKey(card))
                {
                    ShiftCard(card, before, delay);
                }
                else if (riseDelay is { } rise)
                {
                    card.DOKill();
                    card.anchoredPosition = new Vector2(0f, -SLIDE_DISTANCE);
                    card.DOAnchorPosY(0f, SLIDE_SECONDS)
                        .SetDelay(rise)
                        .SetEase(Ease.OutCubic)
                        .SetUpdate(true);
                }
            }
        }

        private void ShiftCard(RectTransform card, Dictionary<RectTransform, float> before, float delay)
        {
            if (_shifts.Remove(card, out var running))
            {
                running.Kill();
            }

            float offset = before.TryGetValue(card, out float position)
                ? position - SlotPosition(card)
                : 0f;

            bool settled = Mathf.Abs(offset) < 0.5f;
            card.anchoredPosition = new Vector2(settled ? 0f : offset, card.anchoredPosition.y);
            if (settled)
            {
                return;
            }

            _shifts[card] = card.DOAnchorPosX(0f, SLIDE_SECONDS)
                .SetDelay(delay)
                .SetEase(Ease.OutCubic)
                .SetUpdate(true);
        }

        private static float SlotPosition(RectTransform card)
        {
            return card.parent.localPosition.x;
        }

        private static float CardPosition(RectTransform card)
        {
            return SlotPosition(card) + card.anchoredPosition.x;
        }

        private void UpdateStatus()
        {
            if (IsOvershell)
            {
                return;
            }

            int ready = _columns.Count(c => c.IsDecided && !c.IsSittingOut);
            bool allDecided = _columns.Count > 0 && _columns.All(c => c.IsDecided);

            if (allDecided && ready == 0)
            {
                _footer.text = Localize.Key("Menu.DifficultySelect.Band.NobodyPlaying");
            }
            else
            {
                _footer.text = string.Empty;
            }

            _startPending = allDecided && ready > 0;

            if (!IsOvershell && _scheme != null &&
                _scheme.SetActionDisabled(MenuAction.Green, _columns.Count == 0) && HelpBar.Instance != null)
            {
                HelpBar.Instance.RefreshDisabledActions(_scheme);
            }
        }

        private void StartSong()
        {
            if (_started)
            {
                return;
            }

            _started = true;
            foreach (var column in _columns)
            {
                column.Player.SittingOut = column.IsSittingOut;
            }

            var players = _columns.Select(c => c.Player).ToList();
            var vocalLeader = players.FirstOrDefault(p => !p.SittingOut && p.Profile.GameMode == GameMode.Vocals);
            if (vocalLeader != null)
            {
                PlayerSelectionRules.ShareVocalModifiers(vocalLeader, players);
            }

            _startInputBlocker = Navigator.Instance.PushInputBlocker();
            foreach (var column in _columns)
            {
                column.SlideOut();
            }

            foreach (var slot in _joinSlots)
            {
                JoinCard(slot).DOAnchorPosY(-SLIDE_DISTANCE, SLIDE_SECONDS)
                    .SetEase(Ease.InCubic)
                    .SetUpdate(true)
                    .SetLink(slot.gameObject);
            }

            _startDelay = DOVirtual.DelayedCall(SLIDE_SECONDS, () =>
                {
                    _startDelay = null;
                    ReleaseStartInputBlocker();
                    _startSong();
                })
                .SetUpdate(true)
                .SetLink(gameObject);
        }

        private void ReleaseStartInputBlocker()
        {
            _startInputBlocker?.Dispose();
            _startInputBlocker = null;
        }

        private void UpdateInstruments()
        {
            var song = GlobalVariables.State.CurrentSong;

            MaxHarmonyIndex = song.VocalsCount;
            foreach (var showSong in _songs)
            {
                MaxHarmonyIndex = Mathf.Min(MaxHarmonyIndex, showSong.VocalsCount);
            }

            var instruments = new HashSet<Instrument>();
            foreach (var mode in EnumExtensions<GameMode>.Values)
            {
                if (mode == GameMode.Menu)
                {
                    continue;
                }

                foreach (var instrument in mode.PossibleInstrumentsForSong(song))
                {
                    instruments.Add(instrument);
                }
            }

            _instruments.Clear();
            foreach (var instrument in instruments.OrderBy(i => i))
            {
                if (_songs.All(s => PlayerSelectionRules.SongHasInstrument(s, instrument)))
                {
                    _instruments.Add(instrument);
                }
            }
        }

        private static List<GameMode> GetCandidateModes(YargProfile profile, Instrument instrument)
        {
            var song = GlobalVariables.State.CurrentSong;
            var candidates = new List<GameMode>();

            void TryAdd(GameMode mode)
            {
                if (!candidates.Contains(mode) && mode.PossibleInstrumentsForSong(song).Contains(instrument))
                {
                    candidates.Add(mode);
                }
            }

            TryAdd(profile.GameMode);
            TryAdd(instrument.ToNativeGameMode());
            foreach (var mode in EnumExtensions<GameMode>.Values)
            {
                if (mode != GameMode.Menu)
                {
                    TryAdd(mode);
                }
            }

            if (candidates.Count == 0)
            {
                candidates.Add(instrument.ToNativeGameMode());
            }

            return candidates;
        }

        internal float MaxCardHeight => _band.rect.height;

        private void ShowFooterBackground(bool show)
        {
            if (IsOvershell)
            {
                return;
            }

            _footerBackground.gameObject.SetActive(show);

            if (!show || HelpBar.Instance == null)
            {
                return;
            }

            var helpBar = (RectTransform) HelpBar.Instance.transform;
            _footerBackground.sizeDelta = new Vector2(_footerBackground.sizeDelta.x, helpBar.rect.height);
        }

        private float GetColumnWidth(int slots)
        {
            float spacing = _band.GetComponent<HorizontalLayoutGroup>().spacing;
            float available = _band.rect.width - spacing * (slots - 1);
            return Mathf.Min(MAX_COLUMN_WIDTH, available / slots);
        }
    }
}
