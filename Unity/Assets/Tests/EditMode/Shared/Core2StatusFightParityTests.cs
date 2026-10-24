using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-9 (D-240): the statuses, the control, the riders and the remaining effect ops live in real fights. Every sequence recorded by
    /// Tools/oracle-core2-statuses.mjs (4 classes; statuses laid on the fighters, the player playing the cards of its hand that stay inside the
    /// filter, answering the choices they open and recovering from control, the enemies taking their turn) is played again on the C# port from
    /// the same seed and must equal the web after every step: the whole combat as a delta (every field) and the event types in order.
    /// Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2StatusFightParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "statuses");

        public static IEnumerable<string> FightFiles() =>
            Directory.Exists(Dir) ? Directory.GetFiles(Dir, "fights-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>();

        public static IEnumerable<TestCaseData> Fights()
        {
            foreach (var file in FightFiles())
            {
                var fights = (JArray)Core2StatusParityTests.Read(file)["fights"];
                for (var i = 0; i < fights.Count; i++)
                    yield return new TestCaseData(file, i).SetName("Core2StatusFight_" + Path.GetFileNameWithoutExtension(file).Replace("fights-", string.Empty) + "_" + fights[i].Value<string>("bot") + "_" + i);
            }
        }

        [Test]
        public void TheFightsArePresent() => Assert.That(FightFiles().Count(), Is.EqualTo(4), "one file per class");

        [TestCaseSource(nameof(Fights))]
        public void TheFightPlaysAsTheWebPlaysIt(string file, int index) =>
            Core2FightParityTests.Replay((JObject)((JArray)Core2StatusParityTests.Read(file)["fights"])[index], file + "[" + index + "]");
    }
}
