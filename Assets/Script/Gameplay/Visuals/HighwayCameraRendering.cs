using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using YARG.Core.Logging;
using YARG.Gameplay.Player;
using YARG.Settings;

namespace YARG.Gameplay.Visuals
{
    [ExecuteInEditMode]
    [RequireComponent(typeof(Camera))]
    public class HighwayCameraRendering : MonoBehaviour
    {
        public const int MAX_MATRICES = 32;

        [SerializeField]
        private RawImage _highwaysOutput;

        private List<Camera>  _cameras          = new();
        private List<Vector3> _highwayPositions = new();
        private List<float>   _raisedRotations  = new();

        private Camera _renderCamera;

        public  RenderTexture        HighwaysOutputTexture { get; private set; }
        private RenderTexture        _highwaysAlphaTexture;
        private ScriptableRenderPass _fadeCalcPass;

        private float[] _curveFactors = new float[MAX_MATRICES];
        private float[] _zeroFadePositions = new float[MAX_MATRICES];
        private float[] _fadeSize = new float[MAX_MATRICES];
        private float[] _fadeParams = new float[MAX_MATRICES * 2];
        private Matrix4x4[] _camViewMatrices = new Matrix4x4[MAX_MATRICES];
        private Matrix4x4[] _camInvViewMatrices = new Matrix4x4[MAX_MATRICES];
        private Matrix4x4[] _camProjMatrices = new Matrix4x4[MAX_MATRICES];
        private float[] _laneScales             = new float[MAX_MATRICES];

        private int     _lastScreenWidth        = 0;
        private int     _lastScreenHeight       = 0;
        private bool    _needsTextureRecreation = false;

        public static readonly int YargHighwaysNumberID = Shader.PropertyToID("_YargHighwaysN");
        public static readonly int YargHighwayCamViewMatricesID = Shader.PropertyToID("_YargCamViewMatrices");
        public static readonly int YargHighwayCamInvViewMatricesID = Shader.PropertyToID("_YargCamInvViewMatrices");
        public static readonly int YargHighwayCamProjMatricesID = Shader.PropertyToID("_YargCamProjMatrices");
        public static readonly int YargCurveFactorsID = Shader.PropertyToID("_YargCurveFactors");
        public static readonly int YargFadeParamsID = Shader.PropertyToID("_YargFadeParams");
        public static readonly int YargHighwaysAlphaTextureID = Shader.PropertyToID("_YargHighwaysAlphaMask");

        // On a 9:16 screen, this will be ~0.316.
        // On a 16:9 screen, this will be 1.0.
        public float AspectCorrectionFactor => Screen.width / (float)Screen.height / (16f / 9f);

        public event Action<RenderTexture> OnRenderTextureRecreated;

        public RenderTexture CreateHighwayOutputTexture()
        {
            // Set up render texture
            var descriptor = new RenderTextureDescriptor(
                Screen.width, Screen.height,
                RenderTextureFormat.DefaultHDR);
            descriptor.mipCount = 0;
            HighwaysOutputTexture = new RenderTexture(descriptor);
            _highwaysOutput.texture = HighwaysOutputTexture;
            OnRenderTextureRecreated?.Invoke(HighwaysOutputTexture);
            return HighwaysOutputTexture;
        }

        public Vector2 WorldToViewport(Vector3 positionWS, int index)
        {
            Vector4 clipSpacePos = (_camProjMatrices[index] * _camViewMatrices[index]) * new Vector4(positionWS.x, positionWS.y, positionWS.z, 1.0f);
            // Perspective divide to get NDC
            float ndcX = clipSpacePos.x / clipSpacePos.w;
            float ndcY = clipSpacePos.y / clipSpacePos.w;

            // NDC [-1, 1] → Viewport [0, 1]
            float viewportX = (ndcX + 1.0f) * 0.5f;
            float viewportY = (ndcY + 1.0f) * 0.5f;

            Vector2 viewportPos = new Vector2(viewportX, viewportY);
            return viewportPos;
        }

