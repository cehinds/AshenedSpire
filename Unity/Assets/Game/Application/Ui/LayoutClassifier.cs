using System;

namespace Ashen.App.Ui
{
    /// <summary>Height bands (docs/design/04 §0): standard, compact ("short-wide rails"), or below the upright gate.</summary>
    public enum HeightBand
    {
        Standard,
        Compact,
        Gate,
    }

    /// <summary>The layout a viewport resolves to: band, narrow/wide mode, the PanelSettings it uses and that panel's scale.</summary>
    public sealed class LayoutState
    {
        public int ViewportWidth;
        public int ViewportHeight;
        public HeightBand Band;
        public bool Narrow;
        public PanelDef Panel;

        /// <summary>Physical pixels per reference pixel (Scale With Screen Size).</summary>
        public double Scale;

        /// <summary>The viewport in reference pixels.</summary>
        public double ReferenceWidth => ViewportWidth / Scale;
        public double ReferenceHeight => ViewportHeight / Scale;

        /// <summary>A physical minimum (layout.json#minPhysical, physical px) expressed in reference px, so it holds after scaling.</summary>
        public double ReferenceMinimum(int physicalPixels) => physicalPixels / Scale;

        public bool SameAs(LayoutState other) =>
            other != null && other.ViewportWidth == ViewportWidth && other.ViewportHeight == ViewportHeight;
    }

    /// <summary>Pure layout classification (US-17.1 responsive rules; ui/layout.json). No engine types.</summary>
    public static class LayoutClassifier
    {
        public static LayoutState Classify(int width, int height, LayoutRules rules)
        {
            var narrow = width <= rules.NarrowMaxWidth;
            var panel = narrow ? rules.Narrow : rules.Wide;
            HeightBand band;
            if (height >= rules.StandardMinHeight) band = HeightBand.Standard;
            else if (height >= rules.CompactMinHeight && height >= rules.GateBelowHeight) band = HeightBand.Compact;
            else band = HeightBand.Gate;
            return new LayoutState
            {
                ViewportWidth = width,
                ViewportHeight = height,
                Narrow = narrow,
                Band = narrow ? HeightBand.Standard : band,
                Panel = panel,
                Scale = Scale(width, height, panel),
            };
        }

        /// <summary>
        /// Scale With Screen Size: expand = the smaller ratio, shrink = the larger, matchWidthOrHeight = the
        /// logarithmic blend Unity uses (match 0 = width, 1 = height).
        /// </summary>
        public static double Scale(int width, int height, PanelDef panel)
        {
            var sw = (double)width / panel.Width;
            var sh = (double)height / panel.Height;
            if (panel.ScreenMatch == Ashen.Generated.UiValues.MatchShrink) return Math.Max(sw, sh);
            if (panel.ScreenMatch == Ashen.Generated.UiValues.MatchWidthOrHeight)
                return Math.Exp(Math.Log(sw) + (Math.Log(sh) - Math.Log(sw)) * panel.Match);
            return Math.Min(sw, sh);
        }
    }
}
