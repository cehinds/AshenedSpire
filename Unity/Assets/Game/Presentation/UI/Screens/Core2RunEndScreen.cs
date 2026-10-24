using System;
using System.Globalization;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The Core 2 run end (VP-12, D-964; web ui/screens/gameover.js, docs/qa/web-screens/run-end-*): THE CLIMB ENDS over the gold Defeat (THE CLIMB over Victory) in a door on the spire vista painting; the
    /// heading ("You perished" in the loss colour, "Ember restored") and the ornament; the identity card (Forsaken, the name, the floor and fights, the seed, the blow that struck it); the four numbers;
    /// the Earned card (the classes the run's close-out opened); the final deck; and Run again (a new climb of the same class with a new seed, focused as the web does), Run history and Return to title.
    /// Every sentence is <see cref="Core2RunEndFlow"/>'s; the run is already recorded in the profile and its slot is clear (permadeath). Escape does nothing: the run is over.
    /// </summary>
    public sealed class Core2RunEndScreen : ScreenView
    {
        private Core2RunSession _run;
        private Core2RunEndFlow _flow;
        private LocButton _again;
        private LocButton _history;
        private LocButton _title;

        public Core2RunSession Run => _run;

        public Core2RunEndFlow Flow => _flow;

        public LocButton AgainButton => _again;

        public LocButton HistoryButton => _history;

        public LocButton TitleButton => _title;

        protected override void OnBind(object args)
        {
            _run = (args as Core2RunArgs)?.Session ?? Ui.Core2Session;
            if (_run == null || !_run.RunOver)
            {
                Debug.LogError(UiMessages.NoRun);
                return;
            }
            _flow = new Core2RunEndFlow(_run, Ui.Data);
            var door = Root.Q<CombatModal>(UiNames.CendDoor);
            var texture = Ui.Texture(Ui.Data.Art?.Scene(RunEndArt.Scene));
            BoardHud.SetImage(Root.Q(UiNames.CendBackground), texture);
            door.Reset();
            door.SetHead(_flow.Eyebrow, _flow.Title);
            var body = door.Body;
            body.Add(Text(_flow.Heading, RunEndClasses.Heading, _flow.Victory ? null : RunEndClasses.HeadingLoss));
            var divider = new VisualElement { pickingMode = PickingMode.Ignore };
            divider.AddToClassList(RunEndClasses.Divider);
            divider.AddToClassList(UiClasses.Divider);
            body.Add(divider);
            body.Add(Identity());
            body.Add(Strip());
            if (_flow.Earned.Count > 0) body.Add(Earned());
            body.Add(Deck());
            body.Add(Actions());
            Context.Instance.LastFocused = _again;
        }

        private VisualElement Identity()
        {
            var card = new VisualElement();
            card.AddToClassList(RunEndClasses.Identity);
            card.AddToClassList(UiClasses.Frame);
            card.Add(Text(_flow.IdentityEyebrow, RunEndClasses.Eyebrow, UiClasses.Upper));
            card.Add(Text(_flow.Name, RunEndClasses.Name));
            card.Add(Text(_flow.Line, RunEndClasses.Line));
            card.Add(Text(_flow.Meta, RunEndClasses.Meta));
            if (!string.IsNullOrEmpty(_flow.Cause)) card.Add(Text(_flow.Cause, RunEndClasses.Cause));
            return card;
        }

        private VisualElement Strip()
        {
            var strip = new VisualElement();
            strip.AddToClassList(RunEndClasses.Strip);
            foreach (var chip in _flow.Chips)
            {
                var item = new VisualElement { pickingMode = PickingMode.Ignore };
                item.AddToClassList(RunEndClasses.Chip);
                item.Add(Text(chip.Key, RunEndClasses.ChipKey, UiClasses.Upper));
                item.Add(Text(chip.Value, RunEndClasses.ChipValue));
                strip.Add(item);
            }
            return strip;
        }

        private VisualElement Earned()
        {
            var card = new VisualElement();
            card.AddToClassList(RunEndClasses.Earned);
            card.AddToClassList(UiClasses.Frame);
            card.Add(Text(_flow.EarnedHeading, RunEndClasses.Eyebrow, UiClasses.Upper));
            foreach (var line in _flow.Earned)
            {
                var row = new VisualElement { pickingMode = PickingMode.Ignore };
                row.AddToClassList(RunEndClasses.EarnedLine);
                row.Add(Text(line.Name, RunEndClasses.Line));
                row.Add(Text(line.Kind, RunEndClasses.EarnedKind));
                card.Add(row);
            }
            return card;
        }

        private VisualElement Deck()
        {
            var section = new VisualElement { pickingMode = PickingMode.Ignore };
            var head = new VisualElement { pickingMode = PickingMode.Ignore };
            head.AddToClassList(RunEndClasses.DeckHead);
            head.Add(Text(_flow.DeckEyebrow, RunEndClasses.Eyebrow, UiClasses.Upper));
            head.Add(Text(_flow.DeckTitle, RunEndClasses.DeckTitle));
            section.Add(head);
            var deck = new VisualElement { pickingMode = PickingMode.Ignore };
            deck.AddToClassList(RunEndClasses.Deck);
            foreach (var card in _flow.Deck)
            {
                var name = Text(card.Name, RunEndClasses.DeckCard);
                name.EnableInClassList(RunEndClasses.DeckCardUpgraded, card.Upgraded);
                deck.Add(name);
            }
            section.Add(deck);
            return section;
        }

        private VisualElement Actions()
        {
            var row = new VisualElement();
            row.AddToClassList(RunEndClasses.Actions);
            _again = Action(_flow.AgainLabel, RunAgain);
            _again.AddToClassList(UiClasses.ButtonReady);
            _history = Action(_flow.HistoryLabel, OpenHistory);
            _title = Action(_flow.TitleLabel, ToTitle);
            row.Add(_again);
            row.Add(_history);
            row.Add(_title);
            return row;
        }

        private LocButton Action(string text, Action press)
        {
            var button = new LocButton();
            button.AddToClassList(RunEndClasses.Action);
            button.SetResolved(text);
            button.clicked += press;
            return button;
        }

        private static LocLabel Text(string value, params string[] classes)
        {
            var label = new LocLabel { pickingMode = PickingMode.Ignore };
            foreach (var c in classes)
                if (c != null) label.AddToClassList(c);
            label.SetResolved(value ?? string.Empty);
            return label;
        }

        // ------------------------------------------------------------------ the ways on

        /// <summary>Run again: a new climb of the same class and name with a new seed, in the same slot; a refusal is said at the button.</summary>
        public void RunAgain()
        {
            Core2RunAgainResult result;
            try { result = _flow.RunAgain(Ui.RunContent, Ui.Saves, RunFlow.NewSeed()); }
            catch (Exception e)
            {
                Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.SaveFailed, e.Message));
                RunFlow.Refuse(Context, _again, StringKeys.Core2EndAgainRefused);
                return;
            }
            if (!result.Done)
            {
                Kit.Refusal.Show(_again, Context.Overlay, result.Refusal, Ui.Data.Tokens.Duration(TokenKeys.Refusal));
                return;
            }
            Ui.Core2Session = result.Session;
            Ui.Session = null;
            RunFlow.Show(Context, result.Session);
        }

        /// <summary>Run history: the journal when it is built, else its refusal at the button (the web's history screen is a later work package).</summary>
        public void OpenHistory()
        {
            if (Ui.Data.Screens.IsBuilt(ScreenIds.Journal)) Nav.Go(ScreenIds.Journal);
            else RunFlow.Refuse(Context, _history, StringKeys.RunEndHistoryLater);
        }

        /// <summary>Return to title: the run is over and its slot already clear.</summary>
        public void ToTitle()
        {
            Ui.ClearRun();
            Nav.Go(ScreenIds.Title, null, true);
        }

        public override bool HandleBack() => true;
    }
}
