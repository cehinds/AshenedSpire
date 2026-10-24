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
    /// W-20 pause (AF-12), a W1 frame without categories over the run screen: the seed, the rows from ui/menus.json pause
    /// in order (Deck, Armoury out of combat only, Settings, Save &amp; quit ⟲, Abandon run), and a full-width Resume.
    /// Rows whose screen is planned are disabled with a "Later build" note (D-058). Save &amp; quit opens its hold door,
    /// checkpoints the run and returns to the title; a failed save opens W1r "Could not save" with Retry (it never replays
    /// gameplay). Escape, the exit control and pad Start resume.
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
            _shell.SetFooter(null, StringKeys.PauseResume);
            _shell.PrimaryButton?.AddToClassList(UiClasses.ButtonReady);
            _shell.Primary += Resume;
            _shell.Exit += Resume;
            if (_session != null)
                Root.Q<LocLabel>(UiNames.PauseSeed)?.SetResolved(strings.Format(StringKeys.PauseSeed, new StringArgs().Add(UiPlaceholders.Seed, _session.SeedText))
                    + strings.Get(StringKeys.CreationStatJoin) + strings.Format(StringKeys.PauseSaved, new StringArgs().Add(UiPlaceholders.Slot, _session.SlotIndex)));
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

        private void Activate(MenuEntryDef def, LocButton button) => PauseActions.Activate(Context, _session, def);

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
