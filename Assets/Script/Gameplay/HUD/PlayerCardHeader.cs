using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

namespace YARG.Gameplay.HUD
{
    public sealed class PlayerCardHeader
    {
        private const float MARGIN = 40f;

        private readonly TextMeshProUGUI       _heading;
        private readonly LayoutElement         _headingLayout;
        private readonly Image                 _icon;
        private readonly LayoutElement         _iconLayout;
        private readonly HorizontalLayoutGroup _layout;

        private AsyncOperationHandle<Sprite> _iconHandle;
        private string                       _iconKey;

        public PlayerCardHeader(TextMeshProUGUI heading, Image icon)
        {
            _heading = heading;
            _headingLayout = heading.GetComponent<LayoutElement>();
            _icon = icon;
            _iconLayout = icon.GetComponent<LayoutElement>();
            _layout = heading.GetComponentInParent<HorizontalLayoutGroup>(true);
        }

        public void SetIcon(string key)
        {
            if (key == _iconKey)
            {
                return;
            }

            ReleaseIcon();
            _iconKey = key;
            if (key is null)
            {
                return;
            }

            _iconHandle = Addressables.LoadAssetAsync<Sprite>(key);
            _icon.sprite = _iconHandle.WaitForCompletion();
        }

        public void ReleaseIcon()
        {
            _icon.sprite = null;
            _iconKey = null;
            if (_iconHandle.IsValid())
            {
                Addressables.Release(_iconHandle);
            }
        }

        public void Fit(float cardWidth, float margin = MARGIN)
        {
            if (cardWidth <= 0f)
            {
                return;
            }

            float maxWidth = cardWidth - margin;
            if (_icon.gameObject.activeSelf)
            {
                float iconWidth = _iconLayout != null && _iconLayout.preferredWidth > 0f
                    ? _iconLayout.preferredWidth
                    : _icon.rectTransform.sizeDelta.x;
                maxWidth -= iconWidth + (_layout != null ? _layout.spacing : 0f);
            }

            _heading.enableAutoSizing = false;
            _heading.fontSize = _heading.fontSizeMax;

            float preferred = _heading.preferredWidth;
            _headingLayout.preferredWidth = Mathf.Min(preferred, maxWidth);
            _heading.enableAutoSizing = preferred > maxWidth;
        }
    }
}
