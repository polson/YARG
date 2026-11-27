using System;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Game;
using YARG.Core.Song;
using YARG.Gameplay.Visuals;
using YARG.Input;
using YARG.Localization;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    public sealed partial class BandSelectMenu : MonoBehaviour
    {
        internal static readonly Color READY_COLOR = new Color32(0x00, 0xD3, 0xFF, 0xFF);

        private const int MAX_PLAYERS = HighwayCameraRendering.MAX_MATRICES;
        private const int MIN_SLOTS = 4;

        private const float MAX_COLUMN_WIDTH = 460f;
        internal const float SLIDE_DISTANCE = 900f;
        internal const float JOIN_STRIP_HEIGHT = 72f;
        internal const float SLIDE_SECONDS = 0.3f;
        private const int OPEN_CARD_SORTING_ORDER = 107;

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

        private static readonly BandSeatTracker _seatTracker = new();

        private readonly BandCardAnimator _animator = new();
        private readonly BandPlayerJoiner _joiner = new();
        private readonly List<BandColumn> _columns = new();
        private readonly List<Instrument> _instruments = new();
        private readonly List<GameMode> _modeBuffer = new();
        private readonly List<Instrument> _otherInstrumentBuffer = new();
        private readonly List<Transform> _slotOrderBuffer = new();
        private readonly Dictionary<RectTransform, (float Position, float Width)> _cardSnapshotBuffer = new();
        private readonly List<JoinSlotEntry> _joinSlots = new();
        private readonly List<InputDevice> _pendingJoins = new();

        private BandColumn _profilesInUse;
        private LayoutElement _profilesInUseSlot;
        private HorizontalLayoutGroup _bandLayout;
        private List<SongEntry> _songs;
        private Action _startSong;
        private int _focusIndex;
        private bool _startPending;
        private bool _started;
        private IDisposable _startInputBlocker;
        private Tween _startDelay;
        private Canvas _bandCanvas;
        private int _bandSortingOrder;
        private float _fittedBandHeight;
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

        internal bool CanClickCards => !_started && !DialogManager.Instance.IsDialogShowing &&
            (!IsOvershell || (!_hidden && _canEngage()));

        private bool IsJoinBlocked => _started || DialogManager.Instance.IsDialogShowing || IsFull ||
            (IsOvershell && (_hidden || !_canEngage()));

        private void Awake()
        {
            _bandLayout = _band.GetComponent<HorizontalLayoutGroup>();
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
                    ctx =>
                    {
                        var col = ColumnFor(ctx.Player);
                        if (col != null)
                        {
                            col.NavigateVertical(-1, ctx.IsRepeat);
                        }
                    }),
                new NavigationScheme.Entry(MenuAction.Down, "Menu.Common.Down",
                    ctx =>
                    {
                        var col = ColumnFor(ctx.Player);
                        if (col != null)
                        {
                            col.NavigateVertical(1, ctx.IsRepeat);
                        }
                    }),
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

            var seats = _seatTracker.Snapshot();
            foreach (var player in PlayerContainer.Players)
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
            var joinSlotTransforms = new List<Transform>(_joinSlots.Count);
            foreach (var entry in _joinSlots)
            {
                joinSlotTransforms.Add(entry.Slot.transform);
            }

            joinSlotTransforms.Sort((a, b) => a.GetSiblingIndex().CompareTo(b.GetSiblingIndex()));
            _seatTracker.Apply(seats, _columns, joinSlotTransforms);
        }

        private void ListenForPlayers()
        {
            InputManager.UnclaimedDevicePressed = OnUnclaimedDevicePressed;
            PlayerContainer.PlayerAdded += OnPlayerAdded;
            PlayerContainer.PlayerRemoved += OnPlayerRemoved;
        }

        public void SetOvershellHidden(bool hidden)
        {
            if (!IsOpen || !IsOvershell || hidden == _hidden)
            {
                return;
            }

            _hidden = hidden;
            if (hidden)
            {
                for (int i = _columns.Count - 1; i >= 0; i--)
                {
                    var column = _columns[i];
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

            foreach (var column in _joiner.SnapshotJoiningColumns())
            {
                CancelJoin(column);
            }

            foreach (var column in _columns)
            {
                Destroy(column.gameObject);
            }

            _columns.Clear();
            if (_profilesInUse != null)
            {
                Destroy(_profilesInUse.gameObject);
            }

            _profilesInUse = null;
            _profilesInUseSlot = null;

            _band.gameObject.SetActive(false);
            _footer.gameObject.SetActive(false);
            ShowFooterBackground(false);

            if (!IsOvershell)
            {
                Navigator.Instance.PopScheme();
            }
        }

        private void OnDisable()
        {
            Unsubscribe();
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
                for (int i = 0; i < _pendingJoins.Count; i++)
                {
                    if (!IsJoinBlocked)
                    {
                        Join(_pendingJoins[i]);
                    }
                }

                _pendingJoins.Clear();
            }

            if (_startPending)
            {
                _startPending = false;
                StartSong();
            }

            RefitOnBandResize();
            UpdateSortingOrder();
        }

        internal void OnColumnChanged()
        {
            if (!IsOpen)
            {
                return;
            }

            bool anyJoining = SyncPlayerOrder();
            int slots = UpdateJoinSlotLayout(anyJoining);
            _seatTracker.Record(_columns, OrderedSlots());

            float width = GetColumnWidth(slots);
            UpdateJoinSlotCards(width);
            ApplyColumnWidthsAndFocus(width);
            UpdateStatus();
        }

        private bool SyncPlayerOrder()
        {
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
            return anyJoining;
        }

        private int UpdateJoinSlotLayout(bool anyJoining)
        {
            int spare = anyJoining ? 0 : 1;
            int slots = Mathf.Max(MIN_SLOTS, _columns.Count + spare);
            SetJoinSlotCount(slots - _columns.Count);
            return slots;
        }

        private void UpdateJoinSlotCards(float width)
        {
            string joinText = IsFull
                ? Localize.KeyFormat("Menu.DifficultySelect.Band.Full", MAX_PLAYERS)
                : Localize.Key("Menu.DifficultySelect.Band.JoinPrompt");

            foreach (var entry in _joinSlots)
            {
                if (!Mathf.Approximately(entry.Slot.preferredWidth, width))
                {
                    entry.Slot.preferredWidth = width;
                    entry.Card.sizeDelta = new Vector2(width, entry.Card.sizeDelta.y);
                }

                entry.Label.text = joinText;
            }
        }

        private void ApplyColumnWidthsAndFocus(float width)
        {
            if (_profilesInUse != null)
            {
                _profilesInUse.SetWidth(width);
            }

            _focusIndex = Mathf.Clamp(_focusIndex, 0, Mathf.Max(0, _columns.Count - 1));
            bool hasKeyboard = PlayerContainer.HasConnectedKeyboardProfile();
            for (int i = 0; i < _columns.Count; i++)
            {
                _columns[i].SetWidth(width);
                _columns[i].SetFocused(IsOvershell || (i == _focusIndex && !hasKeyboard));
            }
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
            _otherInstrumentBuffer.Clear();
            foreach (var other in _columns)
            {
                if (other != column && other.HasChosenInstrument)
                {
                    _otherInstrumentBuffer.Add(other.Player.Profile.CurrentInstrument);
                }
            }

            return PlayerSelectionRules.GetInstrumentBlocker(GlobalVariables.State.CurrentSong, column.Player,
                instrument, _otherInstrumentBuffer, out mode, _modeBuffer);
        }

        private void UpdateStatus()
        {
            if (IsOvershell)
            {
                return;
            }

            int ready = 0;
            bool allDecided = _columns.Count > 0;
            foreach (var column in _columns)
            {
                if (!column.IsDecided)
                {
                    allDecided = false;
                }
                else if (!column.IsSittingOut)
                {
                    ready++;
                }
            }

            _footer.text = allDecided && ready == 0
                ? Localize.Key("Menu.DifficultySelect.Band.NobodyPlaying")
                : string.Empty;

            _startPending = allDecided && ready > 0;

            if (_scheme != null &&
                _scheme.SetActionDisabled(MenuAction.Green, _columns.Count == 0) &&
                HelpBar.Instance != null)
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
            var players = new List<YargPlayer>(_columns.Count);
            YargPlayer vocalLeader = null;
            foreach (var column in _columns)
            {
                column.Player.SittingOut = column.IsSittingOut;
                players.Add(column.Player);
                if (vocalLeader == null && !column.Player.SittingOut &&
                    column.Player.Profile.GameMode == GameMode.Vocals)
                {
                    vocalLeader = column.Player;
                }
            }

            if (vocalLeader != null)
            {
                PlayerSelectionRules.ShareVocalModifiers(vocalLeader, players);
            }

            _startInputBlocker = Navigator.Instance.PushInputBlocker();
            foreach (var column in _columns)
            {
                column.SlideOut();
            }

            _animator.SlideOutJoinCards(_joinSlots);

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
            MaxHarmonyIndex = PlayerSelectionRules.GetMaxHarmonyIndex(song, _songs);
            PlayerSelectionRules.CollectSongInstruments(song, _songs, _instruments);
        }
    }
}
