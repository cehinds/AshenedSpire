using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The Progression hub's attribute banner, Character Sheet button and the two dialogs they open (web ui/components/progressionCards.js attributesCard, characterStatsButton, openStatsInspection and
    /// openProgressionAllocation; statAllocationCard.js; creationCards.js primaryStatCards): the Character Sheet lists the pools with their formulas and one card per attribute, and the allocation dialog previews
    /// the points the character holds. Both open over the Armoury as a second modal and close with Back, Cancel, Escape or the close glyph.
    /// </summary>
    public sealed partial class ArmouryScreen
    {
        private enum DialogKind { None, Sheet, Allocation, Lore }

        private VisualElement _dialogScrim;
        private CombatModal _dialogModal;
        private DialogKind _dialogKind;
        private VisualElement _dialogOpener;
        private AttributeAllocation _allocation;
        private string _paneCard;
        private string _dialogCard;
        private string _dialogFocus;
        private LoreParts _loreParts;
        private string _loreTitle;

        private bool DialogOpen => _dialogKind != DialogKind.None;

        private void BindDialog()
        {
            _dialogScrim = Root.Q(UiNames.ArmouryDialogScrim);
            _dialogModal = Root.Q<CombatModal>(UiNames.ArmouryDialog);
            if (_dialogScrim == null || _dialogModal == null) return;
            _dialogModal.CloseRequested += CloseDialog;
            _dialogScrim.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.target == _dialogScrim) CloseDialog();
            });
        }

        private void Tip(VisualElement target, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Tooltip.AttachResolved(target, Context.Overlay, Ui.Data.Components.TooltipPlain, text, string.Empty, Ui.Data.Tokens.Duration(TokenKeys.TooltipDelay));
        }

        // ------------------------------------------------------------------ the pane's two cards

        /// <summary>attributesCard: the banner (the Attributes title over the short names and their numbers; N points available) and, under it, every attribute's card.</summary>
        private void AttributesCard(VisualElement host, CharacterProgressionView view)
        {
            var open = _open.Contains(ArmouryValues.FoldAttributes);
            var section = new VisualElement();
            section.AddToClassList(ArmouryClasses.ProgressionCard);
            host.Add(section);
            var banner = new VisualElement();
            banner.AddToClassList(ProgressionClasses.Banner);
            section.Add(banner);
            var toggle = new VisualElement { focusable = true, pickingMode = PickingMode.Position, userData = ArmouryValues.FoldAttributes };
            toggle.AddToClassList(ProgressionClasses.Toggle);
            Label(toggle, Strings.Get(StringKeys.ProgressionAttributes), ProgressionClasses.ToggleTitle);
            SummaryLine(toggle, view.Attributes.Select(a => new KeyValuePair<string, string>(a.Short, a.Value)));
            toggle.RegisterCallback<ClickEvent>(_ =>
            {
                if (!_open.Add(ArmouryValues.FoldAttributes)) _open.Remove(ArmouryValues.FoldAttributes);
                _focusKey = ArmouryValues.FoldAttributes;
                Render();
            });
            Tip(toggle, Strings.Get(open ? StringKeys.ProgressionAttributesCollapse : StringKeys.ProgressionAttributesExpand));
            banner.Add(toggle);

            var points = new LocLabel { focusable = true, pickingMode = PickingMode.Position, userData = AttrCardValues.PoolKey };
            points.AddToClassList(ProgressionClasses.Points);
            points.EnableInClassList(ProgressionClasses.PointsOn, view.Points > 0);
            points.SetResolved(view.PointsLabel);
            points.RegisterCallback<ClickEvent>(_ =>
            {
                if (view.Points > 0) OpenAllocation(points);
            });
            Tip(points, Strings.Get(view.Points > 0 ? StringKeys.ProgressionPointsTipOpen : StringKeys.ProgressionPointsTipNone));
            banner.Add(points);

            if (!open) return;
            var detail = new VisualElement { pickingMode = PickingMode.Ignore };
            detail.AddToClassList(ProgressionClasses.Detail);
            section.Add(detail);
            Label(detail, Strings.Get(StringKeys.ProgressionAttributesNote), ArmouryClasses.Muted);
            foreach (var card in view.Stats.Attributes)
                StatCard(detail, card, true, null, _paneCard, key =>
                {
                    _paneCard = _paneCard == key ? null : key;
                    _focusKey = key;
                    Render();
                });
        }

        /// <summary>characterStatsButton: the Character Sheet title over each stat's face label and number; pressing it opens the inspection.</summary>
        private void SheetButton(VisualElement host, CharacterProgressionView view)
        {
            var button = new VisualElement { focusable = true, pickingMode = PickingMode.Position, userData = StringKeys.ProgressionSheet };
            button.AddToClassList(ProgressionClasses.SheetButton);
            Label(button, Strings.Get(StringKeys.ProgressionSheet), ProgressionClasses.ToggleTitle);
            SummaryLine(button, view.StatChips);
            button.RegisterCallback<ClickEvent>(_ => OpenSheet(button));
            Tip(button, Strings.Get(StringKeys.ProgressionSheetTip));
            host.Add(button);
        }

        private void SummaryLine(VisualElement host, IEnumerable<KeyValuePair<string, string>> pairs)
        {
            var line = new VisualElement { pickingMode = PickingMode.Ignore };
            line.AddToClassList(ProgressionClasses.SummaryLine);
            foreach (var pair in pairs)
            {
                Label(line, pair.Key + ArmouryKeys.Colon, ProgressionClasses.SummaryKey);
                Label(line, pair.Value, ProgressionClasses.SummaryValue);
            }
            host.Add(line);
        }

        /// <summary>One primary stat card (creationCards.js renderPrimaryStatCard): the face (name with its short label, the benefit it leads with, the number) and, when open, the reveal (name, sense, every stat it feeds).</summary>
        private void StatCard(VisualElement host, AttributeCardView card, bool showValue, VisualElement trail, string openCard, Action<string> toggle)
        {
            var key = string.Format(System.Globalization.CultureInfo.InvariantCulture, AttrCardValues.CardKey, card.Id);
            var open = openCard == key;
            var wrap = new VisualElement();
            wrap.AddToClassList(ProgressionClasses.StatCard);
            host.Add(wrap);
            var face = new VisualElement { focusable = true, pickingMode = PickingMode.Position, userData = key };
            face.AddToClassList(ProgressionClasses.StatFace);
            face.EnableInClassList(ProgressionClasses.StatFaceOpen, open);
            wrap.Add(face);
            var text = new VisualElement { pickingMode = PickingMode.Ignore };
            text.style.flexGrow = 1;
            text.style.flexShrink = 1;
            face.Add(text);
            Label(text, card.FaceLabel, ProgressionClasses.StatName);
            Label(text, card.MainSummary, ProgressionClasses.StatHint);
            if (showValue) Label(face, ProgressionStatsFormat(card.Value), ProgressionClasses.StatValue);
            if (trail != null) face.Add(trail);
            face.RegisterCallback<ClickEvent>(_ => toggle(key));
            Tip(face, card.Tooltip);
            if (!open) return;
            var reveal = new VisualElement { pickingMode = PickingMode.Ignore };
            reveal.AddToClassList(ProgressionClasses.StatReveal);
            wrap.Add(reveal);
            Label(reveal, card.Title, ProgressionClasses.StatRevealName);
            if (card.Sense.Length > 0) Label(reveal, card.Sense, ArmouryClasses.Muted);
            foreach (var line in card.Lines) Label(reveal, line, ProgressionClasses.StatRevealLine);
        }

        private static string ProgressionStatsFormat(double value) => Ashen.Domain.Run.RunJs.NumStr(value);

        // ------------------------------------------------------------------ the dialogs

        private void ShowDialog(DialogKind kind, VisualElement opener)
        {
            if (_dialogModal == null) return;
            _dialogKind = kind;
            _dialogOpener = opener;
            UiDom.Show(_dialogScrim, true);
            RenderDialog();
        }

        private void CloseDialog()
        {
            if (!DialogOpen) return;
            _dialogKind = DialogKind.None;
            _allocation = null;
            _dialogCard = null;
            UiDom.Show(_dialogScrim, false);
            _dialogModal.Reset();
            var opener = _dialogOpener;
            _dialogOpener = null;
            if (opener != null && opener.panel != null) Nav.FocusTop(opener);
        }

        private void RenderDialog()
        {
            _dialogModal.Reset();
            if (_dialogKind == DialogKind.Sheet) RenderSheet();
            else if (_dialogKind == DialogKind.Allocation) RenderAllocation();
            else if (_dialogKind == DialogKind.Lore) RenderLore();
            var target = _dialogFocus != null ? _dialogModal.Query<VisualElement>().Where(e => e.focusable && _dialogFocus.Equals(e.userData)).First() : null;
            target ??= _dialogModal.Body.Query<VisualElement>().Where(e => e.focusable && e != _dialogModal.Body).First() ?? _dialogModal.CloseButton;
            Nav.FocusTop(target);
        }

        // ---- openStatsInspection

        private void OpenSheet(VisualElement opener)
        {
            _dialogCard = null;
            _dialogFocus = null;
            ShowDialog(DialogKind.Sheet, opener);
        }

        private void RenderSheet()
        {
            var stats = ProgressionStats.For(_run, Ui.Data);
            _dialogModal.size = UiValues.ModalSizeLg;
            _dialogModal.SetHead(RunHudView.ClassName(Ui.Data, _run.ClassId), Strings.Get(StringKeys.ProgressionSheet));
            var body = _dialogModal.Body;
            Label(body, Strings.Get(StringKeys.ProgressionCurrentStats), ArmouryClasses.Eyebrow, UiClasses.Upper);
            var grid = new VisualElement { pickingMode = PickingMode.Ignore };
            grid.AddToClassList(ProgressionClasses.StatGrid);
            body.Add(grid);
            foreach (var row in stats.Rows)
            {
                var chip = new VisualElement { focusable = true, pickingMode = PickingMode.Position, userData = row.Id };
                chip.AddToClassList(ProgressionClasses.StatChip);
                Label(chip, row.FaceLabel, ProgressionClasses.StatChipKey);
                Label(chip, ProgressionStatsFormat(row.Value), ProgressionClasses.StatChipValue);
                grid.Add(chip);
                Tip(chip, row.Label + ArmouryKeys.Colon + ArmouryKeys.Space + (row.Formula.Length > 0 ? row.Formula : ProgressionStatsFormat(row.Value)));
            }
            Label(body, Strings.Get(StringKeys.ProgressionSheetNote), ArmouryClasses.Muted);
            foreach (var card in stats.Attributes)
                StatCard(body, card, true, null, _dialogCard, key =>
                {
                    _dialogCard = _dialogCard == key ? null : key;
                    _dialogFocus = key;
                    RenderDialog();
                });
            DialogButton(StringKeys.ConfirmBack, UiClasses.ButtonBack, CloseDialog);
        }

        // ---- the tooltip glossary (tooltipGlossary.js decorateKeywords)

        /// <summary>The words of a text the glossary explains, each on hover: a line that names a term, a status or Block says what it means (the web underlines the word and explains it where it stands).</summary>
        private void Glossed(Label line, string text)
        {
            var entries = _run.Glossary(Ui.Data).Explain(text);
            if (entries.Count == 0) return;
            line.pickingMode = PickingMode.Position;
            line.focusable = true;
            var body = string.Join(Strings.Get(StringKeys.GlossaryJoin), entries.Select(e => entries.Count == 1 ? e.Explanation : Strings.Format(StringKeys.GlossaryEntry, new StringArgs().Add(UiPlaceholders.Name, e.Name).Add(UiPlaceholders.Text, e.Explanation))));
            Tooltip.AttachResolved(line, Context.Overlay, Ui.Data.Components.TooltipPlain, entries.Count == 1 ? entries[0].Name : text, body, Ui.Data.Tokens.Duration(TokenKeys.TooltipDelay));
        }

        // ---- the lore line and the lore window (loreLine.js)

        /// <summary>The lore line of an inspection: the identity line, and a Read cue that opens the window when the lore has more to say.</summary>
        private void Lore(VisualElement parent, string text, string title)
        {
            VisualElement line = null;
            line = LoreLine.Build(Strings, text, parts => OpenLore(parts, title, line));
            if (line != null) parent.Add(line);
        }

        private void OpenLore(LoreParts parts, string title, VisualElement opener)
        {
            _loreParts = parts;
            _loreTitle = title ?? string.Empty;
            _dialogFocus = null;
            ShowDialog(DialogKind.Lore, opener);
        }

        private void RenderLore()
        {
            _dialogModal.size = UiValues.ModalSizeMd;
            _dialogModal.SetHead(Strings.Get(StringKeys.LoreWindowEyebrow), _loreTitle);
            LoreLine.Window(_dialogModal.Body, _loreParts);
            DialogButton(StringKeys.LoreWindowClose, UiClasses.ButtonBack, CloseDialog);
        }

        // ---- openProgressionAllocation

        private void OpenAllocation(VisualElement opener)
        {
            var allocation = new AttributeAllocation(_run, Ui.Data);
            if (allocation.Points == 0) return;
            _allocation = allocation;
            _dialogCard = null;
            _dialogFocus = null;
            ShowDialog(DialogKind.Allocation, opener);
        }

        private LocButton DialogButton(string key, string kind, Action action)
        {
            var button = new LocButton(key);
            button.AddToClassList(UiClasses.Button);
            if (kind != null) button.AddToClassList(kind);
            button.userData = key;
            button.clicked += action;
            _dialogModal.Foot.Add(button);
            return button;
        }

        private void RenderAllocation()
        {
            var allocation = _allocation;
            _dialogModal.size = UiValues.ModalSizeMd;
            _dialogModal.SetHead(Strings.Get(StringKeys.ProgressionAttributes), Strings.Get(StringKeys.ProgressionAllocateTitle));
            var body = _dialogModal.Body;
            var pool = new VisualElement { pickingMode = PickingMode.Ignore };
            pool.AddToClassList(ProgressionClasses.Pool);
            body.Add(pool);
            Label(pool, Strings.Get(StringKeys.ProgressionAllocatePool), ArmouryClasses.Muted);
            Label(pool, allocation.Remaining.ToString(System.Globalization.CultureInfo.InvariantCulture), ProgressionClasses.StatChipValue);
            Label(body, Strings.Get(StringKeys.ProgressionAllocateNote), ProgressionClasses.DialogNote);
            var rows = new VisualElement { pickingMode = PickingMode.Ignore };
            rows.AddToClassList(ProgressionClasses.AllocRows);
            body.Add(rows);
            foreach (var row in allocation.Rows())
            {
                var trail = new VisualElement { pickingMode = PickingMode.Position };
                trail.AddToClassList(ProgressionClasses.StatTrail);
                trail.AddToClassList(ProgressionClasses.Stepper);
                Step(trail, row, false);
                Label(trail, ProgressionStatsFormat(row.Value), ProgressionClasses.StepperValue);
                Step(trail, row, true);
                StatCard(rows, row.Card, false, trail, _dialogCard, key =>
                {
                    _dialogCard = _dialogCard == key ? null : key;
                    _dialogFocus = key;
                    RenderDialog();
                });
            }
            DialogButton(StringKeys.ProgressionAllocateCancel, UiClasses.ButtonBack, CloseDialog);
            var done = DialogButton(StringKeys.ProgressionAllocateDone, UiClasses.ButtonPrimary, () =>
            {
                if (!allocation.DoneDisabled && allocation.Commit()) CloseDialog();
            });
            done.EnableInClassList(ArmouryClasses.ActionOff, allocation.DoneDisabled);
            done.EnableInClassList(UiClasses.ButtonReady, !allocation.DoneDisabled);
        }

        /// <summary>One stepper button (statAllocationCard.js stepperFor): pressing it spends or takes back a point and does not open the card.</summary>
        private void Step(VisualElement trail, AllocationRow row, bool increase)
        {
            var allowed = increase ? row.CanIncrease : row.CanDecrease;
            var key = string.Format(System.Globalization.CultureInfo.InvariantCulture, AttrCardValues.StepperKey, row.Id, increase ? AttrCardValues.StepUp : AttrCardValues.StepDown);
            var button = new LocLabel { focusable = true, pickingMode = PickingMode.Position, userData = key };
            button.AddToClassList(ProgressionClasses.StepperButton);
            button.EnableInClassList(ProgressionClasses.StepperOff, !allowed);
            button.SetResolved(Strings.Get(increase ? StringKeys.ProgressionAllocatePlus : StringKeys.ProgressionAllocateMinus));
            button.RegisterCallback<ClickEvent>(e =>
            {
                e.StopPropagation();
                if (!allowed) return;
                if (increase) _allocation.Increase(row.Id);
                else _allocation.Decrease(row.Id);
                _dialogFocus = key;
                RenderDialog();
            });
            Tip(button, Strings.Format(increase ? StringKeys.ProgressionAllocateIncrease : StringKeys.ProgressionAllocateDecrease, new StringArgs().Add(UiPlaceholders.Label, row.Label)));
            trail.Add(button);
        }
    }
}
