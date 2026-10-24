using System;
using System.Collections.Generic;
using System.Linq;

namespace Ashen.App.Combat
{
    public enum FxItemKind
    {
        None = 0,

        /// <summary>Show a cue.</summary>
        Cue,

        /// <summary>An actor swings (a lunge toward its foe) for <see cref="FxItem.Ms"/>.</summary>
        Swing,

        /// <summary>The beat's events are applied: its log lines are revealed.</summary>
        Reveal,

        /// <summary>The beat's actor starts to act (the highlight and the acting name).</summary>
        Acting,

        /// <summary>The beat is over: its terminal cues played.</summary>
        End,
    }

    /// <summary>One thing to do at a moment of the presentation.</summary>
    public sealed class FxItem
    {
        public double AtMs;
        public FxItemKind Kind;
        public FxCue Cue;
        public FxBeat Beat;
        public string ActorId;
        public bool Attack;
        public double Ms;
        public int Lines;
        public int Order;
    }

    public sealed class FxSchedule
    {
        public List<FxItem> Items = new List<FxItem>();

        /// <summary>When the last beat ends and the next thing (the player's turn, the spoils) may start.</summary>
        public double TotalMs;
    }

    /// <summary>
    /// The order and timing of a beat list (src/ui/actionTimeline.js runActionTimeline with the pace of animationPace.js actionPacing, D11): for an actor the action word, a gap,
    /// the swing to its impact frame (held by the hit-stop), the contact cues, the rest of the swing, a gap, then for every reaction the result words, a gap, the reaction, a gap and the
    /// numbers with the log lines they belong to, a gap, the end and one more gap before the next beat. A banner or a beat without an actor shows everything at once and waits
    /// its banner or step. At animation speed instant (or with reduced motion) every beat shows at once, one instant step after the one before (fx.js animateEvents).
    /// </summary>
    public static class FxTimeline
    {
        public static FxSchedule Build(IReadOnlyList<FxBeat> beats, CombatPace pace, double instantStepMs)
        {
            var schedule = new FxSchedule();
            var t = 0d;
            var order = 0;
            void At(double ms, FxItemKind kind, FxBeat beat, FxCue cue = null, string actor = null, bool attack = false, double span = 0, int lines = 0) =>
                schedule.Items.Add(new FxItem { AtMs = ms, Kind = kind, Beat = beat, Cue = cue, ActorId = actor, Attack = attack, Ms = span, Lines = lines, Order = order++ });
            void Cues(double ms, FxBeat beat, IEnumerable<FxCue> cues)
            {
                foreach (var cue in cues) At(ms, FxItemKind.Cue, beat, cue);
            }
            foreach (var beat in beats)
            {
                if (pace.Instant)
                {
                    At(t, FxItemKind.Acting, beat, actor: beat.HasActor ? beat.ActorId : null);
                    Cues(t, beat, beat.Together);
                    Cues(t, beat, beat.Contact);
                    foreach (var group in beat.Groups)
                    {
                        Cues(t, beat, group.Result);
                        Cues(t, beat, group.Reaction);
                        Cues(t, beat, group.Numbers);
                        At(t, FxItemKind.Reveal, beat, lines: group.Lines);
                    }
                    At(t, FxItemKind.Reveal, beat, lines: beat.Lines);
                    Cues(t, beat, beat.Resolution);
                    At(t, FxItemKind.End, beat);
                    t += instantStepMs;
                    continue;
                }
                var reset = Math.Max(pace.GapMs, pace.StepMs);
                if (!beat.HasActor)
                {
                    At(t, FxItemKind.Acting, beat);
                    Cues(t, beat, beat.Together);
                    At(t, FxItemKind.Reveal, beat, lines: beat.Lines);
                    Cues(t, beat, beat.Resolution);
                    At(t, FxItemKind.End, beat);
                    t += beat.Banner != null ? pace.BannerMs : pace.StepMs;
                    continue;
                }
                At(t, FxItemKind.Acting, beat, actor: beat.ActorId);
                if (beat.Word != null) At(t, FxItemKind.Cue, beat, new FxCue { Type = FxCueType.Callout, TargetId = beat.ActorId, Text = beat.Word, Key = beat.Word });
                t += beat.Prepared ? 0 : pace.GapMs;
                var swing = !beat.Prepared && pace.Scale > 0;
                var attack = beat.Kind == FxBeatKind.Attack || beat.Kind == FxBeatKind.Counter;
                if (swing) At(t, FxItemKind.Swing, beat, actor: beat.ActorId, attack: attack, span: pace.LungeMs);
                var impact = swing ? pace.ImpactMs : 0;
                var total = swing ? pace.LungeMs : 0;
                t += impact;
                Cues(t, beat, beat.Contact);
                t += Math.Max(total - impact + beat.HoldMs, 0);
                if (beat.Telegraph)
                {
                    Cues(t, beat, beat.Resolution);
                    At(t, FxItemKind.End, beat);
                    t += reset;
                    continue;
                }
                t += pace.GapMs;
                for (var i = 0; i < beat.Groups.Count; i++)
                {
                    var group = beat.Groups[i];
                    Cues(t, beat, group.Result);
                    t += pace.GapMs;
                    Cues(t, beat, group.Reaction);
                    t += pace.GapMs;
                    Cues(t, beat, group.Numbers);
                    At(t, FxItemKind.Reveal, beat, lines: group.Lines);
                    t += pace.GapMs;
                }
                At(t, FxItemKind.Reveal, beat, lines: beat.Lines);
                Cues(t, beat, beat.Resolution);
                At(t, FxItemKind.End, beat);
                t += reset;
            }
            schedule.TotalMs = t;
            schedule.Items = schedule.Items.OrderBy(i => i.AtMs).ThenBy(i => i.Order).ToList();
            return schedule;
        }
    }
}
