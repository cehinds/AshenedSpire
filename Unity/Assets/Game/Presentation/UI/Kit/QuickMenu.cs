using System;
using System.Collections.Generic;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>One row of the quick menu: its words, an optional note at the right (a disabled row says why), whether it is offered, whether it ends the run (red) and whether it starts a new band.</summary>
    public sealed class QuickRowData
    {
        public string Id;
        public string Label;
        public string Note;
        public bool Enabled = true;
        public bool Danger;
        public bool Band;

        /// <summary>The tooltip's words (web quicknav attachTooltip: the label as its title, the row's tip), or null.</summary>
        public string Tip;

        /// <summary>The Menu overlay's open tab: the row reads in gold (web .qn-row.on).</summary>
        public bool Current;

        /// <summary>A switch row: its sentence (web .qn-condition) and its On or Off pill (.qn-state).</summary>
        public bool Switch;

        public bool Checked;
        public string Condition;
        public string State;
    }

    /// <summary>
    /// The in-combat quick menu (src/ui/components/quicknav.js, docs/qa/web-screens/combat-quick-menu-*.jpg): a dropdown panel under the menu button with a muted caption and the menu
    /// rows, bands divided by hairlines, an exit row in red. Pressing a row raises <see cref="Chosen"/> with its id; the host decides what each does and closes the menu.
    /// </summary>
    [UxmlElement]
    public partial class QuickMenu : VisualElement
    {
        private readonly VisualElement _rows;
        private readonly List<Button> _buttons = new List<Button>();

        public QuickMenu()
        {
            AddToClassList(nameof(QuickMenu).ToLowerInvariant());
            UiDom.CloneTemplate(this, UiResources.KitQuickMenu);
            _rows = this.Q(UiNames.QuickRows);
        }

        public IReadOnlyList<Button> Rows => _buttons;

        public event Action<string> Chosen;

        /// <summary>Raised when a disabled row is pressed (its anchor and the id), so the host can say why.</summary>
        public event Action<string, VisualElement> Refused;

        /// <summary>Gives each row that has a tip its tooltip (the row's label as the title), the way the web's quick menu does (quicknav.js attachTooltip).</summary>
        public void AttachTips(IReadOnlyList<QuickRowData> rows, VisualElement overlay, string sizeId, int delayMs)
        {
            for (var i = 0; i < rows.Count && i < _buttons.Count; i++)
                if (!string.IsNullOrEmpty(rows[i].Tip)) Tooltip.AttachResolved(_buttons[i], overlay, sizeId, rows[i].Label, rows[i].Tip, delayMs);
        }

        public void SetRows(IReadOnlyList<QuickRowData> rows)
        {
            _rows.Clear();
            _buttons.Clear();
            foreach (var row in rows)
            {
                var button = new Button();
                button.AddToClassList(UiClasses.QuickRow);
                button.AddToClassList(UiClasses.Touch);
                button.EnableInClassList(UiClasses.QuickRowBand, row.Band);
                button.EnableInClassList(UiClasses.QuickRowDanger, row.Danger);
                button.EnableInClassList(UiClasses.QuickRowDisabled, !row.Enabled);
                button.EnableInClassList(MenuClasses.QuickRowOn, row.Current);
                var label = new LocLabel { pickingMode = PickingMode.Ignore };
                label.AddToClassList(UiClasses.QuickRowLabel);
                label.AddToClassList(UiClasses.ValueText);
                label.SetResolved(row.Label);
                button.Add(label);
                if (row.Switch)
                {
                    var condition = new LocLabel { pickingMode = PickingMode.Ignore };
                    condition.AddToClassList(MenuClasses.QuickCondition);
                    condition.SetResolved(row.Condition);
                    button.Add(condition);
                    var state = new LocLabel { pickingMode = PickingMode.Ignore };
                    state.AddToClassList(MenuClasses.QuickState);
                    state.EnableInClassList(MenuClasses.QuickStateOn, row.Checked);
                    state.SetResolved(row.State);
                    button.Add(state);
                }
                if (!string.IsNullOrEmpty(row.Note))
                {
                    var note = new LocLabel { pickingMode = PickingMode.Ignore };
                    note.AddToClassList(UiClasses.QuickRowNote);
                    note.AddToClassList(UiClasses.ValueText);
                    note.SetResolved(row.Note);
                    button.Add(note);
                }
                var id = row.Id;
                var enabled = row.Enabled;
                button.clicked += () =>
                {
                    if (enabled) Chosen?.Invoke(id);
                    else Refused?.Invoke(id, button);
                };
                _rows.Add(button);
                _buttons.Add(button);
            }
        }
    }
}
