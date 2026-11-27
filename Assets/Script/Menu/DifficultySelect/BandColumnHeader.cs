using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core.Game;
using YARG.Gameplay.HUD;
using YARG.Helpers.Extensions;

namespace YARG.Menu.DifficultySelect
{
    internal sealed class BandColumnHeader
    {
        private const float ICON_SIZE = 38f;
        private const float MARGIN = 40f;
        private const float SPACING = 10f;
        private const float FONT_SIZE_MIN = 6f;
        private const float ICON_MIN_CARD_WIDTH = 110f;

        private readonly TextMeshProUGUI _heading;
        private readonly Image _icon;
        private readonly LayoutElement _layout;
        private readonly LayoutElement _iconLayout;
        private readonly HorizontalLayoutGroup _content;
        private readonly PlayerCardHeader _header;
        private readonly Color _headingColor;
        private bool _wantsIcon;

        public BandColumnHeader(TextMeshProUGUI heading, Image icon, LayoutElement layout)
        {
            _heading = heading;
            _icon = icon;
            _layout = layout;
            _header = new PlayerCardHeader(heading, icon);
            _iconLayout = icon.GetComponent<LayoutElement>();
            _content = icon.GetComponentInParent<HorizontalLayoutGroup>(true);
            _heading.fontSizeMin = FONT_SIZE_MIN;
            _headingColor = heading.color;
        }

        public void SetText(string text, bool showIcon = false)
        {
            _heading.text = text;
            _heading.color = _headingColor;
            _heading.gameObject.SetActive(!string.IsNullOrEmpty(text));
            _wantsIcon = showIcon && _icon.sprite != null;
            Fit();
        }

        public void SetPlayer(YargProfile profile, bool ready)
        {
            string mode = profile.GameMode.ToResourceName();
            _header.SetIcon(mode == null ? null : $"InstrumentIcons[{mode}]");
            SetText(profile.Name, showIcon: true);
            if (ready)
            {
                _heading.color = BandSelectMenu.READY_COLOR;
            }
        }

        public void Fit()
        {
            float width = _layout.preferredWidth;
            _icon.gameObject.SetActive(_wantsIcon && width >= ICON_MIN_CARD_WIDTH);
            float scale = Mathf.Clamp01(width * BandColumnRows.AVATAR_WIDTH_SHARE / ICON_SIZE);
            float iconSize = ICON_SIZE * scale;
            _iconLayout.minWidth = iconSize;
            _iconLayout.minHeight = iconSize;
            _iconLayout.preferredWidth = iconSize;
            _iconLayout.preferredHeight = iconSize;
            _icon.rectTransform.sizeDelta = new Vector2(iconSize, iconSize);
            float spaceScale = scale * scale;
            _content.spacing = SPACING * spaceScale;
            _header.Fit(width, MARGIN * spaceScale);
        }

        public void ReleaseIcon()
        {
            _header.ReleaseIcon();
        }
    }
}
