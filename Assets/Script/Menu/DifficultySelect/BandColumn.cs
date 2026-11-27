using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YARG.Core;
using YARG.Helpers.Extensions;
using YARG.Menu.Navigation;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    internal sealed class BandColumn : MonoBehaviour, IPointerClickHandler, IScrollHandler
    {
        private const float UNFOCUSED_BORDER_BRIGHTNESS = 0.5f;

        [SerializeField]
        private RectTransform _card;
        [SerializeField]
        private VerticalLayoutGroup _cardLayout;
        [SerializeField]
        private Image _border;
        [SerializeField]
        private TextMeshProUGUI _heading;
        [SerializeField]
        private Image _icon;
        [SerializeField]
        private ScrollRect _scroll;
        [SerializeField]
        private VerticalLayoutGroup _rowLayout;
        [SerializeField]
        private LayoutElement _viewportLayout;
        [SerializeField]
        private LayoutElement _layout;
        [SerializeField]
        private NavigationGroup _nav;

        [Space]
        [SerializeField]
        private DifficultyItem _itemPrefab;
        [SerializeField]
        private DifficultyItem _readyPrefab;
        [SerializeField]
        private DifficultyItem _smallPrefab;
        [SerializeField]
        private DifficultyItem _profilePrefab;
        [SerializeField]
        private ModifierItem _modifierPrefab;
        [SerializeField]
        private Sprite _profileGenericSprite;
        [SerializeField]
        private Sprite _profileBotSprite;

        private BandSelectMenu _menu;
        private BandSessionState _session;
        private Color _borderColor;

        private BandColumnRows _rows;
        private BandColumnHeader _header;
        private BandColumnViewport _viewport;
        private BandColumnSettings _settings;
        private BandColumnMenus _menus;

        private float _appliedWidth = -1f;
        private Tween _rise;
        private Tween _collapse;
        private BandColumnState _builtState;

        public BandColumnModel Model { get; private set; }
        public YargPlayer Player => Model?.Player;

        public BandColumnState State => Model.State;
        public bool Ready => Model.Ready;

        public bool IsJoining => Model.IsJoining;
        public bool IsExpanded => Model.IsExpanded;
        public bool IsAtTopLevel => Model.IsAtTopLevel;
        public bool IsSittingOut => _session.IsSittingOut(Model);
        public bool HasChosenInstrument => Model.HasChosenInstrument;
        public bool IsDecided => _session.IsDecided(Model);

        public NavigationGroup Nav => _nav;
        public RectTransform Card => _card;

        private void Awake()
        {
            _header = new BandColumnHeader(_heading, _icon, _layout);
            _rows = new BandColumnRows(_scroll.content, _nav, _layout,
                itemPrefab: _itemPrefab,
                readyPrefab: _readyPrefab,
                smallPrefab: _smallPrefab,
                profilePrefab: _profilePrefab,
                modifierPrefab: _modifierPrefab,
                genericAvatar: _profileGenericSprite,
                botAvatar: _profileBotSprite);
            _viewport = new BandColumnViewport(_card, _cardLayout, _scroll, _rowLayout, _viewportLayout, _layout, _nav,
                _rows);
            _borderColor = _border.color;
        }

        public void Initialize(BandSelectMenu menu, BandSessionState session, BandColumnModel model)
        {
            _menu = menu;
            _session = session;
            Model = model;
            _settings = new BandColumnSettings(model, session, _rows, _nav);
            _menus = new BandColumnMenus(this, menu, session, _rows, _header, _settings);
            _border.raycastTarget = true;
            _nav.SelectionChanged += OnSelectionChanged;
        }

        public void RiseFrom(float offset)
        {
            _rise?.Kill();
            _card.anchoredPosition = new Vector2(_card.anchoredPosition.x, offset);
            _rise = _card.DOAnchorPosY(0f, BandSelectMenu.SLIDE_SECONDS)
                .SetEase(Ease.OutCubic)
                .SetUpdate(true);
        }

        private void OnDestroy()
        {
            _card.DOKill();
            _nav.SelectionChanged -= OnSelectionChanged;
            _header.ReleaseIcon();
            _rows.ReleaseAvatars();
        }

        public void DismissAndDestroy()
        {
            _nav.SelectionChanged -= OnSelectionChanged;
            _layout.ignoreLayout = true;
            gameObject.AddComponent<CanvasGroup>().blocksRaycasts = false;

            _card.DOKill();
            _card.DOAnchorPosY(-BandSelectMenu.SLIDE_DISTANCE, BandSelectMenu.SLIDE_SECONDS)
                .SetEase(Ease.InCubic)
                .SetUpdate(true)
                .SetLink(gameObject)
                .OnComplete(() => Destroy(gameObject));
        }

        public void SlideOut()
        {
            _rise?.Kill();
            _collapse?.Kill();
            if (!TryGetComponent(out CanvasGroup group))
            {
                group = gameObject.AddComponent<CanvasGroup>();
            }

            group.blocksRaycasts = false;
            _card.DOAnchorPosY(-BandSelectMenu.SLIDE_DISTANCE, BandSelectMenu.SLIDE_SECONDS)
                .SetEase(Ease.InCubic)
                .SetUpdate(true)
                .SetLink(gameObject);
        }

        public void SetFocused(bool focused)
        {
            _border.color = focused
                ? _borderColor
                : (_borderColor * UNFOCUSED_BORDER_BRIGHTNESS).WithAlpha(_borderColor.a);
        }

        public void SetWidth(float width)
        {
            if (Mathf.Approximately(_appliedWidth, width))
            {
                return;
            }

            _appliedWidth = width;
            _layout.preferredWidth = width;
            _rows.FitProfileRows();
            _card.sizeDelta = new Vector2(width, _card.sizeDelta.y);
            _header.Fit();
        }

        public void FitRows()
        {
            _viewport.Fit(Model.IsCollapsed, _menu.MaxCardHeight);
        }

        public void BeginJoin()
        {
            Rebuild();
        }

        public void Open()
        {
            Rebuild();
        }

        public void Collapse()
        {
            _session.ResetForOvershell(Model);
            Rebuild();
        }

        public void CompleteJoin()
        {
            if (_session.IsOvershell)
            {
                RebuildFromCurrentTop();
            }
            else
            {
                Rebuild();
            }
        }

        public void ShowProfilesInUse()
        {
            Model.ViewStack.SetRoot(BandColumnState.ChooseProfile);
            Rebuild();
        }

        public void Confirm()
        {
            _settings.Confirm();
        }

        public void NavigateVertical(int direction, bool isRepeat)
        {
            _settings.NavigateVertical(direction, isRepeat);
        }

        public bool AdjustSelection(int direction)
        {
            return _settings.AdjustSelection(direction);
        }

        public void Back()
        {
            if (_settings.StopEditing())
            {
                return;
            }

            if (State == BandColumnState.ChooseProfile ||
                (State == BandColumnState.Instrument && Model.ViewStack.Depth == 0))
            {
                _menu.Disconnect(this);
                return;
            }

            if (State == BandColumnState.Main)
            {
                if (Ready)
                {
                    _session.ToggleReady(Model);
                }
                else if (_session.CollapseColumn(Model))
                {
                    SlideDownAndCollapse();
                }

                return;
            }

            if (State == BandColumnState.SittingOut)
            {
                _session.OpenPicker(Model, canCancel: true);
                return;
            }

            _session.PopView(Model);
        }

        public void Rebuild()
        {
            int? previousIndex = _nav.SelectedIndex;
            bool sameState = _builtState == State;

            if (_collapse != null)
            {
                _collapse.Kill();
                _collapse = null;
                _card.anchoredPosition = new Vector2(_card.anchoredPosition.x, 0f);
            }

            _rows.Clear();
            _settings.Clear();
            _menus.Build(State);

            _builtState = State;
            Model.ViewStack.RestoreFocusKey = null;

            _viewport.StyleRows();
            _menus.SelectDefault(sameState ? previousIndex : null);
            _viewport.UpdateDividers();
            if (!sameState)
            {
                _viewport.ResetScroll();
            }

            FitRows();
        }

        public void ApplyReadyVisuals()
        {
            if (Ready)
            {
                SlideDownAndCollapse();
            }
            else
            {
                RebuildFromCurrentTop();
                if (_menus.ReadyButton != null)
                {
                    _nav.SelectButton(_menus.ReadyButton);
                }
            }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left || !_session.CanClickCards ||
                State != BandColumnState.Main)
            {
                return;
            }

            if (eventData.pointerPressRaycast.gameObject == null)
            {
                return;
            }

            if (Ready)
            {
                _session.ToggleReady(Model);
                return;
            }

            if (Expand())
            {
                return;
            }

            bool onHeader = !_viewport.ContainsScreenPoint(eventData.position, eventData.pressEventCamera);
            if (_session.IsOvershell && onHeader)
            {
                Back();
            }
        }

        public void OnScroll(PointerEventData eventData)
        {
            _viewport.OnScroll(eventData);
        }

        public bool Expand()
        {
            return _session.ExpandColumn(Model);
        }

        public void RebuildFromCurrentTop()
        {
            float top = _card.anchoredPosition.y + _card.rect.height;
            Rebuild();
            RiseFrom(top - _card.rect.height);
        }

        private void OnSelectionChanged(NavigatableBehaviour selected, SelectionOrigin origin)
        {
            _settings.OnSelectionChanged(selected);
            _viewport.UpdateDividers();

            if (origin == SelectionOrigin.Mouse)
            {
                _session.FocusColumn(Model);
            }
            else
            {
                _viewport.ScrollToSelection();
            }
        }

        private void SlideDownAndCollapse()
        {
            _rise?.Kill();
            _collapse?.Kill();

            _collapse = _card.DOAnchorPosY(_viewport.CollapsedHeight - _card.rect.height, BandSelectMenu.SLIDE_SECONDS)
                .SetEase(Ease.OutCubic)
                .SetUpdate(true)
                .OnComplete(() =>
                {
                    _collapse = null;
                    _card.anchoredPosition = new Vector2(_card.anchoredPosition.x, 0f);
                    Rebuild();
                });
        }
    }

    internal static class BandColumnExtensions
    {
        public static void SelectButton(this NavigationGroup nav, NavigatableBehaviour button)
        {
            for (int i = 0; i < nav.Navigatables.Count; i++)
            {
                if (nav.Navigatables[i] == button)
                {
                    nav.SelectAt(i);
                    return;
                }
            }

            nav.SelectAt(0);
        }

        public static bool ContainsInstrument(this IReadOnlyList<Instrument> instruments, Instrument target)
        {
            foreach (var instrument in instruments)
            {
                if (instrument == target)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
