using System;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.Song;
using YARG.Gameplay.Visuals;
using YARG.Helpers;
using YARG.Input;
using YARG.Input.Bindings;
using YARG.Localization;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Menu.ProfileList;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    public sealed partial class BandSelectMenu : MonoBehaviour
    {
        private void OnConfirm(NavigationContext context)
        {
            if (_started)
            {
                return;
            }

            var column = ColumnFor(context.Player);
            if (column is not null && !column.Expand())
            {
                column.Confirm();
            }
        }

        private void OnBack(NavigationContext context)
        {
            if (_started)
            {
                return;
            }

            var column = ColumnFor(context.Player);
            if (column is null || column.IsAtTopLevel)
            {
                MenuManager.Instance.PopMenu();
            }
            else
            {
                column.Back();
            }
        }

        private void OnNavigationEvent(NavigationContext context)
        {
            int direction = context.Action switch
            {
                MenuAction.Left  => -1,
                MenuAction.Right => 1,
                _                => 0,
            };

            if (direction != 0 && !_started && ColumnFor(context.Player) is { } column &&
                column.AdjustSelection(direction))
            {
                return;
            }

            bool movesFocus = direction != 0 && context.Player is null && _columns.Count > 0;
            if (!movesFocus)
            {
                return;
            }

            _focusIndex = Mathf.Clamp(_focusIndex + direction, 0, _columns.Count - 1);
            OnColumnChanged();
        }

        private bool InterceptOvershellInput(NavigationContext context)
        {
            if (_hidden || !_canEngage())
            {
                return false;
            }

            var column = FindColumn(context.Player);
            if (column is null)
            {
                return IsMenuExitAction(context.Action) && AnyColumnOpen();
            }

            if (context.Action == MenuAction.Start)
            {
                if (column.IsJoining)
                {
                    return true;
                }

                if (column.IsExpanded)
                {
                    column.Back();
                }
                else if (_canEngage())
                {
                    column.Expand();
                }

                return true;
            }

            if (!column.IsExpanded)
            {
                return IsMenuExitAction(context.Action) && AnyColumnOpen();
            }

            switch (context.Action)
            {
                case MenuAction.Up:
                    column.NavigateVertical(-1, context.IsRepeat);
                    break;
                case MenuAction.Down:
                    column.NavigateVertical(1, context.IsRepeat);
                    break;
                case MenuAction.Green:
                    column.Confirm();
                    break;
                case MenuAction.Red:
                    column.Back();
                    break;
                case MenuAction.Left:
                    column.AdjustSelection(-1);
                    break;
                case MenuAction.Right:
                    column.AdjustSelection(1);
                    break;
            }

            return true;
        }

        private static bool IsMenuExitAction(MenuAction action)
        {
            return action is MenuAction.Green or MenuAction.Red;
        }

        private bool AnyColumnOpen()
        {
            return _columns.Any(c => c.IsExpanded && !c.IsSittingOut && c.Player.DeviceInfo.HasControllerAssigned);
        }

        private BandColumn ColumnFor(YargPlayer player)
        {
            return player is null ? FocusedColumn : FindColumn(player);
        }

        private BandColumn FindColumn(YargPlayer player)
        {
            return player is null ? null : _columns.FirstOrDefault(c => c.Player == player);
        }

        private BandColumn FocusedColumn =>
            _columns.Count > 0 ? _columns[Mathf.Clamp(_focusIndex, 0, _columns.Count - 1)] : null;

    }
}
