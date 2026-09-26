using System.Collections.Generic;
using System.Linq;
using Ashen.App.Ui;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI
{
    /// <summary>
    /// One focus model for keyboard, pad and pointer (US-17.1). The screen's screens.json focusOrder names regions;
    /// each region contributes its focusable descendants in document order. The ordering and wrapping rules are the
    /// engine-free Ashen.App.Ui.FocusOrder; this class maps them onto UI Toolkit elements.
    /// </summary>
    public sealed class FocusModel
    {
        private readonly ScreenInstance _screen;

        public FocusModel(ScreenInstance screen) => _screen = screen;

        /// <summary>Region candidates in focusOrder, and whether each can take focus now.</summary>
        private List<List<VisualElement>> Candidates()
        {
            var regions = new List<List<VisualElement>>();
            foreach (var name in _screen.Def.FocusOrder)
            {
                var region = _screen.Root.Q(name);
                var items = new List<VisualElement>();
                if (region != null)
                {
                    if (region.focusable) items.Add(region);
                    else items.AddRange(region.Query<VisualElement>().Where(e => e != region && e.focusable).ToList());
                }
                regions.Add(items);
            }
            return regions;
        }

        private bool Usable(VisualElement e) => e.focusable && e.enabledInHierarchy && e.panel != null && UiDom.IsShown(e, _screen.Root.parent);

        /// <summary>The usable items in focus order.</summary>
        public List<VisualElement> Items()
        {
            var regions = Candidates();
            var usable = regions.Select(r => (IReadOnlyList<bool>)r.Select(Usable).ToList()).ToList();
            return FocusOrder.Resolve(usable).Select(s => regions[s.Region][s.Item]).ToList();
        }

        public VisualElement Focused
        {
            get
            {
                var f = _screen.Root.panel?.focusController?.focusedElement as VisualElement;
                return f != null && _screen.Root.Contains(f) ? f : null;
            }
        }

        /// <summary>Focus the screen's first item: the remembered item, else initialFocus, else the first usable item.</summary>
        public VisualElement FocusInitial(VisualElement remembered = null)
        {
            var items = Items();
            if (items.Count == 0) return null;
            if (remembered != null && items.Contains(remembered))
            {
                remembered.Focus();
                return remembered;
            }
            var regions = Candidates();
            FocusSlot? preferred = null;
            if (_screen.Def.InitialFocus != null)
            {
                var target = _screen.Root.Q(_screen.Def.InitialFocus);
                for (var r = 0; r < regions.Count && preferred == null; r++)
                {
                    var i = regions[r].IndexOf(target);
                    if (i >= 0) preferred = new FocusSlot(r, i);
                }
            }
            var usable = regions.Select(r => (IReadOnlyList<bool>)r.Select(Usable).ToList()).ToList();
            var order = FocusOrder.Resolve(usable);
            var index = FocusOrder.Initial(order, preferred);
            if (index < 0) return null;
            var element = regions[order[index].Region][order[index].Item];
            element.Focus();
            return element;
        }

        /// <summary>Move focus by delta (+1 next, -1 previous) with wrapping.</summary>
        public VisualElement Move(int delta)
        {
            var items = Items();
            if (items.Count == 0) return null;
            var current = CurrentIndex(items);
            var next = FocusOrder.Step(items.Count, current, delta);
            items[next].Focus();
            return items[next];
        }

        private int CurrentIndex(List<VisualElement> items)
        {
            var f = Focused;
            for (var v = f; v != null; v = v.parent)
            {
                var i = items.IndexOf(v);
                if (i >= 0) return i;
            }
            return -1;
        }
    }
}
