using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>US-0.5 / US-18.4: the C# RNG, seed codec and seat order reproduce the shipped JS exactly (suite: Parity).</summary>
    [TestFixture, Category("Parity")]
    public class RngParityTests
    {
        private static RngStream Stream(string wire)
        {
            var s = RngStreamNames.Parse(wire);
            Assert.That(s, Is.Not.EqualTo(RngStream.None), "unknown stream " + wire);
            return s;
        }

        [Test]
        public void StreamListMatchesTheShippedEngine()
        {
            var oracle = TestContent.Oracle("rng.json");
            var shipped = oracle["streams"].Select(t => (string)t).OrderBy(s => s, System.StringComparer.Ordinal);
            Assert.That(RngStreamNames.All.OrderBy(s => s, System.StringComparer.Ordinal), Is.EqualTo(shipped));
        }

        [Test]
        public void RawDrawsIntsShufflesChancesAndRestoredCountersMatch()
        {
            var oracle = TestContent.Oracle("rng.json");
            var checkedDraws = 0;
            foreach (var c in oracle["cases"])
            {
                var seed = (uint)(long)c["seed"];
                foreach (var prop in ((JObject)c["streams"]).Properties())
                {
                    var stream = Stream(prop.Name);
                    var o = prop.Value;
                    var rng = new Rng(seed);
                    foreach (var expected in o["draws"]) { Assert.That(rng.NextUInt(stream), Is.EqualTo((uint)(decimal)expected), seed + "/" + prop.Name); checkedDraws++; }

                    rng = new Rng(seed);
                    var ints = o["ints"].Select(t => (int)t).ToList();
                    for (var i = 0; i < ints.Count; i++) Assert.That(rng.Int(stream, -3, 3 + i), Is.EqualTo(ints[i]), seed + "/" + prop.Name + " int " + i);

                    rng = new Rng(seed);
                    Assert.That(rng.Shuffle(stream, Enumerable.Range(0, 10).ToList()), Is.EqualTo(o["shuffle"].Select(t => (int)t).ToList()));

                    rng = new Rng(seed);
                    foreach (var ch in o["chances"])
                    {
                        var pctHundredths = (long)((decimal)ch["pct"] * 100m);
                        var hits = ch["hits"].Select(t => (bool)t).ToList();
                        for (var i = 0; i < hits.Count; i++) Assert.That(rng.Chance(stream, pctHundredths), Is.EqualTo(hits[i]), seed + "/" + prop.Name + " chance " + ch["pct"] + " #" + i);
                    }

                    rng = new Rng(seed, new Dictionary<RngStream, uint> { { stream, 1000 } });
                    foreach (var expected in o["afterRestore"]) Assert.That(rng.NextUInt(stream), Is.EqualTo((uint)(decimal)expected));
                }
            }
            Assert.That(checkedDraws, Is.EqualTo(15 * 14 * 64));
        }

        [Test]
        public void StreamsAreIndependent()
        {
            var a = new Rng(42);
            var b = new Rng(42);
            for (var i = 0; i < 10; i++) a.NextUInt(RngStream.Map);
            Assert.That(a.NextUInt(RngStream.Seats), Is.EqualTo(b.NextUInt(RngStream.Seats)));
            Assert.That(a.Counter(RngStream.Map), Is.EqualTo(10u));
        }

        [Test]
        public void CloneDoesNotAdvanceTheOriginal()
        {
            var rng = new Rng(7);
            rng.NextUInt(RngStream.Shop);
            var clone = rng.Clone();
            var previewed = clone.NextUInt(RngStream.Shop);
            Assert.That(rng.Counter(RngStream.Shop), Is.EqualTo(1u), "preview advanced the real stream");
            Assert.That(rng.NextUInt(RngStream.Shop), Is.EqualTo(previewed), "clone must see the same next value");
        }

        [Test]
        public void SeedCodecMatchesTheShippedFormatAndParse()
        {
            var rules = TestContent.ContentJson(ContentFiles.RulesRng)["seed"];
            var codec = new SeedCodec((string)rules["alphabet"],
                rules["homoglyphs"].Select(p => new KeyValuePair<string, string>((string)p[0], (string)p[1])).ToList(),
                (int)rules["maxLength"]);
            var oracle = TestContent.Oracle("seeds.json");
            foreach (var f in oracle["format"]) Assert.That(codec.Format((uint)(long)f["seed"]), Is.EqualTo((string)f["text"]));
            foreach (var p in oracle["parse"]) Assert.That(codec.Parse((string)p["text"]), Is.EqualTo((uint)(long)p["seed"]), (string)p["text"]);
            Assert.That(codec.Problem("AB-12"), Is.EqualTo('-'));
            Assert.That(codec.Problem("ab12"), Is.Null);
        }

        [Test]
        public void SeatOrderMatchesTheShippedDrawIncludingPinnedFirstSeat()
        {
            var oracle = TestContent.Oracle("seats.json");
            var seats = oracle["seats"].Select(s => new SeatInfo((string)s["id"], (int)s["baseTier"])).ToList();
            foreach (var c in oracle["cases"])
            {
                var seed = (uint)(long)c["seed"];
                Assert.That(SeatOrder.Draw(new Rng(seed), seats), Is.EqualTo(c["order"].Select(t => (string)t).ToList()), "seed " + seed);
                Assert.That(SeatOrder.Draw(new Rng(seed), seats, "marches"), Is.EqualTo(c["pinnedMarches"].Select(t => (string)t).ToList()), "pinned seed " + seed);
            }
        }

        [Test]
        public void SeatCatalogAgreesWithTheOracleSeats()
        {
            var catalog = TestContent.ContentJson(ContentFiles.CatalogSeats);
            var oracle = TestContent.Oracle("seats.json");
            foreach (var s in oracle["seats"]) Assert.That((int)catalog[(string)s["id"]]["baseTier"], Is.EqualTo((int)s["baseTier"]));
        }
    }
}
