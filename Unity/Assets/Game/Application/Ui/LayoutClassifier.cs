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
        /// <summary>The player's View mode (the web's viewPreset): auto follows the window; mobile is the narrow layout; desktop, widescreen and tablet the wide one.</summary>
        public static string Preset = Ashen.Generated.SettingsValues.ViewAuto;

        /// <summary>The player's UI size (the web's uiScale): Auto flexes with the window, S to XL asks for a fixed size and gets as much of it as fits (the wide layout only).</summary>
        public static string Size = Ashen.Generated.SettingsValues.SizeAuto;

        public static LayoutState Classify(int width, int height, LayoutRules rules)
        {
            var narrow = Preset == Ashen.Generated.SettingsValues.ViewMobile
                || (Preset == Ashen.Generated.SettingsValues.ViewAuto && width <= rules.NarrowMaxWidth);
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
                Scale = narrow ? Scale(width, height, panel) : SizedScale(width, height, panel, rules.UiScale),
            };
        }

        /// <summary>
        /// The wide layout's scale under the player's UI size: Auto is Scale With Screen Size; S to XL is the web's fixed zoom (balance.ui.uiScale.named over the design canvas)
        /// turned into reference pixels, never more than what fits the window.
        /// </summary>
        public static double SizedScale(int width, int height, PanelDef panel, UiScaleRules rules)
        {
            var auto = Scale(width, height, panel);
            if (Size == Ashen.Generated.SettingsValues.SizeAuto || !rules.Named.TryGetValue(Size.ToLowerInvariant(), out var named)) return auto;
            var referenceZoom = Math.Min(panel.Width / rules.DesignW, panel.Height / rules.DesignH);
            return Math.Min(named / referenceZoom, auto);
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
