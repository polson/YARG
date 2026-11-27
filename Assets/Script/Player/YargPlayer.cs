using System;
using System.IO;
using UnityEngine.InputSystem;
using YARG.Core;
using YARG.Core.Engine;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.IO;
using YARG.Core.Replays;
using YARG.Input;
using YARG.Settings.Customization;
using YARG.Themes;

namespace YARG.Player
{
    public class YargPlayer : IDisposable
    {
        public event MenuInputEvent MenuInput;

        public YargProfile Profile { get; private set; }

        /// <summary>
        /// Whether or not the player is sitting out. This is not needed in <see cref="Profile"/> as
        /// players that are sitting out are not included in replays.
        /// </summary>
        public bool SittingOut;

        public bool InputsEnabled { get; private set; }
        public PlayerDeviceInfo DeviceInfo { get; private set; }

        public EnginePreset    EnginePreset    { get; private set; }
        public ThemePreset     ThemePreset     { get; private set; }
        public ColorProfile    ColorProfile    { get; private set; }
        public CameraPreset    CameraPreset    { get; private set; }
        public HighwayPreset   HighwayPreset   { get; private set; }
        public RockMeterPreset RockMeterPreset { get; private set; }

        /// <summary>
        /// Whether or not the score is valid.
        /// </summary>
        /// <remarks>Could be invalidated due abusing pauses or no fail mode.</remarks>
        public bool IsScoreValid { get; set; } = true;

        public bool IsActive { get; private set; } = true;

        public void ResetParticipation() => IsActive = true;
        public void DropOut() => IsActive = false;

        public bool IsReplay { get; private set; }
        public int ReplayIndex = -1;

        /// <summary>
        /// Overrides the engine parameters in the gameplay player.
        /// This is only used when loading replays.
        /// </summary>
        public BaseEngineParameters EngineParameterOverride { get; set; }

        public bool IsMissingMicrophone => !IsReplay && Profile.GameMode is GameMode.Vocals && !DeviceInfo.HasMicrophoneAssigned && !Profile.IsBot;
        public bool IsMissingController => !IsReplay && Profile.GameMode is not GameMode.Vocals && !DeviceInfo.HasControllerAssigned && !Profile.IsBot;

        public YargPlayer(YargProfile profile, PlayerDeviceInfo bindings)
        {
            Profile = profile;
            DeviceInfo = bindings;
            IsReplay = false;
            RegisterDeviceInfo();
        }

        public YargPlayer(ReplayFrame frame, ReplayData replay)
        {
            Profile = frame.Profile;
            DeviceInfo = null;
            EngineParameterOverride = frame.EngineParameters;
            IsReplay = true;

            EnginePreset = CustomContentManager.EnginePresets.GetPresetById(Profile.EnginePreset)
                ?? EnginePreset.Default;
            ThemePreset = CustomContentManager.ThemePresets.GetPresetById(Profile.ThemePreset)
                ?? ThemePreset.Default;
            ColorProfile = replay.GetColorProfile(Profile.ColorProfile)
                ?? CustomContentManager.ColorProfiles.GetPresetById(Profile.ColorProfile)
                ?? ColorProfile.Default;
            CameraPreset = replay.GetCameraPreset(Profile.CameraPreset)
                ?? CustomContentManager.CameraSettings.GetPresetById(Profile.CameraPreset)
                ?? CameraPreset.Default;

            HighwayPreset = CustomContentManager.HighwayPresets.GetPresetById(Profile.HighwayPreset)
                ?? HighwayPreset.Default;

            RockMeterPreset = replay.GetRockMeterPreset(Profile.RockMeterPreset)
                ?? CustomContentManager.RockMeterPresets.GetPresetById(Profile.RockMeterPreset)
                ?? RockMeterPreset.Normal;
        }

        public void SwapToProfile(YargProfile profile, PlayerDeviceInfo bindings, bool resolveDevices)
        {
            // Force-disable inputs
            bool enabled = InputsEnabled;
            DisableInputs();
            UnregisterDeviceInfo();

            // Swap to the new profile
            DeviceInfo?.Dispose();
            Profile = profile;
            DeviceInfo = bindings;

            // Resolve bindings
            if (resolveDevices)
            {
                DeviceInfo?.ResolveDevices();
            }

            RegisterDeviceInfo();

            // Re-enable inputs
            if (enabled)
            {
                EnableInputs();
            }
        }

        public void RefreshPresets()
        {
            EnginePreset = CustomContentManager.EnginePresets.GetPresetById(Profile.EnginePreset)
                ?? EnginePreset.Default;
            Profile.EnginePreset = EnginePreset.Id;
            ThemePreset = CustomContentManager.ThemePresets.GetPresetById(Profile.ThemePreset)
                ?? ThemePreset.Default;
            Profile.ThemePreset = ThemePreset.Id;
            ColorProfile = CustomContentManager.ColorProfiles.GetPresetById(Profile.ColorProfile)
                ?? ColorProfile.Default;
            Profile.ColorProfile = ColorProfile.Id;
            CameraPreset = CustomContentManager.CameraSettings.GetPresetById(Profile.CameraPreset)
                ?? CameraPreset.Default;
            Profile.CameraPreset = CameraPreset.Id;
            HighwayPreset = CustomContentManager.HighwayPresets.GetPresetById(Profile.HighwayPreset)
                ?? HighwayPreset.Default;
            Profile.HighwayPreset = HighwayPreset.Id;
            RockMeterPreset = CustomContentManager.RockMeterPresets.GetPresetById(Profile.RockMeterPreset) ??
                RockMeterPreset.Normal;
            Profile.RockMeterPreset = RockMeterPreset.Id;

        }

        public void EnableInputs()
        {
            if (InputsEnabled || DeviceInfo == null)
            {
                return;
            }

            DeviceInfo.ActivateMenuBindings();

            DeviceInfo.EnableInputs();
            DeviceInfo.SubscribeToMenuInputs(OnMenuInput);
            InputManager.RegisterMenuInput(this);

            InputsEnabled = true;
        }

        public void DisableInputs()
        {
            if (!InputsEnabled || DeviceInfo == null)
            {
                return;
            }

            DeviceInfo.DisableInputs();
            DeviceInfo.UnsubscribeFromMenuInputs(OnMenuInput);
            InputManager.UnregisterMenuInput(this);

            InputsEnabled = false;
        }

        private void RegisterDeviceInfo()
        {
            if (DeviceInfo == null)
            {
                return;
            }

            InputManager.RegisterDevices(DeviceInfo.Controllers);
            DeviceInfo.ControllerAdded += OnControllerAdded;
            DeviceInfo.ControllerRemoved += OnControllerRemoved;
        }

        private void UnregisterDeviceInfo()
        {
            if (DeviceInfo == null)
            {
                return;
            }

            DeviceInfo.ControllerAdded -= OnControllerAdded;
            DeviceInfo.ControllerRemoved -= OnControllerRemoved;
            InputManager.UnregisterDevices(DeviceInfo.Controllers);
        }

        private static void OnControllerAdded(InputDevice controller)
        {
            InputManager.RegisterDevice(controller);
        }

        private static void OnControllerRemoved(InputDevice controller)
        {
            InputManager.UnregisterDevice(controller);
        }

        private void OnMenuInput(ref GameInput input)
        {
            MenuInput?.Invoke(this, ref input);
        }

        public void Dispose()
        {
            DisableInputs();
            UnregisterDeviceInfo();
            DeviceInfo?.Dispose();
        }
    }
}
