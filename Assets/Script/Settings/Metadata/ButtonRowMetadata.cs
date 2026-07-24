using System;

namespace YARG.Settings.Metadata
{
    public sealed class ButtonRowMetadata : AbstractMetadata
    {
        public override string[] UnlocalizedSearchNames { get; }

        public string[] Buttons { get; private set; }
        public Func<bool> VisibleWhen { get; }

        public ButtonRowMetadata(string button, bool isAdvanced = false)
            : base(isAdvanced)
        {
            UnlocalizedSearchNames = new[] { $"Button.{button}" };
            Buttons = new[] { button };
        }

        public ButtonRowMetadata(string button, Func<bool> visibleWhen, bool isAdvanced = false)
            : this(button, isAdvanced)
        {
            VisibleWhen = visibleWhen;
        }

        public ButtonRowMetadata(bool isAdvanced, params string[] buttons)
            : base(isAdvanced)
        {
            UnlocalizedSearchNames = new string[buttons.Length];
            for (int i = 0; i < buttons.Length; i++)
            {
                UnlocalizedSearchNames[i] = $"Button.{buttons[i]}";
            }

            Buttons = buttons;
        }

        public ButtonRowMetadata(params string[] buttons)
            : this(false, buttons)
        {
        }
    }
}
