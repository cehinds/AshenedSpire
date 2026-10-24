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
    public sealed class Core2CombatScreen : ScreenView
    {
        private readonly Dictionary<string, Fighter> _enemies = new Dictionary<string, Fighter>(StringComparer.Ordinal);
        private readonly List<LocButton> _options = new List<LocButton>();
        private Core2RunSession _run;
        private Core2CombatFlow _flow;
        private Fighter _player;
        private HandFan _hand;
        private FooterGroup _footer;
        private IVisualElementScheduledItem _timeline;
        private IVisualElementScheduledItem _bannerHide;
        private List<List<Core2LogLine>> _segments = new List<List<Core2LogLine>>();
        private int _revealed;
        private int _hiddenLines;
        private bool _busy;
        private string _acting;
        private bool _wasPrompt;
        private bool _wasTargeting;

        public Core2RunSession Run => _run;
        public Core2CombatFlow Flow => _flow;
        public Core2CombatViewState View => _flow?.View;

        /// <summary>The focus mode in force (a ui/screens.json focusModes key, or null for normal play).</summary>
        public string Mode => _busy ? UiValues.FocusEnemyTurn : _flow?.Mode;

        public override string FocusMode => Mode;

        public bool EnemyTurn => _busy;
        public bool Ended => _flow != null && _flow.Ended && !_busy;
        public IReadOnlyList<KitCard> HandCards => _hand?.Cards ?? (IReadOnlyList<KitCard>)new KitCard[0];
        public IReadOnlyDictionary<string, Fighter> Enemies => _enemies;
        public Fighter Player => _player;
        public FooterGroup Footer => _footer;

        /// <summary>The buttons of the open prompt, in order.</summary>
        public IReadOnlyList<LocButton> PromptOptions => _options;

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
            _flow = new Core2CombatFlow(_run, Strings);
            _hand = Root.Q<HandFan>(UiNames.Hand);
            _footer = Root.Q<FooterGroup>(UiNames.FooterGroup);
            _footer.EndTurn += OnEndTurn;
            _footer.Potions += () => Handle(_flow.OpenFlasks(), _footer.PotionsButton);
            _footer.Piles += anchor => RunFlow.Refuse(Context, anchor, StringKeys.CombatPilesLater);
            Root.Q<LocButton>(UiNames.HudMenu).clicked += OpenPause;
            Root.Q<LocButton>(UiNames.PromptBack).clicked += OnPromptBack;
            var confirm = Root.Q<LocButton>(UiNames.PromptConfirm);
            confirm.clicked += () => Handle(_flow.ConfirmPicks(), confirm);
            var reactions = Root.Q<LocButton>(UiNames.ToolReactions);
            reactions.clicked += () => Handle(_flow.ToggleReactions(), reactions);
            var armaments = Root.Q<LocButton>(UiNames.ToolArmaments);
            armaments.clicked += () => Handle(_flow.OpenEquipment(), armaments);
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
            _player = new Fighter { focusable = false };
            Root.Q(UiNames.AllyRow).Add(_player);
            Render();
            if ((args as Core2RunArgs)?.ResumeWarning == true) ShowNotice(StringKeys.CombatResumeWarning);
            if (_flow.Ended) EnterEnded();
            else Context.Instance.LastFocused = FirstPlayableCard();
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
            var portrait = Root.Q(UiNames.HudPortrait);
            if (portrait != null) Ui.ApplyBackground(portrait, _run.Portrait);
            Root.Q<LocLabel>(UiNames.HudName)?.SetResolved(Strings.Format(StringKeys.CombatIdentity, new StringArgs()
                .Add(UiPlaceholders.Name, _run.Name).Add(UiPlaceholders.Class, RunFlow.ClassName(Ui, _run.ClassId))));
            Root.Q<LocLabel>(UiNames.HudTrail)?.SetResolved(Strings.Format(StringKeys.CombatTrail, new StringArgs()
                .Add(UiPlaceholders.Act, _run.Act).Add(UiPlaceholders.Region, RunHudView.SeatName(Ui.Data, _run.SeatId)).Add(UiPlaceholders.Turn, Int(View.Turn))));
            Root.Q<Meter>(UiNames.HudHp)?.Set(Int(p.Hp), Int(p.MaxHp));
            Root.Q<Meter>(UiNames.HudMp)?.Set(Int(p.Mana), Int(p.MaxMana));
            Root.Q<Meter>(UiNames.HudSp)?.Set(Int(p.Stamina), Int(p.MaxStamina));
        }

        private void RenderFighters()
        {
            var order = Ui.Data.Components.FighterRatings;
            _player.Bind(FighterData(View.Player, true, order, 0));
            var row = Root.Q(UiNames.EnemyRow);
            var reference = View.Enemies.Count == 0 ? 0 : Int(View.Enemies.Max(e => e.MaxHp));
            foreach (var enemy in View.Enemies)
            {
                if (!_enemies.TryGetValue(enemy.Id, out var element))
                {
                    element = new Fighter();
                    element.Activated += OnFighter;
                    element.PredictPressed += OnPredict;
                    row.Add(element);
                    _enemies[enemy.Id] = element;
                }
                var data = FighterData(enemy, false, order, reference);
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
                Alive = f.Alive,
                Hp = Int(f.Hp),
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
            if (f.Poise != null) { d.Poise = Int(f.Poise.Value); d.PoiseMax = Int(f.Poise.Max); }
            if (d.HasWard) { d.Ward = Int(f.Ward.Value); d.WardMax = Int(f.Ward.Max); }
            if (f.Alive && f.Intent != null)
            {
                d.IntentText = Core2CombatText.Intent(Strings, f.Intent);
                d.IntentKind = Core2CombatText.IntentKind(f.Intent);
            }
            return d;
        }

        private void RenderHand()
        {
            var components = Ui.Data.Components;
            var cards = View.Hand.Select(CardData).ToList();
            _hand.SetCards(cards, components.DegreesPerCard, components.MaxSpread, components.ScrollAfter);
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
                view.focusable = Mode == null;
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
                if (kind == Core2UiValues.CostStamina) costs.Add(new CardCostData { Kind = kind, Text = card.CostIsX ? Strings.Get(StringKeys.CombatCostX) : CombatText.Number(card.Stamina) });
                else if (kind == Core2UiValues.CostMana && card.Mana > 0) costs.Add(new CardCostData { Kind = kind, Text = CombatText.Number(card.Mana) });
            }
            return new CardViewData
            {
                Name = card.Name,
                TypeLine = Core2CombatText.TypeLine(Strings, card),
                Text = card.Text,
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
            _footer.SetBusy(_busy, _busy ? StringKeys.CombatSkip : StringKeys.KitFooterEndTurn);
            _footer.SetEnabled(!_flow.Ended || _busy);
        }

        private void RenderTools()
        {
            var reactions = Root.Q<LocButton>(UiNames.ToolReactions);
            reactions?.SetResolved(Core2CombatText.Reactions(Strings, View.ReactionsEnabled));
            var idle = !_busy && !_flow.Ended;
            reactions?.SetEnabled(idle);
            Root.Q<LocButton>(UiNames.ToolArmaments)?.SetEnabled(idle && View.Equipment != null && View.Equipment.Slots.Count > 0);
            var recover = Root.Q<LocButton>(UiNames.ToolRecover);
            UiDom.Show(recover, idle && _flow.CanRecover);
            var prompt = Root.Q<LocLabel>(UiNames.TargetPrompt);
            var armed = _flow.Armed?.Name ?? _flow.ArmedFlask?.Name;
            prompt?.SetResolved(armed == null ? string.Empty : Strings.Format(StringKeys.CombatTargetCard, new StringArgs().Add(UiPlaceholders.Name, armed)));
            UiDom.Show(prompt, _flow.Targeting && !_busy);
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
            list.Clear();
            var back = Root.Q<LocButton>(UiNames.PromptBack);
            var confirm = Root.Q<LocButton>(UiNames.PromptConfirm);
            if (prompt == null)
            {
                UiDom.Show(confirm, false);
                return;
            }
            Root.Q<LocLabel>(UiNames.PromptTitle)?.SetResolved(PromptTitle(prompt));
            foreach (var option in prompt.Options)
            {
                var button = new LocButton();
                button.AddToClassList(UiClasses.PromptOption);
                button.SetResolved(OptionText(prompt, option));
                button.EnableInClassList(UiClasses.PromptOptionSelected, option.Selected);
                button.EnableInClassList(UiClasses.PromptOptionDisabled, !option.Enabled);
                var captured = option;
                button.clicked += () => Handle(_flow.Choose(captured), button);
                list.Add(button);
                _options.Add(button);
            }
            // A reaction offer is declined with its own Back; a card choice cannot be left; every other prompt closes with Back.
            var offer = prompt.Kind == Core2UiValues.PromptOffer;
            var cards = prompt.Kind == Core2UiValues.PromptCards;
            UiDom.Show(back, !cards);
            back.SetResolved(Strings.Get(offer ? StringKeys.Core2OfferBack : StringKeys.Core2PromptBack));
            UiDom.Show(confirm, cards);
            if (cards)
            {
                var picked = _flow.PickedCards.Count;
                confirm.SetResolved(Strings.Format(StringKeys.Core2PromptConfirm, new StringArgs().Add(UiPlaceholders.Selected, picked).Add(UiPlaceholders.Count, prompt.Count)));
                confirm.EnableInClassList(UiClasses.ButtonReady, picked == prompt.Count);
            }
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
            Handle(_flow.SelectCard(index), anchor);
        }

        private void OnFighter(Fighter fighter)
        {
            if (_busy || fighter?.Data == null || !_flow.Targeting) return;
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
                    Render();
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

        /// <summary>Drops the enemy-turn lock and redraws from the flow's current view (each step guarded: a throwing redraw is logged, never left half done).</summary>
        private void Unlock()
        {
            _timeline?.Pause();
            _timeline = null;
            _segments.Clear();
            _hiddenLines = 0;
            _acting = null;
            _busy = false;
            try { _flow.Refresh(); Render(); }
            catch (Exception e) { Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.Core2StepFailed, e)); }
        }

        /// <summary>Apply a result the flow produced outside the controls (the PlayMode smoke finishes a fight through the flow, then lets the screen catch up).</summary>
        public void Apply(Core2StepResult step) => Handle(step, _footer.EndTurnButton);

        // ------------------------------------------------------------------ the enemy turn at the combat pace (D11)

        /// <summary>The seconds one step takes: the animation step then the stage gap, both after the speed multiplier (auto combat has its own); reduced motion drops the animation.</summary>
        private double StepMs()
        {
            var pacing = _flow.Pacing;
            var reduced = _run.PlayerSettings.On(SettingIds.ReducedMotion);
            var ms = pacing.StageGapScaledMs + (reduced ? 0d : pacing.AnimationStepScaledMs);
            return Math.Max(1d, ms);
        }

        /// <summary>Splits the new log lines into steps, one per enemy that acted (the lines of its action stay with it), and walks them; with nothing to walk the screen simply catches up.</summary>
        private void StartTimeline(Core2StepResult step)
        {
            // The animation speed 'instant' walks nothing: the screen catches up at once.
            _segments = _flow.Pacing.AnimSpeed == Core2UiValues.AnimInstant ? new List<List<Core2LogLine>>() : Segments(step);
            _revealed = 0;
            var lines = step.Timeline.Count(l => !string.IsNullOrEmpty(l.Text));
            if (_segments.Count == 0)
            {
                _hiddenLines = 0;
                Render();
                AfterStep();
                return;
            }
            _busy = true;
            _hiddenLines = lines;
            _acting = null;
            Render();
            FocusSoon(_footer.EndTurnButton);
            Banner(StringKeys.CombatEnemyTurn, null, 0);
            var ms = (long)StepMs();
            _timeline?.Pause();
            _timeline = Root.schedule.Execute(StepTimeline).StartingIn(ms).Every(ms);
        }

        private static List<List<Core2LogLine>> Segments(Core2StepResult step)
        {
            var segments = new List<List<Core2LogLine>>();
            var actors = new HashSet<string>(step.Actors, StringComparer.Ordinal);
            if (actors.Count == 0) return segments;
            string current = null;
            foreach (var line in step.Timeline.Where(l => !string.IsNullOrEmpty(l.Text)))
            {
                var actor = line.SourceId != null && actors.Contains(line.SourceId) ? line.SourceId : null;
                if (segments.Count == 0 || (actor != null && actor != current))
                {
                    segments.Add(new List<Core2LogLine>());
                    current = actor ?? current;
                }
                segments[segments.Count - 1].Add(line);
            }
            return segments;
        }

        private void StepTimeline()
        {
            try
            {
                if (_revealed >= _segments.Count)
                {
                    FinishTimeline();
                    return;
                }
                var segment = _segments[_revealed++];
                _hiddenLines = Math.Max(0, _hiddenLines - segment.Count);
                var lead = segment.FirstOrDefault(l => l.SourceId != null && _enemies.ContainsKey(l.SourceId));
                _acting = lead?.SourceId;
                Banner(StringKeys.CombatActing, lead?.SourceName, 0);
                Render();
            }
            catch (Exception e)
            {
                Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.Core2StepFailed, e));
                Unlock();
            }
        }

        /// <summary>Skip ▶▶: end the timeline now.</summary>
        public void Skip()
        {
            if (!_busy) return;
            FinishTimeline();
        }

        private void FinishTimeline()
        {
            Unlock();
            try { AfterStep(); }
            catch (Exception e) { Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.Core2StepFailed, e)); }
        }

        /// <summary>After an intent and its walk: the end of the fight, or the player's turn again.</summary>
        private void AfterStep()
        {
            if (_flow.Ended) { EnterEnded(); return; }
            if (_flow.Prompt != null) return;
            UiDom.Show(Root.Q(UiNames.PhaseBanner), false);
            FocusSoon(_flow.Targeting ? FirstTarget() : FirstPlayableCard());
        }

        private void Banner(string key, string name, int hideAfterMs)
        {
            var banner = Root.Q<LocLabel>(UiNames.PhaseBanner);
            if (banner == null) return;
            banner.SetResolved(Strings.Format(key, new StringArgs().Add(UiPlaceholders.Name, name)));
            UiDom.Show(banner, true);
            _bannerHide?.Pause();
            if (hideAfterMs > 0) _bannerHide = banner.schedule.Execute(() => UiDom.Show(banner, false)).StartingIn(hideAfterMs);
        }

        // ------------------------------------------------------------------ the end of the fight

        private void EnterEnded()
        {
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
            UiDom.Show(toTitle, over && !Ui.Data.Screens.IsBuilt(ScreenIds.RunEnd));
            UiDom.Show(Root.Q(UiNames.CombatEnd), true);
            FocusSoon(over ? (summary.ClassListContains(UiClasses.Hidden) ? (VisualElement)toTitle : summary) : next);
        }

        /// <summary>Continue after a won fight: back to the Core 2 act map (the run stands where the fight ended it).</summary>
        private void OnContinue()
        {
            if (!_flow.Ended || _run.RunOver) return;
            RunFlow.Show(Context, _run);
        }

        /// <summary>The run is over (a death, or the last act's boss felled): W-15 run end.</summary>
        private void OpenRunEnd()
        {
            if (!_flow.Ended || !_run.RunOver) return;
            Nav.Go(ScreenIds.RunEnd, new Core2RunArgs { Session = _run }, true);
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
            if (_busy) { Skip(); return true; }
            if (_flow.Ended) return true;
            if (_flow.Prompt?.Kind == Core2UiValues.PromptOffer) return true;
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
            _timeline?.Pause();
            _bannerHide?.Pause();
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
