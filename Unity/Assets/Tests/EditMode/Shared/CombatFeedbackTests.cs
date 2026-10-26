using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// VP-5 (D-750): the combat feedback the web plays (src/ui/fx.js), planned from the events a step returns: the data (ui/feedback.json) is whole, the animation maths
    /// matches CSS, the pace follows the pacing settings as animationPace.js does, each rule of visualPartsFor / calloutForEvent / outcomeKindForEvent holds, and the
    /// timeline orders the stages as runActionTimeline does (suite: Presentation, engine-free).
    /// </summary>
    [TestFixture, Category("Presentation")]
    public class CombatFeedbackTests
    {
        private static UiData _ui;
        private static UiData Ui => _ui ?? (_ui = UiData.Load(TestContent.Source));
        private static FeedbackDefs Defs => Ui.Feedback;

        private static CombatPacing Pacing(string anim = "normal", double speed = 1, double gap = 500, double step = 390) =>
            new CombatPacing { AnimSpeed = anim, Speed = speed, AutoSpeed = speed, StageGapMs = gap, AnimationStepMs = step };

        private static FxContext Context(CombatPace pace = null, double maxHp = 20) => new FxContext
        {
            Defs = Defs, Strings = Ui.Strings, Pace = pace ?? CombatPace.From(Pacing(), Defs, false), PlayerId = "player", PlayerManeuver = "attack", MaxHp = _ => maxHp,
        };

        private static Core2LogLine Line(string type, string source, string target, object payload = null, string detail = null, string text = "x")
        {
            var e = payload == null ? new JObject() : JObject.FromObject(payload);
            e["type"] = type;
            if (source != null) e["sourceId"] = source;
            if (target != null) e["targetId"] = target;
            return new Core2LogLine { Type = type, SourceId = source, TargetId = target, Event = e, Detail = detail, Text = text };
        }

        // ------------------------------------------------------------------ the data

        [Test]
        public void TheDataIsWhole()
        {
            foreach (var id in Enum.GetValues(typeof(FxTrack)).Cast<FxTrack>().Where(t => t != FxTrack.None))
            {
                var track = Defs.Track(id);
                Assert.That(Defs.Easings.ContainsKey(track.Ease), Is.True, id + " ease " + track.Ease);
                Assert.That(track.Ms, Is.GreaterThan(0), id.ToString());
                Assert.That(track.Stops.Select(s => s.T), Is.Ordered, id.ToString());
                Assert.That(track.Stops.First().T, Is.EqualTo(0), id.ToString());
                Assert.That(track.Stops.Last().T, Is.EqualTo(1), id.ToString());
            }
            foreach (var kind in Enum.GetValues(typeof(FxCallout)).Cast<FxCallout>().Where(k => k != FxCallout.None))
            {
                var def = Defs.Kind(kind);
                Assert.That(Ui.Strings.Has(string.Format(CultureInfo.InvariantCulture, FxFormats.CalloutKey, def.Word)), Is.True, kind.ToString());
                Assert.That(def.Tone, Is.Not.Empty, kind.ToString());
            }
            foreach (var status in Defs.StatusColours.Keys)
                Assert.That(Ui.Strings.Has(string.Format(CultureInfo.InvariantCulture, FxFormats.StatusKey, status)), Is.True, status);
            foreach (var speed in Defs.Pace.Speeds.Values) Assert.That(speed.BeatMs, Is.GreaterThan(0));
            Assert.That(Defs.Pace.Speeds.ContainsKey(Defs.Pace.Reference), Is.True);
        }

        // ------------------------------------------------------------------ CSS maths

        [Test]
        public void BezierEndsAtItsEndsAndFollowsTheCurve()
        {
            var ease = new[] { 0.25, 0.1, 0.25, 1 };
            Assert.That(FxCurve.Bezier(ease, 0), Is.EqualTo(0));
            Assert.That(FxCurve.Bezier(ease, 1), Is.EqualTo(1));
            // CSS 'ease' is 0.802 at half way.
            Assert.That(FxCurve.Bezier(ease, 0.5), Is.EqualTo(0.8024).Within(0.001));
            var linear = Defs.Easings["linear"];
            Assert.That(FxCurve.Bezier(linear, 0.37), Is.EqualTo(0.37).Within(0.001));
        }

        [Test]
        public void NumPopRisesPopsAndFades()
        {
            var track = Defs.Track(FxTrack.NumPop);
            var start = FxCurve.Sample(track, Defs.Easings, 0, pop: 1.6);
            Assert.That(start.Opacity, Is.EqualTo(0));
            Assert.That(start.Y, Is.EqualTo(8));
            Assert.That(start.Scale, Is.EqualTo(0.4));
            var peak = FxCurve.Sample(track, Defs.Easings, track.Ms * 0.14, pop: 1.6);
            Assert.That(peak.Opacity, Is.EqualTo(1).Within(0.001));
            Assert.That(peak.Scale, Is.EqualTo(1.6).Within(0.001));
            var held = FxCurve.Sample(track, Defs.Easings, track.Ms * 0.72, pop: 1.6);
            Assert.That(held.Opacity, Is.EqualTo(1).Within(0.001));
            var end = FxCurve.Sample(track, Defs.Easings, track.Ms, pop: 1.6);
            Assert.That(end.Opacity, Is.EqualTo(0).Within(0.001));
            Assert.That(end.Y, Is.EqualTo(-52).Within(0.001));
        }

        [Test]
        public void RecoilHoldsAtItsFarthestPointAndTintsUntilFortyFivePercent()
        {
            var track = Defs.Track(FxTrack.HitEnemy);
            Assert.That(FxCurve.Sample(track, Defs.Easings, 0).Tinted, Is.True);
            Assert.That(FxCurve.Sample(track, Defs.Easings, track.Ms * 0.3).X, Is.EqualTo(22).Within(0.001));
            Assert.That(FxCurve.Sample(track, Defs.Easings, track.Ms * 0.44).Tinted, Is.True);
            Assert.That(FxCurve.Sample(track, Defs.Easings, track.Ms * 0.46).Tinted, Is.False);
            Assert.That(FxCurve.Sample(track, Defs.Easings, track.Ms).X, Is.EqualTo(0).Within(0.001));
            // The player recoils the other way.
            Assert.That(FxCurve.Sample(Defs.Track(FxTrack.HitPlayer), Defs.Easings, Defs.Track(FxTrack.HitPlayer).Ms * 0.3).X, Is.EqualTo(-13).Within(0.001));
        }

        [Test]
        public void ShakeScalesByItsAmplitude()
        {
            var track = Defs.Track(FxTrack.Shake);
            var quarter = FxCurve.Sample(track, Defs.Easings, track.Ms * 0.25, amp: 7);
            Assert.That(quarter.X, Is.EqualTo(7).Within(0.001));
            Assert.That(quarter.Y, Is.EqualTo(-5.25).Within(0.001));
        }

        // ------------------------------------------------------------------ pace

        [Test]
        public void ThePaceFollowsTheSettingsAsTheWebDoes()
        {
            var normal = CombatPace.From(Pacing(), Defs, false);
            Assert.That(normal.Scale, Is.EqualTo(1));
            Assert.That(normal.GapMs, Is.EqualTo(500));
            Assert.That(normal.LungeMs, Is.EqualTo(390).Within(0.001));
            Assert.That(normal.ImpactMs, Is.EqualTo(390 * 0.55).Within(0.001));
            Assert.That(normal.BannerMs, Is.EqualTo(240));
            Assert.That(normal.StepMs, Is.EqualTo(68));
            var slow = CombatPace.From(Pacing("slow"), Defs, false);
            Assert.That(slow.Scale, Is.EqualTo(1.75).Within(0.001));
            var fast = CombatPace.From(Pacing("fast"), Defs, false);
            Assert.That(fast.BannerMs, Is.EqualTo(Math.Max(156, 108) * 0.45).Within(0.001));
            var double_speed = CombatPace.From(Pacing(speed: 2), Defs, false);
            Assert.That(double_speed.GapMs, Is.EqualTo(250));
            var shortPause = CombatPace.From(Pacing(gap: 250, step: 195), Defs, false);
            Assert.That(shortPause.GapMs, Is.EqualTo(250));
            Assert.That(shortPause.LungeMs, Is.EqualTo(195).Within(0.001));
        }

        [Test]
        public void TheSettingAutoPlaysAtTheReferenceSpeedAndInstantOrReducedPlayNoWaits()
        {
            Assert.That(CombatPace.From(Pacing("auto"), Defs, false).Scale, Is.EqualTo(1));
            Assert.That(CombatPace.From(Pacing(Core2UiValues.AnimInstant), Defs, false).Instant, Is.True);
            var reduced = CombatPace.From(Pacing(), Defs, true);
            Assert.That(reduced.Instant, Is.True);
            Assert.That(reduced.GapMs, Is.EqualTo(0));
            Assert.That(reduced.Reduced, Is.True);
            Assert.That(CombatPace.From(Pacing(Core2UiValues.AnimInstant), Defs, false).CalloutMs, Is.EqualTo(Defs.Callout.InstantMs));
            Assert.That(CombatPace.From(Pacing("slow"), Defs, false).CalloutMs, Is.EqualTo(900 * 1.75).Within(0.001));
        }

        // ------------------------------------------------------------------ callout queue

        [Test]
        public void WordsStartInPriorityOrderOneStepApartAndAFourthWaitsForALane()
        {
            var items = new[]
            {
                new CalloutWord { Arrival = 0, Rank = 3 },
                new CalloutWord { Arrival = 1, Rank = 6 },
                new CalloutWord { Arrival = 2, Rank = 3 },
                new CalloutWord { Arrival = 3, Rank = 3 },
            };
            var plan = CalloutPlan.Plan(items, new double[] { 0, 0, 0 }, 150, 900, null);
            Assert.That(plan.Select(p => p.Arrival), Is.EqualTo(new[] { 1, 0, 2, 3 }));
            Assert.That(plan.Select(p => p.Start), Is.EqualTo(new[] { 0d, 150d, 300d, 900d }));
            Assert.That(plan.Select(p => p.Lane), Is.EqualTo(new[] { 0, 1, 2, 0 }));
        }

        [Test]
        public void StatusWordsOutrankAMissAndABlockOutranksAResist()
        {
            Assert.That(CalloutPlan.Rank(Defs.Callout, null, true), Is.GreaterThan(CalloutPlan.Rank(Defs.Callout, FxCalloutNames.ToWire(FxCallout.Evaded), false)));
            Assert.That(CalloutPlan.Rank(Defs.Callout, FxCalloutNames.ToWire(FxCallout.Blocked), false), Is.GreaterThan(CalloutPlan.Rank(Defs.Callout, FxCalloutNames.ToWire(FxCallout.Resisted), false)));
            Assert.That(CalloutPlan.Rank(Defs.Callout, FxCalloutNames.ToWire(FxCallout.Staggered), false), Is.EqualTo(Defs.Callout.Priority[FxPriority.Other]));
        }

        // ------------------------------------------------------------------ the planner

        [Test]
        public void ThePlayersStrikeIsOneAttackBeatThatNamesItself()
        {
            var lines = new[]
            {
                Line(FxEvents.CardPlayed, null, "e1", new { cardType = "attack", cardId = "strike" }, detail: "Strike"),
                Line(FxEvents.DamageDealt, "player", "e1", new { amount = 12, blocked = 0, hpLoss = 12 }),
                Line(FxEvents.HpLost, null, "e1", new { amount = 12, cause = "attack" }),
            };
            var beats = Core2Feedback.Plan(lines, Context());
            Assert.That(beats, Has.Count.EqualTo(1));
            var beat = beats[0];
            Assert.That(beat.Kind, Is.EqualTo(FxBeatKind.Attack));
            Assert.That(beat.ActorId, Is.EqualTo("player"));
            Assert.That(beat.Word, Is.EqualTo("Attack · Strike"));
            Assert.That(beat.Contact.Select(c => c.Type), Does.Contain(FxCueType.Slash));
            var group = beat.Groups.Single();
            Assert.That(group.Result.Select(c => c.Callout), Is.EqualTo(new[] { FxCallout.Hit }));
            Assert.That(group.Reaction.Select(c => c.Type), Does.Contain(FxCueType.HitFlash));
            var shake = group.Reaction.Single(c => c.Type == FxCueType.Shake);
            Assert.That(shake.Amount, Is.EqualTo(10), "12 of 20 HP is over a third: the 10 px step");
            var number = group.Numbers.Single(c => c.Type == CueFloat);
            Assert.That(number.Text, Is.EqualTo("-12"));
            Assert.That(number.Float, Is.EqualTo(FxFloat.DamagePct35), "12 of 20 is over a third of its max HP: the biggest tier");
            Assert.That(number.Pop, Is.EqualTo(Defs.Float.PopPct35));
            Assert.That(number.Jitter, Is.True);
        }

        private const FxCueType CueFloat = FxCueType.Float;

        [Test]
        public void ABlockedHitShowsGuardAndDamageSideBySideWithoutJitter()
        {
            var lines = new[]
            {
                Line(FxEvents.EnemyMoveStarted, "e1", null, new { kind = "attack", moveId = "cinderSlash" }),
                Line(FxEvents.DamageDealt, "e1", "player", new { amount = 9, blocked = 4, hpLoss = 5 }),
            };
            var beat = Core2Feedback.Plan(lines, Context(maxHp: 71)).Single();
            Assert.That(beat.Word, Is.EqualTo("Cinder Slash"));
            var numbers = beat.Groups.Single().Numbers.Where(c => c.Type == FxCueType.Float).ToList();
            Assert.That(numbers.Select(n => n.Text), Is.EqualTo(new[] { "4", "-5" }));
            Assert.That(numbers.Select(n => n.X), Is.EqualTo(new[] { -26d, 26d }));
            Assert.That(numbers.All(n => !n.Jitter), Is.True);
            Assert.That(numbers[1].Float, Is.EqualTo(FxFloat.DamageSmall));
            Assert.That(beat.Contact.Select(c => c.Type), Does.Contain(FxCueType.Spark));
            Assert.That(beat.Contact.Select(c => c.Type), Does.Contain(FxCueType.Effect), "the block plays the guard effect");
        }

        [Test]
        public void AHitThatDoesNoHpDamageSaysBlockedInsteadOfNumbers()
        {
            var lines = new[]
            {
                Line(FxEvents.EnemyMoveStarted, "e1", null, new { kind = "attack", moveId = "slash" }),
                Line(FxEvents.DamageDealt, "e1", "player", new { amount = 6, blocked = 6, hpLoss = 0 }),
            };
            var group = Core2Feedback.Plan(lines, Context()).Single().Groups.Single();
            Assert.That(group.Numbers.Where(c => c.Type == FxCueType.Float), Is.Empty);
            Assert.That(group.Numbers.Where(c => c.Type == FxCueType.Callout).Select(c => c.Callout), Is.EqualTo(new[] { FxCallout.Blocked }));
            Assert.That(group.Reaction.Select(c => c.Type), Does.Contain(FxCueType.GuardPose));
            Assert.That(group.Reaction.Select(c => c.Type), Does.Not.Contain(FxCueType.HitFlash), "fully guarded: no flinch");
        }

        [Test]
        public void AMissAndAnEvadeSayMissedAndDodged()
        {
            var lines = new[]
            {
                Line(FxEvents.EnemyMoveStarted, "e1", null, new { kind = "attack", moveId = "slash" }),
                Line(FxEvents.AttackMissed, "e1", "player", new { missReason = "accuracy", evadeActive = true }),
            };
            var group = Core2Feedback.Plan(lines, Context()).Single().Groups.Single();
            Assert.That(group.Result.Select(c => c.Callout), Is.EqualTo(new[] { FxCallout.Missed }));
            Assert.That(group.Numbers.Select(c => c.Callout), Is.EqualTo(new[] { FxCallout.Dodged }));
            var range = new[]
            {
                Line(FxEvents.EnemyMoveStarted, "e1", null, new { kind = "attack", moveId = "slash" }),
                Line(FxEvents.AttackMissed, "e1", "player", new { missReason = "range" }),
            };
            Assert.That(Core2Feedback.Plan(range, Context()).Single().Groups.Single().Result.Select(c => c.Callout), Is.EqualTo(new[] { FxCallout.Missed, FxCallout.OutOfRange }));
        }

        [Test]
        public void AHostileStatusEarnsItsWordInItsColourAndAnEffect()
        {
            var lines = new[]
            {
                Line(FxEvents.CardPlayed, null, "e1", new { cardType = "skill" }, detail: "Hex"),
                Line(FxEvents.StatusApplied, "player", "e1", new { status = "venom", stacks = 3 }),
                Line(FxEvents.StatusApplied, "player", "e1", new { status = "strength", stacks = 1 }),
            };
            var beat = Core2Feedback.Plan(lines, Context()).Single();
            var words = beat.Groups.Single().Numbers.Where(c => c.Type == FxCueType.Callout).ToList();
            Assert.That(words.Select(w => w.Status), Is.EqualTo(new[] { "venom" }), "a buff earns no word");
            Assert.That(words[0].Tone, Is.EqualTo(Defs.StatusColours["venom"]));
            Assert.That(beat.Contact.Where(c => c.Type == FxCueType.Effect).Select(c => c.Kind), Is.EqualTo(new[] { "poisoned", "strength" }));
        }

        [Test]
        public void AStaggerWobblesShakesAndFourPixelsAndATurnIsABanner()
        {
            var lines = new[]
            {
                Line(FxEvents.EnemyTurnStart, null, null),
                Line(FxEvents.CardPlayed, null, "e1", new { cardType = "attack" }, detail: "Bash"),
                Line(FxEvents.EnemyStaggered, null, "e1"),
                Line(FxEvents.PlayerTurnStart, null, null),
            };
            var beats = Core2Feedback.Plan(lines, Context());
            Assert.That(beats.Select(b => b.Kind), Is.EqualTo(new[] { FxBeatKind.Banner, FxBeatKind.Attack, FxBeatKind.Banner }));
            Assert.That(beats[0].Together.Single(c => c.Type == FxCueType.Banner).Text, Is.EqualTo(Ui.Strings.Get(StringKeys.Core2FxTurnEnemy)));
            var reaction = beats[1].Groups.Single().Reaction;
            Assert.That(reaction.Select(c => c.Type), Is.EquivalentTo(new[] { FxCueType.Wobble, FxCueType.Glyph, FxCueType.Shake, FxCueType.Effect }));
            Assert.That(reaction.Single(c => c.Type == FxCueType.Shake).Amount, Is.EqualTo(4));
            Assert.That(beats[1].Groups.Single().Numbers.Select(c => c.Callout), Is.EqualTo(new[] { FxCallout.Staggered }));
        }

        [Test]
        public void ADeathIsAResolutionCueAfterTheKillingBlow()
        {
            var lines = new[]
            {
                Line(FxEvents.CardPlayed, null, "e1", new { cardType = "attack" }, detail: "Strike"),
                Line(FxEvents.DamageDealt, "player", "e1", new { amount = 12, blocked = 0 }),
                Line(FxEvents.EnemyDied, null, "e1"),
            };
            var beat = Core2Feedback.Plan(lines, Context()).Single();
            Assert.That(beat.Groups.Single().Numbers.Select(c => c.Text), Is.EqualTo(new[] { "-12" }));
            Assert.That(beat.Resolution.Select(c => c.Type), Is.EqualTo(new[] { FxCueType.Float, FxCueType.Defeated }));
            Assert.That(beat.Resolution[0].Text, Is.EqualTo("Defeated"));
        }

        [Test]
        public void AMultiHitPlaysItsHitsOneByOneAndTheCostEventsRideTheCard()
        {
            var lines = new[]
            {
                Line(FxEvents.EnemyMoveStarted, "e1", null, new { kind = "attack", moveId = "flurry" }),
                Line(FxEvents.DamageDealt, "e1", "player", new { amount = 3, blocked = 0 }),
                Line(FxEvents.DamageDealt, "e1", "player", new { amount = 4, blocked = 0 }),
            };
            var beat = Core2Feedback.Plan(lines, Context()).Single();
            Assert.That(beat.Groups, Has.Count.EqualTo(2));
            Assert.That(beat.Groups.Select(g => g.Numbers.Single(c => c.Type == FxCueType.Float).Text), Is.EqualTo(new[] { "-3", "-4" }));
            var cost = new[]
            {
                Line("staminaSpent", null, null),
                Line(FxEvents.CardPlayed, null, "e1", new { cardType = "attack" }, detail: "Strike"),
            };
            var beats = Core2Feedback.Plan(cost, Context());
            Assert.That(beats, Has.Count.EqualTo(1), "the cost a card pays is no beat of its own");
        }

        [Test]
        public void TheDamageTiersAndShakeStepsAreTheWebs()
        {
            var feel = Defs.Feel;
            Assert.That(Core2Feedback.DamageFloat(5, 0, feel), Is.EqualTo(FxFloat.DamageSmall));
            Assert.That(Core2Feedback.DamageFloat(8, 0, feel), Is.EqualTo(FxFloat.Damage));
            Assert.That(Core2Feedback.DamageFloat(15, 0, feel), Is.EqualTo(FxFloat.DamageHeavy));
            Assert.That(Core2Feedback.DamageFloat(25, 0, feel), Is.EqualTo(FxFloat.DamageCrit));
            Assert.That(Core2Feedback.DamageFloat(20, 100, feel), Is.EqualTo(FxFloat.DamagePct20));
            Assert.That(Core2Feedback.DamageFloat(35, 100, feel), Is.EqualTo(FxFloat.DamagePct35));
            Assert.That(Core2Feedback.ShakePx(1, 100, feel), Is.EqualTo(1));
            Assert.That(Core2Feedback.ShakePx(2, 100, feel), Is.EqualTo(2));
            Assert.That(Core2Feedback.ShakePx(8, 100, feel), Is.EqualTo(4));
            Assert.That(Core2Feedback.ShakePx(35, 100, feel), Is.EqualTo(10));
            Assert.That(Core2Feedback.ShakePx(15, 0, feel), Is.EqualTo(4));
            Assert.That(Core2Feedback.ShakePx(0, 100, feel), Is.EqualTo(0));
            Assert.That(Core2Feedback.HitStopMs(5, 100, feel), Is.EqualTo(0));
            Assert.That(Core2Feedback.HitStopMs(6, 100, feel), Is.EqualTo(60));
            Assert.That(Core2Feedback.HitStopMs(1, 100, feel), Is.EqualTo(0));
            Assert.That(Core2Feedback.HitStopMs(35, 100, feel), Is.EqualTo(120));
        }

        [Test]
        public void MoveLabelSpellsAnIdAsWords()
        {
            Assert.That(Core2Feedback.MoveLabel("cinderSlash"), Is.EqualTo("Cinder Slash"));
            Assert.That(Core2Feedback.MoveLabel("slash"), Is.EqualTo("Slash"));
            Assert.That(Core2Feedback.MoveLabel("big_bad-move"), Is.EqualTo("Big bad move"));
            Assert.That(Core2Feedback.MoveLabel(null), Is.Null);
        }

        // ------------------------------------------------------------------ the timeline

        [Test]
        public void TheTimelineOrdersTheStagesWithOneGapBetweenEach()
        {
            var lines = new[]
            {
                Line(FxEvents.CardPlayed, null, "e1", new { cardType = "attack" }, detail: "Strike"),
                Line(FxEvents.DamageDealt, "player", "e1", new { amount = 12, blocked = 0 }),
            };
            var pace = CombatPace.From(Pacing(), Defs, false);
            var beats = Core2Feedback.Plan(lines, Context(pace, maxHp: 200));
            var schedule = FxTimeline.Build(beats, pace, Defs.Pace.InstantStepMs);
            double At(Func<FxItem, bool> pick) => schedule.Items.First(pick).AtMs;
            var word = At(i => i.Kind == FxItemKind.Cue && i.Cue.Type == FxCueType.Callout && i.Cue.Text != null);
            var swing = At(i => i.Kind == FxItemKind.Swing);
            var contact = At(i => i.Kind == FxItemKind.Cue && i.Cue.Type == FxCueType.Slash);
            var result = At(i => i.Kind == FxItemKind.Cue && i.Cue.Type == FxCueType.Callout && i.Cue.Callout == FxCallout.Hit);
            var reaction = At(i => i.Kind == FxItemKind.Cue && i.Cue.Type == FxCueType.HitFlash);
            var numbers = At(i => i.Kind == FxItemKind.Cue && i.Cue.Type == FxCueType.Float);
            Assert.That(word, Is.EqualTo(0));
            Assert.That(swing, Is.EqualTo(500));
            Assert.That(contact, Is.EqualTo(500 + 390 * 0.55).Within(0.001));
            Assert.That(result, Is.EqualTo(500 + 390 + 60 + 500).Within(0.001), "after the swing ends (held 60 ms by the hit-stop) and one gap");
            Assert.That(reaction, Is.EqualTo(result + 500).Within(0.001));
            Assert.That(numbers, Is.EqualTo(reaction + 500).Within(0.001));
            var reveal = schedule.Items.First(i => i.Kind == FxItemKind.Reveal && i.Lines > 0);
            Assert.That(reveal.AtMs, Is.EqualTo(numbers), "the log lines appear with the numbers");
            Assert.That(schedule.TotalMs, Is.EqualTo(numbers + 500 + 500).Within(0.001));
        }

        [Test]
        public void ABannerHoldsItsBannerAndInstantStaggersTheBeats()
        {
            var lines = new[]
            {
                Line(FxEvents.EnemyTurnStart, null, null),
                Line(FxEvents.EnemyMoveStarted, "e1", null, new { kind = "attack", moveId = "slash" }),
                Line(FxEvents.DamageDealt, "e1", "player", new { amount = 3, blocked = 0 }),
            };
            var pace = CombatPace.From(Pacing(), Defs, false);
            var beats = Core2Feedback.Plan(lines, Context(pace));
            var schedule = FxTimeline.Build(beats, pace, Defs.Pace.InstantStepMs);
            Assert.That(schedule.Items.First(i => i.Kind == FxItemKind.Swing).AtMs, Is.EqualTo(pace.BannerMs + 500).Within(0.001));
            var instant = CombatPace.From(Pacing(Core2UiValues.AnimInstant), Defs, false);
            var quick = FxTimeline.Build(Core2Feedback.Plan(lines, Context(instant)), instant, Defs.Pace.InstantStepMs);
            Assert.That(quick.Items.Where(i => i.Kind == FxItemKind.Swing), Is.Empty);
            Assert.That(quick.Items.First(i => i.Kind == FxItemKind.Cue && i.Cue.Type == FxCueType.Float).AtMs, Is.EqualTo(Defs.Pace.InstantStepMs));
            Assert.That(quick.TotalMs, Is.EqualTo(Defs.Pace.InstantStepMs * 2));
        }

        [Test]
        public void ATelegraphHasNoSwingOrNumbersOfItsOwn()
        {
            var lines = new[] { Line(FxEvents.CombatContactPrepared, "e1", null, new { kind = "attack" }) };
            var pace = CombatPace.From(Pacing(), Defs, false);
            var beat = Core2Feedback.Plan(lines, Context(pace)).Single();
            Assert.That(beat.Telegraph, Is.True);
            Assert.That(beat.Groups.Single().Numbers, Is.Empty);
        }
    }
}
