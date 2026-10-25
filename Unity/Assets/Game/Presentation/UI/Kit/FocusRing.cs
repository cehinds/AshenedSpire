using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// The web's focus ring (base.css:376-389, kit.css:2533): a 2 px gold outline drawn 2 px OUTSIDE the focused control (outline +
    /// outline-offset), for a keyboard or pad cursor only (:focus-visible). USS has no outline, and a border can only grow inward, so one
    /// overlay element follows whichever control holds the focus; its reach beyond the control is the token size.focusRingReach (the offset
    /// plus the stroke). A pointer press moves the focus without showing the ring, as in a browser, until a key or the pad is used.
    /// </summary>
    public sealed class FocusRing : VisualElement
    {
        private readonly VisualElement _root;
        private VisualElement _target;
        private bool _byPointer;

        public FocusRing(VisualElement root)
        {
            _root = root;
            AddToClassList(ChromeClasses.FocusRing);
            pickingMode = PickingMode.Ignore;
            var box = new VisualElement { pickingMode = PickingMode.Ignore };
            box.AddToClassList(ChromeClasses.FocusRingBox);
            Add(box);
            UiDom.Show(this, false);
            root.Add(this);
            root.RegisterCallback<FocusInEvent>(OnFocusIn, TrickleDown.TrickleDown);
            root.RegisterCallback<FocusOutEvent>(OnFocusOut, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerDownEvent>(_ => _byPointer = true, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(_ => _byPointer = false, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationMoveEvent>(_ => _byPointer = false, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationSubmitEvent>(_ => _byPointer = false, TrickleDown.TrickleDown);
            schedule.Execute(Follow).Every(0);
        }

        /// <summary>A key or the pad is driving the focus again (the InputRouter's Move and Submit; UI Toolkit's own navigation events are swallowed by the Navigator): the next focus shows the ring.</summary>
        public void UseKeys() => _byPointer = false;

        /// <summary>The control the ring is on (null when it is hidden).</summary>
        public VisualElement Target => _target;

        private void OnFocusIn(FocusInEvent e)
        {
            var next = e.target as VisualElement;
            if (_byPointer || next == null || next == _root || next is ScrollView) { _target = null; Follow(); return; }
            _target = next;
            BringToFront();
            Follow();
        }

        private void OnFocusOut(FocusOutEvent e)
        {
            if (e.target != _target) return;
            _target = null;
            Follow();
        }

        /// <summary>Keeps the ring on its control (a control scrolls, animates and is re-laid out), or hides it when the control is gone.</summary>
        private void Follow()
        {
            var target = _target;
            if (target == null || target.panel == null || target.resolvedStyle.display == DisplayStyle.None || !target.visible || float.IsNaN(target.worldBound.width))
            {
                if (!ClassListContains(UiClasses.Hidden)) UiDom.Show(this, false);
                return;
            }
            var min = _root.WorldToLocal(target.worldBound.min);
            var max = _root.WorldToLocal(target.worldBound.max);
            style.left = min.x;
            style.top = min.y;
            style.width = max.x - min.x;
            style.height = max.y - min.y;
            if (ClassListContains(UiClasses.Hidden)) UiDom.Show(this, true);
        }
    }
}
