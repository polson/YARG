using UnityEngine;
using YARG.Core.Logging;

namespace YARG.Helpers.UI
{
    /// <summary>
    /// Resizes a RectTransform to fit a specified aspect ratio.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class ScaleByParentSize : MonoBehaviour
    {
        private RectTransform _parentRectTransform;
        private RectTransform ParentRectTransform
        {
            get
            {
                if (_parentRectTransform == null)
                {
                    _parentRectTransform = transform.parent.gameObject.GetComponent<RectTransform>();
                }

                return _parentRectTransform;
            }
        }

        [SerializeField]
        private Vector2 _initialSize = Vector2.one;
        [SerializeField]
        private ScaleMode _scaleMode = ScaleMode.ScaleByHeight;

        public void Initialize()
        {
            if (ParentRectTransform.GetComponentInParent<Canvas>() is Canvas rootCanvas)
            {
                Canvas.ForceUpdateCanvases();
            }

            // 2. Now, the size should be correct.
            _initialSize = ParentRectTransform.rect.size;
            UpdateScale();
        }
        private void Update()
        {
            if (_initialSize == Vector2.one)
            {
                _initialSize = ParentRectTransform.rect.size;
            }
            UpdateScale();
        }

        private void UpdateScale()
        {
            var size = ParentRectTransform.rect.size;
            float scale;
            if (_scaleMode == ScaleMode.ScaleByWidth)
            {
                scale = size.x / _initialSize.x;
            }
            else
            {
                scale = size.y / _initialSize.y;
            }

            scale = 1.0f;
            // YargLogger.LogDebug($">>INitial size x: {_initialSize.x}, y: {_initialSize.y}, scale calculated: {scale}");
            transform.localScale = new Vector3(scale, scale, 1f);
        }

        public enum ScaleMode
        {
            ScaleByHeight,
            ScaleByWidth
        }
    }
}
