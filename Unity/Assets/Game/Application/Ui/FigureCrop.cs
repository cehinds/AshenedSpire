using System;
using System.Collections.Generic;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>The visible (opaque) box of a figure's art, as fractions of its image (ui/components.json dialogue.figures, made by Tools/figure-boxes.mjs): [top, height, centerX, width].</summary>
    public struct FigureBox
    {
        public double Top;
        public double Height;
        public double CenterX;
        public double Width;
    }

    /// <summary>Where a figure goes: the image drawn at <see cref="Scale"/> with its top-left corner at (<see cref="X"/>, <see cref="Y"/>) in the frame, and the width its visible part takes.</summary>
    public struct FigurePlacement
    {
        public double Scale;
        public double X;
        public double Y;
        public double VisibleWidth;
    }

    /// <summary>One speaker's lane: the share of the frame its figure may spread over (the web's dialogueLanes), and which side it is on.</summary>
    public struct FigureLane
    {
        public double Left;
        public double Width;
        public bool Right;
    }

    /// <summary>The numbers of the spoken step's close-up (the web's content/config/ui/scenes/w4c-dialogue.json positioning and sizing): ui/components.json dialogue.</summary>
    public sealed class DialogueRules
    {
        public double InsetVw;
        public double SlotWidthVw;
        public double SlotWidthCompactVw;
        public double SlotTopOffsetVh;

        /// <summary>How much of the figure's visible height the band shows: the top third on a wide host, the whole figure on a compact one.</summary>
        public double VisibleFraction = 1;
        public double VisibleFractionCompact = 1;
        public double MinGapVw;
        public double MinGapPx;

        /// <summary>How far, in frame widths (vw), a figure too wide for its lane may lean out of the frame before it shrinks.</summary>
        public double MaxOuterOverflowVw;

        /// <summary>The least the shown part of a figure may be, in frame heights (vh).</summary>
        public double MinVisibleHeightVh;

        public IReadOnlyDictionary<string, FigureBox> Figures = new Dictionary<string, FigureBox>();

        public static DialogueRules From(JObject json)
        {
            var slot = json?[UiKeys.Slot] as JObject;
            var portraits = json?[UiKeys.Portraits] as JObject;
            var figures = new Dictionary<string, FigureBox>(StringComparer.Ordinal);
            if (json?[UiKeys.Figures] is JObject listed)
                foreach (var p in listed.Properties())
                    if (p.Value is JObject box)
                        figures[p.Name] = new FigureBox { Top = Js.Or0(box[UiKeys.Top]), Height = Js.Or0(box[UiKeys.Height]), CenterX = Js.Or0(box[UiKeys.CenterX]), Width = Js.Or0(box[UiKeys.Width]) };
            return new DialogueRules
            {
                InsetVw = Js.Or0(slot?[UiKeys.InsetVw]),
                SlotWidthVw = Js.Or0(slot?[UiKeys.WidthVw]),
                SlotWidthCompactVw = Js.Or0(slot?[UiKeys.WidthCompactVw]),
                SlotTopOffsetVh = Js.Or0(slot?[UiKeys.TopOffsetVh]),
                VisibleFraction = (double?)portraits?[UiKeys.VisibleFraction] ?? 1d,
                VisibleFractionCompact = (double?)portraits?[UiKeys.VisibleFractionCompact] ?? 1d,
                MinGapVw = Js.Or0(portraits?[UiKeys.MinGapVw]),
                MinGapPx = Js.Or0(portraits?[UiKeys.MinGapPx]),
                MaxOuterOverflowVw = Js.Or0(portraits?[UiKeys.MaxOuterOverflowVw]),
                MinVisibleHeightVh = Js.Or0(portraits?[UiKeys.MinVisibleHeightVh]),
                Figures = figures,
            };
        }

        /// <summary>The visible box of a figure by its art id, or the whole image when no box is known.</summary>
        public FigureBox BoxOf(string artId) => artId != null && Figures.TryGetValue(artId, out var box) ? box : new FigureBox { Top = 0, Height = 1, CenterX = UiMath.Half, Width = 1 };
    }

    /// <summary>
    /// The spoken step's figure close-up, engine-free (the web's PortraitCropModel.closeUpPlacement with the shrinkToLane fit anchored on the reveal line, and DialogueModel.dialogueLanes). A figure is the whole image
    /// scaled so the shown fraction of its VISIBLE height spans from the slot's top to the reveal line (the context band's top edge); one too wide for its lane leans outward first, then shrinks, never below the
    /// least the config allows; its visible top ends up on the reveal line minus the shown part, and it is centred on its slot, kept inside its lane.
    /// </summary>
    public static class FigureCrop
    {
        public static FigureLane LaneFor(bool right, double frameWidth, DialogueRules rules)
        {
            var inset = frameWidth * (rules.InsetVw / UiMath.Percent);
            var gap = Math.Max(frameWidth * (rules.MinGapVw / UiMath.Percent), rules.MinGapPx);
            var width = (frameWidth - inset - inset - gap) * UiMath.Half;
            return new FigureLane { Left = right ? frameWidth - inset - width : inset, Width = width, Right = right };
        }

        /// <summary>
        /// The placement of a figure whose image is <paramref name="imageWidth"/> by <paramref name="imageHeight"/> reference px: <paramref name="slotCenter"/> and <paramref name="slotTop"/> are its slot's centre and top
        /// in the frame, <paramref name="revealLine"/> the context band's top.
        /// </summary>
        public static FigurePlacement Place(FigureBox box, double imageWidth, double imageHeight, double slotCenter, double slotTop, double revealLine, FigureLane lane, bool compact, double frameWidth, double frameHeight, DialogueRules rules)
        {
            var top = box.Top * imageHeight;
            var height = Math.Max(1, box.Height * imageHeight);
            var centerX = box.CenterX * imageWidth;
            var artWidth = Math.Max(1, box.Width * imageWidth);
            var fraction = compact ? rules.VisibleFractionCompact : rules.VisibleFraction;
            var span = Math.Max(1, revealLine - slotTop);
            var scale = span / (height * fraction);
            var allowance = Math.Max(0, frameWidth * (rules.MaxOuterOverflowVw / UiMath.Percent));
            var room = lane.Width + allowance;
            if (artWidth * scale > room) scale = room / artWidth;
            var drawn = artWidth * scale;
            var overhang = Math.Max(0, drawn - lane.Width);
            var floor = frameHeight * (rules.MinVisibleHeightVh / UiMath.Percent);
            if (height * fraction * scale < floor) scale = floor / (height * fraction);
            var visibleTop = revealLine - height * fraction * scale;
            var figureWidth = artWidth * scale;
            var half = figureWidth * UiMath.Half;
            var outerLeft = lane.Right ? lane.Left : lane.Left - overhang;
            var outerWidth = lane.Width + overhang;
            var low = outerLeft + half;
            var high = outerLeft + outerWidth - half;
            // Wider than its lane and its lean together: the inner edge is pinned to the lane's inner edge, so every extra pixel goes over the frame's outer edge.
            var center = low > high ? (lane.Right ? lane.Left + half : lane.Left + lane.Width - half) : Math.Min(Math.Max(slotCenter, low), high);
            return new FigurePlacement { Scale = scale, X = center - centerX * scale, Y = visibleTop - top * scale, VisibleWidth = figureWidth };
        }
    }
}
