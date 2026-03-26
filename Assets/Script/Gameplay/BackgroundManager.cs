using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Cinemachine;
using Cysharp.Threading.Tasks;
using UniHumanoid;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.UI;
using UnityEngine.Video;
using YARG.Core.IO;
using YARG.Core.Song;
using YARG.Core.Venue;
using YARG.Helpers.Extensions;
using YARG.Settings;
using YARG.Venue;
using YARG.Venue.Characters;
using YARG.Core.Logging;
using ExportType = YARG.Venue.BundleBackgroundManager.ExportType;

#if UNITY_EDITOR
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
#endif

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
using System.Collections.Generic;
#endif

namespace YARG.Gameplay
{
    public class BackgroundManager : GameplayBehaviour, IDisposable
    {
        // e.g. DefaultController.Vocals.Rock.controller
        private const string DEFAULT_ANIMATION_CONTROLLER_PATH = "DefaultAnimations/DefaultController.{0}.{1}.controller";

        private string VIDEO_PATH;

        [SerializeField]
        private VideoPlayer _videoPlayer;

        [SerializeField]
        private RawImage _backgroundImage;

        [SerializeField]
        private Image _backgroundDimmer;

        [SerializeField]
        private RawImage _venueOutput;

        private BackgroundType _type;
        private VenueSource _source;

        private bool _videoStarted = false;
        private bool _videoSeeking = false;

        private float YARGROUND_OFFSET = 50f;

        // These values are relative to the video, not to song time!
        // A negative start time will delay when the video starts, a positive one will set the video position
        // to that value when starting playback at the start of a song.
        private double _videoStartTime;
        // End time cannot be negative; a negative value means it is not set.
        private double _videoEndTime;

        private AssetBundle _characterBundle;

        private BundleBackgroundManager _bundleBackgroundManager;

#if UNITY_EDITOR
        private bool        _usingEditorVenue;
        private string      _editorVenuePath;
        private Scene       _editorVenueScene;
#endif
        // "The Unity message 'Start' has an incorrect signature."
        [SuppressMessage("Type Safety", "UNT0006", Justification = "UniTaskVoid is a compatible return type.")]
        private async UniTaskVoid Start()
        {
            YargLogger.LogInfo("=== [VENUE] BackgroundManager.Start() BEGIN - Venue loading starting ===");
            var totalStopwatch = Stopwatch.StartNew();

            // We don't need to update unless we're using a video
            enabled = false;

            // DON'T activate _venueOutput yet - wait until venue is loaded to avoid gray screen
            // It will be activated after venue is ready

#if UNITY_EDITOR
            if (VenueEditorHelper.IsSceneEnabled())
            {
                if (VenueEditorHelper.TryGetScenePath(out _editorVenuePath))
                {
                    var loadedScene = SceneManager.GetSceneByName(_editorVenuePath);
                    if (loadedScene.IsValid() && loadedScene.isLoaded)
                    {
                        _editorVenueScene = loadedScene;
                    }
                    else
                    {
                        var op = EditorSceneManager.LoadSceneAsyncInPlayMode(
                            _editorVenuePath, new LoadSceneParameters(LoadSceneMode.Additive));

                        await op;
                        _editorVenueScene = SceneManager.GetSceneByPath(_editorVenuePath);
                    }
                }

                if (!_editorVenueScene.IsValid() || !_editorVenueScene.isLoaded)
                {
                    YargLogger.LogFormatError("Failed to load editor venue scene {0}", _editorVenuePath);
                    return;
                }

                BundleBackgroundManager editorBg = null;
                foreach (var go in _editorVenueScene.GetRootGameObjects())
                {
                    editorBg = go.GetComponent<BundleBackgroundManager>();

                    if (editorBg != null)
                    {
                        break;
                    }
                }

                if (editorBg == null)
                {
                    YargLogger.LogFormatError("Scene {0} missing BundleBackgroundManager", _editorVenuePath);
                    return;
                }

                _usingEditorVenue = true;

                var editorRenderers = editorBg.GetComponentsInChildren<Renderer>(true);

                // Song specific textures
                var tm = GetComponent<TextureManager>();
                var songBg = GameManager.Song.LoadBackground();

                foreach (var renderer in editorRenderers)
                {
                    var materials = renderer.materials;

                    for (int i = 0; i < materials.Length; i++)
                    {
                        tm.ProcessMaterial(materials[i], songBg?.Type);
                    }

                    renderer.materials = materials;
                }

                editorBg.SetupVenueCamera(editorBg.gameObject);
                editorBg.LimitVenueLights(editorBg.gameObject);

                // Activate venue output after setup is complete
                _venueOutput.gameObject.SetActive(true);

                if (_videoPlayer != null && _videoPlayer.targetCamera != null)
                {
                    Destroy(_videoPlayer.targetCamera.gameObject);
                }

                _type = BackgroundType.Yarground;

                totalStopwatch.Stop();
                YargLogger.LogFormatInfo("[VENUE] Editor venue loaded in {0}ms total", totalStopwatch.ElapsedMilliseconds);
                return;
            }
#endif

            using var result = VenueLoader.GetVenue(GameManager.Song, out _source);
            if (result == null)
            {
                totalStopwatch.Stop();
                YargLogger.LogFormatInfo("[VENUE] No venue found, exiting in {0}ms", totalStopwatch.ElapsedMilliseconds);
                return;
            }

            var colorDim = _backgroundDimmer.color.WithAlpha(1 - SettingsManager.Settings.SongBackgroundOpacity.Value);

            _backgroundDimmer.color = colorDim;

            var loadStopwatch = Stopwatch.StartNew();
            _type = result.Type;
            switch (_type)
            {
                case BackgroundType.Yarground:
                    YargLogger.LogInfo("[VENUE] Starting yarground load...");
                    LoadYarground(result);
                    break;
                case BackgroundType.Video:
                    YargLogger.LogInfo("[VENUE] Starting video load...");
                    LoadVideoBackground(result);
                    break;
                case BackgroundType.Image:
                    YargLogger.LogInfo("[VENUE] Starting image load...");
                    _backgroundImage.texture = result.Image.LoadTexture(false);
                    _backgroundImage.uvRect = new Rect(0f, 0f, 1f, -1f);
                    _backgroundImage.gameObject.SetActive(true);
                    break;
            }
            loadStopwatch.Stop();
            totalStopwatch.Stop();

            if (_type != BackgroundType.Yarground)
            {
                // Only log total here for non-yarground types since yarground loading is async
                YargLogger.LogFormatInfo("[VENUE] BackgroundManager.Start() for {0} took {1}ms total", _type, totalStopwatch.ElapsedMilliseconds);
            }
            else
            {
                YargLogger.LogFormatInfo("[VENUE] BackgroundManager.Start() sync portion took {0}ms (async loading in progress...)", totalStopwatch.ElapsedMilliseconds);
            }
        }

