using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// Refusal (docs/design/04 §1, W-24; US-5.9): an inline message anchored to the control that was refused.
    /// It fades after the refusal duration token, and focus returns to (stays on) the control that started the
    /// action.
    /// </summary>
    [UxmlElement]
    public partial class Refusal : VisualElement
    {
        private readonly LocLabel _text;
        private IVisualElementScheduledItem _hide;

        public Refusal()
        {
            AddToClassList(nameof(Refusal).ToLowerInvariant());
            pickingMode = PickingMode.Ignore;
            UiDom.CloneTemplate(this, UiResources.KitRefusal);
            _text = this.Q<LocLabel>(UiNames.RefusalText);
        }

        [UxmlAttribute]
        public string textKey { get => _text?.stringKey; set { if (_text != null) _text.stringKey = value; } }

        public string Text => _text?.text;

        public void SetText(string resolved) => _text?.SetResolved(resolved);

        /// <summary>Shows text under anchor in the overlay layer for durationMs and keeps focus on anchor.</summary>
        public static Refusal Show(VisualElement anchor, VisualElement overlay, string text, int durationMs)
        {
            var refusal = overlay.Q<Refusal>() ?? new Refusal();
            if (refusal.parent != overlay) overlay.Add(refusal);
            refusal.SetText(text);
            var at = overlay.WorldToLocal(new Vector2(anchor.worldBound.xMin, anchor.worldBound.yMax));
            refusal.style.left = at.x;
            refusal.style.top = at.y;
            refusal.AddToClassList(UiClasses.RefusalVisible);
            refusal._hide?.Pause();
            refusal._hide = refusal.schedule.Execute(() => refusal.RemoveFromClassList(UiClasses.RefusalVisible)).StartingIn(durationMs);
            if (anchor.focusable) anchor.Focus();
            return refusal;
        }
    }
}
