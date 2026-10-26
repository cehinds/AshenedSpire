using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;

namespace Ashen.App.Ui
{
    /// <summary>The properties of a track at one moment; a property the track never names stays null so the screen leaves it alone.</summary>
    public struct FxPose
    {
        public double? Opacity;
        public double? X;
        public double? Y;
        public double? Scale;
        public double? ScaleX;
        public double? Rotate;
        public double? Track;

        /// <summary>The brightness() the figure takes (null when the track never names one) and the share of the track's drop-shadow glow in force.</summary>
        public double? Brightness;
        public double? Glow;

        /// <summary>The red hit tint is on (the web's stepped filter, which holds from impact to <see cref="TrackDef.TintUntil"/>).</summary>
        public bool Tinted;
    }

    /// <summary>
    /// The web's CSS animation maths without a browser (ui/feedback.json tracks): cubic-bezier easing, and keyframe sampling the way CSS does it: each property is
    /// interpolated between the nearest stops that name it, with the track's easing applied to every such interval. Engine-free, so the tests pin it.
    /// </summary>
    public static class FxCurve
    {
        /// <summary>The y of a CSS cubic-bezier(x1, y1, x2, y2) at time share x (0..1), found by bisection on the curve's x.</summary>
        public static double Bezier(IReadOnlyList<double> c, double x)
        {
            if (c == null || c.Count < FxIndex.BezierPoints) return x;
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            double lo = 0, hi = 1, t = x;
            while (hi - lo > FxMath.Epsilon)
            {
                t = (lo + hi) * FxMath.Half;
                if (Axis(c[0], c[FxIndex.IndexX2], t) < x) lo = t; else hi = t;
            }
            return Axis(c[1], c[FxIndex.IndexY2], t);
        }

        private static double Axis(double p1, double p2, double t)
        {
            var u = 1 - t;
            return FxMath.Three * u * u * t * p1 + FxMath.Three * u * t * t * p2 + t * t * t;
        }

        /// <summary>Opacity, offsets, scales, rotation and letter spacing of a track at <paramref name="elapsedMs"/>.</summary>
        /// <param name="pop">The scale the popScale stop takes (the web's --pop).</param>
        /// <param name="amp">What an ampScaled track multiplies its offsets by (the shake's px).</param>
        /// <param name="rotateOffset">Added to every rotation (a slash's random --rot).</param>
        public static FxPose Sample(TrackDef track, IReadOnlyDictionary<string, double[]> easings, double elapsedMs, double pop = 1, double amp = 1, double rotateOffset = 0)
        {
            var share = track.Ms > 0 ? Math.Min(1, Math.Max(0, elapsedMs / track.Ms)) : 1;
            var curve = easings.TryGetValue(track.Ease ?? string.Empty, out var named) ? named : null;
            var scaleBy = track.AmpScaled ? amp : 1;
            return new FxPose
            {
                Opacity = Along(track, share, curve, s => s.Opacity, pop, 1),
                X = Along(track, share, curve, s => s.X, pop, scaleBy),
                Y = Along(track, share, curve, s => s.Y, pop, scaleBy),
                Scale = Along(track, share, curve, s => s.PopScale ? pop : s.Scale, pop, 1),
                ScaleX = Along(track, share, curve, s => s.ScaleX, pop, 1),
                Rotate = Along(track, share, curve, s => s.Rotate, pop, 1) is double r ? r + rotateOffset : (double?)null,
                Track = Along(track, share, curve, s => s.Track, pop, 1),
                Brightness = Along(track, share, curve, s => s.Brightness, pop, 1),
                Glow = Along(track, share, curve, s => s.Glow, pop, 1),
                Tinted = track.TintUntil > 0 && share < track.TintUntil,
            };
        }

        private static double? Along(TrackDef track, double share, IReadOnlyList<double> curve, Func<FxStop, double?> pick, double pop, double factor)
        {
            FxStop before = null, after = null;
            double? vb = null, va = null;
            foreach (var stop in track.Stops)
            {
                var v = pick(stop);
                if (v == null) continue;
                if (stop.T <= share) { before = stop; vb = v; }
                else { after = stop; va = v; break; }
            }
            if (before == null) return va * factor;
            if (after == null) return vb * factor;
            var span = after.T - before.T;
            var local = span > 0 ? (share - before.T) / span : 1;
            var eased = Bezier(curve, local);
            return (vb.Value + (va.Value - vb.Value) * eased) * factor;
        }
    }
}
