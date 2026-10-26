using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;
using KitCard = Ashen.Presentation.UI.Kit.CardView;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The combat overlays of a Core 2 fight (VP-6, D-800), drawn over the board in the web's modal looks: the piles viewer (the draw pile as a grid of faces, the spent piles as discard and exhaust
    /// tabs with the reading of the selected card; src/ui/components/piles.js), the card inspector (a large face with its words and keywords; cardInspection.js), the combatant reading opened
    /// from the target card, and the quick menu under the menu button (quicknav.js). They read what the flow and its view already hold and send nothing to the rules. One overlay is open at a time;
    /// while it is, the screen's focus mode is <see cref="Core2UiValues.FocusOverlay"/> and Escape or back closes it (<see cref="HandleBack"/>), returning focus to the control that opened it.
    /// </summary>
    internal sealed class Core2Overlays
    {
        private enum Kind { None, Piles, Inspect, Detail, Quick }

        private readonly ScreenContext _context;
        private readonly UiContext _ui;
        private readonly StringTable _strings;
        private readonly Core2RunSession _run;
        private readonly Core2CombatFlow _flow;
        private readonly Func<Core2CardView, CardViewData> _cardData;
        private readonly VisualElement _host;
        private readonly CombatModal _modal;
        private readonly QuickMenu _quick;
        private IReadOnlyList<QuickMenuRow> _quickRows = new List<QuickMenuRow>();
        private Kind _kind;
        private VisualElement _opener;
        private string _pile;
        private string _selectedCard;

        public Core2Overlays(ScreenContext context, Core2RunSession run, Core2CombatFlow flow, Func<Core2CardView, CardViewData> cardData)
        {
            _context = context;
            _ui = context.Ui;
            _strings = _ui.Data.Strings;
            _run = run;
            _flow = flow;
            _cardData = cardData;
            var root = context.Root;
            _host = root.Q(UiNames.OverlayHost);
            _modal = root.Q<CombatModal>(UiNames.OverlayModal);
            _quick = root.Q<QuickMenu>(UiNames.OverlayQuick);
            _host.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.target == _host) Close();
            });
            _modal.CloseRequested += Close;
            _quick.Chosen += OnQuick;
            _quick.Refused += (id, anchor) => QuickMenuRows.Refuse(context, _quickRows, id, anchor);
        }

        /// <summary>True while an overlay is open (the screen's focus mode is then the overlay's).</summary>
        public bool IsOpen => _kind != Kind.None;

        /// <summary>The overlay closed (the screen refreshes and focus returned to its opener).</summary>
        public event Action Closed;

        /// <summary>Escape or back: closes the open overlay. True when one was open.</summary>
        public bool HandleBack()
        {
            if (!IsOpen) return false;
            Close();
            return true;
        }

        public void Close()
        {
            if (!IsOpen) return;
            _kind = Kind.None;
            UiDom.Show(_host, false);
            UiDom.Show(_modal, false);
            UiDom.Show(_quick, false);
            _modal.Reset();
            var opener = _opener;
            _opener = null;
            if (opener != null && opener.panel != null)
            {
                _context.Instance.LastFocused = opener;
                _context.Navigator.FocusTop(opener);
            }
            Closed?.Invoke();
        }

        private void Show(Kind kind, VisualElement opener, bool modal)
        {
            _kind = kind;
            _opener = opener;
            UiDom.Show(_host, true);
            _host.EnableInClassList(UiClasses.OverlayScrimClear, !modal);
            UiDom.Show(_modal, modal);
            UiDom.Show(_quick, !modal);
        }

        private void FocusFirst(VisualElement preferred)
        {
            var target = preferred ?? _modal.Body.Query<VisualElement>().Where(e => e.focusable && e != _modal.Body).First() ?? _modal.CloseButton;
            _context.Instance.LastFocused = target;
            _context.Navigator.FocusTop(target);
        }

        // ------------------------------------------------------------------ the piles

        /// <summary>The piles viewer. The draw pile opens "Draw pile" (a grid of the faces, sorted: the order of the pile is not shown); the discard and exhaust piles open "Card piles" on their tab.</summary>
        public void OpenPiles(string pile, VisualElement opener)
        {
            if (IsOpen) return;
            _pile = pile;
            _selectedCard = null;
            _modal.Reset();
            _modal.size = UiValues.ModalSizeXl;
            Show(Kind.Piles, opener, true);
            RenderPiles();
        }

        private void RenderPiles()
        {
            _modal.Reset();
            var draw = _pile == OverlayValues.PileDraw;
            var cards = Core2CombatView.Pile(_flow.Session, _pile, _ui.Data.CardText);
            var view = _flow.View;
            VisualElement preferred = null;
            if (draw)
            {
                _modal.SetHead(_strings.Get(StringKeys.CombatModalPilesEyebrow), _strings.Get(StringKeys.CombatModalPilesDrawTitle), Count(cards.Count));
                _modal.Body.Add(Grid(cards, false, null));
            }
            else
            {
                _modal.SetHead(null, _strings.Get(StringKeys.CombatModalPilesSpentTitle));
                var layout = new VisualElement();
                layout.AddToClassList(UiClasses.PileLayout);
                var rail = new VisualElement();
                rail.AddToClassList(UiClasses.PileRail);
                foreach (var tab in new[] { OverlayValues.PileDiscard, OverlayValues.PileExhaust })
                {
                    var count = tab == OverlayValues.PileDiscard ? view.DiscardCount : view.ExhaustCount;
                    var button = new LocButton();
                    button.AddToClassList(UiClasses.PileTab);
                    button.EnableInClassList(UiClasses.PileTabOn, tab == _pile);
                    button.SetResolved(_strings.Format(tab == OverlayValues.PileDiscard ? StringKeys.CombatModalPilesDiscard : StringKeys.CombatModalPilesExhaust, new StringArgs().Add(UiPlaceholders.Count, count)));
                    var captured = tab;
                    button.clicked += () =>
                    {
                        _pile = captured;
                        _selectedCard = null;
                        RenderPiles();
                        FocusFirst(_modal.Body.Q<LocButton>(className: UiClasses.PileTabOn));
                    };
                    if (tab == _pile) preferred = button;
                    rail.Add(button);
                }
                var pane = new VisualElement();
                pane.AddToClassList(UiClasses.PilePane);
                var detail = new VisualElement();
                detail.AddToClassList(UiClasses.PileDetail);
                if (_selectedCard == null && cards.Count > 0) _selectedCard = cards[0].InstanceId;
                pane.Add(Grid(cards, true, selected =>
                {
                    _selectedCard = selected.InstanceId;
                    Reading(detail, selected);
                    foreach (var cell in pane.Query<KitCard>().ToList()) cell.EnableInClassList(UiClasses.PileCellSelected, cell.userData as string == _selectedCard);
                }));
                Reading(detail, cards.FirstOrDefault(c => c.InstanceId == _selectedCard));
                foreach (var cell in pane.Query<KitCard>().ToList()) cell.EnableInClassList(UiClasses.PileCellSelected, cell.userData as string == _selectedCard);
                layout.Add(rail);
                layout.Add(pane);
                layout.Add(detail);
                _modal.Body.Add(layout);
            }
            _modal.Foot.Add(CloseButton(StringKeys.CombatModalPilesClose));
            FocusFirst(draw ? null : preferred);
        }

        private string Count(int count) => _strings.Format(count == 1 ? StringKeys.CombatModalPilesCard : StringKeys.CombatModalPilesCards, new StringArgs().Add(UiPlaceholders.Count, count));

        /// <summary>The faces of a pile in a wrapping grid; an empty pile says so. With <paramref name="onSelect"/> a face is a control that selects its card (the spent piles).</summary>
        private VisualElement Grid(List<Core2CardView> cards, bool selectable, Action<Core2CardView> onSelect)
        {
            var grid = new VisualElement();
            grid.AddToClassList(UiClasses.PileGrid);
            grid.EnableInClassList(UiClasses.PileGridCentred, !selectable);
            if (cards.Count == 0)
            {
                var empty = new LocLabel();
                empty.AddToClassList(UiClasses.PileEmpty);
                empty.AddToClassList(UiClasses.ValueText);
                empty.SetResolved(_strings.Get(StringKeys.CombatModalPilesEmpty));
                grid.Add(empty);
                return grid;
            }
            foreach (var card in cards)
            {
                var face = new KitCard { focusable = selectable };
                face.AddToClassList(UiClasses.PileCell);
                face.Bind(Plain(card));
                face.userData = card.InstanceId;
                if (selectable)
                {
                    var captured = card;
                    face.RegisterCallback<PointerUpEvent>(_ => onSelect(captured));
                    face.RegisterCallback<NavigationSubmitEvent>(e =>
                    {
                        if (e.target != face) return;
                        onSelect(captured);
                        e.StopPropagation();
                    });
                    face.RegisterCallback<FocusInEvent>(_ => onSelect(captured));
                }
                grid.Add(face);
            }
            return grid;
        }

        /// <summary>The card as a face in a pile or the inspector: never dimmed, never refused (it is not being played).</summary>
        private CardViewData Plain(Core2CardView card, bool inspect = false)
        {
            var data = _cardData(card);
            data.Affordable = true;
            data.RefusalText = null;
            // A pile cell is a plain face (the profile's line limit); the inspector shows every line the model has (the web's level 'inspect').
            if (card.Words != null) data.RulesHtml = inspect ? card.Words.Inspect : card.Words.Face;
            data.RulesOverflow = !inspect && data.RulesOverflow;
            return data;
        }

        // ------------------------------------------------------------------ the card inspector

        /// <summary>The inspector for a card: a large face, its name, type and cost, its words and the keywords it carries.</summary>
        public void OpenInspect(Core2CardView card, VisualElement opener)
        {
            if (IsOpen || card == null) return;
            _modal.Reset();
            _modal.size = UiValues.ModalSizeLg;
            Show(Kind.Inspect, opener, true);
            _modal.SetHead(_strings.Get(StringKeys.CombatModalInspectEyebrow), card.Name);
            var layout = new VisualElement();
            layout.AddToClassList(UiClasses.InspectLayout);
            var face = new KitCard { focusable = false };
            face.AddToClassList(UiClasses.InspectCard);
            face.Bind(Plain(card, true));
            layout.Add(face);
            var text = new VisualElement();
            text.AddToClassList(UiClasses.InspectText);
            Reading(text, card);
            layout.Add(text);
            _modal.Body.Add(layout);
            _modal.Foot.Add(CloseButton(StringKeys.CombatModalInspectClose));
            FocusFirst(null);
        }

        /// <summary>The reading of a card in a column: name, type line, cost, the card's words, then each keyword it carries with its tip (src/ui/components/cardInspection.js, card.js cardTooltip).</summary>
        private void Reading(VisualElement host, Core2CardView card)
        {
            host.Clear();
            if (card == null) return;
            host.Add(Text(card.Name, UiClasses.DetailTitle));
            host.Add(Text(Core2CombatText.TypeLine(_strings, card), UiClasses.DetailMeta));
            host.Add(Text(CostLine(card), UiClasses.DetailMeta));
            foreach (var row in Core2CardHints.Sigils(_strings, _ui.Data.CardFace, card))
            {
                var line = new VisualElement { pickingMode = PickingMode.Ignore };
                line.AddToClassList(UiClasses.DetailSigilRow);
                var mark = new VisualElement { pickingMode = PickingMode.Ignore };
                mark.AddToClassList(UiClasses.DetailSigil);
                var sprite = KitCard.Glyph(row.School ? UiFormats.CardSchoolGlyph : UiFormats.CardSigilGlyph, row.Id);
                mark.style.backgroundImage = sprite != null ? new StyleBackground(sprite) : new StyleBackground(StyleKeyword.None);
                line.Add(mark);
                line.Add(Text(_strings.Format(StringKeys.CardSigilRow, new StringArgs().Add(UiPlaceholders.Label, row.Label).Add(UiPlaceholders.Help, row.Help)), UiClasses.DetailMeta));
                host.Add(line);
            }
            foreach (var hint in Core2CardHints.Hints(_strings, _ui.Data.CardFace, card))
                host.Add(Text(hint.Lead == null ? hint.Text : _strings.Format(StringKeys.CardHintRow, new StringArgs().Add(UiPlaceholders.Lead, hint.Lead).Add(UiPlaceholders.Text, hint.Text)), UiClasses.DetailHint));
            if (card.Words?.Reading != null) host.Add(Text(CardFaceMarkup.ToRich(_ui.Data.CardText, card.Words.Reading, new CardMarkupStyle { ShowExtra = true }), UiClasses.DetailText));
            else if (!string.IsNullOrEmpty(card.Text)) host.Add(Text(card.Text, UiClasses.DetailText));
            foreach (var tag in card.Tags)
            {
                var id = tag.StartsWith(OverlayValues.TraitPrefix, StringComparison.Ordinal) ? tag.Substring(OverlayValues.TraitPrefix.Length) : tag;
                var nameKey = string.Format(System.Globalization.CultureInfo.InvariantCulture, OverlayFormats.KeywordNameKey, id);
                if (!_strings.Has(nameKey)) continue;
                var box = new VisualElement { pickingMode = PickingMode.Ignore };
                box.AddToClassList(UiClasses.DetailTag);
                box.Add(Text(_strings.Get(nameKey), UiClasses.DetailTagName));
                var tipKey = string.Format(System.Globalization.CultureInfo.InvariantCulture, OverlayFormats.KeywordTipKey, id);
                if (_strings.Has(tipKey)) box.Add(Text(_strings.Get(tipKey), UiClasses.DetailTagTip));
                host.Add(box);
            }
        }

        private string CostLine(Core2CardView card)
        {
            var parts = new List<string>();
            if (card.CostIsX) parts.Add(_strings.Get(StringKeys.CombatCostX));
            else if (card.Stamina > 0) parts.Add(_strings.Format(StringKeys.CombatModalCostSp, new StringArgs().Add(UiPlaceholders.Value, CombatText.Number(card.Stamina))));
            if (card.Mana > 0) parts.Add(_strings.Format(StringKeys.CombatModalCostMp, new StringArgs().Add(UiPlaceholders.Value, CombatText.Number(card.Mana))));
            return parts.Count == 0
                ? _strings.Get(StringKeys.CombatModalInspectCostFree)
                : _strings.Format(StringKeys.CombatModalInspectCost, new StringArgs().Add(UiPlaceholders.Cost, string.Join(_strings.Get(StringKeys.Core2OptionJoin), parts)));
        }

        // ------------------------------------------------------------------ the combatant reading

        /// <summary>
        /// The full reading of a fighter (from the target card's info button): the web's combatant door (ui/screens/combat.js openCombatantDoor), its head naming what the fighter is, the preview of the figure with
        /// its pool meters left and the details right, each block as <see cref="Core2CombatantInspector"/> projects it and <see cref="Core2InspectorBlocks"/> draws it.
        /// </summary>
        public void OpenDetail(FighterData d, VisualElement opener)
        {
            if (IsOpen || d == null) return;
            var state = _flow.Session?.State;
            var entity = state == null ? null : d.Player ? state.Player : state.Enemies.FirstOrDefault(e => e.Str(Ashen.Generated.CombatKeys.Id) == d.Id);
            if (entity == null) return;
            var view = Core2CombatantInspector.Build(state, entity, new InspectorContext { Strings = _strings, Text = _ui.Data.CardText, PlayerName = _run.Name });
            _modal.Reset();
            _modal.size = UiValues.ModalSizeLg;
            Show(Kind.Detail, opener, true);
            _modal.SetHead(view.Eyebrow, view.Title);
            var layout = new VisualElement { pickingMode = PickingMode.Ignore };
            layout.AddToClassList(UiClasses.InspectorLayout);
            layout.Add(InspectorPreview(d, view));
            var body = new VisualElement();
            body.AddToClassList(UiClasses.InspectorDetails);
            layout.Add(body);
            _modal.Body.Add(layout);
            foreach (var block in Core2InspectorBlocks.Build(_strings, view, maneuver => _flow.Predict(view.EnemyId, maneuver).Accepted)) body.Add(block);
            _modal.Foot.Add(CloseButton(StringKeys.CombatModalInspectClose));
            FocusFirst(null);
        }

        /// <summary>
        /// combatantInspectorPreview: the figure of the combatant in a box of 3 : 4 (the player's portrait, an enemy's stage in the frame its state holds), its name, and the pool meters the combatant has (the Hit
        /// points always, Mana, Poise and Ward when the pool has a size).
        /// </summary>
        private VisualElement InspectorPreview(FighterData d, CombatantInspectorView view)
        {
            var preview = new VisualElement { pickingMode = PickingMode.Ignore };
            preview.AddToClassList(UiClasses.InspectorPreview);
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.AddToClassList(UiClasses.InspectorArt);
            var aspect = (float)_ui.Data.Tokens.Ratio(RatioKeys.InspectorArtAspect);
            art.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                var w = art.resolvedStyle.width;
                if (!float.IsNaN(w) && w > 0) art.style.height = w * aspect;
            });
            var width = (float)_ui.Data.Tokens.Ratio(RatioKeys.InspectorSpriteW);
            var height = (float)_ui.Data.Tokens.Ratio(RatioKeys.InspectorSpriteH);
            if (d.Enemy != null && TryEnemyStage(d.Enemy, height, out var stage)) art.Add(stage);
            else
            {
                var figure = new VisualElement { pickingMode = PickingMode.Ignore };
                figure.AddToClassList(UiClasses.InspectorFigure);
                figure.style.width = width;
                figure.style.height = height;
                var texture = d.Portrait ?? d.RestArt ?? d.Art;
                if (texture != null) figure.style.backgroundImage = new StyleBackground(texture);
                art.Add(figure);
            }
            preview.Add(art);
            preview.Add(Text(view.PreviewName, UiClasses.InspectorName));
            var meters = new VisualElement { pickingMode = PickingMode.Ignore };
            meters.AddToClassList(UiClasses.InspectorMeters);
            foreach (var meter in view.Meters) AddMeter(meters, meter.Id, MeterName(meter.Id), (int)meter.Cur, (int)meter.Max);
            preview.Add(meters);
            return preview;
        }

        private static void AddMeter(VisualElement host, string kind, string glyphKey, int value, int max)
        {
            var meter = new Ashen.Presentation.UI.Kit.Meter { kind = kind, glyphKey = glyphKey, pickingMode = PickingMode.Ignore };
            meter.Set(value, max);
            host.Add(meter);
        }

        /// <summary>
        /// An enemy's figure as its stage draws it at rest in the state it is in (enemyExpansionSprite over enemySpriteState): a stage as tall as the stage's own height and as wide as its idle figure, the
        /// frame placed in it as in combat. False when the enemy has no frame imported (the legacy art shows).
        /// </summary>
        private static bool TryEnemyStage(EnemyFeed feed, float boxHeight, out VisualElement stage)
        {
            stage = null;
            var figure = feed.Data?.Figure(feed.EnemyId);
            if (figure == null) return false;
            var state = new EnemyStageState(feed.Data, feed.EnemyId);
            state.SetState(feed.Rest, feed.Themes, feed.Category);
            var draw = state.Draw(feed.ArtOf, art => feed.Texture(art) != null);
            var texture = draw == null ? null : feed.Texture(draw.Art);
            if (texture == null) return false;
            var scale = boxHeight / (float)feed.Data.Height;
            var rootWidth = (float)(figure.IdleWidth * feed.Data.Height / figure.IdleHeight) * scale;
            stage = new VisualElement { pickingMode = PickingMode.Ignore };
            stage.style.width = rootWidth;
            stage.style.height = boxHeight;
            stage.style.flexShrink = 0;
            var frame = new VisualElement { pickingMode = PickingMode.Ignore };
            frame.AddToClassList(UiClasses.InspectorStageFrame);
            frame.style.backgroundImage = new StyleBackground(texture);
            frame.style.left = rootWidth * (float)UiMath.Half + (float)draw.Rect.Left * scale;
            frame.style.top = (float)draw.Rect.Top * scale;
            frame.style.width = (float)draw.Rect.Width * scale;
            frame.style.height = (float)draw.Rect.Height * scale;
            stage.Add(frame);
            return true;
        }

        /// <summary>The string key of a pool meter's glyph (strings resource.&lt;pool&gt;.name).</summary>
        private static string MeterName(string id) =>
            id == InspectorMeters.Hp ? InspectorMeters.HpName : id == InspectorMeters.Mp ? InspectorMeters.MpName : id == InspectorMeters.Sp ? InspectorMeters.SpName
            : id == InspectorMeters.Poise ? InspectorMeters.PoiseName : InspectorMeters.WardName;

        // ------------------------------------------------------------------ the quick menu

        /// <summary>The quick menu (web quicknav.js over quickMenuRows, ui/menus.json pause): Settings, Controls, the Fullscreen and Music switches, the Armoury's Inventory and Character (locked in a fight), Load, Save, Save and Quit and Quit Without Saving, as a dropdown under the menu button.</summary>
        public void OpenQuick(VisualElement opener)
        {
            if (IsOpen) return;
            _quickRows = QuickMenuRows.Fill(_context, _quick, _run);
            Show(Kind.Quick, opener, false);
            FocusQuickRow(0);
        }

        private void FocusQuickRow(int index)
        {
            var row = _quick.Rows.ElementAtOrDefault(index);
            _context.Instance.LastFocused = row;
            if (row != null) _context.Navigator.FocusTop(row);
        }

        private void OnQuick(string id)
        {
            var def = _ui.Data.Menus.Pause.FirstOrDefault(e => e.Id == id);
            if (def == null) return;
            var index = _quickRows.ToList().FindIndex(r => r.Def.Id == id);
            // A switch and Save act in place and leave the list open (web quicknav.js: 'keep'); Save says Saved · Slot n on its own row.
            if (QuickMenuRows.KeepsOpen(def, false))
            {
                if (def.Action == MenuActions.Toggle)
                {
                    PauseActions.Toggle(_context, _run, def);
                    _quickRows = QuickMenuRows.Fill(_context, _quick, _run);
                    FocusQuickRow(index);
                }
                else PauseActions.SaveHere(_context, _run, _quick.Rows.ElementAtOrDefault(index));
                return;
            }
            Close();
            PauseActions.Activate(_context, _run, def);
        }

        // ------------------------------------------------------------------ small builders

        private LocButton CloseButton(string key)
        {
            var close = new LocButton(key);
            close.clicked += Close;
            return close;
        }

        private static LocLabel Text(string text, string className, bool upper = false)
        {
            var label = new LocLabel { pickingMode = PickingMode.Ignore };
            label.AddToClassList(className);
            label.EnableInClassList(UiClasses.Upper, upper);
            label.AddToClassList(UiClasses.ValueText);
            label.SetResolved(text);
            return label;
        }
    }
}
