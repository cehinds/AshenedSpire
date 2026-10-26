using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    public sealed class PauseArgs
    {
        public IClimb Session;
    }

    /// <summary>
    /// W-20 pause (AF-12), a W1 frame without categories over the run screen: the rows from ui/menus.json pause in order
    /// (Settings, Armoury out of combat only, Save, Save and Quit, Quit Without Saving: the web's quick menu rows that this build has; the Deck and Abandon rows
    /// are hidden by their data, P-6 D-1647). Rows whose screen is planned are disabled with a "Later build" note (D-058). Save and Quit checkpoints the run and
    /// returns to the title with its gate down again; Quit Without Saving asks first; a failed save opens the save door with Retry (it never replays gameplay).
    /// Escape, the exit control and pad Start resume.
    /// </summary>
    public sealed class PauseScreen : ScreenView
    {
        private readonly List<LocButton> _rows = new List<LocButton>();
        private IClimb _session;
        private Shell _shell;

        public IReadOnlyList<LocButton> Rows => _rows;
        public LocButton ResumeButton => _shell?.PrimaryButton;

        protected override void OnBind(object args)
        {
            _session = (args as PauseArgs)?.Session ?? Ui.Climb;
            var strings = Ui.Data.Strings;
            _shell = Root.Q<Shell>(UiNames.Shell);
            // The web's Menu has no Resume button and no seed line (menuComponents.js: the close X, Save and Quit, Save Game); the way back is the exit control and Escape.
            _shell.SetFooter(null, null);
            _shell.Primary += Resume;
            _shell.Exit += Resume;
            UiDom.Show(Root.Q(UiNames.PauseSeed), false);
            var list = Root.Q(UiNames.PauseRows);
            var context = Ui.Data.MenuContext(true, _session?.IsInCombat ?? false);
            foreach (var state in MenuRules.Evaluate(Ui.Data.Menus.Pause, context).Where(s => s.Visible))
            {
                var row = new VisualElement();
                row.AddToClassList(UiClasses.PauseRow);
                var button = new LocButton(state.Def.LabelKey);
                button.SetEnabled(state.Enabled);
                var def = state.Def;
                button.clicked += () => Activate(def, button);
                row.Add(button);
                if (!state.Enabled)
                {
                    var hint = new LocLabel(StringKeys.PauseLater);
                    hint.AddToClassList(UiClasses.PauseHint);
                    hint.AddToClassList(UiClasses.ValueText);
                    row.Add(hint);
                }
                list.Add(row);
                _rows.Add(button);
            }
        }

        private void Activate(MenuEntryDef def, LocButton button) => PauseActions.Activate(Context, _session, def, button);

        /// <summary>Checkpoint, then the title; a failure opens W1r with Retry and leaves the pause open.</summary>
        public void SaveAndQuit() => PauseActions.SaveAndQuit(Context, _session);

        /// <summary>Abandon run (D-134): the run ends as abandoned and W-15 shows its summary; a save failure opens W1r with Retry.</summary>
        public void Abandon() => PauseActions.Abandon(Context, _session);

        public void Resume() => Nav.Close(Context.Instance);

        public override bool HandleMenu()
        {
            Resume();
            return true;
        }
    }
}
