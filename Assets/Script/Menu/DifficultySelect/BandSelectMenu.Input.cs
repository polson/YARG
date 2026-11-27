using UnityEngine;
using YARG.Core.Input;
using YARG.Menu.Navigation;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    public sealed partial class BandSelectMenu
    {
        private void OnConfirm(NavigationContext context)
        {
            if (_started)
            {
                return;
            }

            var column = ColumnFor(context.Player);
            if (column != null && !column.Expand())
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
            if (column == null || column.IsAtTopLevel)
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

            if (direction != 0 && !_started)
            {
                var column = ColumnFor(context.Player);
                if (column != null && column.AdjustSelection(direction))
                {
                    return;
                }
            }

            if (direction == 0 || context.Player != null || _columns.Count == 0)
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
            if (column == null)
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
            for (int i = 0; i < _columns.Count; i++)
            {
                var column = _columns[i];
                if (column.IsExpanded && !column.IsSittingOut && column.Player.DeviceInfo.HasControllerAssigned)
                {
                    return true;
                }
            }

            return false;
        }

        private BandColumn ColumnFor(YargPlayer player)
        {
            return player == null ? FocusedColumn : FindColumn(player);
        }

        private BandColumn FindColumn(YargPlayer player)
        {
            if (player == null)
            {
                return null;
            }

            for (int i = 0; i < _columns.Count; i++)
            {
                if (_columns[i].Player == player)
                {
                    return _columns[i];
                }
            }

            return null;
        }

        private BandColumn FocusedColumn =>
            _columns.Count > 0 ? _columns[Mathf.Clamp(_focusIndex, 0, _columns.Count - 1)] : null;
    }
}
