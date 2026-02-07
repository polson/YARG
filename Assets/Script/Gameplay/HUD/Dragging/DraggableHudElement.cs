using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;

namespace YARG.Gameplay.HUD
{
    [RequireComponent(typeof(RectTransform))]
    public class DraggableHudElement : GameplayBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler,
        IPointerDownHandler
    {
        [SerializeField]
        private string _draggableElementName;

        [Space]
        [SerializeField]
        private bool _horizontal = true;
        [SerializeField]
        private bool _vertical = true;

        [Space]
        [SerializeField]
        private UnityEvent<bool> _onEditModeChanged;

        [Space]
        [SerializeField]
        private DraggingDisplay _draggingDisplayPrefab;

        private DraggableHudManager _manager;
        private RectTransform _rectTransform;

        private DraggingDisplay _draggingDisplay;

        private Vector2 _defaultPosition;
        private Vector2 _storedPosition;

        private bool _isSelected;
        private bool _isDragging;

        public bool HasCustomPosition =>
            _manager.PositionProfile.HasElementPosition(_draggableElementName);

        public event Action<bool> EditModeChanged;

        public void SetDefaultPosition(Vector2 position)
        {
            _defaultPosition = position;
        }

        protected override void OnSongStarted()
        {
            if (GameManager.Players.Count > 1)
            {
                enabled = false;
                return;
            }

            _manager = GetComponentInParent<DraggableHudManager>();
            _rectTransform = GetComponent<RectTransform>();

            _defaultPosition = _rectTransform.anchoredPosition;

            var customPosition = _manager.PositionProfile.GetElementPosition(_draggableElementName);
            if (customPosition.HasValue)
            {
                _storedPosition = customPosition.Value;
                _rectTransform.anchoredPosition = _storedPosition;
            }
            else
            {
                _storedPosition = _defaultPosition;
            }

            _draggingDisplay = Instantiate(_draggingDisplayPrefab, transform);
            _draggingDisplay.DraggableHud = this;

            _draggingDisplay.Hide();
            _draggingDisplay.gameObject.SetActive(false);
        }

        protected override void GameplayDestroy()
        {
            if (_manager != null)
            {
                _manager.RemoveDraggableElement(this);
            }
        }

        public void Select()
        {
            _isSelected = true;
            _rectTransform.SetAsLastSibling();

            _draggingDisplay.Show();
        }

        public void Deselect()
        {
            _isSelected = false;

            if (_isDragging)
            {
                _isDragging = false;
                SavePosition();
            }

            _draggingDisplay.Hide();
        }

        public void OnEditModeChanged(bool on)
        {
            _draggingDisplay.gameObject.SetActive(on);
            _onEditModeChanged.Invoke(on);
            EditModeChanged?.Invoke(on);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            // Can only start dragging with the left mouse button
            if (!_manager.EditMode || _isDragging || eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            _isDragging = true;
        }

        public void OnDrag(PointerEventData eventData)
        {
            // Prevent dragging with other buttons (and "double dragging", increases speed and gets weird)
            if (!_isDragging || eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            var position = _rectTransform.anchoredPosition;

            if (_horizontal)
            {
                position.x += eventData.delta.x;
            }

            if (_vertical)
            {
                position.y += eventData.delta.y;
            }

            _rectTransform.anchoredPosition = position;
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            // Only end the drag if it was started with the left mouse button
            if (!_isDragging || eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            _isDragging = false;
            SavePosition();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!_manager.EditMode || _isSelected || eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            _manager.SetSelectedElement(this);
        }

        public void RevertElement()
        {
            _rectTransform.anchoredPosition = _storedPosition;
            SavePosition();
        }

        public void ResetElement()
        {
            _rectTransform.anchoredPosition = _defaultPosition;
            _storedPosition = _defaultPosition;
            _manager.PositionProfile.RemoveElementPosition(_draggableElementName);
        }

        private void SavePosition()
        {
            _storedPosition = _rectTransform.anchoredPosition;
            _manager.PositionProfile.SaveElementPosition(_draggableElementName,
                _rectTransform.anchoredPosition);
        }
    }
}
