using System;
using System.Collections.Generic;

namespace YARG.Menu.Overshell
{
    internal enum ColumnState
    {
        ChooseProfile,
        Main,
        Difficulty,
        Instrument,
        Adjustments,
        Modifiers,
        Accessibility,
        OpenLane,
        SittingOut,
        Position,
        Settings,
        SettingOptions,
    }

    /// <summary>
    /// Represents a selectable choice in a band column's generic setting-options submenu.
    /// </summary>
    internal readonly struct SettingOption
    {
        public readonly string Label;
        public readonly bool Current;
        public readonly Action Pick;

        public SettingOption(string label, bool current, Action pick)
        {
            Label = label;
            Current = current;
            Pick = pick;
        }
    }

    /// <summary>
    /// Tracks a single player's submenu navigation history (back stack) inside their band column.
    /// Remembers which row was highlighted before entering a submenu so pressing Back re-highlights it,
    /// and holds the title and choice list while a generic setting-options submenu is open.
    /// </summary>
    internal sealed class ColumnViewStack
    {
        private readonly struct ColumnView
        {
            public readonly ColumnState State;
            public readonly string FocusKey;
            public readonly string SettingTitle;
            public readonly List<SettingOption> SettingOptions;

            public ColumnView(ColumnState state, string focusKey = null, string settingTitle = null,
                List<SettingOption> settingOptions = null)
            {
                State = state;
                FocusKey = focusKey;
                SettingTitle = settingTitle;
                SettingOptions = settingOptions;
            }
        }

        private readonly Stack<ColumnView> _history = new();
        private ColumnView _currentView;

        public ColumnState State => _currentView.State;
        public string SettingTitle => _currentView.SettingTitle;
        public List<SettingOption> SettingOptions => _currentView.SettingOptions;
        public string RestoreFocusKey { get; set; }
        public int Depth => _history.Count;

        public bool IsAtTopLevel(bool ready)
        {
            return State switch
            {
                ColumnState.Main       => !ready,
                ColumnState.SittingOut => true,
                ColumnState.Instrument => _history.Count == 0,
                _                          => false,
            };
        }

        public bool HasChosenInstrument()
        {
            return State switch
            {
                ColumnState.ChooseProfile => false,
                ColumnState.SittingOut    => false,
                ColumnState.Instrument    => _history.Count > 0 && _history.Peek().State == ColumnState.Main,
                _                             => true,
            };
        }

        public void SetRoot(ColumnState state)
        {
            _history.Clear();
            _currentView = new ColumnView(state);
        }

        public void Push(ColumnState state, string focusKey = null, string settingTitle = null,
            List<SettingOption> settingOptions = null)
        {
            _history.Push(new ColumnView(_currentView.State, focusKey, _currentView.SettingTitle,
                _currentView.SettingOptions));
            _currentView = new ColumnView(state, null, settingTitle, settingOptions);
        }

        public bool Pop()
        {
            if (_history.Count == 0)
            {
                return false;
            }

            var previous = _history.Pop();
            RestoreFocusKey = previous.FocusKey;
            _currentView = new ColumnView(previous.State, null, previous.SettingTitle, previous.SettingOptions);
            return true;
        }
    }
}
