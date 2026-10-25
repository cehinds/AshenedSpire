using System;
using System.Collections.Generic;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine.UIElements;
using TV = Ashen.Generated.TooltipPlacementValues;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// The flask action menu (web ui/components/flask.js mountFlaskActionMenu): a popover under the flask that was tapped, a caption naming the flask over the sentence it opens with, then one row per action
    /// (Use, Inspect, Drop). A shut action stays on the list and says why when it is pressed. Pressing a row raises <see cref="Chosen"/>; the host decides what each does and closes the menu.
    /// </summary>
    public sealed class FlaskMenu : VisualElement
    {
        private readonly LocLabel _title = new LocLabel { pickingMode = PickingMode.Ignore };
        private readonly LocLabel _detail = new LocLabel { pickingMode = PickingMode.Ignore };
        private readonly VisualElement _rows = new VisualElement();
        private readonly List<Button> _buttons = new List<Button>();

        public FlaskMenu()
        {
            AddToClassList(UiClasses.FlaskMenu);
            var shadow = new VisualElement { pickingMode = PickingMode.Ignore };
            shadow.AddToClassList(UiClasses.ShadowPlate);
            Add(shadow);
            var cap = new VisualElement { pickingMode = PickingMode.Ignore };
            cap.AddToClassList(UiClasses.FlaskMenuCap);
            _title.AddToClassList(UiClasses.FlaskMenuTitle);
            _title.AddToClassList(UiClasses.ValueText);
            _detail.AddToClassList(UiClasses.FlaskMenuDetail);
            _detail.AddToClassList(UiClasses.ValueText);
            cap.Add(_title);
            cap.Add(_detail);
            Add(cap);
            Add(_rows);
        }

        public IReadOnlyList<Button> Rows => _buttons;

        public event Action<Core2FlaskActionId> Chosen;

        /// <summary>A shut row was pressed: the row and the rules' words.</summary>
        public event Action<VisualElement, string> Refused;

        public void Bind(string name, string detail, IReadOnlyList<Core2FlaskAction> actions)
        {
            _title.SetResolved(name);
            _detail.SetResolved(detail ?? string.Empty);
            UiDom.Show(_detail, !string.IsNullOrEmpty(detail));
            _rows.Clear();
            _buttons.Clear();
            foreach (var action in actions)
            {
                var button = new Button();
                button.AddToClassList(UiClasses.FlaskMenuRow);
                button.AddToClassList(UiClasses.Touch);
                button.EnableInClassList(UiClasses.FlaskMenuRowDisabled, !action.Enabled);
                var label = new LocLabel { pickingMode = PickingMode.Ignore };
                label.AddToClassList(UiClasses.FlaskMenuRowLabel);
                label.AddToClassList(UiClasses.ValueText);
                label.SetResolved(action.Label);
                button.Add(label);
                var id = action.Id;
                var enabled = action.Enabled;
                var reason = action.Reason;
                button.clicked += () =>
                {
                    if (enabled) Chosen?.Invoke(id);
                    else Refused?.Invoke(button, reason);
                };
                _rows.Add(button);
                _buttons.Add(button);
            }
        }

        /// <summary>placeAnchored(intent 'under', align 'start'): the menu's left edge on the flask's, below it, kept inside the room.</summary>
        public void PlaceUnder(VisualElement anchor, VisualElement overlay)
        {
            var room = overlay.layout;
            var at = overlay.WorldToLocal(anchor.worldBound.position);
            var box = TooltipPlacement.Place(new PlacementBox(at.x, at.y, anchor.worldBound.width, anchor.worldBound.height), resolvedStyle.width, resolvedStyle.height, room.width, room.height, TV.Under, TV.AlignStart, Tooltip.Rules);
            style.left = (float)box.Left;
            style.top = (float)box.Top;
        }
    }
}
