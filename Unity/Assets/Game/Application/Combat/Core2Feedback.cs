using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using E = Ashen.Generated.FxEvents;
using F = Ashen.Generated.FxKeys;
using V = Ashen.Generated.FxValues;

namespace Ashen.App.Combat
{
    /// <summary>What a cue does (the screen draws each one).</summary>
    public enum FxCueType
    {
        None = 0,
        Float,
        Callout,
        HitFlash,
        GuardPose,
        Wobble,
        Shake,
        Slash,
        Spark,
        Glyph,
        Shatter,
        Streak,
        Effect,
        Banner,
        Defeated,
    }

    /// <summary>The look of a floating number: its size tier and colour (the web's float-num classes, styles/combat.css:414-445).</summary>
    public enum FxFloat
    {
        None = 0,
        Damage,
        DamageSmall,
        DamageHeavy,
        DamageCrit,
        DamagePct20,
        DamagePct35,
        Barrier,
        Ward,
        Poise,
        Immune,
        Guard,
        GuardSmall,
        Heal,
        Burst,
    }

    /// <summary>One thing to show over or on a fighter. Which fields matter depends on <see cref="Type"/>.</summary>
    public sealed class FxCue
    {
        public FxCueType Type;
        public string TargetId;
        public string Text;
        public FxFloat Float;
        public double X;
        public double Y;
        public bool Jitter;

        /// <summary>The scale a floating number pops to (the web's --pop).</summary>
        public double Pop;

        public FxCallout Callout;
        public string Status;

        /// <summary>The identity of a callout (the web's kind string): the same word already showing on a fighter is not shown twice.</summary>
        public string Key;

        /// <summary>The tone of a callout or shatter ring (a ui/feedback.json callout tone), or the colour of a status word.</summary>
        public string Tone;

        /// <summary>The effect kind of an Effect cue (vfx.{kind}.{frame}).</summary>
        public string Kind;

        /// <summary>Shake px, or ms a flash is held.</summary>
        public double Amount;

        /// <summary>The hit points the fighter loses when this number lands (negative: gains), so its bar can follow the numbers as the web's onBeatApplied does.</summary>
        public double Hp;

        public bool Heavy;
    }

    public enum FxBeatKind
    {
        None = 0,
        Banner,
        Reveal,
        Draw,
        Attack,
        Act,
        Counter,
        Loose,
    }

    /// <summary>The events of one reaction (a hit and what it did) with the cues of its result, reaction and numbers stages.</summary>
    public sealed class FxGroup
    {
        public List<Core2LogLine> Events = new List<Core2LogLine>();
        public List<FxCue> Result = new List<FxCue>();
        public List<FxCue> Reaction = new List<FxCue>();
        public List<FxCue> Numbers = new List<FxCue>();
        public int Lines;
    }

    /// <summary>One beat of the web's ordered presentation (fx.js groupBeats): a card play, a flask, an enemy move, a counter, a turn banner or a loose run of events.</summary>
    public sealed class FxBeat
    {
        public string ActorId;
        public FxBeatKind Kind;
        public bool Prepared;
        public bool Telegraph;
        public string Banner;
        public string Word;
        public List<Core2LogLine> Events = new List<Core2LogLine>();
        public List<FxCue> Contact = new List<FxCue>();
        public double HoldMs;
        public List<FxGroup> Groups = new List<FxGroup>();

        /// <summary>The cues of a beat with no actor (a banner, a draw, a reveal, a loose run): everything at once.</summary>
        public List<FxCue> Together = new List<FxCue>();

        public List<FxCue> Resolution = new List<FxCue>();

        /// <summary>The lines of the events that are not part of a group (a banner's own, a loose run's).</summary>
        public int Lines;

        public bool HasActor => !string.IsNullOrEmpty(ActorId) && Kind != FxBeatKind.Reveal && Kind != FxBeatKind.Banner;
    }

    /// <summary>What the planner needs from the screen: the pace, the strings, a fighter's max HP and the player's stance (a card play carries no maneuver of its own).</summary>
    public sealed class FxContext
    {
        public FeedbackDefs Defs;
        public StringTable Strings;
        public CombatPace Pace;
        public string PlayerId;
        public string PlayerManeuver;
        public Func<string, double> MaxHp = _ => 0;
    }

    /// <summary>
    /// The combat feedback the web plays for a run of events (src/ui/fx.js playTimeline, groupBeats, visualPartsFor, calloutForEvent, outcomeKindForEvent; src/model/combatEffectEvents.js),
    /// planned from the step events the Core 2 session already returns: the beats, and for each beat its action word, swing, contact cues, and per reaction the result words, the
    /// reaction (flash, recoil, shake, break rings), and the numbers. Engine-free, so the tests pin each rule; the screen only draws the cues (D-750).
    /// </summary>
    public static class Core2Feedback
    {
        // ------------------------------------------------------------------ beats

