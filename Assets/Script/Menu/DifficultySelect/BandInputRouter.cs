using System.Collections.Generic;
using YARG.Core.Input;
using YARG.Menu.Navigation;
using YARG.Player;

namespace YARG.Menu.DifficultySelect
{
    internal sealed class BandInputRouter
    {
        private readonly BandSessionState _session;
        private readonly IReadOnlyDictionary<BandColumnModel, BandColumn> _columnViews;

        public BandInputRouter(BandSessionState session, IReadOnlyDictionary<BandColumnModel, BandColumn> columnViews)
        {
            _session = session;
            _columnViews = columnViews;
        }

        public NavigationScheme CreateBandScheme()
        {
            return new NavigationScheme(new()
            {
                new NavigationScheme.Entry(MenuAction.Up, "Menu.Common.Up",
                    ctx =>
                    {
                        var col = ColumnFor(ctx.Player);
                        if (col != null)
                        {
                            col.NavigateVertical(-1, ctx.IsRepeat);
                        }
                    }),
                new NavigationScheme.Entry(MenuAction.Down, "Menu.Common.Down",
                    ctx =>
                    {
                        var col = ColumnFor(ctx.Player);
                        if (col != null)
                        {
                            col.NavigateVertical(1, ctx.IsRepeat);
                        }
                    }),
                new NavigationScheme.Entry(MenuAction.Green, "Menu.Common.Confirm", OnConfirm),
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", OnBack),
            }, allowsMusicPlayer: false);
        }

        private void OnConfirm(NavigationContext context)
        {
            if (_session.Started)
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
            if (_session.Started)
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

        public void OnNavigationEvent(NavigationContext context)
        {
            int direction = context.Action switch
            {
                MenuAction.Left  => -1,
                MenuAction.Right => 1,
                _                => 0,
            };

            if (direction != 0 && !_session.Started)
            {
                var column = ColumnFor(context.Player);
                if (column != null && column.AdjustSelection(direction))
                {
                    return;
                }
            }

            if (direction == 0 || context.Player != null || _columnViews.Count == 0)
            {
                return;
            }

            _session.MoveFocus(direction);
        }

        public bool InterceptOvershellInput(NavigationContext context)
        {
            if (_session.Hidden || !_session.CanEngage)
            {
                return false;
            }

            var column = FindColumn(context.Player);
            if (column == null)
            {
                return IsMenuExitAction(context.Action) && _session.AnyOvershellColumnOpen();
            }

            if (context.Action == MenuAction.Start)
            {
                return HandleOvershellStart(column);
            }

            if (!column.IsExpanded)
            {
                return IsMenuExitAction(context.Action) && _session.AnyOvershellColumnOpen();
            }

            DispatchOvershellAction(column, context);
            return true;
        }

        private bool HandleOvershellStart(BandColumn column)
        {
            if (column.IsJoining)
            {
                return true;
            }

            if (column.IsExpanded)
            {
                column.Back();
            }
            else if (_session.CanEngage)
            {
                column.Expand();
            }

            return true;
        }

        private static void DispatchOvershellAction(BandColumn column, NavigationContext context)
        {
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
        }

        private static bool IsMenuExitAction(MenuAction action)
        {
            return action is MenuAction.Green or MenuAction.Red;
        }

        private BandColumn ColumnFor(YargPlayer player)
        {
            var model = _session.ColumnFor(player);
            return model != null && _columnViews.TryGetValue(model, out var column) ? column : null;
        }

        private BandColumn FindColumn(YargPlayer player)
        {
            var model = _session.FindColumn(player);
            return model != null && _columnViews.TryGetValue(model, out var column) ? column : null;
        }
    }
}
