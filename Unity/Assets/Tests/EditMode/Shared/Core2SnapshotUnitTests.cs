using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-13 (D-280, D-282): what the snapshot refuses, and the shapes the recorded fights do not reach. A stored fight that is malformed is refused by name
    /// (combatSnapshotProblems); a fight saved under another profile is refused as its own recoverable case; a card choice left open and a fight resolving
    /// are saved or refused as the web does. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2SnapshotUnitTests
    {
        private static CoreCombatData Data => Core2DamageParityTests.Data;

        private static CombatState Fight(params string[] hand)
        {
            var spec = (JObject)((JArray)Core2ReactionParityTests.Read("fights-reaver.json")["fights"])[0].DeepClone();
            if (hand.Length > 0)
            {
                spec.Remove("deckAdd");
                spec["hand"] = new JArray(hand);
            }
            return Core2ReactionParityTests.Open(spec);
        }

        private static JObject Intent(string type, params object[] pairs)
        {
            var o = new JObject { ["type"] = type };
            for (var i = 0; i + 1 < pairs.Length; i += 2) o[(string)pairs[i]] = JToken.FromObject(pairs[i + 1] ?? JValue.CreateNull());
            return o;
        }

        private static string[] Problems(JObject snapshot) => CoreSnapshotProblems.Check(Data, snapshot).ToArray();

        // ------------------------------------------------------------------ a sound snapshot

        [Test]
        public void ASoundSnapshotHasNoProblemsAndRestoresToTheSameSnapshot()
        {
            var c = Fight();
            var snapshot = CoreSnapshot.Serialize(c);
            Assert.That(Problems(snapshot), Is.Empty);
            var again = CoreSnapshot.Serialize(Core2SnapshotParityTests.RoundTrip(c));
            Assert.That(JToken.DeepEquals(snapshot, again), Is.True, "serialize . restore . serialize is the snapshot");
            Assert.That(snapshot["gameProfile"].Value<string>("hash"), Is.EqualTo(Data.Run.ProfileHash));
            Assert.That(snapshot["player"]["energy"], Is.EqualTo(snapshot["player"]["stamina"]), "the pool rides under the web's energy names too");
            Assert.That(snapshot["abilityQueue"], Is.Null, "a resting fight carries no queue");
        }

        [Test]
        public void AFightStillResolvingOrHoldingQueuedWorkWithoutAPauseIsNotSaved()
        {
            var c = Fight();
            c.Buffer = new List<JObject>();
            Assert.Throws<InvalidOperationException>(() => CoreSnapshot.Serialize(c));
            c.Buffer = null;
            c.Queue.AddLast(new CombatAction { Effect = new JObject { ["op"] = "block" }, Source = c.Player, Owner = c.Player, Target = c.Player, Meta = ActionMeta.Empty() });
            Assert.Throws<InvalidOperationException>(() => CoreSnapshot.Serialize(c));
            Assert.Throws<ArgumentException>(() => CoreSnapshot.Serialize(new CombatState()), "only a Core 2 fight is saved by this door");
        }

        // ------------------------------------------------------------------ refusals

        public static IEnumerable<TestCaseData> Refusals()
        {
            yield return new TestCaseData("version", (Action<JObject>)(s => s["version"] = 99), "version");
            yield return new TestCaseData("turn", (Action<JObject>)(s => s["turn"] = 0), "turn must be");
            yield return new TestCaseData("phase", (Action<JObject>)(s => s["phase"] = "ended"), "phase/result");
            yield return new TestCaseData("result", (Action<JObject>)(s => s["result"] = "draw"), "result must be");
            yield return new TestCaseData("emitDepth", (Action<JObject>)(s => s["emitDepth"] = 1), "emitDepth");
            yield return new TestCaseData("idCounter", (Action<JObject>)(s => s["idCounter"] = -1), "idCounter");
            yield return new TestCaseData("queue", (Action<JObject>)(s => s["abilityQueue"] = new JArray()), "requires a pending choice");
            yield return new TestCaseData("rules", (Action<JObject>)(s => { s.Remove("reactionRulesVersion"); s["pendingReaction"] = new JObject(); }), "Paused reaction requires");
            yield return new TestCaseData("cursor", (Action<JObject>)(s => s["reactionCursor"] = new JObject { ["index"] = 0, ["stage"] = "start" }), "cursor");
            yield return new TestCaseData("hp", (Action<JObject>)(s => s["player"]["hp"] = 1e6), "player.hp");
            yield return new TestCaseData("kind", (Action<JObject>)(s => s["enemies"][0]["kind"] = "player"), "enemies[0].kind");
            yield return new TestCaseData("alive", (Action<JObject>)(s => s["enemies"][0]["alive"] = "yes"), "enemies[0].alive");
            yield return new TestCaseData("duplicate", (Action<JObject>)(s => s["piles"]["discard"] = new JArray(s["piles"]["hand"][0].DeepClone())), "more than one pile position");
            yield return new TestCaseData("hidden", (Action<JObject>)(s => s["hiddenCards"] = new JArray(new JObject { ["returnTurn"] = 0, ["card"] = new JObject() })), "hiddenCards");
            yield return new TestCaseData("engagement", (Action<JObject>)(s => ((JObject)s["rangeState"]["engagements"]).Properties().First().Remove()), "Missing engagement pair");
            yield return new TestCaseData("actor", (Action<JObject>)(s => s["player"]["rangeActorKey"] = "player:other"), "range actor identity");
            yield return new TestCaseData("derived", (Action<JObject>)(s => s["derivedStatRuleSnapshot"]["snapshotVersion"] = 1), "snapshotVersion");
            yield return new TestCaseData("ruleset", (Action<JObject>)(s => s["derivedStatRuleSnapshot"]["rules"]["rulesetVersion"] = 1), "rulesetVersion");
            yield return new TestCaseData("row", (Action<JObject>)(s => ((JObject)s["derivedStatRuleSnapshot"]["rules"]["rules"]).Properties().First().Value["base"] = "x"), "base");
            yield return new TestCaseData("modifiers", (Action<JObject>)(s => s["derivedStatRuleSnapshot"].Value<JObject>().Remove("relicModifiers")), "relicModifiers");
            yield return new TestCaseData("knowledge", (Action<JObject>)(s => s["enemyKnowledge"]["version"] = 2), "enemyKnowledge");
            yield return new TestCaseData("ward", (Action<JObject>)(s => s["player"]["persistentWard"] = new JObject { ["max"] = 1, ["value"] = 5 }), "persistentWard");
            yield return new TestCaseData("statusControl", (Action<JObject>)(s => s["enemies"][0]["statusControl"] = null), "statusControl");
            yield return new TestCaseData("tier", (Action<JObject>)(s => s["itemUpgradeLevels"] = new JObject { ["spoon/x"] = 1 }), "itemUpgradeLevels");
            yield return new TestCaseData("trigger", (Action<JObject>)(s => s["triggerState"] = new JArray(new JArray("only-a-key"))), "triggerState");
            yield return new TestCaseData("pin", (Action<JObject>)(s => s["gameProfile"] = new JObject()), "gameProfile");
            yield return new TestCaseData("formation", (Action<JObject>)(s => s["player"].Value<JObject>().Remove("formationCell")), "formation cell");
        }

        [TestCaseSource(nameof(Refusals))]
        public void AMalformedSnapshotIsRefusedByName(string label, Action<JObject> damage, string expected)
        {
            var snapshot = CoreSnapshot.Serialize(Fight());
            damage(snapshot);
            var problems = Problems(snapshot);
            Assert.That(problems, Is.Not.Empty, label);
            Assert.That(string.Join("; ", problems), Does.Contain(expected), label);
            var refused = Assert.Throws<SnapshotRefusedException>(() => CoreSnapshot.Restore(Data, new Rng(1), snapshot), label);
            Assert.That(refused.ProfileMismatch, Is.False);
            Assert.That(refused.Problems, Is.Not.Empty);
        }

        [Test]
        public void AFightSavedUnderAnotherProfileIsItsOwnRecoverableRefusal()
        {
            var snapshot = CoreSnapshot.Serialize(Fight());
            snapshot["gameProfile"]["hash"] = "sha256:another";
            var refused = Assert.Throws<SnapshotRefusedException>(() => CoreSnapshot.Restore(Data, new Rng(1), snapshot));
            Assert.That(refused.ProfileMismatch, Is.True);
            Assert.That(refused.Message, Does.Contain("sha256:another"));
        }

        [Test]
        public void ASaveNamingAStreamThisBuildDoesNotHaveIsRefused()
        {
            var counters = CoreSnapshot.RngToJson(new Rng(5));
            Assert.That(counters.Properties().Select(p => p.Name), Does.Contain("enemyReaction").And.Contain("shuffle"), "the 405 streams and the Core 2 streams");
            counters["spoon"] = 3;
            Assert.Throws<SnapshotRefusedException>(() => CoreSnapshot.RngFromJson(5, counters));
        }

        [Test]
        public void TheRandomCountersOfEveryStreamSurviveTheSave()
        {
            var rng = new Rng(77);
            rng.Int(Ashen.Generated.RngStream.Shuffle, 0, 9);
            rng.Int(Ashen.Generated.RngStream.Shuffle, 0, 9);
            rng.Float(Ashen.Generated.Core2RngStream.EnemyReaction);
            rng.Float(Ashen.Generated.Core2RngStream.CombatAvoidance);
            var back = CoreSnapshot.RngFromJson(77, CoreSnapshot.Doubles((JObject)JsonContent.Parse(CoreSnapshot.RngToJson(rng).ToString(Formatting.None))));
            Assert.That(back.AllCounters(), Is.EquivalentTo(rng.AllCounters()));
            Assert.That(back.Float(Ashen.Generated.Core2RngStream.EnemyReaction), Is.EqualTo(rng.Float(Ashen.Generated.Core2RngStream.EnemyReaction)));
        }

        // ------------------------------------------------------------------ a card choice left open

        [Test]
        public void ACardChoiceLeftOpenIsSavedWithItsPaidCardAndTheFightContinuesFromIt()
        {
            var live = Fight("base.exhaust-choose", "core.strike", "core.defend");
            var held = live.Piles.Hand.First(card => card.Value<string>("cardId") == "base.exhaust-choose").Value<string>("instanceId");
            CoreDispatch.Dispatch(live, Intent("playCard", "cardInstanceId", held));
            Assert.That(CoreCardZones.HasChoice(live), Is.True, "the card left a choice open");
            var snapshot = CoreSnapshot.Serialize(live);
            Assert.That(snapshot["pendingCardChoice"], Is.Not.Null);
            Assert.That(snapshot["pendingAbilityPlay"]["instance"].Value<string>("instanceId"), Is.EqualTo(held));
            Assert.That(snapshot["abilityQueue"], Is.Not.Null);
            Assert.That(Problems(snapshot), Is.Empty);
            var restored = Core2SnapshotParityTests.RoundTrip(live);
            Assert.That(CoreCardZones.HasChoice(restored), Is.True);
            Assert.That(restored.PendingAbilityPlay.Value<string>("instanceId"), Is.EqualTo(held));
            Assert.That(restored.PendingPlayInstance, Is.Not.Null);
            Assert.That(restored.PendingPlayRef, Is.Not.Null);
            // Only the answer is taken while the choice waits, on both.
            foreach (var fight in new[] { live, restored })
                Assert.Throws<InvalidOperationException>(() => CoreDispatch.Dispatch(fight, Intent("endTurn", "discardIds", new string[0])));
            var pick = live.Fields["pendingCardChoice"]["candidateIds"][0].Value<string>();
            foreach (var fight in new[] { live, restored }) CoreDispatch.Dispatch(fight, Intent("chooseCards", "cardInstanceIds", new[] { pick }));
            Core2CombatStartParityTests.Same("after the answer", Core2CombatStartParityTests.Project(live), Core2CombatStartParityTests.Project(restored));
            Assert.That(CoreCardZones.HasChoice(restored), Is.False);
            Assert.That(restored.Piles.Exhaust.Select(card => card.Value<string>("instanceId")), Does.Contain(pick));
            Assert.That(JToken.DeepEquals(CoreSnapshot.Serialize(live), CoreSnapshot.Serialize(restored)), Is.True);
        }
    }
}
