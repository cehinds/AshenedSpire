using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>One frame of the card stage: its art, the rectangle it is drawn in on the stage canvas and the landmarks of its body (the source raster's head and feet and its height).</summary>
    public sealed class StageFrame
    {
        public string Art;
        public double X;
        public double Y;
        public double W;
        public double H;
        public bool HasMarks;
        public double HeadY;
        public double FeetY;
        public double MarkHeight;
    }

    /// <summary>A rectangle in stage units, measured from the stage's anchor (x) and floor line (y): Left is right of the figure's centre, Top is below the floor line (negative above it).</summary>
    public struct StageRect
    {
        public double Left;
        public double Top;
        public double Width;
        public double Height;
    }

    /// <summary>The poses of one card action with how long each is held, how far the body travels and which pose is the blow (model/alternativeCardAnimation.js sequences).</summary>
    public sealed class StageSequence
    {
        public string Name;
        public IReadOnlyList<string> Poses;
        public IReadOnlyList<double> Durations;
        public IReadOnlyList<double> Travel;
        public IReadOnlyList<string> Effects;
        public int Impact;
        public bool Hold;
        public bool Pilot;
        public double TimingUnits;
        public bool ContactCue;
        public bool Reaction;

        /// <summary>The sum of the pose durations (the clip's own length before the pace scales it).</summary>
        public double Base => Durations.Sum();

        /// <summary>The share of the clip before the blow lands.</summary>
        public double ContactShare => Durations.Take(Impact).Sum() / Base;
    }

    /// <summary>The hurt clip's registration over the ready figure (content/playerHurtFrames.js): its art, where it lands and how large it is drawn.</summary>
    public sealed class StageHurt
    {
        public string Art;
        public double Dx;
        public double Dy;
        public double Scale;
    }

    /// <summary>How long a clip plays at a pace: the pace's sprite speed (ANIM_SPEEDS scaled as animationPace.js actionPacing scales them for the sprite).</summary>
    public struct SpriteSpeed
    {
        public double LungeMs;
        public double ImpactCapMs;
        public double ReactionMs;
    }

    /// <summary>How long an action plays and when its blow lands.</summary>
    public struct StageTiming
    {
        public double TotalMs;
        public double ImpactMs;
    }

    /// <summary>The class's card stage: every frame, effect trail and action it has, as the combat assembles them (model/cardAnimationFamily.js).</summary>
    public sealed class StageClass
    {
        public string Id;
        public double Unit;

        /// <summary>The stage's own box: its height and its width in stage pixels (the web's root: 190 tall, as wide as the ready frame's envelope), which the board fits the figure by.</summary>
        public double StageHeight;
        public double IdleWidth;
        public double BodyHeight;
        public bool Pilot;
        public bool ClassSmash;
        public HashSet<string> Held = new HashSet<string>(StringComparer.Ordinal);
        public string CounterRest;
        public StageHurt Hurt;
        public string Defeated;
        public Dictionary<string, StageFrame> Frames = new Dictionary<string, StageFrame>(StringComparer.Ordinal);
        public Dictionary<string, string> Effects = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, StageSequence> Sequences = new Dictionary<string, StageSequence>(StringComparer.Ordinal);

        public StageFrame Frame(string name) => name != null && Frames.TryGetValue(name, out var frame) ? frame : null;

        public StageSequence Sequence(string name) => name != null && Sequences.TryGetValue(name, out var sequence) ? sequence : null;
    }

    /// <summary>Where a sequence is at a moment of its clip: the pose it shows, how far the body has travelled, and how far through the clip it is.</summary>
    public struct StageSample
    {
        public int Index;
        public double X;
        public double Progress;
        public bool Finished;
    }

    /// <summary>
    /// ui/cardStage.json (P-3b, D-1490..): the player's combat figure as the web draws it, a card stage (src/ui/alternativeCardStage.js). The stage is one canvas with the class's card-action
    /// frames, held stance frames, pilot or class-smash frames, counter and sweep frames and their trails, each drawn at its own rectangle, sampled along a sequence of poses; this holds the
    /// frames, the sequences and the arithmetic of the clip (sampleSequence, durationFor, cardSequenceTiming, the hurt clip, the recoil, bodyPlacement). Engine-free, tested without the editor.
    /// </summary>
    public sealed class CardStageData
    {
        public readonly double AnchorX;
        public readonly double FloorY;
        public readonly double FrameTop;
        public readonly double DefaultX;
        public readonly double DefaultSize;
        public readonly int HurtFrames;
        public readonly double HurtRedOpacity;
        public readonly double HurtMinMs;
        public readonly double RecoilPx;
        public readonly double RecoilPeak;
        public readonly double HitMs;
        public readonly double PlayMs;
        public readonly double ReactMs;
        public readonly double DurationMs;
        public readonly double PilotUnits;
        public readonly double StaggerTintShare;
        public readonly IReadOnlyDictionary<string, string> Aliases;
        public readonly IReadOnlyList<string> ReactionKinds;
        public readonly IReadOnlyDictionary<string, StageSequence> Reactions;
        private readonly Dictionary<string, StageClass> _classes = new Dictionary<string, StageClass>(StringComparer.Ordinal);

        public CardStageData(JObject json)
        {
            var stage = (JObject)json[StageKeys.Stage];
            AnchorX = stage.Value<double>(StageKeys.AnchorX);
            FloorY = stage.Value<double>(StageKeys.FloorY);
            FrameTop = stage.Value<double>(StageKeys.FrameTop);
            DefaultX = stage.Value<double>(StageKeys.DefaultX);
            DefaultSize = stage.Value<double>(StageKeys.DefaultSize);
            var motion = (JObject)json[StageKeys.Motion];
            HurtFrames = motion.Value<int>(StageKeys.HurtFrames);
            HurtRedOpacity = motion.Value<double>(StageKeys.HurtRedOpacity);
            HurtMinMs = motion.Value<double>(StageKeys.HurtMinMs);
            RecoilPx = motion.Value<double>(StageKeys.RecoilPx);
            RecoilPeak = motion.Value<double>(StageKeys.RecoilPeak);
            HitMs = motion.Value<double>(StageKeys.HitMs);
            PlayMs = motion.Value<double>(StageKeys.PlayMs);
            ReactMs = motion.Value<double>(StageKeys.ReactMs);
            DurationMs = motion.Value<double>(StageKeys.DurationMs);
            PilotUnits = motion.Value<double>(StageKeys.PilotUnits);
            StaggerTintShare = motion.Value<double>(StageKeys.StaggerTintShare);
            Aliases = ((JObject)json[StageKeys.Aliases]).Properties().ToDictionary(p => p.Name, p => p.Value.Value<string>(), StringComparer.Ordinal);
            ReactionKinds = ((JArray)json[StageKeys.ReactionKinds]).Select(t => t.Value<string>()).ToList();
            Reactions = ((JObject)json[StageKeys.Reactions]).Properties().ToDictionary(p => p.Name, p => Sequence(p.Name, (JObject)p.Value), StringComparer.Ordinal);
            foreach (var row in (JObject)json[StageKeys.Classes]) _classes[row.Key] = Class(row.Key, (JObject)row.Value);
        }

        private static StageSequence Sequence(string name, JObject row)
        {
            var effects = row[StageKeys.Effects] as JArray;
            return new StageSequence
            {
                Name = name,
                Poses = ((JArray)row[StageKeys.Poses]).Select(t => t.Value<string>()).ToList(),
                Durations = ((JArray)row[StageKeys.Durations]).Select(t => t.Value<double>()).ToList(),
                Travel = ((JArray)row[StageKeys.Travel]).Select(t => t.Value<double>()).ToList(),
                Effects = effects?.Select(t => t.Type == JTokenType.Null ? null : t.Value<string>()).ToList(),
                Impact = row.Value<int>(StageKeys.Impact),
                Hold = row.Value<bool?>(StageKeys.Hold) ?? false,
                Pilot = row.Value<bool?>(StageKeys.Pilot) ?? false,
                TimingUnits = row.Value<double?>(StageKeys.TimingUnits) ?? 0,
                ContactCue = row.Value<bool?>(StageKeys.ContactCue) ?? false,
                Reaction = row.Value<bool?>(StageKeys.Reaction) ?? false,
            };
        }

        private static StageClass Class(string id, JObject row)
        {
            var hurt = (JObject)row[StageKeys.Hurt];
            var result = new StageClass
            {
                Id = id,
                Unit = row.Value<double>(StageKeys.Unit),
                StageHeight = row.Value<double>(StageKeys.StageHeight),
                IdleWidth = row.Value<double>(StageKeys.IdleWidth),
                BodyHeight = row.Value<double>(StageKeys.BodyHeight),
                Pilot = row.Value<bool>(StageKeys.Pilot),
                ClassSmash = row.Value<bool>(StageKeys.ClassSmash),
                CounterRest = row.Value<string>(StageKeys.CounterRest),
                Defeated = row.Value<string>(StageKeys.Defeated),
                Hurt = new StageHurt { Art = hurt.Value<string>(StageKeys.Art), Dx = hurt.Value<double>(StageKeys.Dx), Dy = hurt.Value<double>(StageKeys.Dy), Scale = hurt.Value<double>(StageKeys.Scale) },
            };
            foreach (var held in (JArray)row[StageKeys.Held]) result.Held.Add(held.Value<string>());
            foreach (var frame in (JObject)row[StageKeys.Frames])
            {
                var f = (JObject)frame.Value;
                var marks = f[StageKeys.Marks] as JArray;
                result.Frames[frame.Key] = new StageFrame
                {
                    Art = f.Value<string>(StageKeys.Art), X = f.Value<double>(StageKeys.X), Y = f.Value<double>(StageKeys.Y), W = f.Value<double>(StageKeys.W), H = f.Value<double>(StageKeys.H),
                    HasMarks = marks != null, HeadY = marks?[0].Value<double>() ?? 0, FeetY = marks?[1].Value<double>() ?? 0, MarkHeight = marks?[P1Count.Pair].Value<double>() ?? 0,
                };
            }
            foreach (var effect in (JObject)row[StageKeys.Effects]) result.Effects[effect.Key] = effect.Value.Value<string>();
            foreach (var sequence in (JObject)row[StageKeys.Sequences]) result.Sequences[sequence.Key] = Sequence(sequence.Key, (JObject)sequence.Value);
            return result;
        }

        /// <summary>A class that has a card stage (every class the combat paints).</summary>
        public bool Has(string classId) => classId != null && _classes.ContainsKey(classId);

        public StageClass Class(string classId) => classId != null && _classes.TryGetValue(classId, out var c) ? c : null;

        public IEnumerable<string> ClassIds => _classes.Keys;

        /// <summary>The action's own name: a card action alias (cast, power and buff are the spell; guard is defend) answers to the sequence it plays (CARD_ANIMATION_ALIASES).</summary>
        public string Alias(string action) => action != null && Aliases.TryGetValue(action, out var key) ? key : action;

        // ------------------------------------------------------------------ placement (alternativeCardStage.js paint, ui/models/CombatBodyModel.js bodyPlacement)

        /// <summary>
        /// The rectangle a frame is drawn in, as paint() places it. The stage's own units, from the anchor (x) and the floor line (y). <paramref name="bodyX"/> is the travel and the knock-back
        /// of the body; <paramref name="held"/> seats the body on the held registration: the frame is scaled about its anchor until its head-to-sole distance is the ready figure's and its soles
        /// stand on the floor line (bodyPlacement), as the combat does for a frame at rest or a frame the clip shares with the held pose.
        /// </summary>
        public StageRect Place(StageClass c, StageFrame frame, double bodyX, bool held)
        {
            var left = frame.X + bodyX;
            var top = frame.Y;
            var width = frame.W;
            var height = frame.H;
            if (held && frame.HasMarks && frame.FeetY > frame.HeadY && frame.MarkHeight > 0)
            {
                var sourceScale = height / frame.MarkHeight;
                var head = top + frame.HeadY * sourceScale;
                var feet = top + frame.FeetY * sourceScale;
                var factor = c.BodyHeight / (feet - head);
                var anchorX = AnchorX + bodyX;
                left = anchorX + (left - anchorX) * factor;
                top = FloorY + (top - feet) * factor;
                width *= factor;
                height *= factor;
            }
            return new StageRect { Left = left - AnchorX, Top = top - FloorY, Width = width, Height = height };
        }

        /// <summary>The hurt clip over the ready figure: drawn at the default offset, scaled and shifted by its registration (the clip has no body landmarks).</summary>
        public StageRect PlaceHurt(StageClass c, double bodyX) =>
            new StageRect { Left = DefaultX + bodyX + c.Hurt.Dx - AnchorX, Top = FrameTop + c.Hurt.Dy - FloorY, Width = DefaultSize * c.Hurt.Scale, Height = DefaultSize * c.Hurt.Scale };

        /// <summary>The downed figure and an effect trail: the default square at the default offset.</summary>
        public StageRect PlaceSquare(double x) => new StageRect { Left = DefaultX + x - AnchorX, Top = FrameTop - FloorY, Width = DefaultSize, Height = DefaultSize };

        // ------------------------------------------------------------------ the clip

        /// <summary>sampleSequence: the pose, the body's travel and the progress at <paramref name="elapsed"/> of a clip that plays for <paramref name="duration"/>.</summary>
        public StageSample Sample(StageSequence s, double elapsed, double duration, bool reduced)
        {
            var total = s.Base;
            if (reduced || duration <= 0) return new StageSample { Index = s.Hold ? s.Poses.Count - 1 : 0, X = 0, Progress = 1, Finished = true };
            var progress = Math.Max(0, Math.Min(1, elapsed / duration));
            var index = 0;
            var boundary = s.Durations[0];
            while (index < s.Poses.Count - 1 && progress * total >= boundary) boundary += s.Durations[++index];
            var phase = (progress * total - boundary + s.Durations[index]) / s.Durations[index];
            var next = index + 1 < s.Travel.Count ? s.Travel[index + 1] : 0;
            var x = s.Travel[index] + (next - s.Travel[index]) * Math.Max(0, Math.Min(1, phase));
            return new StageSample { Index = index, X = progress == 1 ? 0 : x, Progress = progress, Finished = progress == 1 };
        }

        /// <summary>durationFor: how long a card action's clip plays at a pace: its own length scaled to the pace's lunge, never so long that the blow lands after the pace's impact cap.</summary>
        public double DurationFor(StageSequence s, SpriteSpeed speed)
        {
            if (speed.LungeMs <= 0) return 0;
            var contact = s.Durations.Take(s.Impact).Sum();
            var scale = Math.Min(speed.LungeMs / DurationMs, contact > 0 ? speed.ImpactCapMs / contact : double.PositiveInfinity);
            return s.Base * scale;
        }

        /// <summary>cardSequenceTiming and selectedWeaponSequenceTiming: the length of an action and the moment its blow lands. A pilot or class clip is timed in units of the lunge; a body reaction takes the pace's reaction time.</summary>
        public StageTiming Timing(StageSequence s, SpriteSpeed speed)
        {
            if (s == null) return default;
            if (s.Pilot || s.TimingUnits > 0)
            {
                if (speed.LungeMs <= 0) return default;
                if (s.Reaction) return new StageTiming { TotalMs = speed.ReactionMs, ImpactMs = 0 };
                var unit = speed.LungeMs / (s.TimingUnits > 0 ? s.TimingUnits : PilotUnits);
                return new StageTiming { TotalMs = s.Durations.Sum() * unit, ImpactMs = s.Durations.Take(s.Impact).Sum() * unit };
            }
            var total = DurationFor(s, speed);
            return new StageTiming { TotalMs = total, ImpactMs = s.Durations.Take(s.Impact).Sum() / s.Base * total };
        }

        /// <summary>hurtClipFrame: the frame of the hurt clip a reaction is in, and whether it is one of the red ones (the odd ones).</summary>
        public (int Index, bool Red) HurtClipFrame(double progress)
        {
            var p = progress > 0 ? Math.Min(1, progress) : 0;
            var index = Math.Min(HurtFrames - 1, (int)Math.Floor(HurtFrames * p));
            return (index, index % P1Count.Pair == 1);
        }

        /// <summary>hitFlashOpacity: the red mask over a hurt reaction (a strobe on the red frames of the clip).</summary>
        public double HitFlashOpacity(double elapsed, double duration, bool reduced)
        {
            if (reduced) return 0;
            var t = duration > 0 ? elapsed / duration : elapsed;
            if (!(t >= 0) || t >= 1) return 0;
            return HurtClipFrame(t).Red ? HurtRedOpacity : 0;
        }

        /// <summary>hitRecoilX: the knock-back of a hit reaction in stage units (negative is away from the enemies): an ease-out to the peak and an ease-out back.</summary>
        public double RecoilX(double progress)
        {
            if (!(progress > 0) || progress >= 1) return 0;
            if (progress <= RecoilPeak)
            {
                var s = progress / RecoilPeak;
                return RecoilPx * (1 - (1 - s) * (1 - s));
            }
            var back = (progress - RecoilPeak) / (1 - RecoilPeak);
            return RecoilPx * (1 - back) * (1 - back);
        }
    }
}
