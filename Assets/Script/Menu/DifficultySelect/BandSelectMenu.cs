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
    /// <summary>
    /// Coordinates the band strip: takes controller input, tells each card what to do, manages the join slots,
    /// and slides cards when players join, leave, or move. Who is in the band lives in
    /// <see cref="BandSessionState"/>; what each card shows is decided by its <see cref="BandCardController"/>.
    /// </summary>
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

        private readonly struct JoinSlotEntry
        {
            public readonly LayoutElement Slot;
            public readonly RectTransform Card;
            public readonly TextMeshProUGUI Label;

            public JoinSlotEntry(LayoutElement slot, RectTransform card, TextMeshProUGUI label)
            {
                Slot = slot;
                Card = card;
                Label = label;
            }
        }

        private readonly struct CardSnapshot
        {
            public readonly float Position;
            public readonly float Width;

            public CardSnapshot(float position, float width)
            {
                Position = position;
                Width = width;
            }
        }

        private readonly BandSessionState _session = new();
        private readonly Dictionary<BandColumnModel, BandCardController> _cardsByModel = new();
        private readonly List<BandCardController> _cards = new();
        private readonly List<JoinSlotEntry> _joinSlots = new();
        private readonly Dictionary<RectTransform, CardSnapshot> _cardSnapshotBuffer = new();
        private readonly Dictionary<RectTransform, Tween> _shifts = new();
        private readonly Dictionary<RectTransform, Tween> _resizes = new();
        private readonly List<InputDevice> _pendingJoins = new();
        private readonly List<BandColumnModel> _cancelJoinBuffer = new();
        private readonly List<BandColumnModel> _collapseBuffer = new();

        private BandCardController _profilesInUse;
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

            _scheme = CreateBandScheme();
            _ = Navigator.Instance.PushScheme(_scheme);
            Navigator.Instance.NavigationEvent += OnNavigationEvent;

            ListenForPlayers();
        }

        public void OpenOvershell(Func<bool> canEngage)
        {
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
                if (_cardsByModel.TryGetValue(model, out var card))
                {
                    CancelJoin(card);
                }
            }

            foreach (var model in _collapseBuffer)
            {
                if (_cardsByModel.TryGetValue(model, out var card))
                {
                    card.Rebuild();
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
            CancelStartDelay();
            Unsubscribe();

            _session.Close();

            KillAllCardTweens();
            SetJoinSlotCount(0);

            foreach (var card in _cards)
            {
                Destroy(card.View.gameObject);
            }

            _cards.Clear();
            _cardsByModel.Clear();
            if (_profilesInUse != null)
            {
                Destroy(_profilesInUse.View.gameObject);
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

        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            CancelStartDelay();
            Unsubscribe();
        }

        private void OnDestroy()
        {
            _session.ColumnsChanged -= OnSessionColumnsChanged;
            _session.ColumnRebuildRequested -= OnSessionColumnRebuildRequested;
            _session.ColumnExpanded -= OnSessionColumnExpanded;
            _session.ColumnReadyChanged -= OnSessionColumnReadyChanged;
            CancelStartDelay();
            Unsubscribe();
        }

        private void Subscribe()
        {
            if (!_session.IsOpen)
            {
                return;
            }

            ListenForPlayers();

            var navigator = Navigator.Instance;
            if (navigator == null)
            {
                return;
            }

            if (_session.IsOvershell)
            {
                navigator.InputInterceptor = InterceptOvershellInput;
            }
            else
            {
                navigator.NavigationEvent -= OnNavigationEvent;
                navigator.NavigationEvent += OnNavigationEvent;
            }
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
                for (int pendingIndex = 0; pendingIndex < _pendingJoins.Count; pendingIndex++)
                {
                    if (!_session.IsJoinBlocked)
                    {
                        Join(_pendingJoins[pendingIndex]);
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

            SyncCardsWithSession();

            float width = GetColumnWidth(_session.SlotAssignments.Count);
            string joinText = _session.IsFull
                ? Localize.KeyFormat("Menu.DifficultySelect.Band.Full", BandSessionState.MAX_PLAYERS)
                : Localize.Key("Menu.DifficultySelect.Band.JoinPrompt");
            UpdateJoinCards(width, joinText);
            ApplyColumnWidthsAndFocus(width);
            UpdateStatus();
        }

        private void OnSessionColumnRebuildRequested(BandColumnModel model)
        {
            if (_cardsByModel.TryGetValue(model, out var card))
            {
                card.Rebuild();
            }
        }

        private void OnSessionColumnExpanded(BandColumnModel model)
        {
            if (_cardsByModel.TryGetValue(model, out var card))
            {
                card.RebuildFromCurrentTop();
            }
        }

        private void OnSessionColumnReadyChanged(BandColumnModel model)
        {
            if (_cardsByModel.TryGetValue(model, out var card))
            {
                card.ApplyReadyVisuals();
            }
        }

        private void SyncCardsWithSession()
        {
            int joinSlotCount = 0;
            foreach (var player in _session.SlotAssignments)
            {
                if (player == null)
                {
                    joinSlotCount++;
                }
            }

            SetJoinSlotCount(joinSlotCount);

            foreach (var model in _session.Columns)
            {
                if (!_cardsByModel.ContainsKey(model))
                {
                    var card = CreateCard(model);
                    if (!_session.IsOvershell)
                    {
                        card.View.RiseFrom(-SLIDE_DISTANCE);
                    }

                    card.Rebuild();
                }
            }

            _cards.Clear();
            int joinSlotIndex = 0;
            foreach (var player in _session.SlotAssignments)
            {
                if (player == null)
                {
                    _joinSlots[joinSlotIndex++].Slot.transform.SetAsLastSibling();
                    continue;
                }

                var model = _session.FindColumn(player);
                if (model != null && _cardsByModel.TryGetValue(model, out var card))
                {
                    card.View.transform.SetAsLastSibling();
                    _cards.Add(card);
                }
            }
        }

        private void ApplyColumnWidthsAndFocus(float width)
        {
            if (_profilesInUse != null)
            {
                _profilesInUse.View.SetWidth(width);
            }

            bool hasKeyboard = PlayerContainer.HasConnectedKeyboardProfile();
            for (int cardIndex = 0; cardIndex < _cards.Count; cardIndex++)
            {
                bool isFocused = _session.IsOvershell || (cardIndex == _session.FocusIndex && !hasKeyboard);
                _cards[cardIndex].View.SetWidth(width);
                _cards[cardIndex].View.SetFocused(isFocused);
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

        private NavigationScheme CreateBandScheme()
        {
            return new NavigationScheme(new()
            {
                new NavigationScheme.Entry(MenuAction.Up, "Menu.Common.Up",
                    ctx => CardFor(ctx.Player)?.NavigateVertical(-1, ctx.IsRepeat)),
                new NavigationScheme.Entry(MenuAction.Down, "Menu.Common.Down",
                    ctx => CardFor(ctx.Player)?.NavigateVertical(1, ctx.IsRepeat)),
                new NavigationScheme.Entry(MenuAction.Green, "Menu.Common.Confirm", OnConfirm),
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", OnBack),
            }, allowsMusicPlayer: false);
        }

        private void OnConfirm(NavigationContext context)
        {
            if (_session.IsStartingSong)
            {
                return;
            }

            var card = CardFor(context.Player);
            if (card != null && !card.Expand())
            {
                card.Confirm();
            }
        }

        private void OnBack(NavigationContext context)
        {
            if (_session.IsStartingSong)
            {
                return;
            }

            var card = CardFor(context.Player);
            if (card == null || card.IsAtTopLevel)
            {
                MenuManager.Instance.PopMenu();
            }
            else
            {
                card.Back();
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

            if (direction != 0 && !_session.IsStartingSong)
            {
                var card = CardFor(context.Player);
                if (card != null && card.AdjustSelection(direction))
                {
                    return;
                }
            }

            if (direction == 0 || context.Player != null || _cardsByModel.Count == 0)
            {
                return;
            }

            _session.MoveFocus(direction);
        }

        private bool InterceptOvershellInput(NavigationContext context)
        {
            if (_session.Hidden || !_session.CanEngage)
            {
                return false;
            }

            var card = FindCard(context.Player);
            if (card == null)
            {
                return IsMenuExitAction(context.Action) && _session.AnyOvershellColumnOpen();
            }

            if (context.Action == MenuAction.Start)
            {
                return HandleOvershellStart(card);
            }

            if (!card.IsExpanded)
            {
                return IsMenuExitAction(context.Action) && _session.AnyOvershellColumnOpen();
            }

            DispatchOvershellAction(card, context);
            return true;
        }

        private bool HandleOvershellStart(BandCardController card)
        {
            if (card.IsJoining)
            {
                return true;
            }

            if (card.IsExpanded)
            {
                card.Back();
            }
            else if (_session.CanEngage)
            {
                card.Expand();
            }

            return true;
        }

        private static void DispatchOvershellAction(BandCardController card, NavigationContext context)
        {
            switch (context.Action)
            {
                case MenuAction.Up:
                    card.NavigateVertical(-1, context.IsRepeat);
                    break;
                case MenuAction.Down:
                    card.NavigateVertical(1, context.IsRepeat);
                    break;
                case MenuAction.Green:
                    card.Confirm();
                    break;
                case MenuAction.Red:
                    card.Back();
                    break;
                case MenuAction.Left:
                    card.AdjustSelection(-1);
                    break;
                case MenuAction.Right:
                    card.AdjustSelection(1);
                    break;
            }
        }

        private static bool IsMenuExitAction(MenuAction action)
        {
            return action is MenuAction.Green or MenuAction.Red;
        }

        private BandCardController CardFor(YargPlayer player)
        {
            var model = _session.ColumnFor(player);
            return model != null && _cardsByModel.TryGetValue(model, out var card) ? card : null;
        }

        private BandCardController FindCard(YargPlayer player)
        {
            var model = _session.FindColumn(player);
            return model != null && _cardsByModel.TryGetValue(model, out var card) ? card : null;
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
                FinishJoin(model, replacedSlotIndex);
            }
        }

        private BandCardController JoinWithoutDevice(LayoutElement slot, YargProfile chosen = null)
        {
            if (_profilesInUse != null || JoinSlotIndexOf(slot) < 0)
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

            return FinishJoin(model, replacedSlotIndex);
        }

        private BandCardController FinishJoin(BandColumnModel model, int replacedSlotIndex)
        {
            var card = CreateCardInSlot(model, replacedSlotIndex);
            card.Rebuild();
            _session.SyncColumnsAndSlots();
            ShiftCards(delay: SLIDE_SECONDS, riseDelay: SLIDE_SECONDS * 2f);

            LayoutRebuilder.ForceRebuildLayoutImmediate(card.View.Card);
            card.View.RiseFrom(_joinCard.rect.height - card.View.Card.rect.height);
            return card;
        }

        internal void ChooseJoinProfile(BandCardController card, YargProfile profile)
        {
            if (card == _profilesInUse)
            {
                ChooseProfileForPendingSlot(profile);
                return;
            }

            CaptureCardPositions();
            if (!_session.ChooseJoinProfile(card.Model, profile))
            {
                RemoveCard(card);
                return;
            }

            card.CompleteJoin();
            ShiftCards(delay: 0f, riseDelay: SLIDE_SECONDS);
            RefreshProfilePickers(card);
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

        private void RefreshProfilePickers(BandCardController changed)
        {
            if (_profilesInUse != null)
            {
                _profilesInUse.Rebuild();
            }

            foreach (var card in _cards)
            {
                if (card != changed && card.State == BandColumnState.ChooseProfile)
                {
                    card.Rebuild();
                }
            }
        }

        private void CancelJoin(BandCardController card)
        {
            CaptureCardPositions();
            _session.CancelJoin(card.Model);
            RemoveCard(card);
        }

        internal void Disconnect(BandCardController card)
        {
            CaptureCardPositions();
            _session.Disconnect(card.Model);
            RemoveCard(card);
        }

        internal bool MoveColumn(BandCardController card, int direction)
        {
            CaptureCardPositions();
            if (!_session.MoveColumn(card.Model, direction))
            {
                return false;
            }

            ShiftCards(delay: 0f, riseDelay: null);
            return true;
        }

        private void ShowProfilesInUse(LayoutElement slot)
        {
            var view = Instantiate(_columnPrefab, slot.transform);
            var rect = (RectTransform) view.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var card = new BandCardController(this, _session, new BandColumnModel(), view);
            view.SetWidth(slot.preferredWidth);
            card.ShowProfilesInUse();

            _profilesInUse = card;
            _profilesInUseSlot = slot;

            LayoutRebuilder.ForceRebuildLayoutImmediate(view.Card);
            view.RiseFrom(_joinCard.rect.height - view.Card.rect.height);
        }

        internal void CloseProfilesInUse()
        {
            if (_profilesInUse != null)
            {
                _profilesInUse.View.DismissAndDestroy();
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
            var card = CreateCardInSlot(model, replacedSlotIndex);
            card.View.RiseFrom(-SLIDE_DISTANCE);
            card.Rebuild();
            _session.SyncColumnsAndSlots();
            ShiftCards(delay: 0f, riseDelay: null);
        }

        private void OnPlayerRemoved(YargPlayer player)
        {
            CaptureCardPositions();
            _session.HandleExternalPlayerRemoved(player, out var removedModel);
            if (removedModel != null && _cardsByModel.TryGetValue(removedModel, out var card))
            {
                RemoveCard(card);
            }
        }

        private BandCardController CreateCard(BandColumnModel model)
        {
            var view = Instantiate(_columnPrefab, _band);
            var card = new BandCardController(this, _session, model, view);
            _cardsByModel[model] = card;
            RefreshProfilePickers(card);
            return card;
        }

        private BandCardController CreateCardInSlot(BandColumnModel model, int replacedSlotIndex)
        {
            var card = CreateCard(model);
            if (replacedSlotIndex < 0)
            {
                return card;
            }

            var replacedSlot = FindJoinSlotAtSiblingIndex(replacedSlotIndex);
            if (replacedSlot != null)
            {
                card.View.transform.SetSiblingIndex(replacedSlotIndex);
                RemapCardSnapshot(JoinCardOf(replacedSlot), card.View.Card);
                DestroyJoinSlot(replacedSlot);
            }

            return card;
        }

        private void RemoveCard(BandCardController card)
        {
            _cards.Remove(card);
            _cardsByModel.Remove(card.Model);
            KillCardTweens(card.View.Card);
            card.View.DismissAndDestroy();

            RefreshProfilePickers(card);
            ShiftCards(delay: SLIDE_SECONDS, riseDelay: SLIDE_SECONDS);
        }

        private void RefitOnBandResize()
        {
            float height = MaxCardHeight;
            if (Mathf.Approximately(height, _fittedBandHeight))
            {
                return;
            }

            _fittedBandHeight = height;
            foreach (var card in _cards)
            {
                card.FitRows();
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

            bool raise = _session.IsOvershell && (_profilesInUse != null || AnyCardExpanded());
            _bandCanvas.sortingOrder = raise ? OPEN_CARD_SORTING_ORDER : _bandSortingOrder;
        }

        private bool AnyCardExpanded()
        {
            foreach (var card in _cards)
            {
                if (card.IsExpanded)
                {
                    return true;
                }
            }

            return false;
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

        private void SetJoinSlotCount(int count)
        {
            while (_joinSlots.Count > count)
            {
                DestroyJoinSlot(_joinSlots[_joinSlots.Count - 1].Slot);
            }

            while (_joinSlots.Count < count)
            {
                CreateJoinSlot();
            }
        }

        private void CreateJoinSlot()
        {
            int siblingIndex = _band.childCount;
            var slot = Instantiate(_joinSlot, _band);
            slot.transform.SetSiblingIndex(siblingIndex);
            slot.preferredWidth = -1f;

            var card = JoinCardOf(slot);
            card.anchoredPosition = Vector2.zero;
            foreach (var graphic in card.GetComponentsInChildren<Graphic>())
            {
                graphic.raycastTarget = true;
            }

            card.gameObject.AddComponent<JoinSlotClick>().Clicked = () => JoinWithoutDevice(slot);
            slot.gameObject.SetActive(true);

            var label = slot.GetComponentInChildren<TextMeshProUGUI>(true);
            _joinSlots.Add(new JoinSlotEntry(slot, card, label));
        }

        private void DestroyJoinSlot(LayoutElement slot)
        {
            if (slot == null)
            {
                return;
            }

            var card = JoinCardOf(slot);
            int joinSlotIndex = JoinSlotIndexOf(slot);
            if (joinSlotIndex >= 0)
            {
                _joinSlots.RemoveAt(joinSlotIndex);
            }

            KillCardTweens(card);
            slot.gameObject.SetActive(false);
            Destroy(slot.gameObject);
        }

        private void UpdateJoinCards(float width, string text)
        {
            foreach (var entry in _joinSlots)
            {
                if (!Mathf.Approximately(entry.Slot.preferredWidth, width))
                {
                    entry.Slot.preferredWidth = width;
                    entry.Card.sizeDelta = new Vector2(width, entry.Card.sizeDelta.y);
                }

                entry.Label.text = text;
            }
        }

        private LayoutElement FindJoinSlotAtSiblingIndex(int siblingIndex)
        {
            foreach (var entry in _joinSlots)
            {
                if (entry.Slot.transform.GetSiblingIndex() == siblingIndex)
                {
                    return entry.Slot;
                }
            }

            return null;
        }

        private int JoinSlotIndexOf(LayoutElement slot)
        {
            for (int joinSlotIndex = 0; joinSlotIndex < _joinSlots.Count; joinSlotIndex++)
            {
                if (_joinSlots[joinSlotIndex].Slot == slot)
                {
                    return joinSlotIndex;
                }
            }

            return -1;
        }

        private static RectTransform JoinCardOf(LayoutElement slot)
        {
            return (RectTransform) slot.transform.GetChild(0);
        }

        private void CaptureCardPositions()
        {
            _cardSnapshotBuffer.Clear();
            foreach (var card in _cards)
            {
                var rect = card.View.Card;
                _cardSnapshotBuffer[rect] = new CardSnapshot(CardPosition(rect), rect.sizeDelta.x);
            }

            foreach (var entry in _joinSlots)
            {
                _cardSnapshotBuffer[entry.Card] = new CardSnapshot(CardPosition(entry.Card), entry.Card.sizeDelta.x);
            }
        }

        private void RemapCardSnapshot(RectTransform oldCard, RectTransform newCard)
        {
            if (_cardSnapshotBuffer.Remove(oldCard, out var snapshot))
            {
                _cardSnapshotBuffer[newCard] = snapshot;
            }
        }

        private void ShiftCards(float delay, float? riseDelay)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(_band);

            foreach (var card in _cards)
            {
                ShiftCard(card.View.Card, delay);
            }

            foreach (var entry in _joinSlots)
            {
                var card = entry.Card;
                if (_cardSnapshotBuffer.ContainsKey(card))
                {
                    ShiftCard(card, delay);
                }
                else if (riseDelay is { } rise)
                {
                    card.DOKill();
                    card.anchoredPosition = new Vector2(0f, -SLIDE_DISTANCE);
                    card.DOAnchorPosY(0f, SLIDE_SECONDS)
                        .SetDelay(rise)
                        .SetEase(Ease.OutCubic)
                        .SetUpdate(true)
                        .SetLink(card.gameObject);
                }
            }
        }

        private void ShiftCard(RectTransform card, float delay)
        {
            if (_shifts.Remove(card, out var running))
            {
                running.Kill();
            }

            bool captured = _cardSnapshotBuffer.TryGetValue(card, out var snapshot);
            if (captured)
            {
                ResizeCard(card, snapshot.Width, delay);
            }

            float offset = captured ? snapshot.Position - SlotPosition(card) : 0f;
            bool settled = Mathf.Abs(offset) < 0.5f;
            card.anchoredPosition = new Vector2(settled ? 0f : offset, card.anchoredPosition.y);
            if (settled)
            {
                return;
            }

            _shifts[card] = card.DOAnchorPosX(0f, SLIDE_SECONDS)
                .SetDelay(delay)
                .SetEase(Ease.OutCubic)
                .SetUpdate(true)
                .SetLink(card.gameObject);
        }

        private void ResizeCard(RectTransform card, float from, float delay)
        {
            if (_resizes.Remove(card, out var running))
            {
                running.Kill();
            }

            float to = card.sizeDelta.x;
            if (Mathf.Abs(to - from) < 0.5f)
            {
                return;
            }

            card.sizeDelta = new Vector2(from, card.sizeDelta.y);
            _resizes[card] = DOTween.To(
                    () => card.sizeDelta.x,
                    x => card.sizeDelta = new Vector2(x, card.sizeDelta.y),
                    to,
                    SLIDE_SECONDS)
                .SetDelay(delay)
                .SetEase(Ease.OutCubic)
                .SetUpdate(true)
                .SetLink(card.gameObject);
        }

        private void KillCardTweens(RectTransform card)
        {
            if (_shifts.Remove(card, out var shift))
            {
                shift.Kill();
            }

            if (_resizes.Remove(card, out var resize))
            {
                resize.Kill();
            }

            card.DOKill();
        }

        private void KillAllCardTweens()
        {
            foreach (var shift in _shifts.Values)
            {
                shift.Kill();
            }

            foreach (var resize in _resizes.Values)
            {
                resize.Kill();
            }

            _shifts.Clear();
            _resizes.Clear();
        }

        private static float SlotPosition(RectTransform card)
        {
            return card.parent.localPosition.x;
        }

        private static float CardPosition(RectTransform card)
        {
            return SlotPosition(card) + card.anchoredPosition.x;
        }

        private void StartSong()
        {
            if (_session.IsStartingSong)
            {
                return;
            }

            _session.MarkStartingSong();
            _startInputBlocker = Navigator.Instance.PushInputBlocker();
            foreach (var card in _cards)
            {
                card.View.SlideOut();
            }

            foreach (var entry in _joinSlots)
            {
                entry.Card.DOAnchorPosY(-SLIDE_DISTANCE, SLIDE_SECONDS)
                    .SetEase(Ease.InCubic)
                    .SetUpdate(true)
                    .SetLink(entry.Card.gameObject);
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

        private void CancelStartDelay()
        {
            _startDelay?.Kill();
            _startDelay = null;
            ReleaseStartInputBlocker();
        }

        private void ReleaseStartInputBlocker()
        {
            _startInputBlocker?.Dispose();
            _startInputBlocker = null;
        }
    }
}
