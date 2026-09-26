using System.Collections.Generic;

namespace Ashen.App.Ui
{
    /// <summary>A focusable item: its region's position in screens.json#focusOrder and its position inside the region.</summary>
    public struct FocusSlot
    {
        public int Region;
        public int Item;

        public FocusSlot(int region, int item)
        {
            Region = region;
            Item = item;
        }
    }

    /// <summary>
    /// One focus model for keyboard, pad and pointer (US-17.1; docs/design/04 §0 Focus). A screen's focus order
    /// is its screens.json regions in order, each region's focusable items in document order; disabled or hidden
    /// items are skipped. Moving past either end wraps.
    /// </summary>
    public static class FocusOrder
    {
        /// <summary>The linear order of the usable items. usable[region][item] says whether that item can take focus.</summary>
        public static List<FocusSlot> Resolve(IReadOnlyList<IReadOnlyList<bool>> usable)
        {
            var order = new List<FocusSlot>();
            for (var r = 0; r < usable.Count; r++)
                for (var i = 0; i < usable[r].Count; i++)
                    if (usable[r][i]) order.Add(new FocusSlot(r, i));
            return order;
        }

        /// <summary>Index of the item that takes focus first: the preferred slot when it is usable, else the first usable item; -1 when nothing is.</summary>
        public static int Initial(IReadOnlyList<FocusSlot> order, FocusSlot? preferred = null)
        {
            if (order.Count == 0) return -1;
            if (preferred.HasValue)
                for (var i = 0; i < order.Count; i++)
                    if (order[i].Region == preferred.Value.Region && order[i].Item == preferred.Value.Item) return i;
            return 0;
        }

        /// <summary>Step from current by delta (+1 next, -1 previous), wrapping. current -1 (nothing focused) steps onto the first or last item.</summary>
        public static int Step(int count, int current, int delta)
        {
            if (count == 0) return -1;
            if (current < 0) return delta >= 0 ? 0 : count - 1;
            var next = (current + delta) % count;
            return next < 0 ? next + count : next;
        }
    }
}
