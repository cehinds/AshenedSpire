using System;
using System.Collections.Generic;
using System.IO;
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
    /// WP-C1a (D-303): the Core 2 combat view the W-07 screen binds. Built on the recorded whole fights (Tools/oracle-core2-session.mjs) at every decision of a spread of them, it is
    /// held to the web's recorded state (hit points, Block, Barrier, Poise and Ward, Evade and Precision, the stance and Staggered, the statuses with their stacks, the piles, the
    /// flask charges, the pending offer) and to the rules (a card is playable exactly when the legal list has a play of it, the offer's options and Back are legal answers, a hidden move
    /// shows nothing of itself, a prediction is offered exactly while the move is unread). Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2CombatViewTests
    {
        private static CoreCombatData Data => Core2DamageParityTests.Data;

        private static StringTable Strings => new StringTable(TestContent.ContentJson("strings/combat.en.json"));

        public static IEnumerable<TestCaseData> Fights()
        {
            foreach (var file in Core2SessionParityTests.FightFiles())
            {
                var fights = (JArray)Core2SessionParityTests.Read(file)["fights"];
                for (var i = 0; i < fights.Count; i += 9)
                    yield return new TestCaseData(file, i).SetName("Core2View_" + Path.GetFileNameWithoutExtension(file).Replace("fights-", string.Empty) + "_" + fights[i].Value<string>("encounterId") + "_" + i);
            }
        }

        private static double Stacks(JToken statuses, string id) => statuses?[id]?["stacks"]?.Value<double>() ?? 0;

        [TestCaseSource(nameof(Fights))]
        public void TheViewIsTheRecordedFightAsTheScreenDrawsIt(string file, int index)
        {
            var fight = (JObject)((JArray)Core2SessionParityTests.Read(file)["fights"])[index];
            var seed = (uint)fight["seed"].Value<long>();
            var run = CoreRunRules.Create(Data.Run, seed, fight.Value<string>("classId"), CoreRunOptions.FromJson(null, Core2DamageParityTests.Unlocked));
            var session = Ashen.App.Combat.Core2CombatSession.Start(Data, new Rng(seed), run, fight.Value<string>("encounterId"));
            JToken want = fight["start"]["state"].DeepClone();
            var steps = ((JArray)fight["steps"]).OfType<JObject>().ToList();
            var label = file + "[" + index + "] " + fight.Value<string>("encounterId");
            var strings = Strings;
            var offers = 0;
            for (var n = 0; n <= steps.Count; n++)
            {
                var at = label + " decision " + n;
                var view = Core2CombatView.Build(session, strings);
                Check(session, view, want, at, strings);
                if (view.Offer != null) offers++;
                if (n == steps.Count) break;
                Assert.That(session.Execute((JObject)steps[n]["intent"]).Accepted, Is.True, at);
                want = Core2CombatStartParityTests.Apply(want, steps[n]["delta"]);
            }
            if (session.IsOver)
            {
                var done = Core2CombatView.Build(session, strings);
                Assert.That(done.IsOver, Is.True);
                Assert.That(done.Legal, Is.Empty);
                Assert.That(done.Result, Is.EqualTo(session.State.Result));
            }
            Assert.That(offers, Is.GreaterThanOrEqualTo(0));
        }

        private static void Check(Core2CombatSession session, Core2CombatViewState view, JToken want, string at, StringTable strings)
        {
            var c = session.State;
            var player = want["player"];
            // The fighters, held to the web's recorded state.
            Assert.That(view.Turn, Is.EqualTo(want["turn"].Value<double>()), at + " turn");
            Assert.That(view.Phase, Is.EqualTo(want["phase"].Value<string>()), at + " phase");
            Assert.That(view.Player.Hp, Is.EqualTo(player["hp"].Value<double>()), at + " player hp");
            Assert.That(view.Player.MaxHp, Is.EqualTo(player["maxHp"].Value<double>()), at + " player max hp");
            Assert.That(view.Player.Block, Is.EqualTo(player["block"].Value<double>()), at + " block");
            Assert.That(view.Player.Barrier, Is.EqualTo(player["barrier"].Value<double>()), at + " barrier");
            Assert.That(view.Player.Poise.Value, Is.EqualTo(player["persistentPoise"]["value"].Value<double>()), at + " poise");
            Assert.That(view.Player.Poise.Max, Is.EqualTo(player["persistentPoise"]["max"].Value<double>()), at + " poise max");
            Assert.That(view.Player.Ward.Max, Is.EqualTo(player["persistentWard"]["max"].Value<double>()), at + " ward max");
            Assert.That(view.Player.Evade, Is.EqualTo(Stacks(player["statuses"], "evade")), at + " evade");
            Assert.That(view.Player.Precision, Is.EqualTo(Stacks(player["statuses"], "precision")), at + " precision");
            Assert.That(view.Player.Stamina, Is.EqualTo(player["stamina"].Value<double>()), at + " stamina");
            Assert.That(view.Player.Mana, Is.EqualTo(player["mana"].Value<double>()), at + " mana");
            Assert.That(view.Player.StanceManeuver, Is.EqualTo(player["combatStance"]?["maneuver"]?.Value<string>()), at + " player stance");
            Assert.That(view.Player.Staggered, Is.EqualTo(player["combatStance"]?["staggered"]?.Value<bool>() ?? false), at + " player staggered");
            Assert.That(view.Player.Statuses.Where(s => s.Stacks > 0).Select(s => s.Id).OrderBy(s => s, StringComparer.Ordinal),
                Is.EqualTo(((JObject)player["statuses"]).Properties().Where(p => Stacks(player["statuses"], p.Name) > 0).Select(p => p.Name).OrderBy(s => s, StringComparer.Ordinal)), at + " player statuses");
            Assert.That(view.Player.Name, Is.Not.Empty);
            var enemies = (JArray)want["enemies"];
            Assert.That(view.Enemies.Select(e => e.Id), Is.EqualTo(enemies.Select(e => e.Value<string>("id"))), at + " enemies");
            for (var i = 0; i < enemies.Count; i++)
            {
                var e = enemies[i];
                var v = view.Enemies[i];
                Assert.That(v.Alive, Is.EqualTo(e.Value<bool>("alive")), at + " " + v.Id + " alive");
                Assert.That(v.Hp, Is.EqualTo(e.Value<double>("hp")), at + " " + v.Id + " hp");
                Assert.That(v.Block, Is.EqualTo(e.Value<double>("block")), at + " " + v.Id + " block");
                Assert.That(v.Poise.Value, Is.EqualTo(e["persistentPoise"]["value"].Value<double>()), at + " " + v.Id + " poise");
                Assert.That(v.Evade, Is.EqualTo(Stacks(e["statuses"], "evade")), at + " " + v.Id + " evade");
                if (v.Intent == null || v.Intent.Known) Assert.That(v.StanceManeuver, Is.EqualTo(e["combatStance"]?["maneuver"]?.Value<string>()), at + " " + v.Id + " stance");
                else { Assert.That(v.StanceManeuver, Is.Null, at + " an unread enemy shows no stance"); Assert.That(v.StanceName, Is.Null); Assert.That(v.StanceCamp, Is.Null); Assert.That(v.StanceRange, Is.Null); Assert.That(v.Counter, Is.Null, at + " nor a counter"); }
                Assert.That(v.Staggered, Is.EqualTo(e["combatStance"]?["staggered"]?.Value<bool>() ?? false), at + " " + v.Id + " staggered");
                Assert.That(v.Name, Is.Not.Empty);
                if (!v.Alive) { Assert.That(v.Intent, Is.Null, at + " the dead show no intent"); continue; }
                // What the player knows of the move, and nothing more.
                var read = e["knowledgeAction"]?["reads"]?["player"];
                var exact = read?["visibility"]?.Value<string>() == "exact" || e["intent"]?["kind"]?.Value<string>() == "staggered" || e["actorIntentRevealed"]?.Value<bool>() == true;
                if (read != null && !exact && e["proposalIntentRevealedUntilByOwner"] == null) Assert.That(v.Intent.Known, Is.False, at + " " + v.Id + " an unread move is unknown");
                if (!v.Intent.Known)
                {
                    Assert.That(v.Intent.MoveId, Is.Null, at + " a hidden move shows no id");
                    Assert.That(v.Intent.Damage, Is.Null);
                    Assert.That(v.Intent.Block, Is.Null);
                    Assert.That(v.Intent.Action, Is.Null);
                    Assert.That(v.Intent.Label, Is.EqualTo(read?["label"]?.Value<string>()), at + " the knowledge label");
                }
                else if (e["intent"]?["moveId"] != null && e["intent"]["moveId"].Type != JTokenType.Null)
                {
                    Assert.That(v.Intent.MoveId, Is.EqualTo(e["intent"]["moveId"].Value<string>()), at + " " + v.Id + " move");
                    Assert.That(v.Intent.Damage, Is.EqualTo(e["intent"]["damage"]?.Type == JTokenType.Float || e["intent"]["damage"]?.Type == JTokenType.Integer ? e["intent"]["damage"].Value<double>() : (double?)null));
                }
                var predicts = view.Legal.Count(l => l.Value<string>("type") == "predictIntent" && l.Value<string>("enemyInstanceId") == v.Id);
                Assert.That(predicts, Is.EqualTo(v.Intent.Predictable ? Ashen.App.Combat.Core2CombatSession.PredictionChoices.Count : 0), at + " " + v.Id + " predictions are offered exactly while the move is unread");
            }

            // The hand, the piles and the legal plays.
            Assert.That(view.Hand.Select(h => h.InstanceId), Is.EqualTo(((JArray)want["piles"]["hand"]).Select(h => h.Value<string>("instanceId"))), at + " hand");
            Assert.That(view.DrawCount, Is.EqualTo(((JArray)want["piles"]["draw"]).Count), at + " draw pile");
            Assert.That(view.DiscardCount, Is.EqualTo(((JArray)want["piles"]["discard"]).Count), at + " discard pile");
            Assert.That(view.ExhaustCount, Is.EqualTo(((JArray)want["piles"]["exhaust"]).Count), at + " exhaust pile");
            var paused = view.Offer != null || view.Choice != null;
            foreach (var card in view.Hand)
            {
                var plays = view.Legal.Where(l => l.Value<string>("type") == "playCard" && l.Value<string>("cardInstanceId") == card.InstanceId).ToList();
                Assert.That(card.Plays.Count, Is.EqualTo(plays.Count), at + " " + card.CardId + " plays");
                Assert.That(card.Playable, Is.EqualTo(plays.Count > 0), at + " " + card.CardId + " playable");
                if (card.Playable) Assert.That(card.Refusal, Is.Null, at + " " + card.CardId + " a legal card has no refusal");
                Assert.That(card.Name, Is.Not.Empty);
                if (paused) Assert.That(card.Playable, Is.False, at + " nothing is played while the fight waits on an answer");
                if (!card.NeedsTarget) continue;
                Assert.That(card.Targets, Is.EqualTo(view.Enemies.Where(e => e.Alive).Select(e => e.Id)), at + " " + card.CardId + " targets");
                foreach (var target in card.Targets)
                {
                    var hit = card.Hits[target];
                    Assert.That(hit.HitChance, Is.Not.Null.And.InRange(0.0, 100.0), at + " " + card.CardId + " hit chance on " + target);
                    var direct = session.PreviewHit(card.InstanceId, target)["targets"].OfType<JObject>().First(t => t.Value<string>("targetId") == target);
                    Assert.That(hit.HitChance, Is.EqualTo(direct.Value<double?>("currentHitChance") ?? direct.Value<double>("hitChance")), at + " " + card.CardId + " the badge is the engine's number");
                }
                if (card.Routes.Count > 0 && card.Targets.Count > 0)
                {
                    Assert.That(card.Routes.Select(r => r.Id), Does.Contain("advance"), at + " " + card.CardId);
                    foreach (var route in card.Routes) Assert.That(route.Targets.Select(t => t.TargetId), Does.Contain(card.Targets[0]), at + " route " + route.Id + " quotes the default target");
                }
            }

            // The pause: an offer's options and Back, a card choice's candidates.
            Assert.That(view.Offer != null, Is.EqualTo(want["pendingReaction"] != null && want["pendingReaction"].Type != JTokenType.Null), at + " offer");
            if (view.Offer != null)
            {
                var recorded = (JArray)want["pendingReaction"]["options"];
                Assert.That(view.Offer.Options.Select(o => o.Id), Is.EqualTo(recorded.Select(o => o.Value<string>("id"))), at + " options");
                foreach (var option in view.Offer.Options) Assert.That(option.Legal, Is.EqualTo(option.Refusal == null), at + " option " + option.Id);
                Assert.That(view.Legal.Any(l => JToken.DeepEquals(l, view.Offer.Back)), Is.True, at + " Back is always a legal answer");
                Assert.That(view.Hand.All(h => !h.Playable) && view.EndTurnRefusal != null, Is.True, at + " the turn cannot end while an offer waits");
            }
            Assert.That(view.Choice != null, Is.EqualTo(want["pendingCardChoice"] != null && want["pendingCardChoice"].Type != JTokenType.Null || want["pendingAbilityDiscard"] != null && want["pendingAbilityDiscard"].Type != JTokenType.Null), at + " choice");
            if (view.Choice != null) Assert.That(view.Choice.Answers, Is.Not.Empty, at + " a pending choice can be answered");

            // The turn, the reaction switch, the flasks.
            Assert.That(view.EndTurnRefusal == null, Is.EqualTo(view.Legal.Any(l => l.Value<string>("type") == "endTurn")), at + " end turn");
            Assert.That(view.ReactionsEnabled, Is.EqualTo(player["reactionsEnabled"]?.Value<bool>() ?? true), at + " reactions");
            Assert.That(view.Legal.Any(l => JToken.DeepEquals(l, view.ReactionsToggle)), Is.EqualTo(!paused && !view.IsOver), at + " the reaction switch is legal exactly when nothing waits on an answer");
            foreach (var flask in view.Flasks)
            {
                if (flask.ChargeKind != null) Assert.That(flask.Current, Is.EqualTo(player["flaskCharges"][flask.ChargeKind + "Current"].Value<double>()), at + " " + flask.ChargeKind + " charges");
                Assert.That(flask.Usable, Is.EqualTo(flask.Uses.Count > 0 && flask.Uses.Any(u => view.Legal.Any(l => JToken.DeepEquals(l, u)))), at + " flask " + flask.FlaskId);
            }
            Assert.That(view.Legal.Count(l => l.Value<string>("type") == "useFlask"), Is.EqualTo(view.Flasks.Where(f => f.Usable).Sum(f => f.Uses.Count(u => view.Legal.Any(l => JToken.DeepEquals(l, u))))), at + " flasks in the legal list");

            // The log: the newest events, the string table's sentence where it has one.
            Assert.That(view.Log.Select(l => l.Type), Is.EqualTo(c.EventLog.Skip(Math.Max(0, c.EventLog.Count - 60)).Select(e => e.Value<string>("type"))), at + " log");
            foreach (var line in view.Log)
                Assert.That(line.Text != null, Is.EqualTo(strings.Has("combat.log." + line.Type)), at + " log line " + line.Type);
        }

        /// <summary>
        /// The web's previewCard prices a card after its stance entry (combat.js setCombatStance on the detached preview): the receipt the view shows is the one a play of the card deals,
        /// the entry's rating bonus included, not the one the current stance would give.
        /// </summary>
        [Test]
        public void TheDamageShownIsThePriceAfterTheCardsStanceEntry()
        {
            var seen = 0;
            var differs = 0;
            foreach (var classId in new[] { "reaver", "rogue", "herald", "starseer" })
                for (var seed = 1u; seed <= 25; seed++)
                {
                    var run = CoreRunRules.Create(Data.Run, seed, classId, CoreRunOptions.FromJson(null, Core2DamageParityTests.Unlocked));
                    var session = Ashen.App.Combat.Core2CombatSession.Start(Data, new Rng(seed), run, "loneSoldier");
                    var view = Core2CombatView.Build(session);
                    foreach (var card in view.Hand.Where(h => h.NeedsTarget && h.Playable))
                        foreach (var target in card.Targets)
                        {
                            var shown = card.Hits[target].Damage;
                            if (shown == null) continue;
                            var played = CoreTransaction.DetachPaused(session.State);
                            var inst = played.Piles.Hand.First(h => h.Str("instanceId") == card.InstanceId);
                            var def = CoreCosts.Resolve(played, inst);
                            var damage = (def["effects"] as JArray).OfType<JObject>().First(e => e.Str("op") == "damage" && e["amount"]?.Type != JTokenType.String);
                            var before = CoreDamage.ProfileDamageReceipt(played, played.Player, played.Enemies.First(e => e.Str("id") == target), damage.Num("amount"), CoreCardPlay.Carrier(played, inst, def));
                            var entry = CoreCardPlay.Enter(played, card.InstanceId);
                            var after = CoreDamage.ProfileDamageReceipt(played, played.Player, played.Enemies.First(e => e.Str("id") == target), damage.Num("amount"), entry.Carrier);
                            Assert.That(shown, Is.EqualTo(after.Num("amount")), classId + " " + seed + " " + card.CardId + " on " + target);
                            seen++;
                            if (before.Num("amount") != after.Num("amount")) differs++;
                        }
                }
            Assert.That(seen, Is.GreaterThan(0));
            Assert.That(differs, Is.GreaterThan(0), "some card's stance entry changes what it deals, so the case is covered");
        }

        /// <summary>
        /// The web's previewCard prices a card on the fight as the player sees it (combat.js:2465-2476, F.candidateState with the unread enemies stripped of stance, Counter and
        /// pending move): what the card is shown to deal to an enemy whose move is unread is the same whatever stance that enemy is hidden in, so the receipt never leaks the move.
        /// </summary>
        [Test]
        public void AnUnreadEnemysHiddenStanceDoesNotChangeTheDamageShown()
        {
            var covered = 0;
            foreach (var classId in new[] { "reaver", "rogue", "herald", "starseer" })
                for (var seed = 1u; seed <= 12; seed++)
                {
                    var run = CoreRunRules.Create(Data.Run, seed, classId, CoreRunOptions.FromJson(null, Core2DamageParityTests.Unlocked));
                    var session = Ashen.App.Combat.Core2CombatSession.Start(Data, new Rng(seed), run, "loneSoldier");
                    var c = session.State;
                    foreach (var enemy in c.Enemies.Where(e => e.Is("alive") && !CoreVisibility.IntentExact(c, e)).ToList())
                    {
                        enemy.Remove("combatStance");
                        var baseline = Core2CombatView.Build(session);
                        foreach (var maneuver in new[] { "block", "evade", "smash", "attack", "counter" })
                        {
                            enemy["combatStance"] = new JObject { ["maneuver"] = maneuver, ["camp"] = "physical", ["range"] = "melee" };
                            var view = Core2CombatView.Build(session);
                            for (var i = 0; i < baseline.Hand.Count; i++)
                                foreach (var pair in baseline.Hand[i].Hits.Where(h => h.Key == enemy.Str("id") && h.Value.Damage != null))
                                {
                                    var shown = view.Hand[i].Hits[pair.Key];
                                    Assert.That(shown.Damage, Is.EqualTo(pair.Value.Damage), classId + " " + seed + " " + baseline.Hand[i].CardId + " vs hidden " + maneuver);
                                    Assert.That(shown.Receipt?.ToString(), Is.EqualTo(pair.Value.Receipt?.ToString()), classId + " " + seed + " " + baseline.Hand[i].CardId + " receipt vs hidden " + maneuver);
                                    covered++;
                                }
                        }
                        enemy.Remove("combatStance");
                    }
                }
            Assert.That(covered, Is.GreaterThan(0), "some unread enemy was priced");
        }

        /// <summary>
        /// The web's previewCard runs every hit of a multi-hit card through previewDamageHits (combatDamagePreview.js:83): each hit is priced after the one before, so Block spent by an
        /// early hit is not there for a later one, and the card's damage receipt holds every hit with the total the sum of what they take from HP. The shown target HP is that sum.
        /// </summary>
        [Test]
        public void AMultiHitCardsShownDamageIsEveryHitPricedInTurn()
        {
            // The starting hands hold no multi-hit card: two of the monsters' multi-hit cards are dealt in, as the other parity tests deal cards.
            var covered = 0;
            var sequenced = 0;
            foreach (var classId in new[] { "reaver", "rogue", "herald", "starseer" })
                for (var seed = 1u; seed <= 6; seed++)
                {
                    var run = CoreRunRules.Create(Data.Run, seed, classId, CoreRunOptions.FromJson(null, Core2DamageParityTests.Unlocked));
                    var session = Ashen.App.Combat.Core2CombatSession.Start(Data, new Rng(seed), run, "loneSoldier");
                    var added = 0;
                    foreach (var cardId in new[] { "courtDuelist.flurry", "stitchedKing.thousandHands" })
                        session.State.Piles.Hand.Add(new JObject { ["instanceId"] = "mh" + (++added), ["cardId"] = cardId, ["upgraded"] = false });
                    foreach (var enemy in session.State.Enemies)
                    {
                        enemy["block"] = 10;
                        enemy["barrier"] = 0;
                        enemy.Remove("persistentPoise");
                        enemy.Remove("persistentWard");
                    }
                    var view = Core2CombatView.Build(session);
                    foreach (var card in view.Hand)
                        foreach (var pair in card.Hits.Where(h => h.Value.Damage != null && h.Value.HitsCount > 1))
                        {
                            var at = classId + " " + seed + " " + card.CardId + " on " + pair.Key;
                            var priced = CoreVisibility.Project(CoreTransaction.DetachPaused(session.State));
                            var inst = priced.Piles.Hand.First(h => h.Str("instanceId") == card.InstanceId);
                            var def = CoreCosts.Resolve(priced, inst);
                            var damage = (def["effects"] as JArray).OfType<JObject>().First(e => e.Str("op") == "damage" && e["amount"]?.Type != JTokenType.String);
                            var carrier = CoreCardPlay.Enter(priced, card.InstanceId).Carrier;
                            var enemy = priced.Enemies.First(e => e.Str("id") == pair.Key);
                            var lost = new List<double>();
                            for (var hit = 0; hit < pair.Value.HitsCount; hit++)
                            {
                                var receipt = CoreDamage.ProfileDamageReceipt(priced, priced.Player, enemy, damage.Num("amount"), carrier);
                                lost.Add(receipt.Num("hpLoss"));
                                enemy["block"] = receipt.Num("blockRemaining");
                            }
                            var hits = (pair.Value.Receipt["hits"] as JArray).OfType<JObject>().Select(h => h.Num("hpLoss")).ToList();
                            Assert.That(hits, Is.EqualTo(lost), at);
                            Assert.That(pair.Value.Receipt.Num("totalDamage"), Is.EqualTo(lost.Sum()), at);
                            var live = session.State.Enemies.First(e => e.Str("id") == pair.Key);
                            var shown = Core2CombatText.Preview(new StringTable(TestContent.ContentJson("strings/app.en.json")), view.Enemies.First(e => e.Id == pair.Key), pair.Value);
                            Assert.That(shown, Does.EndWith(CombatText.Number(Math.Max(0, live.Num("hp") - lost.Sum()))), at);
                            covered++;
                            if (lost.Sum() != lost[0] * lost.Count) sequenced++;
                        }
                }
            Assert.That(covered, Is.GreaterThan(0), "a multi-hit card was priced");
            Assert.That(sequenced, Is.GreaterThan(0), "Block spent by an early hit changes a later one, so the case is covered");
        }

        [Test]
        public void TheEquipmentAndTheRearmOptionsAreJudgedByTheRules()
        {
            var seed = 1121046183u;
            var run = CoreRunRules.Create(Data.Run, seed, "reaver", CoreRunOptions.FromJson(null, Core2DamageParityTests.Unlocked));
            ((JArray)run["loadout"]["storage"]).Add("dagger");
            run["loadout"]["sets"]["rightHand"][1] = "straightSword";
            var session = Ashen.App.Combat.Core2CombatSession.Start(Data, new Rng(seed), run, "loneSoldier");
            var view = Core2CombatView.Build(session);
            Assert.That(view.Equipment, Is.Not.Null);
            var right = view.Equipment.Slots.First(s => s.SlotId == "rightHand");
            Assert.That(right.Sets.Count(s => s.Active), Is.EqualTo(1));
            var other = right.Sets.First(s => !s.Active && s.ItemId != null);
            Assert.That(other.Refusal, Is.Null, "a swap at the start of a turn is allowed");
            Assert.That(other.Cost, Is.Not.Null.And.GreaterThanOrEqualTo(0));
            var changes = Core2CombatView.EquipmentChanges(session, "rightHand", 1);
            Assert.That(changes.Select(ch => ch.PieceId), Does.Contain("dagger").And.Contain(null as string));
            Assert.That(changes.Where(ch => ch.Allowed).All(ch => session.Why(ch.Intent) == null), Is.True);
            Assert.That(session.Execute(other.Intent).Accepted, Is.True, "the view's swap is the intent the rules take");
            Assert.That(Core2CombatView.Build(session).Equipment.Slots.First(s => s.SlotId == "rightHand").Sets[other.SetIndex].Active, Is.True);
        }

        /// <summary>
        /// The web's previewCard fills a Block token with computeBlockGain on the stance-entered copy (combat.js:2727-2730, tokens at 2803), so Defense and the matching-stance multiplier are in
        /// the number a defensive card shows: it reads what playing it grants, not the authored amount.
        /// </summary>
        [Test]
        public void ADefensiveCardsShownBlockIsWhatPlayingItGrants()
        {
            var seen = 0;
            var differs = 0;
            foreach (var classId in new[] { "reaver", "rogue", "herald", "starseer" })
                foreach (var defense in new[] { 0, 7 })
                    for (var seed = 1u; seed <= 12; seed++)
                    {
                        var run = CoreRunRules.Create(Data.Run, seed, classId, CoreRunOptions.FromJson(null, Core2DamageParityTests.Unlocked));
                        var session = Ashen.App.Combat.Core2CombatSession.Start(Data, new Rng(seed), run, "loneSoldier");
                        session.State.Player.Obj("ratings")["dr"] = defense;
                        var view = Core2CombatView.Build(session);
                        foreach (var card in view.Hand.Where(h => h.Playable && !h.NeedsTarget && h.Choices.Count == 0))
                        {
                            var inst = session.State.Piles.Hand.First(h => h.Str("instanceId") == card.InstanceId);
                            var def = CoreCosts.Resolve(session.State, inst);
                            var block = (def["effects"] as JArray).OfType<JObject>().FirstOrDefault(e => e.Str("op") == "block" && e["amount"]?.Type != JTokenType.String);
                            if (block == null || (def["effects"] as JArray).OfType<JObject>().Count(e => e.Str("op") == "block") != 1) continue;
                            var entered = CoreTransaction.DetachPaused(session.State);
                            CoreCardPlay.Enter(entered, card.InstanceId);
                            var played = CoreTransaction.DetachPaused(session.State);
                            CoreDispatch.Dispatch(played, (JObject)card.Plays[0].DeepClone());
                            var granted = played.Player.Num("block") - entered.Player.Num("block");
                            Assert.That(card.Text, Does.Contain(CombatText.Number(granted) + " Block"), classId + " dr " + defense + " " + seed + " " + card.CardId);
                            seen++;
                            if (granted != Math.Floor(block.Num("amount"))) differs++;
                        }
                    }
            Assert.That(seen, Is.GreaterThan(0));
            Assert.That(differs, Is.GreaterThan(0), "Defense or a matching stance changes what some card grants, so the case is covered");
        }

        private static readonly string[] FormulaCards = { "base.pay-x-effect", "base.scale-negative-status", "base.scale-damage-taken", "base.damage-per-use" };

        /// <summary>
        /// The web's previewCard evaluates a formula amount with the execution evaluator (combat.js:2879) against the aimed target and the Stamina the play pays (2511-2540), and feeds it to previewDamageHits
        /// (2677-2679) and the damage token (2803): Channel X, Affliction Scaling, Reprisal Growth and Rising Damage show what a play deals, not the literal placeholder.
        /// </summary>
        [Test]
        public void AFormulaAmountIsEvaluatedForTheTextAndTheDamageShown()
        {
            var covered = 0;
            var scaled = 0;
            foreach (var classId in new[] { "reaver", "rogue", "herald", "starseer" })
                for (var seed = 1u; seed <= 6; seed++)
                {
                    var run = CoreRunRules.Create(Data.Run, seed, classId, CoreRunOptions.FromJson(null, Core2DamageParityTests.Unlocked));
                    var session = Ashen.App.Combat.Core2CombatSession.Start(Data, new Rng(seed), run, "loneSoldier");
                    var added = 0;
                    foreach (var cardId in FormulaCards)
                        session.State.Piles.Hand.Add(new JObject { ["instanceId"] = "fm" + (++added), ["cardId"] = cardId, ["upgraded"] = false });
                    session.State.Player.Obj("abilityRiders")["hpLostSinceTurnStart"] = 8;
                    foreach (var enemy in session.State.Enemies)
                    {
                        enemy["block"] = 0;
                        enemy["barrier"] = 0;
                        enemy.Remove("persistentPoise");
                        enemy.Remove("persistentWard");
                        enemy.Obj("statuses")["weak"] = new JObject { ["stacks"] = 2 };
                        enemy.Obj("statuses")["vulnerable"] = new JObject { ["stacks"] = 2 };
                    }
                    var view = Core2CombatView.Build(session);
                    foreach (var card in view.Hand.Where(h => FormulaCards.Contains(h.CardId)))
                    {
                        var at = classId + " " + seed + " " + card.CardId;
                        Assert.That(card.Text, Does.Not.Contain("{"), at + " text");
                        foreach (var pair in card.Hits.Where(h => h.Value.Damage != null))
                        {
                            // The player sees the fight through what he has read: an unread enemy's hidden stance is not in the price (combat.js:2465-2476), so the play is run on the same view of it.
                            var played = CoreVisibility.Project(CoreTransaction.DetachPaused(session.State));
                            var before = played.Enemies.First(e => e.Str("id") == pair.Key).Num("hp");
                            var play = card.Plays.FirstOrDefault(l => l.Str("targetId") == pair.Key);
                            if (play == null) continue;
                            CoreDispatch.Dispatch(played, (JObject)play.DeepClone());
                            var lost = before - played.Enemies.First(e => e.Str("id") == pair.Key).Num("hp");
                            if (lost <= 0) continue; // a miss deals nothing; the odds are shown beside the damage
                            Assert.That(pair.Value.Receipt.Num("totalDamage"), Is.EqualTo(lost), at + " on " + pair.Key);
                            // The hand's face is the un-aimed amount before defense and matchups (combat.js:2792), so a Vulnerable target does not move it.
                            Assert.That(card.Text, Does.Not.Contain(CombatText.Number(pair.Value.Damage.Value) + " damage"), at + " text is the face, not the aimed hit");
                            covered++;
                            if (lost != 6) scaled++;
                        }
                    }
                }
            Assert.That(covered, Is.GreaterThan(0), "a formula card was priced against a target");
            Assert.That(scaled, Is.GreaterThan(0), "the formula moves the damage off the printed base, so the case is covered");
        }

        /// <summary>No shipped Core 2 card, dealt into a hand, shows an unfilled {token} placeholder: every value the web's previewCard binds is filled (combat.js:2803).</summary>
        [Test]
        public void NoShippedCardShowsAnUnfilledToken()
        {
            var run = CoreRunRules.Create(Data.Run, 1, "reaver", CoreRunOptions.FromJson(null, Core2DamageParityTests.Unlocked));
            var session = Ashen.App.Combat.Core2CombatSession.Start(Data, new Rng(1), run, "loneSoldier");
            var ids = Js.Items(session.State.CoreData.Content(Ashen.Generated.Core2ProfileContentKeys.Cards)).OfType<JObject>().Select(card => card.Str("id")).Where(id => id != null).ToList();
            Assert.That(ids, Is.Not.Empty);
            var added = 0;
            foreach (var id in ids)
                session.State.Piles.Hand.Add(new JObject { ["instanceId"] = "all" + (++added), ["cardId"] = id, ["upgraded"] = false });
            var view = Core2CombatView.Build(session);
            var unfilled = view.Hand.Where(h => h.Text != null && h.Text.Contains("{")).Select(h => h.CardId + ": " + h.Text).ToList();
            Assert.That(unfilled, Is.Empty);
        }

        /// <summary>
        /// Every numeric token the view prints for a card, shipped Core 2 content and class cards alike, is what playing that card on the same projected copy produces (web previewCard, combat.js:2465-2885):
        /// the Block and Barrier the play grants (Defense, the matching stance, a held charge), the HP it heals (the rating bonus, the charge and the stance's effectiveness, combat.js:2751), the Poise guard,
        /// the damage face (the un-aimed amount before defense, charge included, combat.js:2792) and the figure a card charge grants.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void EveryNumberTheViewShowsIsWhatPlayingTheCardProduces(bool held)
        {
            var seen = new Dictionary<string, int>();
            var shifted = new Dictionary<string, int>();
            void Check(string token, double shown, double made, string at)
            {
                Assert.That(shown, Is.EqualTo(made), at + " " + token);
                seen[token] = seen.TryGetValue(token, out var n) ? n + 1 : 1;
            }
            foreach (var classId in new[] { "reaver", "rogue", "herald", "starseer" })
                for (var seed = 1u; seed <= 2; seed++)
                {
                    var run = CoreRunRules.Create(Data.Run, seed, classId, CoreRunOptions.FromJson(null, Core2DamageParityTests.Unlocked));
                    var session = Ashen.App.Combat.Core2CombatSession.Start(Data, new Rng(seed), run, "loneSoldier");
                    session.State.Player.Obj("ratings")["dr"] = 3;
                    session.State.Player.Obj("ratings")["pr"] = 4;
                    session.State.Player.Obj("ratings")["ar"] = 5;
                    session.State.Player["hp"] = session.State.Player.Num("maxHp") - 30;
                    foreach (var enemy in session.State.Enemies)
                    {
                        enemy["block"] = 0;
                        enemy["barrier"] = 0;
                        enemy.Remove("persistentPoise");
                        enemy.Remove("persistentWard");
                        enemy.Remove("combatStance");
                    }
                    if (held) CoreRiders.Grant(session.State.Player, new JObject { ["key"] = "held", ["damage"] = 3, ["block"] = 2, ["heal"] = 2 });
                    var ids = session.State.Data.Cards.All.Select(card => card.Str("id")).Where(id => id != null).Concat(Js.Items(session.State.CoreData.Content(Ashen.Generated.Core2ProfileContentKeys.Cards)).OfType<JObject>().Select(card => card.Str("id"))).Distinct().ToList();
                    var added = 0;
                    foreach (var id in ids)
                        session.State.Piles.Hand.Add(new JObject { ["instanceId"] = "all" + (++added), ["cardId"] = id, ["upgraded"] = false });
                    var view = Core2CombatView.Build(session);
                    foreach (var card in view.Hand.Where(h => h.Playable && h.Choices.Count == 0))
                    {
                        var at = classId + " " + seed + " " + card.CardId + (held ? " held" : string.Empty);
                        var inst = session.State.Piles.Hand.First(h => h.Str("instanceId") == card.InstanceId);
                        var def = CoreCosts.Resolve(session.State, inst);
                        var effects = (def["effects"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
                        if (effects.Any(e => e["if"] != null || e["oncePerTurn"] != null) || card.Text.Contains("Counter")) continue;
                        var play = (JObject)card.Plays[0].DeepClone();
                        if (play["targetId"] == null && card.Targets.Count > 0) play["targetId"] = card.Targets[0];
                        // The stance the card enters grants its own Block and Poise (CoreCardPlay.Enter); the card's figure is what comes on top of that.
                        var before = CoreVisibility.Project(CoreTransaction.DetachPaused(session.State));
                        try { CoreCardPlay.Enter(before, card.InstanceId); } catch (Exception) { continue; }
                        var played = CoreVisibility.Project(CoreTransaction.DetachPaused(session.State));
                        var logged = played.EventLog.Count;
                        try { CoreDispatch.Dispatch(played, (JObject)play.DeepClone()); }
                        catch (Exception) { continue; }
                        if (played.Piles.Hand.Any(h => h.Str("instanceId") == card.InstanceId)) continue; // the play was refused
                        int Count(string op) => effects.Count(e => e.Str("op") == op);
                        bool Plain(string op) => effects.Where(e => e.Str("op") == op).All(e => e["amount"] != null && (e["amount"].Type == JTokenType.Integer || e["amount"].Type == JTokenType.Float));
                        if (Count("block") == 1 && Plain("block") && Count("gainBarrier") == 0 && card.Tokens.ContainsKey("block"))
                            Check("block", card.Tokens["block"], played.EventLog.Skip(logged).Where(e => e.Str("type") == "blockGained" && e.Str("cardInstanceId") == card.InstanceId).Sum(e => e.Num("amount")), at);
                        if (Count("gainBarrier") == 1 && Plain("gainBarrier") && Count("block") == 0 && card.Tokens.ContainsKey("gainBarrier"))
                            Check("gainBarrier", card.Tokens["gainBarrier"], played.Player.Num("barrier") - before.Player.Num("barrier"), at);
                        if (Count("gainPoise") == 1 && Plain("gainPoise") && card.Tokens.ContainsKey("gainPoise"))
                            Check("gainPoise", card.Tokens["gainPoise"], played.Player.Num("gameProfilePoise") - before.Player.Num("gameProfilePoise"), at);
                        // The web's preview leaves an Ashen Blight restoration cut out of the heal (combat.js:2751).
                        if (Count("heal") == 1 && Plain("heal") && card.Tokens.ContainsKey("heal") && CoreStatusControl.RestorationPercent(before, before.Player) == 0)
                        {
                            var healed = played.EventLog.Skip(logged).FirstOrDefault(e => e.Str("type") == "healed" && e.Str("targetId") == played.Player.Str("id"));
                            if (healed != null)
                            {
                                Check("heal", card.Tokens["heal"], healed.Num("requested"), at);
                                if (healed.Num("requested") != effects.First(e => e.Str("op") == "heal").Num("amount")) shifted["heal"] = shifted.GetValueOrDefault("heal") + 1;
                            }
                        }
                        foreach (var grant in effects.Where(e => e.Str("op") == "grantCardCharge"))
                        {
                            var made = CoreEvents.RidersOf(played.Player).Obj("charges")?.Obj(grant.Str("key"));
                            foreach (var field in new[] { "damage", "block", "heal", "break" })
                                if (grant[field] != null && grant[field].Type != JTokenType.Object && made != null && card.Tokens.TryGetValue("charge" + char.ToUpperInvariant(field[0]) + field.Substring(1), out var shown))
                                    Check("charge" + field, shown, made.Num(field), at);
                        }
                        if (Count("damage") == 1 && Plain("damage") && card.Tokens.ContainsKey("damage") && !card.Text.Contains("times") && !effects.Any(e => e["hits"] != null))
                        {
                            var target = played.Enemies.FirstOrDefault(e => e.Str("id") == play.Str("targetId"));
                            var prior = session.State.Enemies.FirstOrDefault(e => e.Str("id") == play.Str("targetId"));
                            if (target == null || prior == null) { continue; }
                            var lost = prior.Num("hp") - target.Num("hp");
                            if (lost <= 0) seen["missed"] = seen.GetValueOrDefault("missed") + 1;
                            if (lost <= 0 || !target.Is("alive")) continue; // a kill is capped by the hit points left; a miss deals nothing; the odds are shown beside the damage
                            Check("damage", card.Tokens["damage"], lost, at);
                            if (lost != Math.Floor(effects.First(e => e.Str("op") == "damage").Num("amount"))) shifted["damage"] = shifted.GetValueOrDefault("damage") + 1;
                        }
                    }
                }
            foreach (var token in new[] { "block", "heal", "damage" })
                Assert.That(seen.GetValueOrDefault(token), Is.GreaterThan(0), token + " was compared against a play: " + string.Join(", ", seen.Select(kv => kv.Key + "=" + kv.Value)));
            Assert.That(shifted.GetValueOrDefault("damage"), Is.GreaterThan(0), "the attack rating or a charge moves a damage face off its printed amount");
            if (held) Assert.That(shifted.GetValueOrDefault("heal"), Is.GreaterThan(0), "a held charge moves a heal off its printed amount, so the case is covered");
            TestContext.WriteLine(string.Join(", ", seen.Select(kv => kv.Key + "=" + kv.Value)));
        }
    }
}
