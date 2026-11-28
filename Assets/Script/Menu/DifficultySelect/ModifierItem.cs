using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using YARG.Menu.Navigation;

namespace YARG.Menu.DifficultySelect
{
    public class ModifierItem : NavigatableBehaviour
    {
        [SerializeField]
        private TextMeshProUGUI _title;
        [SerializeField]
        private GameObject _checkedToggle;

        private bool _active;
        public bool Active
        {
            get => _active;
            set
            {
                _active = value;
                _checkedToggle.SetActive(_active);
            }
        }

        private Action<bool> _activeChangedCallback;

        private const float TITLE_FONT_SIZE_MIN = 4f;
        private const float SWITCH_WIDTH = 75f;
        private const float SWITCH_MARGIN = 25f;
        private const float SWITCH_WIDTH_SHARE = 0.3f;
        private const float TITLE_MARGIN = 25f;
        private const float TITLE_GAP = 8f;
        private const string UNCHECKED_NAME = "Unchecked";

        public void Initialize(string title, bool active, Action<bool> activeChangedCallback)
        {
            _title.text = title;
            _title.fontSizeMax = _title.fontSize;
            _title.fontSizeMin = TITLE_FONT_SIZE_MIN;
            _title.enableAutoSizing = true;
            _activeChangedCallback = activeChangedCallback;

            Active = active;
            FitToWidth();
        }

        private void OnRectTransformDimensionsChange()
        {
            FitToWidth();
        }

        private void FitToWidth()
        {
            if (_title == null)
            {
                return;
            }

            float width = ((RectTransform) transform).rect.width;
            if (width <= 0f)
            {
                return;
            }

            float scale = Mathf.Min(1f, width * SWITCH_WIDTH_SHARE / SWITCH_WIDTH);
            FitSwitch((RectTransform) _checkedToggle.transform, scale);
            if (transform.Find(UNCHECKED_NAME) is RectTransform uncheckedToggle)
            {
                FitSwitch(uncheckedToggle, scale);
            }

            var title = _title.rectTransform;
            title.offsetMin = new Vector2(TITLE_MARGIN * scale, title.offsetMin.y);
            title.offsetMax = new Vector2(-(SWITCH_MARGIN + SWITCH_WIDTH + TITLE_GAP) * scale, title.offsetMax.y);
        }

        private static void FitSwitch(RectTransform toggle, float scale)
        {
            toggle.localScale = new Vector3(scale, scale, 1f);
            toggle.anchoredPosition = new Vector2(-SWITCH_MARGIN * scale, toggle.anchoredPosition.y);
        }

        public override void OnPointerDown(PointerEventData eventData)
        {
            base.OnPointerDown(eventData);
            Confirm();
        }

        public override void Confirm()
        {
            base.Confirm();

            Active = !Active;

            _activeChangedCallback?.Invoke(Active);
        }
    }
}