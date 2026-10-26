using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// VP-15 (D-1100..): what the combat screen's cards and board read beyond the first pass, held without the editor. The card's text says which numbers the preview moved off
    /// their printed value (the face tints them up and down), the face reads the stance entry's Precision and Evade as counted tags, and a hand card that mirrors a basic card
    /// carries it. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class CombatFidelityTests
    {
        private string _dir;
        private SaveService _saves;
        private static readonly DateTime Epoch = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        private static UiData _ui;

        private static RunContent Content => Core2HostParityTests.Content;
        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-fidelity-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private Core2CombatFlow Fight()
        {
            var session = Core2RunSession.New(Content, _saves, 1, 31415, "reaver", "Aldric", () => Epoch);
            session.EditRunForReview(r => { r["maxHp"] = 300; r["hp"] = 300; });
            session.Travel(session.ReachableNodes()[0]);
            return new Core2CombatFlow(session, Ui.Strings);
        }

        [Test]
        public void ANumberTheTokenMovedAboveItsPrintedValueIsUpAndBelowIsDown()
        {
            var tokens = new JObject { ["damage"] = 12, ["block"] = 3, ["hits"] = 2 };
            var printed = new JObject { ["damage"] = 6, ["block"] = 5, ["hits"] = 2 };
            var shifts = new List<TextShift>();
            var text = CombatViewModel.ResolveText("Deal {damage} damage twice, gain {block} Block, hit {hits} times.", tokens, printed, shifts);
            Assert.That(text, Is.EqualTo("Deal 12 damage twice, gain 3 Block, hit 2 times."));
            Assert.That(shifts.Count, Is.EqualTo(2), "an unmoved number is plain");
            Assert.That(text.Substring(shifts[0].Start, shifts[0].Length), Is.EqualTo("12"));
            Assert.That(shifts[0].Direction, Is.EqualTo(1));
            Assert.That(text.Substring(shifts[1].Start, shifts[1].Length), Is.EqualTo("3"));
            Assert.That(shifts[1].Direction, Is.EqualTo(-1));
        }

        [Test]
        public void ATokenWithNoNumericPrintedValueOrNoPreviewIsLeftPlain()
        {
            var tokens = new JObject { ["damage"] = 12 };
            var shifts = new List<TextShift>();
            Assert.That(CombatViewModel.ResolveText("Deal {damage}.", tokens, new JObject { ["damage"] = "X" }, shifts), Is.EqualTo("Deal 12."));
            Assert.That(shifts, Is.Empty);
            Assert.That(CombatViewModel.ResolveText("Deal {damage}.", tokens, null, shifts), Is.EqualTo("Deal 12."));
            Assert.That(CombatViewModel.ResolveText("Deal {nothing}.", tokens, null, shifts), Is.EqualTo("Deal {nothing}."), "an unknown token stays as written");
            Assert.That(shifts, Is.Empty);
        }

        [Test]
        public void AHandCardsShiftsAreTheNumbersItsPreviewMoved()
        {
            var flow = Fight();
            var hand = flow.View.Hand;
            Assert.That(hand, Is.Not.Empty);
            var moved = 0;
            foreach (var card in hand)
            {
                foreach (var shift in card.TextShifts)
                {
                    var shown = card.Text.Substring(shift.Start, shift.Length);
                    Assert.That(double.TryParse(shown, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _), Is.True, card.Name + ": " + shown);
                    Assert.That(shift.Direction, Is.AnyOf(-1, 1));
                    moved++;
                }
            }
            Assert.That(moved, Is.GreaterThan(0), "the Reaver's Strike reads above its printed damage");
        }

        [Test]
        public void ACardInAPileIsNotPricedSoItsTextIsPlain()
        {
            var flow = Fight();
            foreach (var card in Core2CombatView.Pile(flow.Session, OverlayValues.PileDraw)) Assert.That(card.TextShifts, Is.Empty, card.Name);
        }

        [Test]
        public void ASmashReadsItsStanceEntryPrecisionAsACountedTag()
        {
            var flow = Fight();
            var smash = flow.View.Hand.Concat(Core2CombatView.Pile(flow.Session, OverlayValues.PileDraw)).FirstOrDefault(c => c.Tags.Contains("maneuver:smash") && c.Effects.Any(e => e.Op == "damage"));
            if (smash == null) Assert.Ignore("this deal holds no Smash");
            Assert.That(smash.EntryPrecision, Is.GreaterThan(0), "the Core rules give a stance entry its Precision");
            var plan = Ui.CardFace.Plan(smash.Face);
            var precision = Ui.CardFace.Glyph("precision");
            Assert.That(plan.Rail, Does.Contain(precision));
            Assert.That(plan.RailCounts[precision], Is.EqualTo((int)smash.EntryPrecision.Value));
            Assert.That(plan.Rail.Last(), Is.EqualTo("smash"), "the Smash tag follows the counted benefits");
        }

        // ------------------------------------------------------------------ the card inspector's marks and rule hints

        private static Core2CardView Card(string type, params string[] tags) => new Core2CardView { CardId = "x", Type = type, Tags = tags.ToList(), Rules = new Core2CardRules() };

        private static List<CardHint> Hints(Core2CardView card) => Core2CardHints.Hints(Ui.Strings, Ui.CardFace, card);

        [Test]
        public void ACounterReadsItsReturnHelpItsStanceGroupsAndItsReturnRange()
        {
            var hints = Hints(Card("attack", "camp:physical", "maneuver:counter", "range:reach"));
            Assert.That(hints.Select(h => h.Lead), Is.EqualTo(new[] { null, "Stance group:", "Return range:" }));
            Assert.That(hints[0].Text, Does.StartWith("Prepare counter until next player turn."));
            Assert.That(hints[1].Text, Is.EqualTo("Offense + Defensive."));
            Assert.That(hints[2].Text, Does.StartWith("Reach. Return misses out of range"));
        }

        [Test]
        public void ACounterWithCoverageNamesWhatItCovers()
        {
            var card = Card("attack", "camp:physical", "maneuver:counter");
            card.Rules.CounterCoverage = JObject.Parse("{\"camps\":[\"physical\"],\"ranges\":[\"melee\",\"reach\"],\"targeting\":[\"single\"],\"effects\":[\"damage\"]}");
            var hint = Hints(card)[0];
            Assert.That(hint.Lead, Is.EqualTo("Counter coverage:"));
            Assert.That(hint.Text, Is.EqualTo("Martial \u00b7 Melee + Reach \u00b7 Single \u00b7 damage. The prepared stance remains until another maneuver replaces it."));
        }

        [Test]
        public void AnEvadeIsDefensiveASweepOffensiveAndAReactionSweepSaysSo()
        {
            Assert.That(Hints(Card("skill", "camp:physical", "maneuver:evade")).Single(h => h.Lead == "Stance group:").Text, Is.EqualTo("Defensive."));
            var sweep = Hints(Card("attack", "camp:physical", "maneuver:sweep"));
            Assert.That(sweep.Single().Text, Is.EqualTo("Offense."));
            var reaction = Hints(Card("attack", "camp:physical", "maneuver:sweep", "trait:reaction"));
            Assert.That(reaction.Select(h => h.Lead), Is.EqualTo(new[] { "Stance group:", null }));
            Assert.That(reaction[1].Text, Does.StartWith("Reaction Sweep resolves before the incoming attack."));
            var ranked = Card("attack", "camp:physical", "maneuver:sweep");
            ranked.Rank = 2;
            Assert.That(Hints(ranked).Single().Text, Is.EqualTo("Offense + Defensive."), "a rank 2 sweep also guards");
        }

        [Test]
        public void ASpellOrASkillWithNoStanceHasNoStanceHintsAndADefenseRangeCoversEveryRangeUpToIt()
        {
            Assert.That(Hints(Card("attack", "camp:spell", "maneuver:casting", "school:fire")), Is.Empty);
            var guarded = Card("skill", "camp:physical", "maneuver:defend");
            guarded.Rules.DefenseRange = "reach";
            var hint = Hints(guarded).First();
            Assert.That(hint.Lead, Is.EqualTo("Defense range:"));
            Assert.That(hint.Text, Is.EqualTo("Reach \u00b7 Covers Melee + Reach."));
        }

        [Test]
        public void TheInspectorExplainsEachMarkTheCardWears()
        {
            var cantrip = Card("attack", "camp:spell", "maneuver:casting", "school:fire");
            cantrip.Mirrors = "attack";
            var rows = Core2CardHints.Sigils(Ui.Strings, Ui.CardFace, cantrip);
            Assert.That(rows.Select(r => r.Label), Is.EqualTo(new[] { "Spell", "Attack", "Fire" }));
            Assert.That(rows.Select(r => r.School), Is.EqualTo(new[] { false, false, true }));
            Assert.That(rows[0].Help, Does.StartWith("A spell."));
            var earth = Core2CardHints.Sigils(Ui.Strings, Ui.CardFace, Card("attack", "camp:spell", "maneuver:casting", "school:earth"));
            Assert.That(earth.Last().Id, Is.EqualTo("alteration"), "earth has no mark of its own: it is an alteration school");
            var strike = Core2CardHints.Sigils(Ui.Strings, Ui.CardFace, Card("attack", "camp:physical", "maneuver:attack"));
            Assert.That(strike.Select(r => r.Label), Is.EqualTo(new[] { "Attack" }));
            foreach (var row in rows.Concat(earth))
                Assert.That(System.IO.File.Exists(System.IO.Path.Combine(TestContent.RepoRoot, "Unity", "Assets", "Game", "Presentation", "UI", "Resources", "UI", "CardFace", (row.School ? "school-" : "sigil-") + row.Id + ".png")), Is.True, row.Id);
        }

        // ------------------------------------------------------------------ tooltips of the board's small marks

        [Test]
        public void AStatusTooltipFillsItsTokensFromItsOwnRow()
        {
            var def = JObject.Parse("{\"tooltip\":\"At {proc.threshold}, burst for {proc.burstPercent}% (min {proc.burstMin}). Hardened {resists.percent}% for {decay.duration} turns; {tv.pct}% more; {onEnter.0.amount}; {hooks.0.do.1.amount}; {unknown}.\"," +
                "\"proc\":{\"threshold\":6,\"burstPercent\":12,\"burstMin\":4},\"resists\":{\"percent\":50},\"decay\":{\"duration\":3},\"taggedVulnerability\":{\"mult\":1.25},\"onEnter\":[{\"amount\":2}],\"hooks\":[{\"do\":[{\"amount\":1},{\"amount\":9}]}]}");
            Assert.That(Core2Readings.StatusText(def), Is.EqualTo("At 6, burst for 12% (min 4). Hardened 50% for 3 turns; 25% more; 2; 9; {unknown}."));
            Assert.That(Core2Readings.StatusText(new JObject()), Is.Empty);
        }

        [Test]
        public void AStatusChipNamesItsStacksTurnsLeftAndBuildUp()
        {
            var plain = Core2Readings.Status(Ui.Strings, new Core2StatusView { Name = "Weak", Stacks = 2, Tooltip = "Deals less damage." });
            Assert.That(plain.Title, Is.EqualTo("Weak \u00d72"));
            Assert.That(plain.Body, Is.EqualTo("Deals less damage."));
            var timed = Core2Readings.Status(Ui.Strings, new Core2StatusView { Name = "Frail", Stacks = 1, Duration = 1, Tooltip = "Less Block." });
            Assert.That(timed.Body, Is.EqualTo("Less Block. Turns left: 1."));
            var meter = Core2Readings.Status(Ui.Strings, new Core2StatusView { Name = "Bleed", Stacks = 0, MeterValue = 3, MeterMax = 6, Tooltip = "Build-up." });
            Assert.That(meter.Title, Is.EqualTo("Bleed \u00d73"));
            Assert.That(meter.Body, Is.EqualTo("Build-up.\nBuild-up: 3 / 6"));
            var percent = Core2Readings.Status(Ui.Strings, new Core2StatusView { Name = "Weakened", Stacks = 25, Duration = 2, Percent = true, Tooltip = "x" });
            Assert.That(percent.Title, Is.EqualTo("Weakened 25% \u00b7 2 turns"));
        }

        [Test]
        public void EveryStatusInTheFightsViewCarriesItsFilledTooltip()
        {
            var flow = Fight();
            var all = new[] { flow.View.Player }.Concat(flow.View.Enemies).SelectMany(f => f.Statuses).ToList();
            foreach (var status in all)
            {
                Assert.That(status.Tooltip, Is.Not.Null, status.Id);
                Assert.That(status.Tooltip, Does.Not.Contain("{"), status.Id + " has an unfilled token");
            }
        }

        [Test]
        public void ARelicPipExplainsItselfWithItsNameAndItsFilledText()
        {
            var flow = Fight();
            var data = flow.Session.State.Data;
            var id = data.Relics.All.First(r => !string.IsNullOrEmpty(r.Value<string>("textTemplate"))).Value<string>("id");
            var reading = Core2Readings.Relic(data, Ui.Components, id);
            Assert.That(reading.Title, Is.EqualTo(data.Relics.Get(id).Value<string>("name")));
            Assert.That(reading.Body, Is.Not.Empty);
            Assert.That(Core2Readings.Relic(data, Ui.Components, "no.such.relic").Title, Is.EqualTo("no.such.relic"));
        }

        // ------------------------------------------------------------------ the bars follow the numbers (web fx.js playTimeline onBeatApplied)

        private static Core2LogLine Line(string type, string source, string target, object payload)
        {
            var e = JObject.FromObject(payload);
            e["type"] = type;
            e["sourceId"] = source;
            e["targetId"] = target;
            return new Core2LogLine { Type = type, SourceId = source, TargetId = target, Event = e, Text = "x" };
        }

        private static FxContext Context() => new FxContext
        {
            Defs = Ui.Feedback, Strings = Ui.Strings, Pace = CombatPace.From(new CombatPacing { AnimSpeed = "normal", Speed = 1, AutoSpeed = 1, StageGapMs = 500, AnimationStepMs = 390 }, Ui.Feedback, false),
            PlayerId = "player", PlayerManeuver = "attack", MaxHp = _ => 20,
        };

        private static IEnumerable<FxCue> Numbers(IEnumerable<Core2LogLine> lines) =>
            Core2Feedback.Plan(lines.ToList(), Context()).SelectMany(b => b.Groups.SelectMany(g => g.Numbers).Concat(b.Together).Concat(b.Resolution)).Where(c => c.Type == FxCueType.Float);

        [Test]
        public void ADamageNumberCarriesTheHitPointsItTakesAndAHealCarriesThemBack()
        {
            var hit = Numbers(new[] { Line(FxEvents.DamageDealt, "e1", "player", new { amount = 9, blocked = 4, hpLoss = 5 }) }).ToList();
            Assert.That(hit.Single(c => c.Text == "-5").Hp, Is.EqualTo(5), "the red number is the loss");
            Assert.That(hit.Single(c => c.Text == "4").Hp, Is.EqualTo(0), "the gray number is Block, not hit points");
            var heal = Numbers(new[] { Line(FxEvents.Healed, null, "player", new { amount = 7 }) }).Single();
            Assert.That(heal.Hp, Is.EqualTo(-7), "a heal gives the bar back what the number says");
        }
    }
}
