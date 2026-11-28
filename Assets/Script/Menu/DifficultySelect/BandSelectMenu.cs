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
using YARG.Menu.Filters;
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
        private const float MAX_COLUMN_WIDTH = 460f;
        internal const float JOIN_STRIP_HEIGHT = 72f;
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
        private readonly Dictionary<BandColumnModel, BandCardController> _cardsByModel = new();
        private readonly BandStripAnimator _animator = new();
        private readonly List<JoinedColumn> _joinedBuffer = new();
        private readonly List<BandColumnModel> _cancelJoinBuffer = new();

        private JoinSlotStrip _joinSlots;
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

        private DifficultyRing RingTemplate { get; set; }
        private float MaxCardHeight => _band.rect.height;

        public bool IsOpen => _session.IsOpen;
        public bool IsOvershell => _session.IsOvershell;

        private void Awake()
        {
            _bandLayout = _band.GetComponent<HorizontalLayoutGroup>();
            _joinSlots = new JoinSlotStrip(_band, _joinSlot, _animator, slot => JoinWithoutDevice(slot));
            _session.ColumnsChanged += OnSessionColumnsChanged;
        }

        public void Open(List<SongEntry> songs, SongEntry currentSong, bool isCareer, DifficultyRing ringTemplate,
            Action startSong)
        {
            RingTemplate = ringTemplate;
            _startSong = startSong;

            OpenBand(songs, currentSong, isCareer, isOvershell: false, canEngage: null);

            _scheme = CreateBandScheme();
            _ = Navigator.Instance.PushScheme(_scheme);
            Navigator.Instance.NavigationEvent += OnNavigationEvent;

            ListenForPlayers();
        }

        public void OpenOvershell(Func<bool> canEngage)
        {
            OpenBand(songs: null, currentSong: null, isCareer: false, isOvershell: true, canEngage: canEngage);
            Navigator.Instance.InputInterceptor = InterceptOvershellInput;
            ListenForPlayers();
        }

        private void OpenBand(List<SongEntry> songs, SongEntry currentSong, bool isCareer, bool isOvershell,
            Func<bool> canEngage)
        {
            _joinSlot.gameObject.SetActive(false);
            _band.gameObject.SetActive(true);
            _footer.gameObject.SetActive(!isOvershell);
            ShowFooterBackground(!isOvershell);

            _session.Open(songs, currentSong, isCareer, isOvershell, canEngage,
                isDialogShowing: () => DialogManager.Instance.IsDialogShowing,
                onGameModeChanged: FiltersMenu.ResetIntensityFiltersForProfile);
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
            if (!_session.SetOvershellHidden(hidden, _cancelJoinBuffer))
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

            if (hidden)
            {
                CloseProfilesInUse();
            }

            _band.gameObject.SetActive(!hidden);
        }

        public void Close()
        {
            if (!_session.IsOpen)
            {
                return;
            }

            bool wasOvershell = _session.IsOvershell;
            CancelStartDelay();
            Unsubscribe();

            _session.Close();

            _animator.KillAll();
            _joinSlots.SetCount(0);

            foreach (var card in _cardsByModel.Values)
            {
                Destroy(card.View.gameObject);
            }

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

            _session.DrainPendingJoins(_joinedBuffer);
            if (_joinedBuffer.Count > 0)
            {
                CaptureCardPositions();
                for (int joinIndex = 0; joinIndex < _joinedBuffer.Count; joinIndex++)
                {
                    var joined = _joinedBuffer[joinIndex];
                    FinishJoin(joined.Column, joined.ReplacedSlotIndex);
                }
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

            float width = GetColumnWidth(_session.Slots.Count);
            string joinText = _session.IsFull
                ? Localize.KeyFormat("Menu.DifficultySelect.Band.Full", BandSessionState.MAX_PLAYERS)
                : Localize.Key("Menu.DifficultySelect.Band.JoinPrompt");
            _joinSlots.UpdateCards(width, joinText);
            ApplyColumnWidthsAndFocus(width);
            if (!_session.IsOvershell)
            {
                UpdateStatus();
            }
        }

        private void SyncCardsWithSession()
        {
            int joinSlotCount = 0;
            foreach (var model in _session.Slots)
            {
                if (model == null)
                {
                    joinSlotCount++;
                    continue;
                }

                if (!_cardsByModel.ContainsKey(model))
                {
                    var card = CreateCard(model);
                    if (!_session.IsOvershell)
                    {
                        card.View.RiseFrom(-BandStripAnimator.SLIDE_DISTANCE);
                    }

                    card.Rebuild();
                }
            }

            _joinSlots.SetCount(joinSlotCount);

            int joinSlotIndex = 0;
            foreach (var model in _session.Slots)
            {
                if (model == null)
                {
                    _joinSlots.MoveToEnd(joinSlotIndex++);
                    continue;
                }

                if (_cardsByModel.TryGetValue(model, out var card))
                {
                    card.View.transform.SetAsLastSibling();
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
            int columnIndex = 0;
            foreach (var model in _session.Slots)
            {
                if (model == null || !_cardsByModel.TryGetValue(model, out var card))
                {
                    continue;
                }

                bool isFocused = _session.IsOvershell || (columnIndex == _session.FocusIndex && !hasKeyboard);
                card.View.SetWidth(width);
                card.View.SetFocused(isFocused);
                columnIndex++;
            }
        }

        private void UpdateStatus()
        {
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
            else
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
            return _session.QueuePendingJoin(device);
        }

        private BandCardController JoinWithoutDevice(LayoutElement slot, YargProfile chosen = null)
        {
            if (!_joinSlots.Contains(slot))
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
            var card = SeatCard(model, replacedSlotIndex);
            ShiftCards(delay: BandStripAnimator.SLIDE_SECONDS, riseDelay: BandStripAnimator.SLIDE_SECONDS * 2f);

            LayoutRebuilder.ForceRebuildLayoutImmediate(card.View.Card);
            card.View.RiseFrom(_joinCard.rect.height - card.View.Card.rect.height);
            return card;
        }

        private void ChooseJoinProfile(BandCardController card, YargProfile profile)
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
            ShiftCards(delay: 0f, riseDelay: BandStripAnimator.SLIDE_SECONDS);
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

            foreach (var card in _cardsByModel.Values)
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

        private void Disconnect(BandCardController card)
        {
            CaptureCardPositions();
            _session.Disconnect(card.Model);
            RemoveCard(card);
        }

        private bool MoveColumn(BandCardController card, int direction)
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

            var card = CreateCardController(new BandColumnModel(), view);
            view.SetWidth(slot.preferredWidth);
            card.ShowProfilesInUse();

            _profilesInUse = card;
            _profilesInUseSlot = slot;

            LayoutRebuilder.ForceRebuildLayoutImmediate(view.Card);
            view.RiseFrom(_joinCard.rect.height - view.Card.rect.height);
        }

        private void CloseProfilesInUse()
        {
            if (_profilesInUse != null)
            {
                _profilesInUse.View.DismissAndDestroy();
            }

            _profilesInUse = null;
            _profilesInUseSlot = null;
            _session.CloseProfilePicker();
        }

        private void OnPlayerAdded(YargPlayer player)
        {
            if (_session.IsSuppressingPlayerEvents || _session.FindColumn(player) != null)
            {
                return;
            }

            CaptureCardPositions();
            var model = _session.HandleExternalPlayerAdded(player, out int replacedSlotIndex);
            var card = SeatCard(model, replacedSlotIndex);
            card.View.RiseFrom(-BandStripAnimator.SLIDE_DISTANCE);
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
            var card = CreateCardController(model, view);
            _cardsByModel[model] = card;
            RefreshProfilePickers(card);
            return card;
        }

        private BandCardController CreateCardController(BandColumnModel model, BandColumn view)
        {
            var card = new BandCardController(_session, model, view, () => MaxCardHeight, RingTemplate);
            card.DisconnectRequested += Disconnect;
            card.CloseProfilesInUseRequested += _ => CloseProfilesInUse();
            card.ProfileChosen += ChooseJoinProfile;
            card.MoveRequested += MoveColumn;
            return card;
        }

        /// <summary>
        /// Creates the card for a column the session just seated, puts it where the join slot it replaced was,
        /// shows it, and lets the session settle the slots around it.
        /// </summary>
        private BandCardController SeatCard(BandColumnModel model, int replacedSlotIndex)
        {
            var card = CreateCard(model);
            var replacedSlot = replacedSlotIndex >= 0 ? _joinSlots.FindAtSiblingIndex(replacedSlotIndex) : null;
            if (replacedSlot != null)
            {
                card.View.transform.SetSiblingIndex(replacedSlotIndex);
                _animator.RemapSnapshot(JoinSlotStrip.CardOf(replacedSlot), card.View.Card);
                _joinSlots.Destroy(replacedSlot);
            }

            card.Rebuild();
            _session.SyncColumnsAndSlots();
            return card;
        }

        private void RemoveCard(BandCardController card)
        {
            _cardsByModel.Remove(card.Model);
            _animator.Kill(card.View.Card);
            card.View.DismissAndDestroy();

            RefreshProfilePickers(card);
            ShiftCards(delay: BandStripAnimator.SLIDE_SECONDS, riseDelay: BandStripAnimator.SLIDE_SECONDS);
        }

        private void RefitOnBandResize()
        {
            float height = MaxCardHeight;
            if (Mathf.Approximately(height, _fittedBandHeight))
            {
                return;
            }

            _fittedBandHeight = height;
            foreach (var card in _cardsByModel.Values)
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

            bool raise = _session.IsOvershell && (_session.HasPendingProfilePicker || AnyCardExpanded());
            _bandCanvas.sortingOrder = raise ? OPEN_CARD_SORTING_ORDER : _bandSortingOrder;
        }

        private bool AnyCardExpanded()
        {
            foreach (var card in _cardsByModel.Values)
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

        private void CaptureCardPositions()
        {
            _animator.ClearSnapshots();
            foreach (var card in _cardsByModel.Values)
            {
                _animator.Capture(card.View.Card);
            }

            _joinSlots.CapturePositions();
        }

        private void ShiftCards(float delay, float? riseDelay)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(_band);

            foreach (var card in _cardsByModel.Values)
            {
                _animator.Shift(card.View.Card, delay);
            }

            _joinSlots.Shift(delay, riseDelay);
        }

        private void StartSong()
        {
            if (_session.IsStartingSong)
            {
                return;
            }

            _session.MarkStartingSong();
            _startInputBlocker = Navigator.Instance.PushInputBlocker();
            foreach (var card in _cardsByModel.Values)
            {
                card.View.SlideOut();
            }

            _joinSlots.SlideOut();

            _startDelay = DOVirtual.DelayedCall(BandStripAnimator.SLIDE_SECONDS, () =>
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