        public static List<FxBeat> Plan(IReadOnlyList<Core2LogLine> lines, FxContext ctx)
        {
            var beats = Group(lines, ctx);
            foreach (var beat in beats) Build(beat, ctx);
            return beats;
        }

        /// <summary>fx.js groupBeats: each card play, flask use and enemy move starts its own beat; a turn boundary is a banner beat; the draws before a turn start are their own beat.</summary>
        public static List<FxBeat> Group(IReadOnlyList<Core2LogLine> lines, FxContext ctx)
        {
            var beats = new List<FxBeat>();
            var cur = new FxBeat();
            void Push()
            {
                if (cur.Events.Count > 0 || cur.Banner != null || cur.ActorId != null) beats.Add(cur);
            }
            bool Loose(FxBeat b) => b.ActorId == null && b.Banner == null && b.Kind == FxBeatKind.None && b.Events.Count > 0;
            void StartActor(string actor, FxBeatKind kind, Core2LogLine line)
            {
                var lead = Loose(cur) ? cur.Events : new List<Core2LogLine>();
                if (lead.Count == 0) Push();
                cur = new FxBeat { ActorId = actor, Kind = kind };
                cur.Events.AddRange(lead);
                cur.Events.Add(line);
            }
            foreach (var line in lines)
            {
                var e = line.Event ?? new JObject();
                switch (line.Type)
                {
                    case E.CardPlayed:
                        StartActor(FirstOf(e, F.SourcePlayerId, F.PlayerId) ?? ctx.PlayerId, e.Str(F.CardType) == V.Attack ? FxBeatKind.Attack : FxBeatKind.Act, line);
                        break;
                    case E.FlaskUsed:
                        StartActor(ctx.PlayerId, FxBeatKind.Act, line);
                        break;
                    case E.EnemyMoveStarted:
                    case E.CombatHitStarted:
                        StartActor(e.Str(F.SourceId), e.Str(F.Kind) == V.Attack ? FxBeatKind.Attack : FxBeatKind.Act, line);
                        cur.Prepared = e.Is(F.ContactPrepared);
                        break;
                    case E.CombatContactPrepared:
                        StartActor(e.Str(F.SourceId), FxBeatKind.Attack, line);
                        cur.Telegraph = true;
                        break;
                    case E.EnemyActorTurnStarted:
                        StartActor(e.Str(F.SourceId), FxBeatKind.Reveal, line);
                        break;
                    case E.CombatCounterTriggered:
                        StartActor(FirstOf(e, F.SourcePlayerId, F.SourceId), FxBeatKind.Counter, line);
                        cur.Prepared = e.Is(F.ContactPrepared);
                        break;
                    case E.EnemyTurnStart:
                        Push();
                        cur = new FxBeat { Banner = ctx.Strings.Get(StringKeys.Core2FxTurnEnemy), Kind = FxBeatKind.Banner };
                        cur.Events.Add(line);
                        break;
                    case E.PlayerTurnStart:
                        Push();
                        cur = new FxBeat { Banner = ctx.Strings.Get(StringKeys.Core2FxTurnPlayer), Kind = FxBeatKind.Banner };
                        cur.Events.Add(line);
                        break;
                    case E.CardDrawn:
                        // The draws before a turn start refill the hand as their own step.
                        if (cur.ActorId != null && cur.ActorId != ctx.PlayerId && cur.Kind != FxBeatKind.Draw)
                        {
                            Push();
                            cur = new FxBeat { Kind = FxBeatKind.Draw };
                        }
                        cur.Events.Add(line);
                        break;
                    default:
                        cur.Events.Add(line);
                        break;
                }
            }
            Push();
            return beats;
        }

        private static string FirstOf(JObject e, string a, string b)
        {
            var first = e.Str(a);
            return !string.IsNullOrEmpty(first) ? first : e.Str(b);
        }

        private static bool Terminal(Core2LogLine line) => line.Type == E.EnemyDied || line.Type == E.PlayerDied || line.Type == E.CombatEnd;

        private static int Texted(IEnumerable<Core2LogLine> lines) => lines.Count(l => !string.IsNullOrEmpty(l.Text));

        // ------------------------------------------------------------------ one beat

