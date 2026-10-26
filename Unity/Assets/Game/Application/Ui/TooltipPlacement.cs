using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using TV = Ashen.Generated.TooltipPlacementValues;

namespace Ashen.App.Ui
{
    /// <summary>A rectangle in the overlay's reference pixels (the tooltip's anchor, its panel, the room it must stay in).</summary>
    public struct PlacementBox
    {
        public double Left;
        public double Top;
        public double Width;
        public double Height;

        public PlacementBox(double left, double top, double width, double height)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }

        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }

    /// <summary>The numbers of the web's tooltip placement (content/config/ui/presentation/tooltipPlacement.json, kit.css:685 --place-gap, fx.js placeAnchored pad): ui/components.json tooltip.placement.</summary>
    public sealed class TooltipPlacementRules
    {
        /// <summary>Space between the panel and its trigger.</summary>
        public double Gap;

        /// <summary>The margin the panel keeps from the room's edges.</summary>
        public double Pad;

        /// <summary>Distance under which two edges count as touching (the arrow's choice of edge).</summary>
        public double EdgeEpsilon;

        /// <summary>The wait before a focused control's tooltip opens (tooltipHelp.focusMs; not one of the player's settings).</summary>
        public int FocusMs;

        public static TooltipPlacementRules From(JObject json) => new TooltipPlacementRules
        {
            Gap = Js.Or0(json?[UiKeys.Gap]),
            Pad = Js.Or0(json?[UiKeys.Pad]),
            EdgeEpsilon = Js.Or0(json?[UiKeys.EdgeEpsilon]),
            FocusMs = (int)Js.Or0(json?[UiKeys.FocusMs]),
        };
    }

    /// <summary>Where a tooltip's arrow sits: the panel edge that faces the trigger and the arrow's centre along it.</summary>
    public struct TooltipArrowPlacement
    {
        public string Edge;

        /// <summary>The arrow's centre along the edge, in the panel's own coordinates (x for top and bottom, y for left and right); NaN when there is no arrow.</summary>
        public double Along;
    }

    /// <summary>
    /// The web's tooltip placement, engine-free: the intent a trigger asks for (TooltipPlacementModel tooltipPlacementIntent), the candidates tried in the web's order
    /// (fx.js placeAnchored: 'above' flips under the trigger before it goes sideways, 'under' stays, 'left' and 'right' try the other side) and the clamp that keeps the panel
    /// inside the room, and the arrow on the edge that faces the trigger (tooltipArrow). Tested against the web's own cases.
    /// </summary>
    public static class TooltipPlacement
    {
        /// <summary>
        /// placeAnchored: the panel's top-left. Candidates in the web's order for the intent; the first that fits the room (inside the pad on every side) wins, else the first candidate;
        /// then the box is clamped into the room, so a wrong answer is a misplaced panel and never an absent one.
        /// </summary>
        public static PlacementBox Place(PlacementBox anchor, double width, double height, double roomWidth, double roomHeight, string intent, string align, TooltipPlacementRules rules)
        {
            var pad = rules.Pad;
            var gap = rules.Gap;
            var anchoredX = align == TV.AlignEnd ? anchor.Right - width : align == TV.AlignCenter ? anchor.Left + (anchor.Width - width) * UiMath.Half : anchor.Left;
            var slideX = Math.Min(Math.Max(pad, anchoredX), Math.Max(pad, roomWidth - pad - pad - width));
            var slideY = Math.Min(Math.Max(pad, anchor.Top), Math.Max(pad, roomHeight - pad - pad - height));
            var under = new PlacementBox(slideX, anchor.Bottom + gap, width, height);
            var above = new PlacementBox(slideX, anchor.Top - height - gap, width, height);
            var aboveLeft = new PlacementBox(anchor.Left - width - gap, above.Top, width, height);
            var aboveRight = new PlacementBox(anchor.Right + gap, above.Top, width, height);
            var right = new PlacementBox(anchor.Right + gap, slideY, width, height);
            var left = new PlacementBox(anchor.Left - width - gap, slideY, width, height);
            var candidates = intent == TV.Under ? new List<PlacementBox> { under }
                : intent == TV.Above ? new List<PlacementBox> { above, aboveLeft, aboveRight, under, right, left }
                : intent == TV.Left ? new List<PlacementBox> { left, right, under, above }
                : new List<PlacementBox> { right, left, under, above };
            var chosen = candidates.FirstOrDefault(c => Fits(c, roomWidth, roomHeight, pad));
            if (chosen.Width <= 0 || chosen.Height <= 0) chosen = candidates[0];
            return new PlacementBox(Clamp(chosen.Left, chosen.Width, roomWidth, pad), Clamp(chosen.Top, chosen.Height, roomHeight, pad), chosen.Width, chosen.Height);
        }

        private static bool Fits(PlacementBox p, double roomWidth, double roomHeight, double pad) =>
            p.Left >= pad && p.Top >= pad && p.Right <= roomWidth - pad - pad && p.Bottom <= roomHeight - pad - pad;

        /// <summary>clampBox: the box stays within the pad of the room, the low edge winning when the room is too small.</summary>
        private static double Clamp(double value, double size, double span, double pad)
        {
            var lo = pad;
            var hi = span - pad - size;
            return Math.Min(Math.Max(value, lo), Math.Max(lo, hi));
        }

        /// <summary>
        /// tooltipArrow: the arrow goes on the panel edge that faces the trigger and points at the trigger's centre, slid along the edge but kept the inset clear of its corners
        /// (so after a shift it still points at the trigger); no arrow when the panel overlaps the trigger or the edge is too short.
        /// </summary>
        public static TooltipArrowPlacement ArrowFor(PlacementBox panel, PlacementBox anchor, double arrowWidth, double inset, TooltipPlacementRules rules)
        {
            var eps = rules.EdgeEpsilon;
            var edge = panel.Bottom <= anchor.Top + eps ? TV.EdgeBottom
                : panel.Top >= anchor.Bottom - eps ? TV.EdgeTop
                : panel.Right <= anchor.Left + eps ? TV.EdgeRight
                : panel.Left >= anchor.Right - eps ? TV.EdgeLeft : TV.EdgeNone;
            if (edge == TV.EdgeNone) return new TooltipArrowPlacement { Edge = TV.EdgeNone, Along = double.NaN };
            var along = edge == TV.EdgeBottom || edge == TV.EdgeTop;
            var lo = (along ? panel.Left : panel.Top) + inset + arrowWidth * UiMath.Half;
            var hi = (along ? panel.Right : panel.Bottom) - inset - arrowWidth * UiMath.Half;
            if (hi < lo) return new TooltipArrowPlacement { Edge = TV.EdgeNone, Along = double.NaN };
            var centre = along ? anchor.Left + anchor.Width * UiMath.Half : anchor.Top + anchor.Height * UiMath.Half;
            centre = Math.Min(Math.Max(centre, lo), hi);
            return new TooltipArrowPlacement { Edge = edge, Along = centre - (along ? panel.Left : panel.Top) };
        }
    }

    /// <summary>
    /// The player's hover-explanation settings (the web's resolveTooltipSettings): whether a pointer resting on a detail opens its tooltip, the wait before it opens and the wait
    /// after the pointer leaves before it closes. Set by the settings applier; until then the tooltip kit keeps the delay its caller passed.
    /// </summary>
    public static class TooltipPolicy
    {
        public static bool HoverEnabled = true;
        public static bool Applied;
        public static int OpenMs;
        public static int CloseMs;

        /// <summary>"0.25s" as milliseconds; a value that is not a number of seconds is the fallback.</summary>
        public static int Millis(string choice, int fallback)
        {
            if (string.IsNullOrEmpty(choice) || !choice.EndsWith(TooltipPlacementValues.SecondsSuffix, StringComparison.Ordinal)) return fallback;
            return double.TryParse(choice.Substring(0, choice.Length - TooltipPlacementValues.SecondsSuffix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? (int)Math.Round(seconds * UiMath.MillisPerSecond) : fallback;
        }
    }
}
