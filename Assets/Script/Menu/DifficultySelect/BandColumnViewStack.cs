using System;
using System.Collections.Generic;

namespace YARG.Menu.DifficultySelect
{
    internal enum BandColumnState
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
    internal readonly struct BandSettingOption
    {
        public readonly string Label;
        public readonly bool Current;
        public readonly Action Pick;

        public BandSettingOption(string label, bool current, Action pick)
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
    internal sealed class BandColumnViewStack
    {
        private readonly struct ColumnView
        {
            public readonly BandColumnState State;
            public readonly string FocusKey;
            public readonly string SettingTitle;
            public readonly List<BandSettingOption> SettingOptions;

            public ColumnView(BandColumnState state, string focusKey = null, string settingTitle = null,
                List<BandSettingOption> settingOptions = null)
            {
                State = state;
                FocusKey = focusKey;
                SettingTitle = settingTitle;
                SettingOptions = settingOptions;
            }
        }

        private readonly Stack<ColumnView> _history = new();
        private ColumnView _currentView;

        public BandColumnState State => _currentView.State;
        public string SettingTitle => _currentView.SettingTitle;
        public List<BandSettingOption> SettingOptions => _currentView.SettingOptions;
        public string RestoreFocusKey { get; set; }
        public int Depth => _history.Count;

        public bool IsAtTopLevel(bool ready)
        {
            return State switch
            {
                BandColumnState.Main       => !ready,
                BandColumnState.SittingOut => true,
                BandColumnState.Instrument => _history.Count == 0,
                _                          => false,
            };
        }

        public bool HasChosenInstrument()
        {
            return State switch
            {
                BandColumnState.ChooseProfile => false,
                BandColumnState.SittingOut    => false,
                BandColumnState.Instrument    => _history.Count > 0 && _history.Peek().State == BandColumnState.Main,
                _                             => true,
            };
        }

        public void SetRoot(BandColumnState state)
        {
            _history.Clear();
            _currentView = new ColumnView(state);
        }

        public void Push(BandColumnState state, string focusKey = null, string settingTitle = null,
            List<BandSettingOption> settingOptions = null)
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