        private static void Build(FxBeat beat, FxContext ctx)
        {
            var parts = beat.Events.ToDictionary(l => l, l => PartsFor(l, beat.Kind, ctx));
            if (beat.Banner != null)
            {
                beat.Together.Add(new FxCue { Type = FxCueType.Banner, Text = beat.Banner });
                foreach (var line in beat.Events.Where(l => !Terminal(l))) beat.Together.AddRange(parts[line].Contact.Concat(parts[line].Reaction).Concat(parts[line].Numbers));
                beat.Lines = Texted(beat.Events);
                return;
            }
            if (!beat.HasActor)
            {
                foreach (var line in beat.Events.Where(l => !Terminal(l)))
                    beat.Together.AddRange(parts[line].Contact.Concat(parts[line].Reaction).Concat(parts[line].Callout).Concat(parts[line].Numbers));
                AddResolution(beat, parts, ctx);
                beat.Lines = Texted(beat.Events);
                return;
            }
            beat.Word = beat.Prepared ? null : ActionWord(beat, ctx);
            foreach (var line in beat.Events.Where(l => !Terminal(l))) beat.Contact.AddRange(parts[line].Contact);
            beat.HoldMs = HoldMs(beat, ctx);
            foreach (var group in SplitGroups(beat))
            {
                foreach (var line in group.Events.Where(l => !Terminal(l)))
                {
                    group.Reaction.AddRange(parts[line].Reaction);
                }
                ResultCues(group, ctx);
                NumberCues(group, parts, ctx);
                group.Lines = Texted(group.Events);
                beat.Groups.Add(group);
            }
            AddResolution(beat, parts, ctx);
        }

        private static void AddResolution(FxBeat beat, Dictionary<Core2LogLine, EventParts> parts, FxContext ctx)
        {
            foreach (var line in beat.Events.Where(Terminal))
            {
                var p = parts[line];
                beat.Resolution.AddRange(p.Contact.Concat(p.Reaction).Concat(p.Callout).Concat(p.Numbers));
            }
        }

        /// <summary>Hit-stop: the figures hold at impact for as long as the beat's hits earn (combatFeel.js hitStopMsFor, scaled by the pace).</summary>
        private static double HoldMs(FxBeat beat, FxContext ctx)
        {
            if (ctx.Pace.Scale <= 0) return 0;
            return beat.Events.Where(l => l.Type == E.DamageDealt)
                .Sum(l => HitStopMs(GuardParts(l.Event).Residual, ctx.MaxHp(l.TargetId), ctx.Defs.Feel) * ctx.Pace.Scale);
        }

        public static double HitStopMs(double hpLost, double maxHp, FeelDefs feel)
        {
            var row = feel.HitStop;
            if (!(row.Ms > 0) || !(hpLost > 0)) return 0;
            if (maxHp > 0 && row.HugeMs > 0 && hpLost / maxHp >= row.HugeShare) return row.HugeMs;
            return hpLost >= row.MinHp ? row.Ms : 0;
        }

        /// <summary>fx.js reactionGroups: a beat's events split after each damage receipt, so a multi-hit plays its hits one by one.</summary>
        private static List<FxGroup> SplitGroups(FxBeat beat)
        {
            var groups = new List<FxGroup>();
            var group = new FxGroup();
            var hadHit = false;
            foreach (var line in beat.Events)
            {
                if (hadHit && (line.Type == E.AttackHit || line.Type == E.AttackMissed || line.Type == E.CombatAvoidanceResolved || line.Type == E.DamageDealt))
                {
                    groups.Add(group);
                    group = new FxGroup();
                    hadHit = false;
                }
                group.Events.Add(line);
                if (line.Type == E.DamageDealt) hadHit = true;
            }
            if (group.Events.Count > 0) groups.Add(group);
            if (groups.Count == 0) groups.Add(new FxGroup());
            return groups;
        }

        // ------------------------------------------------------------------ the action word (CombatActionIdentityModel.js)

        /// <summary>The words over the actor as it acts: its maneuver, the card or move, and the weapon, without repeats ("Attack · Strike"); fx.js onAction falls back to the event's name.</summary>
        private static string ActionWord(FxBeat beat, FxContext ctx)
        {
            var line = beat.Events.FirstOrDefault(l => l.Type == E.CardPlayed || l.Type == E.EnemyMoveStarted || l.Type == E.CombatHitStarted
                || l.Type == E.CombatContactPrepared || l.Type == E.CombatCounterTriggered || l.Type == E.FlaskUsed);
            if (line == null) return null;
            var e = line.Event ?? new JObject();
            var maneuver = beat.Kind == FxBeatKind.Counter ? V.Counter : ManeuverOf(line, ctx);
            var maneuverKey = string.IsNullOrEmpty(maneuver) ? null : string.Format(CultureInfo.InvariantCulture, Core2UiFormats.ManeuverKey, maneuver);
            var type = maneuverKey == null ? null : ctx.Strings.Has(maneuverKey) ? ctx.Strings.Get(maneuverKey) : Capitalise(maneuver);
            var name = line.Type == E.CardPlayed || line.Type == E.FlaskUsed ? line.Detail : MoveLabel(e.Str(F.MoveId) ?? line.Detail);
            var words = new List<string>();
            foreach (var w in new[] { type, name }) if (!string.IsNullOrEmpty(w) && !words.Contains(w)) words.Add(w);
            if (words.Count > 0) return string.Join(FxFormats.WordJoin, words);
            if (line.Type == E.FlaskUsed) return ctx.Strings.Get(StringKeys.Core2FxActionFlask);
            return ctx.Strings.Get(beat.Kind == FxBeatKind.Counter ? StringKeys.Core2FxActionCounter : beat.Kind == FxBeatKind.Attack ? StringKeys.Core2FxActionAttack : StringKeys.Core2FxActionGeneric);
        }

