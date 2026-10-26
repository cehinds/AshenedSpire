using System;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-7d (D-1770..): the deck editor's keys, as the web answers them (screens/deckEditor.js dispatch 1030-1075, togglePick/drop/heldInput 933-975, switchPane/cycleFilters 979-995, the keys paragraph 817-824): the Deck key
    /// cycles the filters, the End Turn key picks up the row the focus is on (under Play in deck order only) and puts it down, the arrows then move it, LB and RB step the pane ring, and the key line names the keys as bound.
    /// The filter cycle itself is held to the web row by row in <see cref="Core2DeckEditorParityTests"/> (<c>nextPreset</c>). Engine-free.
    /// </summary>
    [TestFixture, Category("Presentation")]
    public class Core2DeckEditorKeysTests
    {
        private string _dir;
        private SaveService _saves;
        private static readonly DateTime Epoch = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        private static UiData _ui;

        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        private static RunContent Content => Core2HostParityTests.Content;

        private static StringTable Strings => new StringTable(TestContent.ContentJson(ContentFiles.StringsAppEn));

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-core2-deckkeys-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private Core2RunSession New(bool ordered)
        {
            var session = Core2RunSession.New(Content, _saves, 1, 12345, "reaver", "Aldric", () => Epoch);
            if (ordered) session.Profile.Doc[LoopKeys.Settings] = new JObject { [Core2DeckKeys.PlayInDeckOrder] = true };
            return session;
        }

        private static string Id(DeckRow row) => row.InstanceId;

        // ------------------------------------------------------------------ the End Turn key: pick up, move, put down

        [Test]
        public void TheEndTurnKeyPicksUpTheFocusedRowUnderPlayInDeckOrderAndPutsItDown()
        {
            var editor = New(true).OpenDeckEditor(Strings).Editor;
            var view = new DeckEditorView();
            Assert.That(editor.Model(view).Ordered, Is.True, "the setting is on");
            var first = Id(editor.Model(view).Deck[0]);
            Assert.That(editor.EndTurnKey(first, view), Is.True);
            Assert.That(editor.Held, Is.EqualTo(first), "togglePick: the row is held");
            Assert.That(editor.EndTurnKey(first, view), Is.True);
            Assert.That(editor.Held, Is.Null, "the same key puts it down (held === instanceId: drop)");
            Assert.That(editor.EndTurnKey(Id(editor.Model(view).Deck[1]), view), Is.True);
            Assert.That(editor.EndTurnKey(first, view), Is.True);
            Assert.That(editor.Held, Is.EqualTo(first), "another row replaces the held one");
        }

        [Test]
        public void TheEndTurnKeyIsNotTheEditorsWithNoRowInFocusAndPicksNothingUpWhenTheDeckIsShuffled()
        {
            var ordered = New(true).OpenDeckEditor(Strings).Editor;
            var view = new DeckEditorView();
            Assert.That(ordered.EndTurnKey(null, view), Is.False, "web: return !!row, so a key with no row in focus is left alone");
            Assert.That(ordered.Held, Is.Null);
            var shuffled = New(false).OpenDeckEditor(Strings).Editor;
            var row = Id(shuffled.Model(view).Deck[0]);
            Assert.That(shuffled.Model(view).Ordered, Is.False);
            Assert.That(shuffled.EndTurnKey(row, view), Is.True, "web: the key is taken whenever a row is in focus");
            Assert.That(shuffled.Held, Is.Null, "but only model.ordered picks it up");
        }

        [Test]
        public void ArrowsMoveTheHeldRowAndNothingMovesWhenNoneIsHeld()
        {
            var editor = New(true).OpenDeckEditor(Strings).Editor;
            var view = new DeckEditorView();
            Assert.That(editor.MoveHeld(1), Is.False, "nothing held: the arrows are the focus's");
            var ids = editor.Model(view).Deck.Select(Id).ToList();
            editor.TogglePick(ids[0]);
            Assert.That(editor.MoveHeld(1), Is.True);
            Assert.That(editor.Model(view).Deck.Select(Id).Take(2), Is.EqualTo(new[] { ids[1], ids[0] }), "down: one place later");
            Assert.That(editor.MoveHeld(-1), Is.True);
            Assert.That(editor.Model(view).Deck.Select(Id), Is.EqualTo(ids), "up: back where it was");
            Assert.That(editor.MoveHeld(-1), Is.True, "web: the key is taken even when the row cannot go higher");
            Assert.That(editor.Model(view).Deck.Select(Id), Is.EqualTo(ids));
        }

        [Test]
        public void ARowTakenOutOfTheDeckCancelUndoAndDoneAllPutTheHeldRowDown()
        {
            var editor = New(true).OpenDeckEditor(Strings).Editor;
            var view = new DeckEditorView();
            var ids = editor.Model(view).Deck.Select(Id).ToList();
            editor.TogglePick(ids[2]);
            editor.Remove(ids[2]);
            Assert.That(editor.Held, Is.Null, "web remove(): if (held === instanceId) drop()");
            editor.TogglePick(ids[1]);
            editor.Undo();
            Assert.That(editor.Held, Is.Null);
            editor.TogglePick(ids[1]);
            editor.Cancel();
            Assert.That(editor.Held, Is.Null, "web close(): drop()");
        }

        // ------------------------------------------------------------------ the Deck key and the pane ring

        [Test]
        public void TheFilterCycleStartsAtNoFilterGoesThroughTheTypesThenTheSourcesAndWrapsKeepingTheSort()
        {
            var editor = New(false).OpenDeckEditor(Strings).Editor;
            var view = new DeckEditorView { Sort = Core2DeckEditorValues.SortName };
            var seen = new System.Collections.Generic.List<string>();
            for (var i = 0; i < 40; i++)
            {
                var model = editor.Model(view);
                view = DeckEditor.NextFilterPreset(model, view);
                Assert.That(view.Sort, Is.EqualTo(Core2DeckEditorValues.SortName), "the sort is kept");
                Assert.That(view.Cost, Is.Empty, "the cost chips are cleared");
                Assert.That(view.Upgraded, Is.False);
                seen.Add(string.Join("+", view.Type) + "|" + string.Join("+", view.Source));
            }
            var types = editor.Model(new DeckEditorView()).FilterType.Select(c => c.Id).ToList();
            var cycle = new[] { "|" }.Concat(types.Select(t => t + "|")).Concat(DeckEditor.Sources.Select(s => "|" + s)).ToList();
            Assert.That(seen.Take(cycle.Count).ToList(), Is.EqualTo(cycle.Skip(1).Concat(cycle.Take(1)).ToList()), "no filter, each type, each source, then round again");
            Assert.That(seen[cycle.Count], Is.EqualTo(seen[0]), "wraps");
        }

        [Test]
        public void AViewThatIsNoPresetStartsTheCycleOverAtNoFilter()
        {
            var editor = New(false).OpenDeckEditor(Strings).Editor;
            var model = editor.Model(new DeckEditorView());
            var odd = new DeckEditorView { Type = model.FilterType.Take(2).Select(c => c.Id).ToList(), Cost = new[] { "1" } };
            var next = DeckEditor.NextFilterPreset(model, odd);
            Assert.That(next.Type, Is.Empty, "findIndex -1, plus one: the first preset (no filter), as the web's");
            Assert.That(next.Source, Is.Empty);
        }

        [Test]
        public void ThePaneRingStepsBetweenCollectionAndDeckAndWraps()
        {
            Assert.That(DeckEditor.SwitchPane(Core2DeckEditorValues.PaneCollection, 1), Is.EqualTo(Core2DeckEditorValues.PaneDeck));
            Assert.That(DeckEditor.SwitchPane(Core2DeckEditorValues.PaneDeck, 1), Is.EqualTo(Core2DeckEditorValues.PaneCollection), "wraps at the end");
            Assert.That(DeckEditor.SwitchPane(Core2DeckEditorValues.PaneCollection, -1), Is.EqualTo(Core2DeckEditorValues.PaneDeck), "and at the start");
            Assert.That(DeckEditor.SwitchPane(Core2DeckEditorValues.PaneDeck, -1), Is.EqualTo(Core2DeckEditorValues.PaneCollection));
        }

        [Test]
        public void OnANarrowLayoutTheRingDoesNothingForAnUnorderedDeck()
        {
            Assert.That(DeckEditor.CanSwitchPane(false, true), Is.False, "web switchPane: !model.ordered and (max-width: 1000px)");
            Assert.That(DeckEditor.CanSwitchPane(true, true), Is.True, "the Draw order chip reaches the deck pane");
            Assert.That(DeckEditor.CanSwitchPane(false, false), Is.True);
            Assert.That(DeckEditor.CanSwitchPane(true, false), Is.True);
        }

        // ------------------------------------------------------------------ the dispatcher

        private static HotkeyDispatcher Dispatcher(JObject keys = null) => new HotkeyDispatcher(Ui.Controls, new ControlBindings(Ui.Controls, keys, null));

        private static HotkeyState Armoury(bool veil = false, bool ring = true) => new HotkeyState { Screen = ScreenIds.Armoury, Veil = veil, TabRing = ring };

        private static string ActionOf(HotkeyResult r) => r.Kind == HotkeyKind.Action ? r.Id : null;

        [Test]
        public void TheEditorAnswersMenuDeckAndEndTurnAsTheWebsDispatchDoes()
        {
            var d = Dispatcher();
            Assert.That(ActionOf(d.Key(new KeyPress { Key = "m" }, Armoury())), Is.EqualTo(HotkeyActions.Menu), "Done");
            Assert.That(ActionOf(d.Key(new KeyPress { Key = "d" }, Armoury())), Is.EqualTo(HotkeyActions.Deck), "cycleFilters");
            Assert.That(ActionOf(d.Key(new KeyPress { Key = "e" }, Armoury())), Is.EqualTo(HotkeyActions.EndTurn), "togglePick");
            foreach (var key in new[] { "r", "t", "f", "i" }) Assert.That(d.Key(new KeyPress { Key = key }, Armoury()).Consumed, Is.False, key + " is not the editor's");
            Assert.That(d.Key(new KeyPress { Key = "e" }, Armoury(veil: true)).Consumed, Is.False, "a confirmation over the editor owns the keys (closeConfirmationOpen)");
            Assert.That(d.Key(new KeyPress { Key = "e" }, new HotkeyState { Screen = ScreenIds.Armoury, Typing = true }).Consumed, Is.False, "a text field keeps its letters");
        }

        [Test]
        public void TheDeckKeyCyclesFiltersEvenWhenAnotherArmouryDoorSharesItsKey()
        {
            var shared = Dispatcher(new JObject { [HotkeyActions.Deck] = "z", [HotkeyActions.Relics] = "z" });
            Assert.That(ActionOf(shared.Key(new KeyPress { Key = "z" }, Armoury())), Is.EqualTo(HotkeyActions.Deck), "deckEditor.js matchAction(probe, 'deck') has no exactly-one-door test");
            Assert.That(shared.Key(new KeyPress { Key = "z" }, new HotkeyState { Screen = ScreenIds.ActMap }).Consumed, Is.False, "the map still applies it");
        }

        [Test]
        public void ARebindMovesTheEditorsKeys()
        {
            var d = Dispatcher(new JObject { [HotkeyActions.EndTurn] = "x", [HotkeyActions.Deck] = "k" });
            Assert.That(ActionOf(d.Key(new KeyPress { Key = "x" }, Armoury())), Is.EqualTo(HotkeyActions.EndTurn));
            Assert.That(ActionOf(d.Key(new KeyPress { Key = "k" }, Armoury())), Is.EqualTo(HotkeyActions.Deck));
            Assert.That(d.Key(new KeyPress { Key = "e" }, Armoury()).Consumed, Is.False);
        }

        [Test]
        public void ThePadReachesTheSameKeysYFiltersXPicksStartIsDoneAndTheBumpersAreThePaneRing()
        {
            var d = Dispatcher();
            var deck = Ui.Controls.Find(HotkeyActions.Deck).DefButton;
            var move = Ui.Controls.Find(HotkeyActions.EndTurn).DefButton;
            var done = Ui.Controls.Find(HotkeyActions.Menu).DefButton;
            Assert.That(ActionOf(d.Pad(deck, Armoury())), Is.EqualTo(HotkeyActions.Deck), "PAD.y: cycleFilters");
            Assert.That(ActionOf(d.Pad(move, Armoury())), Is.EqualTo(HotkeyActions.EndTurn), "PAD.x: togglePick");
            Assert.That(ActionOf(d.Pad(done, Armoury())), Is.EqualTo(HotkeyActions.Menu), "PAD.start: doDone");
            Assert.That(d.Pad(Ui.Controls.Hotkeys.RingNextButton, Armoury()).Kind, Is.EqualTo(HotkeyKind.TabNext), "PAD.rb");
            Assert.That(d.Pad(Ui.Controls.Hotkeys.RingPrevButton, Armoury()).Kind, Is.EqualTo(HotkeyKind.TabPrev), "PAD.lb");
            Assert.That(d.Key(new KeyPress { Key = "]" }, Armoury()).Kind, Is.EqualTo(HotkeyKind.TabNext), "] without a pad");
            Assert.That(d.Key(new KeyPress { Key = "[" }, Armoury()).Kind, Is.EqualTo(HotkeyKind.TabPrev));
            Assert.That(d.Key(new KeyPress { Key = "]" }, Armoury(ring: false)).Consumed, Is.False, "no ring while the editor is not up");
        }

        // ------------------------------------------------------------------ the key line

        [Test]
        public void TheKeyLineNamesTheKeysAsBoundAndTheHeldRowsMove()
        {
            var bindings = new ControlBindings(Ui.Controls);
            Assert.That(HintBarView.DeckEditorLine(Ui, bindings, false, null),
                Is.EqualTo("Select to inspect · ＋ or － moves a card · [ ] switch panes · D cycles filters · E picks up a row · M Accept · Esc Cancel"));
            Assert.That(HintBarView.DeckEditorLine(Ui, new ControlBindings(Ui.Controls, new JObject { [HotkeyActions.Deck] = "k" }, null), false, null), Does.Contain("K cycles filters"), "a rebind is named");
            var pad = HintBarView.DeckEditorLine(Ui, bindings, true, null);
            Assert.That(pad, Does.Contain(Ui.Controls.PadGlyph(Ui.Controls.Find(HotkeyActions.Deck).DefButton) + " cycles filters"), "the pad's glyph while a pad is connected");
            Assert.That(HintBarView.DeckEditorLine(Ui, bindings, false, "Strike"), Is.EqualTo("Moving Strike: ▲ and ▼ place it, and Move or Enter drops it."));
        }

        // ------------------------------------------------------------------ the combatant door's Inspect key

        [Test]
        public void TheInspectKeyReadsTheSelectedLivingEnemyElseThePlayer()
        {
            var living = new[] { "e1", "e2" };
            Assert.That(InspectSubject.Subject("e2", living), Is.EqualTo("e2"), "combat.js: the enemy they selected");
            Assert.That(InspectSubject.Subject(null, living), Is.Null, "else themselves");
            Assert.That(InspectSubject.Subject("e3", living), Is.Null, "a selected enemy that is gone (dead) is not read: combat.enemies.find(... && enemy.alive)");
            Assert.That(InspectSubject.Subject("player", living), Is.Null, "the player selected themselves: the player's door");
            Assert.That(InspectSubject.Subject("e1", null), Is.Null);
        }
    }
}
