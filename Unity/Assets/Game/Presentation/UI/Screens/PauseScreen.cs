using System.Collections.Generic;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    public sealed class PauseArgs
    {
        public IClimb Session;
    }

    /// <summary>
    /// The Menu door of the run screens that have no menu list of their own (the map, the rest site, the shop, the event, the spoils): the web's quick menu (src/ui/components/quicknav.js, content/config/ui/presentation/uiContent.json
    /// quickMenuRows; docs/qa/web-screens/combat-quick-menu-*.jpg) hung under the menu button, with the rows of ui/menus.json pause in the web's order and bands: Settings and Controls (the two tabs of the Menu overlay,
    /// <see cref="SettingsScreen"/>), the Fullscreen and Music switches, the Armoury's Inventory and Character, Load, Save, Save and Quit and Quit Without Saving. Escape, the pad's Start and a press outside close it.
    /// A switch and Save act in place and leave it open; any other row closes it first (P-7a, D-1710).
    /// </summary>
    public sealed class PauseScreen : ScreenView
    {
        private IClimb _session;
        private QuickMenu _quick;
        private IReadOnlyList<QuickMenuRow> _rows = new List<QuickMenuRow>();

        public IReadOnlyList<Button> Rows => _quick?.Rows ?? new List<Button>();

        /// <summary>The first row: the menu opens on it.</summary>
        public Button ResumeButton => Rows.FirstOrDefault();

        /// <summary>The row of a ui/menus.json pause entry (a screen test presses it), or null.</summary>
        public Button Row(string id)
        {
            var index = _rows.ToList().FindIndex(r => r.Def.Id == id);
            return index < 0 ? null : Rows.ElementAtOrDefault(index);
        }

        protected override void OnBind(object args)
        {
            _session = (args as PauseArgs)?.Session ?? Ui.Climb;
            _quick = Root.Q<QuickMenu>(UiNames.PauseRows);
            _quick.Chosen += Choose;
            _quick.Refused += (id, anchor) => QuickMenuRows.Refuse(Context, _rows, id, anchor);
            Root.Q(UiNames.Scrim).RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.target == e.currentTarget) Resume();
            });
            Fill();
            FocusRow(0);
        }

        private void Fill() => _rows = QuickMenuRows.Fill(Context, _quick, _session);

        private void FocusRow(int index)
        {
            var row = Rows.ElementAtOrDefault(index);
            Context.Instance.LastFocused = row;
            if (row != null) Nav.FocusTop(row);
        }

        private void Choose(string id)
        {
            var def = Ui.Data.Menus.Pause.FirstOrDefault(e => e.Id == id);
            if (def == null) return;
            var index = _rows.ToList().FindIndex(r => r.Def.Id == id);
            if (QuickMenuRows.KeepsOpen(def, false))
            {
                if (def.Action == MenuActions.Toggle)
                {
                    PauseActions.Toggle(Context, _session, def);
                    Fill();
                    FocusRow(index);
                }
                else PauseActions.SaveHere(Context, _session, Rows.ElementAtOrDefault(index));
                return;
            }
            var context = Context;
            var session = _session;
            Resume();
            PauseActions.Activate(context, session, def);
        }

        /// <summary>Checkpoint, then the title; a failure opens W1r with Retry and leaves the menu open.</summary>
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
