using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using YARG.Core.Game;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.Navigation;
using Object = UnityEngine.Object;

namespace YARG.Menu.DifficultySelect
{
    internal enum BandRowStyle
    {
        Normal,
        Small,
        Ready,
        Profile,
    }

    internal sealed class BandColumnRows
    {
        public const float ROW_HEIGHT = 52f;
        public const float TWO_LINE_ROW_HEIGHT = 72f;
        public const float PROFILE_ROW_HEIGHT = 64f;
        public const float AVATAR_WIDTH_SHARE = 0.25f;

        private const float AVATAR_MARGIN = 6f;
        private const float AVATAR_EDGE = 6f;
        private const float SMALL_FONT_SIZE = 20f;
        private const float LABEL_FONT_SIZE = 18f;
        private const float VALUE_FONT_SIZE = 28f;

        private readonly RectTransform _container;
        private readonly NavigationGroup _nav;
        private readonly LayoutElement _layout;
        private readonly DifficultyItem _itemPrefab;
        private readonly DifficultyItem _readyPrefab;
        private readonly DifficultyItem _smallPrefab;
        private readonly DifficultyItem _profilePrefab;
        private readonly ModifierItem _modifierPrefab;
        private readonly Sprite _genericAvatar;
        private readonly Sprite _botAvatar;

        private readonly List<(DifficultyItem Row, VerticalLayoutGroup Layout, float Height)> _profileRows = new();
        private readonly List<(DifficultyItem Row, LayoutElement Layout)> _wrappableRows = new();
        private readonly List<(ModifierItem Item, Modifier Modifier)> _modifierToggles = new();
        private readonly List<Texture2D> _avatarTextures = new();

        public BandColumnRows(RectTransform container, NavigationGroup nav, LayoutElement layout,
            DifficultyItem itemPrefab, DifficultyItem readyPrefab, DifficultyItem smallPrefab,
            DifficultyItem profilePrefab, ModifierItem modifierPrefab, Sprite genericAvatar, Sprite botAvatar)
        {
            _container = container;
            _nav = nav;
            _layout = layout;
            _itemPrefab = itemPrefab;
            _readyPrefab = readyPrefab;
            _smallPrefab = smallPrefab;
            _profilePrefab = profilePrefab;
            _modifierPrefab = modifierPrefab;
            _genericAvatar = genericAvatar;
            _botAvatar = botAvatar;
        }

        public NavigatableBehaviour DefaultButton { get; set; }
        public IReadOnlyList<(DifficultyItem Row, LayoutElement Layout)> WrappableRows => _wrappableRows;

        public void Clear()
        {
            _nav.ClearNavigatables();
            ReleaseAvatars();
            _profileRows.Clear();
            _wrappableRows.Clear();
            _modifierToggles.Clear();
            DefaultButton = null;
            for (int i = _container.childCount - 1; i >= 0; i--)
            {
                var child = _container.GetChild(i).gameObject;
                child.SetActive(false);
                Object.Destroy(child);
            }
        }

        public void ReleaseAvatars()
        {
            foreach (var texture in _avatarTextures)
            {
                Object.Destroy(texture);
            }

            _avatarTextures.Clear();
        }

        public DifficultyItem AddRow(string header, string body, UnityAction action, bool interactable,
            BandRowStyle style = BandRowStyle.Normal, float height = ROW_HEIGHT)
        {
            var prefab = PrefabFor(style);
            var row = Object.Instantiate(prefab != null ? prefab : _itemPrefab, _container);

            if (header == null)
            {
                row.Initialize(body, action);
            }
            else
            {
                row.Initialize(header, body, action);
                row.UseCompactText(LABEL_FONT_SIZE, VALUE_FONT_SIZE);
            }

            row.GetComponent<LayoutElement>().preferredHeight = height;
            row.SetInteractable(interactable);

            if (interactable)
            {
                _nav.AddNavigatable(row.Button);
            }

            return row;
        }

        public DifficultyItem AddLabel(string text)
        {
            var label = Object.Instantiate(_itemPrefab, _container);
            label.InitializeAsTitle(text);
            label.UseCompactText(SMALL_FONT_SIZE);
            return label;
        }

        public void AddWrappable(DifficultyItem row)
        {
            _wrappableRows.Add((row, row.GetComponent<LayoutElement>()));
        }

        public ModifierItem AddToggle(string label, bool active, Action<bool> onChanged)
        {
            var toggle = Object.Instantiate(_modifierPrefab, _container);
            toggle.Initialize(label, active, onChanged);

            _nav.AddNavigatable(toggle);
            return toggle;
        }

        public void AddModifierToggle(YargProfile profile, Modifier modifier)
        {
            var toggle = AddToggle(modifier.ToLocalizedName(), profile.IsModifierActive(modifier), on =>
            {
                if (on)
                {
                    profile.AddSingleModifier(modifier);
                }
                else
                {
                    profile.RemoveModifiers(modifier);
                }

                foreach (var (item, itemModifier) in _modifierToggles)
                {
                    item.Active = profile.IsModifierActive(itemModifier);
                }
            });

            _modifierToggles.Add((toggle, modifier));
        }

        public void AddAvatar(DifficultyItem row, YargProfile profile, float rowHeight)
        {
            if (profile.Avatar != null && profile.Avatar.IsValid)
            {
                var texture = profile.Avatar.LoadTexture(false);
                _avatarTextures.Add(texture);
                row.SetAvatar(Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f)), flipped: true);
            }
            else
            {
                row.SetAvatar(profile.IsBot ? _botAvatar : _genericAvatar, flipped: false);
            }

            var rowLayout = row.GetComponent<VerticalLayoutGroup>();
            _profileRows.Add((row, rowLayout, rowHeight));
            FitProfileRow(row, rowLayout, rowHeight);
        }

        public void FitProfileRows()
        {
            foreach (var (row, rowLayout, rowHeight) in _profileRows)
            {
                FitProfileRow(row, rowLayout, rowHeight);
            }
        }

        private void FitProfileRow(DifficultyItem row, VerticalLayoutGroup rowLayout, float rowHeight)
        {
            float fullSize = rowHeight - AVATAR_EDGE * 2f;
            float size = Mathf.Min(fullSize, _layout.preferredWidth * AVATAR_WIDTH_SHARE);
            float scale = size / fullSize;
            float edge = AVATAR_EDGE * 2f * scale;
            float gap = AVATAR_MARGIN * scale;
            row.FitAvatar(rowHeight, size, edge);

            int inset = Mathf.CeilToInt(edge + size + gap);
            if (rowLayout != null)
            {
                rowLayout.padding = new RectOffset(inset, Mathf.CeilToInt(edge), 0, 0);
            }
        }

        private DifficultyItem PrefabFor(BandRowStyle style)
        {
            return style switch
            {
                BandRowStyle.Small   => _smallPrefab,
                BandRowStyle.Ready   => _readyPrefab,
                BandRowStyle.Profile => _profilePrefab,
                _                    => _itemPrefab,
            };
        }
    }
}
