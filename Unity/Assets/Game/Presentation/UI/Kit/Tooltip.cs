using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;
using TV = Ashen.Generated.TooltipPlacementValues;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// Tooltip (docs/design/04 §0, §1): sizes WT0–WT3 (ui/components.json tooltip.sizes → tooltip--size), glyph,
    /// title and templated body. Attach() shows one in the overlay layer after the tooltip delay when the
    /// target is hovered or focused, placed as the web places it (above the trigger, flipping under it and then to its sides, kept inside the screen;
    /// <see cref="TooltipPlacement"/>) with its arrow on the edge that faces the trigger. The player's hover explanations, delay and close delay
    /// (<see cref="TooltipPolicy"/>) rule it.
    /// </summary>
    [UxmlElement]
    public partial class Tooltip : VisualElement
    {
        /// <summary>The web's placement numbers (ui/components.json tooltip.placement); the UI host sets them.</summary>
        public static TooltipPlacementRules Rules = new TooltipPlacementRules();

        private readonly LocLabel _glyph;
        private readonly LocLabel _title;
        private readonly LocLabel _body;
        private readonly VisualElement _arrow;
        private string _size;
        private float _inset = float.NaN;
        private string _edge;

        public Tooltip()
        {
            AddToClassList(nameof(Tooltip).ToLowerInvariant());
            pickingMode = PickingMode.Ignore;
            UiDom.CloneTemplate(this, UiResources.KitTooltip);
            _glyph = this.Q<LocLabel>(UiNames.TooltipGlyph);
            _title = this.Q<LocLabel>(UiNames.TooltipTitle);
            _body = this.Q<LocLabel>(UiNames.TooltipBody);
            _arrow = this.Q(UiNames.TooltipArrow);
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

        /// <summary>
        /// Places the tooltip beside its trigger as the web does (tooltip.js showWith: placeAnchored then pointArrow). The panel and the trigger are measured in the overlay's own
        /// coordinates; the arrow goes on the panel edge that faces the trigger and points at the trigger's centre, the inset clear of the corners.
        /// </summary>
        public void PlaceBeside(VisualElement target, VisualElement overlay, string align)
        {
            var room = overlay.layout;
            var at = overlay.WorldToLocal(target.worldBound.position);
            var anchor = new PlacementBox(at.x, at.y, target.worldBound.width, target.worldBound.height);
            var box = TooltipPlacement.Place(anchor, resolvedStyle.width, resolvedStyle.height, room.width, room.height, TV.Above, align, Rules);
            style.left = (float)box.Left;
            style.top = (float)box.Top;
            PointArrow(box, anchor);
        }

        private void PointArrow(PlacementBox panel, PlacementBox anchor)
        {
            if (_arrow == null) return;
            var arrowW = _arrow.resolvedStyle.width;
            var arrowH = _arrow.resolvedStyle.height;
            if (float.IsNaN(arrowW) || float.IsNaN(arrowH)) return;
            if (float.IsNaN(_inset)) _inset = _arrow.resolvedStyle.left;
            if (_edge != null) _arrow.RemoveFromClassList(UiClasses.TooltipArrowPrefix + _edge);
            var arrow = TooltipPlacement.ArrowFor(panel, anchor, arrowW, _inset, Rules);
            _edge = arrow.Edge;
            _arrow.EnableInClassList(UiClasses.Hidden, arrow.Edge == TV.EdgeNone);
            if (arrow.Edge == TV.EdgeNone) return;
            _arrow.AddToClassList(UiClasses.TooltipArrowPrefix + arrow.Edge);
            var border = resolvedStyle.borderTopWidth;
            var along = (float)arrow.Along;
            // The arrow's own box is arrowW x arrowH before it is turned to point at the trigger; it lies outside the panel, so its offsets run from the padding box (inside the border).
            switch (arrow.Edge)
            {
                case TV.EdgeTop:
                    _arrow.style.left = along - arrowW * (float)UiMath.Half - border;
                    _arrow.style.top = -arrowH - border;
                    break;
                case TV.EdgeBottom:
                    _arrow.style.left = along - arrowW * (float)UiMath.Half - border;
                    _arrow.style.top = (float)panel.Height - border;
                    break;
                case TV.EdgeLeft:
                    _arrow.style.left = -arrowH * (float)UiMath.Half - arrowW * (float)UiMath.Half - border;
                    _arrow.style.top = along - arrowH * (float)UiMath.Half - border;
                    break;
                default:
                    _arrow.style.left = (float)panel.Width + arrowH * (float)UiMath.Half - arrowW * (float)UiMath.Half - border;
                    _arrow.style.top = along - arrowH * (float)UiMath.Half - border;
                    break;
            }
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

        /// <summary>
        /// Hover/focus tooltip for one run of a rich text label: it opens when the pointer is over the <c>&lt;link&gt;</c> run whose id is <paramref name="linkId"/> and closes when it leaves it (the web's span carrying
        /// data-damage-receipt). Where the platform raises no link tag events, the whole label is the target, as <see cref="AttachResolved"/> makes it.
        /// </summary>
        public static Tooltip AttachLinked(VisualElement label, VisualElement overlay, string sizeId, string title, string body, int delayMs, string linkId)
        {
            var tip = new Tooltip { size = sizeId };
            tip.SetText(title, body);
            return Hook(tip, label, overlay, delayMs, linkId);
        }

        /// <summary>A tap is the question on a phone, where the hover clock never runs (web iconTray.js explain): the tooltip opens at once above the target. <see cref="Dismiss"/> takes it away.</summary>
        public static Tooltip ShowNow(VisualElement target, VisualElement overlay, string sizeId, string title, string body)
        {
            var tip = new Tooltip { size = sizeId };
            tip.SetText(title, body);
            tip.style.visibility = Visibility.Hidden;
            overlay.Add(tip);
            EventCallback<GeometryChangedEvent> placed = null;
            placed = _ =>
            {
                tip.UnregisterCallback(placed);
                if (tip.parent == null) return;
                tip.PlaceBeside(target, overlay, TV.AlignCenter);
                tip.style.visibility = StyleKeyword.Null;
                tip.AddToClassList(UiClasses.TooltipVisible);
            };
            tip.RegisterCallback(placed);
            return tip;
        }

        /// <summary>Takes a tooltip made by <see cref="ShowNow"/> away.</summary>
        public void Dismiss()
        {
            RemoveFromClassList(UiClasses.TooltipVisible);
            RemoveFromHierarchy();
        }

        private static Tooltip Hook(Tooltip tip, VisualElement target, VisualElement overlay, int delayMs, string linkId = null)
        {
            IVisualElementScheduledItem pending = null;
            IVisualElementScheduledItem closing = null;
            var open = false;
            void Place()
            {
                tip.PlaceBeside(target, overlay, TV.AlignCenter);
                tip.style.visibility = StyleKeyword.Null;
                tip.AddToClassList(UiClasses.TooltipVisible);
            }
            void Show()
            {
                open = true;
                if (tip.parent != overlay) overlay.Add(tip);
                tip.style.visibility = Visibility.Hidden;
                if (tip.resolvedStyle.width > 0 && !float.IsNaN(tip.resolvedStyle.width)) Place();
                else
                {
                    EventCallback<GeometryChangedEvent> placed = null;
                    placed = _ => { tip.UnregisterCallback(placed); if (open) Place(); };
                    tip.RegisterCallback(placed);
                }
            }
            void Arm(int wait) { closing?.Pause(); if (open) return; pending?.Pause(); pending = target.schedule.Execute(Show).StartingIn(wait); }
            void Hide() { pending?.Pause(); closing?.Pause(); open = false; tip.RemoveFromClassList(UiClasses.TooltipVisible); }
            void Leave()
            {
                pending?.Pause();
                if (!open) return;
                closing?.Pause();
                closing = target.schedule.Execute(Hide).StartingIn(TooltipPolicy.Applied ? TooltipPolicy.CloseMs : 0);
            }
            void Hover() { if (TooltipPolicy.HoverEnabled) Arm(TooltipPolicy.Applied ? TooltipPolicy.OpenMs : delayMs); }
            var onLink = linkId != null && LinkTagEvents.Register(target, id => { if (id == linkId) Hover(); }, id => { if (id == linkId) Leave(); });
            if (!onLink) target.RegisterCallback<PointerEnterEvent>(_ => Hover());
            target.RegisterCallback<FocusInEvent>(_ => Arm(Rules.FocusMs));
            target.RegisterCallback<PointerLeaveEvent>(_ => Leave());
            target.RegisterCallback<FocusOutEvent>(_ => Leave());
            target.RegisterCallback<DetachFromPanelEvent>(_ => { Hide(); tip.RemoveFromHierarchy(); });
            return tip;
        }
    }
}
