using System.Linq;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The Menu overlay's and Settings' share of the input layer (P-7c, D-1750..): the tab ring (web setTabRing: LB and RB, [ and ], wrap at both ends, over the set in its own order whether the strip shows or is folded) and the
    /// quick menu setting (web quicknav.js quickNavMode, overlay.js syncQuickLauncher): mirror carries the Go to... box beside the tabs, switcher folds the tab strip into one "Settings ▾" button on a narrow layout (and has no box),
    /// off has neither and the menu button goes straight to Settings. The ring is the overlay's own tabs; on the standalone Settings page it is the page's tabs (General, Accessibility, Advanced), claimed only if free, so
    /// in the overlay the bumpers stay with the outer strip (web hasTabRing, Law 3 clause 6).
    /// </summary>
    public sealed partial class SettingsScreen
    {
        private LocButton _switcher;

        /// <summary>The bumpers and [ ] step this screen's tabs for as long as it is open.</summary>
        public override bool HasTabRing => true;

        public override void StepTabs(int delta)
        {
            if (_overlay)
            {
                var ids = Ui.Data.Menus.OverlayTabs.Select(t => t.Id).ToList();
                var next = ids[TabRing.Step(ids.Count, ids.IndexOf(_tab), delta)];
                SelectTab(next, null);
                return;
            }
            var tabs = _page.Tabs.Select(t => t.Id).ToList();
            var current = _page.Tabs.ToList().FindIndex(t => t.Active);
            ChoosePageTab(tabs[TabRing.Step(tabs.Count, current, delta)]);
        }

        /// <summary>A page tab (General, Accessibility, Advanced): a new screenful, so the group, the topic, the search and the Changed filter start over.</summary>
        private void ChoosePageTab(string id)
        {
            _query.Tab = id;
            _query.Group = null;
            _query.Topic = null;
            _query.Search = null;
            _query.ChangedOnly = false;
            Root.Q<TextField>(UiNames.SettingsSearch).SetValueWithoutNotify(string.Empty);
            Render();
        }

        // ------------------------------------------------------------------ the quick menu setting

        private void BindQuick()
        {
            Nav.LayoutChanged += SyncQuick;
            SettingsSession.Changed += OnSettingChanged;
            SyncQuick();
        }

        private void UnbindQuick()
        {
            Nav.LayoutChanged -= SyncQuick;
            SettingsSession.Changed -= OnSettingChanged;
        }

        private void OnSettingChanged(SettingsSession settings, string key)
        {
            if (key == null || key == SettingIds.QuickNav) SyncQuick();
        }

        /// <summary>The Go to... box and the folded strip as the setting and the layout say they are now (web overlay.js syncQuickLauncher).</summary>
        private void SyncQuick()
        {
            if (!_overlay) return;
            var mode = QuickNav.Resolve(_settings.Text(SettingIds.QuickNav));
            var folded = QuickNav.Folds(mode, Nav.Layout != null && Nav.Layout.Narrow);
            UiDom.Show(Root.Q(UiNames.OverlayGoTo), QuickNav.Launcher(mode));
            UiDom.Show(Root.Q(UiNames.OverlayTabs), !folded);
            if (folded && _switcher == null)
            {
                _switcher = new LocButton();
                _switcher.AddToClassList(MenuClasses.OverlayTab);
                _switcher.AddToClassList(MenuClasses.OverlayTabOn);
                _switcher.AddToClassList(HotkeyClasses.OverlaySwitch);
                _switcher.clicked += ToggleGoTo;
                Root.Q(UiNames.OverlayHead).Insert(1, _switcher);
                SyncSwitcherLabel();
            }
            else if (!folded && _switcher != null)
            {
                _switcher.RemoveFromHierarchy();
                _switcher = null;
            }
            if (!QuickNav.Launcher(mode) && !folded) CloseGoTo();
        }

        /// <summary>The switcher says the open tab and that it opens a list ("Settings ▾"; web overlay.js: the tab's label and a down triangle).</summary>
        private void SyncSwitcherLabel()
        {
            if (_switcher == null || _tab == null) return;
            var def = Ui.Data.Menus.OverlayTabs.FirstOrDefault(t => t.Id == _tab);
            if (def == null) return;
            var strings = Ui.Data.Strings;
            _switcher.SetResolved(strings.Format(StringKeys.MenuSwitcher, new StringArgs().Add(UiPlaceholders.Name, strings.Get(def.LabelKey))));
        }
    }
}