        private async UniTaskVoid LoadYarground(BackgroundResult result)
        {
            var totalStopwatch = Stopwatch.StartNew();
            YargLogger.LogInfo("[VENUE] LoadYarground() started - checking cache...");

            // Check if venue is in cache (preloaded during difficulty select or from previous song)
            VenuePreloader.PreloadedVenue cached = null;
            if (VenuePreloader.Instance != null &&
                VenuePreloader.Instance.TryGetVenue(GameManager.Song, out cached))
            {
                YargLogger.LogInfo("[VENUE] Using cached venue!");
                var useStopwatch = Stopwatch.StartNew();
                await InitializeFromCached(cached);
                useStopwatch.Stop();
                totalStopwatch.Stop();
                YargLogger.LogFormatInfo("[VENUE] LoadYarground() from cache took {0}ms total", useStopwatch.ElapsedMilliseconds);
                return;
            }

            // No cache available, load normally
            if (VenuePreloader.Instance == null)
            {
                YargLogger.LogWarning("[VENUE] VenuePreloader.Instance is null - preloader not initialized or was destroyed");
            }
            YargLogger.LogInfo("[VENUE] Venue not in cache, loading from disk...");
            AssetBundle bundle;

            // Use LoadFromFileAsync for file paths (faster), fall back to LoadFromStream for SngFile streams
            if (result.FilePath != null)
            {
                var bundleLoadStopwatch = Stopwatch.StartNew();
                bundle = await AssetBundle.LoadFromFileAsync(result.FilePath);
                bundleLoadStopwatch.Stop();
                YargLogger.LogFormatInfo("[VENUE] AssetBundle.LoadFromFileAsync took {0}ms", bundleLoadStopwatch.ElapsedMilliseconds);
            }
            else
            {
                var bundleLoadStopwatch = Stopwatch.StartNew();
                bundle = AssetBundle.LoadFromStream(result.Stream);
                bundleLoadStopwatch.Stop();
                YargLogger.LogFormatInfo("[VENUE] AssetBundle.LoadFromStream took {0}ms", bundleLoadStopwatch.ElapsedMilliseconds);
            }

            _venueOutput.gameObject.SetActive(true);

            // KEEP THIS PATH LOWERCASE
            // Breaks things for other platforms, because Unity
            var assetLoadStopwatch = Stopwatch.StartNew();
            var bg = (GameObject) await bundle.LoadAssetAsync<GameObject>(
                BundleBackgroundManager.BACKGROUND_PREFAB_PATH.ToLowerInvariant());
            assetLoadStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] LoadAssetAsync (background prefab) took {0}ms", assetLoadStopwatch.ElapsedMilliseconds);

