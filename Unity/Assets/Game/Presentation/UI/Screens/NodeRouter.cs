using System;
using System.Globalization;
using Ashen.App.Nodes;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>What a node screen is opened with: the run and the start (or resume) entry.</summary>
    public sealed class NodeArgs
    {
        public RunSession Session;
        public NodeEntry Entry;
    }

    /// <summary>
    /// The node screens' routing (D-141n). <see cref="Open"/> is the entry point the act map's router calls after
    /// RunLoop.EnterNode: <c>NodeRouter.Open(nav, ui, session, NodeScreens.For(session.Content.Loop, outcome))</c> opens
    /// merchant, event, dialogue or legacyDungeon with that payload (false for any other outcome). <see cref="Resume"/>
    /// reopens the node screen a loaded run (title Continue, W-03 Load) or a returning one (W-08 Continue inside a dungeon)
    /// stands on. <see cref="Exit"/> follows a node screen's exit: a handed-over fight to W-07 (saved as it starts), a
    /// dungeon cache to W-08, the climb's victory to W-15 (or the title while it is planned), anything else to W-06 (or
    /// the title while it is planned).
    /// </summary>
    public static class NodeRouter
    {
        public static bool Open(Navigator nav, UiContext ui, RunSession session, NodeEntry entry)
        {
            if (nav == null || session == null || entry == null || !NodeScreens.IsNodeScreen(entry.Screen) || !ui.Data.Screens.IsBuilt(entry.Screen)) return false;
            ui.Session = session;
            nav.Go(entry.Screen, new NodeArgs { Session = session, Entry = entry }, true);
            return true;
        }

        public static bool Resume(Navigator nav, UiContext ui, RunSession session) => session != null && Open(nav, ui, session, session.ResumeNode);

        public static void Exit(ScreenContext context, RunSession session, NodeExit exit)
        {
            if (exit == null || exit.Kind == NodeValues.ExitStay) return;
            var nav = context.Navigator;
            var ui = context.Ui;
            switch (exit.Kind)
            {
                case NodeValues.ExitFight:
                    session.StartFight(exit.Fight);
                    ui.Session = session;
                    nav.Go(ScreenIds.Combat, new CombatArgs { Session = session }, true);
                    return;
                case NodeValues.ExitRewards:
                    ui.Session = session;
                    nav.Go(ScreenIds.Rewards, new RewardsArgs { Session = session }, true);
                    return;
                case NodeValues.ExitVictory:
                    ui.Session = null;
                    nav.Go(ui.Data.Screens.IsBuilt(ScreenIds.RunEnd) ? ScreenIds.RunEnd : ScreenIds.Title, null, true);
                    return;
                default:
                    if (ui.Data.Screens.IsBuilt(ScreenIds.ActMap))
                    {
                        nav.Go(ScreenIds.ActMap, null, true);
                        return;
                    }
                    ui.Session = null;
                    nav.Go(ScreenIds.Title, null, true);
                    return;
            }
        }
    }

    /// <summary>
    /// Shared behaviour of the node screens: the RUN_HUD band (identity, act and floor, HP and MP, cinders, [Menu]), W-20 on
    /// Escape / pad Start / [Menu], a redraw when a door or the pause above closes, refusals at the control that asked
    /// (the kit Refusal), and W1r "Could not save" with Retry when a step's save fails (the step is undone first).
    /// </summary>
    public abstract class NodeScreen : ScreenView
    {
        public RunSession Session { get; private set; }
        public NodeEntry Entry { get; private set; }

        protected StringTable Strings => Ui.Data.Strings;

        protected bool Ready { get; private set; }

        protected override void OnBind(object args)
        {
            var node = args as NodeArgs;
            Session = node?.Session ?? Ui.Session;
            Entry = node?.Entry ?? Session?.ResumeNode;
            if (Session == null || Entry == null)
            {
                Debug.LogError(string.Format(CultureInfo.InvariantCulture, NodeMessages.NoSession, Context.Def.Id));
                return;
            }
            Ui.Session = Session;
            var menu = Root.Q<LocButton>(UiNames.HudMenu);
            if (menu != null) menu.clicked += OpenPause;
            if (!Guard(Begin, () => OnBind(args))) return;
            Ready = true;
            Render();
        }

        /// <summary>Starts the screen's application session (it saves the run at this screen).</summary>
        protected abstract void Begin();

        public abstract void Render();

        protected void RenderHud()
        {
            var hud = NodeHudView.Build(Session.Run, Session.Name, Session.Portrait, Ui.Data);
            var portrait = Root.Q(UiNames.HudPortrait);
            if (portrait != null) Ui.ApplyBackground(portrait, hud.Portrait);
            Root.Q<LocLabel>(UiNames.HudName)?.SetResolved(hud.Identity);
            Root.Q<LocLabel>(UiNames.HudTrail)?.SetResolved(hud.Trail);
            Root.Q<Meter>(UiNames.HudHp)?.Set(hud.Hp, hud.HpMax);
            Root.Q<Meter>(UiNames.HudMp)?.Set(hud.Mana, hud.ManaMax);
            Root.Q<LocLabel>(UiNames.HudCinders)?.SetResolved(hud.Cinders);
        }

        /// <summary>Sets art from a registry id when the catalog has it; returns whether it did.</summary>
        protected bool Art(VisualElement element, string assetId)
        {
            if (element == null) return false;
            var texture = assetId != null && Ui.Art != null ? Ui.Art.Get(assetId) : null;
            element.style.backgroundImage = texture != null ? new StyleBackground(texture) : new StyleBackground(StyleKeyword.None);
            return texture != null;
        }

        /// <summary>Runs a step; a failed save opens W1r "Could not save" with Retry (the session undid the step). False when it failed.</summary>
        protected bool Guard(Action step, Action retry)
        {
            try
            {
                step();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError(string.Format(CultureInfo.InvariantCulture, NodeMessages.NodeFailed, e));
                if (Ready) Render();
                ConfirmRequest.Open(Nav, new ConfirmRequest { ConfirmId = ConfirmIds.SaveFailed, OnConfirm = () => retry?.Invoke() });
                return false;
            }
        }

        protected void Refuse(VisualElement anchor, string text)
        {
            if (anchor == null || string.IsNullOrEmpty(text)) return;
            Refusal.Show(anchor, Context.Overlay, text, Ui.Data.Tokens.Duration(TokenKeys.Refusal));
        }

        /// <summary>Focus an element once layout catches up; a redraw in between is followed by name (views rebuild their rows).</summary>
        protected void FocusSoon(VisualElement element)
        {
            if (element == null) return;
            var name = element.name;
            Context.Instance.LastFocused = element;
            Root.schedule.Execute(() =>
            {
                var target = element.panel != null ? element : string.IsNullOrEmpty(name) ? null : Root.Q(name);
                if (target == null) return;
                Context.Instance.LastFocused = target;
                Nav.FocusTop(target);
            });
        }

        /// <summary>Follow a node exit; a failed save of a handed-over fight opens W1r with Retry.</summary>
        protected void Leave(NodeExit exit)
        {
            if (exit == null) return;
            Guard(() => NodeRouter.Exit(Context, Session, exit), () => Leave(exit));
        }

        protected void OpenPause() => Nav.OpenModal(ScreenIds.Pause, new PauseArgs { Session = Session });

        public override bool HandleBack()
        {
            OpenPause();
            return true;
        }

        public override bool HandleMenu()
        {
            OpenPause();
            return true;
        }

        public override void OnReturned()
        {
            if (Ready && !Session.RunOver) Render();
        }
    }
}
