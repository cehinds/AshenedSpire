using System;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-10 (D-250): the parts of the reaction stack the recorded fights cannot reach on their own. The switch and the answer refuse what the web
    /// refuses with the web's message; a player-card root that waits on its reaction window keeps its play aside and gets it back; a fight with
    /// enemy reaction moves refuses by name only when an enemy would react. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2ReactionUnitTests
    {
        private static JObject Start => (JObject)((JArray)Core2ReactionParityTests.Read("fights-herald.json")["fights"])[0];

        private static CombatState Open() => Core2ReactionParityTests.Open(Start);

        private static JObject Intent(string type, params object[] pairs)
        {
            var o = new JObject { ["type"] = type };
            for (var i = 0; i < pairs.Length; i += 2) o[(string)pairs[i]] = pairs[i + 1] == null ? JValue.CreateNull() : JToken.FromObject(pairs[i + 1]);
            return o;
        }

        [Test]
        public void TheSwitchTakesABooleanOnThePlayersTurn()
        {
            var c = Open();
            CoreDispatch.Dispatch(c, Intent("setReactions", "enabled", false));
            Assert.That(c.Player.Value<bool>("reactionsEnabled"), Is.False);
            CoreDispatch.Dispatch(c, Intent("setReactions", "enabled", true));
            Assert.That(c.Player.Value<bool>("reactionsEnabled"), Is.True);
            var notBoolean = Assert.Throws<InvalidOperationException>(() => CoreDispatch.Dispatch(c, new JObject { ["type"] = "setReactions", ["enabled"] = "yes" }));
            Assert.That(notBoolean.Message, Is.EqualTo("Reaction preference must be enabled or disabled"));
            c.Phase = "enemy";
            var offTurn = Assert.Throws<InvalidOperationException>(() => CoreDispatch.Dispatch(c, Intent("setReactions", "enabled", false)));
            Assert.That(offTurn.Message, Is.EqualTo("Change reactions between actions on the player turn"));
        }

        [Test]
        public void AnAnswerWithoutAnOfferIsRefused()
        {
            var c = Open();
            var refused = Assert.Throws<InvalidOperationException>(() => CoreDispatch.Dispatch(c, Intent("chooseReaction", "offerId", "none", "optionId", null)));
            Assert.That(refused.Message, Is.EqualTo("This reaction choice is unavailable"));
        }

        [Test]
        public void AnIntentWhileAnOfferWaitsIsRefusedExceptItsAnswer()
        {
            var c = Open();
            c.Fields["pendingReaction"] = new JObject { ["id"] = "offer", ["groupKey"] = "none", ["ownerId"] = "player", ["sourceId"] = "e1", ["options"] = new JArray() };
            var refused = Assert.Throws<InvalidOperationException>(() => CoreDispatch.Dispatch(c, Intent("endTurn", "discardIds", new string[0])));
            Assert.That(refused.Message, Is.EqualTo("Answer the pending reaction first"));
            Assert.That(CoreReactions.Paused(c), Is.True);
        }

        [Test]
        public void ADeferredRootPlayWaitsAsideAndComesBack()
        {
            var c = Open();
            var group = new ExpansionGroup { Key = "root", SourceId = "player", Carrier = new JObject { ["instanceId"] = "c1" } };
            c.Queue.AddLast(new CombatAction { Effect = new JObject { ["op"] = "block" }, Source = c.Player, Owner = c.Player, Target = c.Player, Meta = new ActionMeta { Group = group } });
            c.Fields["pendingReaction"] = new JObject { ["id"] = "offer", ["groupKey"] = "root", ["ownerId"] = "player", ["sourceId"] = "e1", ["options"] = new JArray() };
            Assert.That(CoreReactions.DeferRootPlay(c), Is.False, "no card is mid-play");
            var instance = new JObject { ["instanceId"] = "c1", ["cardId"] = "x" };
            c.PendingAbilityPlay = new JObject { ["instanceId"] = "c1" };
            c.PendingPlayInstance = instance;
            Assert.That(CoreReactions.DeferRootPlay(c), Is.True);
            Assert.That(c.PendingAbilityPlay, Is.Null);
            Assert.That(c.Fields["reactionRootPlay"], Is.Not.Null);
            c.Fields.Remove("pendingReaction");
            CoreReactions.RestoreRootPlay(c);
            Assert.That(c.PendingAbilityPlay.Value<bool>("deferredRoot"), Is.True);
            Assert.That(c.PendingAbilityPlay.Value<string>("instanceId"), Is.EqualTo("c1"));
            Assert.That(c.PendingPlayInstance, Is.SameAs(instance));
            Assert.That(c.Fields["reactionRootPlay"], Is.Null);
        }

        [Test]
        public void ADeferredPlayOfAnEnemyRootIsNotDeferred()
        {
            var c = Open();
            var enemy = c.Enemies.First();
            var group = new ExpansionGroup { Key = "root", SourceId = enemy.Value<string>("id"), Carrier = new JObject { ["instanceId"] = "c1" } };
            c.Queue.AddLast(new CombatAction { Effect = new JObject { ["op"] = "damage" }, Source = enemy, Owner = enemy, Target = c.Player, Meta = new ActionMeta { Group = group } });
            c.Fields["pendingReaction"] = new JObject { ["id"] = "offer", ["groupKey"] = "root", ["ownerId"] = "player", ["sourceId"] = enemy["id"], ["options"] = new JArray() };
            c.PendingAbilityPlay = new JObject { ["instanceId"] = "c1" };
            Assert.That(CoreReactions.DeferRootPlay(c), Is.False, "the root belongs to the enemy");
        }

        [Test]
        public void TheQueueAReactionInterruptedIsHandedBackOnceNothingIsLeftToChoose()
        {
            var c = Open();
            var held = new CombatAction { Effect = new JObject { ["op"] = "block" }, Source = c.Player, Owner = c.Player, Target = c.Player, Meta = new ActionMeta() };
            c.ReactionResumeQueue = new System.Collections.Generic.List<CombatAction> { held };
            c.Fields["reactionResume"] = new JObject { ["queue"] = 1, ["foundation"] = JValue.CreateNull() };
            c.Fields["pendingCardChoice"] = new JObject { ["count"] = 1 };
            Assert.That(CoreReactions.RestoreReactionQueue(c), Is.False, "a choice is still open");
            c.Fields.Remove("pendingCardChoice");
            Assert.That(CoreReactions.RestoreReactionQueue(c), Is.True);
            Assert.That(c.Queue.First.Value, Is.SameAs(held));
            Assert.That(c.Fields["reactionResume"], Is.Null);
            Assert.That(CoreReactions.RestoreReactionQueue(c), Is.False, "nothing is held");
        }
    }
}
