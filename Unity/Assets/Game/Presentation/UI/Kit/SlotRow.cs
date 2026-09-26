using System;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>Display-ready values for one W-03 slot row.</summary>
    public sealed class SlotRowData
    {
        public int Index;
        public string Label;
        public string Facts;
        public string Saved;
        public Texture2D Portrait;
        public bool Empty;
        public bool Unreadable;
        public bool Deletable;
        public string DeleteName;
    }

    /// <summary>
    /// SlotRow (docs/design/04 §1, W-03): a radio-like row with portrait, "Slot n", class/act/floor/HP, saved time
    /// and seed, and a delete [✕]. Focusing or clicking selects it; pressing it again (or Submit while selected)
    /// activates it.
    /// </summary>
    [UxmlElement]
    public partial class SlotRow : VisualElement
    {
        private readonly LocLabel _mark;
        private readonly VisualElement _portrait;
        private readonly LocLabel _label;
        private readonly LocLabel _facts;
        private readonly LocLabel _saved;
        private readonly LocButton _delete;
        private bool _selected;

        public SlotRow()
        {
            AddToClassList(nameof(SlotRow).ToLowerInvariant());
            focusable = true;
            UiDom.CloneTemplate(this, UiResources.KitSlot);
            _mark = this.Q<LocLabel>(UiNames.SlotMark);
            _portrait = this.Q(UiNames.SlotPortrait);
            _label = this.Q<LocLabel>(UiNames.SlotLabel);
            _facts = this.Q<LocLabel>(UiNames.SlotFacts);
            _saved = this.Q<LocLabel>(UiNames.SlotSaved);
            _delete = this.Q<LocButton>(UiNames.SlotDelete);
            if (_delete != null) _delete.clicked += () => DeleteRequested?.Invoke(this);
            RegisterCallback<FocusInEvent>(e => { if (e.target == this) Selected?.Invoke(this); });
            RegisterCallback<PointerUpEvent>(e =>
            {
                if (_delete != null && _delete.worldBound.Contains(e.position)) return;
                if (_selected) Activated?.Invoke(this);
                else { Focus(); Selected?.Invoke(this); }
            });
            RegisterCallback<NavigationSubmitEvent>(e =>
            {
                if (e.target != this) return;
                Activated?.Invoke(this);
                e.StopPropagation();
            });
            SetSelected(false);
        }

        public SlotRowData Data { get; private set; }
        public LocButton DeleteButton => _delete;
        public bool IsSelected => _selected;

        public event Action<SlotRow> Selected;
        public event Action<SlotRow> Activated;
        public event Action<SlotRow> DeleteRequested;

        public void Bind(SlotRowData data)
        {
            Data = data;
            _label?.SetResolved(data.Label);
            _facts?.SetResolved(data.Facts);
            _saved?.SetResolved(data.Saved);
            UiDom.Show(_saved, !string.IsNullOrEmpty(data.Saved));
            if (_portrait != null)
            {
                _portrait.style.backgroundImage = data.Portrait != null ? new StyleBackground(data.Portrait) : new StyleBackground(StyleKeyword.None);
                _portrait.style.visibility = data.Portrait != null ? Visibility.Visible : Visibility.Hidden;
            }
            if (_delete != null)
            {
                UiDom.Show(_delete, data.Deletable);
                _delete.tooltip = data.DeleteName;
            }
            EnableInClassList(UiClasses.SlotEmpty, data.Empty);
            EnableInClassList(UiClasses.SlotUnreadable, data.Unreadable);
            EnableInClassList(UiClasses.SlotReady, !data.Empty && !data.Unreadable);
        }

        public void SetSelected(bool selected)
        {
            _selected = selected;
            EnableInClassList(UiClasses.Selected, selected);
            _mark?.SetResolved(Loc.Get(selected ? StringKeys.GlyphRadioOn : StringKeys.GlyphRadioOff));
        }
    }
}
