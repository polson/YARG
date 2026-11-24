using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Menu.DifficultySelect;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Persistent;
using YARG.Menu.Settings;

namespace YARG.Menu
{
    public class MenuManager : MonoSingleton<MenuManager>
    {
        public enum Menu
        {
            None,
            MainMenu,
            MusicLibrary,
            DifficultySelect,
            Credits,
            ProfileList,
            ProfileInfo,
            History,
            Content,
            CareerList,
            Career,
            GameModes,
        }

        /// <summary>
        /// The values that <see cref="_lastOpenMenu"/> is allowed to be set to
        /// (not including <see cref="Menu.None"/>.
        /// </summary>
        private static readonly HashSet<Menu> _allowedLastOpenMenus = new()
        {
            Menu.MusicLibrary,
            Menu.History,
            Menu.Content,
            Menu.Career,
        };

        /// <summary>
        /// The menu that was last open when the menu scene gets disabled.
        /// </summary>
        private static Menu _lastOpenMenu = Menu.None;

        private static readonly HashSet<Menu> _selfHostedOvershellMenus = new()
        {
            Menu.MainMenu,
            Menu.MusicLibrary,
            Menu.DifficultySelect,
        };

        private static readonly HashSet<Menu> _menusWithoutOvershell = new()
        {
            Menu.Credits,
            Menu.ProfileList,
            Menu.ProfileInfo,
            Menu.History,
            Menu.CareerList,
            Menu.Career,
        };

        private Dictionary<Menu, MenuObject> _menus;
        private BandSelectMenu _overshell;
        private MenuObject _overshellHost;

        private readonly Stack<Menu> _openMenus = new();
        private Coroutine _reactivateCoroutine;

        protected override void SingletonAwake()
        {
            // Convert to dictionary with "Menu" as key
            var children = GetComponentsInChildren<MenuObject>(true);
            _menus = children.ToDictionary(i => i.Menu, i => i);
        }

        private void Start()
        {
            // Always push the main menu
            PushMenu(Menu.MainMenu);

            if (_lastOpenMenu != Menu.None)
            {
                PushMenu(_lastOpenMenu);
            }
        }

        private void Update()
        {
            RefreshOvershell();
        }

        private void OnDisable()
        {
            SetOvershellHost(null);
            _lastOpenMenu = Menu.None;

            // Set the last open menu to the first instance of the allowed menu
            // Loops from top to bottom
            foreach (var menu in _openMenus)
            {
                if (_allowedLastOpenMenus.Contains(menu))
                {
                    _lastOpenMenu = menu;
                    break;
                }
            }
        }

        public MenuObject PushMenu(Menu menu, bool setActiveImmediate = true)
        {
            bool hideOther;

            // Get the new one
            if (_menus.TryGetValue(menu, out var newMenu))
            {
                hideOther = newMenu.HideBelow;
            }
            else
            {
                throw new InvalidOperationException($"Failed to open menu {menu}.");
            }

            // Close the currently open one
            if (hideOther && _openMenus.TryPeek(out var currentMenuEnum) &&
                _menus.TryGetValue(currentMenuEnum, out var currentMenu))
            {
                currentMenu.gameObject.SetActive(false);
            }

            // Show the new one
            if (setActiveImmediate)
            {
                newMenu.gameObject.SetActive(true);
            }

            // ... and push it onto the stack
            _openMenus.Push(menu);
            RefreshOvershell();

            return newMenu;
        }

        public void PopMenu()
        {
            //Don't pop the only remaining menu
            if (_openMenus.Count == 1)
            {
                return;
            }

            // Close the currently open one
            if (_openMenus.TryPop(out var currentMenuEnum) &&
                _menus.TryGetValue(currentMenuEnum, out var currentMenu))
            {
                currentMenu.gameObject.SetActive(false);
            }

            if (_openMenus.TryPeek(out var newMenuEnum) &&
                _menus.TryGetValue(newMenuEnum, out var newMenu))
            {
                newMenu.gameObject.SetActive(true);
            }
            else
            {
                throw new InvalidOperationException($"Failed to open menu {newMenuEnum}.");
            }

            RefreshOvershell();
        }

        private void RefreshOvershell()
        {
            MenuObject host = null;
            if (MusicLibraryMenu.UseOvershell && _openMenus.TryPeek(out var top) &&
                !_selfHostedOvershellMenus.Contains(top) && !_menusWithoutOvershell.Contains(top) && _menus.TryGetValue(top, out var menu) &&
                menu.gameObject.activeInHierarchy)
            {
                host = menu;
            }

            SetOvershellHost(host);

            if (_overshell != null)
            {
                _overshell.SetOvershellHidden(IsSettingsOpen());
            }
        }

        private void SetOvershellHost(MenuObject host)
        {
            if (host == _overshellHost)
            {
                return;
            }

            if (_overshell != null)
            {
                _overshell.Close();
            }

            _overshellHost = host;
            if (host == null)
            {
                return;
            }

            if (_overshell == null)
            {
                var template = _menus[Menu.MainMenu].GetComponentInChildren<BandSelectMenu>(true);
                _overshell = Instantiate(template, host.transform, false);
                _overshell.DiscardCopiedContent();
            }
            else
            {
                _overshell.transform.SetParent(host.transform, false);
            }

            _overshell.transform.SetAsLastSibling();
            _overshell.OpenOvershell(CanEngageOvershell);
        }

        private static bool CanEngageOvershell()
        {
            return !IsSettingsOpen() && !DialogManager.Instance.IsDialogShowing;
        }

        private static bool IsSettingsOpen()
        {
            return SettingsMenu.Instance != null && SettingsMenu.Instance.gameObject.activeSelf;
        }

        // Disables the current menu without popping it from the stack
        public void DisableCurrentMenu()
        {
            if (_openMenus.TryPeek(out var currentMenuEnum) && _menus.TryGetValue(currentMenuEnum, out var currentMenu))
            {
                currentMenu.gameObject.SetActive(false);
            }
        }

        public void ReactivateCurrentMenu(bool forceRefreshIfActive = true)
        {
            // Show the under one
            if (_openMenus.TryPeek(out var menu) && _menus.TryGetValue(menu, out var newMenu))
            {
                if (!forceRefreshIfActive && newMenu.gameObject.activeSelf)
                {
                    return;
                }

                if (_reactivateCoroutine != null)
                {
                    StopCoroutine(_reactivateCoroutine);
                    _reactivateCoroutine = null;
                }

                _reactivateCoroutine = StartCoroutine(ReactivateMenuCoroutine(newMenu.gameObject));
            }
            else
            {
                throw new InvalidOperationException($"Failed to activate menu {menu}.");
            }
        }

        private System.Collections.IEnumerator ReactivateMenuCoroutine(GameObject menuObject)
        {
            if (menuObject == null)
            {
                _reactivateCoroutine = null;
                yield break;
            }

            // Defer activation toggles to avoid SetActive during another object's OnEnable/OnDisable.
            yield return null;

            if (menuObject.activeSelf)
            {
                menuObject.SetActive(false);
                yield return null;
            }

            menuObject.SetActive(true);
            _reactivateCoroutine = null;
        }
    }
}
