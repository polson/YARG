using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using YARG.Core.Game;
using YARG.Input.Bindings;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    public sealed partial class BandSelectMenu
    {
        internal readonly struct JoinSlotEntry
        {
            public readonly LayoutElement Slot;
            public readonly RectTransform Card;
            public readonly TextMeshProUGUI Label;

            public JoinSlotEntry(LayoutElement slot, RectTransform card, TextMeshProUGUI label)
            {
                Slot = slot;
                Card = card;
                Label = label;
            }
        }

        private void SetJoinSlotCount(int count)
        {
            while (_joinSlots.Count > count)
            {
                DestroyJoinSlot(_joinSlots[_joinSlots.Count - 1].Slot);
            }

            while (_joinSlots.Count < count)
            {
                CreateJoinSlot(_band.childCount);
            }
        }

        private LayoutElement CreateJoinSlot(int siblingIndex)
        {
            var slot = Instantiate(_joinSlot, _band);
            slot.transform.SetSiblingIndex(siblingIndex);
            slot.preferredWidth = -1f;

            var card = JoinCard(slot);
            card.anchoredPosition = Vector2.zero;
            foreach (var graphic in card.GetComponentsInChildren<Graphic>())
            {
                graphic.raycastTarget = true;
            }

            card.gameObject.AddComponent<JoinSlotClick>().Clicked = () => JoinWithoutDevice(slot);
            slot.gameObject.SetActive(true);

            var label = slot.GetComponentInChildren<TextMeshProUGUI>(true);
            _joinSlots.Add(new JoinSlotEntry(slot, card, label));
            return slot;
        }

        private void DestroyJoinSlot(LayoutElement slot)
        {
            if (slot == null)
            {
                return;
            }

            var card = JoinCard(slot);
            int index = IndexOfJoinSlot(slot);
            if (index >= 0)
            {
                _joinSlots.RemoveAt(index);
            }

            _animator.KillTweens(card);
            slot.gameObject.SetActive(false);
            Destroy(slot.gameObject);
        }

        private int IndexOfJoinSlot(LayoutElement slot)
        {
            for (int i = 0; i < _joinSlots.Count; i++)
            {
                if (_joinSlots[i].Slot == slot)
                {
                    return i;
                }
            }

            return -1;
        }

        private static RectTransform JoinCard(LayoutElement slot)
        {
            return (RectTransform) slot.transform.GetChild(0);
        }

        internal void ChooseJoinProfile(BandColumn column, YargProfile profile)
        {
            if (column == _profilesInUse)
            {
                ChooseProfileForPendingSlot(profile);
                return;
            }

            CaptureCardPositions();
            if (!_session.ChooseJoinProfile(column.Model, profile))
            {
                RemoveColumnView(column);
                return;
            }

            column.CompleteJoin();
            _animator.ShiftCards(_band, _columns, _joinSlots, _cardSnapshotBuffer, delay: 0f,
                riseDelay: SLIDE_SECONDS);
            RefreshProfilePickers(column);
        }

        private void ChooseProfileForPendingSlot(YargProfile profile)
        {
            var slot = _profilesInUseSlot;
            CloseProfilesInUse();
            var joined = JoinWithoutDevice(slot, profile);
            if (joined != null)
            {
                ChooseJoinProfile(joined, profile);
            }
        }

        private void RefreshProfilePickers(BandColumn changed)
        {
            if (_profilesInUse != null)
            {
                _profilesInUse.Rebuild();
            }

            foreach (var column in _columns)
            {
                if (column != changed && column.State == BandColumnState.ChooseProfile)
                {
                    column.Rebuild();
                }
            }
        }

        internal void CancelJoin(BandColumn column)
        {
            CaptureCardPositions();
            _session.CancelJoin(column.Model);
            RemoveColumnView(column);
        }

        internal void Disconnect(BandColumn column)
        {
            CaptureCardPositions();
            _session.Disconnect(column.Model);
            RemoveColumnView(column);
        }

        private bool OnUnclaimedDevicePressed(InputDevice device)
        {
            if (PlayerContainer.IsControllerTaken(device))
            {
                return false;
            }

            if (_session.IsJoinBlocked)
            {
                return true;
            }

            if (!_pendingJoins.Contains(device))
            {
                _pendingJoins.Add(device);
            }

            return false;
        }

        private void Join(InputDevice device)
        {
            CaptureCardPositions();
            if (_session.TryStartJoinWithDevice(device, out var model, out int replacedSlotIndex))
            {
                FinishJoinView(model, replacedSlotIndex);
            }
        }

        private BandColumn JoinWithoutDevice(LayoutElement slot, YargProfile chosen = null)
        {
            if (_profilesInUse != null || IndexOfJoinSlot(slot) < 0)
            {
                return null;
            }

            int slotIndex = slot.transform.GetSiblingIndex();
            CaptureCardPositions();
            if (!_session.TryStartJoinWithoutDevice(slotIndex, chosen, out var model, out bool needsProfilePicker,
                    out int replacedSlotIndex))
            {
                if (needsProfilePicker)
                {
                    ShowProfilesInUse(slot);
                }

                return null;
            }

            return FinishJoinView(model, replacedSlotIndex);
        }

        private BandColumn FinishJoinView(BandColumnModel model, int replacedSlotIndex)
        {
            var column = CreateAndRemapColumnView(model, replacedSlotIndex);
            column.BeginJoin();
            _session.SyncColumnsAndSlots();
            _animator.ShiftCards(_band, _columns, _joinSlots, _cardSnapshotBuffer, delay: SLIDE_SECONDS,
                riseDelay: SLIDE_SECONDS * 2f);

            LayoutRebuilder.ForceRebuildLayoutImmediate(column.Card);
            column.RiseFrom(_joinCard.rect.height - column.Card.rect.height);
            return column;
        }

        private void ShowProfilesInUse(LayoutElement slot)
        {
            var column = Instantiate(_columnPrefab, slot.transform);
            var rect = (RectTransform) column.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            column.Initialize(this, _session, new BandColumnModel());
            column.SetWidth(slot.preferredWidth);
            column.ShowProfilesInUse();

            _profilesInUse = column;
            _profilesInUseSlot = slot;

            LayoutRebuilder.ForceRebuildLayoutImmediate(column.Card);
            column.RiseFrom(_joinCard.rect.height - column.Card.rect.height);
        }

        internal void CloseProfilesInUse()
        {
            if (_profilesInUse != null)
            {
                _profilesInUse.DismissAndDestroy();
            }

            _profilesInUse = null;
            _profilesInUseSlot = null;
        }

        private void OnPlayerAdded(YargPlayer player)
        {
            if (_session.IsSuppressingPlayerEvents || _session.FindColumn(player) != null)
            {
                return;
            }

            CaptureCardPositions();
            var model = _session.HandleExternalPlayerAdded(player, out int replacedSlotIndex);
            var column = CreateAndRemapColumnView(model, replacedSlotIndex);
            column.RiseFrom(-SLIDE_DISTANCE);
            column.Open();
            _session.SyncColumnsAndSlots();
            _animator.ShiftCards(_band, _columns, _joinSlots, _cardSnapshotBuffer, delay: 0f, riseDelay: null);
        }

        private void OnPlayerRemoved(YargPlayer player)
        {
            CaptureCardPositions();
            _session.HandleExternalPlayerRemoved(player, out var removedModel);
            if (removedModel != null && _columnViews.TryGetValue(removedModel, out var column))
            {
                RemoveColumnView(column);
            }
        }
    }
}
