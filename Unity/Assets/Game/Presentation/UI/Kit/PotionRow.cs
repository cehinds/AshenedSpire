using System;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>What one flask row says: the flask art, its name, what it does, how many remain and whether Use is offered (the rules' refusal otherwise).</summary>
    public sealed class PotionRowData
    {
        public Texture2D Art;
        public string Name;
        public string Description;
        public string Meta;
        public string UseLabel;
        public string Refusal;
        public int HoldMs;
        public bool HoldEnabled = true;
    }

    /// <summary>
    /// A flask in the Potions list (src/ui/components/combatActionRow.js:151-198): the flask art, the name in gold, the effect and charges in words, the count, and Use with its HOLD tag.
    /// A flask the rules refuse stays on the list, dimmed, and pressing Use shows the rules' words (<see cref="Refused"/>). Committing Use (a hold, or a press when holds are off) raises
    /// <see cref="UseCommitted"/>.
    /// </summary>
    public sealed class PotionRow : VisualElement
    {
        private readonly VisualElement _icon = new VisualElement { pickingMode = PickingMode.Ignore };
        private readonly LocLabel _name = new LocLabel { pickingMode = PickingMode.Ignore };
        private readonly LocLabel _desc = new LocLabel { pickingMode = PickingMode.Ignore };
        private readonly LocLabel _meta = new LocLabel { pickingMode = PickingMode.Ignore };
        private string _refusal;

        public PotionRow()
        {
            AddToClassList(UiClasses.PotionRow);
            _icon.AddToClassList(UiClasses.PotionIcon);
            var text = new VisualElement { pickingMode = PickingMode.Ignore };
            text.AddToClassList(UiClasses.PotionText);
            _name.AddToClassList(UiClasses.PotionName);
            _name.AddToClassList(UiClasses.ValueText);
            _desc.AddToClassList(UiClasses.PotionDesc);
            _desc.AddToClassList(UiClasses.ValueText);
            _meta.AddToClassList(UiClasses.PotionMeta);
            _meta.AddToClassList(UiClasses.ValueText);
            text.Add(_name);
            text.Add(_desc);
            text.Add(_meta);
            Use = new HoldButton();
            Use.AddToClassList(UiClasses.PotionUse);
            Use.clicked += () =>
            {
                if (_refusal != null) Refused?.Invoke(_refusal);
            };
            Use.Committed += () =>
            {
                if (_refusal == null) UseCommitted?.Invoke();
            };
            Add(_icon);
            Add(text);
            Add(Use);
        }

        public HoldButton Use { get; }

        /// <summary>Use was held to the end (or pressed with holds off) on a flask the rules allow.</summary>
        public event Action UseCommitted;

        /// <summary>Use was pressed on a flask the rules refuse: the rules' words.</summary>
        public event Action<string> Refused;

        public void Bind(PotionRowData data)
        {
            _refusal = data.Refusal;
            _icon.style.backgroundImage = data.Art != null ? new StyleBackground(data.Art) : new StyleBackground(StyleKeyword.None);
            _name.SetResolved(data.Name);
            _desc.SetResolved(data.Description ?? string.Empty);
            UiDom.Show(_desc, !string.IsNullOrEmpty(data.Description));
            _meta.SetResolved(data.Meta ?? string.Empty);
            UiDom.Show(_meta, !string.IsNullOrEmpty(data.Meta));
            Use.labelKey = null;
            Use.Q<LocLabel>(UiNames.HoldLabel)?.SetResolved(data.UseLabel);
            Use.HoldMs = data.HoldMs;
            Use.HoldEnabled = data.HoldEnabled && data.Refusal == null;
            EnableInClassList(UiClasses.PotionRowDisabled, data.Refusal != null);
        }
    }
}
