using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using DeckKeys = Ashen.Generated.Core2DeckKeys;
using DeckValues = Ashen.Generated.Core2DeckValues;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-18 (D-190): the Core 2 deck editor rules. Tools/oracle-core2-deck.mjs drives the web's model/deckRules.js on the configured
    /// profile: every settings variant (bounds, problems, refusals for every deck size), per class the equipment eligibility, copy
    /// limit and basic classification of every registry card, 24 bot edit scenarios (sideboard moves incl. refused ones, rewards,
    /// begin / cancel / commit of valid and corrupt drafts, settings and range-policy switches) and the draw pile in deck order.
    /// <see cref="DeckRules"/> must give the same answer at every step and leave the same run document. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2DeckParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "deck");

        private static CoreDeckData _data;

        private static CoreDeckData Data
        {
            get
            {
                if (_data != null) return _data;
                var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
                return _data = Core2Registries.Load(snapshot.Content).ToCoreDeckData(snapshot.Content);
            }
        }

        private static JObject Read(string file) => (JObject)JsonContent.Parse(File.ReadAllText(Path.Combine(Dir, file)));

        private static JObject Index => Read("index.json");

        public static IEnumerable<string> EditFiles() => ((JArray)Index["edits"]).Select(e => e.Value<string>("file"));

        public static IEnumerable<string> ProbeFiles() => ((JArray)Index["probes"]).Select(t => t.Value<string>());

        public static IEnumerable<TestCaseData> SettingsCases() =>
            ((JArray)Read("settings.json")["cases"]).OfType<JObject>().Select((c, i) => new TestCaseData(c).SetName("Core2DeckSettings_" + i));

        public static IEnumerable<TestCaseData> OrderCases() =>
            ((JArray)Read("order.json")["cases"]).OfType<JObject>().Select((c, i) => new TestCaseData(c).SetName("Core2DeckOrder_" + i + "_" + c.Value<string>("classId")));

        /// <summary>The WP-3 run of a seed, every class unlocked (as the oracle created it).</summary>
        private static JObject Create(long seed, string classId)
        {
            var meta = new JObject
            {
                [Core2RunKeys.GameProfileUnlocks] = new JObject
                {
                    [Data.Run.ProfileId] = new JObject { [Core2RunKeys.CompletedRunIds] = new JArray(Enumerable.Range(0, Data.Run.Run.Classes.Count).Select(i => "r" + i)) },
                },
            };
            return CoreRunRules.Create(Data.Run, (uint)seed, classId, new CoreRunOptions { ProfileMeta = meta });
        }

        private static string Ref(JToken card) =>
            card.Value<string>("instanceId") + ":" + card.Value<string>("cardId") + (card.Value<bool?>("upgraded") == true ? "+" : string.Empty);

        private static JToken Wrap(Func<JToken> fn)
        {
            try { return fn(); }
            catch (Exception e) { return new JObject { ["error"] = e.Message }; }
        }

        /// <summary>The oracle writes infinity as the string "Infinity" (JSON has none).</summary>
        private static JToken Num(double value) => double.IsPositiveInfinity(value) ? new JValue("Infinity") : new JValue(value);

        private static void Check(string label, JToken want, JToken got)
        {
            var diff = RunParityTests.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail($"{label}: first difference at {diff}");
        }

        /// <summary>The run as the fixture holds it: no runId, the profile snapshot as its id and hash.</summary>
        private static JToken Document(JObject run)
        {
            var copy = (JObject)run.DeepClone();
            copy.Remove(Core2RunKeys.RunId);
            copy[Core2RunSnapshotKeys.GameProfile] = CoreRunRules.PinOf(run);
            return copy;
        }

        // ------------------------------------------------------------------ the oracle

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Index;
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(ProbeFiles().Count(), Is.EqualTo(Data.Run.Run.Classes.Count));
            Assert.That(EditFiles().Count(), Is.GreaterThanOrEqualTo(Data.Run.Run.Classes.Count * 6));
            Assert.That(index.Value<int>("steps"), Is.GreaterThanOrEqualTo(90));
            Assert.That(SettingsCases().Count(), Is.EqualTo(index.Value<int>("settings")));
        }

        // ------------------------------------------------------------------ settings

        [TestCaseSource(nameof(SettingsCases))]
        public void SettingsMatchTheWeb(JObject c)
        {
            var settings = (JObject)c["settings"];
            var bounds = Wrap(() =>
            {
                var b = DeckRules.EditBounds(Data, settings);
                return new JObject { ["min"] = Num(b.Min), ["max"] = Num(b.Max), ["problem"] = b.Problem };
            });
            Check("bounds " + settings.ToString(Newtonsoft.Json.Formatting.None), c["bounds"], bounds);
            Check("problems", c["problems"], Wrap(() => new JArray(DeckRules.SettingsProblems(Data, settings)
                .Select(p => new JObject { ["keys"] = new JArray(p.Keys), ["message"] = p.Message }))));
            Assert.That(DeckRules.EditingOn(Data, settings), Is.EqualTo(c.Value<bool>("editingOn")));
            Assert.That(DeckRules.EditingWhere(Data, settings), Is.EqualTo(c.Value<string>("editingWhere")));
            Assert.That(DeckRules.PlayInDeckOrder(Data, settings), Is.EqualTo(c.Value<bool>("inDeckOrder")));
            var refusals = (JArray)c["refusals"];
            for (var n = 0; n < refusals.Count; n++)
            {
                var count = n;
                Check("refusal at " + n, refusals[n], Wrap(() => new JValue(DeckRules.EditRefusal(Data, count, settings))));
            }
        }

        // ------------------------------------------------------------------ probes

        [TestCaseSource(nameof(ProbeFiles))]
        public void EligibilityAndLimitsMatchTheWeb(string file)
        {
            var probe = Read(file);
            var classId = probe.Value<string>("classId");
            var run = Create(probe.Value<long>("seed"), classId);
            Assert.That(run.Arr(DeckKeys.Deck).Select(Ref), Is.EqualTo(probe["deck"].Select(t => t.Value<string>())), "the run is the one the oracle probed");
            Check("basicCardIds", probe["basicCardIds"], new JArray(DeckRules.BasicCardIds(Data, run)));
            Check("unlimitedCardIds", probe["unlimitedCardIds"], new JArray(DeckRules.UnlimitedCardIds(Data, run)));
            Assert.That(DeckRules.IsEquippedRun(run), Is.EqualTo(probe.Value<bool>("isEquippedRun")));
            Check("owned", probe["owned"], new JObject(run.Arr(DeckKeys.Deck).Select(c => c.Value<string>("cardId")).Distinct()
                .Select(id => new JProperty(id, DeckRules.OwnedCopies(run, id)))));

            var rangeOff = (JObject)run.DeepClone();
            rangeOff[DeckKeys.RangePolicy][DeckKeys.Enabled] = false;
            var legacySettings = ((JArray)probe["legacySettings"]).OfType<JObject>().ToList();
            Func<string, JToken> legacy = id => new JArray(legacySettings.Select(s => Wrap(() => Num(DeckRules.CopyLimit(Data, id, s, classId)))));
            foreach (var p in ((JObject)probe["cards"]).Properties())
            {
                var id = p.Name;
                var want = (JObject)p.Value;
                var instance = new JObject { [DeckKeys.InstanceId] = "probe:" + id, [DeckKeys.CardId] = id, [DeckKeys.Upgraded] = false };
                Check(id + " eligible", want["eligible"], Wrap(() => new JValue(DeckRules.CardEquipmentEligible(Data, run, instance))));
                Check(id + " eligible range off", want["eligibleRangeOff"], Wrap(() => new JValue(DeckRules.CardEquipmentEligible(Data, rangeOff, instance))));
                Check(id + " limit", want["limit"], Wrap(() => Num(DeckRules.CopyLimit(Data, id, new JObject(), run))));
                Assert.That(DeckRules.IsBasicCard(Data, instance, run), Is.EqualTo(want.Value<bool>("basic")), id + " basic");
                Assert.That(DeckRules.IsUnlimitedBasic(Data, instance, run), Is.EqualTo(want.Value<bool>("unlimitedBasic")), id + " unlimited basic");
                Check(id + " class-string limit", want["legacy"], legacy(id));
            }
            foreach (var other in ((JArray)probe["otherClassLegacy"]).OfType<JObject>())
                Check(other.Value<string>("id") + " class-string limit", other["limits"], legacy(other.Value<string>("id")));
            foreach (var lent in ((JArray)probe["lent"]).OfType<JObject>())
            {
                var card = (JObject)lent["card"];
                Check(card.Value<string>("instanceId") + " eligible", lent["eligible"], Wrap(() => new JValue(DeckRules.CardEquipmentEligible(Data, run, card))));
                Assert.That(DeckRules.IsBasicCard(Data, card, run), Is.EqualTo(lent.Value<bool>("basic")));
                Assert.That(DeckRules.IsUnlimitedBasic(Data, card, run), Is.EqualTo(lent.Value<bool>("unlimitedBasic")));
                Assert.That(DeckRules.IsSetAsideBasic(Data, card, run), Is.EqualTo(lent.Value<bool>("setAside")));
            }
            foreach (var variant in ((JArray)probe["variants"]).OfType<JObject>())
            {
                var card = (JObject)variant["card"];
                Assert.That(DeckRules.IsBasicCard(Data, card, run), Is.EqualTo(variant.Value<bool>("basic")));
                Assert.That(DeckRules.IsUnlimitedBasic(Data, card, run), Is.EqualTo(variant.Value<bool>("unlimitedBasic")));
                Assert.That(DeckRules.IsSetAsideBasic(Data, card, run), Is.EqualTo(variant.Value<bool>("setAside")));
            }
        }

        // ------------------------------------------------------------------ the editing bot

        private static JObject State(JObject run)
        {
            var keys = new JObject();
            foreach (var key in new[] { DeckKeys.EquipmentAttackSlotCount, DeckKeys.RemovedAttackSlotIds, DeckKeys.EditMintCounter })
                if (run[key] != null) keys[key] = run[key].DeepClone();
            return new JObject
            {
                ["deck"] = new JArray(run.Arr(DeckKeys.Deck).Select(Ref)),
                ["sideboard"] = new JArray((run.Arr(DeckKeys.Sideboard) ?? new JArray()).Select(Ref)),
                ["keys"] = keys,
            };
        }

        [TestCaseSource(nameof(EditFiles))]
        public void AnEditingSessionMatchesTheWeb(string file)
        {
            var log = Read(file);
            var settingsList = ((JArray)log["settings"]).OfType<JObject>().ToList();
            var run = Create(log.Value<long>("seed"), log.Value<string>("classId"));
            Assert.That(run.Arr(DeckKeys.Deck).Select(Ref), Is.EqualTo(log["start"].Select(t => t.Value<string>())), file + " start");
            var settingsIndex = log.Value<int>("settingsStart");
            JObject snapshot = null;
            var stepNo = 0;
            foreach (var step in ((JArray)log["steps"]).OfType<JObject>())
            {
                stepNo++;
                var label = $"{file} step {stepNo} {step.Value<string>("op")}";
                var settings = settingsList[settingsIndex];
                switch (step.Value<string>("op"))
                {
                    case "toSideboard":
                        Check(label, step["result"], Wrap(() => new JValue(DeckRules.MoveToSideboard(Data, run, step.Value<string>("id")))));
                        break;
                    case "fromSideboard":
                        Check(label, step["result"], Wrap(() => new JValue(DeckRules.MoveFromSideboard(Data, run, step.Value<string>("id"), settings))));
                        break;
                    case "reward":
                    {
                        // The web builds a reward as { instanceId, cardId, upgraded }: key order is part of the JSON compare a commit makes.
                        var wire = (JObject)step["card"];
                        var card = new JObject { [DeckKeys.InstanceId] = wire[DeckKeys.InstanceId], [DeckKeys.CardId] = wire[DeckKeys.CardId], [DeckKeys.Upgraded] = wire[DeckKeys.Upgraded] };
                        Assert.That(DeckRules.UnusedInstanceId(run, "rw", card.Value<string>("cardId")), Is.EqualTo(card.Value<string>("instanceId")), label);
                        if (step.Value<string>("to") == "deck") run.Arr(DeckKeys.Deck).Add(card.DeepClone());
                        else
                        {
                            if (!(run[DeckKeys.Sideboard] is JArray pile)) run[DeckKeys.Sideboard] = pile = new JArray();
                            pile.Add(card.DeepClone());
                        }
                        break;
                    }
                    case "begin":
                        snapshot = DeckRules.BeginEdit(Data, run);
                        Check(label, step["snapshot"], snapshot);
                        break;
                    case "cancel":
                        Assert.That(snapshot != null, Is.EqualTo(step.Value<bool>("had")), label);
                        if (snapshot != null)
                        {
                            DeckRules.CancelEdit(Data, run, snapshot);
                            snapshot = null;
                        }
                        break;
                    case "commit":
                    {
                        var draft = (JObject)JsonContent.Parse(step.Value<string>("draftJson"));
                        var before = State(run);
                        var result = Wrap(() => new JObject { ["count"] = DeckRules.CommitDraft(Data, run, draft, settings) });
                        Check(label + " draft " + step["edits"].ToString(Newtonsoft.Json.Formatting.None) + " got " + result.ToString(Newtonsoft.Json.Formatting.None), step["result"], result);
                        if (result["error"] != null) Check(label + " (a refused draft leaves the run alone)", before, State(run));
                        break;
                    }
                    case "settings":
                        settingsIndex = step.Value<int>("index");
                        break;
                    case "range":
                        run[DeckKeys.RangePolicy][DeckKeys.Enabled] = step.Value<bool>("enabled");
                        break;
                    default:
                        Assert.Fail(label + ": unknown op");
                        break;
                }
                settings = settingsList[settingsIndex];
                Check(label + " state", step["after"], State(run));
                var limits = new JObject();
                foreach (var id in run.Arr(DeckKeys.Deck).Concat(run.Arr(DeckKeys.Sideboard) ?? new JArray()).Select(c => c.Value<string>("cardId")).Distinct())
                    limits[id] = Wrap(() => Num(DeckRules.CopyLimit(Data, id, settings, run)));
                Check(label + " limits", step["limits"], limits);
                var eligible = new JObject();
                foreach (var c in (run.Arr(DeckKeys.Sideboard) ?? new JArray()).OfType<JObject>())
                    eligible[c.Value<string>("instanceId")] = Wrap(() => new JValue(DeckRules.CardEquipmentEligible(Data, run, c)));
                Check(label + " eligible", step["eligible"], eligible);
                Check(label + " refusal", step["refusal"], Wrap(() => new JValue(DeckRules.EditRefusal(Data, run.Arr(DeckKeys.Deck).Count, settings))));
            }
            Check(file + " run document", log["run"], Document(run));
        }

        // ------------------------------------------------------------------ the draw pile in deck order

        private static bool Innate(JObject card) =>
            Js.Items(Cards.Resolve(Data.Run.Run.Combat, card)[DeckKeys.Keywords]).Select(Js.Str).Contains(DeckValues.InnateKeyword);

        private static JObject FromRef(string r)
        {
            var colon = r.LastIndexOf(':');
            return new JObject { [DeckKeys.InstanceId] = r.Substring(0, colon), [DeckKeys.CardId] = r.Substring(colon + 1).TrimEnd('+'), [DeckKeys.Upgraded] = false };
        }

        private static IEnumerable<string> Ids(IEnumerable<JObject> cards) => cards.Select(c => c.Value<string>(DeckKeys.InstanceId));

        [TestCaseSource(nameof(OrderCases))]
        public void TheDrawPileInDeckOrderMatchesTheWeb(JObject c)
        {
            var deck = c["deck"].Select(t => FromRef(t.Value<string>())).ToList();
            var shuffled = c["shuffled"].Select(t => FromRef(t.Value<string>())).ToList();
            Assert.That(shuffled.Where(Innate).Select(x => x.Value<string>(DeckKeys.InstanceId)), Is.EqualTo(c["innate"].Select(t => t.Value<string>())));
            Assert.That(Ids(DeckRules.OrderedDrawPile(shuffled, Innate)), Is.EqualTo(c["drawPile"].Select(t => t.Value<string>())));
            Assert.That(Ids(DeckRules.OrderedDrawPile(deck, Innate)), Is.EqualTo(c["deckOrderPile"].Select(t => t.Value<string>())));
            var discard = c["discard"].Select(t => new JObject { [DeckKeys.InstanceId] = t.Value<string>() }).ToList();
            Assert.That(Ids(DeckRules.OrderedReturn(discard, deck.Select(x => x.Value<string>(DeckKeys.InstanceId)))), Is.EqualTo(c["returned"].Select(t => t.Value<string>())));
        }

        // ------------------------------------------------------------------ properties of the port

        [Test]
        public void AnEditSessionCancelsToTheExactRun()
        {
            var classId = Data.Run.Run.Classes.Ids.First();
            var run = Create(1, classId);
            var before = run.ToString(Newtonsoft.Json.Formatting.None);
            var snapshot = DeckRules.BeginEdit(Data, run);
            var first = run.Arr(DeckKeys.Deck)[0].Value<string>(DeckKeys.InstanceId);
            Assert.That(DeckRules.MoveToSideboard(Data, run, first), Is.True);
            Assert.That(run.Arr(DeckKeys.Sideboard).Count, Is.EqualTo(1));
            DeckRules.CancelEdit(Data, run, snapshot);
            Assert.That(run.ToString(Newtonsoft.Json.Formatting.None), Is.EqualTo(before));
        }

        [Test]
        public void ARefusedMoveLeavesTheRunAlone()
        {
            var run = Create(1, Data.Run.Run.Classes.Ids.First());
            var before = run.ToString(Newtonsoft.Json.Formatting.None);
            Assert.That(DeckRules.MoveToSideboard(Data, run, "missing"), Is.False);
            Assert.That(DeckRules.MoveFromSideboard(Data, run, "missing"), Is.False);
            Assert.That(run.ToString(Newtonsoft.Json.Formatting.None), Is.EqualTo(before));
        }
    }
}
