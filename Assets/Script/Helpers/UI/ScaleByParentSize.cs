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
        private Vector2 _initialSize = new Vector2(1920, 1080);
        [SerializeField]
        private ScaleMode _scaleMode = ScaleMode.ScaleByHeight;

        private void Update()
        {
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
            YargLogger.LogDebug($">>SCale information: parent size: {size}, initial size: {_initialSize}, scale mode: {_scaleMode}, computed scale: {scale}");
            transform.localScale = new Vector3(scale, scale, 1f);
        }

        public enum ScaleMode
        {
            ScaleByHeight,
            ScaleByWidth
        }
    }
}
