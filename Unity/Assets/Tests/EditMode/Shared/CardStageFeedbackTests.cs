using System;
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
    /// P-3b (D-1490..): the beats of the player's card stage. A card plays the clip of its action at the length the pace gives that clip (alternativeCardStage.js actionTiming), the hit-stop holds the
    /// figure at its blow, and the Reaver pilot's body reactions follow the receipts of an enemy blow (model/selectedWeaponStances.js selectedWeaponReactionFor) and are waited for.
    /// </summary>
    [TestFixture, Category("Presentation")]
    public class CardStageFeedbackTests
    {
        private static UiData _ui;
        private static UiData Ui => _ui ?? (_ui = UiData.Load(TestContent.Source));
        private static FeedbackDefs Defs => Ui.Feedback;

        private static CombatPace Pace(string anim = "normal") =>
            CombatPace.From(new CombatPacing { AnimSpeed = anim, Speed = 1, AutoSpeed = 1, StageGapMs = 500, AnimationStepMs = 390 }, Defs, false);

        private static Core2LogLine Line(string type, string source, string target, object payload = null, string detail = null)
        {
            var e = payload == null ? new JObject() : JObject.FromObject(payload);
            e["type"] = type;
            if (source != null) e["sourceId"] = source;
            if (target != null) e["targetId"] = target;
            return new Core2LogLine { Type = type, SourceId = source, TargetId = target, Event = e, Detail = detail, Text = "x" };
        }

        private static FxContext Context(CombatPace pace, bool pilot) => new FxContext
        {
            Defs = Defs, Strings = Ui.Strings, Pace = pace, PlayerId = "player", PlayerManeuver = "attack", MaxHp = _ => 200,
            Stage = pilot ? Ui.CardStage : null, CanReact = kind => pilot && Ui.CardStage.Class("reaver").Pilot,
        };

        // ------------------------------------------------------------------ the receipts

        [Test]
        public void AnEnemyBlowEarnsTheReactionTheWebGivesItsReceipt()
        {
            Assert.That(CardStageReactions.KindFor(Line("combatGuardBroken", "e1", "player"), "player"), Is.EqualTo("guard-break"));
            Assert.That(CardStageReactions.KindFor(Line("attackEvaded", "e1", "player"), "player"), Is.EqualTo("evade"));
            Assert.That(CardStageReactions.KindFor(Line("attackMissed", "e1", "player", new { missReason = "accuracy", evadeActive = true }), "player"), Is.EqualTo("evade"));
            Assert.That(CardStageReactions.KindFor(Line("attackMissed", "e1", "player", new { missReason = "accuracy", evadeStacks = 2 }), "player"), Is.EqualTo("evade"));
            Assert.That(CardStageReactions.KindFor(Line("attackMissed", "e1", "player", new { missReason = "range", evadeActive = true }), "player"), Is.Null, "only an accuracy miss is an evade");
            Assert.That(CardStageReactions.KindFor(Line("combatAvoidanceResolved", "e1", "player", new { avoided = true, evade = new { success = true } }), "player"), Is.EqualTo("evade"));
            Assert.That(CardStageReactions.KindFor(Line("combatAvoidanceResolved", "e1", "player", new { avoided = true, prevention = "invulnerability" }), "player"), Is.EqualTo("defend"));
            Assert.That(CardStageReactions.KindFor(Line("combatAvoidanceResolved", "e1", "player", new { avoided = false }), "player"), Is.Null);
        }

        [Test]
        public void ABlowThatBlockTookWholeIsFullyDefendedAndOneThatHurtIsNot()
        {
            Assert.That(CardStageReactions.KindFor(Line("damageDealt", "e1", "player", new { isAttack = true, amount = 6, blocked = 6 }), "player"), Is.EqualTo("fully-defended"));
            Assert.That(CardStageReactions.KindFor(Line("damageDealt", "e1", "player", new { isAttack = true, amount = 6, blocked = 4 }), "player"), Is.Null, "some of it reached Health");
            Assert.That(CardStageReactions.KindFor(Line("damageDealt", "e1", "player", new { isAttack = true, amount = 0, blocked = 0 }), "player"), Is.Null, "nothing was dealt");
            Assert.That(CardStageReactions.KindFor(Line("damageDealt", "e1", "player", new { isAttack = true, amount = 0, blocked = 0, wardResisted = 3 }), "player"), Is.EqualTo("fully-defended"), "Ward took the whole blow");
            Assert.That(CardStageReactions.KindFor(Line("damageDealt", "e1", "player", new { isAttack = false, amount = 6, blocked = 6 }), "player"), Is.Null, "not an attack");
            Assert.That(CardStageReactions.KindFor(Line("damageDealt", "player", "player", new { isAttack = true, amount = 6, blocked = 6 }), "player"), Is.Null, "not the player's own");
            Assert.That(CardStageReactions.KindFor(Line("damageDealt", "player", "e1", new { isAttack = true, amount = 6, blocked = 6 }), "player"), Is.Null, "the target is an enemy");
        }

        [Test]
        public void AGuardBreakAfterTheLethalBlowIsTheFallAndPlaysNothing()
        {
            var lines = new[]
            {
                Line("hpLost", "e1", "player", new { amount = 9 }),
                Line("combatGuardBroken", "e1", "player"),
                Line("playerDied", null, "player"),
            };
            Assert.That(CardStageReactions.Defeated("guard-break", lines[1], lines, "player", true), Is.True);
            Assert.That(CardStageReactions.Defeated("guard-break", lines[1], lines, "player", false), Is.False, "the player lived");
            Assert.That(CardStageReactions.Defeated("evade", lines[1], lines, "player", true), Is.False);
            var before = new[] { Line("combatGuardBroken", "e1", "player"), Line("hpLost", "e1", "player", new { amount = 9 }) };
            Assert.That(CardStageReactions.Defeated("guard-break", before[0], before, "player", true), Is.False, "the break came first");
        }

        [Test]
        public void OnlyThePilotsGroupPlaysTheBodyReactionAndTheBeatWaitsForIt()
        {
            var lines = new[]
            {
                Line("enemyMoveStarted", "e1", null, new { kind = "attack", moveId = "slash" }),
                Line("attackEvaded", "e1", "player"),
            };
            var pace = Pace();
            var pilot = Core2Feedback.Plan(lines, Context(pace, true)).Single();
            var cue = pilot.Groups.Single().Reaction.Single(c => c.Type == FxCueType.Body);
            Assert.That(cue.Text, Is.EqualTo("evade"));
            Assert.That(cue.TargetId, Is.EqualTo("player"));
            var unit = pace.Sprite.LungeMs / Ui.CardStage.PilotUnits;
            Assert.That(pilot.Groups.Single().BodyMs, Is.EqualTo(3 * unit).Within(1e-9), "three units of a sixth of the lunge");
            var other = Core2Feedback.Plan(lines, Context(pace, false)).Single();
            Assert.That(other.Groups.Single().Reaction.Any(c => c.Type == FxCueType.Body), Is.False);
            Assert.That(other.Groups.Single().BodyMs, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ the timeline

        [Test]
        public void ACardPlaysItsClipAtTheLengthTheStageGivesIt()
        {
            var lines = new[]
            {
                Line("cardPlayed", null, "e1", new { cardType = "attack", cardInstanceId = "c1" }, detail: "Strike"),
                Line("damageDealt", "player", "e1", new { amount = 12, blocked = 0 }),
            };
            var pace = Pace();
            var beats = Core2Feedback.Plan(lines, Context(pace, false));
            var smash = new StageTiming { TotalMs = 200, ImpactMs = 120 };
            var schedule = FxTimeline.Build(beats, pace, Defs.Pace.InstantStepMs, _ => smash);
            var swing = schedule.Items.First(i => i.Kind == FxItemKind.Swing);
            Assert.That(swing.Ms, Is.EqualTo(200), "the clip's length, not the lunge");
            var contact = schedule.Items.First(i => i.Kind == FxItemKind.Cue && i.Cue.Type == FxCueType.Slash);
            Assert.That(contact.AtMs, Is.EqualTo(swing.AtMs + 120).Within(1e-9), "the blow lands at the clip's own frame");
            var hold = schedule.Items.First(i => i.Kind == FxItemKind.Hold);
            Assert.That(hold.AtMs, Is.EqualTo(contact.AtMs), "the figure holds where the blow lands");
            Assert.That(hold.Ms, Is.EqualTo(beats.Single().HoldMs));
            var plain = FxTimeline.Build(beats, pace, Defs.Pace.InstantStepMs);
            Assert.That(plain.Items.Any(i => i.Kind == FxItemKind.Hold), Is.False, "a figure without a stage holds nothing of its own");
            Assert.That(plain.Items.First(i => i.Kind == FxItemKind.Swing).Ms, Is.EqualTo(pace.LungeMs));
        }

        [Test]
        public void ABodyReactionLongerThanTwiceTheGapHoldsTheNextGroup()
        {
            var lines = new[]
            {
                Line("enemyMoveStarted", "e1", null, new { kind = "attack", moveId = "slash" }),
                Line("attackEvaded", "e1", "player"),
            };
            var tight = CombatPace.From(new CombatPacing { AnimSpeed = "normal", Speed = 1, AutoSpeed = 1, StageGapMs = 20, AnimationStepMs = 390 }, Defs, false);
            var beats = Core2Feedback.Plan(lines, Context(tight, true));
            var schedule = FxTimeline.Build(beats, tight, Defs.Pace.InstantStepMs);
            var numbers = beats.Single().Groups.Single();
            var reveal = schedule.Items.First(i => i.Kind == FxItemKind.Reveal);
            var life = Math.Max(tight.Sprite.ReactionMs, numbers.BodyMs);
            Assert.That(life, Is.GreaterThan(2 * tight.GapMs), "the setup outlasts its gap");
            var end = schedule.Items.First(i => i.Kind == FxItemKind.End);
            Assert.That(end.AtMs - reveal.AtMs, Is.EqualTo(life - tight.GapMs).Within(1e-9), "the wait before the end follows the reaction");
        }
    }
}
