using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-C1b (D-311): the Core 2 combat screen's logic, played the way a person presses it. A bot that touches only what the screen touches (a card, a prompt option, an enemy, End Turn,
    /// the offer's Back, the card choice's picks) plays whole fights from a hosted run; every intent it sends is one the view prebuilt and the rules accept, a refusal is the rules' own
    /// and changes nothing, the fight ends in the run (a map to go on from, or a run end), and the words every chip and prompt shows resolve in the string table. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2CombatFlowTests
    {
        private string _dir;
        private SaveService _saves;
        private static readonly DateTime Epoch = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

        private static RunContent Content => Core2HostParityTests.Content;
        private static StringTable Strings => UiData.Load(TestContent.Source).Strings;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-core2-flow-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
            // Every class open, so the bot plays all four.
            var profile = ProfileStore.Load(_saves);
            profile.Doc["gameProfileUnlocks"] = Core2HostParityTests.Unlocked(Content.Core2)["gameProfileUnlocks"];
            profile.Save(Content.ContentHash);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private sealed class Tally
        {
            public int Cards, Targets, Choices, Routes, Offers, Picks, Flasks, Predicts, Swaps, Reactions, EndTurns, Refusals, Presses;
            public readonly HashSet<string> PromptKinds = new HashSet<string>();
        }

        /// <summary>One press of the bot: an offer is answered, a card choice picked, a prompt option taken, an enemy chosen, else the first playable card, a flask, a prediction, a swap, or End Turn.</summary>
        private static bool Press(Core2CombatFlow flow, Tally tally, Random pick)
        {
            tally.Presses++;
            Core2StepResult step;
            if (flow.Prompt != null)
            {
                tally.PromptKinds.Add(flow.Prompt.Kind);
                switch (flow.Prompt.Kind)
                {
                    case Core2UiValues.PromptOffer:
                        tally.Offers++;
                        var option = flow.Prompt.Options.FirstOrDefault(o => o.Enabled && pick.Next(2) == 0);
                        step = option != null ? flow.Choose(option) : flow.DeclineOffer();
                        break;
                    case Core2UiValues.PromptCards:
                        tally.Picks++;
                        foreach (var o in flow.Prompt.Options.Take(flow.Prompt.Count)) flow.Choose(o);
                        step = flow.ConfirmPicks();
                        break;
                    default:
                        var first = flow.Prompt.Options.FirstOrDefault(o => o.Enabled);
                        if (first == null) { Assert.That(flow.Back(), Is.True); return true; }
                        if (flow.Prompt.Kind == Core2UiValues.PromptChoice) tally.Choices++;
                        if (flow.Prompt.Kind == Core2UiValues.PromptRoute) tally.Routes++;
                        step = flow.Choose(first);
                        break;
                }
                return Check(flow, step, tally);
            }
            if (flow.Targeting)
            {
                tally.Targets++;
                step = flow.PickTarget(flow.LegalTargets.First());
                return Check(flow, step, tally);
            }
            var view = flow.View;
            // Sometimes try a side control first: the reaction switch, a flask, a prediction, a swap.
            var roll = pick.Next(12);
            if (roll == 0 && view.Flasks.Any(f => f.Usable))
            {
                tally.Flasks++;
                flow.OpenFlasks();
                var flask = flow.Prompt.Options.First(o => o.Enabled);
                step = flow.Choose(flask);
                if (flow.Targeting) step = flow.PickTarget(flow.LegalTargets.First());
                return Check(flow, step, tally);
            }
            if (roll == 1 && view.Enemies.Any(e => e.Intent != null && e.Intent.Predictable))
            {
                tally.Predicts++;
                flow.OpenPredict(view.Enemies.First(e => e.Intent != null && e.Intent.Predictable).Id);
                return Check(flow, flow.Choose(flow.Prompt.Options[pick.Next(flow.Prompt.Options.Count)]), tally);
            }
            if (roll == 2 && view.Equipment != null && view.Equipment.Slots.Any(s => s.Sets.Any(x => x.Swappable)))
            {
                tally.Swaps++;
                flow.OpenEquipment();
                var swap = flow.Prompt.Options.FirstOrDefault(o => o.Set != null && o.Id != Core2UiValues.OptionChange && o.Enabled);
                if (swap == null) { flow.Back(); return true; }
                return Check(flow, flow.Choose(swap), tally);
            }
            if (roll == 3)
            {
                tally.Reactions++;
                return Check(flow, flow.ToggleReactions(), tally);
            }
            for (var i = 0; i < view.Hand.Count; i++)
            {
                if (!view.Hand[i].Playable) continue;
                tally.Cards++;
                step = flow.SelectCard(i);
                if (step.Refusal != null) { tally.Refusals++; continue; }
                return Check(flow, step, tally);
            }
            tally.EndTurns++;
            step = flow.EndTurn();
            if (step.Refusal != null) tally.Refusals++;
            return Check(flow, step, tally);
        }

        private static bool Check(Core2CombatFlow flow, Core2StepResult step, Tally tally)
        {
            if (step.Ran) Assert.That(step.Accepted, Is.True, "an intent the view built is accepted: " + step.Refusal);
            else if (step.Refusal != null) tally.Refusals++;
            return true;
        }

        private static void Play(Core2RunSession session, Core2CombatFlow flow, Tally tally, int seed)
        {
            var pick = new Random(seed);
            for (var guard = 0; guard < 250 && !flow.Ended; guard++) Press(flow, tally, pick);
            Assert.That(flow.Ended, Is.True, "the bot finished the fight through the screen's own controls; mode " + flow.Mode + ", prompt " + flow.Prompt?.Kind + ", presses " + tally.Presses + ", refusals " + tally.Refusals);
            Assert.That(flow.Mode, Is.EqualTo(UiValues.FocusEnded));
        }

        public static IEnumerable<TestCaseData> Fights()
        {
            var seeds = new uint[] { 11, 4242 };
            foreach (var classId in new[] { "reaver", "starseer", "rogue", "herald" })
                foreach (var seed in seeds)
                    yield return new TestCaseData(classId, seed).SetName("Core2Flow_" + classId + "_" + seed);
        }

        [TestCaseSource(nameof(Fights))]
        public void ABotPlaysAWholeFightThroughTheScreensControls(string classId, uint seed)
        {
            var session = Core2RunSession.New(Content, _saves, 1, seed, classId, "Aldric", () => Epoch);
            // A sturdy run so the fight is long enough to meet its prompts, then a plain one below.
            session.EditRunForReview(r => { r["maxHp"] = 400; r["hp"] = 400; });
            Assert.That(session.Travel(session.ReachableNodes()[0]).Door.IsFight, Is.True);
            var strings = Strings;
            var flow = new Core2CombatFlow(session, strings);
            var tally = new Tally();
            Play(session, flow, tally, (int)seed);
            Assert.That(tally.Cards + tally.EndTurns, Is.GreaterThan(0));
            Assert.That(session.FightFinished, Is.True, "the fight was written back to the run");
            Assert.That(session.LastEnd.Outcome, Is.EqualTo(Ashen.Generated.CombatValues.Victory).Or.EqualTo(Ashen.Generated.CombatValues.Defeat));
            // The words the end of the fight shows resolve.
            foreach (var key in new[] { StringKeys.Core2Staggered, StringKeys.Core2IntentUnknown, StringKeys.Core2ReactionsOn })
                Assert.That(strings.Has(key), Is.True, key);
            TestContext.WriteLine(classId + " " + seed + ": " + tally.Presses + " presses, " + string.Join(",", tally.PromptKinds));
        }

        [Test]
        public void EveryTextTheScreenShowsResolvesAndNoRefusedPressChangesTheFight()
        {
            var session = Core2RunSession.New(Content, _saves, 1, 31415, "reaver", "Aldric", () => Epoch);
            session.EditRunForReview(r => { r["maxHp"] = 300; r["hp"] = 300; });
            session.Travel(session.ReachableNodes()[0]);
            var strings = Strings;
            var flow = new Core2CombatFlow(session, strings);
            var view = flow.View;
            // The fighters' chips.
            foreach (var f in new[] { view.Player }.Concat(view.Enemies))
            {
                Assert.That(Core2CombatText.Stance(strings, f) == null, Is.EqualTo(f.StanceName == null));
                Assert.That(Core2CombatText.Statuses(strings, f, 4, new[] { "evade", "precision" }).All(s => !string.IsNullOrEmpty(s)), Is.True);
                Assert.That(Core2CombatText.Stacks(strings, f).All(s => !string.IsNullOrEmpty(s)), Is.True);
            }
            foreach (var enemy in view.Enemies)
            {
                var text = Core2CombatText.Intent(strings, enemy.Intent);
                Assert.That(text, Is.Not.Empty);
                if (!enemy.Intent.Known) Assert.That(text, Does.Not.Contain(enemy.Intent.MoveId ?? "-"), "an unread move shows only its knowledge label");
            }
            // A card: its rank, traits, hit badge and receipt on a target.
            foreach (var card in view.Hand)
            {
                foreach (var hit in card.Hits.Values)
                {
                    var badge = Core2CombatText.Hit(strings, hit);
                    Assert.That(badge, Is.Not.Null);
                    Core2CombatText.Receipt(strings, hit);
                }
            }
            // A refused press (a card the hand cannot afford) is the rules' words and changes nothing.
            var hash = session.StateHash();
            for (var i = 0; i < view.Hand.Count; i++)
            {
                if (view.Hand[i].Playable) continue;
                var refused = flow.SelectCard(i);
                Assert.That(refused.Refusal, Is.Not.Empty);
                Assert.That(refused.Ran, Is.False);
            }
            Assert.That(session.StateHash(), Is.EqualTo(hash));
            // End Turn works and the log lines resolve.
            var step = flow.EndTurn();
            Assert.That(step.Accepted, Is.True);
            Assert.That(step.Timeline, Is.Not.Empty);
            Assert.That(step.Timeline.All(l => l.Text == null || l.Text.Length > 0), Is.True);
        }

        [Test]
        public void ACardThatAsksForAStanceThenATargetIsPlayedStepByStepWithTheViewsIntents()
        {
            var session = Core2RunSession.New(Content, _saves, 1, 5, "reaver", "Aldric", () => Epoch);
            session.EditRunForReview(r => { r["maxHp"] = 300; r["hp"] = 300; });
            session.Travel(session.ReachableNodes()[0]);
            // A stance card in the hand (the profile's card that lets the player pick the maneuver it enters).
            var hand = session.Combat.State.Piles.Hand;
            hand.Insert(0, new JObject { ["instanceId"] = "t-stance", ["cardId"] = "base.immediate-stance-transition", ["upgraded"] = false });
            var flow = new Core2CombatFlow(session, Strings);
            var i = flow.View.Hand.FindIndex(c => c.InstanceId == "t-stance");
            var card = flow.View.Hand[i];
            Assert.That(card.Choices.Count, Is.GreaterThan(1), "the card offers stances");
            Assert.That(card.Playable, Is.True, card.Refusal);
            var step = flow.SelectCard(i);
            Assert.That(step.Ran, Is.False);
            Assert.That(flow.Prompt?.Kind, Is.EqualTo(Core2UiValues.PromptChoice), "a card with several stances asks which");
            Assert.That(flow.Mode, Is.EqualTo(Core2UiValues.FocusPrompt));
            Assert.That(flow.Prompt.Options.Select(o => o.Id), Is.EqualTo(card.Choices));
            Assert.That(flow.Prompt.Options.All(o => !string.IsNullOrEmpty(o.Label)), Is.True, "each stance has a name");
            Assert.That(flow.Back(), Is.True, "Back puts the card down");
            Assert.That(flow.Armed, Is.Null);
            Assert.That(flow.Mode, Is.Null);
            flow.SelectCard(i);
            var chosen = flow.Prompt.Options.First(o => o.Enabled);
            step = flow.Choose(chosen);
            if (flow.Targeting)
            {
                Assert.That(flow.Mode, Is.EqualTo(UiValues.FocusTargeting));
                Assert.That(flow.LegalTargets, Is.Not.Empty);
                step = flow.PickTarget(flow.LegalTargets.First());
            }
            for (var guard = 0; flow.Prompt != null && !step.Ran && guard < 4; guard++) step = flow.Choose(flow.Prompt.Options.First(o => o.Enabled));
            Assert.That(step.Ran, Is.True);
            Assert.That(step.Accepted, Is.True, step.Refusal);
            Assert.That(flow.View.Player.StanceManeuver, Is.EqualTo(chosen.Id), "the stance the player picked is the one entered");
        }

        [Test]
        public void AReactionOfferIsAModalWithItsOptionsAndBackAndTheFightWaitsOnIt()
        {
            // End the turn with the opening hand: a Defend in it (a reaction card) makes the first incoming attack ask.
            for (uint seed = 1; seed < 24; seed++)
            {
                var session = Core2RunSession.New(Content, _saves, 1, seed, "reaver", "Aldric", () => Epoch);
                session.EditRunForReview(r => { r["maxHp"] = 400; r["hp"] = 400; });
                session.Travel(session.ReachableNodes()[0]);
                var flow = new Core2CombatFlow(session, Strings);
                for (var turn = 0; turn < 3 && !flow.Ended && flow.Prompt == null; turn++) flow.EndTurn();
                if (flow.Prompt?.Kind != Core2UiValues.PromptOffer) continue;
                Assert.That(flow.Mode, Is.EqualTo(Core2UiValues.FocusPrompt));
                Assert.That(flow.Prompt.Required, Is.True);
                Assert.That(flow.Back(), Is.False, "an offer is declined with its own Back, not dismissed");
                var hash = session.StateHash();
                Assert.That(flow.EndTurn().Ran, Is.False, "nothing else can be pressed while the fight waits");
                Assert.That(flow.SelectCard(0).Ran, Is.False);
                Assert.That(session.StateHash(), Is.EqualTo(hash));
                Assert.That(flow.Prompt.Options.All(o => o.Offer != null), Is.True);
                Assert.That(Core2CombatText.OfferOption(Strings, flow.Prompt.Options[0].Offer), Is.Not.Empty);
                var step = flow.DeclineOffer();
                Assert.That(step.Accepted, Is.True, step.Refusal);
                return;
            }
            Assert.Inconclusive("no reaction offer came up in the first fights");
        }

        [Test]
        public void TheFlowRefusesAnIllegalTargetAndKeepsTheCardArmed()
        {
            var session = Core2RunSession.New(Content, _saves, 1, 12, "reaver", "Aldric", () => Epoch);
            session.Travel(session.ReachableNodes()[0]);
            var flow = new Core2CombatFlow(session, Strings);
            var i = flow.View.Hand.FindIndex(c => c.Playable && c.NeedsTarget && c.Choices.Count == 0);
            if (i < 0) Assert.Inconclusive("no plain targeted card in the opening hand");
            flow.SelectCard(i);
            Assert.That(flow.Targeting, Is.True);
            var hash = session.StateHash();
            var step = flow.PickTarget("not-an-enemy");
            Assert.That(step.Refusal, Is.Not.Empty);
            Assert.That(flow.Targeting, Is.True, "the card stays armed");
            Assert.That(session.StateHash(), Is.EqualTo(hash));
        }

        [Test]
        public void AnIntentThatThrowsIsReportedAndTheViewIsRebuiltInsteadOfLeavingTheScreenFrozen()
        {
            var session = Core2RunSession.New(Content, _saves, 1, 12, "reaver", "Aldric", () => Epoch);
            session.EditRunForReview(r => { r["maxHp"] = 5000; r["hp"] = 5000; });
            session.Travel(session.ReachableNodes()[0]);
            var strings = Strings;
            var flow = new Core2CombatFlow(session, strings);
            var endTurn = flow.View.EndTurn;
            var tally = new Tally();
            var pick = new Random(1);
            for (var guard = 0; guard < 400 && !flow.Ended; guard++) Press(flow, tally, pick);
            Assert.That(flow.Ended, Is.True);
            // The fight is over: the session throws (it is not in combat). The flow answers with the error and a message, never an exception.
            var step = flow.Execute(endTurn);
            Assert.That(step.Error, Is.Not.Null, "the exception is handed to the screen to log");
            Assert.That(step.Refusal, Is.EqualTo(strings.Get(StringKeys.Core2ErrorUnexpected)), "the player is told");
            Assert.That(step.Accepted, Is.False);
            Assert.That(flow.View, Is.Not.Null);
            Assert.That(flow.Prompt, Is.Null);
            Assert.That(flow.Ended, Is.True, "the view is rebuilt from the fight as it stands");
        }
    }
}
