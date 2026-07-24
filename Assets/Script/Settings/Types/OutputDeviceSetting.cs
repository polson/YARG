using System;
using YARG.Core.Audio;

namespace YARG.Settings.Types
{
    public class OutputDeviceSetting : DropdownSetting<string>
    {
        public OutputDeviceSetting(string value, Action<string> onChange = null) : base(value, onChange, localizable: false)
        {
        }

        public override void UpdateValues()
        {
            UpdateValues(BackendFor(Value));
        }

        public void UpdateValues(AudioOutputBackend backend)
        {
            _possibleValues.Clear();

            foreach ((int, string name) device in GlobalAudioHandler.GetAllOutputDevices())
            {
                bool isAsio = device.name.StartsWith(BassOutputDevicePrefix, StringComparison.Ordinal);
                if (isAsio == (backend == AudioOutputBackend.Asio))
                {
                    _possibleValues.Add(device.name);
                }
            }
        }

        public bool Contains(string name) => _possibleValues.Contains(name);

        public string FirstOrDefault() => _possibleValues.Count > 0 ? _possibleValues[0] : null;

        public override string ValueToString(string value)
        {
            return value.StartsWith(BassOutputDevicePrefix, StringComparison.Ordinal)
                ? value.Substring(BassOutputDevicePrefix.Length)
                : value;
        }

        public static AudioOutputBackend BackendFor(string name)
        {
            return name?.StartsWith(BassOutputDevicePrefix, StringComparison.Ordinal) == true
                ? AudioOutputBackend.Asio
                : AudioOutputBackend.WindowsAudio;
        }

        private const string BassOutputDevicePrefix = "ASIO: ";
    }
}
