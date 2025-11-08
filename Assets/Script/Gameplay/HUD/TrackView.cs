using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core.Engine;
using YARG.Core.Logging;
using YARG.Gameplay.Player;
using YARG.Gameplay.Visuals;
using YARG.Player;
using YARG.Helpers.UI;
using YARG.Settings;

namespace YARG.Gameplay.HUD
{
    public class TrackView : MonoBehaviour
    {
        [SerializeField]
        private RectTransform _topElementContainer;
        [SerializeField]
        private RectTransform _centerElementContainer;

        [Space]
        [SerializeField]
        private SoloBox _soloBox;
        [SerializeField]
        private TextNotifications _textNotifications;
        [SerializeField]
        private CountdownDisplay _countdownDisplay;
        [SerializeField]
        private PlayerNameDisplay _playerNameDisplay;

        private TrackPlayer _trackPlayer;
        private HighwayCameraRendering      _highwayRenderer;

        //TODO: serialize
        private RectTransform _scaleContainer;

        public void Initialize(TrackPlayer trackPlayer, HighwayCameraRendering highwayRenderer)
        {
            _trackPlayer     = trackPlayer;
            _highwayRenderer = highwayRenderer;
        }

        private void Start()
        {
            _scaleContainer = transform.GetChild(0).GetComponent<RectTransform>();
        }

        public void UpdateHUDPosition(int highwayIndex, int highwayCount)
        {
            Vector3 worldPos = _trackPlayer.transform.position;
            Vector2 viewportPos = _highwayRenderer.WorldToViewport(worldPos, highwayIndex);
            float screenY = (1.0f - viewportPos.y) * Screen.height;

            // --- X Offset Calculations (as you had them) ---
            // var tiltMultiplier = SettingsManager.Settings.HighwayTiltMultiplier.Value / 4;
            // var xOffsetWorld = HighwayCameraRendering.GetMultiplayerXOffset(highwayIndex, highwayCount, tiltMultiplier);
            // var xOffsetViewport = _highwayRenderer.WorldToViewport(worldPos, highwayIndex).x - 0.5f;
            // var xOffsetScreen = xOffsetViewport * Screen.width;
            // YargLogger.LogDebug($">> x offsets: current position: {rect.position.x} world {xOffsetWorld}, viewport {xOffsetViewport}, screen {xOffsetScreen}");
            float screenX = (viewportPos.x) * Screen.width;

            YargLogger.LogDebug($">>Viewport x: {viewportPos.x}, viewport y: {viewportPos.y} screenX: {screenX}, screenY: {screenY}");

            _scaleContainer.position = new Vector2(screenX, screenY);
        }

        public void UpdateCountdown(double countdownLength, double endTime)
        {
            _countdownDisplay.UpdateCountdown(countdownLength, endTime);
        }

        public void StartSolo(SoloSection solo)
        {
            _soloBox.StartSolo(solo);

            // No text notifications during the solo
            _textNotifications.SetActive(false);
        }

        public void EndSolo(int soloBonus)
        {
            _soloBox.EndSolo(soloBonus, () =>
            {
                // Show text notifications again
                _textNotifications.SetActive(true);
            });
        }

        public void UpdateNoteStreak(int streak)
        {
            _textNotifications.UpdateNoteStreak(streak);
        }

        public void ShowNewHighScore()
        {
            _textNotifications.ShowNewHighScore();
        }

        public void ShowFullCombo()
        {
            _textNotifications.ShowFullCombo();
        }

        public void ShowHotStart()
        {
            _textNotifications.ShowHotStart();
        }

        public void ShowBassGroove()
        {
            _textNotifications.ShowBassGroove();
        }

        public void ShowStarPowerReady()
        {
            _textNotifications.ShowStarPowerReady();
        }

        public void ShowStrongFinish()
        {
            _textNotifications.ShowStrongFinish();
        }

        public void ShowPlayerName(YargPlayer player)
        {
            _playerNameDisplay.ShowPlayer(player);
        }

        public void ForceReset()
        {
            _textNotifications.SetActive(true);

            _soloBox.ForceReset();
            _textNotifications.ForceReset();
            _countdownDisplay.ForceReset();
        }
    }
}
