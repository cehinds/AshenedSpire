namespace Ashen.App.Ui
{
    public enum CategoryNavMode
    {
        Rail,
        Selector,
    }

    /// <summary>
    /// Rule 11 (docs/design/04 §0): a screen with several categories shows a rail beside its pane, or a
    /// [Category ▾] selector when the rail would not fit. The choice depends on the space the screen has
    /// (reference px), the category count, the tap floor and hysteresis, never on a single breakpoint.
    /// </summary>
    public static class CategoryNavModel
    {
        /// <summary>
        /// A rail fits when the pane keeps at least minPaneWidth beside it and every category gets the tap
        /// floor in height. Hysteresis widens the test for the mode already shown, so a resize across the
        /// edge does not flap between rail and selector.
        /// </summary>
        public static CategoryNavMode Choose(double width, double height, int count, CategoryNavRules rules, CategoryNavMode? previous = null)
        {
            var slack = previous == CategoryNavMode.Rail ? rules.Hysteresis : previous == CategoryNavMode.Selector ? -rules.Hysteresis : 0d;
            return RailFits(width, height, count, rules, slack) ? CategoryNavMode.Rail : CategoryNavMode.Selector;
        }

        public static bool RailFits(double width, double height, int count, CategoryNavRules rules, double slack = 0d)
        {
            var paneLeft = width - rules.RailWidth + slack;
            var listHeight = count * rules.TapFloor + rules.Chrome;
            return paneLeft >= rules.MinPaneWidth && listHeight <= height + slack;
        }
    }
}
