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
        private readonly Dictionary<RectTransform, Tween> _resizes = new();
        private BandColumn _profilesInUse;
        private LayoutElement _profilesInUseSlot;
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
        private float _fittedBandHeight;
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

        internal bool CanClickCards => !_started && !DialogManager.Instance.IsDialogShowing &&
            (!IsOvershell || (!_hidden && _canEngage()));

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
                    ctx => ColumnFor(ctx.Player)?.NavigateVertical(-1, ctx.IsRepeat)),
                new NavigationScheme.Entry(MenuAction.Down, "Menu.Common.Down",
                    ctx => ColumnFor(ctx.Player)?.NavigateVertical(1, ctx.IsRepeat)),
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

            RefitOnBandResize();
            UpdateSortingOrder();
        }

        internal void OnColumnChanged()
        {
            if (!IsOpen)
            {
                return;
            }

            PlayerContainer.SetPlayerOrder(_columns.Select(c => c.Player).ToList());

            int spare = _columns.Any(c => c.IsJoining) ? 0 : 1;
            int slots = Mathf.Max(MIN_SLOTS, _columns.Count + spare);
            SetJoinSlotCount(slots - _columns.Count);
            RecordSeats();

            string joinText = IsFull
                ? Localize.KeyFormat("Menu.DifficultySelect.Band.Full", MAX_PLAYERS)
                : Localize.Key("Menu.DifficultySelect.Band.JoinPrompt");

            float width = GetColumnWidth(slots);
            foreach (var slot in _joinSlots)
            {
                if (!Mathf.Approximately(slot.preferredWidth, width))
                {
                    slot.preferredWidth = width;
                    var card = JoinCard(slot);
                    card.sizeDelta = new Vector2(width, card.sizeDelta.y);
                }
                slot.GetComponentInChildren<TextMeshProUGUI>(true).text = joinText;
            }

            if (_profilesInUse != null)
            {
                _profilesInUse.SetWidth(width);
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

    }
}
