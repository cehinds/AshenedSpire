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
    /// <summary>
    /// Shared behaviour of the node screens (W-09, W-11 / W4c, W-13; D-141n). They are opened by the climb's router
    /// (<see cref="RunFlow.Show"/>, with <see cref="RunScreenArgs"/>) at the run's location, and after every step they either
    /// redraw (the run still stands at their node) or hand back to the router (a fight to W-07, a pending reward to W-08, a
    /// dungeon shrine's stay to W-10, the map, the run's end). Also the RUN_HUD kit band, W-20 on Escape / pad Start /
    /// [Menu], a redraw when a door or the pause above closes, refusals at the control that asked (the kit Refusal), W1r
    /// "Could not save" with Retry when a step's save fails (the session restored the run first), and a focus keeper while
    /// the screen is on top.
    /// </summary>
    public abstract class NodeScreen : ScreenView
    {
        private RunHud _hud;

        public RunSession Session { get; private set; }

        protected StringTable Strings => Ui.Data.Strings;

        protected bool Ready { get; private set; }

        protected override void OnBind(object args)
        {
            Session = (args as RunScreenArgs)?.Session ?? Ui.Session;
            if (Session == null)
            {
                Debug.LogError(string.Format(CultureInfo.InvariantCulture, NodeMessages.NoSession, Context.Def.Id));
                return;
            }
            Ui.Session = Session;
            _hud = Root.Q<RunHud>(UiNames.RunHud);
            if (_hud != null) _hud.Menu += OpenPause;
            if (!Guard(Begin, null)) return;
            Ready = true;
            Render();
            // A rail that folds into its selector (or a redraw) can hide the focused control; the node screens never leave
            // the keyboard and pad without a focus while they are on top.
            Root.schedule.Execute(KeepFocus).Every(Ui.Data.Tokens.Duration(TokenKeys.FocusSettle));
        }

        /// <summary>Starts the screen's application session over the run session.</summary>
        protected abstract void Begin();

        public abstract void Render();

        private void KeepFocus()
        {
            if (Nav.Top != Context.Instance || Context.Instance.View.InputBlocked) return;
            var focused = Context.Instance.Focus.Focused;
            if (focused == null || !UiDom.IsShown(focused, Root.parent)) Nav.FocusTop();
        }

        protected void RenderHud()
        {
            if (_hud == null) return;
            _hud.Bind(RunHudView.Build(Session, Ui.Data));
            Ui.ApplyBackground(_hud.Portrait, Session.Portrait);
        }

        /// <summary>Sets art from a registry id when the catalog has it; returns whether it did.</summary>
        protected bool Art(VisualElement element, string assetId)
        {
            if (element == null) return false;
            var texture = assetId != null && Ui.Art != null ? Ui.Art.Get(assetId) : null;
            element.style.backgroundImage = texture != null ? new StyleBackground(texture) : new StyleBackground(StyleKeyword.None);
            return texture != null;
        }

        /// <summary>Runs a step; a failed save opens W1r "Could not save" with Retry (the session restored the run). False when it failed.</summary>
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

        /// <summary>After a step: redraw while the run stands at this screen's node, else the router shows where it stands now.</summary>
        protected void Follow()
        {
            var here = NodeScreens.LocationOf(Context.Def.Id);
            if (Session.Location == here && NodeScreens.Refine(Session.Content.Flow.ScreenFor(here), Session) == Context.Def.Id)
            {
                Render();
                return;
            }
            RunFlow.Show(Context, Session);
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
