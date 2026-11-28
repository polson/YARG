using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace YARG.Menu.Overshell
{
    internal sealed class JoinSlotClick : MonoBehaviour, IPointerClickHandler
    {
        public Action Clicked;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
            {
                Clicked?.Invoke();
            }
        }
    }
}
