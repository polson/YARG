using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Core;
using YARG.Core.Game;
using YARG.Core.Song;
using YARG.Menu.MusicLibrary;

namespace YARG.Menu.DifficultySelect
{
    internal enum BandMenuItemKind
    {
        Button,
        Label,
        Toggle,
        Stepper,
    }

    internal enum BandRowStyle
    {
        Normal,
        Small,
        Ready,
        Profile,
    }

    /// <summary>
    /// Plain data for one row on a band card: what it says, how it looks, and what happens when it is used.
    /// </summary>
    internal sealed class BandMenuItem
    {
        public BandMenuItemKind Kind { get; private set; }
        public string Header { get; set; }
        public string Body { get; set; }
        public BandRowStyle Style { get; set; }
        public float? Height { get; set; }
        public bool Enabled { get; set; } = true;
        public Color? TextColor { get; set; }
        public bool WrapIfLong { get; set; }
        public bool WrapText { get; set; }
        public bool AllowTinyText { get; set; }
        public YargProfile Avatar { get; set; }

        public Action Picked { get; private set; }
        public Func<bool> IsOn { get; private set; }
        public Action<bool> Toggled { get; private set; }
        public Func<string> Value { get; private set; }
        public Action<int> Stepped { get; private set; }

        public DifficultyRing RingTemplate { get; set; }
        public IReadOnlyList<Instrument> RingInstruments { get; set; }
        public Instrument RingCurrent { get; set; }
        public SongEntry RingSong { get; set; }

        /// <summary>
        /// A row that runs <paramref name="picked"/> when confirmed.
        /// </summary>
        public static BandMenuItem Button(string body, Action picked, bool enabled = true)
        {
            return new BandMenuItem
            {
                Kind = BandMenuItemKind.Button,
                Body = body,
                Picked = picked,
                Enabled = enabled,
            };
        }

        /// <summary>
        /// A row with a small header line above its value.
        /// </summary>
        public static BandMenuItem Button(string header, string body, Action picked, bool enabled = true)
        {
            var item = Button(body, picked, enabled);
            item.Header = header;
            return item;
        }

        /// <summary>
        /// A row of text that cannot be selected.
        /// </summary>
        public static BandMenuItem Label(string text)
        {
            return new BandMenuItem
            {
                Kind = BandMenuItemKind.Label,
                Body = text,
                Enabled = false,
            };
        }

        /// <summary>
        /// An on/off switch that reads its state from <paramref name="isOn"/>.
        /// </summary>
        public static BandMenuItem Toggle(string label, Func<bool> isOn, Action<bool> toggled)
        {
            return new BandMenuItem
            {
                Kind = BandMenuItemKind.Toggle,
                Body = label,
                IsOn = isOn,
                Toggled = toggled,
            };
        }

        /// <summary>
        /// A number row changed with left and right, showing the text from <paramref name="value"/>.
        /// </summary>
        public static BandMenuItem Stepper(string header, Func<string> value, Action<int> stepped)
        {
            return new BandMenuItem
            {
                Kind = BandMenuItemKind.Stepper,
                Header = header,
                Value = value,
                Stepped = stepped,
            };
        }
    }

    /// <summary>
    /// Everything one band card shows for its current screen.
    /// </summary>
    internal sealed class BandCardContent
    {
        private readonly List<BandMenuItem> _items = new();

        public string HeaderText { get; set; }
        public YargProfile HeaderProfile { get; set; }
        public bool HeaderReady { get; set; }
        public IReadOnlyList<BandMenuItem> Items => _items;

        /// <summary>
        /// Selected even when the card is rebuilt on the same screen.
        /// </summary>
        public BandMenuItem FocusItem { get; set; }

        /// <summary>
        /// Selected when the card opens a screen, unless a previous selection is being restored.
        /// </summary>
        public BandMenuItem DefaultItem { get; set; }

        /// <summary>
        /// Adds <paramref name="item"/> to the bottom of the card and returns it.
        /// </summary>
        public BandMenuItem Add(BandMenuItem item)
        {
            _items.Add(item);
            return item;
        }

        /// <summary>
        /// Empties the card so it can be built again.
        /// </summary>
        public void Clear()
        {
            _items.Clear();
            HeaderText = null;
            HeaderProfile = null;
            HeaderReady = false;
            FocusItem = null;
            DefaultItem = null;
        }
    }
}
