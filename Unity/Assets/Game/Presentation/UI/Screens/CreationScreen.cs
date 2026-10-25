using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>Which slot the new climb is written to (W-03 New chose it).</summary>
    public sealed class CreationArgs
    {
        public int SlotIndex = 1;
    }

    public sealed class CreationViewModel : ViewModel
    {
        private string _selected;
        private string _pane = FrontValues.PaneClass;
        private bool _grid;

        public string Selected { get => _selected; set => Set(ref _selected, value); }

        /// <summary>The rail pane in front: <see cref="FrontValues.PaneClass"/> or <see cref="FrontValues.PaneReview"/>.</summary>
        public string Pane { get => _pane; set => Set(ref _pane, value); }

        /// <summary>The class cards as a grid (the web's List | Grid switch).</summary>
        public bool Grid { get => _grid; set => Set(ref _grid, value); }
    }

    /// <summary>
    /// W-04 creation (US-2.1, US-2.8; web src/ui/screens/creation, D-1003): the rail Class | Review beside the pane. Class lists the classes in content order as cards (figure, name,
    /// summary, HP SP MP HAND DRAW, the starting relic; a locked class shows the lock and what opens it), as a list or a grid. The data default (rules/runFlow.json) is preselected
    /// when it can begin. Next opens Review: the seed (the same seed makes the same climb), the destination slot, the CHARACTER, ARMOUR, MAIN HAND and RELIC cards the class
    /// starts with and its starting deck, read from the run the class would make (<see cref="Core2RunSession.Preview"/>, nothing written). Begin makes the climb through the run
    /// loop's newRun (W2c Replace first when the slot holds a climb) and opens the opening, or the act map (W-06).
    /// </summary>
    public sealed class CreationScreen : ScreenView
    {
        private readonly List<LocButton> _tiles = new List<LocButton>();
        private readonly Dictionary<string, CreationReviewView> _reviews = new Dictionary<string, CreationReviewView>(StringComparer.Ordinal);
        private IReadOnlyList<string> _classes = new string[0];
        private RunContent _content;
        private CreationClasses _rules;
        private Shell _shell;
        private Workspace _workspace;
        private TextField _seedField;
        private LocButton _headList;
        private LocButton _headGrid;
        private VisualElement _avatar;
        private int _slot = 1;

        public CreationViewModel ViewModel { get; } = new CreationViewModel();
        public IReadOnlyList<LocButton> Tiles => _tiles;
        public IReadOnlyList<string> Classes => _classes;

        /// <summary>The seed field (tests and captures type into it).</summary>
        public TextField SeedField => _seedField;

        public override string FocusMode => ViewModel.Pane == FrontValues.PaneReview ? FrontValues.FocusReview : null;

        protected override void OnBind(object args)
        {
            _slot = (args as CreationArgs)?.SlotIndex ?? _slot;
            _content = Ui.RunContent;
            _rules = CreationClasses.For(_content, ProfileStore.Load(Ui.Saves).Doc);
            _classes = _rules.Ids;
            _shell = Root.Q<Shell>(UiNames.Shell);
            _shell.Exit += () => Nav.Pop();
            _shell.Back += GoBack;
            _shell.Primary += () => Primary(_shell.PrimaryButton);
            _workspace = Root.Q<Workspace>(UiNames.Workspace);
            if (_workspace != null)
            {
                _workspace.Rules = Ui.Data.Layout.CategoryNav;
                _workspace.Picked += OnPane;
                _workspace.Nav.StepKey = StringKeys.CreationStep;
                _workspace.Nav.SelectorKey = StringKeys.CreationAllSteps;
            }
            BuildHeaderTools();
            Root.Q<LocButton>(UiNames.ClassViewList).clicked += () => ViewModel.Grid = false;
            Root.Q<LocButton>(UiNames.ClassViewGrid).clicked += () => ViewModel.Grid = true;
            var list = Root.Q(UiNames.ClassList);
            foreach (var id in _classes)
            {
                var card = ClassCard(id);
                var captured = id;
                card.clicked += () => ViewModel.Selected = captured;
                list.Add(card);
                _tiles.Add(card);
            }
            _seedField = new TextField { name = UiNames.SeedField, maxLength = _content.Seeds.MaxLength, value = _content.Seeds.Format(RunFlow.NewSeed()) };
            _seedField.AddToClassList(FrontClasses.ReviewSeed);
            _seedField.RegisterValueChangedCallback(_ => _seedField.RemoveFromClassList(FrontClasses.ReviewSeedBad));
            ViewModel.Selected = _rules.Default();
            ViewModel.Observe(Render);
            var index = _classes.ToList().IndexOf(ViewModel.Selected);
            Context.Instance.LastFocused = index >= 0 ? (VisualElement)_tiles[index] : _shell.PrimaryButton;
        }

        private string ClassName(string id) => RunFlow.ClassName(Ui, id);

        // ------------------------------------------------------------------ the header's cluster (flow-05 / flow-06, both sizes)

        /// <summary>
        /// The web's header (creation shots): after the title, the List | Grid switch (the phone only; the wide screen keeps it over the cards),
        /// the chosen class's portrait, the menu button (the settings) and the close.
        /// </summary>
        private void BuildHeaderTools()
        {
            var header = Root.Q(UiNames.ShellHeader);
            var exit = _shell.ExitButton;
            if (header == null || exit == null) return;
            var tools = new VisualElement { pickingMode = PickingMode.Ignore };
            tools.AddToClassList(ChromeClasses.CreationHeadTools);
            var seg = new VisualElement();
            seg.AddToClassList(ChromeClasses.CreationHeadSeg);
            _headList = HeadSegButton(StringKeys.CreationViewList, () => ViewModel.Grid = false);
            _headGrid = HeadSegButton(StringKeys.CreationViewGrid, () => ViewModel.Grid = true);
            seg.Add(_headList);
            seg.Add(_headGrid);
            tools.Add(seg);
            _avatar = new VisualElement { pickingMode = PickingMode.Ignore };
            _avatar.AddToClassList(ChromeClasses.CreationAvatar);
            tools.Add(_avatar);
            var menu = new LocButton { stringKey = StringKeys.CombatMenu };
            menu.AddToClassList(ChromeClasses.CreationMenu);
            BoardHud.SetImage(menu, BoardHud.Art(Ui, ArtKinds.Component, BoardArt.ComponentMenu));
            menu.clicked += OpenSettings;
            tools.Add(menu);
            header.Insert(header.IndexOf(exit), tools);
        }

        private static LocButton HeadSegButton(string key, Action pressed)
        {
            var button = new LocButton { stringKey = key };
            button.AddToClassList(ChromeClasses.CreationHeadSegButton);
            button.clicked += pressed;
            return button;
        }

        private void OpenSettings()
        {
            if (Ui.Data.Screens.IsBuilt(ScreenIds.Settings)) Nav.Go(ScreenIds.Settings);
        }

        // ------------------------------------------------------------------ the class cards

        /// <summary>One class as the web's choice card: the figure, the name, the sentence, the figures and the starting relic; a locked class shows the lock and what opens it.</summary>
        private LocButton ClassCard(string id)
        {
            var strings = Ui.Data.Strings;
            var card = new LocButton();
            card.AddToClassList(FrontClasses.ClassCard);
            var locked = _rules.Locked(id);
            card.EnableInClassList(FrontClasses.ClassCardLocked, locked);
            card.EnableInClassList(UiClasses.ClassTileProblem, _rules.Problem(id) != null);
            var figure = new VisualElement { pickingMode = PickingMode.Ignore };
            figure.AddToClassList(FrontClasses.ClassCardFigure);
            Ui.ApplyBackground(figure, _content.Portrait(id));
            card.Add(figure);
            var texts = new VisualElement { pickingMode = PickingMode.Ignore };
            texts.AddToClassList(FrontClasses.ClassCardTexts);
            card.Add(texts);
            texts.Add(Text(new LocLabel(), FrontClasses.ClassCardName, ClassName(id)));
            texts.Add(Text(new LocLabel(), FrontClasses.ClassCardSummary, strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.ClassDescriptionKey, id))));
            var review = ReviewOf(id);
            if (review != null) FillStats(texts, review);
            else if (!locked) FillLegacyStats(texts, id);
            if (!locked && _rules.Problem(id) != null) texts.Add(Text(new LocLabel(), FrontClasses.ClassCardProblem, strings.Get(StringKeys.CreationProblem)));
            if (locked)
            {
                var lockIcon = new VisualElement { pickingMode = PickingMode.Ignore };
                lockIcon.AddToClassList(FrontClasses.ClassCardLock);
                BoardHud.SetImage(lockIcon, Ui.Icon(FrontArt.IconLock));
                card.Add(lockIcon);
                var games = _rules.GamesToUnlock(id);
                var note = new LocLabel { pickingMode = PickingMode.Ignore };
                note.AddToClassList(UiClasses.Chip);
                note.AddToClassList(FrontClasses.ClassCardNote);
                note.SetResolved(strings.Format(games == 1 ? StringKeys.CreationUnlockOne : StringKeys.CreationUnlockMany, new StringArgs().Add(UiPlaceholders.N, games)));
                card.Add(note);
            }
            var chevron = new VisualElement { pickingMode = PickingMode.Ignore };
            chevron.AddToClassList(FrontClasses.ClassCardChevron);
            BoardHud.SetImage(chevron, Ui.Icon(FrontArt.IconNext));
            card.Add(chevron);
            return card;
        }

        private static LocLabel Text(LocLabel label, string cls, string text)
        {
            label.pickingMode = PickingMode.Ignore;
            label.AddToClassList(cls);
            label.SetResolved(text);
            return label;
        }

        private void FillStats(VisualElement texts, CreationReviewView review)
        {
            var stats = new VisualElement { pickingMode = PickingMode.Ignore };
            stats.AddToClassList(FrontClasses.ClassCardStats);
            foreach (var stat in review.ClassStats)
            {
                var cell = new VisualElement { pickingMode = PickingMode.Ignore };
                cell.AddToClassList(FrontClasses.ClassCardStat);
                var label = Text(new LocLabel(), FrontClasses.ClassCardStatLabel, stat.Key);
                label.AddToClassList(UiClasses.Upper);
                label.SetResolved(stat.Key);
                cell.Add(label);
                cell.Add(Text(new LocLabel(), FrontClasses.ClassCardStatValue, stat.Value));
                stats.Add(cell);
            }
            texts.Add(stats);
            if (review.RelicName == null) return;
            var relic = new VisualElement { pickingMode = PickingMode.Ignore };
            relic.AddToClassList(FrontClasses.ClassCardRelic);
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList(FrontClasses.ClassCardChevron);
            BoardHud.SetImage(icon, Ui.Icon(FrontArt.IconRelic));
            relic.Add(icon);
            relic.Add(Text(new LocLabel(), FrontClasses.ClassCardNote, review.RelicName));
            texts.Add(relic);
        }

        /// <summary>The 405 run has no preview run: the old lines of the class (attributes, pools, flasks).</summary>
        private void FillLegacyStats(VisualElement texts, string id)
        {
            var strings = Ui.Data.Strings;
            var preview = _rules.Preview(id);
            if (preview == null) return;
            var stats = string.Join(strings.Get(StringKeys.CreationStatJoin), preview.Attributes.Select(a => strings.Format(StringKeys.CreationStat, new StringArgs()
                .Add(UiPlaceholders.Name, strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.AttributeShortKey, a.Key))).Add(UiPlaceholders.Value, (int)a.Value))));
            texts.Add(Text(new LocLabel(), FrontClasses.ClassCardSummary, stats));
            texts.Add(Text(new LocLabel(), FrontClasses.ClassCardSummary, strings.Format(StringKeys.CreationDerived, new StringArgs()
                .Add(UiPlaceholders.Hp, (int)preview.MaxHp).Add(UiPlaceholders.Mp, (int)preview.MaxMana).Add(UiPlaceholders.Sp, (int)preview.MaxStamina).Add(UiPlaceholders.Actions, (int)preview.Actions))));
        }

        /// <summary>The kit the class starts with (the run it would make), or null for a class that cannot begin.</summary>
        private CreationReviewView ReviewOf(string classId)
        {
            if (classId == null || _rules.Problem(classId) != null) return null;
            if (_reviews.TryGetValue(classId, out var known)) return known;
            CreationReviewView review = null;
            try
            {
                var session = Core2RunSession.Preview(_content, Ui.Saves, 1u, classId, Ui.Data.Strings.Get(StringKeys.CreationDefaultName));
                review = Core2CreationReview.Build(session, Ui.Data);
            }
            catch (Exception e)
            {
                Debug.LogWarning(string.Format(CultureInfo.InvariantCulture, UiMessages.ClassRefused, classId, e.Message));
            }
            _reviews[classId] = review;
            return review;
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            var selected = ViewModel.Selected;
            var review = ViewModel.Pane == FrontValues.PaneReview;
            for (var i = 0; i < _tiles.Count; i++)
            {
                _tiles[i].EnableInClassList(UiClasses.Selected, _classes[i] == selected);
                _tiles[i].EnableInClassList(FrontClasses.ClassCardGrid, ViewModel.Grid);
            }
            Root.Q(UiNames.ClassList).EnableInClassList(FrontClasses.ClassListGrid, ViewModel.Grid);
            _headList?.EnableInClassList(FrontClasses.ClassViewOn, !ViewModel.Grid);
            _headGrid?.EnableInClassList(FrontClasses.ClassViewOn, ViewModel.Grid);
            if (_avatar != null && selected != null) Ui.ApplyBackground(_avatar, _content.Portrait(selected));
            Root.Q(UiNames.ClassViewList).EnableInClassList(FrontClasses.ClassViewOn, !ViewModel.Grid);
            Root.Q(UiNames.ClassViewGrid).EnableInClassList(FrontClasses.ClassViewOn, ViewModel.Grid);
            _workspace?.SetCategories(Ui.Data.Menus.CreationPanes.Select(p => new CategoryItem
            {
                Id = p.Id,
                LabelKey = p.LabelKey,
                Count = p.Id == FrontValues.PaneClass && selected != null ? ClassName(selected) : null,
                Enabled = p.Built,
            }).ToList(), ViewModel.Pane);
            UiDom.Show(Root.Q(UiNames.ClassPane), !review);
            UiDom.Show(Root.Q(UiNames.ReviewPane), review);
            _shell.SetFooter(StringKeys.CreationBack, review ? StringKeys.CreationBegin : StringKeys.CreationNext);
            var problem = selected == null || _rules.Problem(selected) != null;
            _shell.PrimaryButton?.EnableInClassList(UiClasses.ButtonReady, !problem);
            if (review) BuildReview();
        }

        private void BuildReview()
        {
            var strings = Ui.Data.Strings;
            var body = Root.Q(UiNames.ReviewBody);
            body.Clear();
            var seed = Row(strings.Get(StringKeys.CreationReviewSeed), strings.Get(StringKeys.CreationReviewSeedHint));
            seed.Add(_seedField);
            body.Add(seed);
            var destination = Row(strings.Get(StringKeys.CreationReviewDestination), strings.Get(StringKeys.CreationReviewDestinationHint));
            var slot = Text(new LocLabel(), FrontClasses.ReviewValue, strings.Format(StringKeys.CreationReviewSlot, new StringArgs().Add(UiPlaceholders.Slot, _slot)));
            destination.Add(slot);
            body.Add(destination);
            var review = ReviewOf(ViewModel.Selected);
            if (review == null)
            {
                body.Add(Text(new LocLabel(), FrontClasses.ReviewHeading, strings.Get(StringKeys.CreationReviewNoPreview)));
                return;
            }
            var pieces = new VisualElement();
            pieces.AddToClassList(FrontClasses.ReviewPieces);
            body.Add(pieces);
            pieces.Add(Column(strings.Get(StringKeys.CreationReviewCharacter), CharacterCard(review)));
            if (review.Armour != null) pieces.Add(Column(review.Armour.Heading, PieceCard(review.Armour)));
            if (review.MainHand != null) pieces.Add(Column(review.MainHand.Heading, PieceCard(review.MainHand)));
            if (review.Relic != null) pieces.Add(Column(review.Relic.Heading, PieceCard(review.Relic)));
            body.Add(Text(new LocLabel(), FrontClasses.ReviewHeading, strings.Format(StringKeys.CreationReviewDeck, new StringArgs().Add(UiPlaceholders.Count, review.DeckSize))));
            var deck = new VisualElement();
            deck.AddToClassList(FrontClasses.ReviewDeck);
            body.Add(deck);
            foreach (var entry in review.Deck)
            {
                var item = new VisualElement { pickingMode = PickingMode.Ignore };
                item.AddToClassList(FrontClasses.ReviewDeckItem);
                var face = new CardView { focusable = false, pickingMode = PickingMode.Ignore };
                face.AddToClassList(FrontClasses.ReviewDeckCard);
                face.Bind(ItemFaces.DeckCard(Ui.Data, entry.Card));
                item.Add(face);
                if (entry.Count > 1) item.Add(Text(new LocLabel(), FrontClasses.ReviewDeckCount, strings.Format(StringKeys.CreationReviewCopies, new StringArgs().Add(UiPlaceholders.Count, entry.Count))));
                deck.Add(item);
            }
        }

        private static VisualElement Row(string title, string hint)
        {
            var row = new VisualElement();
            row.AddToClassList(FrontClasses.ReviewRow);
            var texts = new VisualElement { pickingMode = PickingMode.Ignore };
            texts.AddToClassList(FrontClasses.ReviewRowTexts);
            texts.Add(Text(new LocLabel(), FrontClasses.ReviewRowTitle, title));
            texts.Add(Text(new LocLabel(), FrontClasses.ReviewRowHint, hint));
            row.Add(texts);
            return row;
        }

        private static VisualElement Column(string heading, VisualElement card)
        {
            var column = new VisualElement { pickingMode = PickingMode.Ignore };
            column.AddToClassList(FrontClasses.ReviewPieceColumn);
            var label = Text(new LocLabel(), FrontClasses.ReviewHeading, heading);
            label.AddToClassList(UiClasses.Upper);
            label.SetResolved(heading);
            column.Add(label);
            column.Add(card);
            return column;
        }

        /// <summary>The CHARACTER card: the class and its epithet, the five attributes and the figures of the run.</summary>
        private VisualElement CharacterCard(CreationReviewView review)
        {
            var card = new VisualElement { pickingMode = PickingMode.Ignore };
            card.AddToClassList(FrontClasses.ReviewPiece);
            card.AddToClassList(FrontClasses.ReviewChar);
            var eyebrow = Text(new LocLabel(), FrontClasses.ReviewCharEyebrow, review.ClassName);
            eyebrow.AddToClassList(UiClasses.Upper);
            eyebrow.SetResolved(review.ClassName);
            card.Add(eyebrow);
            card.Add(Text(new LocLabel(), FrontClasses.ReviewCharName, review.Epithet));
            card.Add(Grid(review.Attributes));
            var rule = new VisualElement { pickingMode = PickingMode.Ignore };
            rule.AddToClassList(FrontClasses.ReviewCharRule);
            card.Add(rule);
            card.Add(Grid(review.Resources));
            return card;
        }

        private static VisualElement Grid(IEnumerable<KeyValuePair<string, string>> cells)
        {
            var grid = new VisualElement { pickingMode = PickingMode.Ignore };
            grid.AddToClassList(FrontClasses.ReviewCharGrid);
            foreach (var cell in cells)
            {
                var box = new VisualElement { pickingMode = PickingMode.Ignore };
                box.AddToClassList(FrontClasses.ReviewCharCell);
                var label = Text(new LocLabel(), FrontClasses.ReviewCharLabel, cell.Key);
                label.AddToClassList(UiClasses.Upper);
                label.SetResolved(cell.Key);
                box.Add(label);
                box.Add(Text(new LocLabel(), FrontClasses.ReviewCharValue, cell.Value));
                grid.Add(box);
            }
            return grid;
        }

        /// <summary>An ARMOUR, MAIN HAND or RELIC card: the name, the picture, the numbers, the sentence and what it is.</summary>
        private VisualElement PieceCard(CreationPiece piece)
        {
            var face = piece.Face;
            var card = new VisualElement { pickingMode = PickingMode.Ignore };
            card.AddToClassList(FrontClasses.ReviewPiece);
            card.Add(Text(new LocLabel(), FrontClasses.ReviewPieceName, face.Name));
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.AddToClassList(FrontClasses.ReviewPieceArt);
            BoardHud.SetImage(art, Ui.Texture(face.ArtId) ?? Ui.Icon(face.Kind == Core2RewardsValues.KindArmor ? FrontArt.IconArmour : face.Kind == Core2RewardsValues.KindWeapons ? FrontArt.IconSword : FrontArt.IconRelic));
            card.Add(art);
            if (face.Stats.Count > 0)
            {
                var stats = new VisualElement { pickingMode = PickingMode.Ignore };
                stats.AddToClassList(FrontClasses.ReviewPieceStats);
                foreach (var stat in face.Stats) stats.Add(Text(new LocLabel(), FrontClasses.ReviewPieceStat, Ui.Data.Strings.Format(StringKeys.CreationReviewStat, new StringArgs().Add(UiPlaceholders.Value, stat.Value).Add(UiPlaceholders.Label, stat.Key))));
                card.Add(stats);
            }
            if (!string.IsNullOrEmpty(face.PackageLine)) card.Add(Text(new LocLabel(), FrontClasses.ReviewPieceText, face.PackageLine));
            if (!string.IsNullOrEmpty(face.Effect)) card.Add(Text(new LocLabel(), FrontClasses.ReviewPieceText, face.Effect));
            var kind = Text(new LocLabel(), FrontClasses.ReviewPieceKind, face.KindLabel);
            kind.AddToClassList(UiClasses.Upper);
            kind.SetResolved(face.KindLabel);
            card.Add(kind);
            return card;
        }

        // ------------------------------------------------------------------ steps

        private void OnPane(string id)
        {
            if (id == FrontValues.PaneReview) GoReview(_shell.PrimaryButton);
            else ViewModel.Pane = FrontValues.PaneClass;
        }

        /// <summary>The primary: Next on the Class pane, Begin on Review.</summary>
        private void Primary(VisualElement anchor)
        {
            if (ViewModel.Pane == FrontValues.PaneReview) Begin(anchor);
            else GoReview(anchor);
        }

        private void GoBack()
        {
            if (ViewModel.Pane == FrontValues.PaneReview) ViewModel.Pane = FrontValues.PaneClass;
            else Nav.Pop();
        }

        public override bool HandleBack()
        {
            if (ViewModel.Pane != FrontValues.PaneReview) return false;
            ViewModel.Pane = FrontValues.PaneClass;
            return true;
        }

        /// <summary>Review needs a class that can begin; otherwise it refuses with the reason at the control that asked (US-2.1).</summary>
        private bool Refused(VisualElement anchor)
        {
            var selected = ViewModel.Selected;
            if (selected == null) { RunFlow.Refuse(Context, anchor, StringKeys.CreationRefusalNoClass); return true; }
            var problem = _rules.Problem(selected);
            if (problem == null) return false;
            Debug.LogWarning(string.Format(CultureInfo.InvariantCulture, UiMessages.ClassRefused, selected, problem));
            RunFlow.Refuse(Context, anchor, _rules.Locked(selected) ? StringKeys.Core2CreationLocked : StringKeys.CreationProblem);
            return true;
        }

        private void GoReview(VisualElement anchor)
        {
            if (Refused(anchor)) return;
            ViewModel.Pane = FrontValues.PaneReview;
        }

        /// <summary>Begin (US-2.1: always pressable; refuses with the reason and keeps focus on the control that asked).</summary>
        private void Begin(VisualElement anchor)
        {
            if (Refused(anchor)) return;
            uint seed;
            try { seed = string.IsNullOrWhiteSpace(_seedField.value) ? RunFlow.NewSeed() : _content.Seeds.Parse(_seedField.value); }
            catch (FormatException)
            {
                _seedField.AddToClassList(FrontClasses.ReviewSeedBad);
                RunFlow.Refuse(Context, anchor, StringKeys.CreationReviewSeedProblem);
                return;
            }
            var slot = SlotSummaries.ReadOne(Ui.Saves, _slot);
            if (slot.Status != SlotStatus.Empty)
            {
                ConfirmRequest.Open(Nav, new ConfirmRequest
                {
                    ConfirmId = ConfirmIds.ReplaceSlot,
                    Args = new StringArgs().Add(UiPlaceholders.Slot, _slot),
                    Target = RunFlow.SlotFacts(Ui, slot),
                    OnConfirm = () => StartRun(seed),
                });
                return;
            }
            StartRun(seed);
        }

        private void StartRun(uint seed)
        {
            var hosted = RunHost.Start(_content, Ui.Saves, _slot, seed, ViewModel.Selected, Ui.Data.Strings.Get(_content.Flow.NameKey));
            Ui.Core2Session = hosted.Core2;
            var opening = RunFlow.OpeningOverride != false
                ? PrologueFlow.For(_content, hosted.Core2.PlayerSettings, hosted.Core2.ClassId, hosted.Core2.Name)
                : null;
            if (opening != null)
            {
                Nav.Go(ScreenIds.Prologue, new PrologueArgs { Session = hosted.Core2, Opening = opening }, true);
                return;
            }
            Nav.Fire(ScreenTriggers.Begin, new Core2RunArgs { Session = hosted.Core2 });
        }
    }
}
