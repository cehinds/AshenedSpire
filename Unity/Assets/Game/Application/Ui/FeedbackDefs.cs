using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>One animation speed of the web's pace table (src/ui/animationPace.js ANIM_SPEEDS): the beat, step and lunge it plays at.</summary>
    public sealed class SpeedDef
    {
        public double BeatMs;
        public double StepMs;
        public double LungeMs;
        public double ImpactCapMs;
        public double ReactionMs;
        public double StanceLeadMs;
    }

    /// <summary>ui/feedback.json pace: the speeds and the fixed waits (animationPace.js COMBAT_TIMING, actionPacing.json motion).</summary>
    public sealed class PaceDefs
    {
        public Dictionary<string, SpeedDef> Speeds = new Dictionary<string, SpeedDef>(StringComparer.Ordinal);
        public string Reference;
        public double WindupFraction;
        public double TurnBannerFloorMs;
        public double InstantStepMs;
        public double FightEndDelayMs;
        public double SpritePlaybackRate;
        public double ReferenceAnimationStepMs;
        public double ActionMs;
        public double TickMs;
    }

    public sealed class ShakeStep
    {
        public double MinShare;
        public double MinHp;
        public double Px;
    }

    public sealed class HitStopDef
    {
        public double MinHp;
        public double Ms;
        public double HugeShare;
        public double HugeMs;
    }

    /// <summary>ui/feedback.json feel: how hard a hit feels (src/content/combatFeel.js) and how long the one-shot effects live (src/ui/fx.js).</summary>
    public sealed class FeelDefs
    {
        public double BigHitShare;
        public double HugeHitShare;
        public double CritAmount;
        public double HeavyAmount;
        public double SmallBelow;
        public double ShakeMs;
        public double ShakeFloorPx;
        public double ShakeMaxPx;
        public double StaggerShakePx;
        public List<ShakeStep> ShakeSteps = new List<ShakeStep>();
        public List<ShakeStep> ShakeStepsHp = new List<ShakeStep>();
        public HitStopDef HitStop = new HitStopDef();
        public double HitFlashMs;
        public double HitFlashHeavyMs;
        public double WobbleMs;
        public double BurstMs;
        public double SparkMs;
        public double SlashMs;
        public double GlyphMs;
        public double StreakMs;
        public double CardFlyMs;
        public double CardFlyKeep;
        public double PoseHoldMs;
        public double PoseHurtMs;
    }

    /// <summary>One keyframe of a track: the share of the duration it sits at and the properties it names (a property it leaves out is interpolated across it).</summary>
    public sealed class FxStop
    {
        public double T;
        public double? Opacity;
        public double? X;
        public double? Y;
        public double? Scale;
        public double? ScaleX;
        public double? Rotate;
        public double? Track;
        public bool PopScale;

        /// <summary>The brightness() of the figure (1 is none) and the share of its drop-shadow glow (0 none, 1 the track's whole glow) at this stop: the filter of the web's action families.</summary>
        public double? Brightness;
        public double? Glow;
    }

    /// <summary>One of the web's @keyframes (ui/feedback.json tracks): duration, easing name and stops.</summary>
    public sealed class TrackDef
    {
        public double Ms;
        public string Ease;
        public bool AmpScaled;
        public double RotateJitter;
        public double TintUntil;

        /// <summary>The blur radius (px) and colour (#rrggbb) of the drop-shadow the stops' Glow scales; null colour for a track with no glow.</summary>
        public double GlowBlur;
        public string GlowColour;
        public List<FxStop> Stops = new List<FxStop>();
    }

    public sealed class Offset
    {
        public double X;
        public double Y;
    }

    /// <summary>ui/feedback.json float: where a floating number appears and the pops of its sizes (src/ui/fx.js floatNum, styles/combat.css).</summary>
    public sealed class FloatDefs
    {
        public double LifeMs;
        public double JitterPx;
        public double TopShare;
        public double SpawnShare;
        public double ClampPad;
        public double PopDefault;
        public double PopPct20;
        public double PopPct35;
        public Offset Barrier = new Offset();
        public Offset WardAlone = new Offset();
        public Offset Ward = new Offset();
        public Offset Poise = new Offset();
        public Offset Immune = new Offset();
        public Offset GuardPaired = new Offset();
        public Offset DamagePaired = new Offset();
    }

    public sealed class CalloutKindDef
    {
        public string Tone;
        public string Word;
    }

    /// <summary>ui/feedback.json callout: the words over a fighter and how they queue (src/ui/fx.js CALLOUT_KINDS, planWordQueue).</summary>
    public sealed class CalloutDefs
    {
        public double LifeMs;
        public double InstantMs;
        public int Lanes;
        public double StepMs;
        public double SlotPx;
        public double RisePx;
        public double ClampPad;
        public double TopInset;
        public double FloorGap;
        public double DefaultHeight;
        public double Reach;
        public Dictionary<string, CalloutKindDef> Kinds = new Dictionary<string, CalloutKindDef>(StringComparer.Ordinal);
        public Dictionary<string, double> Priority = new Dictionary<string, double>(StringComparer.Ordinal);
    }

    public sealed class TreatmentDef
    {
        public double SizeScale;
        public double Opacity;
        public double StartScale;
        public double EndScale;
    }

    public sealed class PlaybackDef
    {
        public double EffectMs;
        public int Frames;
        public double MinFrameMs;
        public double EventSize;
        public double DefaultSize;
        public double EntryOpacityFraction;
        public double TravelMidOffset;
    }

    public sealed class TreatmentSet
    {
        public TreatmentDef Aura = new TreatmentDef();
        public TreatmentDef Subtle = new TreatmentDef();
        public TreatmentDef Full = new TreatmentDef();
    }

    /// <summary>ui/feedback.json effects: which event plays which effect frames and how they play (src/ui/combatEffectSprites.js, src/content/combatDefenseEffects.js).</summary>
    public sealed class EffectDefs
    {
        public Dictionary<string, string> Stance = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> Status = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> Proc = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<string> AuraKinds = new List<string>();
        public List<string> SubtleKinds = new List<string>();
        public List<string> LayeredKinds = new List<string>();
        public double LayeredOpacity;
        public TreatmentSet Treatments = new TreatmentSet();
        public PlaybackDef Playback = new PlaybackDef();

        /// <summary>The treatment of an effect kind (combatEffectPresentation.js): an aura fades and rests, a subtle effect is smaller, the rest play full; a layered card effect is a little transparent.</summary>
        public TreatmentDef TreatmentOf(string kind, out double opacity)
        {
            if (AuraKinds.Contains(kind)) { opacity = Treatments.Aura.Opacity; return Treatments.Aura; }
            if (SubtleKinds.Contains(kind)) { opacity = Treatments.Subtle.Opacity; return Treatments.Subtle; }
            opacity = LayeredKinds.Contains(kind) ? LayeredOpacity : Treatments.Full.Opacity;
            return Treatments.Full;
        }
    }

    /// <summary>ui/feedback.json poses: the enemy's rest pose rules and the per-enemy figure scale of its state frames (src/ui/enemyStates.js, enemyPoseStage.js).</summary>
    public sealed class PoseDefs
    {
        public double WoundedShare;
        public double ProjectileScale;
        public List<string> Negative = new List<string>();
        public Dictionary<string, double> StateScale = new Dictionary<string, double>(StringComparer.Ordinal);
        public Dictionary<string, double> DefeatedScale = new Dictionary<string, double>(StringComparer.Ordinal);
    }

    public sealed class IntroDefs
    {
        public double BossHoldMs;
        public double RemoveMs;
        public double VictoryBeatMs;
    }

    /// <summary>
    /// ui/feedback.json (VP-5, D-750): every number the combat feedback plays with, as the web has it (see the file's webSource). Engine-free and shared with the tests;
    /// the screen only interprets what <see cref="Core2Feedback"/> plans from these. The members bind to the JSON names (case-insensitive), so no key appears in code.
    /// </summary>
    public sealed class FeedbackDefs
    {
        public PaceDefs Pace = new PaceDefs();
        public FeelDefs Feel = new FeelDefs();
        public Dictionary<string, double[]> Easings = new Dictionary<string, double[]>(StringComparer.Ordinal);
        public Dictionary<string, TrackDef> Tracks = new Dictionary<string, TrackDef>(StringComparer.Ordinal);
        public FloatDefs Float = new FloatDefs();
        public CalloutDefs Callout = new CalloutDefs();
        public Dictionary<string, string> StatusColours = new Dictionary<string, string>(StringComparer.Ordinal);
        public EffectDefs Effects = new EffectDefs();
        public PoseDefs Poses = new PoseDefs();
        public IntroDefs Intro = new IntroDefs();

        public static FeedbackDefs From(JObject json) => json.ToObject<FeedbackDefs>();

        public TrackDef Track(FxTrack id) => Tracks[FxTrackNames.ToWire(id)];

        public CalloutKindDef Kind(FxCallout kind) => Callout.Kinds[FxCalloutNames.ToWire(kind)];

        /// <summary>The cubic-bezier of an easing name (a track's Ease), linear when the name is unknown.</summary>
        public double[] Ease(string name) =>
            name != null && Easings.TryGetValue(name, out var curve) ? curve : Easings.Values.FirstOrDefault() ?? Array.Empty<double>();
    }
}