        private Vector2 CalculateFadeParams(int index, Vector3 trackPosition, float ZeroFadePosition, float FadeSize)
        {
            var worldZeroFadePosition = new Vector3(trackPosition.x, trackPosition.y, ZeroFadePosition - FadeSize);
            var worldFullFadePosition = new Vector3(trackPosition.x, trackPosition.y, ZeroFadePosition);

            // Use the individual highway camera instead of the main render camera
            var highwayCamera = _cameras[index];
            Plane farPlane = new Plane();

            farPlane.SetNormalAndPosition(highwayCamera.transform.forward, worldZeroFadePosition);
            var fadeEnd = Mathf.Abs(farPlane.GetDistanceToPoint(highwayCamera.transform.position));

            farPlane.SetNormalAndPosition(highwayCamera.transform.forward, worldFullFadePosition);
            var fadeStart = Mathf.Abs(farPlane.GetDistanceToPoint(highwayCamera.transform.position));

            // Fix: fadeStart should be the smaller distance (closer to camera), fadeEnd should be larger
            // Swap them if they're backwards
            if (fadeStart > fadeEnd)
            {
                (fadeStart, fadeEnd) = (fadeEnd, fadeStart);
            }

            return new Vector2(fadeStart, fadeEnd);
        }

        private void RecalculateCameraBounds()
        {
            float maxWorld = float.NaN;
            float minWorld = float.NaN;
            foreach (var position in _highwayPositions)
            {
                // This doesn't matter too much as long
                // as everything fits. This is just for frustrum culling.
                var x = position.x;
                if (float.IsNaN(maxWorld) || maxWorld < x + 1)
                {
                    maxWorld = x + 1;
                }
                if (float.IsNaN(minWorld) || minWorld > x - 1)
                {
                    minWorld = x - 1;
                }
            }

            _renderCamera.transform.position = _renderCamera.transform.position.WithX((minWorld + maxWorld) / 2);
            float safeAspect = Mathf.Max(_renderCamera.aspect, 0.001f);
            float requiredHalfWidth = Math.Max(25, (maxWorld - minWorld) / safeAspect / 2f);
            _renderCamera.orthographicSize = requiredHalfWidth;
        }

        public void RecalculateScaleFactors()
        {
            if (_cameras.Count == 0)
            {
                return;
            }

            //First pass, just scale according to aspect ratio, then recalc matrices
            for (int i = 0; i < _cameras.Count; i++)
            {
                _laneScales[i] = AspectCorrectionFactor;
            }
            UpdateCameraProjectionMatrices();

            // Second pass, use screen width and height of the lane to adjust scale again
            for (int i = 0; i < _cameras.Count; i++)
            {
                var raisedRotation = _raisedRotations[i];
                Vector2 trackSize = GetTrackScreenSize(i, raisedRotation);
                float trackWidth = trackSize.x;
                float trackHeight = trackSize.y;

                float targetScreenWidth = _cameras.Count == 1
                    // Special case for single player
                    ? Math.Min(Screen.width, trackWidth)
                    // For multiple lanes, cap to 45% of screen or 90% of max lane width
                    : Math.Min(Screen.width * 0.45f, (float)Screen.width / _cameras.Count * 0.90f);

                float scaleFactorWidth = targetScreenWidth / trackWidth;

                // Also calculate scale factor needed to fit within 50% of screen height
                float targetScreenHeight = Screen.height * 0.5f;
                float scaleFactorHeight = targetScreenHeight / trackHeight;

                // Use the smaller scale factor
                float scaleFactor = Math.Min(scaleFactorWidth, scaleFactorHeight);
                _laneScales[i] *= scaleFactor;
            }
        }

