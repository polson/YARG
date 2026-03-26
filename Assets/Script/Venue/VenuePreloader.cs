using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core.Song;
using YARG.Core.Logging;
using YARG.Core.Venue;
using YARG.Settings;
using YARG.Venue;

namespace YARG.Gameplay
{
    /// <summary>
    /// Preloads and caches venues to reduce loading time when gameplay starts.
    /// Venues are cached by file path and persist across songs until the cache is full.
    /// </summary>
    public class VenuePreloader : MonoSingleton<VenuePreloader>
    {
        public class PreloadedVenue
        {
            public AssetBundle Bundle;
            public GameObject BackgroundPrefab;
            public AssetBundle ShaderBundle;
            public BackgroundResult Result;
            public VenueSource Source;
            public string CacheKey;
            public DateTime LastAccessed;
        }

        // Cache settings
        private const int MAX_CACHED_VENUES = 3; // ~30MB for 3 venues
        private const int MAX_CACHED_VENUES_HIGH_MEMORY = 5; // ~50MB for 5 venues

        private readonly Dictionary<string, PreloadedVenue> _venueCache = new();
        private SongEntry _preloadedSong;
        private int _maxCacheSize = MAX_CACHED_VENUES;

        public bool IsPreloaded => _preloadedSong != null && _venueCache.ContainsKey(GetVenueCacheKey(_preloadedSong));
        public int CacheCount => _venueCache.Count;
        public int MaxCacheSize => _maxCacheSize;

        private void Awake()
        {
            // Persist across scene transitions so cached venues survive
            DontDestroyOnLoad(gameObject);
            YargLogger.LogInfo("[VENUE PRELOAD] VenuePreloader initialized with DontDestroyOnLoad");
        }

        private void Start()
        {
            // Adjust cache size based on system memory (rough heuristic)
            // More than 8GB RAM = use larger cache
            if (SystemInfo.systemMemorySize > 8000)
            {
                _maxCacheSize = MAX_CACHED_VENUES_HIGH_MEMORY;
                YargLogger.LogFormatInfo("[VENUE PRELOAD] High memory system detected ({0}MB), cache size set to {1}",
                    SystemInfo.systemMemorySize, _maxCacheSize);
            }
        }

        /// <summary>
        /// Gets a cache key for a venue based on its file path.
        /// This allows multiple songs to share the same cached venue.
        /// </summary>
        private string GetVenueCacheKey(SongEntry song)
        {
            using var result = VenueLoader.GetVenue(song, out var source);
            if (result == null || result.Type != BackgroundType.Yarground)
            {
                return null;
            }
            return result.FilePath;
        }

        /// <summary>
        /// Starts async preloading of the venue for the given song.
        /// Call this when the difficulty select screen opens.
        /// </summary>
        public void StartPreload(SongEntry song)
        {
            if (song == null)
            {
                YargLogger.LogDebug("[VENUE PRELOAD] No song provided, skipping preload");
                return;
            }

            _preloadedSong = song;
            _ = PreloadAsync(song);
        }

        private async UniTaskVoid PreloadAsync(SongEntry song)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using var result = VenueLoader.GetVenue(song, out var source);
                if (result == null || result.Type != BackgroundType.Yarground)
                {
                    stopwatch.Stop();
                    YargLogger.LogFormatInfo("[VENUE PRELOAD] No yarground to preload (type: {0}), took {1}ms",
                        result?.Type.ToString() ?? "null", stopwatch.ElapsedMilliseconds);
                    return;
                }

                // Can only preload from file paths, not streams (SngFile)
                if (result.FilePath == null)
                {
                    stopwatch.Stop();
                    YargLogger.LogFormatInfo("[VENUE PRELOAD] Cannot preload from stream (SngFile), took {1}ms",
                        result.FilePath, stopwatch.ElapsedMilliseconds);
                    return;
                }

                var cacheKey = result.FilePath;

                // Check if already cached
                if (_venueCache.ContainsKey(cacheKey))
                {
                    YargLogger.LogFormatInfo("[VENUE PRELOAD] Venue '{0}' already cached, skipping load", cacheKey);
                    return;
                }

                YargLogger.LogFormatInfo("[VENUE PRELOAD] Preloading venue '{0}'...", cacheKey);

                var loadStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var bundle = await AssetBundle.LoadFromFileAsync(result.FilePath);
                loadStopwatch.Stop();
                YargLogger.LogFormatInfo("[VENUE PRELOAD] AssetBundle loaded in {0}ms", loadStopwatch.ElapsedMilliseconds);

                var prefabStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var bg = (GameObject) await bundle.LoadAssetAsync<GameObject>(
                    BundleBackgroundManager.BACKGROUND_PREFAB_PATH.ToLowerInvariant());
                prefabStopwatch.Stop();
                YargLogger.LogFormatInfo("[VENUE PRELOAD] Background prefab loaded in {0}ms", prefabStopwatch.ElapsedMilliseconds);

                AssetBundle shaderBundle = null;

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
                // Preload Metal shaders too
                var shaderStopwatch = System.Diagnostics.Stopwatch.StartNew();

                var shaderBundleName = "Assets/" + BundleBackgroundManager.BACKGROUND_SHADER_BUNDLE_NAME;
                var shaderBundleData = (TextAsset) await bundle.LoadAssetAsync<TextAsset>(shaderBundleName);

                if (shaderBundleData != null && shaderBundleData.bytes.Length > 0)
                {
                    shaderBundle = await AssetBundle.LoadFromMemoryAsync(shaderBundleData.bytes);
                }

