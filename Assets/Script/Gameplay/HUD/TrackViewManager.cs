using System.Collections.Generic;
using System.Transactions;
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
        private HighwayCameraRendering _highwayCameraRendering;

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

        protected override void GameplayAwake()
        {
            _highwayCameraRendering.OnRenderTextureRecreated += InitializeRenderTexture;
        }

        public void CreateVocalTrackView()
        {
            _vocalImage.gameObject.SetActive(true);
        }

        private void InitializeRenderTexture(RenderTexture texture)
        {
            GameManager.VocalTrack.InitializeRenderTexture(_vocalImage, texture);
        }

        public VocalsPlayerHUD CreateVocalsPlayerHUD()
        {
            var go = Instantiate(_vocalHudPrefab, _vocalHudParent);
            return go.GetComponent<VocalsPlayerHUD>();
        }

        public void AddTrackPlayer(TrackPlayer trackPlayer)
        {
            _highwayCameraRendering.AddTrackPlayer(trackPlayer);
        }

        protected override void GameplayDestroy()
        {
            _highwayCameraRendering.OnRenderTextureRecreated -= InitializeRenderTexture;
        }
    }
}