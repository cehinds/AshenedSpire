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
                var label = new LocLabel { pickingMode = PickingMode.Ignore };
                label.AddToClassList(UiClasses.QuickRowLabel);
                label.AddToClassList(UiClasses.ValueText);
                label.SetResolved(row.Label);
                button.Add(label);
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
