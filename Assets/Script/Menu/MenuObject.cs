using UnityEngine;
using UnityEngine.Assertions;

namespace YARG.Menu
{
    public class MenuObject : MonoBehaviour
    {
        [field: SerializeField]
        public MenuManager.Menu Menu { get; private set; }

        [field: SerializeField]
        public bool HideBelow { get; private set; } = true;

        [field: SerializeField]
        public bool SupportsOvershell { get; private set; }

        private void Start()
        {
            Assert.AreNotEqual(Menu, MenuManager.Menu.None);
        }

        private void OnEnable()
        {
            if (MenuManager.Instance != null)
            {
                MenuManager.Instance.RefreshOvershell();
            }
        }

        public void CloseMenu()
        {
            MenuManager.Instance.PopMenu();
        }
    }
}