        private static string ManeuverOf(Core2LogLine line, FxContext ctx)
        {
            var e = line.Event ?? new JObject();
            var profile = e.Obj(F.Profile) ?? e.Obj(F.ActionPreview)?.Obj(F.Profile) ?? e.Obj(F.CombatProfile);
            var own = profile?.Str(F.Maneuver) ?? e.Str(F.Maneuver);
            if (!string.IsNullOrEmpty(own)) return own;
            foreach (var key in new[] { F.DerivedTags, F.CardTags })
                foreach (var tag in Js.Items(e[key]).Select(Js.Str))
                    if (tag != null && tag.StartsWith(FxFormats.ManeuverTag, StringComparison.Ordinal)) return tag.Substring(FxFormats.ManeuverTag.Length);
            return line.Type == E.CardPlayed ? ctx.PlayerManeuver : null;
        }

        /// <summary>The web's moveLabel: an id spelled as words, first letter up ("cinderSlash" reads "Cinder Slash").</summary>
        public static string MoveLabel(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var text = new System.Text.StringBuilder();
            var lastWasSeparator = true;
            for (var i = 0; i < id.Length; i++)
            {
                var c = id[i];
                if (!char.IsLetterOrDigit(c)) { if (!lastWasSeparator) text.Append(FxFormats.Space); lastWasSeparator = true; continue; }
                if (i > 0 && char.IsUpper(c) && (char.IsLower(id[i - 1]) || char.IsDigit(id[i - 1])) && !lastWasSeparator) text.Append(FxFormats.Space);
                text.Append(lastWasSeparator && text.Length == 0 ? char.ToUpperInvariant(c) : c);
                lastWasSeparator = false;
            }
            return text.ToString().Trim();
        }

        private static string Capitalise(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        // ------------------------------------------------------------------ result, numbers

        /// <summary>fx.js onResult: accuracy is known before the target reacts, so a Hit or Missed word comes first; a hit with no accuracy receipt says Hit when its damage lands.</summary>
        private static void ResultCues(FxGroup group, FxContext ctx)
        {
            var said = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in group.Events)
            {
                var e = line.Event ?? new JObject();
                FxCallout? kind = null;
                if (line.Type == E.AttackHit) kind = FxCallout.Hit;
                else if (line.Type == E.AttackMissed || line.Type == E.AttackEvaded || (line.Type == E.CombatAvoidanceResolved && e.Is(F.Avoided))) kind = FxCallout.Missed;
                var target = e.Str(F.TargetId);
                if (kind == null || target == null || !said.Add(target)) continue;
                group.Result.Add(Word(kind.Value, target));
                if (kind == FxCallout.Missed && (e.Str(F.MissReason) == V.Range || e.Str(F.Reason) == V.Range)) group.Result.Add(Word(FxCallout.OutOfRange, target));
            }
            foreach (var line in group.Events)
            {
                var target = line.Event?.Str(F.TargetId);
                if (line.Type == E.DamageDealt && target != null && said.Add(target)) group.Result.Add(Word(FxCallout.Hit, target));
            }
        }

        /// <summary>fx.js onNumbers for an actor's group: the dodge word, each event's own word, then its numbers.</summary>
        private static void NumberCues(FxGroup group, Dictionary<Core2LogLine, EventParts> parts, FxContext ctx)
        {
            foreach (var line in group.Events)
            {
                var e = line.Event ?? new JObject();
                var target = e.Str(F.TargetId);
                if (line.Type == E.AttackEvaded || (line.Type == E.CombatAvoidanceResolved && e.Is(F.Avoided) && e.Obj(F.Evade)?.Is(F.Success) == true)
                    || (line.Type == E.AttackMissed && e.Str(F.MissReason) == V.Accuracy && e.Is(F.EvadeActive)))
                {
                    if (target != null) group.Numbers.Add(Word(FxCallout.Dodged, target));
                    continue;
                }
                if (Terminal(line) || line.Type == E.AttackMissed || (line.Type == E.AttackHit && !(e.Is(F.EvadeActive) && !e.Is(F.Bypass)))) continue;
                group.Numbers.AddRange(parts[line].Callout);
            }
            foreach (var line in group.Events.Where(l => !Terminal(l))) group.Numbers.AddRange(parts[line].Numbers);
        }

