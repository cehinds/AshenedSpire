using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// W-08 through the run session (US-11.1 to US-11.3; PF-06 "reward claimed" commit point; AF-10 resume at the
    /// rewards): a won fight is checkpointed at its pending reward and resumes there; the door grants the cinders on
    /// arrival; a claim, a skip and Continue are each saved, survive a reload and are never applied twice; Continue
    /// sweeps under 'auto' and leaves the rest under 'manual' (the profile's setting over the preset's default); the
    /// post-reward save resumes at the map location; a death closes the run out into the profile and clears the slot. The
    /// W-08 view reads every recorded reward offer with resolved titles.
    /// </summary>
    [TestFixture]
    public class RewardsSessionTests
    {
        /// <summary>The first fight the bot wins under the default preset with a card offer and a flask (the PlayMode smoke seed).</summary>
        private const uint WinSeed = 20260927u;

        private static RunContent _default;
        private static RunContent _shipped;
        private static UiData _ui;
        private string _dir;
        private SaveService _saves;

        private static RunContent Default => _default ??= RunContent.Load(TestContent.Source);
        private static RunContent Shipped => _shipped ??= RunContent.Load(TestContent.Source, "shipped");
        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-rewards-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private static CombatCommand NextCommand(CombatState state)
        {
            var view = CombatViewModel.Build(state);
            var card = view.Hand.FirstOrDefault(c => c.Playable);
            if (card != null) return CombatCommand.PlayCard(card.InstanceId, card.Targets.FirstOrDefault());
            return EndTurn(state);
        }

        private static CombatCommand EndTurn(CombatState state)
        {
            var plan = HandRules.Plan(state);
            return CombatCommand.EndTurn(plan.Cards.Take((int)plan.Minimum).Select(c => c.Value<string>("instanceId")));
        }

        private RunSession Won(uint seed = WinSeed, RunContent content = null)
        {
            content ??= Default;
            var session = RunSession.New(content, _saves, 1, seed, content.DefaultClass(), "Aldric");
            session.StartEncounter();
            for (var i = 0; i < 400 && session.IsInCombat; i++) session.Execute(NextCommand(session.Combat.State));
            Assert.That(session.FightOutcome, Is.EqualTo("reward"), "seed " + seed + " wins the first fight");
            return session;
        }

        private RunSession Reload() => RunSession.Load(Default, _saves, 1).Session;

        private static List<string> States(RunSession s) => RewardsView.Build(s, Ui).Rows.Select(r => r.Key + "=" + r.State).ToList();

        [Test]
        public void TheSmokeSeedWinsWithACardOfferAndAFlask()
        {
            var session = Won();
            var rewards = session.Run["pendingReward"]["rewards"];
            Assert.That(((JArray)rewards["cardIds"]).Count, Is.EqualTo(3), "a card offer of 3");
            Assert.That(rewards.Value<string>("flaskId"), Is.Not.Null, "a flask (the smoke test's second claim)");
        }

        [Test]
        public void AWonFightResumesAtItsRewardsAndTheCindersAreGrantedOnArrivalOnce()
        {
            var session = Won();
            Assert.That(session.Location, Is.EqualTo(RunFlowValues.LocationRewards));
            var cinders = session.Run.Value<double>("cinders");
            var offered = session.Run["pendingReward"]["rewards"].Value<double>("cinders");
            var resumed = Reload();
            Assert.That(resumed.HasPendingReward && !resumed.IsInCombat, Is.True, "Continue lands on W-08");
            var view = RewardsView.Build(resumed, Ui);
            Assert.That(view.Rows.First(r => r.Kind == "cinders").State, Is.EqualTo("taken"), "granted on arrival");
            Assert.That(resumed.Run.Value<double>("cinders"), Is.EqualTo(cinders + offered));
            var again = Reload();
            RewardsView.Build(again, Ui);
            Assert.That(again.Run.Value<double>("cinders"), Is.EqualTo(cinders + offered), "a second arrival grants nothing");
            Assert.That(view.StatusText, Is.EqualTo(Ui.Strings.Format(StringKeys.RewardsStatus, new StringArgs().Add(UiPlaceholders.Claimed, view.Claimed).Add(UiPlaceholders.Total, view.Total))));
        }

        [Test]
        public void ClaimsSkipsAndRefusalsAreSavedAndNeverAppliedTwice()
        {
            var session = Won();
            var view = RewardsView.Build(session, Ui);
            var card = view.Rows.First(r => r.Kind == "card");
            Assert.That(session.ClaimReward(card.Key).Refusal, Is.EqualTo(RunFlowValues.RefusalNoPick));
            Assert.That(session.ClaimReward(card.Key, "notOnOffer").Refusal, Is.EqualTo(RunFlowValues.RefusalNotOffered));
            var pick = card.Picks[1].Id;
            var deck = session.Run["deck"].Count();
            Assert.That(session.ClaimReward(card.Key, pick).Landed, Is.True);
            Assert.That(session.Run["deck"].Count(), Is.EqualTo(deck + 1));
            Assert.That(session.Run["deck"].Last().Value<string>("cardId"), Is.EqualTo(pick));
            Assert.That(session.ClaimReward(card.Key, card.Picks[0].Id).Refusal, Is.EqualTo(RunFlowValues.RefusalClaimed));
            var flask = view.Rows.First(r => r.Kind == "flask");
            Assert.That(session.SkipReward(flask.Key).Landed, Is.True);
            Assert.That(session.ClaimReward(flask.Key).Refusal, Is.EqualTo(RunFlowValues.RefusalClaimed), "a skipped row is resolved");
            var states = States(session);
            var resumed = Reload();
            Assert.That(States(resumed), Is.EqualTo(states), "the claim state survives a reload (US-11.3)");
            Assert.That(resumed.Run["deck"].Count(), Is.EqualTo(deck + 1), "the card is not granted twice");
            Assert.That(resumed.ClaimReward(card.Key, pick).Refusal, Is.EqualTo(RunFlowValues.RefusalClaimed));
        }

        [Test]
        public void ContinueUnderManualLeavesTheRestAndResumesAtTheMap()
        {
            var session = Won();
            Assert.That(session.RewardCollectMode, Is.EqualTo("manual"), "the default preset's player setting");
            var deck = session.Run["deck"].Count();
            var flasks = session.Run["flasks"].Count();
            Assert.That(session.FinishRewards(), Is.EqualTo("map"));
            Assert.That(session.HasPendingReward, Is.False);
            Assert.That(session.Run["deck"].Count(), Is.EqualTo(deck), "manual leaves the card offer");
            Assert.That(session.Run["flasks"].Count(), Is.EqualTo(flasks), "and the flask");
            var resumed = Reload();
            Assert.That(resumed.Location, Is.EqualTo(RunFlowValues.LocationMap), "the post-reward checkpoint");
            Assert.That(resumed.HasPendingReward || resumed.IsInCombat, Is.False);
            Assert.That(resumed.After, Is.EqualTo("map"));
        }

        [Test]
        public void ContinueUnderAutoTakesEveryUnskippedRow()
        {
            var session = Won();
            session.Profile.Doc["settings"] = new JObject { ["rewardCollect"] = "auto" };
            Assert.That(session.RewardCollectMode, Is.EqualTo("auto"), "the profile's setting wins over the preset");
            var deck = session.Run["deck"].Count();
            var flasks = session.Run["flasks"].Count();
            session.FinishRewards();
            Assert.That(session.Run["deck"].Count(), Is.EqualTo(deck + 1), "the card offer is picked");
            Assert.That(session.Run["flasks"].Count(), Is.EqualTo(flasks + 1), "the flask is taken");
        }

        [Test]
        public void ADeathClosesTheRunOutIntoTheProfileAndClearsTheSlot()
        {
            var session = RunSession.New(Default, _saves, 1, WinSeed, Default.DefaultClass(), "Aldric");
            session.StartEncounter();
            for (var i = 0; i < 400 && session.IsInCombat; i++) session.Execute(EndTurn(session.Combat.State));
            Assert.That(session.FightOutcome, Is.EqualTo("defeat"), "ending every turn loses");
            Assert.That(session.RunOver, Is.True);
            Assert.That(_saves.Exists(session.Slot), Is.False, "permadeath: saves.clearRun");
            var profile = ProfileStore.Load(_saves);
            Assert.That(((JArray)profile.Doc["results"]).Count, Is.EqualTo(1), "finishRun recorded the run");
            Assert.That(RunSession.Load(Default, _saves, 1).Status, Is.EqualTo(RunLoadStatus.Empty));
        }

        [Test]
        public void TheViewReadsEveryRecordedOfferWithResolvedTitles()
        {
            var dir = Path.Combine(TestContent.OracleRoot, "rewards");
            var kinds = new HashSet<string>();
            var checkedOffers = 0;
            foreach (var file in Directory.GetFiles(dir, "case-*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                JObject log;
                using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(file))) { FloatParseHandling = FloatParseHandling.Double, DateParseHandling = DateParseHandling.None })
                    log = JObject.Load(reader);
                if (log["expected"]["receipt"].Value<string>("outcome") != "reward") continue;
                var payload = new JObject
                {
                    ["formatVersion"] = Shipped.Flow.SaveFormat, ["presetId"] = "shipped", ["summary"] = new JObject(), ["name"] = "Oracle",
                    ["portrait"] = "class.reaver.sprite.ember", ["playtimeSeconds"] = 0, ["run"] = log["expected"]["run"].DeepClone(), ["location"] = "rewards",
                };
                _saves.Checkpoint(_saves.Rules.RunSlotName(2), payload, Shipped.ContentHash, "oracle");
                var session = RunSession.Load(Shipped, _saves, 2).Session;
                var view = RewardsView.Build(session, Ui);
                Assert.That(view.Rows.Count, Is.EqualTo(view.Total), file);
                foreach (var row in view.Rows)
                {
                    kinds.Add(row.Kind);
                    Assert.That(row.Title, Is.Not.Empty.And.Not.Contains("{"), Path.GetFileName(file) + " " + row.Key + " title");
                    Assert.That(row.StateText, Is.Not.Empty.And.Not.Contains("{"), Path.GetFileName(file) + " " + row.Key + " state");
                    if (row.Kind == "card" || row.Kind == "skillDraft" || row.Kind == "classDraft")
                    {
                        Assert.That(row.Picks.Count, Is.GreaterThan(0), row.Key);
                        Assert.That(row.Picks.All(p => !string.IsNullOrEmpty(p.Name) && !p.Name.Contains("{")), Is.True, Path.GetFileName(file) + " " + row.Key + " picks");
                    }
                }
                checkedOffers++;
            }
            Assert.That(checkedOffers, Is.GreaterThanOrEqualTo(100));
            foreach (var kind in new[] { "cinders", "smithingStone", "classDraft", "skillDraft", "card", "flask", "armament", "relic" })
                Assert.That(kinds, Does.Contain(kind), kind);
        }

        [Test, Explicit("seed search for the smoke test and captures")]
        public void FindSeeds()
        {
            for (uint seed = WinSeed; seed < WinSeed + 200; seed++)
            {
                var session = RunSession.New(Default, _saves, 1, seed, Default.DefaultClass(), "Aldric");
                session.StartEncounter();
                for (var i = 0; i < 400 && session.IsInCombat; i++) session.Execute(NextCommand(session.Combat.State));
                if (session.FightOutcome != "reward") continue;
                var rewards = session.Run["pendingReward"]["rewards"];
                TestContext.WriteLine(seed + " flask=" + rewards.Value<string>("flaskId") + " cards=" + rewards["cardIds"]?.Count() + " stone=" + rewards["smithingStoneReceipt"]?.Value<double?>("amount"));
            }
        }
    }
}