        // This is only directly used for fake track player really
        // Rest should go through AddPlayer
        public void AddPlayerParams(Vector3 position, Camera TrackCamera, float CurveFactor, float ZeroFadePosition, float FadeSize, float raisedRotation)
        {
            var index = _cameras.Count;

            _cameras.Add(TrackCamera);
            _raisedRotations.Add(raisedRotation);
            _highwayPositions.Add(position);

            RecalculateScaleFactors();
            UpdateCameraProjectionMatrices();
            UpdateCurveFactor(CurveFactor, index);
            UpdateFadeParams(index, ZeroFadePosition, FadeSize);
        }

        public void UpdateCurveFactor(float CurveFactor, int index)
        {
            _curveFactors[index] = CurveFactor;
            Shader.SetGlobalFloatArray(YargCurveFactorsID, _curveFactors);
        }

        private void RecalculateFadeParams()
        {
            for (int index = 0; index < _cameras.Count; ++index)
            {
                var fadeParams = CalculateFadeParams(index, _highwayPositions[index], _zeroFadePositions[index], _fadeSize[index]);
                _fadeParams[index * 2] = fadeParams.x;
                _fadeParams[index * 2 + 1] = fadeParams.y;

            }
            Shader.SetGlobalFloatArray(YargFadeParamsID, _fadeParams);
        }

        public void UpdateFadeParams(int index, float ZeroFadePosition, float FadeSize)
        {
            _fadeSize[index] = FadeSize > 0.0 ? FadeSize : 0.0001f;
            _zeroFadePositions[index] = ZeroFadePosition;
            RecalculateFadeParams();
        }

        public void AddTrackPlayer(TrackPlayer trackPlayer)
        {
            // This effectively disables rendering it but keeps components active
            var cameraData = trackPlayer.TrackCamera.GetUniversalAdditionalCameraData();
            cameraData.renderType = CameraRenderType.Overlay;

            AddPlayerParams(trackPlayer.transform.position, trackPlayer.TrackCamera, trackPlayer.Player.CameraPreset.CurveFactor, trackPlayer.ZeroFadePosition, trackPlayer.FadeSize, trackPlayer.Player.CameraPreset.Rotation);
            RecalculateCameraBounds();
        }

        private void OnEnable()
        {
            _renderCamera = GetComponent<Camera>();

            if (_highwaysOutput != null)
            {
                _renderCamera.targetTexture = CreateHighwayOutputTexture();
            }

            if (_highwaysAlphaTexture == null)
            {
                ResetHighwayAlphaTexture();
            }

            if (_fadeCalcPass == null)
            {
                _fadeCalcPass = new FadePass(this);
            }

            Shader.SetGlobalInteger(YargHighwaysNumberID, 0);
            RenderPipelineManager.beginCameraRendering += OnPreCameraRender;
            RenderPipelineManager.endCameraRendering += OnEndCameraRender;
        }

        private void ResetHighwayAlphaTexture()
        {
            _highwaysAlphaTexture?.Release();
            float scaling = 1.0f;
            var descriptor = new RenderTextureDescriptor(
                (int) (Screen.width * scaling), (int) (Screen.height * scaling),
                RenderTextureFormat.RFloat);
            descriptor.mipCount = 0;
            _highwaysAlphaTexture = new RenderTexture(descriptor);
            Shader.SetGlobalTexture(YargHighwaysAlphaTextureID, _highwaysAlphaTexture);
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnPreCameraRender;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRender;
        }

        private void OnEndCameraRender(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam != _renderCamera)
            {
                return;
            }
            Shader.SetGlobalInteger(YargHighwaysNumberID, 0);
        }

        private void OnPreCameraRender(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam != _renderCamera)
            {
                return;
            }

            if (_cameras.Count == 0)
            {
                return;
            }

