using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace YARG.Menu.DifficultySelect
{
    internal sealed class SettingStepClick : MonoBehaviour, IPointerClickHandler
    {
        public Action<int> Stepped;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            var rect = (RectTransform) transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, eventData.position,
                    eventData.pressEventCamera, out var point))
            {
                return;
            }

            Stepped?.Invoke(point.x < rect.rect.center.x ? -1 : 1);
        }
    }
}
