using System.Collections.Generic;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-7b (D-1730..): the combatant door for the player and for an enemy, with Block, a stance, a prepared Evade, statuses and intents laid on a started fight. The web's wording
    /// (Core2InspectorParityTests holds every block to the web's record) is written out here for a few fixtures so a change to a section, a row or a word names itself. Suite: Core2.
    /// </summary>
    [TestFixture]
    public class Core2CombatantInspectorTests
    {
        private static CoreCombatData _data;
        private static UiData _ui;

        private static CoreCombatData Data
        {
            get
            {
                if (_data != null) return _data;
                var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
                var registries = Core2Registries.Load(snapshot.Content);
                return _data = registries.ToCoreCombatData(snapshot.Content, registries.ToCoreRunData(snapshot.Content));
            }
        }

        private static UiData Ui => _ui ?? (_ui = UiData.Load(TestContent.Source));

        private const uint Seed = 4242;

        /// <summary>A fight against a lone Wandering Soldier, as a copy the test may lay things on.</summary>
        private static CombatState Fight()
        {
            var unlocked = new JObject
            {
                ["gameProfileUnlocks"] = new JObject { [Data.Run.ProfileId] = new JObject { ["completedRunIds"] = new JArray(Enumerable.Range(0, Data.Run.Run.Classes.Count).Select(i => "r" + i)) } },
            };
            var run = CoreRunRules.Create(Data.Run, Seed, "reaver", CoreRunOptions.FromJson(null, unlocked));
            var session = Core2CombatSession.Start(Data, new Rng(Seed), run, "loneSoldier");
            return session.State.Detach();
        }

        private static CombatantInspectorView Door(CombatState c, JObject entity, string playerName = null) =>
            Core2CombatantInspector.Build(c, entity, new InspectorContext { Strings = Ui.Strings, Text = Ui.CardText, PlayerName = playerName });

        private static InspectorBlock Block(CombatantInspectorView view, string kind, string id = null) =>
            view.Blocks.First(b => b.Kind == kind && (id == null || b.Id == id));

        private static InspectorRow RowOf(InspectorBlock block, string label) => block.Rows.First(r => r.Label == label);

        [Test]
        public void ThePlayersDoorNamesTheCharacterAndSaysBlockPoolsAndTheActiveList()
        {
            var c = Fight();
            c.Player["block"] = 7;
            c.Player["combatStance"] = new JObject { ["maneuver"] = "defend", ["camp"] = "physical", ["range"] = "melee", ["reach"] = "melee" };
            c.Player["combatEvade"] = new JObject { ["charges"] = 2 };
            ((JObject)c.Player["statuses"])["strength"] = new JObject { ["stacks"] = 3 };
            ((JObject)c.Player["statuses"])["vulnerable"] = new JObject { ["stacks"] = 2, ["duration"] = 2 };
            ((JObject)c.Player["statuses"])["magicVulnerable"] = new JObject { ["stacks"] = 25, ["duration"] = 2 };
            var view = Door(c, c.Player, "Ember");

            Assert.That(view.Title, Is.EqualTo("EMBER"), "the name the player gave, in capitals");
            Assert.That(view.Eyebrow, Is.EqualTo("Reaver · Level 1"));
            Assert.That(view.Meters.Select(m => m.Label + " " + m.Value), Is.EqualTo(new[] { "HP 71 / 71", "MP 1 / 1", "Poise 0 / 10" }), "the pools the fighter has, in the web's order");
            Assert.That(view.Blocks.Select(b => b.Kind == "section" ? b.Id : b.Kind), Is.EqualTo(new[] { "summary", "state", "history", "abilities", "traits", "lore" }));

            var summary = Block(view, "section", "summary");
            Assert.That(summary.Rows.Select(r => r.Label + ": " + r.Status), Is.EqualTo(new[] { "HP: 71 / 71", "Defense: 7 Block" }));
            var state = Block(view, "section", "state");
            Assert.That(state.Rows.Select(r => r.Label), Is.EqualTo(new[] { "MP", "Poise", "AR", "DR", "PR" }), "pools in play; HP and Block are in the summary, the effects in the active list");

            Assert.That(Block(view, "section", "history").Knowledge, Is.EqualTo("unknown"), "the player has no recorded history");
            var list = Block(view, "abilities");
            Assert.That(list.Title, Is.EqualTo("Active skills & stance (5)"));
            Assert.That(list.Items.Select(i => i.Name), Is.EqualTo(new[] { "Defend stance", "Evade ×2", "Strength ×3", "Vulnerable ×2", "Magic Vulnerable 25% · 2 turns" }));
            Assert.That(list.Items[2].Detail, Is.EqualTo("Attacks deal +1 damage per stack."));
            Assert.That(list.Items[3].Detail, Does.EndWith("Turns left: 2."));
            Assert.That(list.Items[1].Detail, Does.StartWith("2 charges remaining."));
        }

        [Test]
        public void AnEmptyActiveListSaysNoneUnderItsOwnTitle()
        {
            var c = Fight();
            var view = Door(c, c.Player);
            var abilities = Block(view, "section", "abilities");
            Assert.That(abilities.Knowledge, Is.EqualTo("none"));
            Assert.That(abilities.Title, Is.EqualTo("Active skills & stance"));
            Assert.That(abilities.Rows.Single().Label, Is.EqualTo("None."));
            Assert.That(abilities.Rows.Single().Disabled, Is.True);
            Assert.That(view.Title, Is.EqualTo("REAVER"), "without a name of its own the character is its class");
        }

        [Test]
        public void AnEnemysDoorReadsItsMoveItsDefensesItsEffectsAndItsBestiary()
        {
            var c = Fight();
            var enemy = c.Enemies[0];
            enemy["actorIntentRevealed"] = true;
            enemy["block"] = 4;
            ((JObject)enemy["statuses"])["vulnerable"] = new JObject { ["stacks"] = 1, ["duration"] = 1 };
            ((JObject)enemy["statuses"])["bleed"] = new JObject { ["stacks"] = 0, ["duration"] = 0, ["meter"] = new JObject { ["value"] = 3, ["max"] = 8 } };
            var view = Door(c, enemy);

            Assert.That(view.Title, Is.EqualTo("Wandering Soldier"));
            Assert.That(view.Eyebrow, Is.EqualTo("Humanoid"), "the head says what it is: its tags");
            Assert.That(view.Meters.Select(m => m.Label), Is.EqualTo(new[] { "HP", "SP", "Poise" }));
            Assert.That(view.Blocks.Select(b => b.Kind == "section" ? b.Id : b.Kind), Is.EqualTo(new[] { "summary", "state", "knowledge", "prediction", "history", "deck", "traits", "lore" }));

            var summary = Block(view, "section", "summary");
            Assert.That(summary.Rows.Select(r => r.Label), Is.EqualTo(new[] { "HP", "SP", "Intent", "Defense" }));
            Assert.That(RowOf(summary, "Intent").Status, Does.StartWith("Warcry · Apply 2 Strength to self"), "the move's name, then what it does");
            Assert.That(RowOf(summary, "Defense").Status, Is.EqualTo("4 Block"));

            var state = Block(view, "section", "state");
            Assert.That(RowOf(state, "Vulnerable").Status, Is.EqualTo("Takes 25% more attack damage. 1 stack expires each turn. Turns left: 1."));
            Assert.That(RowOf(state, "Bleed").Status, Does.Contain("burst for"), "a build-up reads its own numbers");
            Assert.That(RowOf(state, "Poise").Status, Is.EqualTo("0 / 5"));

            var bestiary = Block(view, "knowledge");
            Assert.That(bestiary.Stage, Is.EqualTo(0), "a Core 2 run keeps no ledger of what it learned");
            Assert.That(bestiary.Lines.First(l => l.Kind == "title").Text, Is.EqualTo("Unencountered · 0 / 30"));
            Assert.That(bestiary.Lines.Where(l => l.Kind == "stage").Select(l => l.Text).First(), Is.EqualTo("1. Encountered — Unlocks at 1 points"));
            Assert.That(Block(view, "section", "traits").Knowledge, Is.EqualTo("unknown"));
            Assert.That(Block(view, "section", "lore").Knowledge, Is.EqualTo("unknown"));
        }

        [Test]
        public void AnUnreadMoveNeverNamesItselfAndCanBePredictedUntilItIs()
        {
            var c = Fight();
            var enemy = c.Enemies[0];
            enemy.Remove("actorIntentRevealed");
            var read = enemy["knowledgeAction"]["reads"]["player"];
            read["visibility"] = "unknown";
            read["label"] = "?";
            read["prediction"] = JValue.CreateNull();
            read["resolved"] = false;
            enemy["knowledgeAction"]["executed"] = false;
            enemy["knowledgeAction"]["cancelled"] = false;
            c.Phase = "player";
            var view = Door(c, enemy);

            var summary = Block(view, "section", "summary");
            Assert.That(RowOf(summary, "Intent").Label, Is.EqualTo("Intent"));
            Assert.That(RowOf(summary, "Intent").Status, Does.StartWith("? · Exact action unread."), "an unread move is a question mark, never its name or its damage");
            Assert.That(RowOf(summary, "Intent").Status, Does.Not.Contain("Warcry"));
            Assert.That(Block(view, "section", "state").Rows.Select(r => r.Label), Does.Not.Contain("Attack stance"), "its stance would give the move away");

            var prediction = Block(view, "prediction");
            Assert.That(prediction.Eligible, Is.True);
            Assert.That(prediction.Lines.First(l => l.Kind == "select").Choices, Is.EqualTo(Core2CombatSession.PredictionChoices));
            Assert.That(prediction.Lines.Last().Text, Is.EqualTo("Predict action"));

            read["prediction"] = "Attack";
            var after = Block(Door(c, enemy), "prediction");
            Assert.That(after.Eligible, Is.False, "one prediction per action");
            Assert.That(after.Lines.Single(l => l.Kind == "status").Text, Is.EqualTo("Prediction: Attack. Awaiting the action."));
        }

        [Test]
        public void PreviousActionsAreTheMovesItPerformedNewestFirst()
        {
            var c = Fight();
            var enemy = c.Enemies[0];
            enemy["performedMoves"] = new JArray("slash", "guard");
            var history = Block(Door(c, enemy), "section", "history");
            Assert.That(history.Knowledge, Is.EqualTo("known"));
            Assert.That(history.Rows.Select(r => r.Label), Is.EqualTo(new[] { "Guard", "Slash" }));
            Assert.That(history.Rows.All(r => r.Status.Length > 0), Is.True, "each says what it did");
            enemy["performedMoves"] = new JArray();
            Assert.That(Block(Door(c, enemy), "section", "history").Knowledge, Is.EqualTo("none"));
        }

        [Test]
        public void ARevealedMoveOpensTheEnemysHandAndTheDoorShowsIt()
        {
            var c = Fight();
            var enemy = c.Enemies[0];
            Assert.That(Door(c, enemy).Blocks.Any(b => b.Kind == "revealedHand"), Is.False, "nothing is revealed until a card reads it");
            enemy["proposalIntentRevealedUntilByOwner"] = new JObject { ["player"] = c.Turn + 2 };
            enemy["burdenPiles"] = new JObject
            {
                ["draw"] = new JArray(), ["discard"] = new JArray(), ["exhaust"] = new JArray(),
                ["hand"] = new JArray(new JObject { ["instanceId"] = "h0", ["cardId"] = "strike" }, new JObject { ["instanceId"] = "h1", ["cardId"] = "strike" }),
            };
            var hand = Block(Door(c, enemy), "revealedHand");
            Assert.That(hand.Title, Is.EqualTo("Revealed enemy hand"));
            Assert.That(hand.Rows.Single().Label, Is.EqualTo("Strike ×2"));
            Assert.That(hand.Counts, Is.EqualTo("draw: 0 · hand: 2 · discard: 0 · exhaust: 0"));
        }

        [Test]
        public void TheViewSerialisesAsTheOracleRecordsIt()
        {
            var c = Fight();
            var json = Door(c, c.Player).ToJson();
            Assert.That(json["blocks"], Is.InstanceOf<JArray>());
            Assert.That((string)json["preview"]["name"], Is.EqualTo("REAVER"));
            Assert.That((string)json["blocks"][0]["id"], Is.EqualTo("summary"));
        }
    }
}