        // ------------------------------------------------------------------ one event's visuals (fx.js visualPartsFor)

        private sealed class EventParts
        {
            public List<FxCue> Contact = new List<FxCue>();
            public List<FxCue> Reaction = new List<FxCue>();
            public List<FxCue> Callout = new List<FxCue>();
            public List<FxCue> Numbers = new List<FxCue>();
        }

        /// <summary>The split of one damage receipt (fx.js guardHitFloatParts): what Block took, what Poise took, what reached Health.</summary>
        public struct GuardPartsResult
        {
            public double Amount;
            public double Blocked;
            public double Residual;
            public double Barrier;
            public double Ward;
            public double Poise;
            public double Immune;
        }

        public static GuardPartsResult GuardParts(JObject e, double targetMaxHp = 0)
        {
            var amount = Math.Max(0, e.Or0(F.Amount));
            var blocked = Math.Min(amount, Math.Max(0, e.Or0(F.Blocked)));
            return new GuardPartsResult
            {
                Amount = amount,
                Blocked = blocked,
                Residual = amount - blocked,
                Barrier = Math.Max(0, e.Or0(F.BarrierAbsorbed)),
                Ward = Math.Max(0, e.Or0(F.WardAbsorbed)),
                Poise = Math.Max(0, e.Or0(F.PoiseAbsorbed)),
                Immune = Math.Max(0, e.Or0(F.ForceImmune)),
            };
        }

        /// <summary>fx.js dmgClass: the size tier of a damage number.</summary>
        public static FxFloat DamageFloat(double amount, double targetMaxHp, FeelDefs feel)
        {
            var share = targetMaxHp > 0 ? amount / targetMaxHp : 0;
            if (share >= feel.HugeHitShare) return FxFloat.DamagePct35;
            if (amount >= feel.CritAmount) return FxFloat.DamageCrit;
            if (share >= feel.BigHitShare) return FxFloat.DamagePct20;
            if (amount >= feel.HeavyAmount) return FxFloat.DamageHeavy;
            if (amount < feel.SmallBelow) return FxFloat.DamageSmall;
            return FxFloat.Damage;
        }

        /// <summary>The scale a damage number pops to (--pop: 1.3, 1.6 for a fifth of max HP, 1.9 for a third).</summary>
        public static double PopOf(FxFloat kind, FloatDefs f) => kind == FxFloat.DamagePct35 ? f.PopPct35 : kind == FxFloat.DamagePct20 ? f.PopPct20 : f.PopDefault;

        /// <summary>fx.js shakePxFor: how far a hit shakes the scenery (by the share of max HP it cost, else by absolute HP).</summary>
        public static double ShakePx(double hpLost, double maxHp, FeelDefs feel)
        {
            if (!(hpLost > 0)) return 0;
            double px = 0;
            if (maxHp > 0)
            {
                var share = hpLost / maxHp;
                px = feel.ShakeFloorPx;
                foreach (var step in feel.ShakeSteps) if (share >= step.MinShare) px = Math.Max(px, step.Px);
            }
            else
            {
                foreach (var step in feel.ShakeStepsHp) if (hpLost >= step.MinHp) px = Math.Max(px, step.Px);
            }
            return Math.Min(feel.ShakeMaxPx, px);
        }

        /// <summary>fx.js outcomeKindForEvent: the one word of a hit that does no HP damage (Immune, Blocked, Resisted), or null.</summary>
        public static FxCallout? OutcomeOf(Core2LogLine line)
        {
            var e = line.Event ?? new JObject();
            if (line.Type == E.AttackEvaded) return FxCallout.Evaded;
            if (line.Type == E.CombatAvoidanceResolved) return e.Is(F.Avoided) ? FxCallout.Evaded : (FxCallout?)null;
            if (line.Type == E.StatusImmune) return FxCallout.Immune;
            if (line.Type != E.DamageDealt) return null;
            if (e.Is(F.Immune) && !(e.Or0(F.Amount) > 0)) return FxCallout.Immune;
            var parts = GuardParts(e);
            if (parts.Residual > 0) return null;
            if (parts.Blocked > 0) return FxCallout.Blocked;
            if (parts.Amount == 0 && parts.Barrier > 0) return Js.IsNum(e[F.BarrierRemaining]) && e.Or0(F.BarrierRemaining) == 0 ? (FxCallout?)null : FxCallout.Resisted;
            if (parts.Amount == 0 && parts.Ward > 0) return FxCallout.WardResisted;
            return null;
        }

