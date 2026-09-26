using System;
using System.Collections.Generic;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>A category for CategoryNav: id, label key, and an optional count or value text.</summary>
    public sealed class CategoryItem
    {
        public string Id;
        public string LabelKey;
        public string Count;

        /// <summary>False for a category that is not built yet (it shows, but cannot be picked; D-058).</summary>
        public bool Enabled = true;
    }

    /// <summary>
    /// Rule 11 CategoryNav (docs/design/04 §0, §1): the same element holds a rail and a [Category ▾] selector and
    /// toggles between them by class (nav--rail / nav--selector), never by a second UXML. The mode comes from the
    /// engine-free CategoryNavModel with hysteresis. Escape closes an open selector list first (Navigator.Back).
    /// </summary>
    [UxmlElement]
    public partial class CategoryNav : VisualElement
    {
        private readonly VisualElement _rail;
        private readonly LocButton _selector;
        private readonly VisualElement _list;
        private readonly List<CategoryItem> _items = new List<CategoryItem>();
        private CategoryNavMode? _mode;

        public CategoryNav()
        {
            AddToClassList(nameof(CategoryNav).ToLowerInvariant());
            UiDom.CloneTemplate(this, UiResources.KitCategoryNav);
            _rail = this.Q(UiNames.NavRail);
            _selector = this.Q<LocButton>(UiNames.NavSelector);
            _list = this.Q(UiNames.NavSelectorList);
            if (_selector != null) _selector.clicked += () => ToggleInClassList(UiClasses.NavOpen);
            SetMode(CategoryNavMode.Rail);
        }

        public string ActiveId { get; private set; }
        public CategoryNavMode Mode => _mode ?? CategoryNavMode.Rail;

        public event Action<string> Picked;

        public void SetCategories(IReadOnlyList<CategoryItem> items, string activeId)
        {
            _items.Clear();
            _items.AddRange(items);
            ActiveId = activeId;
            Rebuild();
        }

        /// <summary>Re-evaluate rail vs selector for the space the host has (reference px).</summary>
        public CategoryNavMode Evaluate(double width, double height, CategoryNavRules rules)
        {
            var mode = CategoryNavModel.Choose(width, height, _items.Count, rules, _mode);
            SetMode(mode);
            return mode;
        }

        public void SetMode(CategoryNavMode mode)
        {
            _mode = mode;
            EnableInClassList(UiClasses.NavRailMode, mode == CategoryNavMode.Rail);
            EnableInClassList(UiClasses.NavSelectorMode, mode == CategoryNavMode.Selector);
            if (mode == CategoryNavMode.Rail) RemoveFromClassList(UiClasses.NavOpen);
        }

        private void Rebuild()
        {
            _rail?.Clear();
            _list?.Clear();
            foreach (var item in _items)
            {
                _rail?.Add(Button(item));
                _list?.Add(Button(item));
                if (item.Id == ActiveId) _selector?.SetResolved(Loc.Format(StringKeys.NavSelector, new StringArgs().Add(UiPlaceholders.Name, Label(item))));
            }
        }

        private static string Label(CategoryItem item) =>
            string.IsNullOrEmpty(item.Count)
                ? Loc.Get(item.LabelKey)
                : Loc.Format(StringKeys.NavItemCount, new StringArgs().Add(UiPlaceholders.Name, Loc.Get(item.LabelKey)).Add(UiPlaceholders.Count, item.Count));

        private LocButton Button(CategoryItem item)
        {
            var b = new LocButton();
            b.SetResolved(Label(item));
            b.AddToClassList(UiClasses.NavItem);
            b.EnableInClassList(UiClasses.Selected, item.Id == ActiveId);
            b.SetEnabled(item.Enabled);
            b.clicked += () =>
            {
                ActiveId = item.Id;
                RemoveFromClassList(UiClasses.NavOpen);
                Rebuild();
                Picked?.Invoke(item.Id);
            };
            return b;
        }
    }
}
