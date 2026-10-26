using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using DeckKeys = Ashen.Generated.Core2DeckKeys;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-18 (D-401): the deck editor session and its projection. Tools/oracle-core2-deck.mjs drives the web's openDeckEdit and deckEditorModel
    /// (ui/models/DeckEditorModel.js) over real Core 2 runs: a bot opens editors, adds from the collection tiles (and from keys no tile offers),
    /// removes, places, undoes, confirms and cancels while rewards land and the settings and range policy switch. After each step the answer
    /// ({ ok, refusal } or the thrown message), the whole projection under a view (counter, cost curve, deck rows, collection tiles, filter chips,
    /// sorts), the run's piles and editor fields, the session flags and the doors must equal the web's. The words come from strings/app.en.json.
    /// Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2DeckEditorParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "deck");

        private static CoreDeckData _data;
        private static DeckEditorText _text;

        private static void Load()
        {
            if (_data != null) return;
            var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
            _data = Core2Registries.Load(snapshot.Content).ToCoreDeckData(snapshot.Content);
            _text = new DeckEditorText(new StringTable((JObject)snapshot.Content.Get(ContentFiles.StringsAppEn)));
        }

        private static CoreDeckData Data { get { Load(); return _data; } }

        private static JObject Read(string file) => (JObject)JsonContent.Parse(File.ReadAllText(Path.Combine(Dir, file)));

        public static IEnumerable<string> EditorFiles() =>
            ((JArray)Read("index.json")["editors"]).Select(e => e.Value<string>("file"));

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

        private static void Check(string label, JToken want, JToken got)
        {
            var diff = OracleCompare.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail($"{label}: first difference at {diff}");
        }

        // ------------------------------------------------------------------ the projection as JSON (the web's shape)

        private static JToken Num(double value) => double.IsPositiveInfinity(value) ? new JValue("Infinity") : new JValue(value);

        private static JObject CardFields(DeckCardRow row)
        {
            var o = new JObject { ["cardId"] = row.CardId, ["name"] = row.Name };
            if (row.CostRaw != null) o["cost"] = row.CostRaw;
            o["costBucket"] = row.CostBucket;
            if (row.Type != null) o["type"] = row.Type;
            o["source"] = row.Source;
            o["upgraded"] = row.Upgraded;
            return o;
        }

        private static JObject RowJson(DeckRow row)
        {
            var o = CardFields(row);
            o["instanceId"] = row.InstanceId;
            o["tileKey"] = row.TileKey;
            o["index"] = row.Index;
            o["locked"] = row.Locked;
            o["lockText"] = string.Empty;
            o["lockSentence"] = string.Empty;
            o["removable"] = row.Removable;
            o["groupKey"] = row.GroupKey;
            o["instanceIds"] = new JArray(row.InstanceIds);
            o["count"] = row.Count;
            o["countText"] = row.CountText;
            o["canUp"] = row.CanUp;
            o["canDown"] = row.CanDown;
            return o;
        }

        private static JObject TileJson(DeckTile tile)
        {
            var o = CardFields(tile);
            o["key"] = tile.Key;
            o["unlimited"] = tile.Unlimited;
            o["owned"] = Num(tile.Owned);
            o["inDeck"] = tile.InDeck;
            o["countText"] = tile.CountText;
            if (tile.EquipmentEligible.HasValue) o["equipmentEligible"] = tile.EquipmentEligible.Value;
            o["addable"] = tile.Addable;
            o["refusal"] = tile.Refusal.Text;
            o["removeInstanceId"] = tile.RemoveInstanceId == null ? JValue.CreateNull() : new JValue(tile.RemoveInstanceId);
            o["removable"] = tile.Removable;
            return o;
        }

        private static JArray Chips(IEnumerable<DeckChip> chips) =>
            new JArray(chips.Select(c => new JObject { ["id"] = c.Id, ["label"] = c.Label, ["on"] = c.On }));

        private static JObject ModelJson(DeckEditorModelData m) => new JObject
        {
            ["counter"] = new JObject { ["count"] = m.Counter.Count, ["min"] = Num(m.Counter.Min), ["max"] = Num(m.Counter.Max), ["text"] = m.Counter.Text, ["outOfBounds"] = m.Counter.OutOfBounds, ["refusal"] = m.Counter.Refusal },
            ["done"] = new JObject { ["disabled"] = m.DoneDisabled, ["refusal"] = m.DoneRefusal },
            ["curve"] = new JArray(m.Curve.Select(b => new JObject { ["bucket"] = b.Bucket, ["count"] = b.Count, ["share"] = b.Share })),
            ["ordered"] = m.Ordered,
            ["deck"] = new JArray(m.Deck.Select(RowJson)),
            ["collection"] = new JArray(m.Collection.Select(TileJson)),
            ["filters"] = new JObject
            {
                ["type"] = Chips(m.FilterType), ["cost"] = Chips(m.FilterCost), ["source"] = Chips(m.FilterSource),
                ["upgraded"] = new JObject { ["label"] = m.FilterUpgraded.Label, ["on"] = m.FilterUpgraded.On },
            },
            ["sorts"] = Chips(m.Sorts),
            ["view"] = ViewJson(m.View),
        };

        private static JObject ViewJson(DeckEditorView v) => new JObject
        {
            ["filters"] = new JObject { ["type"] = new JArray(v.Type), ["cost"] = new JArray(v.Cost), ["source"] = new JArray(v.Source), ["upgraded"] = v.Upgraded },
            ["sort"] = v.Sort,
        };

        private static DeckEditorView ViewOf(JToken json)
        {
            var filters = json?["filters"] as JObject;
            return new DeckEditorView
            {
                Type = filters?["type"]?.Select(t => t.Value<string>()).ToList() ?? new List<string>(),
                Cost = filters?["cost"]?.Select(t => t.Value<string>()).ToList() ?? new List<string>(),
                Source = filters?["source"]?.Select(t => t.Value<string>()).ToList() ?? new List<string>(),
                Upgraded = filters?["upgraded"]?.Value<bool>() ?? false,
                Sort = json?["sort"]?.Value<string>() ?? "cost",
            };
        }

        // ------------------------------------------------------------------ the run

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

        private static JToken Document(JObject run)
        {
            var copy = (JObject)run.DeepClone();
            copy.Remove(Core2RunKeys.RunId);
            copy[Core2RunSnapshotKeys.GameProfile] = CoreRunRules.PinOf(run);
            return copy;
        }

        private static void Reward(JObject run, JObject wire, string to)
        {
            // The web builds a reward as { instanceId, cardId, upgraded }: key order is part of the JSON compare a commit makes.
            var card = new JObject { [DeckKeys.InstanceId] = wire[DeckKeys.InstanceId], [DeckKeys.CardId] = wire[DeckKeys.CardId], [DeckKeys.Upgraded] = wire[DeckKeys.Upgraded] };
            Assert.That(DeckRules.UnusedInstanceId(run, "rw", card.Value<string>("cardId")), Is.EqualTo(card.Value<string>("instanceId")), "reward id");
            if (to == "deck") run.Arr(DeckKeys.Deck).Add(card);
            else
            {
                if (!(run[DeckKeys.Sideboard] is JArray pile)) run[DeckKeys.Sideboard] = pile = new JArray();
                pile.Add(card);
            }
        }

        /// <summary>What the answer comes to: the flag and the sentence, or the thrown message.</summary>
        private static JObject Answer(Func<DeckEditResult> call)
        {
            try
            {
                var r = call();
                return new JObject { ["ok"] = r.Ok, ["refusal"] = r.Refusal.Text };
            }
            catch (Exception e)
            {
                return new JObject { ["error"] = e.Message };
            }
        }

        private static JObject WebAnswer(JToken result) =>
            result["error"] != null ? new JObject { ["error"] = result["error"] } : new JObject { ["ok"] = result["ok"], ["refusal"] = result["refusal"]?.Value<string>() ?? string.Empty };

        // ------------------------------------------------------------------ the oracle

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Read("index.json");
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(EditorFiles().Count(), Is.GreaterThanOrEqualTo(Data.Run.Run.Classes.Count * 2));
        }

        [TestCaseSource(nameof(EditorFiles))]
        public void AnEditorSessionMatchesTheWeb(string file)
        {
            var log = Read(file);
            var settingsList = ((JArray)log["settings"]).OfType<JObject>().ToList();
            var run = Create(log.Value<long>("seed"), log.Value<string>("classId"));
            Assert.That(run.Arr(DeckKeys.Deck).Select(Ref), Is.EqualTo(log["start"].Select(t => t.Value<string>())), file + " start");
            var settingsIndex = log.Value<int>("settingsStart");
            var session = DeckEditor.Open(Data, _text, run, settingsList[settingsIndex]);
            var view = new DeckEditorView();
            var n = 0;
            foreach (var step in ((JArray)log["steps"]).OfType<JObject>())
            {
                n++;
                var op = step.Value<string>("op");
                var label = $"{file} step {n} {op}";
                var pre = (JObject)step["pre"];
                if (pre.Value<bool>("reopened")) session = DeckEditor.Open(Data, _text, run, settingsList[settingsIndex]);
                foreach (var reward in ((JArray)pre["rewards"]).OfType<JObject>()) Reward(run, (JObject)reward["card"], reward.Value<string>("to"));
                switch (op)
                {
                    case "add":
                        Check(label, WebAnswer(step["result"]), Answer(() => session.Add(step.Value<string>("key"))));
                        break;
                    case "remove":
                        Check(label, WebAnswer(step["result"]), Answer(() => session.Remove(step.Value<string>("id"))));
                        break;
                    case "moveTo":
                        Check(label, WebAnswer(step["result"]), Answer(() => session.MoveTo(step.Value<string>("id"), step.Value<int>("index"))));
                        break;
                    case "move":
                        Check(label, WebAnswer(step["result"]), Answer(() => session.Move(step.Value<string>("id"), step.Value<int>("delta"))));
                        break;
                    case "undo":
                        Check(label, WebAnswer(step["result"]), Answer(() => session.Undo()));
                        break;
                    case "confirm":
                        Check(label, WebAnswer(step["result"]), Answer(() => session.Confirm()));
                        break;
                    case "cancel":
                        Check(label, WebAnswer(step["result"]), Answer(() => { session.Cancel(); return new DeckEditResult(true); }));
                        break;
                    case "open":
                        session = DeckEditor.Open(Data, _text, run, settingsList[settingsIndex]);
                        break;
                    case "noop":
                        break;
                    case "reward":
                        Reward(run, (JObject)step["card"], step.Value<string>("to"));
                        break;
                    case "settings":
                        settingsIndex = step.Value<int>("index");
                        session = DeckEditor.Open(Data, _text, run, settingsList[settingsIndex]);
                        break;
                    case "range":
                        run[DeckKeys.RangePolicy][DeckKeys.Enabled] = step.Value<bool>("enabled");
                        break;
                    case "view":
                        view = ViewOf(step["view"]);
                        break;
                    default:
                        Assert.Fail(label + ": unknown op");
                        break;
                }
                var settings = settingsList[settingsIndex];
                Assert.That(settingsIndex, Is.EqualTo(step.Value<int>("settingsIndex")), label + " settings index");
                Assert.That(session.CanUndo, Is.EqualTo(step.Value<bool>("canUndo")), label + " canUndo");
                Assert.That(session.Closed, Is.EqualTo(step.Value<bool>("closed")), label + " closed");
                Check(label + " state", step["after"], State(run));
                var got = ModelJson(DeckEditor.Model(Data, _text, run, settings, ViewOf(step["view"])));
                Check(label + " model" + Order(step["model"], got), step["model"], got);
                // The Deck key and pad Y (web nextFilterPreset): the view one press on from this one.
                var live = DeckEditor.Model(Data, _text, run, settings, ViewOf(step["view"]));
                Check(label + " next filter preset", step["nextPreset"], ViewJson(DeckEditor.NextFilterPreset(live, ViewOf(step["view"])).Normalized()));
                var doors = new JArray(
                    DoorsJson(DeckEditor.Doors(Data, settings, false, false)),
                    DoorsJson(DeckEditor.Doors(Data, settings, true, false)),
                    DoorsJson(DeckEditor.Doors(Data, settings, false, true)));
                Check(label + " doors", step["doors"], doors);
            }
            Check(file + " run document", log["run"], Document(run));
        }

        /// <summary>On a failure the order of the rows tells more than the first difference: both lists, side by side.</summary>
        private static string Order(JToken want, JToken got) =>
            "\n web  deck " + string.Join(", ", want["deck"].Select(r => r.Value<string>("name"))) + "\n here deck " + string.Join(", ", got["deck"].Select(r => r.Value<string>("name")))
            + "\n web  coll " + string.Join(", ", want["collection"].Select(r => r.Value<string>("name") + "/" + r.Value<string>("cost"))) + "\n here coll " + string.Join(", ", got["collection"].Select(r => r.Value<string>("name") + "/" + r.Value<string>("cost")));

        private static JObject DoorsJson(DeckDoors doors) => new JObject { ["armoury"] = doors.Armoury, ["rest"] = doors.Rest };

        // ------------------------------------------------------------------ the browser's collation

        [Test]
        public void LocaleCompareSortsTheNamesAsTheBrowserDoes()
        {
            var c = Read("collation.json");
            var names = c["names"].Select(t => t.Value<string>()).ToList();
            var sorted = names.OrderBy(x => x, Comparer<string>.Create(DeckEditor.LocaleCompare)).ToList();
            Assert.That(sorted, Is.EqualTo(c["sorted"].Select(t => t.Value<string>()).ToList()));
            foreach (var pair in ((JArray)c["pairs"]).OfType<JArray>())
                Assert.That(Math.Sign(DeckEditor.LocaleCompare(pair[0].Value<string>(), pair[1].Value<string>())), Is.EqualTo(pair[2].Value<int>()), pair[0] + " vs " + pair[1]);
        }
    }
}