        private static FxCue Word(FxCallout kind, string target) => new FxCue { Type = FxCueType.Callout, TargetId = target, Callout = kind, Key = FxCalloutNames.ToWire(kind) };

        /// <summary>fx.js calloutForEvent: the on-character word an event earns, over the fighter it names.</summary>
        public static FxCue CalloutFor(Core2LogLine line, FxContext ctx)
        {
            var e = line.Event ?? new JObject();
            var target = e.Str(F.TargetId);
            if (target == null) return null;
            if (line.Type == E.StatusApplied && e.Or0(F.Stacks) > 0)
            {
                var status = e.Str(F.Status);
                if (status != null && ctx.Defs.StatusColours.TryGetValue(status, out var colour))
                    return new FxCue { Type = FxCueType.Callout, TargetId = target, Status = status, Tone = colour, Key = FxFormats.StatusPrefix + status };
            }
            FxCallout? kind = null;
            switch (line.Type)
            {
                case E.AttackHit: kind = e.Is(F.EvadeActive) && !e.Is(F.Bypass) ? FxCallout.EvadeFailed : FxCallout.Hit; break;
                case E.AttackMissed: kind = FxCallout.Missed; break;
                case E.DamageDealt:
                case E.StatusImmune: kind = OutcomeOf(line); break;
                case E.EnemyStaggered: if (!e.Is(F.Cause)) kind = FxCallout.Staggered; break;
                case E.PlayerStaggered: kind = FxCallout.Staggered; break;
                case E.EnemyInterrupted: kind = FxCallout.Interrupted; break;
                case E.BlockBroken: if (!e.Is(F.Smash)) kind = FxCallout.BlockBreak; break;
                case E.RatingImpact: if (e.Or0(F.Breaks) > 0) kind = e.Str(F.Label) == V.Disruption ? FxCallout.Disrupted : FxCallout.Staggered; break;
                case E.CombatCounterTriggered: kind = FxCallout.Countered; break;
                case E.AttackEvaded: kind = FxCallout.Evaded; break;
                case E.CombatAvoidanceResolved: if (e.Is(F.Avoided) && !e.Is(F.HitCheck)) kind = FxCallout.Evaded; break;
                case E.CritHit: kind = FxCallout.Critical; break;
                case E.CombatGuardBroken: kind = FxCallout.GuardBreak; break;
                case E.WardBroken: kind = FxCallout.WardBreak; break;
                case E.BarrierBroken: kind = FxCallout.BarrierBreak; break;
                case E.PoiseBroken: kind = FxCallout.PoiseBreak; break;
            }
            return kind == null ? null : Word(kind.Value, target);
        }

        private static EventParts PartsFor(Core2LogLine line, FxBeatKind beatKind, FxContext ctx)
        {
            var parts = new EventParts();
            Base(line, beatKind, ctx, parts);
            var said = CalloutFor(line, ctx);
            if (said != null) parts.Callout.Add(said);
            Effect(line, ctx, parts);
            return parts;
        }

        private static FxCue Float(string target, string text, FxFloat kind, FloatDefs f, double x = 0, double y = 0, bool jitter = true, double pop = 0) => new FxCue
        {
            Type = FxCueType.Float, TargetId = target, Text = text, Float = kind, X = x, Y = y, Jitter = jitter, Pop = pop > 0 ? pop : f.PopDefault,
        };

        private static string Num(double d) => CombatText.Number(d);

        private static string Say(FxContext ctx, string key, double amount) => ctx.Strings.Format(key, new StringArgs().Add(UiPlaceholders.Amount, Num(amount)));

