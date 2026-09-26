using System.Collections.Generic;
using System.Linq;
using Ashen.App.Nodes;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// W-13 legacy dungeon (W4b variant; US-4.5), bound to <see cref="DungeonSession"/>: the SCENE (the catalog scene's
    /// background and floor, the player's figure, the occupant of a hostile node), the SCENE_TRAY (the node's name, the
    /// speaker's line or the chosen response's text, and its controls — the node's responses: Listen, Fight, Flee with its
    /// Dexterity odds; the room's Rest / Open / Fight; the exits, the boss door marked ☠; Continue; Leave once cleared,
    /// behind a door that states the outcome) and the scene-graph mini map (the dungeon's map art with the nodes: current,
    /// visited, resolved, reachable, boss). A fight goes to W-07, a cache to W-08; every step is saved. Escape and pad Start
    /// open W-20.
    /// </summary>
    public sealed class DungeonScreen : NodeScreen
    {
        private readonly List<Button> _actions = new List<Button>();
        private DungeonSession _dungeon;
        private DungeonViewState _view;

        public DungeonSession Dungeon => _dungeon;
        public DungeonViewState View => _view;
        public IReadOnlyList<Button> Actions => _actions;

        protected override void Begin() => _dungeon = DungeonSession.Start(Session.NodeContext(), Session, Entry);

        public override void Render()
        {
            if (_dungeon == null || _dungeon.Left) return;
            var keep = Context.Instance.Focus.Focused?.name;
            _view = DungeonView.Build(_dungeon, Ui.Data);
            RenderHud();
            Art(Root.Q(UiNames.SceneBackground), _view.BackgroundId);
            Art(Root.Q(UiNames.SceneFloor), _view.FloorId);
            Art(Root.Q(UiNames.SceneFigure), Session.Portrait);
            var occupant = Root.Q(UiNames.SceneOccupant);
            UiDom.Show(occupant, Art(occupant, _view.OccupantArtId));
            Root.Q<LocLabel>(UiNames.SceneName)?.SetResolved(_view.SceneTitle);
            Root.Q<LocLabel>(UiNames.SceneLine)?.SetResolved(_view.Line);
            Root.Q<LocLabel>(UiNames.SceneNote)?.SetResolved(_view.Note);
            RenderActions();
            RenderMap();
            var again = keep == null ? null : _actions.FirstOrDefault(a => a.name == keep);
            if (again != null) again.Focus();
            else if (keep != null) FocusSoon(_actions.FirstOrDefault());
        }

        private void RenderActions()
        {
            var tray = Root.Q(UiNames.TrayActions);
            tray.Clear();
            _actions.Clear();
            foreach (var action in _view.Actions)
            {
                var button = new LocButton { name = action.Kind + NodeFormats.KeySeparator + (action.Id ?? string.Empty) };
                button.SetResolved(action.Label);
                button.AddToClassList(NodeClasses.TrayButton);
                button.AddToClassList(NodeClasses.TrayButtonPrefix + action.Kind);
                button.EnableInClassList(NodeClasses.TrayButtonBoss, action.Boss);
                button.EnableInClassList(UiClasses.ButtonReady, action.Kind == NodeValues.ActionContinue || action.Kind == NodeValues.ActionRoom);
                var captured = action;
                button.clicked += () => Press(captured, button);
                tray.Add(button);
                _actions.Add(button);
            }
        }

        private void RenderMap()
        {
            var map = Root.Q(UiNames.SceneMap);
            UiDom.Show(map, Art(map, _view.MapId));
            var dots = Root.Q(UiNames.MapDots);
            dots.Clear();
            foreach (var node in _view.Map)
            {
                var dot = new VisualElement { pickingMode = PickingMode.Ignore };
                dot.AddToClassList(NodeClasses.MapDot);
                dot.EnableInClassList(NodeClasses.MapDotVisited, node.Visited);
                dot.EnableInClassList(NodeClasses.MapDotResolved, node.Resolved);
                dot.EnableInClassList(NodeClasses.MapDotReachable, node.Reachable);
                dot.EnableInClassList(NodeClasses.MapDotBoss, node.Boss);
                dot.EnableInClassList(NodeClasses.MapDotCurrent, node.Current);
                dot.style.left = new Length((float)node.X, LengthUnit.Percent);
                dot.style.top = new Length((float)node.Y, LengthUnit.Percent);
                dots.Add(dot);
            }
        }

        /// <summary>One tray control: a response, the room, an exit, Continue or Leave.</summary>
        public void Press(DungeonAction action, VisualElement anchor)
        {
            switch (action.Kind)
            {
                case NodeValues.ActionResponse:
                    if (Guard(() => _dungeon.Choose(action.Id), () => Press(action, anchor)))
                    {
                        Render();
                        FocusSoon(_actions.FirstOrDefault());
                    }
                    return;
                case NodeValues.ActionContinue:
                {
                    NodeExit exit = null;
                    if (!Guard(() => exit = _dungeon.Continue(), () => Press(action, anchor))) return;
                    if (exit == null || exit.Kind == NodeValues.ExitStay)
                    {
                        Render();
                        FocusSoon(_actions.FirstOrDefault());
                        return;
                    }
                    Leave(exit);
                    return;
                }
                case NodeValues.ActionRoom:
                {
                    NodeExit exit = null;
                    if (!Guard(() => exit = _dungeon.EnterRoom(), () => Press(action, anchor))) return;
                    if (exit == null || exit.Kind == NodeValues.ExitStay) Render();
                    else Leave(exit);
                    return;
                }
                case NodeValues.ActionTravel:
                {
                    var moved = false;
                    if (!Guard(() => moved = _dungeon.Travel(action.Id), () => Press(action, anchor))) return;
                    if (!moved)
                    {
                        Refuse(anchor, Strings.Get(NodeStringKeys.NodesDungeonRefused));
                        return;
                    }
                    Render();
                    FocusSoon(_actions.FirstOrDefault());
                    return;
                }
                case NodeValues.ActionLeave:
                    ConfirmRequest.Open(Nav, new ConfirmRequest
                    {
                        ConfirmId = ConfirmIds.DungeonLeave,
                        Args = new StringArgs().Add(NodePlaceholders.Dungeon, _view.DungeonName).Add(NodePlaceholders.Outcome, _view.LeaveText),
                        Target = _view.SceneTitle,
                        OnConfirm = LeaveDungeon,
                    });
                    return;
            }
        }

        private void LeaveDungeon()
        {
            NodeExit exit = null;
            if (!Guard(() => exit = _dungeon.Leave(), LeaveDungeon)) return;
            Leave(exit);
        }

        /// <summary>Presses the first tray control of a kind (and id) — smoke tests and captures.</summary>
        public void PressForReview(string kind, string id = null)
        {
            var i = _view?.Actions.FindIndex(a => a.Kind == kind && (id == null || a.Id == id)) ?? -1;
            if (i >= 0) Press(_view.Actions[i], i < _actions.Count ? _actions[i] : null);
        }
    }
}