            RecalculateFadeParams();
            if (HighwaysOutputTexture != null)
            {
                if (Screen.width != _lastScreenWidth || Screen.height != _lastScreenHeight)
                {
                    _lastScreenWidth = Screen.width;
                    _lastScreenHeight = Screen.height;

                    foreach (var camera in _cameras)
                    {
                        camera.aspect = (float) Screen.width / Screen.height;
                    }

                    //TODO: when screen size changes, we should always recreate the texture, but we should not always be doing the other stuff if there are no highways
                    //TODO: move text recreation into its own thing
                    _needsTextureRecreation = true;
                    RecalculateScaleFactors();
                    UpdateCameraProjectionMatrices();
                    RecalculateCameraBounds();
                }
            }

            for (int i = 0; i < _cameras.Count; ++i)
            {
                var camera = _cameras[i];

                float multiplayerXOffset = GetMultiplayerXOffset(i, _cameras.Count,
                    -1f * SettingsManager.Settings.HighwayTiltMultiplier.Value);
                OffsetLocalPosition(camera.transform, multiplayerXOffset);

                _camViewMatrices[i] = camera.worldToCameraMatrix;
                _camInvViewMatrices[i] = camera.cameraToWorldMatrix;
            }

            Shader.SetGlobalMatrixArray(YargHighwayCamViewMatricesID, _camViewMatrices);
            Shader.SetGlobalMatrixArray(YargHighwayCamInvViewMatricesID, _camInvViewMatrices);
            Shader.SetGlobalInteger(YargHighwaysNumberID, _cameras.Count);
            var renderer = _renderCamera.GetUniversalAdditionalCameraData().scriptableRenderer;
            renderer.EnqueuePass(_fadeCalcPass);
        }


        private void LateUpdate()
        {
            if (!_needsTextureRecreation)
            {
                return;
            }

            _needsTextureRecreation = false;
            HighwaysOutputTexture.Release();
            HighwaysOutputTexture.DiscardContents();
            _renderCamera.targetTexture = CreateHighwayOutputTexture();
            ResetHighwayAlphaTexture();
        }

        public void UpdateCameraProjectionMatrices()
        {
            for (int i = 0; i < _cameras.Count; ++i)
            {
                var camera = _cameras[i];
                _camViewMatrices[i] = camera.worldToCameraMatrix;
                _camInvViewMatrices[i] = camera.cameraToWorldMatrix;
                var projMatrix = GetModifiedProjectionMatrix(camera.projectionMatrix,
                    i, _cameras.Count, _laneScales[i]);
                _camProjMatrices[i] = GL.GetGPUProjectionMatrix(projMatrix, SystemInfo.graphicsUVStartsAtTop);
                Shader.SetGlobalMatrixArray(YargHighwayCamProjMatricesID, _camProjMatrices);
            }
        }

        /// <summary>
        /// Builds a post-projection matrix that applies NDC-space scaling and offset,
        /// used to tile multiple viewports side-by-side in clip space.
        /// </summary>
        /// <param name="index">The index of the highway [0, N-1]</param>
        /// <param name="highwayCount">Total number of highways (N)</param>
        /// <param name="highwayScale">Scale of each highway in NDC (e.g. 1.0 means full size)</param>
        public static Matrix4x4 GetPostProjectionMatrix(int index, int highwayCount, float highwayScale)
        {
            if (highwayCount < 1)
                return Matrix4x4.identity;

            // Divide screen into N equal regions: [-1, 1] => 2.0 width
            float laneWidth = 2.0f / highwayCount; // NDC horizontal span is [-1, 1] → 2.0
            float centerX = -1.0f + laneWidth * (index + 0.5f);
            float offsetX = centerX + GetMultiplayerXOffset(index, highwayCount,
                -1f * SettingsManager.Settings.HighwayTiltMultiplier.Value / highwayCount);
            float offsetY = -1.0f + highwayScale; // Offset down if scaled vertically

            // This matrix modifies the output of clip space before perspective divide
            // Performs: clip.xy = clip.xy * scale + offset * clip.w
            Matrix4x4 postProj = Matrix4x4.identity;

            postProj.m00 = highwayScale;
            postProj.m11 = highwayScale;
            postProj.m03 = offsetX;
            postProj.m13 = offsetY;

            return postProj;
        }

        /// <summary>
        /// Generates the modified projection matrix (postProj * camProj).
        /// </summary>
        public static Matrix4x4 GetModifiedProjectionMatrix(Matrix4x4 camProj, int index, int highwayCount, float highwayScale)
        {
            Matrix4x4 postProj = GetPostProjectionMatrix(index, highwayCount, highwayScale);
            return postProj * camProj; // HLSL-style: mul(postProj, proj)
        }

        // Calculate Alpha mask for the highways rt
        private sealed class FadePass : ScriptableRenderPass
        {
            private ProfilingSampler _ProfilingSampler = new ProfilingSampler("CalcFadeAlphaMask");
            private CommandBuffer _cmd;
            private HighwayCameraRendering _highwayCameraRendering;
            private Material _material;

            public FadePass(HighwayCameraRendering highCamRend)
            {
                _highwayCameraRendering = highCamRend;
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
                _material = new Material(Shader.Find("HighwaysAlphaMask"));
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                ScriptableRenderer renderer = renderingData.cameraData.renderer;
                CommandBuffer cmd = CommandBufferPool.Get("CalcFadeAlphaMask");

                using (new ProfilingScope(cmd, _ProfilingSampler))
                {
                    cmd.SetRenderTarget(_highwayCameraRendering._highwaysAlphaTexture);
                    var shaderTagIds = new ShaderTagId[] { new ShaderTagId("UniversalForward") };
                    var desc = new RendererListDesc(shaderTagIds, renderingData.cullResults, renderingData.cameraData.camera)
                    {
                        sortingCriteria = SortingCriteria.RenderQueue,
                        renderQueueRange = RenderQueueRange.all,
                        overrideMaterial = _material
                    };

                    var rendererList = context.CreateRendererList(desc);
                    //The RenderingUtils.fullscreenMesh argument specifies that the mesh to draw is a quad.
                    cmd.DrawRendererList(rendererList);
                }
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                CommandBufferPool.Release(cmd);
            }
        }

        // Offset is defined from -1f to 1f
        public static float GetMultiplayerXOffset(int playerIndex, int totalPlayers, float magnitude)
        {
            // No need to offset if only one or fewer players
            if (totalPlayers < 2)
            {
                return 0f;
            }

            // Take segments = (n - 1); e.g., if 3 players, have 3 highways with 2 separations
            float segmentSize = 2f / (totalPlayers - 1);

            // Offset so that the second player out three players is centered
            return magnitude * (-1f + playerIndex * segmentSize);
        }

        public static void OffsetLocalPosition(Transform transform, float xOffset)
        {
            transform.localPosition = new Vector3(xOffset, transform.localPosition.y, transform.localPosition.z);
        }

        /// <summary>
        /// Calculates the width and height of the visible track in screen space (pixels).
        /// This considers the top of the track to be the zero fade position.
        /// </summary>
        /// <param name="cameraIndex">The index of the camera to use for the calculation.</param>
        /// <param name="camRotation">Optional camera rotation (X-axis) to apply during calculation.
        /// If null, uses the camera's current rotation.
        /// This is useful for knowing the screen size of the raised track ahead of time</param>
        /// <returns>A Vector2 containing the lane width (x) and height (y) in screen pixels or Vector2.zero if the camera index is invalid.</returns>
        public Vector2 GetTrackScreenSize(int cameraIndex, float? camRotation = null)
        {
            if (cameraIndex < 0 || cameraIndex >= _cameras.Count)
            {
                YargLogger.LogError($"Invalid camera index: {cameraIndex}");
                return Vector2.zero;
            }

            var camera = _cameras[cameraIndex];
            var trackPosition = _highwayPositions[cameraIndex];
            var originalRotation = camera.transform.localRotation;

            // Apply custom rotation for the calculation if needed (e.g. for raised highway position)
            if (camRotation.HasValue)
            {
                var newRotation = Quaternion.Euler(new Vector3().WithX(camRotation.Value));
                camera.transform.localRotation = newRotation;
                _camViewMatrices[cameraIndex] = camera.worldToCameraMatrix;
            }

            // Get the world space positions of the track corners assuming the screen bottom is the widest part of the track
            float halfWidth = TrackPlayer.TRACK_WIDTH / 2f;
            var trackBottom = FindTrackBottom(camera, trackPosition);
            var trackTop = _zeroFadePositions[cameraIndex]; // Use zero fade position for the top

            Vector3 bottomLeft = new Vector3(trackPosition.x - halfWidth, trackPosition.y, trackBottom);
            Vector3 bottomRight = new Vector3(trackPosition.x + halfWidth, trackPosition.y, trackBottom);
            Vector3 topLeft = new Vector3(trackPosition.x - halfWidth, trackPosition.y, trackTop);

            // Viewport space
            Vector2 viewportBottomLeft = WorldToViewport(bottomLeft, cameraIndex);
            Vector2 viewportBottomRight = WorldToViewport(bottomRight, cameraIndex);
            Vector2 viewportTopLeft = WorldToViewport(topLeft, cameraIndex);
            float viewportWidth = Mathf.Abs(viewportBottomRight.x - viewportBottomLeft.x);
            float viewportHeight = Mathf.Abs(viewportTopLeft.y - viewportBottomLeft.y);

            // Screen space
            float widthPixels = viewportWidth * Screen.width;
            float heightPixels = viewportHeight * Screen.height;

            // Restore the original camera rotation
            camera.transform.localRotation = originalRotation;
            _camViewMatrices[cameraIndex] = camera.worldToCameraMatrix;

            return new Vector2(widthPixels, heightPixels);
        }

        public Vector2 GetTrackDepthByPercent(int trackIndex, float percent)
        {
            if (trackIndex < 0 || trackIndex >= _cameras.Count)
            {
                YargLogger.LogError($"Invalid track index: {trackIndex}");
                return Vector2.zero;
            }

            var camera = _cameras[trackIndex];
            var trackPosition = _highwayPositions[trackIndex];

            // Calculate the bottom and top z positions of the track
            float trackBottom = FindTrackBottom(camera, trackPosition);

            float trackTop = _zeroFadePositions[trackIndex];

            YargLogger.LogDebug($">>Track bottom and top z positions: {trackBottom}, {trackTop}");

            // Calculate the z position at the given percent (0% = bottom, 100% = top)
            float zPositionAtPercent = Mathf.LerpUnclamped(trackBottom, trackTop, percent);

            // Create a world position at the center of the track at the calculated z
            Vector3 worldPositionAtPercent = new Vector3(trackPosition.x, trackPosition.y, zPositionAtPercent);

            // Convert to viewport space
            Vector2 viewportPosition = WorldToViewport(worldPositionAtPercent, trackIndex);

            // Convert viewport space to screen space
            float screenX = viewportPosition.x * Screen.width;
            float screenY = (1.0f - viewportPosition.y) * Screen.height;

            return new Vector2(screenX, screenY);
        }

        // Find the actual bottom z value of the track by raycasting from the bottom center of the screen.  Some camera presets might have the bottom
        // of the track off screen.
        private float FindTrackBottom(Camera camera, Vector3 trackPosition)
        {
            var trackPlane = new Plane(Vector3.up, new Vector3(0, trackPosition.y, 0));
            var bottomRay = camera.ViewportPointToRay(new Vector3(trackPosition.x, 0f, 0f));
            if (!trackPlane.Raycast(bottomRay, out var enter))
            {
                //Track is off the screen at bottom position, default to the position as a best guess
                return trackPosition.z;
            }

            var bottomIntersection = bottomRay.GetPoint(enter);
            YargLogger.LogDebug($">>Found track bottom at z={bottomIntersection.z}, trackY={trackPosition.y}");
            return bottomIntersection.z;
        }
    }
}