        private static void Base(Core2LogLine line, FxBeatKind beatKind, FxContext ctx, EventParts parts)
        {
            var e = line.Event ?? new JObject();
            var defs = ctx.Defs;
            var f = defs.Float;
            var target = e.Str(F.TargetId);
            switch (line.Type)
            {
                case E.DamageDealt:
                {
                    var maxHp = ctx.MaxHp(target);
                    var g = GuardParts(e, maxHp);
                    var outcome = OutcomeOf(line);
                    var paired = g.Blocked > 0 && g.Residual > 0;
                    if (g.Blocked > 0) parts.Contact.Add(new FxCue { Type = FxCueType.Spark, TargetId = target, Text = ctx.Strings.Get(StringKeys.Core2FxGuard) });
                    if (g.Residual > 0 && beatKind == FxBeatKind.Attack) parts.Contact.Add(new FxCue { Type = FxCueType.Slash, TargetId = target });
                    if (g.Blocked > 0 && g.Residual <= 0) parts.Reaction.Add(new FxCue { Type = FxCueType.GuardPose, TargetId = target, Amount = defs.Feel.PoseHoldMs });
                    if (g.Residual > 0)
                    {
                        var heavy = g.Residual >= defs.Feel.HeavyAmount;
                        parts.Reaction.Add(new FxCue { Type = FxCueType.HitFlash, TargetId = target, Heavy = heavy, Amount = heavy ? defs.Feel.HitFlashHeavyMs : defs.Feel.HitFlashMs });
                        parts.Reaction.Add(new FxCue { Type = FxCueType.Shake, Amount = ShakePx(g.Residual, maxHp, defs.Feel) });
                    }
                    if (outcome != null) break;
                    if (g.Barrier > 0) parts.Numbers.Add(Float(target, g.Amount == 0 && g.Ward == 0 ? Say(ctx, StringKeys.Core2FxResisted, g.Barrier) : Num(g.Barrier), FxFloat.Barrier, f, f.Barrier.X, f.Barrier.Y, false));
                    if (g.Ward > 0) parts.Numbers.Add(Float(target, Num(g.Ward), FxFloat.Ward, f, g.Barrier > 0 ? f.Ward.X : f.WardAlone.X, f.Ward.Y, false));
                    if (g.Poise > 0) parts.Numbers.Add(Float(target, Say(ctx, StringKeys.Core2FxPoiseAbsorbed, g.Poise), FxFloat.Poise, f, f.Poise.X, f.Poise.Y, false));
                    if (g.Immune > 0) parts.Numbers.Add(Float(target, Say(ctx, StringKeys.Core2FxImmuneNumber, g.Immune), FxFloat.Immune, f, f.Immune.X, f.Immune.Y, false));
                    if (g.Blocked > 0) parts.Numbers.Add(Float(target, Num(g.Blocked), FxFloat.GuardSmall, f, paired ? f.GuardPaired.X : 0, 0, false));
                    if (g.Residual > 0)
                    {
                        var tier = DamageFloat(g.Residual, maxHp, defs.Feel);
                        var loss = Float(target, Say(ctx, StringKeys.Core2FxLoss, g.Residual), tier, f, paired ? f.DamagePaired.X : 0, 0, !paired, PopOf(tier, f));
                        loss.Hp = g.Residual;
                        parts.Numbers.Add(loss);
                    }
                    break;
                }
                case E.BlockGained:
                    if (e.Or0(F.Amount) > 0) parts.Numbers.Add(Float(target, Say(ctx, StringKeys.Core2FxGain, e.Or0(F.Amount)), FxFloat.Guard, f));
                    break;
                case E.HpLost:
                {
                    var cause = e.Str(F.Cause);
                    var proc = cause != null && cause.StartsWith(FxFormats.ProcPrefix, StringComparison.Ordinal);
                    if (!proc && cause != V.Effect) break;
                    var amount = e.Or0(F.Amount);
                    if (amount > 0) parts.Reaction.Add(new FxCue { Type = FxCueType.HitFlash, TargetId = target, Amount = defs.Feel.PoseHurtMs });
                    var burst = Float(target, Say(ctx, StringKeys.Core2FxLoss, amount), FxFloat.Burst, f);
                    burst.Hp = amount;
                    parts.Numbers.Add(burst);
                    break;
                }
                case E.Healed:
                    if (e.Or0(F.Amount) > 0)
                    {
                        var gain = Float(target, Say(ctx, StringKeys.Core2FxGain, e.Or0(F.Amount)), FxFloat.Heal, f);
                        gain.Hp = -e.Or0(F.Amount);
                        parts.Numbers.Add(gain);
                    }
                    break;
                case E.ProcBurst:
                    parts.Numbers.Add(new FxCue { Type = FxCueType.Banner, Text = ctx.Strings.Format(StringKeys.Core2FxBurst, new StringArgs().Add(UiPlaceholders.Name, (line.Detail ?? e.Str(F.Status) ?? string.Empty).ToUpperInvariant())), Tone = V.Blood });
                    break;
                case E.ProcResisted:
                    parts.Numbers.Add(Float(target, Say(ctx, StringKeys.Core2FxResisted, e.Or0(F.Blocked)), FxFloat.GuardSmall, f));
                    break;
                case E.RatingImpact:
                    if (e.Or0(F.Breaks) > 0) parts.Reaction.Add(new FxCue { Type = FxCueType.Wobble, TargetId = target });
                    else if (e.Or0(F.Guarded) > 0)
                    {
                        var ward = e.Str(F.Meter) == V.Ward;
                        parts.Numbers.Add(Float(target, Say(ctx, ward ? StringKeys.Core2FxGuardedWard : StringKeys.Core2FxGuardedPoise, e.Or0(F.Guarded)), ward ? FxFloat.Ward : FxFloat.Poise, f));
                    }
                    break;
                case E.MeterGuardGained:
                    if (e.Or0(F.Amount) > 0)
                    {
                        var ward = e.Str(F.Meter) == V.Ward;
                        parts.Numbers.Add(Float(target, Say(ctx, ward ? StringKeys.Core2FxGainedWard : StringKeys.Core2FxGainedPoise, e.Or0(F.Amount)), ward ? FxFloat.Ward : FxFloat.Poise, f));
                    }
                    break;
                case E.CombatAvoidanceResolved:
                case E.AttackEvaded:
                    if (line.Type == E.CombatAvoidanceResolved && !e.Is(F.Avoided)) break;
                    parts.Reaction.Add(new FxCue { Type = FxCueType.Streak, TargetId = target });
                    break;
                case E.BlockBroken: if (!e.Is(F.Smash)) parts.Reaction.Add(Ring(target, V.ToneBlock)); break;
                case E.BarrierBroken: parts.Reaction.Add(Ring(target, V.ToneBarrier)); break;
                case E.WardBroken: parts.Reaction.Add(Ring(target, V.ToneWard)); break;
                case E.PoiseBroken: parts.Reaction.Add(Ring(target, V.ToneGold)); break;
                case E.EnemyInterrupted: parts.Reaction.Add(Ring(target, V.ToneInterrupt)); break;
                case E.PlayerStaggered: parts.Reaction.Add(Ring(target, V.ToneGold)); break;
                case E.EnemyStaggered:
                    parts.Reaction.Add(new FxCue { Type = FxCueType.Wobble, TargetId = target });
                    parts.Reaction.Add(new FxCue { Type = FxCueType.Glyph, TargetId = target, Text = ctx.Strings.Get(StringKeys.Core2FxStun) });
                    parts.Reaction.Add(new FxCue { Type = FxCueType.Shake, Amount = defs.Feel.StaggerShakePx });
                    break;
                case E.EnemyDied:
                    parts.Numbers.Add(Float(target, ctx.Strings.Get(StringKeys.Core2FxDefeated), FxFloat.DamageHeavy, f));
                    parts.Numbers.Add(new FxCue { Type = FxCueType.Defeated, TargetId = target });
                    break;
            }
        }

