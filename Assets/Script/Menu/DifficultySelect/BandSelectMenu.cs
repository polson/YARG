using System;
using System.Collections.Generic;
using System.Linq;
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
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Menu.ProfileList;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    public sealed class BandSelectMenu : MonoBehaviour
    {
        public struct Assets
        {
            public RectTransform   Root;
            public DifficultyItem  ItemPrefab;
            public DifficultyItem  ReadyPrefab;
            public DifficultyItem  SmallPrefab;
            public ModifierItem    ModifierPrefab;
            public TextMeshProUGUI TextTemplate;
        }

        internal static readonly Color READY_COLOR = new Color32(0x00, 0xD3, 0xFF, 0xFF);

        private const int MAX_PLAYERS = HighwayCameraRendering.MAX_MATRICES;

        private const float MAX_COLUMN_WIDTH = 380f;
        private const float SHORT_LABEL_WIDTH = 230f;
        private const float HIDE_NAME_WIDTH = 190f;
        private const float COLUMN_SPACING = 16f;
        private const float JOIN_SLOT_HEIGHT = 180f;
        private const float START_DELAY = 1f;
        private const float EXIT_HOLD_SECONDS = 0.6f;

        private static readonly Color JOIN_SLOT_COLOR = new(0f, 0f, 0f, 0.35f);

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

        private readonly List<BandColumn> _columns = new();
        private readonly List<Instrument> _instruments = new();

        private RectTransform _band;
        private GameObject _joinSlot;
        private LayoutElement _joinSlotLayout;
        private TextMeshProUGUI _joinText;
        private TextMeshProUGUI _footer;

        private List<SongEntry> _songs;
        private Action _startSong;
        private int _focusIndex;
        private float _startTimer = -1f;
        private bool _suppressPlayerEvents;

        internal Assets Prefabs { get; private set; }
        internal IReadOnlyList<SongEntry> Songs => _songs;
        internal IReadOnlyList<Instrument> Instruments => _instruments;
        internal int MaxHarmonyIndex { get; private set; }
        internal bool ShortLabels { get; private set; }
        internal bool HideNames { get; private set; }

        public bool IsOpen { get; private set; }

        private bool IsFull => PlayerContainer.Players.Count >= MAX_PLAYERS;

        public void Initialize(Assets assets)
        {
            Prefabs = assets;

            var bandObject = new GameObject("Band", typeof(RectTransform));
            _band = (RectTransform) bandObject.transform;
            _band.SetParent(assets.Root, false);
            _band.anchorMin = Vector2.zero;
            _band.anchorMax = Vector2.one;
            _band.offsetMin = new Vector2(50f, 230f);
            _band.offsetMax = new Vector2(-50f, -180f);

            var row = bandObject.AddComponent<HorizontalLayoutGroup>();
            row.childAlignment = TextAnchor.LowerCenter;
            row.spacing = COLUMN_SPACING;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;

            _joinSlot = new GameObject("Join Slot", typeof(RectTransform));
            _joinSlot.transform.SetParent(_band, false);
            _joinSlot.AddComponent<Image>().color = JOIN_SLOT_COLOR;
            _joinSlotLayout = _joinSlot.AddComponent<LayoutElement>();
            _joinSlotLayout.preferredHeight = JOIN_SLOT_HEIGHT;

            _joinText = CreateText(_joinSlot.transform, 24f, TextAlignmentOptions.Center);
            var joinRect = _joinText.rectTransform;
            joinRect.anchorMin = Vector2.zero;
            joinRect.anchorMax = Vector2.one;
            joinRect.pivot = new Vector2(0.5f, 0.5f);
            joinRect.offsetMin = new Vector2(10f, 10f);
            joinRect.offsetMax = new Vector2(-10f, -10f);

            _footer = CreateText(assets.Root, 28f, TextAlignmentOptions.Left);
            var footerRect = _footer.rectTransform;
            footerRect.anchorMin = Vector2.zero;
            footerRect.anchorMax = new Vector2(0.7f, 0f);
            footerRect.pivot = Vector2.zero;
            footerRect.offsetMin = new Vector2(50f, 170f);
            footerRect.offsetMax = new Vector2(0f, 220f);

            bandObject.SetActive(false);
            _footer.gameObject.SetActive(false);
        }

        public void Open(List<SongEntry> songs, Action startSong)
        {
            IsOpen = true;
            _songs = songs;
            _startSong = startSong;
            _startTimer = -1f;
            _focusIndex = 0;

            UpdateInstruments();

            foreach (var player in PlayerContainer.Players)
            {
                player.Profile.RestoreSavedModifiers();
            }

            _band.gameObject.SetActive(true);
            _footer.gameObject.SetActive(true);

            UpdateLabelSizes(PlayerContainer.Players.Count + 1);
            foreach (var player in PlayerContainer.Players)
            {
                AddColumn(player).OpenForSong();
            }

            _ = Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                new NavigationScheme.Entry(MenuAction.Up, "Menu.Common.Up",
                    ctx => ColumnFor(ctx.Player)?.Nav.SelectPrevious(ctx.IsRepeat)),
                new NavigationScheme.Entry(MenuAction.Down, "Menu.Common.Down",
                    ctx => ColumnFor(ctx.Player)?.Nav.SelectNext(ctx.IsRepeat)),
                new NavigationScheme.Entry(MenuAction.Green, "Menu.Common.Confirm",
                    ctx => ColumnFor(ctx.Player)?.Nav.ConfirmSelection()),
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", OnBack,
                    onHoldHandler: _ => MenuManager.Instance.PopMenu(),
                    holdSeconds: EXIT_HOLD_SECONDS, showHeldState: true),
                new NavigationScheme.Entry(MenuAction.Left, "Menu.Common.Left", ctx => MoveFocus(ctx, -1),
                    hide: true),
                new NavigationScheme.Entry(MenuAction.Right, "Menu.Common.Right", ctx => MoveFocus(ctx, 1),
                    hide: true),
            }, false));

            InputManager.UnclaimedDevicePressed = OnUnclaimedDevicePressed;
            PlayerContainer.PlayerAdded += OnPlayerAdded;
            PlayerContainer.PlayerRemoved += OnPlayerRemoved;

            OnColumnChanged();
        }

        public void Close()
        {
            if (!IsOpen)
            {
                return;
            }

            IsOpen = false;

            if (InputManager.UnclaimedDevicePressed == OnUnclaimedDevicePressed)
            {
                InputManager.UnclaimedDevicePressed = null;
            }

            PlayerContainer.PlayerAdded -= OnPlayerAdded;
            PlayerContainer.PlayerRemoved -= OnPlayerRemoved;

            foreach (var column in _columns.ToList())
            {
                if (column.IsJoining)
                {
                    CancelJoin(column);
                }
            }

            foreach (var column in _columns)
            {
                column.Destroy();
            }

            _columns.Clear();
            _startTimer = -1f;

            _band.gameObject.SetActive(false);
            _footer.gameObject.SetActive(false);

            Navigator.Instance.PopScheme();
        }

        private void OnDestroy()
        {
            if (InputManager.UnclaimedDevicePressed == OnUnclaimedDevicePressed)
            {
                InputManager.UnclaimedDevicePressed = null;
            }

            PlayerContainer.PlayerAdded -= OnPlayerAdded;
            PlayerContainer.PlayerRemoved -= OnPlayerRemoved;
        }

        private void Update()
        {
            if (!IsOpen || _startTimer < 0f)
            {
                return;
            }

            _startTimer -= Time.unscaledDeltaTime;
            if (_startTimer <= 0f)
            {
                _startTimer = -1f;
                StartSong();
            }
        }

        internal void OnColumnChanged()
        {
            if (!IsOpen)
            {
                return;
            }

            bool joining = _columns.Any(c => c.IsJoining);
            bool editing = _columns.Any(c => c.IsEditingInstrument);
            bool showSlot = !joining && (IsFull || !editing);

            _joinSlot.SetActive(showSlot);
            _joinSlot.transform.SetAsLastSibling();
            _joinText.text = IsFull
                ? Localize.KeyFormat("Menu.DifficultySelect.Band.Full", MAX_PLAYERS)
                : Localize.Key("Menu.DifficultySelect.Band.JoinPrompt");

            int slots = _columns.Count + (showSlot ? 1 : 0);
            if (UpdateLabelSizes(slots))
            {
                foreach (var column in _columns)
                {
                    column.Rebuild();
                }
            }

            float width = GetColumnWidth(slots);
            _joinSlotLayout.preferredWidth = width;

            _focusIndex = Mathf.Clamp(_focusIndex, 0, Mathf.Max(0, _columns.Count - 1));
            for (int i = 0; i < _columns.Count; i++)
            {
                _columns[i].Layout.preferredWidth = width;
                _columns[i].SetFocused(i == _focusIndex && !PlayerContainer.HasConnectedKeyboardProfile());
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
                    if (other == column || other.State != BandColumnState.Main)
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
            var device = column.JoinDevice;
            profile ??= CreateProfileFor(device);

            if (profile != column.Player.Profile)
            {
                var created = column.CreatedProfile;
                DetachPlayer(column);

                if (created is not null && created != profile)
                {
                    PlayerContainer.RemoveProfile(created);
                    column.CreatedProfile = null;
                }

                var player = AttachPlayer(profile, device, out bool owned);
                if (player is null)
                {
                    RemoveColumn(column);
                    OnColumnChanged();
                    return;
                }

                column.Player = player;
                column.JoinDeviceOwned = owned;
            }

            column.OpenForSong();
            PlayerContainer.SaveProfiles();
            BindingsContainer.SaveBindings();
            OnColumnChanged();
        }

        internal void CancelJoin(BandColumn column)
        {
            var created = column.CreatedProfile;
            DetachPlayer(column);

            if (created is not null)
            {
                PlayerContainer.RemoveProfile(created);
            }

            RemoveColumn(column);
            OnColumnChanged();
        }

        internal void Disconnect(BandColumn column)
        {
            if (column.IsJoining)
            {
                CancelJoin(column);
                return;
            }

            _suppressPlayerEvents = true;
            try
            {
                PlayerContainer.DisposePlayer(column.Player);
            }
            finally
            {
                _suppressPlayerEvents = false;
            }

            RemoveColumn(column);
            OnColumnChanged();
        }

        private void OnBack(NavigationContext context)
        {
            var column = ColumnFor(context.Player);
            if (column is not null)
            {
                column.Back();
            }
            else if (context.Player is null && _columns.Count == 0)
            {
                MenuManager.Instance.PopMenu();
            }
        }

        private void MoveFocus(NavigationContext context, int direction)
        {
            if (context.Player is not null || _columns.Count == 0)
            {
                return;
            }

            _focusIndex = Mathf.Clamp(_focusIndex + direction, 0, _columns.Count - 1);
            OnColumnChanged();
        }

        private BandColumn ColumnFor(YargPlayer player)
        {
            if (player is not null)
            {
                return _columns.FirstOrDefault(c => c.Player == player);
            }

            return _columns.Count > 0 ? _columns[Mathf.Clamp(_focusIndex, 0, _columns.Count - 1)] : null;
        }

        private bool OnUnclaimedDevicePressed(InputDevice device)
        {
            if (PlayerContainer.IsControllerTaken(device))
            {
                return false;
            }

            if (!IsOpen || DialogManager.Instance.IsDialogShowing || IsFull ||
                _columns.Any(c => c.IsJoining || c.IsEditingInstrument))
            {
                return true;
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

                return true;
            }

            AddColumn(player).BeginJoin(device, owned, created);
            OnColumnChanged();
            return true;
        }

        private YargPlayer AttachPlayer(YargProfile profile, InputDevice device, out bool owned)
        {
            var deviceInfo = BindingsContainer.GetDeviceInfoForProfile(profile);
            owned = deviceInfo.MatchesController(device) || deviceInfo.ContainsController(device);

            YargPlayer player;
            _suppressPlayerEvents = true;
            try
            {
                player = PlayerContainer.CreatePlayerFromProfile(profile, false);
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

        private void DetachPlayer(BandColumn column)
        {
            var player = column.Player;

            if (!column.JoinDeviceOwned && column.JoinDevice is not null)
            {
                player.DisableInputs();
                player.DeviceInfo.RemoveController(column.JoinDevice);
            }

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
            if (_suppressPlayerEvents || _columns.Any(c => c.Player == player))
            {
                return;
            }

            AddColumn(player).OpenForSong();
            OnColumnChanged();
        }

        private void OnPlayerRemoved(YargPlayer player)
        {
            if (_suppressPlayerEvents)
            {
                return;
            }

            var column = _columns.FirstOrDefault(c => c.Player == player);
            if (column is null)
            {
                return;
            }

            RemoveColumn(column);
            OnColumnChanged();
        }

        private BandColumn AddColumn(YargPlayer player)
        {
            var column = new BandColumn(this, _band, player);
            _columns.Add(column);
            _joinSlot.transform.SetAsLastSibling();
            return column;
        }

        private void RemoveColumn(BandColumn column)
        {
            _columns.Remove(column);
            column.Destroy();
        }

        private void UpdateStatus()
        {
            int ready = _columns.Count(c => c.IsDecided && !c.IsSittingOut);
            int playing = _columns.Count(c => !c.IsSittingOut);
            bool allDecided = _columns.Count > 0 && _columns.All(c => c.IsDecided);

            if (_columns.Count == 0)
            {
                _footer.text = Localize.Key("Menu.DifficultySelect.Band.NoPlayers");
            }
            else if (allDecided && ready == 0)
            {
                _footer.text = Localize.Key("Menu.DifficultySelect.Band.NobodyPlaying");
            }
            else if (allDecided)
            {
                _footer.text = Localize.Key("Menu.DifficultySelect.Band.Starting");
            }
            else
            {
                _footer.text = Localize.KeyFormat("Menu.DifficultySelect.Band.ReadyCount", ready, playing);
            }

            if (allDecided && ready > 0)
            {
                if (_startTimer < 0f)
                {
                    _startTimer = START_DELAY;
                }
            }
            else
            {
                _startTimer = -1f;
            }
        }

        private void StartSong()
        {
            YargProfile vocalLeader = null;

            foreach (var column in _columns)
            {
                var player = column.Player;
                player.SittingOut = column.IsSittingOut;

                if (player.SittingOut || player.Profile.GameMode != GameMode.Vocals)
                {
                    continue;
                }

                if (vocalLeader is null)
                {
                    vocalLeader = player.Profile;
                }
                else
                {
                    player.Profile.ApplySessionModifiers(vocalLeader);
                }
            }

            _startSong?.Invoke();
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
                if (_songs.All(s => DifficultySelectMenu.SongHasInstrument(s, instrument)))
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

        private float GetColumnWidth(int slots)
        {
            slots = Mathf.Max(1, slots);
            float available = _band.rect.width - COLUMN_SPACING * (slots - 1);
            return Mathf.Min(MAX_COLUMN_WIDTH, available / slots);
        }

        private bool UpdateLabelSizes(int slots)
        {
            float width = GetColumnWidth(slots);
            bool shortLabels = width < SHORT_LABEL_WIDTH;
            bool hideNames = width < HIDE_NAME_WIDTH;

            bool changed = shortLabels != ShortLabels || hideNames != HideNames;
            ShortLabels = shortLabels;
            HideNames = hideNames;
            return changed;
        }

        private TextMeshProUGUI CreateText(Transform parent, float fontSize, TextAlignmentOptions alignment)
        {
            var text = Instantiate(Prefabs.TextTemplate, parent);
            text.gameObject.SetActive(true);
            text.name = "Text";
            text.text = string.Empty;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Ellipsis;
            return text;
        }
    }
}