                shaderStopwatch.Stop();
                YargLogger.LogFormatInfo("[VENUE PRELOAD] Metal shaders loaded in {0}ms", shaderStopwatch.ElapsedMilliseconds);
#endif

                // Make room in cache if needed
                EnsureCacheSpace();

                // Add to cache
                var cachedVenue = new PreloadedVenue
                {
                    Bundle = bundle,
                    BackgroundPrefab = bg,
                    ShaderBundle = shaderBundle,
                    Result = result,
                    Source = source,
                    CacheKey = cacheKey,
                    LastAccessed = DateTime.UtcNow
                };
                _venueCache[cacheKey] = cachedVenue;

                stopwatch.Stop();
                YargLogger.LogFormatInfo("[VENUE PRELOAD] Complete! Venue '{0}' cached. Total preload took {1}ms. Cache size: {2}/{3}",
                    cacheKey, stopwatch.ElapsedMilliseconds, _venueCache.Count, _maxCacheSize);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, "[VENUE PRELOAD] Failed to preload venue");
            }
        }

        /// <summary>
        /// Gets the venue for the given song, using cache if available.
        /// Returns a COPY of the cached venue (doesn't remove from cache).
        /// </summary>
        public bool TryGetVenue(SongEntry song, out PreloadedVenue venue)
        {
            if (song == null)
            {
                venue = null;
                return false;
            }

            var cacheKey = GetVenueCacheKey(song);

            YargLogger.LogFormatInfo("[VENUE] TryGetVenue: song='{0}', key='{1}', cache={2}/{3}",
                song.Name, cacheKey ?? "null", _venueCache.Count, _maxCacheSize);

            if (cacheKey != null && _venueCache.TryGetValue(cacheKey, out var cached))
            {
                // Update access time for LRU
                cached.LastAccessed = DateTime.UtcNow;

                // Return a copy with the same assets (don't remove from cache)
                venue = new PreloadedVenue
                {
                    Bundle = cached.Bundle,
                    BackgroundPrefab = cached.BackgroundPrefab,
                    ShaderBundle = cached.ShaderBundle,
                    Result = cached.Result,
                    Source = cached.Source,
                    CacheKey = cached.CacheKey
                };

                YargLogger.LogFormatInfo("[VENUE] CACHE HIT! Using cached venue '{0}'", cacheKey);
                return true;
            }

            venue = null;
            if (cacheKey != null)
            {
                YargLogger.LogFormatInfo("[VENUE] CACHE MISS for '{0}'", cacheKey);
            }
            else
            {
                YargLogger.LogInfo("[VENUE] No yarground venue for this song (video/image or no venue)");
            }
            return false;
        }

        /// <summary>
        /// Ensures there's room in the cache by evicting the least recently used venue if needed.
        /// </summary>
        private void EnsureCacheSpace()
        {
            while (_venueCache.Count >= _maxCacheSize)
            {
                // Find LRU venue
                string lruKey = null;
                DateTime oldestTime = DateTime.UtcNow;

                foreach (var kvp in _venueCache)
                {
                    if (kvp.Value.LastAccessed < oldestTime)
                    {
                        oldestTime = kvp.Value.LastAccessed;
                        lruKey = kvp.Key;
                    }
                }

                if (lruKey != null)
                {
                    UnloadVenue(lruKey);
                }
                else
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Unloads a venue from the cache.
        /// </summary>
        private void UnloadVenue(string cacheKey)
        {
            if (_venueCache.TryGetValue(cacheKey, out var venue))
            {
                YargLogger.LogFormatInfo("[VENUE PRELOAD] Unloading LRU venue '{0}' from cache", cacheKey);
                venue.Bundle?.Unload(false); // Unload bundle but keep loaded assets
                venue.ShaderBundle?.Unload(true);
                _venueCache.Remove(cacheKey);
            }
        }

        /// <summary>
        /// Adds a loaded venue to the cache. Called by BackgroundManager when a venue is loaded from disk.
        /// </summary>
        public void AddToCache(string cacheKey, AssetBundle bundle, GameObject prefab,
            AssetBundle shaderBundle, BackgroundResult result, VenueSource source)
        {
            if (string.IsNullOrEmpty(cacheKey))
            {
                return;
            }

            // Check if already cached
            if (_venueCache.ContainsKey(cacheKey))
            {
                YargLogger.LogFormatInfo("[VENUE] Venue '{0}' already cached, skipping", cacheKey);
                return;
            }

            // Make room in cache if needed
            EnsureCacheSpace();

            // Add to cache
            var cachedVenue = new PreloadedVenue
            {
                Bundle = bundle,
                BackgroundPrefab = prefab,
                ShaderBundle = shaderBundle,
                Result = result,
                Source = source,
                CacheKey = cacheKey,
                LastAccessed = DateTime.UtcNow
            };
            _venueCache[cacheKey] = cachedVenue;

            YargLogger.LogFormatInfo("[VENUE] CACHED venue '{0}' (cache now {1}/{2})",
                cacheKey, _venueCache.Count, _maxCacheSize);
        }

        /// <summary>
        /// Clears all cached venues.
        /// </summary>
        public void ClearCache()
        {
            YargLogger.LogFormatInfo("[VENUE PRELOAD] Clearing venue cache ({0} venues)", _venueCache.Count);
            foreach (var kvp in _venueCache)
            {
                kvp.Value.Bundle?.Unload(false);
                kvp.Value.ShaderBundle?.Unload(true);
            }
            _venueCache.Clear();
            _preloadedSong = null;
        }

        private void OnDestroy()
        {
            ClearCache();
        }
    }
}
