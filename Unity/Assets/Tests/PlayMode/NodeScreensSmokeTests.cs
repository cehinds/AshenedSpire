using System;
using System.Collections;
using System.IO;
using System.Linq;
using Ashen.App.Nodes;
using Ashen.App.Run;
using Ashen.Generated;
using Ashen.Presentation;
using Ashen.Presentation.UI;
using Ashen.Presentation.UI.Kit;
using Ashen.Presentation.UI.Screens;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Ashen.Tests.Play
{
    /// <summary>
    /// F3 node screen smoke (W-09 US-9.1–9.3, W-11 US-10.1–10.3, W-13 US-4.5; AF-07, AF-08, AF-12, PF-06): a new run made
    /// by the loop's newRun is travelled to a merchant, an event or a legacy dungeon and opened through the router's entry
    /// point (<see cref="NodeRouter.Open"/>); real keyboard and pad devices drive the kit Navigator. Merchant: buy a card
    /// (select, then the W2a door), a refusal when unaffordable, burn a card (the pick, then the destructive hold door),
    /// sell (the shopSell setting on), Save &amp; quit → Continue → the same stock, Leave. Event: an illegal response shows and
    /// refuses its requirement, a legal one is taken through its review door, Save &amp; quit → Continue → the resolved
    /// state, Continue; a fight response reads Steel yourself and hands the fight to W-07; a quest-gated step opens as
    /// dialogue with its speaker. Dungeon: enter (listen at the gate), travel, a response, Save &amp; quit → Continue → the
    /// same node, then leave a cleared dungeon into the next act.
    /// </summary>
    [TestFixture, Category("Smoke")]
    public class NodeScreensSmokeTests
    {
        private const uint Seed = 20260927u;

        private string _saves;
        private Keyboard _keyboard;
        private Gamepad _pad;
        private InputSettings.BackgroundBehavior _background;
        private InputSettings.EditorInputBehaviorInPlayMode _editorInput;

        [SetUp]
        public void SetUp()
        {
            _saves = Path.Combine(Path.GetTempPath(), "ashen-nodes-smoke-" + Guid.NewGuid().ToString("N"));
            AshenBoot.SaveDirectoryOverride = _saves;
            TitleScreen.ResetSession();
            _background = InputSystem.settings.backgroundBehavior;
            _editorInput = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            _keyboard = InputSystem.AddDevice<Keyboard>();
            _pad = InputSystem.AddDevice<Gamepad>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_keyboard != null) InputSystem.RemoveDevice(_keyboard);
            if (_pad != null) InputSystem.RemoveDevice(_pad);
            InputSystem.settings.backgroundBehavior = _background;
            InputSystem.settings.editorInputBehaviorInPlayMode = _editorInput;
            AshenBoot.SaveDirectoryOverride = null;
            if (Directory.Exists(_saves)) Directory.Delete(_saves, true);
        }

        // ------------------------------------------------------------------ input helpers (as FirstFightSmokeTests)

        private IEnumerator Press(Key key)
        {
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(key));
            yield return null;
            yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
            yield return null;
            yield return null;
        }

        private IEnumerator Hold(Key key, float seconds)
        {
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(key));
            var end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
            yield return null;
            yield return null;
        }

        private IEnumerator Press(GamepadButton button)
        {
            InputSystem.QueueStateEvent(_pad, new GamepadState(button));
            yield return null;
            yield return null;
            InputSystem.QueueStateEvent(_pad, new GamepadState());
            yield return null;
            yield return null;
        }

        private static IEnumerator Until(Func<bool> condition, float seconds, string what)
        {
            var end = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > end) Assert.Fail("timed out waiting for " + what + " — focused: " + Describe(Focused) + ", screen: " + Nav?.CurrentId);
                yield return null;
            }
        }

        private IEnumerator PressUntil(Key key, Func<bool> condition, int n, string what)
        {
            for (var i = 0; i < n && !condition(); i++) yield return Press(key);
            Assert.That(condition(), Is.True, what + " — focused: " + Describe(Focused));
        }

        private static Navigator Nav => AshenBoot.Current != null && AshenBoot.Current.Host != null ? AshenBoot.Current.Host.Navigator : null;
        private static VisualElement Focused => Nav?.Top?.Focus.Focused;
        private static UiContext Ui => AshenBoot.Current.Host.Context;
        private static int HoldMs => Ui.Data.Tokens.Duration(TokenKeys.HoldConfirm);

        private static string Describe(VisualElement e) => e == null ? "nothing" : e.GetType().Name + " '" + e.name + "' " + ((e as TextElement)?.text ?? string.Empty);

        private static bool RefusalShowing(string text) =>
            Nav.Overlay.Q<Refusal>() is Refusal r && r.ClassListContains(UiClasses.RefusalVisible) && (text == null || r.Text == text);

        private IEnumerator Boot()
        {
            yield return SceneManager.LoadSceneAsync("Boot", LoadSceneMode.Single);
            yield return Until(() => Nav != null && Nav.CurrentId == ScreenIds.Title, 60f, "the title screen");
            var title = (TitleScreen)Nav.Top.View;
            yield return Press(Key.Space);
            yield return Until(() => !title.GateActive && Focused != null, 5f, "the gate to lift");
        }

        /// <summary>A run in slot 1 made by the loop's newRun, before it travels.</summary>
        private static RunSession NewRun()
        {
            var content = Ui.RunContent;
            var session = RunSession.New(content, Ui.Saves, 1, Seed, content.DefaultClass(), Ui.Data.Strings.Get(content.Flow.NameKey));
            NodeReview.UseLoopRun(session, Seed);
            return session;
        }

        private IEnumerator Open(RunSession session, NodeEntry entry, string screen)
        {
            Assert.That(NodeRouter.Open(Nav, Ui, session, entry), Is.True, "the router opens " + screen);
            yield return Until(() => Nav.CurrentId == screen && Focused != null, 10f, screen);
        }

        /// <summary>The door on top: walk from Back to the primary and commit it (a hold door is held past holdMs).</summary>
        private IEnumerator ConfirmDoor()
        {
            yield return Until(() => Nav.CurrentId == ScreenIds.Confirm && Focused != null, 5f, "the door");
            Assert.That(Focused.name, Is.EqualTo(UiNames.ConfirmBack), "doors open on Back (04 §0)");
            yield return Press(Key.DownArrow);
            if (Focused.name == UiNames.ConfirmHold) yield return Hold(Key.Enter, HoldMs / 1000f + 0.5f);
            else yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId != ScreenIds.Confirm, 5f, "the door to close");
        }

        /// <summary>Escape → W-20 → Save &amp; quit (hold) → title → Continue → the load door → the screen the run stands on.</summary>
        private IEnumerator SaveQuitAndContinue(string screen)
        {
            yield return Until(() => Focused != null, 5f, "focus before pausing");
            yield return Press(Key.Escape);
            yield return Until(() => Nav.CurrentId == ScreenIds.Pause && Focused != null, 5f, "W-20");
            var pause = (PauseScreen)Nav.Top.View;
            var saveQuit = pause.Rows.Single(r => r.enabledSelf);
            yield return PressUntil(Key.UpArrow, () => Focused == saveQuit, 4, "focus on Save & quit");
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Confirm && Focused != null, 5f, "the Save & quit door");
            yield return Press(Key.DownArrow);
            yield return Hold(Key.Enter, HoldMs / 1000f + 0.5f);
            yield return Until(() => Nav.CurrentId == ScreenIds.Title && Nav.Stack.Count == 1, 10f, "the title after Save & quit");
            var title = (TitleScreen)Nav.Top.View;
            yield return Until(() => Focused == title.Buttons[0], 5f, "focus on Continue");
            yield return new WaitForSecondsRealtime(0.3f);
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Confirm && Focused != null, 5f, "the load door");
            yield return Press(Key.DownArrow);
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == screen && Focused != null, 20f, screen + " after Continue");
        }

        // ------------------------------------------------------------------ W-09

        /// <summary>A rail pick with the keyboard (the [Shelf ▾] selector first when the rail folded into it).</summary>
        private IEnumerator PickShelf(MerchantScreen merchant, string shelf)
        {
            var nav = Nav.Top.Root.Q<CategoryNav>();
            var index = merchant.View.Shelves.FindIndex(s => s.Id == shelf);
            VisualElement list;
            if (nav.Mode == Ashen.App.Ui.CategoryNavMode.Selector)
            {
                nav.Q<LocButton>(UiNames.NavSelector).Focus();
                yield return Press(Key.Enter);
                list = nav.Q(UiNames.NavSelectorList);
            }
            else list = nav.Q(UiNames.NavRail);
            var button = (Button)list[index];
            button.Focus();
            yield return Press(Key.Enter);
        }

        [UnityTest]
        public IEnumerator MerchantBuysRefusesBurnsSellsResumesAndLeaves()
        {
            yield return Boot();
            var session = NewRun();
            var entry = NodeReview.AtMerchant(session);
            session.EditRunForReview(run => run["cinders"] = 600.0);
            yield return Open(session, entry, ScreenIds.Merchant);
            var merchant = (MerchantScreen)Nav.Top.View;
            Assert.That(merchant.View.Shelves.Select(s => s.Id), Does.Contain("sell"), "shopSell is on by default");
            Assert.That(merchant.ShelfId, Is.EqualTo("cards"));

            // Buy a card: the keyboard walks to the first tile; Enter selects it, Enter again opens the W2a door.
            var deck = session.Run["deck"].Count();
            var firstTile = merchant.Tiles[0];
            yield return PressUntil(Key.DownArrow, () => Focused == merchant.Tiles[0], 12, "focus on the first card");
            yield return Press(Key.Enter);
            Assert.That(merchant.Selected, Is.EqualTo(firstTile.name), "the first press selects");
            yield return Until(() => Focused != null && Focused.name == firstTile.name, 5f, "focus back on the tile");
            yield return Press(Key.Enter);
            yield return ConfirmDoor();
            yield return Until(() => Nav.CurrentId == ScreenIds.Merchant && Focused != null, 5f, "the merchant after the purchase");
            Assert.That(session.Run["deck"].Count(), Is.EqualTo(deck + 1), "the card joined the deck");
            Assert.That(RunSession.Load(Ui.RunContent, Ui.Saves, 1).Session.Run["deck"].Count(), Is.EqualTo(deck + 1), "saved after the purchase");

            // A refusal when unaffordable: the purse emptied, Buy on a card is refused at the control and nothing changes.
            session.EditRunForReview(run => run["cinders"] = 0.0);
            merchant.Render();
            var offer = merchant.Shelf.Offers.First();
            Assert.That(offer.Available, Is.False);
            merchant.Select(offer.Key);
            var before = session.Run.ToString();
            yield return PressUntil(Key.DownArrow, () => Focused == merchant.PrimaryButton, 12, "focus on Buy");
            yield return Press(Key.Enter);
            Assert.That(RefusalShowing(offer.ReasonText), Is.True, "the refusal says what it needs (US-9.2)");
            Assert.That(Nav.CurrentId, Is.EqualTo(ScreenIds.Merchant), "no door opens");
            Assert.That(session.Run.ToString(), Is.EqualTo(before));

            // Burn a card: the Services shelf from the rail, the removal, its card pick, the destructive hold door.
            session.EditRunForReview(run => run["cinders"] = 600.0);
            merchant.Render();
            yield return PickShelf(merchant, "services");
            yield return Until(() => merchant.ShelfId == "services", 5f, "the Services shelf");
            var removal = merchant.Shelf.Offers.First(o => o.Kind == "remove");
            var removalTile = merchant.Tiles.First(t => t.name == removal.Key);
            yield return Until(() => Focused != null, 5f, "focus on the shelf");
            removalTile.Focus();
            yield return Press(Key.Enter);
            yield return Press(Key.Enter);
            yield return Until(() => merchant.Picking != null && Focused is CardView, 5f, "the card pick");
            Assert.That(merchant.FocusMode, Is.EqualTo("pick"));
            yield return Press(Key.Enter);
            Assert.That(merchant.Pick, Is.Not.Null, "Submit selects the focused card");
            var burned = merchant.Pick;
            yield return PressUntil(Key.DownArrow, () => Focused == merchant.ConfirmPickButton, 12, "focus on Confirm");
            yield return Press(Key.Enter);
            yield return ConfirmDoor();
            yield return Until(() => Nav.CurrentId == ScreenIds.Merchant && merchant.Picking == null, 5f, "back on the shelf");
            Assert.That(session.Run["deck"].Any(c => c.Value<string>("instanceId") == burned), Is.False, "the card left the deck");
            Assert.That(session.Run.Value<double>("removesPurchased"), Is.EqualTo(1));

            // Sell with the pad: the Sell shelf, a pad press selects, a second opens the door.
            // The run carries a relic the merchant buys (a stocked one), so the Sell shelf has a sale.
            var relicId = (string)session.Run["shopStock"]["relics"][0]["id"];
            session.EditRunForReview(run => ((JArray)run["relics"]).Add(relicId));
            merchant.Render();
            yield return PickShelf(merchant, "sell");
            yield return Until(() => Focused == merchant.Tiles.FirstOrDefault(), 5f, "focus on the first sale");
            var sale = merchant.Shelf.Offers.First(o => o.Available);
            var cinders = session.Run.Value<double>("cinders");
            for (var i = 0; i < 12 && Focused?.name != sale.Key; i++) yield return Press(GamepadButton.DpadDown);
            yield return Press(GamepadButton.South);
            yield return Press(GamepadButton.South);
            yield return ConfirmDoor();
            yield return Until(() => Nav.CurrentId == ScreenIds.Merchant, 5f, "the merchant after the sale");
            Assert.That(session.Run.Value<double>("cinders"), Is.EqualTo(cinders + sale.Price), "the merchant paid");

            // Save & quit → Continue → the same stock (US-9.1).
            var stock = session.Run["shopStock"].ToString();
            yield return SaveQuitAndContinue(ScreenIds.Merchant);
            merchant = (MerchantScreen)Nav.Top.View;
            Assert.That(merchant.Session, Is.Not.SameAs(session), "a fresh session loaded from the slot");
            Assert.That(merchant.Merchant.Rolled, Is.False);
            Assert.That(merchant.Session.Run["shopStock"].ToString(), Is.EqualTo(stock), "the saved stock is reused");

            // Leave: the stock is cleared and the run saved at the map (the act map is a later build: the title).
            yield return PressUntil(Key.DownArrow, () => Focused == merchant.LeaveButton, 30, "focus on Leave");
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Title, 10f, "the title after Leave");
            var after = RunSession.Load(Ui.RunContent, Ui.Saves, 1).Session;
            Assert.That(after.Location, Is.EqualTo("map"));
            Assert.That(after.Run["shopStock"].Type, Is.EqualTo(JTokenType.Null));
        }

        // ------------------------------------------------------------------ W-11

        [UnityTest]
        public IEnumerator EventRefusesARequirementTakesAResponseResumesAndContinues()
        {
            yield return Boot();
            var session = NewRun();
            var entry = NodeReview.AtEvent(session, "weepingPilgrim");
            session.EditRunForReview(run => run["cinders"] = 0.0);
            yield return Open(session, entry, ScreenIds.Event);
            var ev = (EventScreen)Nav.Top.View;
            Assert.That(ev.ContinueButton.enabledSelf, Is.False, "Continue is disabled until a response is taken (US-10.1)");

            // The priced response shows its requirement and is refused at the press.
            var give = ev.View.Responses.FindIndex(r => r.ChoiceId == "giveCinders");
            Assert.That(ev.View.Responses[give].Label, Does.Contain(ev.View.Responses[give].ReasonText), "the requirement shows on the response");
            yield return PressUntil(Key.DownArrow, () => Focused == ev.Responses[give], 6, "focus on the priced response");
            var before = session.Run.ToString();
            yield return Press(Key.Enter);
            Assert.That(RefusalShowing(ev.View.Responses[give].ReasonText), Is.True);
            Assert.That(session.Run.ToString(), Is.EqualTo(before));

            // A legal response through its review door.
            var refuse = ev.View.Responses.FindIndex(r => r.ChoiceId == "refuse");
            yield return PressUntil(Key.DownArrow, () => Focused == ev.Responses[refuse], 6, "focus on the legal response");
            yield return Press(Key.Enter);
            yield return ConfirmDoor();
            yield return Until(() => ev.View.Resolved && Focused == ev.ContinueButton, 5f, "the resolved state with focus on Continue");
            Assert.That(ev.ContinueButton.enabledSelf, Is.True);
            Assert.That(((LocButton)ev.ContinueButton).text, Is.EqualTo(Ui.Data.Strings.Get("nodes.event.continue")));
            var result = ev.View.ResultText;

            // Save & quit → Continue → the resolved state.
            yield return SaveQuitAndContinue(ScreenIds.Event);
            ev = (EventScreen)Nav.Top.View;
            Assert.That(ev.View.Resolved, Is.True);
            Assert.That(ev.View.ResultText, Is.EqualTo(result));
            yield return Until(() => Focused == ev.ContinueButton, 5f, "focus on Continue");
            yield return Press(Key.Enter);
            yield return Until(() => Nav.CurrentId == ScreenIds.Title, 10f, "the map (the title while it is planned)");
            Assert.That(RunSession.Load(Ui.RunContent, Ui.Saves, 1).Session.Location, Is.EqualTo("map"));
        }

        [UnityTest]
        public IEnumerator AFightResponseHandsToCombatAndAQuestStepOpensAsDialogue()
        {
            yield return Boot();
            var session = NewRun();
            var entry = NodeReview.AtEvent(session, "wyrmTrial");
            yield return Open(session, entry, ScreenIds.Event);
            var ev = (EventScreen)Nav.Top.View;
            var enter = ev.View.Responses.FindIndex(r => r.ChoiceId == "enterRing");
            yield return PressUntil(Key.DownArrow, () => Focused == ev.Responses[enter], 6, "focus on the fight response");
            yield return Press(Key.Enter);
            yield return ConfirmDoor();
            yield return Until(() => ev.View.Resolved, 5f, "the resolved state");
            Assert.That(ev.View.StartsFight, Is.True);
            Assert.That(((LocButton)ev.ContinueButton).text, Is.EqualTo("Steel yourself"), "US-10.1");
            yield return Until(() => Focused == ev.ContinueButton, 5f, "focus on Steel yourself");
            yield return Press(GamepadButton.South);
            yield return Until(() => Nav.CurrentId == ScreenIds.Combat, 10f, "W-07 with the event's fight");
            var combat = (CombatScreen)Nav.Top.View;
            Assert.That(combat.Session.IsInCombat, Is.True);
            Assert.That(RunSession.Load(Ui.RunContent, Ui.Saves, 1).Session.IsInCombat, Is.True, "the fight is saved as it starts");

            // A quest-gated step (US-10.2): with the Grave's history, the Keeper opens as dialogue with its speaker.
            var quest = NewRun();
            quest.EditRunForReview(run => ((JArray)run["history"]).Add(new JObject { ["kind"] = "eventChoice", ["eventId"] = "graveOfTheNameless", ["choiceId"] = "payRespects", ["actNumber"] = 1.0, ["floor"] = 0.0, ["mapNodeId"] = null }));
            var step = NodeReview.AtEvent(quest, "namelessKeeper");
            Assert.That(step.Screen, Is.EqualTo(ScreenIds.Dialogue));
            yield return Open(quest, step, ScreenIds.Dialogue);
            var dialogue = (EventScreen)Nav.Top.View;
            Assert.That(dialogue.IsDialogue && dialogue.View.HasSpeaker, Is.True);
            Assert.That(Nav.Top.Root.Q<LocLabel>(UiNames.SpeakerName).text, Is.EqualTo(dialogue.View.SpeakerName));
            Assert.That(dialogue.View.Responses.First(r => r.ChoiceId == "acceptThanks").Locked, Is.False, "payRespects opens the Keeper's thanks");
            Assert.That(dialogue.View.Responses.First(r => r.ChoiceId == "faceKeeper").Locked, Is.True, "digForCinders was not chosen");
            var thanks = dialogue.View.Responses.FindIndex(r => r.ChoiceId == "acceptThanks");
            yield return PressUntil(Key.DownArrow, () => Focused == dialogue.Responses[thanks], 8, "focus on the Keeper's thanks");
            yield return Press(Key.Enter);
            yield return ConfirmDoor();
            yield return Until(() => dialogue.View.Resolved, 5f, "the dialogue resolved");
            Assert.That(quest.Run["relics"].Any(r => (string)r == "gravetendersBell"), Is.True, "the bell was given");
        }

        // ------------------------------------------------------------------ W-13

        [UnityTest]
        public IEnumerator DungeonEntersTravelsChoosesResumesAndLeaves()
        {
            yield return Boot();
            var session = NewRun();
            var entry = NodeReview.AtDungeon(session, "BS");
            yield return Open(session, entry, ScreenIds.LegacyDungeon);
            var dungeon = (DungeonScreen)Nav.Top.View;
            Assert.That(dungeon.View.Actions.Single().Id, Is.EqualTo("listen"), "the gate's one response");

            // Enter: listen at the gate, Continue, then travel along the one open edge.
            yield return Until(() => Focused == dungeon.Actions[0], 5f, "focus on Listen");
            yield return Press(Key.Enter);
            yield return Until(() => dungeon.View.Pending, 5f, "the pending response");
            yield return Until(() => Focused == dungeon.Actions[0], 5f, "focus on Continue");
            yield return Press(Key.Enter);
            yield return Until(() => dungeon.View.Actions.Any(a => a.Kind == "travel"), 5f, "the exits");
            var from = dungeon.View.NodeName;
            yield return Until(() => Focused == dungeon.Actions[0], 5f, "focus on the first exit");
            yield return Press(GamepadButton.South);
            yield return Until(() => dungeon.View.NodeName != from, 5f, "the next node");

            // A choice at the new node.
            var node = dungeon.View.NodeName;
            yield return Until(() => Focused == dungeon.Actions[0], 5f, "focus on the node's first response");
            yield return Press(Key.Enter);
            yield return Until(() => dungeon.View.Pending, 5f, "the chosen response");
            var state = session.Run["legacyDungeon"].ToString();

            // Save & quit → Continue → the same node with the pending response.
            yield return SaveQuitAndContinue(ScreenIds.LegacyDungeon);
            dungeon = (DungeonScreen)Nav.Top.View;
            Assert.That(dungeon.View.NodeName, Is.EqualTo(node));
            Assert.That(dungeon.Session.Run["legacyDungeon"].ToString(), Is.EqualTo(state), "the dungeon state survived the reload");

            // Leave a cleared dungeon: the door states the outcome, the next act follows (the map is planned: the title).
            NodeReview.StandAt(dungeon.Session, "BS-24", true, true);
            dungeon.Render();
            yield return PressUntil(Key.DownArrow, () => Focused != null && Focused == dungeon.Actions.Last(), 8, "focus on Leave");
            Assert.That(dungeon.View.Actions.Last().Kind, Is.EqualTo("leave"));
            yield return Press(Key.Enter);
            yield return ConfirmDoor();
            yield return Until(() => Nav.CurrentId == ScreenIds.Title, 10f, "the next act (the title while the map is planned)");
            var after = RunSession.Load(Ui.RunContent, Ui.Saves, 1).Session;
            Assert.That(after.Run["legacyDungeon"], Is.Null, "left the dungeon");
            Assert.That(after.Run.Value<double>("actNumber"), Is.EqualTo(2), "the act advanced");
        }
    }
}