        private static FxCue Ring(string target, string tone) => new FxCue { Type = FxCueType.Shatter, TargetId = target, Tone = tone };

        /// <summary>combatEffectEvents.js combatEffectForEvent: the effect frames an outcome plays, over the fighter it names.</summary>
        public static FxCue EffectOf(Core2LogLine line, FxContext ctx)
        {
            var e = line.Event ?? new JObject();
            var effects = ctx.Defs.Effects;
            string kind = null;
            var target = e.Str(F.TargetId);
            switch (line.Type)
            {
                case E.StanceEntered:
                    kind = Lookup(effects.Stance, e.Str(F.Stance));
                    target = FirstOf(e, F.PlayerId, F.TargetId) ?? ctx.PlayerId;
                    break;
                case E.StatusApplied:
                    if (e.Or0(F.Stacks) > 0) kind = Lookup(effects.Status, e.Str(F.Status));
                    break;
                case E.ProcBurst: kind = Lookup(effects.Proc, e.Str(F.Status)); break;
                case E.EnemyStaggered: kind = FxEffects.StaggerBreak; break;
                case E.ProcResisted: if (e.Or0(F.Blocked) > 0) kind = FxEffects.Resist; break;
                case E.DodgeRolled:
                    if (e.Is(F.Success)) { kind = FxEffects.Dodge; target = e.Str(F.SourceId); }
                    break;
                case E.MeterGuardGained:
                    if (e.Is(F.Dodge) && e.Str(F.Meter) == V.Poise && e.Or0(F.Amount) > 0) kind = FxEffects.Dodge;
                    break;
                case E.Healed: if (e.Or0(F.Amount) > 0) kind = FxEffects.Heal; break;
                case E.DamageDealt:
                    if (e.Or0(F.Blocked) > 0) kind = FxEffects.PhysicalGuard;
                    break;
            }
            return kind == null || target == null ? null : new FxCue { Type = FxCueType.Effect, TargetId = target, Kind = kind };
        }

        private static string Lookup(Dictionary<string, string> map, string key) => key != null && map.TryGetValue(key, out var v) ? v : null;

        private static void Effect(Core2LogLine line, FxContext ctx, EventParts parts)
        {
            var effect = EffectOf(line, ctx);
            if (effect == null) return;
            // The stagger's break sprite plays with its reaction (the wobble), not at the impact frame.
            if (line.Type == E.EnemyStaggered) parts.Reaction.Add(effect);
            else parts.Contact.Add(effect);
        }
    }
}
