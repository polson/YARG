using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using YARG.Helpers;
using YARG.Settings;
using YARG.Core.Song;
using YARG.Core.Venue;
using YARG.Core.IO;
using YARG.Core.Logging;

namespace YARG.Venue
{
    public enum VenueSource
    {
        Global,
        Song,
    }

    public static class VenueLoader
    {
        private static readonly string _venueFolder = Path.Combine(PathHelper.PersistentDataPath, "venue");
        private static readonly string _defaultVenue = Path.Combine(Application.streamingAssetsPath, "venue", "default.yarground");
        public static string VenueFolder
        {
            get
            {
                if (!Directory.Exists(_venueFolder))
                {
                    Directory.CreateDirectory(_venueFolder);
                }
                return _venueFolder;
            }
        }

#nullable enable
        public static BackgroundResult? GetVenue(SongEntry song, out VenueSource source)
        {
            var totalStopwatch = Stopwatch.StartNew();
            BackgroundResult? result = null;
#nullable disable
            source = VenueSource.Song;

            var songBgStopwatch = Stopwatch.StartNew();
            if (!SettingsManager.Settings.DisablePerSongBackgrounds.Value)
            {
                result = song.LoadBackground();
            }
            songBgStopwatch.Stop();
            if (result != null)
            {
                YargLogger.LogFormatInfo("[VENUE] Song-specific background loaded in {0}ms", songBgStopwatch.ElapsedMilliseconds);
            }

            var globalBgStopwatch = Stopwatch.StartNew();
            if (!SettingsManager.Settings.DisableGlobalBackgrounds.Value && result == null)
            {
                source = VenueSource.Global;
                result = GetVenuePathFromGlobal();
            }
            globalBgStopwatch.Stop();
            if (result != null)
            {
                YargLogger.LogFormatInfo("[VENUE] Global background loaded in {0}ms", globalBgStopwatch.ElapsedMilliseconds);
            }

            var defaultBgStopwatch = Stopwatch.StartNew();
            if (!SettingsManager.Settings.DisableDefaultBackground.Value && result == null)
            {
                result = LoadDefaultVenue();
            }
            defaultBgStopwatch.Stop();
            if (result != null)
            {
                YargLogger.LogFormatInfo("[VENUE] Default background loaded in {0}ms", defaultBgStopwatch.ElapsedMilliseconds);
            }

            totalStopwatch.Stop();
            if (result != null)
            {
                YargLogger.LogFormatInfo("[VENUE] GetVenue() total took {0}ms (source: {1})", totalStopwatch.ElapsedMilliseconds, source);
            }

            return result;
        }

#nullable enable
        private static BackgroundResult? GetVenuePathFromGlobal()
#nullable disable
        {
            var stopwatch = Stopwatch.StartNew();

            string[] validExtensions =
            {
                "*.yarground", "*.mp4", "*.mov", "*.webm", "*.png", "*.jpg", "*.jpeg"
            };

            string venueFolder = VenueFolder;
            string launcherVenueFolder = PathHelper.VenuePath;
            var filePaths = new List<string>();

            var enumerateStopwatch = Stopwatch.StartNew();
            foreach (var ext in validExtensions)
            {
                filePaths.AddRange(Directory.EnumerateFiles(venueFolder, ext, PathHelper.SafeSearchOptions));
            }

            if (launcherVenueFolder != null && Directory.Exists(launcherVenueFolder))
            {
                // We limit ourselves to yarground here because that's all that will be downloaded by the launcher
                filePaths.AddRange(Directory.EnumerateFiles(launcherVenueFolder, "*.yarground", PathHelper.SafeSearchOptions));
            }
            enumerateStopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] File enumeration took {0}ms, found {1} files", enumerateStopwatch.ElapsedMilliseconds, filePaths.Count);

            while (filePaths.Count > 0)
            {
                int index = Random.Range(0, filePaths.Count);
                var file = filePaths[index];
                var fileLoadStopwatch = Stopwatch.StartNew();
                switch (Path.GetExtension(file))
                {
                    case ".png":
                    case ".jpg":
                    case ".jpeg":
                        var image = YARGImage.Load(file);
                        fileLoadStopwatch.Stop();
                        if (image != null)
                        {
                            YargLogger.LogFormatInfo("[VENUE] Image {0} loaded in {1}ms", file, fileLoadStopwatch.ElapsedMilliseconds);
                            stopwatch.Stop();
                            YargLogger.LogFormatInfo("[VENUE] GetVenuePathFromGlobal() total took {0}ms", stopwatch.ElapsedMilliseconds);
                            return new BackgroundResult(image);
                        }
                        break;
                    case ".mp4":
                    case ".mov":
                    case ".webm":
                        fileLoadStopwatch.Stop();
                        YargLogger.LogFormatInfo("[VENUE] Video {0} selected in {1}ms", file, fileLoadStopwatch.ElapsedMilliseconds);
                        stopwatch.Stop();
                        YargLogger.LogFormatInfo("[VENUE] GetVenuePathFromGlobal() total took {0}ms", stopwatch.ElapsedMilliseconds);
                        return new BackgroundResult(BackgroundType.Video, File.OpenRead(file));
                    case ".yarground":
                        fileLoadStopwatch.Stop();
                        YargLogger.LogFormatInfo("[VENUE] Yarground {0} selected in {1}ms", file, fileLoadStopwatch.ElapsedMilliseconds);
                        stopwatch.Stop();
                        YargLogger.LogFormatInfo("[VENUE] GetVenuePathFromGlobal() total took {0}ms", stopwatch.ElapsedMilliseconds);
                        return new BackgroundResult(BackgroundType.Yarground, file);
                    default:
                        filePaths.RemoveAt(index);
                        break;
                }
            }

            stopwatch.Stop();
            YargLogger.LogFormatInfo("[VENUE] GetVenuePathFromGlobal() found no valid venue in {0}ms", stopwatch.ElapsedMilliseconds);
            return null;
        }

#nullable enable
        private static BackgroundResult? LoadDefaultVenue()
#nullable disable
        {
            if (!File.Exists(_defaultVenue))
            {
                YargLogger.LogWarning("Default venue not found. Build error?");
                return null;
            }

            return new BackgroundResult(BackgroundType.Yarground, _defaultVenue);
        }
    }
}