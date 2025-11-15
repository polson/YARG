using System;
using UnityEngine;

namespace YARG.Helpers.UI
{
    public class ScreenSizeDetector : MonoSingleton<ScreenSizeDetector>
    {
        public static event Action<int, int> OnScreenSizeChanged;

        public static bool HasScreenSizeChanged { get; private set; }

        private int _lastWidth;
        private int _lastHeight;

        protected override void SingletonAwake()
        {
            _lastWidth = Screen.width;
            _lastHeight = Screen.height;
            HasScreenSizeChanged = false;
        }

        void Update()
        {
            if (Screen.width != _lastWidth || Screen.height != _lastHeight)
            {
                _lastWidth = Screen.width;
                _lastHeight = Screen.height;
                HasScreenSizeChanged = true;
                OnScreenSizeChanged?.Invoke(_lastWidth, _lastHeight);
            }
        }
    }
}