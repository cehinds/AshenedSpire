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
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-3b (D-1490..): the player's combat figure is the web's card stage (src/ui/alternativeCardStage.js). Tools/oracle-core2-stage.mjs records, from the web's own modules, what every profile
    /// card (plain and upgraded) tells the stage of each class (the action, the rest, the stance, the pilot's stance) and where every frame of the assembled stage is drawn, held and unheld;
    /// the C# port (CardStagePlans, CardStageData) is compared with them, and the clip arithmetic (sampleSequence, durationFor, the hurt clip, the knock-back) with the web's formulas.
    /// Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class CardStageTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "stage");

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

        private static CardStageData Stage => Ui.CardStage;

        private static JObject Read(string name) => OracleDocuments.Read(Path.Combine(Dir, name));

        private static JObject Unlocked => new JObject
        {
            ["gameProfileUnlocks"] = new JObject { [Data.Run.ProfileId] = new JObject { ["completedRunIds"] = new JArray(Enumerable.Range(0, Data.Run.Run.Classes.Count).Select(i => "r" + i)) } },
        };

        private static CombatState Open(long seedValue, string classId, string encounterId)
        {
            var seed = (uint)seedValue;
            var run = CoreRunRules.Create(Data.Run, seed, classId, CoreRunOptions.FromJson(null, Unlocked));
            return CoreRunCombat.Create(Data, new Rng(seed), run, encounterId);
        }

        private static string[] Classes => ((JObject)Read("cards.json")["classes"]).Properties().Select(p => p.Name).ToArray();

        // ------------------------------------------------------------------ the cards

        [TestCaseSource(nameof(Classes))]
        public void EveryCardTellsTheStageWhatTheWebTellsIt(string classId)
        {
            var want = (JObject)Read("cards.json")["classes"][classId];
            var c = Open(want.Value<long>("seed"), classId, want.Value<string>("encounterId"));
            var problems = new List<string>();
            var checkedCards = 0;
            foreach (var row in ((JObject)want["cards"]).Properties())
            {
                var expected = (JObject)row.Value;
                if (expected["error"] != null) continue;
                var upgraded = row.Name.EndsWith("+", StringComparison.Ordinal);
                var cardId = upgraded ? row.Name.Substring(0, row.Name.Length - 1) : row.Name;
                var instance = new JObject { ["instanceId"] = "s:" + row.Name, ["cardId"] = cardId, ["upgraded"] = upgraded };
                var def = CoreCosts.Resolve(c, instance);
                var plan = CardStagePlans.For(c, def);
                var pilot = want["weaponProfile"] != null && want["weaponProfile"].Type != JTokenType.Null;
                var got = new[]
                {
                    ("technique", expected.Value<string>("technique"), plan.Technique),
                    ("rest", expected.Value<string>("rest"), plan.Rest),
                    ("play", expected["animation"].Value<string>("technique"), plan.Play),
                    ("stance", expected.Value<string>("stance"), plan.Stance),
                    ("pilotStance", expected.Value<string>("pilotStance"), plan.PilotStance),
                    ("counter", expected.Value<string>("counter"), plan.PilotCounter),
                };
                foreach (var (field, web, unity) in got)
                    if (web != unity && !(field == "pilotStance" && !pilot)) problems.Add($"{row.Name} {field}: web {web ?? "null"}, unity {unity ?? "null"}");
                checkedCards++;
            }
            Assert.That(checkedCards, Is.GreaterThan(400));
            if (problems.Count > 0) Assert.Fail(problems.Count + " differences:\n" + string.Join("\n", problems.Take(12)));
        }

        // ------------------------------------------------------------------ the frames

        [TestCaseSource(nameof(Classes))]
        public void EveryFrameIsDrawnWhereTheWebDrawsIt(string classId)
        {
            var want = (JObject)Read("placement.json")["classes"][classId];
            var c = Stage.Class(classId);
            Assert.That(c.Unit, Is.EqualTo(want.Value<double>("unit")).Within(1e-9));
            Assert.That(c.BodyHeight, Is.EqualTo(want.Value<double>("bodyHeight")).Within(1e-4), "the ready figure's head-to-sole distance");
            Assert.That(c.Frames.Keys.OrderBy(k => k, StringComparer.Ordinal), Is.EqualTo(((JObject)want["frames"]).Properties().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal)));
            foreach (var row in (JObject)want["frames"])
            {
                var frame = c.Frames[row.Key];
                var authored = (JObject)row.Value["authored"];
                var held = (JObject)row.Value["held"];
                var plain = Stage.Place(c, frame, 0, false);
                Assert.That(plain.Left, Is.EqualTo(authored.Value<double>("left") - Stage.AnchorX).Within(1e-9), row.Key + " left");
                Assert.That(plain.Top, Is.EqualTo(authored.Value<double>("top") - Stage.FloorY).Within(1e-9), row.Key + " top");
                Assert.That(plain.Width, Is.EqualTo(authored.Value<double>("width")).Within(1e-9), row.Key + " width");
                Assert.That(plain.Height, Is.EqualTo(authored.Value<double>("height")).Within(1e-9), row.Key + " height");
                var seated = Stage.Place(c, frame, 0, true);
                Assert.That(seated.Left, Is.EqualTo(held.Value<double>("left") - Stage.AnchorX).Within(1e-6), row.Key + " held left");
                Assert.That(seated.Top, Is.EqualTo(held.Value<double>("top") - Stage.FloorY).Within(1e-6), row.Key + " held top");
                Assert.That(seated.Width, Is.EqualTo(held.Value<double>("width")).Within(1e-6), row.Key + " held width");
                Assert.That(seated.Height, Is.EqualTo(held.Value<double>("height")).Within(1e-6), row.Key + " held height");
            }
        }

        [Test]
        public void EveryArtOfTheStageIsAnImportedIdOfTheRegistry()
        {
            var registry = (JObject)JsonContent.Parse(TestContent.Source.ReadText(ContentFiles.AssetsRegistry));
            var ids = new List<string>();
            foreach (var id in Stage.ClassIds)
            {
                var c = Stage.Class(id);
                ids.AddRange(c.Frames.Values.Select(f => f.Art));
                ids.AddRange(c.Effects.Values);
                ids.Add(c.Hurt.Art);
                ids.Add(c.Defeated);
            }
            Assert.That(ids.Count, Is.GreaterThan(150));
            foreach (var id in ids.Distinct())
            {
                Assert.That(registry.ContainsKey(id), Is.True, id);
                Assert.That(registry[id].Value<string>("unityPath"), Is.Not.Null.And.Not.Empty, id + " is imported");
            }
        }

        [Test]
        public void EveryClassHasEveryActionACardCanPlayAndEveryPoseOfItsClips()
        {
            foreach (var id in Stage.ClassIds)
            {
                var c = Stage.Class(id);
                foreach (var action in new[] { StageActions.Attack, StageActions.Smash, StageActions.Sweep, StageActions.Counter, StageActions.Defend, StageActions.Spell, StageActions.Ranged, StageActions.RangedMagic, StageActions.Evade, StageNames.CounterPrepare })
                    Assert.That(c.Sequence(action), Is.Not.Null, id + " " + action);
                var own = c.Sequences.Values.ToList();
                if (c.Pilot) own.AddRange(Stage.Reactions.Values);
                foreach (var sequence in own)
                    foreach (var pose in sequence.Poses.Where(p => p != StageNames.Origin))
                        Assert.That(c.Frame(pose), Is.Not.Null, id + " " + sequence.Name + " " + pose);
                Assert.That(c.Frame(StageNames.Ready), Is.Not.Null, id);
                Assert.That(c.Frame(c.CounterRest), Is.Not.Null, id + " counter rest");
                foreach (var held in c.Held) Assert.That(c.Frame(StageNames.StancePrefix + held), Is.Not.Null, id + " held " + held);
            }
        }

        // ------------------------------------------------------------------ the clip (alternativeCardAnimation.js, selectedWeaponSequences.js)

        private static StageSequence Attack(string classId = "rogue") => Stage.Class(classId).Sequence(StageActions.Attack);

        [Test]
        public void ASequenceIsSampledByItsShareOfTheClip()
        {
            // The rogue's attack: five poses held 60, 55, 50, 45 and 50 ms, travel 0, 0, 80, 50, 0 (content/alternativeCardAnimations.js).
            var attack = Attack();
            Assert.That(attack.Durations, Is.EqualTo(new[] { 60d, 55d, 50d, 45d, 50d }));
            var start = Stage.Sample(attack, 0, 260, false);
            Assert.That(start.Index, Is.EqualTo(0));
            Assert.That(start.X, Is.EqualTo(0));
            var third = Stage.Sample(attack, 140, 260, false);
            Assert.That(third.Index, Is.EqualTo(2), "the blow's pose");
            Assert.That(third.X, Is.EqualTo(80 + (50 - 80) * 0.5).Within(1e-9), "half way from travel 80 to travel 50");
            var end = Stage.Sample(attack, 260, 260, false);
            Assert.That(end.Finished, Is.True);
            Assert.That(end.X, Is.EqualTo(0), "the clip ends where it began");
            Assert.That(Stage.Sample(attack, 100, 260, true).Index, Is.EqualTo(0), "reduced motion holds the first pose");
        }

        [Test]
        public void AClipPlaysAtThePaceAndNeverLandsItsBlowAfterTheCap()
        {
            var attack = Attack();
            // The web's normal pace scaled for the sprite (ANIM_SPEEDS.normal.lungeMs 195, impactCapMs 186) at the reference animation step.
            var speed = new SpriteSpeed { LungeMs = 195, ImpactCapMs = 186, ReactionMs = 180 };
            var contact = 60d + 55;
            var scale = Math.Min(195 / 260d, 186 / contact);
            Assert.That(Stage.DurationFor(attack, speed), Is.EqualTo(260 * scale).Within(1e-9));
            var tight = new SpriteSpeed { LungeMs = 1000, ImpactCapMs = 100, ReactionMs = 180 };
            Assert.That(Stage.DurationFor(attack, tight) * (contact / 260), Is.EqualTo(100).Within(1e-9), "the blow lands at the cap");
            Assert.That(Stage.DurationFor(attack, default), Is.EqualTo(0), "no pace, no clip");
            var timing = Stage.Timing(attack, speed);
            Assert.That(timing.TotalMs, Is.EqualTo(260 * scale).Within(1e-9));
            Assert.That(timing.ImpactMs, Is.EqualTo(contact / 260 * 260 * scale).Within(1e-9));
        }

        [Test]
        public void ThePilotAndClassClipsAreTimedInUnitsOfTheLungeAndAReactionTakesTheReactionTime()
        {
            var speed = new SpriteSpeed { LungeMs = 195, ImpactCapMs = 186, ReactionMs = 180 };
            var reaver = Stage.Class("reaver");
            var smash = reaver.Sequence(StageActions.Smash);
            Assert.That(smash.TimingUnits, Is.EqualTo(5), "origin, takeoff, airborne, impact, origin");
            Assert.That(Stage.Timing(smash, speed).TotalMs, Is.EqualTo(195), "five units of a fifth");
            Assert.That(Stage.Timing(smash, speed).ImpactMs, Is.EqualTo(195 * 3 / 5d).Within(1e-9));
            var attack = reaver.Sequence(StageActions.Attack);
            Assert.That(Stage.Timing(attack, speed).TotalMs, Is.EqualTo(195).Within(1e-9), "six equal poses in a sixth each");
            Assert.That(Stage.Timing(Stage.Reactions[StageReactions.Defend], speed).TotalMs, Is.EqualTo(180), "a defend reaction takes the reaction time");
            Assert.That(Stage.Timing(Stage.Reactions[StageReactions.Evade], speed).TotalMs, Is.EqualTo(195 / 2d).Within(1e-9), "three units of a sixth");
            var rogue = Stage.Class("rogue").Sequence(StageActions.Smash);
            Assert.That(rogue.TimingUnits, Is.EqualTo(5), "the other classes' smash is a class clip");
        }

        [Test]
        public void TheHurtClipStrobesRedOnItsEvenFramesAndTheBodyKnocksBackAndReturns()
        {
            Assert.That(Stage.HurtClipFrame(0).Red, Is.False);
            Assert.That(Stage.HurtClipFrame(1.5 / 7).Red, Is.True, "the second of seven");
            Assert.That(Stage.HurtClipFrame(2.5 / 7).Red, Is.False);
            Assert.That(Stage.HurtClipFrame(3.5 / 7).Red, Is.True);
            Assert.That(Stage.HurtClipFrame(1).Index, Is.EqualTo(6));
            Assert.That(Stage.HitFlashOpacity(220 * 1.5 / 7, 220, false), Is.EqualTo(0.85).Within(1e-9));
            Assert.That(Stage.HitFlashOpacity(220 * 1.5 / 7, 220, true), Is.EqualTo(0), "reduced motion draws no mask");
            Assert.That(Stage.HitFlashOpacity(220, 220, false), Is.EqualTo(0), "the clip is over");
            Assert.That(Stage.RecoilX(0), Is.EqualTo(0));
            Assert.That(Stage.RecoilX(0.3), Is.EqualTo(-14).Within(1e-9), "the peak");
            Assert.That(Stage.RecoilX(0.65), Is.EqualTo(-14 * 0.5 * 0.5).Within(1e-9), "half way back, eased out");
            Assert.That(Stage.RecoilX(1), Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ the state

        private static readonly Func<string, bool> All = _ => true;

        [Test]
        public void ARestingFigureStandsReadyAndGuardsFromTheBraceAndAHeldStanceBeatsBoth()
        {
            var state = new CardStageState(Stage, "rogue");
            Assert.That(state.RestPose(), Is.EqualTo(StageNames.Ready));
            state.SetRest(StageNames.RestDefend);
            Assert.That(state.RestPose(), Is.EqualTo(StageNames.GuardBrace), "a guard rests braced into the next turn");
            state.SetRest(StageNames.RestCounter);
            Assert.That(state.RestPose(), Is.EqualTo(Stage.Class("rogue").CounterRest));
            state.SetStance(StageActions.Offensive);
            Assert.That(state.RestPose(), Is.EqualTo(StageNames.StancePrefix + StageActions.Offensive));
            Assert.That(state.SetStance("nonsense"), Is.False, "no frame, no stance");
            state.SetRest(StageNames.Defeated);
            Assert.That(state.RestPose(), Is.EqualTo(StageNames.Defeated), "the downed figure beats every stance");
            Assert.That(state.Draw(All, false, false).Down, Is.True);
        }

        [Test]
        public void AnActionWalksItsPosesThenReturnsToTheRest()
        {
            var state = new CardStageState(Stage, "rogue");
            var attack = Attack();
            Assert.That(state.Play(StageActions.Attack, 260, 1000, false), Is.True);
            Assert.That(state.Busy, Is.True);
            state.Tick(1000, false);
            Assert.That(state.Draw(All, false, false).Pose, Is.EqualTo(attack.Poses[0] == StageNames.Ready ? StageNames.Ready : attack.Poses[0]));
            state.Tick(1000 + 60 + 55 + 10, false);
            Assert.That(state.Draw(All, false, false).Pose, Is.EqualTo(attack.Poses[2]), "the blow's pose");
            Assert.That(state.Tick(1000 + 400, false), Is.False, "the clip is over");
            Assert.That(state.Pose, Is.EqualTo(StageNames.Ready));
            Assert.That(state.Play(StageActions.Spell, 0, 2000, false), Is.True, "a zero-length clip just settles");
            Assert.That(state.Busy, Is.False);
            Assert.That(state.Play("not an action", 100, 2000, false), Is.False);
        }

        [Test]
        public void AClipHeldByAHitStopDoesNotAdvanceUntilTheHoldPasses()
        {
            var state = new CardStageState(Stage, "reaver");
            state.Play(StageActions.Smash, 200, 0, false);
            Assert.That(state.Hold(100, 50), Is.True);
            state.Tick(100, false);
            var held = state.Draw(All, false, false).Pose;
            state.Tick(140, false);
            Assert.That(state.Draw(All, false, false).Pose, Is.EqualTo(held), "still held at 140");
            Assert.That(held, Is.EqualTo("pilot-smash-impact"), "the hold brings a contact-cued clip to its blow first");
            state.Tick(400, false);
            Assert.That(state.Busy, Is.False);
        }

        [Test]
        public void AContactCuedClipWaitsAtItsBlowUntilTheTimelineSaysItLanded()
        {
            var state = new CardStageState(Stage, "reaver");
            Assert.That(state.CueContact(false), Is.False, "nothing is playing to cue");
            state.Play(StageActions.Smash, 200, 0, false);
            Assert.That(state.CueContact(true), Is.False, "reduced motion plays no cue");
            Assert.That(state.CueContact(false), Is.True, "a smash is contact-cued (alternativeCardStage.js contactCue)");
            state.Tick(400, false);
            Assert.That(state.Busy, Is.True, "a late timer does not let the follow-through play");
            Assert.That(state.Draw(All, false, false).Pose, Is.EqualTo("pilot-smash-impact"), "it waits at the blow");
            Assert.That(state.RemainingMs(400), Is.GreaterThan(0));
            Assert.That(state.Contact(400), Is.True);
            Assert.That(state.Contact(400), Is.False, "the cue is spent");
            state.Tick(700, false);
            Assert.That(state.Busy, Is.False, "released, the clip finishes");
            Assert.That(state.RemainingMs(700), Is.EqualTo(0));
        }

        [Test]
        public void AHitDrawsTheHurtClipWithItsMaskAndKnocksTheBodyBack()
        {
            var state = new CardStageState(Stage, "rogue");
            Assert.That(state.Hit(0, 0, true), Is.False, "reduced motion plays no hit");
            Assert.That(state.Hit(0, 0, false), Is.True);
            state.Tick(220 * 1.5 / 7 * 280 / 220d, false);
            var draw = state.Draw(All, false, false);
            Assert.That(draw.Hurt, Is.True);
            Assert.That(draw.Art, Is.EqualTo(Stage.Class("rogue").Hurt.Art));
            Assert.That(draw.Flash, Is.EqualTo(0.85).Within(1e-9), "a red frame");
            Assert.That(state.Draw(All, false, true).Flash, Is.EqualTo(0), "reduce flashes drops the mask");
            Assert.That(state.Tick(10000, false), Is.False);
            Assert.That(state.Draw(All, false, false).Hurt, Is.False);
        }

        [Test]
        public void AFrameWithoutArtShowsTheReadyFigureAndNothingShowsWithoutIt()
        {
            var state = new CardStageState(Stage, "rogue");
            state.SetStance(StageActions.Casting);
            var ready = Stage.Class("rogue").Frame(StageNames.Ready).Art;
            var draw = state.Draw(art => art == ready, false, false);
            Assert.That(draw.Art, Is.EqualTo(ready));
            Assert.That(state.Draw(_ => false, false, false), Is.Null);
        }

        [Test]
        public void OnlyThePilotReactsToABlow()
        {
            var reaver = new CardStageState(Stage, "reaver");
            var rogue = new CardStageState(Stage, "rogue");
            foreach (var kind in Stage.ReactionKinds)
            {
                Assert.That(reaver.CanReact(kind), Is.True, kind);
                Assert.That(rogue.CanReact(kind), Is.False, kind);
            }
            Assert.That(reaver.React(StageReactions.Evade, 180, 0), Is.True);
            Assert.That(reaver.Draw(All, false, false).Pose, Is.EqualTo("pilot-evade-1"));
            reaver.Tick(1000, false);
            Assert.That(reaver.Busy, Is.False);
        }
    }
}
