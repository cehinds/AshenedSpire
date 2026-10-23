using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-7 (D-220): the damage chain live in real fights. Every sequence recorded by Tools/oracle-core2-damage.mjs (4 classes, the enemies
    /// taking their turn and the player playing attack and Block cards) is played again on the C# port from the same seed and must equal the
    /// web after every step: the whole combat as a delta (every field), the event types in order and every RNG counter. The oracle records
    /// only steps that stay inside what this package ports (its dry-run filter, documented at the top of the oracle): no reaction offer, no
    /// Counter, no status proc, no move that has to close the distance. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2FightParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "damage");

        private static JObject Read(string name) => Core2DamageParityTests.Read(name);

        public static IEnumerable<string> FightFiles() =>
            Directory.Exists(Dir) ? Directory.GetFiles(Dir, "fights-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>();

        public static IEnumerable<TestCaseData> Fights()
        {
            foreach (var file in FightFiles())
            {
                var fights = (JArray)Read(file)["fights"];
                for (var i = 0; i < fights.Count; i++)
                    yield return new TestCaseData(file, i).SetName("Core2Fight_" + Path.GetFileNameWithoutExtension(file).Replace("fights-", string.Empty) + "_" + fights[i].Value<string>("bot") + "_" + i);
            }
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Read("index.json");
            Assert.That(FightFiles().Count(), Is.GreaterThanOrEqualTo(4));
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Core2DamageParityTests.Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(((JArray)index["coveredEvents"]).Count, Is.GreaterThan(0));
        }

        [TestCaseSource(nameof(Fights))]
        public void TheFightPlaysAsTheWebPlaysIt(string file, int index) => Replay((JObject)((JArray)Read(file)["fights"])[index], file + "[" + index + "]");

        /// <summary>Plays one recorded fight on the port and holds it to the web after every step (the whole combat as a delta, the event types in order).</summary>
        internal static void Replay(JObject fight, string where)
        {

            var c = Core2CombatStartParityTests.Open(fight, out _);
            var label = $"{where} {fight.Value<string>("bot")} {fight.Value<string>("encounterId")}";
            JToken want = fight["start"]["state"].DeepClone();
            Core2CombatStartParityTests.Same(label + " start", want, Core2CombatStartParityTests.Project(c));
            Assert.That(Core2CombatStartParityTests.Events(c, 0), Is.EqualTo(fight["start"]["events"].Select(t => t.Value<string>()).ToList()), label + " start events");
            var n = 0;
            foreach (var step in ((JArray)fight["steps"]).OfType<JObject>())
            {
                var intent = (JObject)step["intent"];
                var at = $"{label} step {n++} {intent.Value<string>("type")}";
                var mark = c.EventLog.Count;
                switch (intent.Value<string>("type"))
                {
                    case "setReactions":
                        c.Player["reactionsEnabled"] = intent.Value<bool>("enabled");
                        break;
                    case "endTurn":
                        foreach (var id in (step["staggered"] as JArray ?? new JArray()).Select(t => t.Value<string>()))
                            c.Enemies.First(e => e.Value<string>("id") == id)["skipNextTurn"] = true;
                        CoreTurns.EndTurn(c, intent["discardIds"].Select(t => t.Value<string>()).ToList());
                        break;
                    case "playCard":
                        CoreCardPlay.Play(c, intent.Value<string>("cardInstanceId"), intent["targetId"]?.Value<string>(), intent["choice"]);
                        break;
                    case "chooseCards":
                        CoreCardPlay.ChooseCards(c, intent["cardInstanceIds"].Select(t => t.Value<string>()).ToList());
                        break;
                    case "chooseDiscard":
                        CoreCardPlay.ChooseDiscard(c, intent["cardInstanceIds"].Select(t => t.Value<string>()).ToList());
                        break;
                    case "recoverControl":
                        CoreCardPlay.RecoverControl(c, (JArray)intent["selections"]);
                        break;
                    case "applyStatus":
                        CoreStatuses.Apply(c, Core2StatusParityTests.Entity(c, intent.Value<string>("who")), intent.Value<string>("status"), intent.Value<double>("stacks"), Core2StatusParityTests.Entity(c, intent.Value<string>("from")));
                        CoreActions.Drain(c);
                        break;
                    default:
                        Assert.Fail(at + " an intent the port does not play");
                        break;
                }
                want = Core2CombatStartParityTests.Apply(want, step["delta"]);
                Core2CombatStartParityTests.Same(at, want, Core2CombatStartParityTests.Project(c));
                Assert.That(Core2CombatStartParityTests.Events(c, mark), Is.EqualTo(step["events"].Select(t => t.Value<string>()).ToList()), at + " events");
            }
        }
    }
}
