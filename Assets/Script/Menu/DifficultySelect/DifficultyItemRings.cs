using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Song;
using YARG.Helpers.Extensions;
using YARG.Menu.MusicLibrary;

namespace YARG.Menu.DifficultySelect
{
    /// <summary>
    /// Hosts the instrument tier-wheel row on a difficulty item. Lives on the
    /// DifficultyItemRings prefab variant.
    /// </summary>
    [RequireComponent(typeof(DifficultyItem))]
    public class DifficultyItemRings : MonoBehaviour
    {
        public const float INSTRUMENT_RING_SIZE = 40f;

        // Backdrop circle marking the selected instrument's ring — translucent
        // black (a blue tint blended into the row's blue selection highlight).
        private static readonly Color SELECTED_INSTRUMENT_COLOR = new Color(0f, 0f, 0f, 0.5f);

        // Non-selected ring arcs and instrument icons dim while the instrument
        // field is focused (blue row highlight, backdrop visible) and further
        // when another field has focus (black row, where the backdrop is
        // invisible). The intensity number is deliberately left undimmed.
        private const float RING_DIM_FOCUSSED = 0.6f;
        private const float RING_DIM_UNFOCUSSED = 0.2f;
        private const float ICON_DIM_FOCUSSED = 0.45f;
        private const float ICON_DIM_UNFOCUSSED = 0.3f;

        /// <summary>
        /// Clones the given ring into a centered horizontal group on a new
        /// layout row below the text, and returns the created rings.
        /// </summary>
        public DifficultyRing[] AttachRingRow(DifficultyRing template, int count,
            float size = 56f, float spacing = 10f)
        {
            // A direct child of the item root joins the VerticalLayoutGroup as
            // a real row; the LayoutElement height makes the item grow to fit.
            var row = new GameObject("RingRow", typeof(RectTransform), typeof(LayoutElement));
            row.layer = gameObject.layer;

            var rowRect = (RectTransform) row.transform;
            rowRect.SetParent(transform, false);
            rowRect.sizeDelta = new Vector2(
                ((RectTransform) transform).sizeDelta.x, RingRowHeight(size));

            var layout = row.GetComponent<LayoutElement>();
            layout.minHeight = layout.preferredHeight = RingRowHeight(size);

            var rings = new DifficultyRing[count];
            for (int i = 0; i < count; i++)
            {
                var ring = Instantiate(template, rowRect);
                var rt = (RectTransform) ring.transform;

                // Normalize to the ring prefab's native 65x65 rect and scale
                // uniformly, centered in the row.
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(65f, 65f);
                rt.localScale = Vector3.one * (size / 65f);
                rt.anchoredPosition = new Vector2((i - (count - 1) * 0.5f) * (size + spacing), 0f);

                ring.gameObject.SetActive(true);
                rings[i] = ring;
            }

            return rings;
        }

        public static float RingRowHeight(float size) => size + 8f;

        public void ShowInstrumentTiers(DifficultyRing template, IReadOnlyList<Instrument> instruments,
            Instrument currentInstrument, SongEntry song, float ringSize = INSTRUMENT_RING_SIZE)
        {
            var item = GetComponent<DifficultyItem>();
            var rings = AttachRingRow(template, instruments.Count, ringSize);
            DifficultyRing selectedRing = null;

            for (int i = 0; i < instruments.Count; i++)
            {
                var instrument = instruments[i];
                rings[i].SetInfo(
                    GetInstrumentRingAsset(instrument, song.VocalsCount),
                    instrument,
                    PlayerSelectionRules.GetTierValues(song, instrument));

                if (instrument == currentInstrument)
                {
                    // Extra size is in the ring's native units; scale it
                    // so the circle extends four *screen* pixels per
                    // side, giving the ring a prominent rim.
                    rings[i].ShowSelectionBackdrop(SELECTED_INSTRUMENT_COLOR,
                        extraSize: 8f * 65f / ringSize);
                    selectedRing = rings[i];
                }
            }

            void ApplyInstrumentFieldFocus(bool focused)
            {
                selectedRing?.SetBackdropVisible(focused);
                for (int i = 0; i < rings.Length; i++)
                {
                    if (rings[i] != selectedRing)
                    {
                        rings[i].SetRingOpacity(
                            focused ? RING_DIM_FOCUSSED : RING_DIM_UNFOCUSSED);
                        rings[i].SetIconColor(Color.white.WithAlpha(
                            focused ? ICON_DIM_FOCUSSED : ICON_DIM_UNFOCUSSED));
                    }
                }
            }

            item.Button.SelectionStateChanged += (_, selected, _) =>
                ApplyInstrumentFieldFocus(selected);
            ApplyInstrumentFieldFocus(item.Button.Selected);
        }

        // Resolve the bare Addressable icon name for the ring. Handles the 22-fret
        // pro-instrument gap (ToResourceName returns null for ProGuitar_22Fret /
        // ProBass_22Fret) and selects the part-count mic icon for harmony and
        // party vocals based on the song's vocal part count.
        private static string GetInstrumentRingAsset(Instrument instrument, int vocalPartCount)
            => instrument switch
        {
            Instrument.ProGuitar_22Fret => "realGuitar",
            Instrument.ProBass_22Fret   => "realBass",
            Instrument.Harmony or Instrument.PartyVocals => vocalPartCount switch
            {
                >= 3 => "harmVocals",
                2    => "twoVocals",
                _    => "vocals",
            },
            _ => instrument.ToResourceName(),
        };
    }
}
