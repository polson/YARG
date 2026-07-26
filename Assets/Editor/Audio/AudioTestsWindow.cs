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
            "ASIO Routing",
            "Master Mixer",
            "BASS Latency"
        };

        private AsioClockTestTab _asioClockTest;
        private AsioMicMonitorTestTab _asioMicMonitorTest;
        private AsioRoutingTestTab _asioRoutingTest;
        private MasterMixerTestTab _masterMixerTest;
        private BassLatencyTestTab _bassLatencyTest;
        private int _activeTab = -1;

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
            _asioRoutingTest = new AsioRoutingTestTab(Repaint);
            _masterMixerTest = new MasterMixerTestTab(Repaint);
            _bassLatencyTest = new BassLatencyTestTab(Repaint, Notify);
            _selectedTab = Mathf.Clamp(_selectedTab, 0, TabNames.Length - 1);
            SwitchTab(_selectedTab);
        }

        private void OnDisable()
        {
            DisableTab(_activeTab);
            _activeTab = -1;
        }

        private void OnGUI()
        {
            int selectedTab = GUILayout.Toolbar(_selectedTab, TabNames);
            if (selectedTab != _selectedTab)
            {
                _selectedTab = selectedTab;
                SwitchTab(_selectedTab);
            }
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
                _asioRoutingTest.Draw();
            }
            else if (_selectedTab == 3)
            {
                _masterMixerTest.Draw();
            }
            else
            {
                _bassLatencyTest.Draw();
            }
        }

        private void SwitchTab(int tab)
        {
            if (_activeTab == tab)
            {
                return;
            }

            DisableTab(_activeTab);
            _activeTab = tab;
            switch (_activeTab)
            {
                case 0:
                    _asioClockTest.Enable();
                    break;
                case 1:
                    _asioMicMonitorTest.Enable();
                    break;
                case 2:
                    _asioRoutingTest.Enable();
                    break;
                case 3:
                    _masterMixerTest.Enable();
                    break;
                case 4:
                    _bassLatencyTest.Enable();
                    break;
            }
        }

        private void DisableTab(int tab)
        {
            switch (tab)
            {
                case 0:
                    _asioClockTest?.Disable();
                    break;
                case 1:
                    _asioMicMonitorTest?.Disable();
                    break;
                case 2:
                    _asioRoutingTest?.Disable();
                    break;
                case 3:
                    _masterMixerTest?.Disable();
                    break;
                case 4:
                    _bassLatencyTest?.Disable();
                    break;
            }
        }

        private void Notify(string message)
        {
            ShowNotification(new GUIContent(message));
        }
    }
}
