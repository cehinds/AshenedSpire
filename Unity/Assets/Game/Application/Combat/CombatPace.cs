using System;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Generated;

namespace Ashen.App.Combat
{
    /// <summary>
    /// The pace a combat presentation plays at, from the player's pacing settings (D11, D12) as the web resolves them (src/ui/animationPace.js actionPacing): the animation
    /// speed picks the table row (the setting "auto" plays at the reference speed, "instant" and reduced motion play no waits), the combat speed divides everything, the
    /// pause between steps is the gap between the stages of one action and the animation step sets how long a swing takes. Engine-free and presentation-only: it never
    /// reaches the rules.
    /// </summary>
    public sealed class CombatPace
    {
        /// <summary>No waits: the web's classic fast path (animation speed "instant", or reduced motion) staggers one beat after another.</summary>
        public bool Instant;

        /// <summary>The web's pace scale (0 when instant): the speed row's beat over the reference beat, over the combat speed.</summary>
        public double Scale;

        /// <summary>The wait between two stages of one action (word, swing, result, reaction, damage, reset), after the scale.</summary>
        public double GapMs;

        /// <summary>The wait after a beat that has no action (a draw, a reveal), and the floor of a beat's reset.</summary>
        public double StepMs;

        /// <summary>How long a turn banner shows (the speed row's beat, never under the floor, scaled).</summary>
        public double BannerMs;

        /// <summary>How long an attacker's swing takes, and the share of it before the blow lands.</summary>
        public double LungeMs;

        public double ImpactMs;

        /// <summary>How long a callout lives (the web's CALLOUT_MS scaled, or the instant length).</summary>
        public double CalloutMs;

        /// <summary>What the damage floats do: the reduced-motion rise (a plain fade) instead of the bounce.</summary>
        public bool Reduced;

        /// <summary>The pause between the last blow and the spoils (a victory), as the web holds it.</summary>
        public double FightEndMs;

        public static CombatPace From(CombatPacing pacing, FeedbackDefs defs, bool reducedMotion)
        {
            var pace = defs.Pace;
            var animInstant = pacing.AnimSpeed == Core2UiValues.AnimInstant;
            var instant = animInstant || reducedMotion;
            var row = pacing.AnimSpeed != null && pace.Speeds.TryGetValue(pacing.AnimSpeed, out var named) ? named : pace.Speeds[pace.Reference];
            var reference = pace.Speeds[pace.Reference];
            var speed = pacing.EffectiveSpeed > 0 ? pacing.EffectiveSpeed : 1;
            var raw = animInstant ? 0 : row.BeatMs / reference.BeatMs / speed;
            var scale = instant ? 0 : raw;
            var spriteScale = scale / pace.SpritePlaybackRate * (pacing.AnimationStepMs / pace.ReferenceAnimationStepMs);
            var lunge = row.LungeMs * spriteScale;
            return new CombatPace
            {
                Instant = instant,
                Reduced = reducedMotion,
                Scale = scale,
                GapMs = pacing.StageGapMs * scale,
                StepMs = row.StepMs * scale,
                BannerMs = Math.Max(pace.TurnBannerFloorMs, row.BeatMs) * scale,
                LungeMs = lunge,
                ImpactMs = lunge * pace.WindupFraction,
                CalloutMs = animInstant ? defs.Callout.InstantMs : defs.Callout.LifeMs * (raw > 0 ? raw : 1),
                FightEndMs = pace.FightEndDelayMs,
            };
        }
    }
}
