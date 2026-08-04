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
            UpdateValues(GlobalAudioHandler.GetOutputBackend(Value));
        }

        public void UpdateValues(AudioOutputBackend backend)
        {
            _possibleValues.Clear();

            foreach ((int, string name) device in GlobalAudioHandler.GetAllOutputDevices())
            {
                // Classification comes from the transport implementations, not name parsing
                if (GlobalAudioHandler.GetOutputBackend(device.name) == backend)
                {
                    _possibleValues.Add(device.name);
                }
            }
        }

        public bool Contains(string name) => _possibleValues.Contains(name);

        public string FirstOrDefault() => _possibleValues.Count > 0 ? _possibleValues[0] : null;

        public override string ValueToString(string value)
        {
            return value.StartsWith(AsioPrefix, StringComparison.Ordinal)
                ? value.Substring(AsioPrefix.Length)
                : value;
        }

        // Display-only: strips the ASIO family prefix for the UI. Never used for routing.
        private const string AsioPrefix = "ASIO: ";
    }
}
