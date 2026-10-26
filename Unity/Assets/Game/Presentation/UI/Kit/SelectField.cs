using System;
using System.Collections.Generic;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>One choice of a SelectField: the stored value and the words.</summary>
    public sealed class SelectOption
    {
        public string Value;
        public string Label;
    }

    /// <summary>
    /// The web's native select (kit.css select rules; settings.js renders every enum setting and every group picker as a select, on the
    /// wide screen and the phone): a dark face with the chosen words and a chevron, and a list of the choices. UI Toolkit's popup is
    /// clipped by the settings' scroll view and sits outside the screen's focus regions, so the list opens in place under the face and
    /// pushes the rows below it down. The element carries nav--open while it is open, which the Navigator's Back closes first (the
    /// same rule as CategoryNav's selector), and its options are ordinary focusable buttons, so keys, pad and pointer all work.
    /// </summary>
    public sealed class SelectField : VisualElement
    {
        private readonly LocButton _face;
        private readonly LocLabel _value;
        private readonly VisualElement _list;
        private readonly List<LocButton> _options = new List<LocButton>();

        public SelectField()
        {
            AddToClassList(ChromeClasses.Select);
            _face = new LocButton();
            _face.AddToClassList(ChromeClasses.SelectFace);
            _value = new LocLabel { pickingMode = PickingMode.Ignore };
            _value.AddToClassList(ChromeClasses.SelectValue);
            _face.Add(_value);
            var chevron = new LocLabel(StringKeys.SelectChevron) { pickingMode = PickingMode.Ignore };
            chevron.AddToClassList(ChromeClasses.SelectChevron);
            _face.Add(chevron);
            _face.clicked += Toggle;
            Add(_face);
            _list = new VisualElement();
            _list.AddToClassList(ChromeClasses.SelectList);
            Add(_list);
        }

        /// <summary>The control that takes focus (and carries the setting's key for the screen to refocus after a change).</summary>
        public LocButton Face => _face;

        public bool IsOpen => ClassListContains(UiClasses.NavOpen);

        /// <summary>A choice was made (its value). The list closes first.</summary>
        public event Action<string> Picked;

        /// <summary>The words the face shows while nothing is chosen (the smith's 'Choose an item').</summary>
        public void SetOptions(IReadOnlyList<SelectOption> options, string current, string placeholder = null)
        {
            _list.Clear();
            _options.Clear();
            _value.SetResolved(placeholder ?? string.Empty);
            foreach (var option in options)
            {
                var button = new LocButton();
                button.AddToClassList(ChromeClasses.SelectOption);
                var chosen = option.Value == current;
                button.EnableInClassList(UiClasses.Selected, chosen);
                button.SetResolved(option.Label);
                if (chosen) _value.SetResolved(option.Label);
                var value = option.Value;
                button.clicked += () =>
                {
                    Close();
                    _face.Focus();
                    Picked?.Invoke(value);
                };
                _list.Add(button);
                _options.Add(button);
            }
        }

        public void Close() => RemoveFromClassList(UiClasses.NavOpen);

        private void Toggle()
        {
            if (IsOpen) { Close(); return; }
            AddToClassList(UiClasses.NavOpen);
            var chosen = _options.Find(o => o.ClassListContains(UiClasses.Selected)) ?? (_options.Count > 0 ? _options[0] : null);
            chosen?.Focus();
        }
    }
}
