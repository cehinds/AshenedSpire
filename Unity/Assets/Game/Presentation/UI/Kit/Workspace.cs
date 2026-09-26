using System;
using System.Collections.Generic;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// W1 workspace (docs/design/04 §1): CategoryNav beside (rail) or above (selector) the active pane. The mode is
    /// re-evaluated from the workspace's own size whenever it changes, with the CategoryNav model's hysteresis.
    /// Children added in UXML go into the pane.
    /// </summary>
    [UxmlElement]
    public partial class Workspace : VisualElement
    {
        private readonly VisualElement _pane;

        public Workspace()
        {
            AddToClassList(nameof(Workspace).ToLowerInvariant());
            UiDom.CloneTemplate(this, UiResources.KitWorkspace);
            Nav = this.Q<CategoryNav>(UiNames.WorkspaceNav);
            _pane = this.Q(UiNames.WorkspacePane);
            RegisterCallback<GeometryChangedEvent>(_ => Evaluate());
        }

        public override VisualElement contentContainer => _pane ?? this;

        public CategoryNav Nav { get; }

        /// <summary>Layout rules (ui/layout.json categoryNav); set by the owning screen.</summary>
        public CategoryNavRules Rules { get; set; }

        public event Action<string> Picked
        {
            add { if (Nav != null) Nav.Picked += value; }
            remove { if (Nav != null) Nav.Picked -= value; }
        }

        public void SetCategories(IReadOnlyList<CategoryItem> items, string activeId) => Nav?.SetCategories(items, activeId);

        public void Evaluate()
        {
            if (Nav == null || Rules == null || float.IsNaN(layout.width) || layout.width <= 0f) return;
            var mode = Nav.Evaluate(layout.width, layout.height, Rules);
            EnableInClassList(UiClasses.NavRailMode, mode == CategoryNavMode.Rail);
            EnableInClassList(UiClasses.NavSelectorMode, mode == CategoryNavMode.Selector);
        }
    }
}
