using System;
using Ashen.App.Ui;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>The wall clock the feedback times itself by (ms), independent of the scheduler's own tick.</summary>
    public static class FxClock
    {
        public static double Now() => (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
    }

    /// <summary>One running track: stop it to leave the element where it is (the caller resets it).</summary>
    public sealed class FxRun
    {
        internal IVisualElementScheduledItem Item;

        public bool Done { get; internal set; }

        public void Stop()
        {
            Done = true;
            Item?.Pause();
        }
    }

    /// <summary>
    /// Plays the web's keyframes (ui/feedback.json tracks, sampled by <see cref="FxCurve"/>) on an element: every scheduler tick it samples the track and writes the opacity,
    /// translate, scale, rotation and letter spacing the track names as inline styles. Time is the wall clock, so a slow frame skips ahead as a CSS animation does.
    /// Simple two-state changes (the card's flight, the boss splash) are USS transitions instead; tracks are for the multi-stop motions a transition cannot play.
    /// </summary>
    public sealed class FxMotion
    {
        private readonly FeedbackDefs _defs;

        public FxMotion(FeedbackDefs defs, Color hitTint)
        {
            _defs = defs;
            HitTint = hitTint;
        }

        public FeedbackDefs Defs => _defs;

        /// <summary>The red the figure takes from impact to 45% of its recoil (the web's stepped sepia/hue-rotate filter, approximated as a multiply tint).</summary>
        public Color HitTint { get; }

        /// <summary>
        /// Plays <paramref name="track"/> over <paramref name="ms"/> on <paramref name="host"/>'s scheduler, calling <paramref name="apply"/> with each sample (the first one at once).
        /// <paramref name="holdMs"/> keeps the track frozen at its start first (the web's hit-stop). <paramref name="done"/> runs after the last sample.
        /// </summary>
        public FxRun Play(VisualElement host, TrackDef track, double ms, Action<FxPose> apply, Action done = null, double pop = 1, double amp = 1, double rotate = 0, double holdMs = 0)
        {
            var run = new FxRun();
            var start = FxClock.Now() + holdMs;
            FxPose Sample(double elapsed) => FxCurve.Sample(track, _defs.Easings, ms > 0 ? elapsed / ms * track.Ms : track.Ms, pop, amp, rotate);
            apply(Sample(0));
            run.Item = host.schedule.Execute(() =>
            {
                if (run.Done) return;
                var elapsed = FxClock.Now() - start;
                apply(Sample(Math.Max(0, elapsed)));
                if (elapsed < ms) return;
                run.Stop();
                done?.Invoke();
            }).Every((long)_defs.Pace.TickMs);
            return run;
        }

        /// <summary>Writes the properties a sample names onto an element's inline style.</summary>
        public static void Apply(VisualElement el, FxPose p)
        {
            if (p.Opacity.HasValue) el.style.opacity = (float)p.Opacity.Value;
            if (p.X.HasValue || p.Y.HasValue) el.style.translate = new Translate((float)(p.X ?? 0), (float)(p.Y ?? 0));
            if (p.Scale.HasValue || p.ScaleX.HasValue)
            {
                var y = (float)(p.Scale ?? 1);
                var x = (float)(p.ScaleX ?? p.Scale ?? 1);
                el.style.scale = new Scale(new Vector2(x, y));
            }
            if (p.Rotate.HasValue) el.style.rotate = new Rotate(new Angle((float)p.Rotate.Value, AngleUnit.Degree));
            if (p.Track.HasValue) el.style.letterSpacing = (float)(p.Track.Value * el.resolvedStyle.fontSize);
        }

        /// <summary>Gives back every style <see cref="Apply"/> may have written (the USS rules show again).</summary>
        public static void Reset(VisualElement el)
        {
            el.style.opacity = StyleKeyword.Null;
            el.style.translate = StyleKeyword.Null;
            el.style.scale = StyleKeyword.Null;
            el.style.rotate = StyleKeyword.Null;
            el.style.letterSpacing = StyleKeyword.Null;
        }
    }
}
