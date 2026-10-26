using System;
using System.Collections.Generic;
using Ashen.App.Ui;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Run
{
    /// <summary>The deck editor's words from the string table (strings/app.en.json, the web's deckEditor.* ui strings).</summary>
    public sealed class DeckEditorText : IDeckEditorText
    {
        private readonly StringTable _strings;

        public DeckEditorText(StringTable strings) => _strings = strings ?? throw new ArgumentNullException(nameof(strings));

        public bool Has(string key) => _strings.Has(key);

        public string Say(string key, params KeyValuePair<string, string>[] args)
        {
            var named = new StringArgs();
            if (args != null) foreach (var pair in args) named.Add(pair.Key, pair.Value);
            return _strings.Format(key, named);
        }
    }

    /// <summary>Whether the deck editor may open now, and if not why (a string-table key the screen shows).</summary>
    public sealed class Core2DeckOpen
    {
        /// <summary>The string key of the reason the editor stays shut, or null when it opened.</summary>
        public string Refusal;

        public Core2DeckEditor Editor;

        public bool Opened => Editor != null;
    }

    /// <summary>What committing a deck draft did: the deck's size, or the web's sentence for the refusal (the run is as it was).</summary>
    public sealed class Core2DeckCommit
    {
        public bool Ok;
        public int DeckSize;
        public string Message;
    }

    /// <summary>
    /// The deck editor over a Core 2 run (docs/design/11 WP-18; web mountDeckEditor, main.js showDeckEditor): the session edits the run in place, <see cref="Confirm"/>
    /// writes the run to the slot at once (main.js onDone: persist), <see cref="Cancel"/> restores the run exactly so there is nothing to write.
    /// <see cref="Model"/> is the projection a screen draws. Engine-free.
    /// </summary>
    public sealed class Core2DeckEditor
    {
        private readonly Core2RunSession _owner;
        private readonly DeckEditSession _session;

        internal Core2DeckEditor(Core2RunSession owner, DeckEditSession session)
        {
            _owner = owner;
            _session = session;
        }

        public bool Closed => _session.Closed;
        public bool CanUndo => _session.CanUndo;

        public DeckEditorModelData Model(DeckEditorView view = null) => _session.Model(view);

        /// <summary>Add from a collection tile's key (<c>card:variant</c>, <c>basic:id</c>, <c>kept:variant</c>).</summary>
        public DeckEditResult Add(string key) => _session.Add(key);

        public DeckEditResult Remove(string instanceId)
        {
            // web remove(): a row taken out of the deck is put down first.
            if (Held == instanceId) Held = null;
            return _session.Remove(instanceId);
        }

        /// <summary>
        /// The deck row picked up for reordering (web mountDeckEditor <c>held</c>, deckEditor.js:86 and 933-947): while one is held the arrows (and the pad's up and down) move it instead of the focus, and Enter, pad A, pad X
        /// or the End Turn key put it down. Null when none.
        /// </summary>
        public string Held { get; private set; }

        /// <summary>togglePick(instanceId): the row is picked up, or put down when it is the one held.</summary>
        public void TogglePick(string instanceId)
        {
            Held = Held == instanceId ? null : instanceId;
        }

        /// <summary>drop(): nothing is held.</summary>
        public void Drop() => Held = null;

        /// <summary>
        /// The End Turn key in the editor (deckEditor.js:1061): it picks up (or puts down) the row the focus is on, only under Play in deck order, and the key is taken whenever a row had the focus.
        /// Returns whether the key was the editor's.
        /// </summary>
        public bool EndTurnKey(string focusedRowId, DeckEditorView view)
        {
            if (string.IsNullOrEmpty(focusedRowId)) return false;
            if (Model(view).Ordered) TogglePick(focusedRowId);
            return true;
        }

        /// <summary>heldInput up and down (deckEditor.js:962-966): the held row one place up (-1) or down (1). False when nothing is held.</summary>
        public bool MoveHeld(int delta)
        {
            if (Held == null) return false;
            _session.Move(Held, delta < 0 ? -1 : 1);
            return true;
        }

        /// <summary>Place a card at a deck index (only under Play in deck order).</summary>
        public DeckEditResult MoveTo(string instanceId, int index) => _session.MoveTo(instanceId, index);

        public DeckEditResult Move(string instanceId, int delta) => _session.Move(instanceId, delta);

        public DeckEditResult Undo()
        {
            // The web has no Undo; a row put back may be the one held, so it is put down.
            Held = null;
            return _session.Undo();
        }

        /// <summary>
        /// Done: inside the bounds the slot is saved and then the edit is kept; otherwise the refusal is the rules' sentence and the editor stays open. A save that
        /// throws leaves the editor open (Done, Cancel and Undo still work), so a full disk never strands the player in a closed editor.
        /// </summary>
        public DeckEditResult Confirm()
        {
            var check = _session.CheckConfirm();
            if (!check.Ok) return check;
            _owner.Save();
            var done = _session.Confirm();
            if (done.Ok) Held = null;
            return done;
        }

        /// <summary>Cancel: both piles, the attack-slot allocation and the mint counter put back exactly.</summary>
        public void Cancel()
        {
            Held = null;
            _session.Cancel();
        }
    }
}
