using System;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-18 (D-402): the deck editor on a hosted Core 2 run (<see cref="Core2RunSession.OpenDeckEditor"/>): it opens at the map and shows the deck; a card moves to the sideboard
    /// and back; Done inside the bounds writes the slot (a resume holds the edit); Done outside them is refused with the rules' sentence and changes nothing; Cancel gives the run
    /// back exactly; the editor stays shut during a fight, with Deck editing off and after the climb; a draft commits atomically with the web's refusal sentences. The rules behind
    /// the answers are held to the web by <see cref="Core2DeckParityTests"/> and <see cref="Core2DeckEditorParityTests"/>. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2DeckEditorSessionTests
    {
        private string _dir;
        private SaveService _saves;
        private static readonly DateTime Epoch = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

        private static RunContent Content => Core2HostParityTests.Content;

        private static StringTable Strings => new StringTable(TestContent.ContentJson(ContentFiles.StringsAppEn));

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-core2-deck-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private Core2RunSession New() => Core2RunSession.New(Content, _saves, 1, 12345, "reaver", "Aldric", () => Epoch);

        private static string FirstCard(Core2RunSession session) => session.Run.Arr(Core2DeckKeys.Deck)[0].Value<string>(Core2DeckKeys.InstanceId);

        [Test]
        public void ADoneWhoseSaveFailsLeavesTheEditorOpen()
        {
            var session = New();
            var editor = session.OpenDeckEditor(Strings).Editor;
            // The save's temp file is a directory: the write fails, as a full or read-only disk would.
            var blocker = Path.Combine(_dir, session.Slot + ".json.tmp");
            Directory.CreateDirectory(blocker);
            Assert.That(() => editor.Confirm(), Throws.Exception);
            Assert.That(editor.Closed, Is.False, "the editor stays usable after a failed save");
            Directory.Delete(blocker);
            Assert.That(editor.Confirm().Ok, Is.True, "Done works once the disk takes writes again");
            Assert.That(editor.Closed, Is.True);
        }

        [Test]
        public void TheEditorOpensAtTheMapAndShowsTheDeck()
        {
            var session = New();
            var open = session.OpenDeckEditor(Strings);
            Assert.That(open.Opened, Is.True, open.Refusal);
            var model = open.Editor.Model();
            var deck = session.Run.Arr(Core2DeckKeys.Deck);
            Assert.That(model.Counter.Count, Is.EqualTo(deck.Count));
            Assert.That(model.Counter.Text, Does.StartWith(deck.Count + " / 10"));
            Assert.That(model.Deck.Sum(r => r.Count), Is.EqualTo(deck.Count));
            Assert.That(model.Ordered, Is.False, "the deck is shuffled by default");
        }

        [Test]
        public void ACardMovesToTheSideboardAndBackAndDoneSavesTheSlot()
        {
            var session = New();
            var editor = session.OpenDeckEditor(Strings).Editor;
            var size = session.Run.Arr(Core2DeckKeys.Deck).Count;
            var first = FirstCard(session);
            Assert.That(editor.Remove(first).Ok, Is.True);
            Assert.That(editor.CanUndo, Is.True);
            Assert.That(session.Run.Arr(Core2DeckKeys.Sideboard).Count, Is.EqualTo(1));
            var tile = editor.Model().Collection.First(t => t.Addable);
            Assert.That(editor.Add(tile.Key).Ok, Is.True, "the card comes back from the sideboard");
            Assert.That(session.Run.Arr(Core2DeckKeys.Deck).Count, Is.EqualTo(size));
            Assert.That(editor.Confirm().Ok, Is.True);
            Assert.That(editor.Closed, Is.True);
            var resumed = Core2RunSession.Load(Content, _saves, 1, () => Epoch);
            Assert.That(resumed.Session.Run.ToString(Newtonsoft.Json.Formatting.None), Is.EqualTo(session.Run.ToString(Newtonsoft.Json.Formatting.None)), "a resume holds the confirmed deck");
        }

        [Test]
        public void DoneOutsideTheBoundsIsRefusedWithTheRulesSentence()
        {
            var session = New();
            var editor = session.OpenDeckEditor(Strings).Editor;
            foreach (var id in session.Run.Arr(Core2DeckKeys.Deck).Select(c => c.Value<string>(Core2DeckKeys.InstanceId)).ToList()) editor.Remove(id);
            var model = editor.Model();
            Assert.That(model.DoneDisabled, Is.True);
            var done = editor.Confirm();
            Assert.That(done.Ok, Is.False);
            Assert.That(done.Refusal.Text, Is.EqualTo(model.DoneRefusal));
            Assert.That(done.Refusal.Text, Does.StartWith("Your deck has 0 cards; it needs at least 10"));
            Assert.That(editor.Closed, Is.False, "the editor stays open");
        }

        [Test]
        public void CancelGivesTheRunBackExactly()
        {
            var session = New();
            var before = session.Run.ToString(Newtonsoft.Json.Formatting.None);
            var editor = session.OpenDeckEditor(Strings).Editor;
            editor.Remove(FirstCard(session));
            editor.Remove(FirstCard(session));
            editor.Cancel();
            Assert.That(session.Run.ToString(Newtonsoft.Json.Formatting.None), Is.EqualTo(before));
            Assert.Throws<InvalidOperationException>(() => editor.Remove("x"), "a closed editor answers nothing");
        }

        [Test]
        public void UndoRestoresTheStateBeforeTheLastEdit()
        {
            var session = New();
            var editor = session.OpenDeckEditor(Strings).Editor;
            var before = session.Run.ToString(Newtonsoft.Json.Formatting.None);
            Assert.That(editor.Remove(FirstCard(session)).Ok, Is.True);
            Assert.That(editor.Undo().Ok, Is.True);
            Assert.That(session.Run.ToString(Newtonsoft.Json.Formatting.None), Is.EqualTo(before));
            Assert.That(editor.Undo().Ok, Is.False, "nothing left to undo");
        }

        [Test]
        public void TheEditorStaysShutDuringAFightAndAfterTheClimb()
        {
            var session = New();
            session.StartEncounter(FirstEncounter(session));
            Assert.That(session.IsInCombat, Is.True);
            var open = session.OpenDeckEditor(Strings);
            Assert.That(open.Opened, Is.False);
            Assert.That(open.Refusal, Is.EqualTo(StringKeys.Core2DeckLocked));
            Assert.That(Strings.Get(open.Refusal), Is.Not.EqualTo(open.Refusal), "the key says something");
            session.Abandon();
            Assert.That(session.OpenDeckEditor(Strings).Refusal, Is.EqualTo(StringKeys.Core2DeckRunOver));
        }

        private static string FirstEncounter(Core2RunSession session) => session.Core.Loop.Map.Encounters.Ids.First();

        [Test]
        public void ADraftCommitsAtomicallyAndARefusedOneChangesNothing()
        {
            var session = New();
            var draft = session.Run;
            var deck = draft.Arr(Core2DeckKeys.Deck);
            var moved = (JObject)deck[0];
            deck.RemoveAt(0);
            draft[Core2DeckKeys.Sideboard] = new JArray(moved);
            // Nine cards: under the minimum of ten.
            var before = session.Run.ToString(Newtonsoft.Json.Formatting.None);
            var refused = session.CommitDeckDraft(draft);
            Assert.That(refused.Ok, Is.False);
            Assert.That(refused.Message, Does.StartWith("Your deck has 9 cards; it needs at least 10"));
            Assert.That(session.Run.ToString(Newtonsoft.Json.Formatting.None), Is.EqualTo(before));

            var invented = session.Run;
            invented.Arr(Core2DeckKeys.Deck).Add(new JObject { [Core2DeckKeys.InstanceId] = "inv1", [Core2DeckKeys.CardId] = "core.strike", [Core2DeckKeys.Upgraded] = false });
            var cannot = session.CommitDeckDraft(invented);
            Assert.That(cannot.Ok, Is.False);
            Assert.That(cannot.Message, Is.EqualTo("Deck editing cannot invent a limited or enhanced card"));

            var swap = session.Run;
            var sideboard = new JArray(swap.Arr(Core2DeckKeys.Deck)[0].DeepClone());
            swap.Arr(Core2DeckKeys.Deck).RemoveAt(0);
            swap[Core2DeckKeys.Sideboard] = sideboard;
            swap.Arr(Core2DeckKeys.Deck).Add(swap.Arr(Core2DeckKeys.Deck)[0].DeepClone());
            // A duplicated identity.
            Assert.That(session.CommitDeckDraft(swap).Message, Is.EqualTo("Duplicate edited card identity"));

            var ok = session.Run;
            var reorder = ok.Arr(Core2DeckKeys.Deck);
            var last = reorder[reorder.Count - 1];
            reorder.RemoveAt(reorder.Count - 1);
            reorder.Insert(0, last);
            var committed = session.CommitDeckDraft(ok);
            Assert.That(committed.Ok, Is.True, committed.Message);
            Assert.That(session.Run.Arr(Core2DeckKeys.Deck)[0].Value<string>(Core2DeckKeys.InstanceId), Is.EqualTo(last.Value<string>(Core2DeckKeys.InstanceId)));
        }

        [Test]
        public void PlayInDeckOrderDealsTheDeckAsListed()
        {
            var session = New();
            session.Profile.Doc[LoopKeys.Settings] = new JObject { [Core2DeckKeys.PlayInDeckOrder] = true };
            var deck = session.Run.Arr(Core2DeckKeys.Deck).Select(c => c.Value<string>(Core2DeckKeys.InstanceId)).ToList();
            var combat = session.StartEncounter(FirstEncounter(session));
            var state = combat.State;
            var order = state.Fields["orderedDraw"]["order"].Select(t => t.Value<string>()).ToList();
            Assert.That(order.Count, Is.EqualTo(deck.Count));
            var dealt = state.Piles.Hand.Concat(state.Piles.Draw).Select(c => c.Value<string>("instanceId")).ToList();
            Assert.That(dealt.Count, Is.EqualTo(deck.Count));
        }
    }
}
