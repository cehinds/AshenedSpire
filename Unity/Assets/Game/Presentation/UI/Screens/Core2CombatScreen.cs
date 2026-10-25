using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using KitCard = Ashen.Presentation.UI.Kit.CardView;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// W-07 for a Core 2 fight (docs/design/04 W-07, docs/design/11 WP-C1b, D-311): bound only to <see cref="Core2CombatFlow"/> (which holds the fight's step logic over the
    /// Core 2 run session) and the <see cref="Core2CombatViewState"/> it builds. The screen composes nothing: every button resolves to an intent the view prebuilt and the flow sends through
    /// <see cref="Core2RunSession.Execute"/>; a refusal is the rules' own words, shown at the control that was pressed, and changes nothing.
    /// It draws the fighters (Evade, Poise, Ward, Block, HP with their ratings; stance and Staggered; Precision, Barrier and Counter chips; statuses; the enemy's intent with its knowledge
    /// label and the Perception predict control), the hand (costs, rank, traits, playability and the rules' refusal), the target layer (hit chance and damage receipt on each legal target),
    /// the prompt panel (stance, route, reaction offer, card choice, flasks, equipment, prediction), the reaction switch, End Turn with its refusal, the pile counts and the combat log.
    /// An enemy turn is walked step by step at the pace of the player's combat settings (<see cref="CombatPacing"/>); Skip fast-forwards it. States, each with its focus order in
    /// ui/screens.json: normal; targeting; prompt; the enemy turn; the end. Escape backs out of the innermost state, else opens W-20; pad Start opens W-20.
    /// </summary>
    public sealed partial class Core2CombatScreen : ScreenView
    {
        private readonly Dictionary<string, Fighter> _enemies = new Dictionary<string, Fighter>(StringComparer.Ordinal);
        private readonly List<Button> _options = new List<Button>();
        private readonly List<string> _handIds = new List<string>();
        private Core2RunSession _run;
        private Core2CombatFlow _flow;
        private Fighter _player;
        private HandFan _hand;
        private Core2Overlays _overlays;
        private Core2PromptRows _promptRows;
        private Core2TargetInspector _targets;
        private FooterGroup _footer;
        private int _hiddenLines;
        private bool _busy;
        private string _acting;
        private bool _wasPrompt;
        private bool _wasTargeting;
        private bool _logOpen;
        private bool _gridOn = true;

        public Core2RunSession Run => _run;
        public Core2CombatFlow Flow => _flow;
        public Core2CombatViewState View => _flow?.View;

        /// <summary>The focus mode in force (a ui/screens.json focusModes key, or null for normal play).</summary>
        public string Mode => _overlays != null && _overlays.IsOpen ? Core2UiValues.FocusOverlay : _busy ? UiValues.FocusEnemyTurn : _flow?.Mode;

        public override string FocusMode => Mode;

        public bool EnemyTurn => _busy;
        public bool Ended => _flow != null && _flow.Ended && !_busy;
        public IReadOnlyList<KitCard> HandCards => _hand?.Cards ?? (IReadOnlyList<KitCard>)new KitCard[0];
        public IReadOnlyDictionary<string, Fighter> Enemies => _enemies;
        public Fighter Player => _player;
        public FooterGroup Footer => _footer;

        /// <summary>The buttons of the open prompt, in order.</summary>
        public IReadOnlyList<Button> PromptOptions => _options;

        private StringTable Strings => Ui.Data.Strings;

        protected override void OnBind(object args)
        {
            _run = (args as Core2RunArgs)?.Session ?? Ui.Core2Session;
            if (_run?.Combat == null)
            {
                Debug.LogError(UiMessages.NoCore2Fight);
                return;
            }
            Ui.Core2Session = _run;
            Ui.Session = null;
            _flow = new Core2CombatFlow(_run, Strings, Ui.Data.CardText);
            _hand = Root.Q<HandFan>(UiNames.Hand);
            _footer = Root.Q<FooterGroup>(UiNames.FooterGroup);
            ChipTips.Overlay = Context.Overlay;
            ChipTips.Size = Ui.Data.Components.CardRefusalTooltip;
            ChipTips.DelayMs = Ui.Data.Tokens.Duration(TokenKeys.TooltipDelay);
            _overlays = new Core2Overlays(Context, _run, _flow, CardData);
            _overlays.Closed += () => Render();
            _promptRows = new Core2PromptRows(Ui, _run, _flow);
            _targets = new Core2TargetInspector(Root, () => _overlays.IsOpen || ShowingPrompt, () => _flow.Targeting, _overlays.OpenDetail, Ui.Data.Tokens.Duration(TokenKeys.TargetHide));
            _footer.EndTurn += OnEndTurn;
            _footer.Potions += () => Handle(_flow.OpenFlasks(), _footer.PotionsButton);
            _footer.Piles += anchor => _overlays.OpenPiles(anchor == _footer.DrawButton ? OverlayValues.PileDraw : OverlayValues.PileDiscard, anchor);
            var menu = Root.Q<LocButton>(UiNames.HudMenu);
            menu.clicked += () => OpenQuickMenu(menu);
            Root.Q<LocButton>(UiNames.PromptBack).clicked += OnPromptBack;
            Root.Q<LocButton>(UiNames.PromptClose).clicked += OnPromptBack;
            var confirm = Root.Q<LocButton>(UiNames.PromptConfirm);
            confirm.clicked += () => OnPromptConfirm(confirm);
            _promptRows.PickChanged += RenderPromptConfirm;
            var reactions = Root.Q<LocButton>(UiNames.ToolReactions);
            reactions.clicked += () => Handle(_flow.ToggleReactions(), reactions);
            var armaments = Root.Q<LocButton>(UiNames.ToolArmaments);
            armaments.clicked += () => Handle(_flow.OpenEquipment(), armaments);
            Root.Q<LocButton>(UiNames.ToolLog).clicked += () => SetLog(!_logOpen);
            Root.Q<LocButton>(UiNames.LogClose).clicked += () => SetLog(false);
            Root.Q<LocButton>(UiNames.ToolGrid).clicked += () =>
            {
                _gridOn = !_gridOn;
                RenderTools();
            };
            var recover = Root.Q<LocButton>(UiNames.ToolRecover);
            recover.clicked += () => Handle(_flow.OpenRecover(), recover);
            var list = Root.Q<ScrollView>(UiNames.PromptList);
            // The focus model walks the options; neither the scroll view nor its bars take focus themselves.
            list.focusable = false;
            list.contentViewport.focusable = false;
            list.horizontalScroller.slider.focusable = false;
            list.verticalScroller.slider.focusable = false;
            Root.Q<LocButton>(UiNames.EndContinue).clicked += OnContinue;
            Root.Q<LocButton>(UiNames.EndSummary).clicked += OpenRunEnd;
            Root.Q<LocButton>(UiNames.EndToTitle).clicked += () =>
            {
                Ui.ClearRun();
                Nav.Fire(ScreenTriggers.Quit);
            };
            ApplyBackground();
            ApplyBoardArt();
            SetupFx();
            _player = new Fighter { focusable = false };
            Root.Q(UiNames.AllyRow).Add(_player);
            _targets.Attach(_player);
            Render();
            if ((args as Core2RunArgs)?.ResumeWarning == true) ShowNotice(StringKeys.CombatResumeWarning);
            if (_flow.Ended) EnterEnded();
            else
            {
                Context.Instance.LastFocused = FirstPlayableCard();
                if ((args as Core2RunArgs)?.ResumeWarning != true) ShowBossIntro();
            }
        }

        // ------------------------------------------------------------------ render

        private void ApplyBackground()
        {
            var components = Ui.Data.Components;
            var id = StringTable.Fill(components.CombatBackground, new StringArgs().Add(UiPlaceholders.Region, _run.RegionOfEncounter(_run.EncounterId)));
            Ui.ApplyBackground(Root.Q(UiNames.CombatBackground), Ui.Art != null && Ui.Art.Get(id) != null ? id : components.CombatFallbackBackground);
        }

        /// <summary>Rebuild the screen from the flow's view; focus stays on the same card or fighter (US-5.9).</summary>
        public void Render()
        {
            var keep = FocusIdentity();
            foreach (var mode in new[] { UiValues.FocusTargeting, Core2UiValues.FocusPrompt, UiValues.FocusEnemyTurn, UiValues.FocusEnded })
                Root.EnableInClassList(UiClasses.CombatModePrefix + mode, Mode == mode);
            RenderHud();
            RenderFighters();
            RenderHand();
            RenderFooter();
            RenderPrompt();
            RenderTools();
            RenderLog();
            RestoreFocus(keep);
            _targets?.Refresh();
            var prompt = ShowingPrompt;
            if (prompt && !_wasPrompt) FocusSoon(_options.FirstOrDefault(o => o.enabledSelf) ?? (VisualElement)Root.Q(UiNames.PromptBack));
            else if (_flow.Targeting && !_wasTargeting) FocusSoon(FirstTarget());
            _wasPrompt = prompt;
            _wasTargeting = _flow.Targeting;
        }

        private bool ShowingPrompt => _flow.Prompt != null && !_busy;

        private void RenderHud()
        {
            var p = View.Player;
            Root.Q<Meter>(UiNames.HudHp)?.Set(Int(p.Hp + HeldHp(p.Id)), Int(p.MaxHp));
            var ribbon = Root.Q<LocLabel>(UiNames.TurnRibbon);
            ribbon?.SetResolved(Strings.Get(_busy ? StringKeys.Core2BoardRibbonEnemy : StringKeys.Core2BoardRibbonPlayer));
            ribbon?.EnableInClassList(UiClasses.RibbonEnemy, _busy);
            var run = _run.Run;
            var cinders = (double?)run[RunKeys.Cinders] ?? 0d;
            Root.Q<LocLabel>(UiNames.HudCinders)?.SetResolved(Strings.Format(StringKeys.Core2BoardCinders, new StringArgs().Add(UiPlaceholders.Amount, Int(cinders))));
            var pips = Root.Q(UiNames.HudPips);
            if (pips == null) return;
            pips.Clear();
            if (!(run[CombatKeys.Relics] is JArray relics)) return;
            foreach (var relic in relics)
            {
                var pip = new VisualElement { pickingMode = PickingMode.Ignore };
                pip.AddToClassList(UiClasses.HudPip);
                var reading = Core2Readings.Relic(_flow.Session.State.Data, Ui.Data.Components, (string)relic);
                ChipTips.Attach(pip, reading.Title, reading.Body);
                var art = Ui.Texture(StringTable.Fill(Ui.Data.Components.RewardRelicArt, new StringArgs().Add(UiPlaceholders.Id, (string)relic)));
                pip.style.backgroundImage = art != null ? new StyleBackground(art) : new StyleBackground(StyleKeyword.None);
                pips.Add(pip);
            }
        }

        /// <summary>The raster art of the board (art lookups by name, ui/art.json): the footer's orb, plate, piles and potion tray, the HUD's heart and its armoury and menu buttons.</summary>
        private void ApplyBoardArt()
        {
            _footer.SetArt(new FooterArt
            {
                Orb = new OrbArt
                {
                    Disc = BoardTexture(ArtKinds.Orb, BoardArt.OrbDisc),
                    Frame = BoardTexture(ArtKinds.Orb, BoardArt.OrbFrame),
                    Diamond = BoardTexture(ArtKinds.Orb, BoardArt.OrbDiamond),
                    Spent = BoardTexture(ArtKinds.Orb, BoardArt.OrbSpent),
                },
                Plate = BoardTexture(ArtKinds.Footer, BoardArt.FooterPlate),
                Draw = BoardTexture(ArtKinds.Footer, BoardArt.FooterDraw),
                Spent = BoardTexture(ArtKinds.Footer, BoardArt.FooterSpent),
                Potions = BoardTexture(ArtKinds.Footer, BoardArt.FooterPotions),
            });
            SetImage(Root.Q(UiNames.HudHeart), Ui.Icon(BoardIcons.Health));
            SetImage(Root.Q(UiNames.ToolArmaments), BoardTexture(ArtKinds.Component, BoardArt.ComponentArmoury));
            SetImage(Root.Q(UiNames.HudMenu), BoardTexture(ArtKinds.Component, BoardArt.ComponentMenu));
        }

        private Texture2D BoardTexture(string kind, string name) =>
            Ui.Texture(Ui.Data.Art?.Find(kind, new StringArgs().Add(ArtPlaceholders.Name, name)));

        private static void SetImage(VisualElement element, Texture2D texture)
        {
            if (element == null) return;
            element.style.backgroundImage = texture != null ? new StyleBackground(texture) : new StyleBackground(StyleKeyword.None);
        }

        private void RenderFighters()
        {
            var order = Ui.Data.Components.FighterRatings;
            _player.Bind(FighterData(View.Player, true, order, 0));
            var row = Root.Q(UiNames.EnemyRow);
            foreach (var enemy in View.Enemies)
            {
                if (!_enemies.TryGetValue(enemy.Id, out var element))
                {
                    element = new Fighter();
                    element.Activated += OnFighter;
                    element.PredictPressed += OnPredict;
                    _targets.Attach(element);
                    row.Add(element);
                    _enemies[enemy.Id] = element;
                }
                var data = FighterData(enemy, false, order, 0);
                var legal = _flow.Targeting && _flow.LegalTargets.Contains(enemy.Id);
                data.Target = legal;
                data.Acting = enemy.Id == _acting;
                if (legal && _flow.Armed != null && _flow.Armed.Hits.TryGetValue(enemy.Id, out var hit))
                {
                    data.HitText = Core2CombatText.Hit(Strings, hit);
                    data.PreviewText = Core2CombatText.Preview(Strings, enemy, hit);
                    data.ReceiptText = Core2CombatText.Receipt(Strings, hit);
                }
                else if (legal && _flow.ArmedFlask != null) data.PreviewText = null;
                data.CanPredict = enemy.Alive && enemy.Intent != null && enemy.Intent.Predictable && !_busy && _flow.Prompt == null && !_flow.Ended;
                element.Bind(data);
                element.focusable = enemy.Alive && !_busy && (Mode == null || legal);
            }
        }

        private FighterData FighterData(Core2FighterView f, bool player, IReadOnlyList<string> order, int reference)
        {
            var d = new FighterData
            {
                Id = f.Id,
                Name = player ? Strings.Format(StringKeys.CombatIdentity, new StringArgs().Add(UiPlaceholders.Name, _run.Name).Add(UiPlaceholders.Class, RunFlow.ClassName(Ui, _run.ClassId))) : f.Name,
                Art = Ui.Texture(player ? _run.Portrait : StringTable.Fill(Ui.Data.Components.EnemyArt, new StringArgs().Add(UiPlaceholders.Id, f.DefId))),
                Player = player,
                Alive = f.Alive || _dying.Contains(f.Id),
                Hp = Int(f.Hp + HeldHp(f.Id)),
                MaxHp = Int(f.MaxHp),
                ReferenceMaxHp = reference,
                HasPoise = f.Poise != null,
                HasWard = f.Ward != null && f.Ward.Max > 0,
                EvadeText = Core2CombatText.Evade(Strings, f),
                BlockText = Core2CombatText.Block(Strings, f),
                StanceText = Core2CombatText.Stance(Strings, f),
                BadgeText = Core2CombatText.Badge(Strings, f),
                Staggered = f.Staggered,
                RatingOrder = order,
                Stacks = Core2CombatText.Stacks(Strings, f),
                Statuses = Core2CombatText.Statuses(Strings, f, Math.Max(1, Ui.Data.Components.StatusChips), Ui.Data.Components.FighterStackStatuses),
            };
            _hpShown[f.Id] = d.Hp;
            AddChips(d, f);
            if (!player) ApplyRestArt(d, f);
            if (f.Poise != null) { d.Poise = Int(f.Poise.Value); d.PoiseMax = Int(f.Poise.Max); }
            if (d.HasWard) { d.Ward = Int(f.Ward.Value); d.WardMax = Int(f.Ward.Max); }
            if (f.Alive && f.Intent != null)
            {
                d.IntentText = Core2CombatText.Intent(Strings, f.Intent);
                d.IntentKind = Core2CombatText.IntentKind(f.Intent);
                d.IntentIcon = IntentIcon(d.IntentKind);
                d.IntentReduced = f.Intent.Reduced;
                var tip = Core2CombatText.IntentTip(Strings, f.Intent);
                if (tip != null)
                {
                    d.IntentTipTitle = Core2CombatText.IntentBadge(Strings, f.Intent)?.Title;
                    d.IntentTipBody = tip;
                }
            }
            var poiseTip = Core2CombatText.PoiseTip(Strings, f);
            if (poiseTip != null) { d.PoiseTipTitle = poiseTip.Title; d.PoiseTipBody = poiseTip.Body; }
            var wardTip = Core2CombatText.WardTip(Strings, f);
            if (wardTip != null) { d.WardTipTitle = wardTip.Title; d.WardTipBody = wardTip.Body; }
            return d;
        }

        /// <summary>The icon beside the intent text: the crossed swords for an attack, the shield for Block, the unknown mark for a move not read yet (none for the others).</summary>
        private Texture2D IntentIcon(string kind)
        {
            if (kind == Core2UiValues.IntentUnknown) return Ui.Icon(BoardIcons.Unknown);
            if (kind == Core2CombatValues.IntentBlock) return Ui.Icon(BoardIcons.Block);
            if (kind == Core2CombatValues.IntentAttack || kind == Core2CombatValues.IntentAttackDebuff) return Ui.Icon(BoardIcons.Attack);
            return null;
        }

        /// <summary>
        /// The chips under the HP bar as the web draws them: Evade and Block (rating chips), Precision, Barrier and an armed Counter (stack chips), then the statuses with their
        /// icon and number, capped with a "+N" tail. A chip whose icon is not imported shows its words instead.
        /// </summary>
        private void AddChips(FighterData d, Core2FighterView f)
        {
            if (f.Evade > 0) d.RatingChips.Add(Chip(Ui.Icon(BoardIcons.Evade), CombatText.Number(f.Evade), Core2CombatText.Evade(Strings, f), f.Statuses.FirstOrDefault(s => s.Id == Core2CombatKeys.Evade)));
            if (f.Block > 0) d.RatingChips.Add(Tipped(Chip(Ui.Icon(BoardIcons.Block), CombatText.Number(f.Block), Core2CombatText.Block(Strings, f)), Core2CombatText.BlockTip(Strings, f)));
            if (f.Precision > 0)
            {
                var precision = Chip(Ui.Icon(BoardIcons.Precision), CombatText.Number(f.Precision), Strings.Format(StringKeys.Core2HudPrecision, new StringArgs().Add(UiPlaceholders.Value, CombatText.Number(f.Precision))), f.Statuses.FirstOrDefault(s => s.Id == Core2CombatKeys.Precision));
                precision.Gold = true;
                d.StackChips.Add(precision);
            }
            if (f.Barrier > 0) d.StackChips.Add(Tipped(Chip(null, null, Strings.Format(StringKeys.Core2HudBarrier, new StringArgs().Add(UiPlaceholders.Value, CombatText.Number(f.Barrier)))), Core2CombatText.BarrierTip(Strings, f)));
            if (f.Counter != null) d.StackChips.Add(Tipped(Chip(null, null, Strings.Get(StringKeys.Core2HudCounter)), Core2CombatText.CounterTip(Strings)));
            var shown = f.Statuses.Where(s => !Ui.Data.Components.FighterStackStatuses.Contains(s.Id)).ToList();
            var limit = Math.Max(1, Ui.Data.Components.StatusChips);
            var take = shown.Count > limit ? limit - 1 : shown.Count;
            foreach (var status in shown.Take(take))
            {
                var number = CombatText.Number(status.MeterValue ?? status.Stacks);
                var words = status.MeterValue.HasValue && status.MeterMax.HasValue
                    ? Strings.Format(StringKeys.CombatStatusMeter, new StringArgs().Add(UiPlaceholders.Name, status.Name).Add(UiPlaceholders.Value, number).Add(UiPlaceholders.Max, CombatText.Number(status.MeterMax.Value)))
                    : Strings.Format(StringKeys.CombatStatus, new StringArgs().Add(UiPlaceholders.Name, status.Name).Add(UiPlaceholders.Stacks, CombatText.Number(status.Stacks)));
                d.StatusChips.Add(Chip(Ui.Icon(status.Id), number, words, status));
            }
            if (shown.Count > take) d.StatusChips.Add(Chip(null, null, Strings.Format(StringKeys.CombatStatusMore, new StringArgs().Add(UiPlaceholders.Count, shown.Count - take))));
        }

        /// <summary>A chip that says what it is when pointed at (the web's health-badge tooltip).</summary>
        private static ChipData Tipped(ChipData chip, Core2CombatText.Tip tip)
        {
            if (tip == null) return chip;
            chip.TipTitle = tip.Title;
            chip.TipBody = tip.Body;
            return chip;
        }

        /// <summary>A chip: the icon with its number when the art exists, else the words.</summary>
        private static ChipData Chip(Texture2D icon, string number, string words) =>
            icon != null && number != null ? new ChipData { Icon = icon, Text = number } : new ChipData { Text = words };

        /// <summary>A chip that explains the status behind it when pointed at (the web's status pip tip).</summary>
        private ChipData Chip(Texture2D icon, string number, string words, Core2StatusView status)
        {
            var chip = Chip(icon, number, words);
            if (status == null) return chip;
            var reading = Core2Readings.Status(Strings, status);
            chip.TipTitle = reading.Title;
            chip.TipBody = reading.Body;
            return chip;
        }

        private void RenderHand()
        {
            var components = Ui.Data.Components;
            var cards = View.Hand.Select(CardData).ToList();
            _hand.SetCards(cards, components.DegreesPerCard, components.MaxSpread, components.ScrollAfter);
            _handIds.Clear();
            _handIds.AddRange(View.Hand.Select(c => c.InstanceId));
            var tipDelay = Ui.Data.Tokens.Duration(TokenKeys.TooltipDelay);
            for (var i = 0; i < _hand.Cards.Count; i++)
            {
                var view = _hand.Cards[i];
                var card = View.Hand[i];
                var index = i;
                view.RegisterCallback<PointerUpEvent>(_ => OnCard(index, view));
                view.RegisterCallback<NavigationSubmitEvent>(e =>
                {
                    if (e.target != view) return;
                    OnCard(index, view);
                    e.StopPropagation();
                });
                view.EnableInClassList(UiClasses.CardArmed, _flow.Armed != null && _flow.Armed.InstanceId == card.InstanceId);
                view.Selected = _flow.Armed != null && _flow.Armed.InstanceId == card.InstanceId;
                view.focusable = Mode == null;
                view.ShowInspect = true;
                view.Inspect += _ => _overlays.OpenInspect(card, view);
                if (!card.Playable && Mode == null && !string.IsNullOrEmpty(card.Refusal))
                    Tooltip.AttachResolved(view, Context.Overlay, components.CardRefusalTooltip, card.Name, card.Refusal, tipDelay);
            }
        }

        private CardViewData CardData(Core2CardView card)
        {
            var kinds = Ui.Data.Components.CostKinds;
            var costs = new List<CardCostData>();
            // Pips in ui/components.json card.costKinds order: in Core 2 the Actions are Stamina, so the Stamina pip is always shown and the Mana pip when the card costs it.
            foreach (var kind in kinds)
            {
                if (kind == Core2UiValues.CostStamina) costs.Add(new CardCostData { Kind = kind, Text = card.CostIsX ? Strings.Get(StringKeys.CombatCostX) : CombatText.Number(card.Stamina), Shift = card.CostIsX ? 0 : card.CostShift });
                else if (kind == Core2UiValues.CostMana && card.Mana > 0) costs.Add(new CardCostData { Kind = kind, Text = CombatText.Number(card.Mana) });
            }
            return new CardViewData
            {
                Name = card.Name,
                TypeLine = Core2CombatText.TypeLine(Strings, card),
                Face = card.Face,
                Rank = Core2CombatText.Rank(Strings, card),
                Text = card.Text,
                Shifts = card.TextShifts,
                RulesHtml = card.Words?.Hand,
                RulesOverflow = card.Words?.Lines != null && card.Words.HandLines != null && card.Words.Lines.Count > card.Words.HandLines.Count,
                Keywords = Core2CombatText.Traits(Strings, card),
                Affordable = card.Playable,
                Upgraded = card.Upgraded,
                RefusalText = card.Refusal,
                Costs = costs,
            };
        }

        private void RenderFooter()
        {
            var p = View.Player;
            var charges = View.Flasks.Sum(f => Int(f.Current));
            var ready = View.EndTurnRefusal == null && p.Stamina <= 0;
            _footer.Compact = Nav.Layout?.Narrow ?? false;
            _footer.Set(Int(p.Stamina), Int(p.MaxStamina), View.DrawCount, View.DiscardCount, charges, ready);
            _footer.SetPiles(View.DiscardCount, View.ExhaustCount);
            _footer.SetMana(Int(p.Mana), Int(p.MaxMana));
            _footer.SetBusy(_busy, _busy ? StringKeys.CombatSkip : StringKeys.KitFooterEndTurn);
            _footer.SetEnabled(!_flow.Ended || _busy);
        }

        private void RenderTools()
        {
            var reactions = Root.Q<LocButton>(UiNames.ToolReactions);
            reactions?.SetResolved(Core2CombatText.Reactions(Strings, View.ReactionsEnabled));
            reactions?.EnableInClassList(UiClasses.ToolOn, View.ReactionsEnabled);
            var idle = !_busy && !_flow.Ended;
            reactions?.SetEnabled(idle);
            Root.Q<LocButton>(UiNames.ToolGrid)?.EnableInClassList(UiClasses.ToolOn, _gridOn);
            UiDom.Show(Root.Q(UiNames.StageGrid), _gridOn);
            UiDom.Show(Root.Q(UiNames.CombatLog), _logOpen);
            Root.Q<LocButton>(UiNames.ToolArmaments)?.SetEnabled(idle && View.Equipment != null && View.Equipment.Slots.Count > 0);
            var recover = Root.Q<LocButton>(UiNames.ToolRecover);
            UiDom.Show(recover, idle && _flow.CanRecover);
            var prompt = Root.Q<LocLabel>(UiNames.TargetPrompt);
            var armed = _flow.Armed?.Name ?? _flow.ArmedFlask?.Name;
            prompt?.SetResolved(armed == null ? string.Empty : Strings.Format(StringKeys.CombatTargetCard, new StringArgs().Add(UiPlaceholders.Name, armed)));
            UiDom.Show(prompt, _flow.Targeting && !_busy);
        }

        /// <summary>Opens or closes the combat log panel (a view-only toggle: the footer's Combat log control, its Close button and back).</summary>
        private void SetLog(bool open)
        {
            _logOpen = open;
            RenderTools();
            FocusSoon(open ? Root.Q(UiNames.LogClose) : Root.Q(UiNames.ToolLog));
        }

        private void RenderLog()
        {
            var host = Root.Q(UiNames.LogLines);
            if (host == null) return;
            host.Clear();
            var lines = View.Log.Where(l => !string.IsNullOrEmpty(l.Text)).ToList();
            var visible = Math.Max(0, lines.Count - _hiddenLines);
            var from = Math.Max(0, visible - Ui.Data.Components.LogLines);
            for (var i = from; i < visible; i++)
            {
                var label = new LocLabel();
                label.SetResolved(lines[i].Text);
                label.AddToClassList(UiClasses.LogLine);
                label.EnableInClassList(UiClasses.LogLineNewest, i == visible - 1);
                label.AddToClassList(UiClasses.ValueText);
                label.pickingMode = PickingMode.Ignore;
                host.Add(label);
            }
        }

        // ------------------------------------------------------------------ the prompt panel

        private void RenderPrompt()
        {
            var panel = Root.Q(UiNames.PromptPanel);
            var prompt = ShowingPrompt ? _flow.Prompt : null;
            UiDom.Show(panel, prompt != null);
            _options.Clear();
            var list = Root.Q<ScrollView>(UiNames.PromptList);
            var back = Root.Q<LocButton>(UiNames.PromptBack);
            var confirm = Root.Q<LocButton>(UiNames.PromptConfirm);
            if (prompt == null)
            {
                list.Clear();
                UiDom.Show(confirm, false);
                return;
            }
            var offer = prompt.Kind == Core2UiValues.PromptOffer;
            var cards = prompt.Kind == Core2UiValues.PromptCards;
            Root.Q<LocLabel>(UiNames.PromptTitle)?.SetResolved(offer ? Strings.Get(StringKeys.CombatModalReactionTitle) : PromptTitle(prompt));
            var eyebrow = Root.Q<LocLabel>(UiNames.PromptEyebrow);
            eyebrow?.SetResolved(offer ? Strings.Get(StringKeys.CombatModalReactionEyebrow) : string.Empty);
            UiDom.Show(eyebrow, offer);
            _options.AddRange(_promptRows.Fill(list.contentContainer, prompt, option => OptionText(prompt, option), (option, anchor) => Handle(_flow.Choose(option), anchor), Refuse));
            // The web closes a reaction offer from its X, Back or Escape (all decline it); a card choice cannot be left; every other prompt closes with Back.
            UiDom.Show(Root.Q(UiNames.PromptClose), !cards);
            UiDom.Show(back, !cards);
            back.SetResolved(Strings.Get(offer ? StringKeys.CombatModalReactionBack : StringKeys.Core2PromptBack));
            UiDom.Show(confirm, cards || offer);
            if (cards)
            {
                var picked = _flow.PickedCards.Count;
                confirm.SetResolved(Strings.Format(StringKeys.Core2PromptConfirm, new StringArgs().Add(UiPlaceholders.Selected, picked).Add(UiPlaceholders.Count, prompt.Count)));
                confirm.EnableInClassList(UiClasses.ButtonReady, picked == prompt.Count);
            }
            else if (offer) RenderPromptConfirm();
        }

        /// <summary>The reaction offer's Play: dim until a card is picked, then the primary action (the web's Play).</summary>
        private void RenderPromptConfirm()
        {
            if (_flow.Prompt?.Kind != Core2UiValues.PromptOffer) return;
            var confirm = Root.Q<LocButton>(UiNames.PromptConfirm);
            confirm.SetResolved(Strings.Get(StringKeys.CombatModalReactionPlay));
            var ready = _promptRows.PickedOffer != null;
            confirm.EnableInClassList(UiClasses.ButtonReady, ready);
            confirm.EnableInClassList(UiClasses.PromptOptionDisabled, !ready);
        }

        /// <summary>Confirm in the prompt panel: answer a card choice with the cards picked, or play the picked reaction.</summary>
        private void OnPromptConfirm(LocButton confirm)
        {
            if (_flow.Prompt?.Kind == Core2UiValues.PromptOffer)
            {
                if (_promptRows.PickedOffer != null) Handle(_flow.Choose(_promptRows.PickedOffer), confirm);
                return;
            }
            Handle(_flow.ConfirmPicks(), confirm);
        }

        /// <summary>The menu button: the quick menu under it (the pause list as a dropdown); pad Start still opens the pause screen.</summary>
        private void OpenQuickMenu(VisualElement anchor)
        {
            if (_busy) Skip();
            if (_flow.Ended || _flow.Prompt != null) return;
            _targets.Hide();
            _overlays.OpenQuick(anchor);
        }

        private string PromptTitle(Core2Prompt prompt)
        {
            switch (prompt.Kind)
            {
                case Core2UiValues.PromptChoice: return Strings.Format(StringKeys.Core2PromptChoice, new StringArgs().Add(UiPlaceholders.Name, prompt.Card?.Name));
                case Core2UiValues.PromptRoute: return Strings.Format(StringKeys.Core2PromptRoute, new StringArgs().Add(UiPlaceholders.Target, prompt.Enemy?.Name ?? string.Empty));
                case Core2UiValues.PromptOffer: return Strings.Format(StringKeys.Core2PromptOffer, new StringArgs().Add(UiPlaceholders.Source, prompt.Offer?.SourceName ?? string.Empty));
                case Core2UiValues.PromptCards: return Strings.Format(StringKeys.Core2PromptCards, new StringArgs().Add(UiPlaceholders.Count, prompt.Count));
                case Core2UiValues.PromptFlasks: return Strings.Get(StringKeys.CombatFlasksTitle);
                case Core2UiValues.PromptEquipment: return Strings.Get(StringKeys.Core2PromptEquipment);
                case Core2UiValues.PromptChange: return Strings.Format(StringKeys.Core2PromptChange, new StringArgs().Add(UiPlaceholders.Name, prompt.Set?.Name ?? Strings.Get(StringKeys.Core2EquipmentEmpty)));
                case Core2UiValues.PromptRecover: return Strings.Get(StringKeys.Core2PromptRecover);
                case Core2UiValues.PromptPredict: return Strings.Format(StringKeys.Core2PredictTitle, new StringArgs().Add(UiPlaceholders.Name, prompt.Enemy?.Name ?? string.Empty));
                default: return string.Empty;
            }
        }

        private string OptionText(Core2Prompt prompt, Core2PromptOption option)
        {
            switch (prompt.Kind)
            {
                case Core2UiValues.PromptRoute: return Core2CombatText.Route(Strings, option);
                case Core2UiValues.PromptOffer: return Core2CombatText.OfferOption(Strings, option.Offer);
                case Core2UiValues.PromptPredict: return Core2CombatText.Maneuver(Strings, option.Id);
                case Core2UiValues.PromptChange: return Core2CombatText.Change(Strings, option.Change);
                case Core2UiValues.PromptRecover: return Core2CombatText.Recover(Strings, option);
                case Core2UiValues.PromptEquipment:
                    return option.Id == Core2UiValues.OptionChange ? Strings.Get(StringKeys.Core2EquipmentChange) : Core2CombatText.Set(Strings, option.Slot, option.Set);
                case Core2UiValues.PromptFlasks:
                    var flask = option.Flask;
                    return flask.ChargeKind != null
                        ? Strings.Format(StringKeys.CombatFlasksCharge, new StringArgs().Add(UiPlaceholders.Name, flask.Name).Add(UiPlaceholders.Value, Int(flask.Current)).Add(UiPlaceholders.Max, Int(flask.Max)))
                        : Strings.Format(StringKeys.CombatFlasksCarried, new StringArgs().Add(UiPlaceholders.Name, flask.Name));
                default: return option.Label ?? option.Id;
            }
        }

        // ------------------------------------------------------------------ focus (US-5.9: kept across redraws)

        private string FocusIdentity()
        {
            var focused = Context.Instance.Focus.Focused;
            if (focused is KitCard card && _hand != null && View != null)
            {
                var i = _hand.Cards.ToList().IndexOf(card);
                if (i >= 0 && i < View.Hand.Count) return View.Hand[i].InstanceId;
            }
            if (focused is Fighter f) return f.Data?.Id;
            return null;
        }

        private void RestoreFocus(string identity)
        {
            if (identity == null) return;
            var i = View.Hand.FindIndex(c => c.InstanceId == identity);
            if (i >= 0 && i < _hand.Cards.Count && _hand.Cards[i].focusable) { _hand.Cards[i].Focus(); return; }
            if (_enemies.TryGetValue(identity, out var enemy) && enemy.focusable) enemy.Focus();
        }

        private VisualElement FirstPlayableCard()
        {
            var i = View.Hand.FindIndex(c => c.Playable);
            return i >= 0 && i < _hand.Cards.Count ? _hand.Cards[i] : (VisualElement)_footer.EndTurnButton;
        }

        private VisualElement FirstTarget()
        {
            var id = _flow.LegalTargets.FirstOrDefault();
            return id != null && _enemies.TryGetValue(id, out var f) ? f : null;
        }

        private void FocusSoon(VisualElement element)
        {
            if (element == null) return;
            Context.Instance.LastFocused = element;
            Nav.FocusTop(element);
        }

        // ------------------------------------------------------------------ presses → the flow

        private void OnCard(int index, KitCard anchor)
        {
            if (_busy || _flow.Ended) return;
            CaptureFlight(index < View.Hand.Count ? View.Hand[index].InstanceId : null, null);
            Handle(_flow.SelectCard(index), anchor);
        }

        private void OnFighter(Fighter fighter)
        {
            if (_busy || fighter?.Data == null || !_flow.Targeting) return;
            CaptureFlight(_flow.Armed?.InstanceId, fighter.Data.Id);
            Handle(_flow.PickTarget(fighter.Data.Id), fighter);
        }

        private void OnPredict(Fighter fighter)
        {
            if (_busy || fighter?.Data == null) return;
            Handle(_flow.OpenPredict(fighter.Data.Id), fighter.PredictButton);
        }

        private void OnEndTurn()
        {
            if (_busy) { Skip(); return; }
            if (_flow.Ended) return;
            Handle(_flow.EndTurn(), _footer.EndTurnButton);
        }

        private void OnPromptBack()
        {
            if (_flow.Prompt?.Kind == Core2UiValues.PromptOffer) { Handle(_flow.DeclineOffer(), Root.Q(UiNames.PromptBack)); return; }
            if (_flow.Back()) Render();
        }

        /// <summary>The one place a press's result is shown: a refusal at the control, a step the flow opened, or an intent that ran (its log lines walked at the combat pace).</summary>
        private void Handle(Core2StepResult step, VisualElement anchor)
        {
            try
            {
                if (step.Error != null) Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.Core2StepFailed, step.Error));
                if (step.Refusal != null)
                {
                    var slot = SlotOf(anchor);
                    Render();
                    anchor = Live(anchor, slot);
                    Refuse(anchor, step.Refusal);
                    return;
                }
                if (!step.Ran)
                {
                    Render();
                    return;
                }
                StartTimeline(step);
            }
            catch (Exception e)
            {
                // Never leave the screen locked or stale because a presenter step threw.
                Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.Core2StepFailed, e));
                Unlock();
                Refuse(anchor, Strings.Get(StringKeys.Core2ErrorUnexpected));
            }
        }

        /// <summary>Where a pressed control sits before a redraw replaces it: its card's instance id in the hand, or its position in the prompt's options.</summary>
        private (string Card, int Option) SlotOf(VisualElement anchor)
        {
            var card = anchor as KitCard;
            var c = card == null ? -1 : _hand.Cards.ToList().IndexOf(card);
            return (c >= 0 && c < _handIds.Count ? _handIds[c] : null, anchor is Button b ? _options.IndexOf(b) : -1);
        }

        /// <summary>The control a redraw left in place of the pressed one (same card, same option), else its stable parent, so a refusal and focus never land on a detached element.</summary>
        private VisualElement Live(VisualElement anchor, (string Card, int Option) slot)
        {
            if (anchor == null || anchor.panel != null) return anchor;
            if (slot.Card != null)
            {
                var i = View.Hand.FindIndex(c => c.InstanceId == slot.Card);
                return i >= 0 && i < _hand.Cards.Count ? _hand.Cards[i] : _hand;
            }
            if (slot.Option >= 0) return slot.Option < _options.Count ? _options[slot.Option] : Root.Q(UiNames.PromptList);
            return Root.Q(UiNames.PromptList);
        }

        /// <summary>Drops the enemy-turn lock and redraws from the flow's current view (each step guarded: a throwing redraw is logged, never left half done).</summary>
        private void Unlock()
        {
            CancelFx();
            _hiddenLines = 0;
            _acting = null;
            _busy = false;
            try { _flow.Refresh(); Render(); }
            catch (Exception e) { Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.Core2StepFailed, e)); }
        }

        /// <summary>Apply a result the flow produced outside the controls (the PlayMode smoke finishes a fight through the flow, then lets the screen catch up).</summary>
        public void Apply(Core2StepResult step) => Handle(step, _footer.EndTurnButton);

        // The enemy turn and every other play's feedback run on the web's timeline: Core2CombatScreen.Fx.cs (StartTimeline, Skip, FinishTimeline, AfterStep).

        // ------------------------------------------------------------------ the end of the fight

        private void ShowEnded()
        {
            // A won fight leads to its spoils (the reward door the session opened): the victory beat has been, the next thing is the screen that holds them (VP-7).
            if (View.Result == CombatValues.Victory && _run.HasPendingReward && !_run.RunOver)
            {
                RunFlow.Show(Context, _run);
                return;
            }
            Render();
            UiDom.Show(Root.Q(UiNames.PhaseBanner), false);
            var victory = View.Result == CombatValues.Victory;
            var over = _run.RunOver;
            Root.Q<LocLabel>(UiNames.EndTitle)?.SetResolved(Strings.Get(victory ? StringKeys.CombatEndVictory : StringKeys.CombatEndDefeat));
            Root.Q<LocLabel>(UiNames.EndBody)?.SetResolved(Strings.Format(over && !victory ? StringKeys.CombatEndDefeatBody : StringKeys.Core2EndBody,
                new StringArgs().Add(UiPlaceholders.Slot, _run.SlotIndex)));
            var notice = victory ? Core2PortView.FightNotice(_run, Ui.Data) : null;
            var planned = Root.Q<LocLabel>(UiNames.EndPlanned);
            planned?.SetResolved(notice ?? string.Empty);
            UiDom.Show(planned, !string.IsNullOrEmpty(notice));
            var next = Root.Q<LocButton>(UiNames.EndContinue);
            var summary = Root.Q<LocButton>(UiNames.EndSummary);
            var toTitle = Root.Q<LocButton>(UiNames.EndToTitle);
            UiDom.Show(next, !over);
            UiDom.Show(summary, over);
            UiDom.Show(toTitle, over && !Ui.Data.Screens.IsBuilt(ScreenIds.Core2RunEnd));
            var end = Root.Q(UiNames.CombatEnd);
            end.EnableInClassList(UiClasses.CombatEndVictory, victory);
            end.EnableInClassList(UiClasses.CombatEndDefeat, !victory);
            UiDom.Show(end, true);
            FocusSoon(over ? (summary.ClassListContains(UiClasses.Hidden) ? (VisualElement)toTitle : summary) : next);
        }

        /// <summary>Continue after a won fight: back to the Core 2 act map (the run stands where the fight ended it).</summary>
        private void OnContinue()
        {
            if (!_flow.Ended || _run.RunOver) return;
            RunFlow.Show(Context, _run);
        }

        /// <summary>The run is over (a death, or the last act's boss felled): the run end the run's location routes to (VP-12).</summary>
        private void OpenRunEnd()
        {
            if (!_flow.Ended || !_run.RunOver) return;
            RunFlow.Show(Context, _run);
        }

        // ------------------------------------------------------------------ pause and back

        private void OpenPause()
        {
            if (_busy) Skip();
            if (_flow.Ended) return;
            Nav.OpenModal(ScreenIds.Pause, new PauseArgs { Session = _run });
        }

        public override bool HandleBack()
        {
            if (_introOpen) { CloseIntro(); return true; }
            if (_overlays != null && _overlays.HandleBack()) return true;
            if (_targets != null && _targets.IsPinned) { _targets.Hide(); return true; }
            if (_logOpen) { SetLog(false); return true; }
            if (_busy) { Skip(); return true; }
            if (_flow.Ended) return true;
            if (_flow.Prompt?.Kind == Core2UiValues.PromptOffer)
            {
                OnPromptBack();
                return true;
            }
            if (_flow.Back())
            {
                Render();
                FocusSoon(FirstPlayableCard());
                return true;
            }
            if (_flow.Prompt != null) return true;
            OpenPause();
            return true;
        }

        public override bool HandleMenu()
        {
            OpenPause();
            return true;
        }

        public override void OnReturned()
        {
            if (_flow == null || _run?.Combat == null) return;
            _flow.Refresh();
            Render();
        }

        public override void Unbind()
        {
            CancelFx();
            _introTimer?.Pause();
        }

        // ------------------------------------------------------------------ helpers

        private void Refuse(VisualElement anchor, string text)
        {
            if (anchor == null || string.IsNullOrEmpty(text)) return;
            Kit.Refusal.Show(anchor, Context.Overlay, text, Ui.Data.Tokens.Duration(TokenKeys.Refusal));
        }

        private void ShowNotice(string key)
        {
            var notice = Root.Q<LocLabel>(UiNames.Notice);
            if (notice == null) return;
            notice.SetResolved(Strings.Get(key));
            UiDom.Show(notice, true);
            notice.schedule.Execute(() => UiDom.Show(notice, false)).StartingIn(Ui.Data.Tokens.Duration(TokenKeys.Notice));
        }

        private static int Int(double value) => (int)Math.Floor(value);
    }
}
