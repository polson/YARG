using System;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using YARG.Core.Game;
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
    public sealed class BandSelectMenu : MonoBehaviour
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
        private readonly List<InputDevice> _pendingJoins = new();
        private readonly List<BandColumnModel> _cancelJoinBuffer = new();
        private readonly List<BandColumnModel> _collapseBuffer = new();

        private BandInputRouter _input;
        private BandJoinSlots _joinSlots;
        private BandColumn _profilesInUse;
        private LayoutElement _profilesInUseSlot;
        private HorizontalLayoutGroup _bandLayout;
        private Action _startSong;
        private IDisposable _startInputBlocker;
        private Tween _startDelay;
        private Canvas _bandCanvas;
        private int _bandSortingOrder;
        private float _fittedBandHeight;
        private NavigationScheme _scheme;

        internal DifficultyRing RingTemplate { get; private set; }
        internal float MaxCardHeight => _band.rect.height;

        public bool IsOpen => _session.IsOpen;
        public bool IsOvershell => _session.IsOvershell;

        private void Awake()
        {
            _bandLayout = _band.GetComponent<HorizontalLayoutGroup>();
            _input = new BandInputRouter(_session, _columnViews);
            _joinSlots = new BandJoinSlots(_band, _joinSlot, _animator, slot => JoinWithoutDevice(slot));
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

            _scheme = _input.CreateBandScheme();
            _ = Navigator.Instance.PushScheme(_scheme);
            Navigator.Instance.NavigationEvent += _input.OnNavigationEvent;

            ListenForPlayers();
        }

        public void OpenOvershell(Func<bool> canEngage)
        {
            OpenBand(songs: null, isOvershell: true, canEngage: canEngage);
            Navigator.Instance.InputInterceptor = _input.InterceptOvershellInput;
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

            navigator.NavigationEvent -= _input.OnNavigationEvent;
            if (navigator.InputInterceptor == _input.InterceptOvershellInput)
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
            string joinText = _session.IsFull
                ? Localize.KeyFormat("Menu.DifficultySelect.Band.Full", BandSessionState.MAX_PLAYERS)
                : Localize.Key("Menu.DifficultySelect.Band.JoinPrompt");
            _joinSlots.UpdateCards(width, joinText);
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

            _joinSlots.SetCount(joinSlotTarget);

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
                    _joinSlots.Entries[joinIndex++].Slot.transform.SetAsLastSibling();
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

        private bool OnUnclaimedDevicePressed(InputDevice device)
        {
            if (_session.IsJoinBlocked)
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
            CaptureCardPositions();
            if (_session.TryStartJoinWithDevice(device, out var model, out int replacedSlotIndex))
            {
                FinishJoinView(model, replacedSlotIndex);
            }
        }

        private BandColumn JoinWithoutDevice(LayoutElement slot, YargProfile chosen = null)
        {
            if (_profilesInUse != null || !_joinSlots.Contains(slot))
            {
                return null;
            }

            int slotIndex = slot.transform.GetSiblingIndex();
            CaptureCardPositions();
            if (!_session.TryStartJoinWithoutDevice(slotIndex, chosen, out var model, out bool needsProfilePicker,
                    out int replacedSlotIndex))
            {
                if (needsProfilePicker)
                {
                    ShowProfilesInUse(slot);
                }

                return null;
            }

            return FinishJoinView(model, replacedSlotIndex);
        }

        private BandColumn FinishJoinView(BandColumnModel model, int replacedSlotIndex)
        {
            var column = CreateAndRemapColumnView(model, replacedSlotIndex);
            column.BeginJoin();
            _session.SyncColumnsAndSlots();
            _animator.ShiftCards(_band, _columns, _joinSlots.Entries, _cardSnapshotBuffer, delay: SLIDE_SECONDS,
                riseDelay: SLIDE_SECONDS * 2f);

            LayoutRebuilder.ForceRebuildLayoutImmediate(column.Card);
            column.RiseFrom(_joinCard.rect.height - column.Card.rect.height);
            return column;
        }

        internal void ChooseJoinProfile(BandColumn column, YargProfile profile)
        {
            if (column == _profilesInUse)
            {
                ChooseProfileForPendingSlot(profile);
                return;
            }

            CaptureCardPositions();
            if (!_session.ChooseJoinProfile(column.Model, profile))
            {
                RemoveColumnView(column);
                return;
            }

            column.CompleteJoin();
            _animator.ShiftCards(_band, _columns, _joinSlots.Entries, _cardSnapshotBuffer, delay: 0f,
                riseDelay: SLIDE_SECONDS);
            RefreshProfilePickers(column);
        }

        private void ChooseProfileForPendingSlot(YargProfile profile)
        {
            var slot = _profilesInUseSlot;
            CloseProfilesInUse();
            var joined = JoinWithoutDevice(slot, profile);
            if (joined != null)
            {
                ChooseJoinProfile(joined, profile);
            }
        }

        private void RefreshProfilePickers(BandColumn changed)
        {
            if (_profilesInUse != null)
            {
                _profilesInUse.Rebuild();
            }

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
            CaptureCardPositions();
            _session.CancelJoin(column.Model);
            RemoveColumnView(column);
        }

        internal void Disconnect(BandColumn column)
        {
            CaptureCardPositions();
            _session.Disconnect(column.Model);
            RemoveColumnView(column);
        }

        private void ShowProfilesInUse(LayoutElement slot)
        {
            var column = Instantiate(_columnPrefab, slot.transform);
            var rect = (RectTransform) column.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            column.Initialize(this, _session, new BandColumnModel());
            column.SetWidth(slot.preferredWidth);
            column.ShowProfilesInUse();

            _profilesInUse = column;
            _profilesInUseSlot = slot;

            LayoutRebuilder.ForceRebuildLayoutImmediate(column.Card);
            column.RiseFrom(_joinCard.rect.height - column.Card.rect.height);
        }

        internal void CloseProfilesInUse()
        {
            if (_profilesInUse != null)
            {
                _profilesInUse.DismissAndDestroy();
            }

            _profilesInUse = null;
            _profilesInUseSlot = null;
        }

        private void OnPlayerAdded(YargPlayer player)
        {
            if (_session.IsSuppressingPlayerEvents || _session.FindColumn(player) != null)
            {
                return;
            }

            CaptureCardPositions();
            var model = _session.HandleExternalPlayerAdded(player, out int replacedSlotIndex);
            var column = CreateAndRemapColumnView(model, replacedSlotIndex);
            column.RiseFrom(-SLIDE_DISTANCE);
            column.Open();
            _session.SyncColumnsAndSlots();
            _animator.ShiftCards(_band, _columns, _joinSlots.Entries, _cardSnapshotBuffer, delay: 0f, riseDelay: null);
        }

        private void OnPlayerRemoved(YargPlayer player)
        {
            CaptureCardPositions();
            _session.HandleExternalPlayerRemoved(player, out var removedModel);
            if (removedModel != null && _columnViews.TryGetValue(removedModel, out var column))
            {
                RemoveColumnView(column);
            }
        }

        private BandColumn CreateColumnView(BandColumnModel model)
        {
            var column = Instantiate(_columnPrefab, _band);
            column.Initialize(this, _session, model);
            _columnViews[model] = column;
            RefreshProfilePickers(column);
            return column;
        }

        private BandColumn CreateAndRemapColumnView(BandColumnModel model, int replacedSlotIndex)
        {
            var column = CreateColumnView(model);
            if (replacedSlotIndex >= 0)
            {
                var target = _joinSlots.FindAtSiblingIndex(replacedSlotIndex);
                if (target != null)
                {
                    column.transform.SetSiblingIndex(replacedSlotIndex);
                    _animator.RemapCard(_cardSnapshotBuffer, BandJoinSlots.CardOf(target), column.Card);
                    _joinSlots.Destroy(target);
                }
            }

            return column;
        }

        internal bool MoveColumn(BandColumn column, int direction)
        {
            CaptureCardPositions();
            if (!_session.MoveColumn(column.Model, direction))
            {
                return false;
            }

            _animator.ShiftCards(_band, _columns, _joinSlots.Entries, _cardSnapshotBuffer, delay: 0f, riseDelay: null);
            return true;
        }

        private void RemoveColumnView(BandColumn column)
        {
            _columns.Remove(column);
            _columnViews.Remove(column.Model);
            _animator.KillTweens(column.Card);
            column.DismissAndDestroy();

            RefreshProfilePickers(column);
            _animator.ShiftCards(_band, _columns, _joinSlots.Entries, _cardSnapshotBuffer, delay: SLIDE_SECONDS,
                riseDelay: SLIDE_SECONDS);
        }

        private void CaptureCardPositions()
        {
            _animator.Capture(_columns, _joinSlots.Entries, _cardSnapshotBuffer);
        }

        private void RefitOnBandResize()
        {
            float height = MaxCardHeight;
            if (Mathf.Approximately(height, _fittedBandHeight))
            {
                return;
            }

            _fittedBandHeight = height;
            for (int i = 0; i < _columns.Count; i++)
            {
                _columns[i].FitRows();
            }

            if (_profilesInUse != null)
            {
                _profilesInUse.FitRows();
            }
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

            bool raise = false;
            if (_session.IsOvershell)
            {
                raise = _profilesInUse != null;
                for (int i = 0; !raise && i < _columns.Count; i++)
                {
                    if (_columns[i].IsExpanded)
                    {
                        raise = true;
                    }
                }
            }

            _bandCanvas.sortingOrder = raise ? OPEN_CARD_SORTING_ORDER : _bandSortingOrder;
        }

        private void ShowFooterBackground(bool show)
        {
            if (_session.IsOvershell)
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
            float spacing = _bandLayout != null ? _bandLayout.spacing : 0f;
            float available = _band.rect.width - spacing * (slots - 1);
            return Mathf.Min(MAX_COLUMN_WIDTH, available / slots);
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

            _animator.SlideOutJoinCards(_joinSlots.Entries);

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
