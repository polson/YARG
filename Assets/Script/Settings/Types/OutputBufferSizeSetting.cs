using System;
using YARG.Core.Audio;
using YARG.Localization;

namespace YARG.Settings.Types
{
    public sealed class OutputBufferSizeSetting : DropdownSetting<int>
    {
        private int _preferredLength;
        private int _sampleRate;

        public OutputBufferSizeSetting(int value, Action<int> onChange = null)
            : base(value, onChange, localizable: false)
        {
        }

        public override void UpdateValues()
        {
            _possibleValues.Clear();
            _possibleValues.Add(0);

            OutputBufferInfo? info;
            try
            {
                info = GlobalAudioHandler.GetOutputBufferInfo();
            }
            catch
            {
                return;
            }

            if (info == null)
            {
                return;
            }

            _preferredLength = info.Value.PreferredLength;
            _sampleRate = info.Value.SampleRate;
            _possibleValues.AddRange(info.Value.SupportedLengths);
        }

        public bool Supports(int length) => _possibleValues.Contains(length);

        public override string ValueToString(int value)
        {
            if (value == 0)
            {
                return _preferredLength > 0
                    ? Localize.KeyFormat("Settings.Setting.AsioBufferSize.DriverDefaultWithSize", _preferredLength)
                    : Localize.Key("Settings.Setting.AsioBufferSize.DriverDefault");
            }

            if (_sampleRate <= 0)
            {
                return Localize.KeyFormat("Settings.Setting.AsioBufferSize.Samples", value);
            }

            double milliseconds = value * 1000.0 / _sampleRate;
            return Localize.KeyFormat("Settings.Setting.AsioBufferSize.SamplesWithDuration", value,
                milliseconds.ToString("0.0##"));
        }
    }
}