            var renderersStopwatch = Stopwatch.StartNew();
            var renderers = bg.GetComponentsInChildren<Renderer>(true);
            renderersStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] GetComponentsInChildren<Renderer> took {0}ms, found {1} renderers",
                renderersStopwatch.ElapsedMilliseconds, renderers.Length);

            AssetBundle shaderBundle = null;

            // Load Metal shaders, if necessary
            var shaderStopwatch = Stopwatch.StartNew();
            shaderBundle = await LoadMetalShaders(bundle, bg, ExportType.Background);
            shaderStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] LoadMetalShaders took {0}ms", shaderStopwatch.ElapsedMilliseconds);

            // Hookup song-specific textures
            var textureManager = GetComponent<TextureManager>();
            // Load SongBackground here to determine if textures need to be replaced
            var textureProcStopwatch = Stopwatch.StartNew();
            var songBackground = GameManager.Song.LoadBackground();
            int materialCount = 0;
            foreach (var renderer in renderers)
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    textureManager.ProcessMaterial(material, songBackground?.Type);
                    materialCount++;
                }
            }
            textureProcStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] Texture processing took {0}ms for {1} materials",
                textureProcStopwatch.ElapsedMilliseconds, materialCount);

            var instantiateStopwatch = Stopwatch.StartNew();
            YargLogger.LogInfo("[VENUE] About to Instantiate venue prefab...");
            var bgInstance = Instantiate(bg);
            instantiateStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] Instantiate venue took {0}ms (venue GameObject now exists)", instantiateStopwatch.ElapsedMilliseconds);

            var bundleBackgroundManager = bgInstance.GetComponent<BundleBackgroundManager>();
            bundleBackgroundManager.Bundle = bundle;
            bundleBackgroundManager.ShaderBundles.Add(shaderBundle);
            YargLogger.LogInfo("[VENUE] About to SetupVenueCamera...");
            bundleBackgroundManager.SetupVenueCamera(bgInstance);
            YargLogger.LogInfo("[VENUE] SetupVenueCamera complete - camera renderer should now be active");
            bundleBackgroundManager.LimitVenueLights(bgInstance);

            // Activate venue output now that venue is loaded and camera is set up
            if (_venueOutput != null)
            {
                YargLogger.LogInfo("[VENUE] Activating _venueOutput GameObject - venue is ready");
                _venueOutput.gameObject.SetActive(true);
            }

            _bundleBackgroundManager = bundleBackgroundManager;

            // Position venue as close to origin as is conveniently possible without wrecking scene view
            SetYargroundOrigin(bgInstance);

            // Destroy the default camera (venue has its own)
            Destroy(_videoPlayer.targetCamera.gameObject);

            if (textureManager.VideoTexFound())
            {
                SetUpVideoTexture(songBackground);
            }

            var charLoadStopwatch = Stopwatch.StartNew();
            await LoadCustomCharacter(bgInstance);
            charLoadStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] LoadCustomCharacter took {0}ms", charLoadStopwatch.ElapsedMilliseconds);

            // Initialize CharacterManager, if it exists
            var charInitStopwatch = Stopwatch.StartNew();
            var characterManager = bgInstance.GetComponentInChildren<CharacterManager>();
            if (characterManager != null)
            {
                characterManager.Initialize();
            }
            charInitStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] CharacterManager.Initialize took {0}ms", charInitStopwatch.ElapsedMilliseconds);

            // Add to cache for future use (restart, next song with same venue)
            // Ensure preloader exists even if it wasn't preloaded
            if (result.FilePath != null)
            {
                if (VenuePreloader.Instance == null)
                {
                    var preloaderGo = new GameObject("VenuePreloader");
                    preloaderGo.AddComponent<VenuePreloader>();
                    YargLogger.LogInfo("[VENUE] Created VenuePreloader GameObject during venue load");
                }
                VenuePreloader.Instance.AddToCache(result.FilePath, bundle, bg, shaderBundle, result, _source);
            }

            totalStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] LoadYarground() TOTAL took {0}ms - venue should now be visible", totalStopwatch.ElapsedMilliseconds);
        }

        private async UniTask InitializeFromCached(VenuePreloader.PreloadedVenue cached)
        {
            YargLogger.LogInfo("[VENUE] InitializeFromCached() started - using cached venue");
            var bg = cached.BackgroundPrefab;
            var bundle = cached.Bundle;
            var shaderBundle = cached.ShaderBundle;

            var renderersStopwatch = Stopwatch.StartNew();
            var renderers = bg.GetComponentsInChildren<Renderer>(true);
            renderersStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] GetComponentsInChildren<Renderer> took {0}ms, found {1} renderers",
                renderersStopwatch.ElapsedMilliseconds, renderers.Length);

            // Hookup song-specific textures
            var textureManager = GetComponent<TextureManager>();
            var textureProcStopwatch = Stopwatch.StartNew();
            var songBackground = GameManager.Song.LoadBackground();
            int materialCount = 0;
            foreach (var renderer in renderers)
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    textureManager.ProcessMaterial(material, songBackground?.Type);
                    materialCount++;
                }
            }
            textureProcStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] Texture processing took {0}ms for {1} materials",
                textureProcStopwatch.ElapsedMilliseconds, materialCount);

            var instantiateStopwatch = Stopwatch.StartNew();
            YargLogger.LogInfo("[VENUE] About to Instantiate venue prefab...");
            var bgInstance = Instantiate(bg);
            instantiateStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] Instantiate venue took {0}ms (venue GameObject now exists)", instantiateStopwatch.ElapsedMilliseconds);

            var bundleBackgroundManager = bgInstance.GetComponent<BundleBackgroundManager>();
            bundleBackgroundManager.Bundle = bundle;
            bundleBackgroundManager.BundlesManagedByCache = true; // Cache manages bundle lifecycle
            if (shaderBundle != null)
            {
                bundleBackgroundManager.ShaderBundles.Add(shaderBundle);
            }
            bundleBackgroundManager.SetupVenueCamera(bgInstance);
            bundleBackgroundManager.LimitVenueLights(bgInstance);

            // Activate venue output after setup is complete
            if (_venueOutput != null)
            {
                YargLogger.LogInfo("[VENUE] Activating _venueOutput GameObject - preloaded venue is ready");
                _venueOutput.gameObject.SetActive(true);
            }

            _bundleBackgroundManager = bundleBackgroundManager;

            // Position venue as close to origin as is conveniently possible without wrecking scene view
            SetYargroundOrigin(bgInstance);

            // Destroy the default camera (venue has its own)
            Destroy(_videoPlayer.targetCamera.gameObject);

            if (textureManager.VideoTexFound())
            {
                SetUpVideoTexture(songBackground);
            }

            var charLoadStopwatch = Stopwatch.StartNew();
            await LoadCustomCharacter(bgInstance);
            charLoadStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] LoadCustomCharacter took {0}ms", charLoadStopwatch.ElapsedMilliseconds);

            // Initialize CharacterManager, if it exists
            var charInitStopwatch = Stopwatch.StartNew();
            var characterManager = bgInstance.GetComponentInChildren<CharacterManager>();
            if (characterManager != null)
            {
                characterManager.Initialize();
            }
            charInitStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] CharacterManager.Initialize took {0}ms", charInitStopwatch.ElapsedMilliseconds);
            YargLogger.LogInfo("[VENUE] InitializeFromCached() complete - cached venue should now be visible");
        }

        private void SetUpVideoTexture(BackgroundResult songBackGround)
        {
            var textureManager = GetComponent<TextureManager>();
            textureManager.CreateVideoTexture();
            if (songBackGround == null || songBackGround.Type == BackgroundType.Yarground)
            {
                return;
            }
            switch (songBackGround.Type)
            {
                case BackgroundType.Video:
                    //set venue source to song to enable video seeking/pausing features
                    _source = VenueSource.Song;
                    //set up videoPlayer to render to venue texture
                    _videoPlayer.renderMode = VideoRenderMode.RenderTexture;
                    _videoPlayer.targetTexture = textureManager.GetVideoTexture(0, 0);

                    LoadVideoBackground(songBackGround);
                    break;
                case BackgroundType.Image:
                    var songTex = songBackGround.Image.LoadTexture(false);
                    //render image background flipped to match video
                    Graphics.Blit(songTex, textureManager.GetVideoTexture(0, 0), new Vector2(1, -1), new Vector2(0, 1));
                    //clean up unused texture
                    Destroy(songTex);
                    return;
            }
        }

        private void LoadVideoBackground(BackgroundResult bg)
        {
            switch (bg.Stream)
            {
                case FileStream fs:
                {
                    _videoPlayer.url = fs.Name;
                    break;
                }
                case SngFileStream sngStream:
                {
                    // UNFORTUNATELY, Videoplayer can't use streams, so video files
                    // MUST BE FULLY DECRYPTED

                    VIDEO_PATH = Path.Combine(Application.persistentDataPath, sngStream.Name);
                    using var tmp = File.OpenWrite(VIDEO_PATH);
                    File.SetAttributes(VIDEO_PATH, File.GetAttributes(VIDEO_PATH) | FileAttributes.Temporary | FileAttributes.Hidden);
                    bg.Stream.CopyTo(tmp);
                    _videoPlayer.url = VIDEO_PATH;
                    break;
                }
            }

            _videoPlayer.enabled = true;
            _videoPlayer.prepareCompleted += OnVideoPrepared;
            _videoPlayer.seekCompleted += OnVideoSeeked;
            _videoPlayer.Prepare();
            enabled = true;
        }

        private void Update()
        {
            if (_videoSeeking)
                return;

            double time = GameManager.SongTime + GameManager.Song.SongOffsetSeconds;
            // Start video
            if (!_videoStarted)
            {
                // Don't start playing the video until the start of the song
                if (time < 0.0)
                    return;

                // Delay until the start time is reached
                if (_source == VenueSource.Song && time < -_videoStartTime)
                    return;

                if (_videoEndTime == 0)
                    return;

                _videoStarted = true;
                _videoPlayer.Play();

                // Disable after starting the video if it's not from the song folder
                // or if video end time is not specified
                if (_source != VenueSource.Song || double.IsNaN(_videoEndTime))
                {
                    enabled = false;
                    return;
                }
            }

            // End video when reaching the specified end time
            if (time + _videoStartTime >= _videoEndTime)
            {
                _videoPlayer.Stop();
                _videoPlayer.enabled = false;
                enabled = false;
            }
        }

        // Some video player properties don't work correctly until
        // it's finished preparing, such as the length
        private void OnVideoPrepared(VideoPlayer player)
        {
            // Start time is considered set if it is greater than 25 ms in either direction
            // End time is only set if it is greater than 0
            // Video will only loop if its length is less than 85% of the song's length
            const double startTimeThreshold = 0.025;
            const double endTimeThreshold = 0;
            const double dontLoopThreshold = 0.85;

            if (_source == VenueSource.Song && !GameManager.Song.VideoLoop)
            {
                _videoStartTime = GameManager.Song.VideoStartTimeSeconds;
                _videoEndTime = GameManager.Song.VideoEndTimeSeconds;

                player.time = _videoStartTime;
                player.playbackSpeed = GameManager.SongSpeed;

                // Only loop the video if it's not around the same length as the song
                if (Math.Abs(_videoStartTime) < startTimeThreshold &&
                    _videoEndTime <= endTimeThreshold &&
                    player.length < GameManager.SongLength * dontLoopThreshold)
                {
                    player.isLooping = true;
                    _videoEndTime = double.NaN;
                }
                else
                {
                    player.isLooping = false;
                    if (_videoEndTime <= 0)
                    {
                        _videoEndTime = player.length;
                    }
                }
            }
            else
            {
                _videoStartTime = 0;
                _videoEndTime = double.NaN;
                player.isLooping = true;
            }
        }

        public void SetTime(double songTime)
        {
            switch (_type)
            {
                case BackgroundType.Video:
                    // Don't seek videos that aren't from the song
                    if (_source != VenueSource.Song)
                        return;

                    double videoTime = songTime + _videoStartTime;
                    if (videoTime < 0f) // Seeking before video start
                    {
                        enabled = true;
                        _videoPlayer.enabled = true;
                        _videoStarted = false;
                        _videoPlayer.Stop();
                    }
                    else if (videoTime >= _videoPlayer.length) // Seeking after video end
                    {
                        enabled = false;
                        _videoPlayer.enabled = false;
                        _videoPlayer.Stop();
                    }
                    else
                    {
                        enabled = false; // Temp disable
                        _videoPlayer.enabled = true;

                        // Hack to ensure the video stays synced to the audio
                        _videoSeeking = true; // Signaling flag; must come first
                        if (SettingsManager.Settings.WaitForSongVideo.Value)
                            GameManager.OverridePause();

                        _videoPlayer.time = videoTime;
                    }
                    break;
            }
        }

        private void OnVideoSeeked(VideoPlayer player)
        {
            if (!_videoSeeking)
                return;

            if (!SettingsManager.Settings.WaitForSongVideo.Value || GameManager.OverrideResume())
                player.Play();

            enabled = !double.IsNaN(_videoEndTime);
            _videoSeeking = false;
        }

        public void SetSpeed(float speed)
        {
            switch (_type)
            {
                case BackgroundType.Video:
                    _videoPlayer.playbackSpeed = speed;
                    break;
            }
        }

        public void SetPaused(bool paused)
        {
            // Pause/unpause video
            if (_videoPlayer.enabled && _videoStarted && !_videoSeeking)
            {
                if (paused)
                {
                    _videoPlayer.Pause();
                }
                else
                {
                    _videoPlayer.Play();
                }
            }

            // The venue is dealt with in the GameManager via Time.timeScale
        }

        private async UniTask LoadCustomCharacter(GameObject venueRoot)
        {
            var stopwatch = Stopwatch.StartNew();

            string characterPath = SettingsManager.Settings.CustomVocalsCharacter.Value;

            if (string.IsNullOrEmpty(characterPath))
            {
                return;
            }

            var charBundleStopwatch = Stopwatch.StartNew();
            var bundle = AssetBundle.LoadFromFile(characterPath);
            charBundleStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] Character bundle LoadFromFile took {0}ms", charBundleStopwatch.ElapsedMilliseconds);

            if (bundle == null)
            {
                return;
            }

            _bundleBackgroundManager.CharacterBundles.Add(bundle);

            var charAssetStopwatch = Stopwatch.StartNew();
            var character = bundle.LoadAsset<GameObject>(BundleBackgroundManager.CHARACTER_PREFAB_PATH.ToLowerInvariant());
            charAssetStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] Character asset LoadAsset took {0}ms", charAssetStopwatch.ElapsedMilliseconds);

            if (character == null)
            {
                YargLogger.LogFormatError("Failed to load character from {0}", characterPath);
                return;
            }

            // Load Metal shaders
            var charShaderStopwatch = Stopwatch.StartNew();
            var shaderBundle = await LoadMetalShaders(bundle, character, ExportType.Character);
            charShaderStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] Character LoadMetalShaders took {0}ms", charShaderStopwatch.ElapsedMilliseconds);

            if (shaderBundle != null)
            {
                _bundleBackgroundManager.ShaderBundles.Add(shaderBundle);
            }

            // Check for an existing animation controller and use default if none is found
            var animator = character.GetComponent<Animator>();
            if (animator != null)
            {
                var controller = animator.runtimeAnimatorController;
                if (controller == null)
                {
                    var genre = GetDefaultGenre(GameManager.Song.Genre);
                    var charType = character.GetComponent<VenueCharacter>().Type;
                    var path = string.Format(DEFAULT_ANIMATION_CONTROLLER_PATH, charType.ToString(), genre);
                    var newController = Resources.Load<RuntimeAnimatorController>(path);
                    if (newController != null)
                    {
                        animator.runtimeAnimatorController = newController;
                    }
                    else
                    {
                        YargLogger.LogFormatError("Failed to load default animation controller for {0}", charType);
                    }
                }
            }

            var newType = character.GetComponent<VenueCharacter>().Type;
            // Find a character of the same type in venueRoot
            GameObject existingCharacter = null;

            var findCharStopwatch = Stopwatch.StartNew();
            var characters = venueRoot.GetComponentsInChildren<VenueCharacter>();
            foreach (var c in characters)
            {
                if (c.Type == newType)
                {
                    existingCharacter = c.gameObject;
                    break;
                }
            }
            findCharStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] Find existing character took {0}ms (found {1} characters total)",
                findCharStopwatch.ElapsedMilliseconds, characters.Length);

            if (existingCharacter == null)
            {
                YargLogger.LogFormatError("Failed to find character of type {0} in venue root", newType);
                return;
            }

            // Replace existingCharacter with the new character
            var existingParent = existingCharacter.transform.parent;

            var replaceCharStopwatch = Stopwatch.StartNew();
            var newCharacter = Instantiate(character, existingParent);
            ReplaceReferences(venueRoot, existingCharacter, newCharacter);
            existingCharacter.SetActive(false);
            Destroy(existingCharacter);

            // Lastly, make sure the new character and all its children are in the Venue layer
            var layerIndex = LayerMask.NameToLayer("Venue");
            SetLayer(newCharacter, layerIndex);
            replaceCharStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] Character replacement took {0}ms", replaceCharStopwatch.ElapsedMilliseconds);

            stopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] LoadCustomCharacter() total took {0}ms", stopwatch.ElapsedMilliseconds);
        }

        private async UniTask<AssetBundle> LoadMetalShaders(AssetBundle bundle, GameObject bg, ExportType type)
        {
            var totalStopwatch = Stopwatch.StartNew();
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            AssetBundle shaderBundle = null;
            var renderers = bg.GetComponentsInChildren<Renderer>(true);
            var metalShaders = new Dictionary<string, Shader>();

            var shaderBundleName = type switch
            {
                ExportType.Character => "Assets/" + BundleBackgroundManager.CHARACTER_SHADER_BUNDLE_NAME,
                ExportType.Background => "Assets/" + BundleBackgroundManager.BACKGROUND_SHADER_BUNDLE_NAME,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };

            var shaderDataStopwatch = Stopwatch.StartNew();
            var shaderBundleData = (TextAsset)await bundle.LoadAssetAsync<TextAsset>(
                shaderBundleName
            );
            shaderDataStopwatch.Stop();

            if (shaderBundleData != null && shaderBundleData.bytes.Length > 0)
            {
                YargLogger.LogFormatInfo("[VENUE] Metal shader bundle data loaded in {0}ms", shaderDataStopwatch.ElapsedMilliseconds);

                var shaderBundleLoadStopwatch = Stopwatch.StartNew();
                shaderBundle = await AssetBundle.LoadFromMemoryAsync(shaderBundleData.bytes);
                shaderBundleLoadStopwatch.Stop();
                YargLogger.LogFormatInfo("[VENUE] Metal shader bundle LoadFromMemoryAsync took {0}ms", shaderBundleLoadStopwatch.ElapsedMilliseconds);

                var allAssets = shaderBundle.LoadAllAssets<Shader>();
                foreach (var shader in allAssets)
                {
                    metalShaders.Add(shader.name, shader);
                }
            }
            else
            {
                YargLogger.LogInfo("Did not find Metal shader bundle");
            }

            // Yarground comes with shaders for dx11/dx12/glcore/vulkan
            // Metal shaders used on OSX come in this separate bundle
            // Update our renderers to use them
            var shaderApplyStopwatch = Stopwatch.StartNew();
            int shaderApplyCount = 0;
            foreach (var renderer in renderers)
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    var shaderName = material.shader.name;
                    if (metalShaders.TryGetValue(shaderName, out var shader))
                    {
                        YargLogger.LogFormatDebug("Found bundled shader {0}", shaderName);
                        // We found shader from Yarground
                        material.shader = shader;
                        shaderApplyCount++;
                    }
                    else
                    {
                        YargLogger.LogFormatDebug("Did not find bundled shader {0}", shaderName);
                        // Fallback to try to find among builtin shaders
                        material.shader = Shader.Find(shaderName);
                    }
                }
            }
            shaderApplyStopwatch.Stop();
            if (shaderApplyCount > 0)
            {
                YargLogger.LogFormatInfo("[VENUE] Applied {0} Metal shaders in {1}ms", shaderApplyCount, shaderApplyStopwatch.ElapsedMilliseconds);
            }

            totalStopwatch.Stop();
            if (shaderBundle != null)
            {
                YargLogger.LogFormatInfo("[VENUE] LoadMetalShaders() total took {0}ms", totalStopwatch.ElapsedMilliseconds);
            }
            return shaderBundle;
