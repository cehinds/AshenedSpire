using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// Tooltip (docs/design/04 §0, §1): sizes WT0–WT3 (ui/components.json tooltip.sizes → tooltip--size), glyph,
    /// title and templated body. Attach() shows one in the overlay layer after the tooltip delay token when the
    /// target is hovered or focused, anchored below the target.
    /// </summary>
    [UxmlElement]
    public partial class Tooltip : VisualElement
    {
        private readonly LocLabel _glyph;
        private readonly LocLabel _title;
        private readonly LocLabel _body;
        private readonly VisualElement _arrow;
        private string _size;
        private float _anchorX = float.NaN;
        private float _inset = float.NaN;

        public Tooltip()
        {
            AddToClassList(nameof(Tooltip).ToLowerInvariant());
            pickingMode = PickingMode.Ignore;
            UiDom.CloneTemplate(this, UiResources.KitTooltip);
            _glyph = this.Q<LocLabel>(UiNames.TooltipGlyph);
            _title = this.Q<LocLabel>(UiNames.TooltipTitle);
            _body = this.Q<LocLabel>(UiNames.TooltipBody);
            _arrow = this.Q(UiNames.TooltipArrow);
            RegisterCallback<GeometryChangedEvent>(_ => PlaceArrow());
        }

        /// <summary>
        /// WT0.ARROW (web tooltip.js pointArrow): the arrow sits on the edge that faces the trigger and points at its centre, kept the
        /// inset (its USS left) clear of the corners. anchorX is the trigger's centre in the tooltip's parent's coordinates.
        /// </summary>
        public void PointAt(float anchorX)
        {
            _anchorX = anchorX;
            PlaceArrow();
        }

        private void PlaceArrow()
        {
            if (_arrow == null || float.IsNaN(_anchorX)) return;
            var width = resolvedStyle.width;
            var arrowW = _arrow.resolvedStyle.width;
            if (float.IsNaN(width) || float.IsNaN(arrowW)) return;
            if (float.IsNaN(_inset)) _inset = _arrow.resolvedStyle.left;
            var inset = _inset;
            var left = _anchorX - resolvedStyle.left - arrowW * (float)UiMath.Half;
            _arrow.style.left = Mathf.Clamp(left, inset, Mathf.Max(inset, width - inset - arrowW));
        }

        [UxmlAttribute]
        public string size
        {
            get => _size;
            set
            {
                if (_size != null) RemoveFromClassList(UiClasses.TooltipSizePrefix + _size);
                _size = value;
                if (_size != null) AddToClassList(UiClasses.TooltipSizePrefix + _size);
            }
        }

        [UxmlAttribute] public string glyphKey { get => _glyph?.stringKey; set { if (_glyph != null) { _glyph.stringKey = value; UiDom.Show(_glyph, !string.IsNullOrEmpty(value)); } } }
        [UxmlAttribute] public string titleKey { get => _title?.stringKey; set { if (_title != null) _title.stringKey = value; } }
        [UxmlAttribute] public string bodyKey { get => _body?.stringKey; set { if (_body != null) _body.stringKey = value; } }

        public void SetText(string title, string body)
        {
            _title?.SetResolved(title);
            _body?.SetResolved(body);
        }

        /// <summary>Hover/focus tooltip for target, shown in overlay after delayMs.</summary>
        public static Tooltip Attach(VisualElement target, VisualElement overlay, string sizeId, string titleKey, string bodyKey, int delayMs) =>
            Hook(new Tooltip { size = sizeId, titleKey = titleKey, bodyKey = bodyKey }, target, overlay, delayMs);

        /// <summary>Hover/focus tooltip with already-resolved text (a refusal reason, a filled template).</summary>
        public static Tooltip AttachResolved(VisualElement target, VisualElement overlay, string sizeId, string title, string body, int delayMs)
        {
            var tip = new Tooltip { size = sizeId };
            tip.SetText(title, body);
            return Hook(tip, target, overlay, delayMs);
        }

        private static Tooltip Hook(Tooltip tip, VisualElement target, VisualElement overlay, int delayMs)
        {
            IVisualElementScheduledItem pending = null;
            void Show()
            {
                if (tip.parent != overlay) overlay.Add(tip);
                var at = overlay.WorldToLocal(new UnityEngine.Vector2(target.worldBound.xMin, target.worldBound.yMax));
                tip.style.left = at.x;
                tip.style.top = at.y;
                tip.PointAt(overlay.WorldToLocal(target.worldBound.center).x);
                tip.AddToClassList(UiClasses.TooltipVisible);
            }
            void Arm() { pending?.Pause(); pending = target.schedule.Execute(Show).StartingIn(delayMs); }
            void Hide() { pending?.Pause(); tip.RemoveFromClassList(UiClasses.TooltipVisible); }
            target.RegisterCallback<PointerEnterEvent>(_ => Arm());
            target.RegisterCallback<FocusInEvent>(_ => Arm());
            target.RegisterCallback<PointerLeaveEvent>(_ => Hide());
            target.RegisterCallback<FocusOutEvent>(_ => Hide());
            target.RegisterCallback<DetachFromPanelEvent>(_ => { Hide(); tip.RemoveFromHierarchy(); });
            return tip;
        }
    }
}
