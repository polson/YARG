using UnityEditor;
using UnityEngine;

namespace YARG.Editor
{
    public sealed class AudioTestsWindow : EditorWindow
    {
        private static readonly Vector2 DefaultWindowSize = new(700, 800);
        private static readonly string[] TabNames =
        {
            "ASIO Clock",
            "ASIO Mic Monitor",
            "Master Mixer",
            "BASS Latency"
        };

        private AsioClockTestTab _asioClockTest;
        private AsioMicMonitorTestTab _asioMicMonitorTest;
        private MasterMixerTestTab _masterMixerTest;
        private BassLatencyTestTab _bassLatencyTest;

        [SerializeField]
        private int _selectedTab;

        [MenuItem("YARG/Audio Tests")]
        private static void Open()
        {
            var window = GetWindow<AudioTestsWindow>("Audio Tests");
            window.minSize = DefaultWindowSize;
            window.position = new Rect(window.position.position, DefaultWindowSize);
            window.Show();
        }

        private void OnEnable()
        {
            minSize = DefaultWindowSize;
            _asioClockTest = new AsioClockTestTab(Repaint, Notify);
            _asioMicMonitorTest = new AsioMicMonitorTestTab(Repaint);
            _masterMixerTest = new MasterMixerTestTab(Repaint);
            _bassLatencyTest = new BassLatencyTestTab(Repaint, Notify);
            _asioClockTest.Enable();
            _asioMicMonitorTest.Enable();
            _masterMixerTest.Enable();
            _bassLatencyTest.Enable();
        }

        private void OnDisable()
        {
            _asioClockTest?.Disable();
            _asioMicMonitorTest?.Disable();
            _masterMixerTest?.Disable();
            _bassLatencyTest?.Disable();
        }

        private void OnGUI()
        {
            _selectedTab = GUILayout.Toolbar(_selectedTab, TabNames);
            EditorGUILayout.Space();

            if (_selectedTab == 0)
            {
                _asioClockTest.Draw();
            }
            else if (_selectedTab == 1)
            {
                _asioMicMonitorTest.Draw();
            }
            else if (_selectedTab == 2)
            {
                _masterMixerTest.Draw();
            }
            else
            {
                _bassLatencyTest.Draw();
            }
        }

        private void Notify(string message)
        {
            ShowNotification(new GUIContent(message));
        }
    }
}