#endif
            // Fallback if we're not running on OSX
            totalStopwatch.Stop();
            return null;
        }

        // It would be better if we could replace all references, but I'm not sure how to do that, so I'm fixing up the ones I know how to do
        public void ReplaceReferences(GameObject venueRoot, GameObject oldObject, GameObject newObject)
        {
            Transform hips = null;
            Transform head = null;
            var humanoid = newObject.GetComponent<Humanoid>();
            if (humanoid != null)
            {
                hips = humanoid.Hips;
                head = humanoid.Head;
            }

            // Find references to oldObject.transform anywhere in venueRoot..for now we'll just deal with Cinemachine and Lights having lookat/follow properties
            var lookAts = venueRoot.GetComponentsInChildren<LookAtConstraint>(true);
            var sources = new List<ConstraintSource>();
            foreach (var lookat in lookAts)
            {
                sources.Clear();
                lookat.GetSources(sources);

                for (int i = 0; i < sources.Count; i++)
                {
                    var s = sources[i];
                    if (s.sourceTransform != null && s.sourceTransform.IsChildOf(oldObject.transform))
                    {
                        if (head != null && (s.sourceTransform.gameObject.name.Contains("Head") ||
                            s.sourceTransform.gameObject.name.Contains("Face")))
                        {
                            s.sourceTransform = head;
                        }
                        else if (hips != null && s.sourceTransform.gameObject.name.Contains("Hips"))
                        {
                            s.sourceTransform = hips;
                        }
                        else
                        {
                            s.sourceTransform = newObject.transform;
                        }

                        sources[i] = s;
                    }
                }

                lookat.SetSources(sources);
            }

            var cinemachines = venueRoot.GetComponentsInChildren<CinemachineVirtualCamera>(true);
            foreach (var cinemachine in cinemachines)
            {
                // If we can easily determine face/hips, we use the corresponding transform on the VRM character, otherwise we default to hips if set, otherwise newObject.transform
                // We also use a heuristic based on the camera name so as to make certain existing venues not look stupid on the Vocals Closeup cam
                var follow = cinemachine.Follow;
                if (follow != null && follow.IsChildOf(oldObject.transform))
                {
                    if (head != null &&
                        (follow.gameObject.name.Contains("Face") ||
                         follow.gameObject.name.Contains("Head") ||
                         cinemachine.gameObject.name == "Vocals Closeup" ||
                         cinemachine.gameObject.name.EndsWith("Closeup Head")))
                    {
                        cinemachine.Follow = head;
                    }
                    else if (hips != null)
                    {
                        cinemachine.Follow = hips;
                    }
                    else
                    {
                        cinemachine.Follow = newObject.transform;
                    }
                }

                var lookAt = cinemachine.LookAt;
                if (lookAt != null && lookAt.IsChildOf(oldObject.transform))
                {
                    if (head != null && (lookAt.gameObject.name.Contains("Face") ||
                        lookAt.gameObject.name.Contains("Head") ||
                        cinemachine.gameObject.name == "Vocals Closeup" ||
                        cinemachine.gameObject.name.EndsWith("Closeup Head")))
                    {
                        cinemachine.LookAt = head;
                    }
                    else if (hips != null)
                    {
                        cinemachine.LookAt = hips;
                    }
                    else
                    {
                        cinemachine.LookAt = newObject.transform;
                    }
                }
            }
        }

        private void SetLayer(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
            {
                SetLayer(child.gameObject, layer);
            }
        }

        private void SetYargroundOrigin(GameObject venueRoot)
        {
            // Calculate bounds for everything in venueRoot
            venueRoot.transform.localPosition = Vector3.zero;
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            var children = venueRoot.GetComponentsInChildren<Renderer>(true);
            foreach (var child in children)
            {
                bounds.Encapsulate(child.bounds);
            }

            var sizeX = bounds.size.x;
            var sizeZ = bounds.size.z;

            var offsetX = (sizeX * 0.5f) + YARGROUND_OFFSET;
            var offsetZ = (sizeZ * 0.5f) + YARGROUND_OFFSET;

            // New origin places maxZ and maxX at -50
            venueRoot.transform.position = new Vector3(-offsetX, 0, -offsetZ);
        }

        // TODO: Move this to Genrelizer or sth and implement
        public static string GetDefaultGenre(string realGenre)
        {
            return "Generic";
        }

        public void Dispose()
        {
            if (VIDEO_PATH != null)
            {
                File.Delete(VIDEO_PATH);
                VIDEO_PATH = null;
            }

#if UNITY_EDITOR
            if (_usingEditorVenue)
            {
                SceneManager.UnloadSceneAsync(_editorVenueScene);
            }
#endif
        }

        ~BackgroundManager()
        {
            Dispose();
        }
    }
}
