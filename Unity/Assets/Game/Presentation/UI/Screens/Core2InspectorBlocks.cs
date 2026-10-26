using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine.UIElements;
using X = Ashen.Generated.InspectorValues;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The details pane of the combatant door (web src/ui/components/combatantInspector.js combatantDetailBody, src/ui/components/enemyKnowledge.js, styles/kit.css .combatant-inspector-*): each block of
    /// the view model (<see cref="CombatantInspectorView"/>) as the kit pieces the web builds it from: a section (eyebrow, hairline, rows of a label and a status line), the active list (a fold per
    /// entry), the bestiary and the prediction (prose, status lines, stages, a choice and a button), the enemy deck (two folds and the authored deck, each with its inert cards) and the revealed hand.
    /// Pure drawing: the words are the view model's; the only thing it sends back is a prediction.
    /// </summary>
    internal static class Core2InspectorBlocks
    {
        private static LocLabel Text(string text, string className)
        {
            var label = new LocLabel { pickingMode = PickingMode.Ignore };
            label.AddToClassList(className);
            label.AddToClassList(UiClasses.ValueText);
            label.SetResolved(text);
            return label;
        }

        private static VisualElement Hairline()
        {
            var line = new VisualElement { pickingMode = PickingMode.Ignore };
            line.AddToClassList(UiClasses.InspectorHairline);
            return line;
        }

        private static LocLabel Eyebrow(string text)
        {
            var label = Text(text, UiClasses.InspectorEyebrow);
            label.AddToClassList(UiClasses.Upper);
            return label;
        }

        private static VisualElement Row(string label, string status, bool disabled)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.AddToClassList(UiClasses.InspectorRow);
            row.EnableInClassList(UiClasses.InspectorRowDisabled, disabled);
            row.Add(Text(label, UiClasses.InspectorRowLabel));
            if (!string.IsNullOrEmpty(status)) row.Add(Text(status, UiClasses.InspectorRowStatus));
            return row;
        }

        private static VisualElement Panel()
        {
            var panel = new VisualElement { pickingMode = PickingMode.Ignore };
            panel.AddToClassList(UiClasses.InspectorSection);
            return panel;
        }

        /// <summary>The inert cards of the kit's option list (name, what it does, the meta line): the live one is marked.</summary>
        private static VisualElement Cards(IEnumerable<InspectorCard> cards)
        {
            var list = new VisualElement { pickingMode = PickingMode.Ignore };
            list.AddToClassList(UiClasses.InspectorCards);
            foreach (var card in cards)
            {
                var box = new VisualElement { pickingMode = PickingMode.Ignore };
                box.AddToClassList(UiClasses.InspectorCard);
                box.EnableInClassList(UiClasses.InspectorCardSelected, card.Selected);
                box.Add(Text(card.Name, UiClasses.InspectorCardName));
                if (!string.IsNullOrEmpty(card.Detail)) box.Add(Text(card.Detail, UiClasses.InspectorProse));
                if (!string.IsNullOrEmpty(card.Meta)) box.Add(Text(card.Meta, UiClasses.InspectorCardMeta));
                list.Add(box);
            }
            return list;
        }

        /// <summary>A fold: a header that opens and closes what is under it (a details element of the web, open to begin with).</summary>
        private static VisualElement Fold(StringTable strings, string summary, string headClass, params VisualElement[] content)
        {
            var fold = new VisualElement();
            fold.AddToClassList(UiClasses.InspectorFold);
            var head = new VisualElement();
            head.AddToClassList(UiClasses.InspectorFoldHead);
            head.focusable = true;
            var mark = Text(strings.Get(StringKeys.CombatantFoldOpen), UiClasses.InspectorFoldMark);
            head.Add(mark);
            head.Add(Text(summary, headClass));
            var body = new VisualElement();
            foreach (var element in content) body.Add(element);
            var open = true;
            void Toggle()
            {
                open = !open;
                mark.SetResolved(strings.Get(open ? StringKeys.CombatantFoldOpen : StringKeys.CombatantFoldClosed));
                UiDom.Show(body, open);
            }
            head.RegisterCallback<ClickEvent>(_ => Toggle());
            head.RegisterCallback<NavigationSubmitEvent>(_ => Toggle());
            fold.Add(head);
            fold.Add(body);
            return fold;
        }

        private static VisualElement Section(InspectorBlock block)
        {
            var section = Panel();
            section.Add(Eyebrow(block.Title));
            section.Add(Hairline());
            foreach (var row in block.Rows) section.Add(Row(row.Label, row.Status, row.Disabled));
            return section;
        }

        private static VisualElement Abilities(StringTable strings, InspectorBlock block)
        {
            var list = Panel();
            list.Add(Eyebrow(block.Title));
            list.Add(Hairline());
            foreach (var item in block.Items)
            {
                var entry = Fold(strings, item.Name, UiClasses.InspectorAbilityName, Text(item.Detail, UiClasses.InspectorProse));
                entry.AddToClassList(UiClasses.InspectorAbility);
                list.Add(entry);
            }
            if (!string.IsNullOrEmpty(block.Empty)) list.Add(Text(block.Empty, UiClasses.InspectorStatus));
            return list;
        }

        private static VisualElement RevealedHand(InspectorBlock block)
        {
            var section = Panel();
            section.Add(Eyebrow(block.Title));
            section.Add(Hairline());
            foreach (var row in block.Rows) section.Add(Row(row.Label, row.Status, false));
            if (!string.IsNullOrEmpty(block.Empty)) section.Add(Row(block.Empty, null, true));
            section.Add(Text(block.Counts, UiClasses.InspectorStatus));
            return section;
        }

        private static VisualElement Deck(StringTable strings, InspectorBlock block)
        {
            var section = Panel();
            section.Add(Eyebrow(block.Title));
            section.Add(Hairline());
            section.Add(Text(block.Note, UiClasses.InspectorStatus));
            foreach (var fold in new[] { block.Current, block.Last, block.Authored })
                section.Add(Fold(strings, fold.Summary, UiClasses.InspectorFoldTitle, Cards(fold.Cards)));
            return section;
        }

        private static VisualElement Lines(IEnumerable<InspectorLine> lines, Func<InspectorLine, VisualElement> special)
        {
            var panel = Panel();
            foreach (var line in lines)
            {
                if (line.Kind == X.LineEyebrow) panel.Add(Eyebrow(line.Text));
                else if (line.Kind == X.LineTitle) panel.Add(Text(line.Text, UiClasses.InspectorTitle));
                else if (line.Kind == X.LineProse) panel.Add(Text(line.Text, UiClasses.InspectorProse));
                else if (line.Kind == X.LineStatus) panel.Add(Text(line.Text, UiClasses.InspectorStatus));
                else if (line.Kind == X.LineStage)
                {
                    var stage = Text(line.Text, UiClasses.InspectorProse);
                    stage.EnableInClassList(UiClasses.InspectorStageLearned, line.Learned);
                    panel.Add(stage);
                }
                else if (line.Kind == X.LineHairline) panel.Add(Hairline());
                else
                {
                    var made = special?.Invoke(line);
                    if (made != null) panel.Add(made);
                }
            }
            return panel;
        }

        /// <summary>
        /// renderIntentPrediction: what the prediction says and, when the rules take one now, the choice of the action expected and the button that sends it. <paramref name="predict"/> answers whether the
        /// rules accepted the maneuver; the line says so in place and the controls are put down, as the web's.
        /// </summary>
        private static VisualElement Prediction(StringTable strings, InspectorBlock block, Func<string, bool> predict)
        {
            DropdownField choice = null;
            LocLabel status = null;
            LocButton commit = null;
            var panel = Lines(block.Lines, line =>
            {
                if (line.Kind == X.LineSelect)
                {
                    var host = new VisualElement();
                    host.AddToClassList(UiClasses.InspectorRow);
                    host.Add(Text(line.Label, UiClasses.InspectorRowLabel));
                    choice = new DropdownField(line.Choices, 0);
                    choice.AddToClassList(UiClasses.InspectorSelect);
                    host.Add(choice);
                    return host;
                }
                if (line.Kind == X.LineButton)
                {
                    commit = new LocButton();
                    commit.AddToClassList(UiClasses.InspectorButton);
                    commit.SetResolved(line.Text);
                    return commit;
                }
                return null;
            });
            status = panel.Query<LocLabel>(className: UiClasses.InspectorStatus).First();
            if (commit != null && predict != null)
                commit.clicked += () =>
                {
                    var maneuver = choice?.value;
                    if (string.IsNullOrEmpty(maneuver)) return;
                    commit.SetEnabled(false);
                    choice.SetEnabled(false);
                    if (predict(maneuver))
                    {
                        status?.SetResolved(strings.Format(StringKeys.KnowledgePredictionStatus, new StringArgs().Add(X.ArgPrediction, maneuver).Add(X.ArgStatus, strings.Get(StringKeys.KnowledgePredictionAwaiting))));
                        return;
                    }
                    status?.SetResolved(strings.Get(StringKeys.KnowledgePredictionFailed));
                    commit.SetEnabled(true);
                    choice.SetEnabled(true);
                };
            return panel;
        }

        /// <summary>The blocks of the pane in order, ready to add to the details column.</summary>
        public static List<VisualElement> Build(StringTable strings, CombatantInspectorView view, Func<string, bool> predict)
        {
            var blocks = new List<VisualElement>();
            foreach (var block in view.Blocks)
            {
                if (block.Kind == X.BlockSection) blocks.Add(Section(block));
                else if (block.Kind == X.BlockAbilities) blocks.Add(Abilities(strings, block));
                else if (block.Kind == X.BlockRevealedHand) blocks.Add(RevealedHand(block));
                else if (block.Kind == X.BlockKnowledge) blocks.Add(Lines(block.Lines, null));
                else if (block.Kind == X.BlockPrediction) blocks.Add(Prediction(strings, block, predict));
                else if (block.Kind == X.BlockDeck) blocks.Add(Deck(strings, block));
            }
            return blocks;
        }
    }
}
