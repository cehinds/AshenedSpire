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

        public string Selected { get => _selected; set => Set(ref _selected, value); }
    }

    /// <summary>
    /// W-04 creation, Class pane only (US-2.1, US-2.8; D-058 for the planned panes). The four classes come from content in
    /// authoring order, each with its portrait, name and summary; the selected class unfolds with its starting attributes,
    /// pools and flask split read from createRunState. The data default (rules/runFlow.json) is preselected when it can
    /// begin. The rail lists Class, Character, Equipment and Review with their values; only Class is built. Begin makes the
    /// climb through the run loop's newRun (W2c Replace first when the slot holds a climb) and opens the act map (W-06).
    /// </summary>
    public sealed class CreationScreen : ScreenView
    {
        private readonly List<LocButton> _tiles = new List<LocButton>();
        private IReadOnlyList<string> _classes = new string[0];
        private RunContent _content;
        private Shell _shell;
        private Workspace _workspace;
        private int _slot = 1;

        public CreationViewModel ViewModel { get; } = new CreationViewModel();
        public IReadOnlyList<LocButton> Tiles => _tiles;
        public IReadOnlyList<string> Classes => _classes;

        protected override void OnBind(object args)
        {
            _slot = (args as CreationArgs)?.SlotIndex ?? _slot;
            _content = Ui.RunContent;
            _classes = _content.ClassIds;
            _shell = Root.Q<Shell>(UiNames.Shell);
            _shell.SetFooter(StringKeys.CreationBack, StringKeys.CreationBegin);
            _shell.Exit += () => Nav.Pop();
            _shell.Back += () => Nav.Pop();
            _shell.Primary += () => Begin(_shell.PrimaryButton);
            _workspace = Root.Q<Workspace>(UiNames.Workspace);
            if (_workspace != null) _workspace.Rules = Ui.Data.Layout.CategoryNav;
            var list = Root.Q(UiNames.ClassList);
            foreach (var id in _classes)
            {
                var tile = new LocButton();
                tile.AddToClassList(UiClasses.ClassTile);
                var portrait = new VisualElement { pickingMode = PickingMode.Ignore };
                portrait.AddToClassList(UiClasses.ClassTilePortrait);
                Ui.ApplyBackground(portrait, _content.Portrait(id));
                tile.Insert(0, portrait);
                tile.EnableInClassList(UiClasses.ClassTileProblem, _content.ClassProblem(id) != null);
                var captured = id;
                tile.clicked += () => ViewModel.Selected = captured;
                list.Add(tile);
                _tiles.Add(tile);
            }
            ViewModel.Selected = _content.DefaultClass();
            ViewModel.Observe(Render);
            var index = _classes.ToList().IndexOf(ViewModel.Selected);
            Context.Instance.LastFocused = index >= 0 ? (VisualElement)_tiles[index] : _shell.PrimaryButton;
        }

        private string ClassName(string id) => RunFlow.ClassName(Ui, id);

        private void Render()
        {
            var strings = Ui.Data.Strings;
            var selected = ViewModel.Selected;
            for (var i = 0; i < _tiles.Count; i++)
            {
                var on = _classes[i] == selected;
                _tiles[i].EnableInClassList(UiClasses.Selected, on);
                _tiles[i].SetResolved(strings.Format(StringKeys.CreationTile, new StringArgs()
                    .Add(UiPlaceholders.Mark, strings.Get(on ? StringKeys.GlyphRadioOn : StringKeys.GlyphRadioOff)).Add(UiPlaceholders.Name, ClassName(_classes[i]))));
            }
            _workspace?.SetCategories(Ui.Data.Menus.CreationPanes.Select(p => new CategoryItem
            {
                Id = p.Id,
                LabelKey = p.LabelKey,
                Count = p.Built ? ClassName(selected) : strings.Get(StringKeys.CreationPaneLater),
                Enabled = p.Built,
            }).ToList(), Ui.Data.Menus.CreationPanes.FirstOrDefault(p => p.Built)?.Id);

            var figure = Root.Q(UiNames.ClassFigure);
            if (figure != null) Ui.ApplyBackground(figure, selected != null ? _content.Portrait(selected) : null);
            Root.Q<LocLabel>(UiNames.ClassName)?.SetResolved(ClassName(selected));
            Root.Q<LocLabel>(UiNames.ClassSummary)?.SetResolved(selected == null ? string.Empty : strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.ClassDescriptionKey, selected)));
            var preview = selected != null ? _content.Preview(selected) : null;
            var stats = preview == null ? string.Empty : string.Join(strings.Get(StringKeys.CreationStatJoin), preview.Attributes.Select(a => strings.Format(StringKeys.CreationStat, new StringArgs()
                .Add(UiPlaceholders.Name, strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.AttributeShortKey, a.Key))).Add(UiPlaceholders.Value, (int)a.Value))));
            Line(UiNames.ClassStats, stats);
            Line(UiNames.ClassDerived, preview == null ? string.Empty : strings.Format(StringKeys.CreationDerived, new StringArgs()
                .Add(UiPlaceholders.Hp, (int)preview.MaxHp).Add(UiPlaceholders.Mp, (int)preview.MaxMana).Add(UiPlaceholders.Sp, (int)preview.MaxStamina).Add(UiPlaceholders.Actions, (int)preview.Actions)));
            Line(UiNames.ClassFlasks, preview == null ? string.Empty : strings.Format(StringKeys.CreationFlasks, new StringArgs()
                .Add(UiPlaceholders.Hp, (int)preview.HpFlasks).Add(UiPlaceholders.Mp, (int)preview.ManaFlasks)));
            var problem = selected == null || _content.ClassProblem(selected) != null;
            UiDom.Show(Root.Q(UiNames.ClassProblem), selected != null && problem);
            _shell.PrimaryButton?.EnableInClassList(UiClasses.ButtonReady, !problem);
        }

        private void Line(string name, string text)
        {
            var label = Root.Q<LocLabel>(name);
            label?.SetResolved(text);
            UiDom.Show(label, !string.IsNullOrEmpty(text));
        }

        /// <summary>Begin (US-2.1: always pressable; refuses with the reason and keeps focus on the control that asked).</summary>
        private void Begin(VisualElement anchor)
        {
            var selected = ViewModel.Selected;
            if (selected == null) { RunFlow.Refuse(Context, anchor, StringKeys.CreationRefusalNoClass); return; }
            var problem = _content.ClassProblem(selected);
            if (problem != null)
            {
                Debug.LogWarning(string.Format(CultureInfo.InvariantCulture, UiMessages.ClassRefused, selected, problem));
                RunFlow.Refuse(Context, anchor, StringKeys.CreationProblem);
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
                    OnConfirm = StartRun,
                });
                return;
            }
            StartRun();
        }

        private void StartRun()
        {
            var session = RunSession.New(_content, Ui.Saves, _slot, RunFlow.NewSeed(), ViewModel.Selected, Ui.Data.Strings.Get(_content.Flow.NameKey), null, RunFlow.CustomOverride);
            Ui.Session = session;
            Nav.Fire(ScreenTriggers.Begin, new RunScreenArgs { Session = session });
        }
    }
}
