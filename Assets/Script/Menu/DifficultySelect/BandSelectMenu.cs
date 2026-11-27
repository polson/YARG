using System;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using YARG.Core.Input;
using YARG.Core.Song;
using YARG.Gameplay.HUD;
using YARG.Input;
using YARG.Localization;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    public sealed partial class BandSelectMenu : MonoBehaviour
    {
        internal static readonly Color READY_COLOR = new Color32(0x00, 0xD3, 0xFF, 0xFF);

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

        private readonly BandSessionState _session = new();
        private readonly BandCardAnimator _animator = new();
        private readonly Dictionary<BandColumnModel, BandColumn> _columnViews = new();
        private readonly List<BandColumn> _columns = new();
        private readonly Dictionary<RectTransform, (float Position, float Width)> _cardSnapshotBuffer = new();
        private readonly List<JoinSlotEntry> _joinSlots = new();
        private readonly List<InputDevice> _pendingJoins = new();
        private readonly List<BandColumnModel> _cancelJoinBuffer = new();
        private readonly List<BandColumnModel> _collapseBuffer = new();

        private BandColumn _profilesInUse;
        private LayoutElement _profilesInUseSlot;
        private HorizontalLayoutGroup _bandLayout;
        private Action _startSong;
        private IDisposable _startInputBlocker;
        private Tween _startDelay;
        private Canvas _bandCanvas;
        private int _bandSortingOrder;
        private float _fittedBandHeight;
        private Func<bool> _canEngage;
        private NavigationScheme _scheme;

        internal DifficultyRing RingTemplate { get; private set; }

        public bool IsOpen => _session.IsOpen;
        public bool IsOvershell => _session.IsOvershell;

        private void Awake()
        {
            _bandLayout = _band.GetComponent<HorizontalLayoutGroup>();
            _session.ColumnsChanged += OnSessionColumnsChanged;
            _session.ColumnRebuildRequested += OnSessionColumnRebuildRequested;
            _session.ColumnExpanded += OnSessionColumnExpanded;
            _session.ColumnReadyChanged += OnSessionColumnReadyChanged;
        }

        public void Open(List<SongEntry> songs, DifficultyRing ringTemplate, Action startSong)
        {
            RingTemplate = ringTemplate;
            _startSong = startSong;

            OpenBand(songs, isOvershell: false, canEngage: null);

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
        }

        public void OpenOvershell(Func<bool> canEngage)
        {
            _canEngage = canEngage;
            OpenBand(songs: null, isOvershell: true, canEngage: canEngage);
            Navigator.Instance.InputInterceptor = InterceptOvershellInput;
            ListenForPlayers();
        }

        private void OpenBand(List<SongEntry> songs, bool isOvershell, Func<bool> canEngage)
        {
            _joinSlot.gameObject.SetActive(false);
            _band.gameObject.SetActive(true);
            _footer.gameObject.SetActive(!isOvershell);
            ShowFooterBackground(true);

            _session.Open(songs, isOvershell, canEngage);
        }

        private void ListenForPlayers()
        {
            InputManager.UnclaimedDevicePressed = OnUnclaimedDevicePressed;
            PlayerContainer.PlayerAdded += OnPlayerAdded;
            PlayerContainer.PlayerRemoved += OnPlayerRemoved;
        }

        public void SetOvershellHidden(bool hidden)
        {
            _cancelJoinBuffer.Clear();
            _collapseBuffer.Clear();
            if (!_session.SetOvershellHidden(hidden, _cancelJoinBuffer, _collapseBuffer))
            {
                return;
            }

            foreach (var model in _cancelJoinBuffer)
            {
                if (_columnViews.TryGetValue(model, out var column))
                {
                    CancelJoin(column);
                }
            }

            foreach (var model in _collapseBuffer)
            {
                if (_columnViews.TryGetValue(model, out var column))
                {
                    column.Rebuild();
                }
            }

            _band.gameObject.SetActive(!hidden);
            ShowFooterBackground(!hidden);
        }

        public void Close()
        {
            if (!_session.IsOpen)
            {
                return;
            }

            bool wasOvershell = _session.IsOvershell;
            _pendingJoins.Clear();
            _startDelay?.Kill();
            _startDelay = null;
            ReleaseStartInputBlocker();
            Unsubscribe();

            _session.Close();

            foreach (var column in _columns)
            {
                Destroy(column.gameObject);
            }

            _columns.Clear();
            _columnViews.Clear();
            if (_profilesInUse != null)
            {
                Destroy(_profilesInUse.gameObject);
            }

            _profilesInUse = null;
            _profilesInUseSlot = null;

            _band.gameObject.SetActive(false);
            _footer.gameObject.SetActive(false);
            ShowFooterBackground(false);

            if (!wasOvershell)
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
            _session.ColumnsChanged -= OnSessionColumnsChanged;
            _session.ColumnRebuildRequested -= OnSessionColumnRebuildRequested;
            _session.ColumnExpanded -= OnSessionColumnExpanded;
            _session.ColumnReadyChanged -= OnSessionColumnReadyChanged;
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
            if (!_session.IsOpen)
            {
                return;
            }

            if (_pendingJoins.Count > 0)
            {
                for (int i = 0; i < _pendingJoins.Count; i++)
                {
                    if (!_session.IsJoinBlocked)
                    {
                        Join(_pendingJoins[i]);
                    }
                }

                _pendingJoins.Clear();
            }

            if (_session.StartPending)
            {
                _session.ConsumeStartPending();
                StartSong();
            }

            RefitOnBandResize();
            UpdateSortingOrder();
        }

        private void OnSessionColumnsChanged()
        {
            if (!_session.IsOpen)
            {
                return;
            }

            SyncViewsWithSession();

            float width = GetColumnWidth(_session.SlotAssignments.Count);
            UpdateJoinSlotCards(width);
            ApplyColumnWidthsAndFocus(width);
            UpdateStatus();
        }

        private void OnSessionColumnRebuildRequested(BandColumnModel model)
        {
            if (_columnViews.TryGetValue(model, out var column))
            {
                column.Rebuild();
            }
        }

        private void OnSessionColumnExpanded(BandColumnModel model)
        {
            if (_columnViews.TryGetValue(model, out var column))
            {
                column.RebuildFromCurrentTop();
            }
        }

        private void OnSessionColumnReadyChanged(BandColumnModel model)
        {
            if (_columnViews.TryGetValue(model, out var column))
            {
                column.ApplyReadyVisuals();
            }
        }

        private void SyncViewsWithSession()
        {
            int joinSlotTarget = 0;
            foreach (var player in _session.SlotAssignments)
            {
                if (player == null)
                {
                    joinSlotTarget++;
                }
            }

            SetJoinSlotCount(joinSlotTarget);

            foreach (var model in _session.Columns)
            {
                if (!_columnViews.ContainsKey(model))
                {
                    var column = CreateColumnView(model);
                    if (!_session.IsOvershell)
                    {
                        column.RiseFrom(-SLIDE_DISTANCE);
                    }

                    column.Open();
                }
            }

            _columns.Clear();
            int joinIndex = 0;
            foreach (var player in _session.SlotAssignments)
            {
                if (player == null)
                {
                    _joinSlots[joinIndex++].Slot.transform.SetAsLastSibling();
                }
                else
                {
                    var model = _session.FindColumn(player);
                    if (model != null && _columnViews.TryGetValue(model, out var column))
                    {
                        column.transform.SetAsLastSibling();
                        _columns.Add(column);
                    }
                }
            }
        }

        private void UpdateJoinSlotCards(float width)
        {
            string joinText = _session.IsFull
                ? Localize.KeyFormat("Menu.DifficultySelect.Band.Full", BandSessionState.MAX_PLAYERS)
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

            bool hasKeyboard = PlayerContainer.HasConnectedKeyboardProfile();
            for (int i = 0; i < _columns.Count; i++)
            {
                _columns[i].SetWidth(width);
                _columns[i].SetFocused(_session.IsOvershell || (i == _session.FocusIndex && !hasKeyboard));
            }
        }

        private void UpdateStatus()
        {
            if (_session.IsOvershell)
            {
                return;
            }

            _footer.text = _session.FooterText;

            if (_scheme != null &&
                _scheme.SetActionDisabled(MenuAction.Green, _session.GreenDisabled) &&
                HelpBar.Instance != null)
            {
                HelpBar.Instance.RefreshDisabledActions(_scheme);
            }
        }

        private void StartSong()
        {
            if (_session.Started)
            {
                return;
            }

            _session.MarkStarted();
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
    }
}
