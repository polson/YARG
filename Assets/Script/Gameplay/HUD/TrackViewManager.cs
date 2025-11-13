using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core.Logging;
using YARG.Gameplay.Player;
using YARG.Gameplay.Visuals;
using YARG.Helpers.Extensions;
using YARG.Player;

namespace YARG.Gameplay.HUD
{
    public class TrackViewManager : GameplayBehaviour
    {
        [Header("Prefabs")]
        [SerializeField]
        private GameObject _trackViewPrefab;
        [SerializeField]
        private GameObject _vocalHudPrefab;
        [SerializeField]
        public HighwayCameraRendering _highwayCameraRendering;

        [Header("References")]
        [SerializeField]
        private RectTransform _vocalImage;
        [SerializeField]
        private Transform _vocalHudParent;
        [SerializeField]
        private CountdownDisplay _vocalsCountdownDisplay;

        [SerializeField]
        HorizontalLayoutGroup _horizontalLayoutGroup;

        private readonly List<TrackView> _trackViews = new();

        public TrackView CreateTrackView(TrackPlayer trackPlayer, YargPlayer player)
        {
            // Create a track view
            var trackView = Instantiate(_trackViewPrefab, transform).GetComponent<TrackView>();
            trackView.Initialize(trackPlayer, _highwayCameraRendering);
            _trackViews.Add(trackView);
            return trackView;
        }

        public void CreateVocalTrackView()
        {
            _vocalImage.gameObject.SetActive(true);

            // Apply the vocal track texture
            GameManager.VocalTrack.InitializeRenderTexture(_vocalImage, _highwayCameraRendering.HighwaysOutputTexture);

            //TODO: -= this
            _highwayCameraRendering.OnRenderTextureRecreated += texture =>
            {
                YargLogger.LogDebug(">>TEXTURE RECREATED");
                GameManager.VocalTrack.InitializeRenderTexture(_vocalImage, texture);
            };
        }

        public VocalsPlayerHUD CreateVocalsPlayerHUD()
        {
            var go = Instantiate(_vocalHudPrefab, _vocalHudParent);
            return go.GetComponent<VocalsPlayerHUD>();
        }
    }
}
