using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;
using AppCard = Ashen.App.Combat.CardView;
using KitCard = Ashen.Presentation.UI.Kit.CardView;
using DomainRefusal = Ashen.Domain.Combat.Refusal;

namespace Ashen.Presentation.UI.Screens
{
    public sealed class CombatArgs
    {
        public RunSession Session;

        /// <summary>The resume skipped or cut the command log (D-017): say the last turn may be lost.</summary>
        public bool ResumeWarning;
    }

    /// <summary>
    /// W-07 combat (AF-05, PF-03; US-5.1 to US-5.10, US-6.x): bound only to the RunSession's CombatSession and the
    /// CombatViewModel. Every intent becomes a command through RunSession.Execute (legality first; a refusal is shown at
    /// the control that started it, from strings/combat.en.json, and focus stays there); accepted commands are autosaved
    /// by the session. States, each with its own focus order (ui/screens.json focusModes): normal; targeting (an armed
    /// card or flask outlines the legal targets and previews "HP 30 → 18"); the discard chooser when the hand rules prompt;
    /// the flask panel; the enemy turn (the field washes red, the acting enemy lifts in board order, End Turn becomes
    /// Skip ▶▶; reduced motion shortens each step); and the end state: a win opens W-08 with the rolled rewards (already
    /// saved at the rewards), a death has closed the run out and cleared the slot. Escape backs out of the innermost state, else opens W-20; pad Start opens W-20.
    /// </summary>
    public sealed class CombatScreen : ScreenView
    {
        private readonly Dictionary<string, Combatant> _enemies = new Dictionary<string, Combatant>(StringComparer.Ordinal);
        private readonly HashSet<string> _chosen = new HashSet<string>(StringComparer.Ordinal);
        private RunSession _session;
        private CombatViewState _view;
        private string _mode;
        private AppCard _armedCard;
        private FlaskView _armedFlask;
        private Combatant _player;
        private HandFan _hand;
        private FooterGroup _footer;
        private IVisualElementScheduledItem _timeline;
        private IVisualElementScheduledItem _bannerHide;
        private Queue<string> _actors = new Queue<string>();
        private string _acting;

        public RunSession Session => _session;
        public CombatViewState View => _view;

        /// <summary>The state in force (a ui/screens.json focusModes key, or null for normal play).</summary>
        public string Mode => _mode;

        public bool EnemyTurn => _mode == UiValues.FocusEnemyTurn;
        public bool Ended => _mode == UiValues.FocusEnded;
        public IReadOnlyList<KitCard> HandCards => _hand?.Cards ?? (IReadOnlyList<KitCard>)new KitCard[0];
        public IReadOnlyDictionary<string, Combatant> Enemies => _enemies;
        public FooterGroup Footer => _footer;

        public override string FocusMode => _mode;

        protected override void OnBind(object args)
        {
            var combatArgs = args as CombatArgs;
            _session = combatArgs?.Session ?? Ui.Session;
            if (_session?.Combat == null)
            {
                Debug.LogError(UiMessages.NoFight);
                return;
            }
            Ui.Session = _session;
            _hand = Root.Q<HandFan>(UiNames.Hand);
            _footer = Root.Q<FooterGroup>(UiNames.FooterGroup);
            _footer.EndTurn += OnEndTurn;
            _footer.Potions += ToggleFlasks;
            _footer.Piles += anchor => RunFlow.Refuse(Context, anchor, StringKeys.CombatPilesLater);
            Root.Q<LocButton>(UiNames.HudMenu).clicked += OpenPause;
            Root.Q<LocButton>(UiNames.DiscardBack).clicked += LeaveDiscard;
            var confirm = Root.Q<LocButton>(UiNames.DiscardConfirm);
            confirm.clicked += () => ConfirmDiscard(confirm);
            Root.Q<LocButton>(UiNames.FlaskClose).clicked += CloseFlasks;
            Root.Q<LocButton>(UiNames.EndToTitle).clicked += () =>
            {
                Ui.Session = null;
                Nav.Fire(ScreenTriggers.Quit);
            };
            var rewards = Root.Q<LocButton>(UiNames.EndRewards);
            rewards.SetEnabled(Ui.Data.Screens.IsBuilt(ScreenIds.Rewards));
            rewards.clicked += OpenRewards;
            Root.Q<LocButton>(UiNames.EndSummary).clicked += OpenRunEnd;

            ApplyBackground();
            _player = new Combatant { focusable = false };
            Root.Q(UiNames.AllyRow).Add(_player);
            Render();
            if (combatArgs?.ResumeWarning == true) ShowNotice(StringKeys.CombatResumeWarning);
            if (_session.Combat.IsOver) EnterEnded();
            else Context.Instance.LastFocused = FirstPlayableCard();
        }

        // ------------------------------------------------------------------ render

        private StringTable Strings => Ui.Data.Strings;

        private void ApplyBackground()
        {
            var components = Ui.Data.Components;
            var region = _session.Content.RegionOf(_session.EncounterId);
            var id = StringTable.Fill(components.CombatBackground, new StringArgs().Add(UiPlaceholders.Region, region));
            var element = Root.Q(UiNames.CombatBackground);
            Ui.ApplyBackground(element, Ui.Art != null && Ui.Art.Get(id) != null ? id : components.CombatFallbackBackground);
        }

        /// <summary>Rebuild the view from the committed state; focus stays on the same card or combatant (US-5.9).</summary>
        public void Render()
        {
            var keep = FocusIdentity();
            _view = CombatViewModel.Build(_session.Combat.State);
            foreach (var mode in new[] { UiValues.FocusTargeting, UiValues.FocusDiscard, UiValues.FocusFlasks, UiValues.FocusEnemyTurn, UiValues.FocusEnded })
                Root.EnableInClassList(UiClasses.CombatModePrefix + mode, _mode == mode);
            RenderHud();
            RenderCombatants();
            RenderHand();
            RenderFooter();
            RenderPrompts();
            RestoreFocus(keep);
        }

        private void RenderHud()
        {
            var p = _view.Player;
            var portrait = Root.Q(UiNames.HudPortrait);
            if (portrait != null) Ui.ApplyBackground(portrait, _session.Portrait);
            Root.Q<LocLabel>(UiNames.HudName)?.SetResolved(Strings.Format(StringKeys.CombatIdentity, new StringArgs()
                .Add(UiPlaceholders.Name, _session.Name).Add(UiPlaceholders.Class, RunFlow.ClassName(Ui, _session.ClassId))));
            Root.Q<LocLabel>(UiNames.HudTrail)?.SetResolved(Strings.Format(StringKeys.CombatTrail, new StringArgs()
                .Add(UiPlaceholders.Act, _session.Act).Add(UiPlaceholders.Region, SeatName()).Add(UiPlaceholders.Turn, (int)_view.Turn)));
            Root.Q<Meter>(UiNames.HudHp)?.Set(Int(p.Body.Hp), Int(p.Body.MaxHp));
            Root.Q<Meter>(UiNames.HudMp)?.Set(Int(p.Mana), Int(p.MaxMana));
            Root.Q<Meter>(UiNames.HudSp)?.Set(Int(p.Stamina), Int(p.MaxStamina));
        }

        private string SeatName()
        {
            var seat = _session.Content.Data.Encounters.Has(_session.EncounterId)
                ? _session.Content.Data.Encounters.Get(_session.EncounterId).Str(MapKeys.Seat) : null;
            return seat == null ? string.Empty : Strings.Get(string.Format(CultureInfo.InvariantCulture, UiFormats.SeatNameKey, seat));
        }

        private void RenderCombatants()
        {
            var p = _view.Player;
            var playerData = BodyData(p.Body, true);
            playerData.Name = Strings.Format(StringKeys.CombatIdentity, new StringArgs().Add(UiPlaceholders.Name, _session.Name).Add(UiPlaceholders.Class, RunFlow.ClassName(Ui, _session.ClassId)));
            playerData.Art = Ui.Texture(_session.Portrait);
            playerData.StanceText = p.StanceName == null ? null : Strings.Format(StringKeys.CombatStance, new StringArgs().Add(UiPlaceholders.Name, p.StanceName));
            if (p.PendingActionLoss > 0)
            {
                playerData.Staggered = true;
                playerData.BadgeText = Strings.Format(StringKeys.CombatStaggered, new StringArgs().Add(UiPlaceholders.Count, Int(p.PendingActionLoss)));
            }
            _player.Bind(playerData);

            var row = Root.Q(UiNames.EnemyRow);
            var reference = _view.Enemies.Count == 0 ? 0 : Int(_view.Enemies.Max(e => e.MaxHp));
            foreach (var enemy in _view.Enemies)
            {
                if (!_enemies.TryGetValue(enemy.Id, out var element))
                {
                    element = new Combatant();
                    element.Activated += OnCombatant;
                    row.Add(element);
                    _enemies[enemy.Id] = element;
                }
                var data = BodyData(enemy, false);
                data.ReferenceMaxHp = reference;
                data.Art = Ui.Texture(StringTable.Fill(Ui.Data.Components.EnemyArt, new StringArgs().Add(UiPlaceholders.Id, enemy.DefId)));
                if (enemy.Alive && enemy.Intent != null)
                {
                    data.IntentText = CombatText.Intent(Strings, enemy.Intent);
                    data.IntentKind = enemy.Intent.Kind;
                    data.Staggered = enemy.Intent.Kind == CombatValues.IntentStaggered;
                    if (data.Staggered) data.BadgeText = Strings.Get(enemy.Intent.LabelKey);
                }
                if (!enemy.Alive) data.BadgeText = Strings.Get(StringKeys.CombatDefeated);
                var legal = _mode == UiValues.FocusTargeting && IsLegalTarget(enemy.Id);
                data.Target = legal;
                data.Acting = enemy.Id == _acting;
                if (legal && _armedCard != null && _armedCard.DamageByTarget.TryGetValue(enemy.Id, out var damage))
                {
                    // Ghost values (04 W-07 targeting): the engine's preview damage, Block absorbing first.
                    var after = Math.Max(0d, enemy.Hp - Math.Max(0d, damage - enemy.Block));
                    data.PreviewText = Strings.Format(StringKeys.CombatTargetPreview, new StringArgs().Add(UiPlaceholders.Before, Int(enemy.Hp)).Add(UiPlaceholders.After, Int(after)));
                }
                element.Bind(data);
                element.focusable = enemy.Alive && (_mode == null || legal);
            }
        }

        private bool IsLegalTarget(string id)
        {
            if (_armedCard != null) return _armedCard.Targets.Contains(id);
            if (_armedFlask != null) return _view.Enemies.Any(e => e.Id == id && e.Alive);
            return false;
        }

        private CombatantData BodyData(CombatantView body, bool player)
        {
            var d = new CombatantData
            {
                Id = body.Id,
                Name = body.Name,
                Player = player,
                Alive = body.Alive,
                Hp = Int(body.Hp),
                MaxHp = Int(body.MaxHp),
                BlockText = body.Block > 0 ? Strings.Format(StringKeys.CombatBlock, new StringArgs().Add(UiPlaceholders.Value, Int(body.Block))) : null,
                HasBreakMeters = body.Poise != null || body.Ward != null,
            };
            if (body.Poise != null) { d.Poise = Int(body.Poise.Value); d.PoiseMax = Int(body.Poise.Max); }
            if (body.Ward != null) { d.Ward = Int(body.Ward.Value); d.WardMax = Int(body.Ward.Max); }
            var cap = Math.Max(1, Ui.Data.Components.StatusChips);
            var shown = body.Statuses.Count > cap ? cap - 1 : body.Statuses.Count;
            foreach (var s in body.Statuses.Take(shown))
                d.Statuses.Add(s.MeterValue.HasValue && s.MeterMax.HasValue
                    ? Strings.Format(StringKeys.CombatStatusMeter, new StringArgs().Add(UiPlaceholders.Name, s.Name).Add(UiPlaceholders.Value, Int(s.MeterValue.Value)).Add(UiPlaceholders.Max, Int(s.MeterMax.Value)))
                    : Strings.Format(StringKeys.CombatStatus, new StringArgs().Add(UiPlaceholders.Name, s.Name).Add(UiPlaceholders.Stacks, CombatText.Number(s.Stacks))));
            if (body.Statuses.Count > shown)
                d.Statuses.Add(Strings.Format(StringKeys.CombatStatusMore, new StringArgs().Add(UiPlaceholders.Count, body.Statuses.Count - shown)));
            return d;
        }

        private void RenderHand()
        {
            var components = Ui.Data.Components;
            var cards = _view.Hand.Select(CardData).ToList();
            _hand.SetCards(cards, components.DegreesPerCard, components.MaxSpread, components.ScrollAfter);
            var tipDelay = Ui.Data.Tokens.Duration(TokenKeys.TooltipDelay);
            for (var i = 0; i < _hand.Cards.Count; i++)
            {
                var view = _hand.Cards[i];
                var card = _view.Hand[i];
                var index = i;
                view.RegisterCallback<PointerUpEvent>(_ => OnCard(index, view));
                view.RegisterCallback<NavigationSubmitEvent>(e =>
                {
                    if (e.target != view) return;
                    OnCard(index, view);
                    e.StopPropagation();
                });
                view.EnableInClassList(UiClasses.CardArmed, _armedCard != null && _armedCard.InstanceId == card.InstanceId);
                view.EnableInClassList(UiClasses.CardChosen, _chosen.Contains(card.InstanceId));
                view.focusable = _mode == null || _mode == UiValues.FocusDiscard;
                if (!card.Playable && _mode == null)
                    Tooltip.AttachResolved(view, Context.Overlay, Ui.Data.Components.CardRefusalTooltip, card.Name, CombatText.Refusal(Strings, card.Refusal), tipDelay);
            }
        }

        private CardViewData CardData(AppCard card)
        {
            var costKinds = Ui.Data.Components.CostKinds;
            var costs = new List<CardCostData>();
            // Pips in ui/components.json card.costKinds order (actions, stamina, mana): Actions always, the pools when the card costs them.
            var amounts = new[] { card.Cost, card.StaminaCost, card.ManaCost };
            for (var i = 0; i < costKinds.Count && i < amounts.Length; i++)
            {
                if (i == 0) costs.Add(new CardCostData { Kind = costKinds[i], Text = card.CostIsX ? Strings.Get(StringKeys.CombatCostX) : CombatText.Number(amounts[i]) });
                else if (amounts[i] > 0) costs.Add(new CardCostData { Kind = costKinds[i], Text = CombatText.Number(amounts[i]) });
            }
            return new CardViewData
            {
                Name = card.Name,
                TypeLine = CombatText.CardKind(Strings, card.Kind),
                Text = card.Text,
                Affordable = card.Playable,
                Upgraded = card.Upgraded,
                RefusalText = CombatText.Refusal(Strings, card.Refusal),
                Costs = costs,
            };
        }

        private void RenderFooter()
        {
            var p = _view.Player;
            var charges = _view.Flasks.Sum(f => Int(f.Current));
            var ready = _view.EndTurnRefusal == null && p.Energy <= 0;
            _footer.Compact = Nav.Layout?.Narrow ?? false;
            _footer.Set(Int(p.Energy), Int(p.EnergyMax), _view.DrawCount, _view.DiscardCount, charges, ready);
            _footer.SetPiles(_view.DiscardCount, _view.ExhaustCount);
            _footer.SetBusy(EnemyTurn, EnemyTurn ? StringKeys.CombatSkip : StringKeys.KitFooterEndTurn);
            var discard = _mode == UiValues.FocusDiscard;
            UiDom.Show(_footer, !discard);
            UiDom.Show(Root.Q(UiNames.DiscardFooter), discard);
            _footer.SetEnabled(!Ended);
        }

        private void RenderPrompts()
        {
            var target = Root.Q<LocLabel>(UiNames.TargetPrompt);
            var armedName = _armedCard?.Name ?? _armedFlask?.Name;
            target?.SetResolved(armedName == null ? string.Empty : Strings.Format(StringKeys.CombatTargetCard, new StringArgs().Add(UiPlaceholders.Name, armedName)));
            UiDom.Show(target, _mode == UiValues.FocusTargeting);
            var discard = _mode == UiValues.FocusDiscard;
            UiDom.Show(Root.Q(UiNames.DiscardBar), discard);
            if (discard)
            {
                var min = Int(_view.DiscardMinimum);
                var max = Int(_view.DiscardMaximum);
                var args = new StringArgs().Add(UiPlaceholders.Min, min).Add(UiPlaceholders.Max, max).Add(UiPlaceholders.Cap, Math.Max(0, _view.Hand.Count - min));
                Root.Q<LocLabel>(UiNames.DiscardPrompt)?.SetResolved(Strings.Format(min == max ? StringKeys.CombatDiscardExact : StringKeys.CombatDiscardRange, args));
                Root.Q<LocLabel>(UiNames.DiscardCount)?.SetResolved(Strings.Format(StringKeys.CombatDiscardSelected, new StringArgs().Add(UiPlaceholders.Count, _chosen.Count).Add(UiPlaceholders.Max, max)));
                var confirm = Root.Q<LocButton>(UiNames.DiscardConfirm);
                confirm?.SetResolved(Strings.Format(StringKeys.CombatDiscardConfirm, new StringArgs().Add(UiPlaceholders.Count, _chosen.Count)));
                confirm?.EnableInClassList(UiClasses.ButtonReady, _chosen.Count >= min && _chosen.Count <= max);
            }
            var flasks = _mode == UiValues.FocusFlasks;
            var panel = Root.Q(UiNames.FlaskPanel);
            UiDom.Show(panel, flasks);
            if (flasks) RenderFlasks();
        }

        private void RenderFlasks()
        {
            var list = Root.Q(UiNames.FlaskList);
            list.Clear();
            foreach (var flask in _view.Flasks)
            {
                var chip = new LocButton();
                chip.AddToClassList(UiClasses.FlaskChip);
                chip.SetResolved(flask.IsCharge
                    ? Strings.Format(StringKeys.CombatFlasksCharge, new StringArgs().Add(UiPlaceholders.Name, flask.Name).Add(UiPlaceholders.Value, Int(flask.Current)).Add(UiPlaceholders.Max, Int(flask.Max)))
                    : Strings.Format(StringKeys.CombatFlasksCarried, new StringArgs().Add(UiPlaceholders.Name, flask.Name)));
                chip.EnableInClassList(UiClasses.CardUnaffordable, !flask.Usable);
                var captured = flask;
                chip.clicked += () => OnFlask(captured, chip);
                list.Add(chip);
            }
        }

        // ------------------------------------------------------------------ focus (US-5.9: kept across redraws)

        private string FocusIdentity()
        {
            var focused = Context.Instance.Focus.Focused;
            if (focused is KitCard card && _hand != null)
            {
                var i = _hand.Cards.ToList().IndexOf(card);
                if (i >= 0 && _view != null && i < _view.Hand.Count) return _view.Hand[i].InstanceId;
            }
            if (focused is Combatant c) return c.Data?.Id;
            return null;
        }

        private void RestoreFocus(string identity)
        {
            if (identity == null) return;
            var i = _view.Hand.FindIndex(c => c.InstanceId == identity);
            if (i >= 0 && i < _hand.Cards.Count && _hand.Cards[i].focusable) { _hand.Cards[i].Focus(); return; }
            if (_enemies.TryGetValue(identity, out var enemy) && enemy.focusable) enemy.Focus();
        }

        private VisualElement FirstPlayableCard()
        {
            var i = _view.Hand.FindIndex(c => c.Playable);
            return i >= 0 && i < _hand.Cards.Count ? _hand.Cards[i] : (VisualElement)_footer.EndTurnButton;
        }

        private void FocusSoon(VisualElement element)
        {
            if (element == null) return;
            Context.Instance.LastFocused = element;
            Nav.FocusTop(element);
        }

        // ------------------------------------------------------------------ intents → commands

        private void OnCard(int index, KitCard anchor)
        {
            if (index < 0 || index >= _view.Hand.Count || EnemyTurn || Ended) return;
            var card = _view.Hand[index];
            if (_mode == UiValues.FocusDiscard)
            {
                if (!_chosen.Remove(card.InstanceId)) _chosen.Add(card.InstanceId);
                Render();
                return;
            }
            if (_mode == UiValues.FocusFlasks) return;
            if (!card.Playable)
            {
                Refuse(anchor, CombatText.Refusal(Strings, card.Refusal));
                return;
            }
            if (card.NeedsTarget && card.Targets.Count > 0)
            {
                Arm(card, null, anchor);
                return;
            }
            Execute(CombatCommand.PlayCard(card.InstanceId), anchor);
        }

        private void Arm(AppCard card, FlaskView flask, VisualElement anchor)
        {
            _armedCard = card;
            _armedFlask = flask;
            _mode = UiValues.FocusTargeting;
            Render();
            var first = _view.Enemies.FirstOrDefault(e => e.Alive && IsLegalTarget(e.Id));
            if (first != null) FocusSoon(_enemies[first.Id]);
        }

        /// <summary>Leave targeting; focus returns to the card or flask control that armed it.</summary>
        public void Disarm()
        {
            var card = _armedCard;
            _armedCard = null;
            _armedFlask = null;
            _mode = null;
            Render();
            if (card != null)
            {
                var i = _view.Hand.FindIndex(c => c.InstanceId == card.InstanceId);
                FocusSoon(i >= 0 ? _hand.Cards[i] : FirstPlayableCard());
            }
            else FocusSoon(_footer.PotionsButton);
        }

        private void OnCombatant(Combatant target)
        {
            if (_mode != UiValues.FocusTargeting || target?.Data == null) return;
            if (!IsLegalTarget(target.Data.Id))
            {
                Refuse(target, CombatText.Refusal(Strings, new DomainRefusal(CombatStringKeys.CombatRefusalInvalidTarget)));
                return;
            }
            var card = _armedCard;
            var flask = _armedFlask;
            _armedCard = null;
            _armedFlask = null;
            _mode = null;
            if (card != null) Execute(CombatCommand.PlayCard(card.InstanceId, target.Data.Id), target);
            else if (flask != null) Execute(CombatCommand.UseFlask(flask.Slot, flask.ChargeKind, target.Data.Id), target);
            else Render();
        }

        private void OnEndTurn()
        {
            if (EnemyTurn) { Skip(); return; }
            if (Ended || _mode != null) return;
            var button = _footer.EndTurnButton;
            if (_view.EndTurnRefusal != null)
            {
                Refuse(button, CombatText.Refusal(Strings, _view.EndTurnRefusal));
                return;
            }
            if (_view.DiscardChoice)
            {
                _chosen.Clear();
                _mode = UiValues.FocusDiscard;
                Render();
                FocusSoon(_hand.Cards.FirstOrDefault() ?? (VisualElement)Root.Q(UiNames.DiscardConfirm));
                return;
            }
            Execute(CombatCommand.EndTurn(), button);
        }

        private void LeaveDiscard()
        {
            _chosen.Clear();
            _mode = null;
            Render();
            FocusSoon(_footer.EndTurnButton);
        }

        private void ConfirmDiscard(VisualElement anchor)
        {
            var chosen = _view.Hand.Where(c => _chosen.Contains(c.InstanceId)).Select(c => c.InstanceId).ToList();
            var outcome = TryExecute(CombatCommand.EndTurn(chosen));
            if (outcome == null) return;
            if (!outcome.Accepted)
            {
                Refuse(anchor, CombatText.Refusal(Strings, outcome.Refusal));
                return;
            }
            _chosen.Clear();
            _mode = null;
            StartEnemyTurn(outcome);
        }

        private void ToggleFlasks()
        {
            if (EnemyTurn || Ended) return;
            if (_mode == UiValues.FocusFlasks) { CloseFlasks(); return; }
            if (_mode != null) return;
            _mode = UiValues.FocusFlasks;
            Render();
            FocusSoon(Root.Q(UiNames.FlaskList)?.Children().FirstOrDefault());
        }

        private void CloseFlasks()
        {
            _mode = null;
            Render();
            FocusSoon(_footer.PotionsButton);
        }

        private void OnFlask(FlaskView flask, VisualElement anchor)
        {
            if (!flask.Usable)
            {
                Refuse(anchor, CombatText.Refusal(Strings, flask.Refusal));
                return;
            }
            _mode = null;
            if (flask.Targeted)
            {
                Arm(null, flask, anchor);
                return;
            }
            Execute(CombatCommand.UseFlask(flask.Slot, flask.ChargeKind), _footer.PotionsButton);
        }

        /// <summary>Runs a command through the session (legality, engine, autosave) and shows the outcome.</summary>
        private void Execute(CombatCommand command, VisualElement anchor)
        {
            var outcome = TryExecute(command);
            if (outcome == null) return;
            if (!outcome.Accepted)
            {
                Render();
                Refuse(anchor, CombatText.Refusal(Strings, outcome.Refusal));
                return;
            }
            if (command.Type == CombatValues.CommandEndTurn) { StartEnemyTurn(outcome); return; }
            Render();
            if (_session.Combat.IsOver) { EnterEnded(); return; }
            if (anchor is Combatant || !(anchor?.focusable ?? false) || anchor.panel == null) FocusSoon(FirstPlayableCard());
        }

        private CombatOutcome TryExecute(CombatCommand command)
        {
            try { return _session.Execute(command); }
            catch (Exception e)
            {
                Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.SaveFailed, e.Message));
                ConfirmRequest.Open(Nav, new ConfirmRequest { ConfirmId = ConfirmIds.SaveFailed, OnConfirm = () => TrySave() });
                Render();
                return null;
            }
        }

        private void TrySave()
        {
            try { _session.Save(); }
            catch (Exception e)
            {
                Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.SaveFailed, e.Message));
                ConfirmRequest.Open(Nav, new ConfirmRequest { ConfirmId = ConfirmIds.SaveFailed, OnConfirm = () => TrySave() });
            }
        }

        // ------------------------------------------------------------------ enemy turn (US-5.10)

        /// <summary>
        /// The enemy timeline: the engine has already resolved the turn; the screen walks the enemies that acted, in
        /// order, one step each (enemyStep, or enemyStepReduced with reduced motion), then shows the player's turn. Skip
        /// fast-forwards. The next intents are on screen before the player's turn begins.
        /// </summary>
        private void StartEnemyTurn(CombatOutcome outcome)
        {
            _mode = UiValues.FocusEnemyTurn;
            _actors = new Queue<string>(outcome.EnemyActors);
            _acting = null;
            Render();
            FocusSoon(_footer.EndTurnButton);
            Banner(StringKeys.CombatEnemyTurn, null, 0);
            var step = Ui.Data.Tokens.Duration(ReducedMotion ? TokenKeys.EnemyStepReduced : TokenKeys.EnemyStep);
            _timeline?.Pause();
            _timeline = Root.schedule.Execute(StepEnemyTurn).StartingIn(step).Every(step);
        }

        /// <summary>The live reducedMotion setting (profile, then preset, then data; D-150), not the run's frozen snapshot.</summary>
        private bool ReducedMotion => _session.PlayerSettings.On(SettingIds.ReducedMotion);

        private void StepEnemyTurn()
        {
            if (_actors.Count == 0)
            {
                FinishEnemyTurn();
                return;
            }
            _acting = _actors.Dequeue();
            var name = _view.Enemies.FirstOrDefault(e => e.Id == _acting)?.Name;
            Banner(StringKeys.CombatActing, name, 0);
            Render();
        }

        /// <summary>Skip ▶▶: end the timeline now.</summary>
        public void Skip()
        {
            if (!EnemyTurn) return;
            FinishEnemyTurn();
        }

        private void FinishEnemyTurn()
        {
            _timeline?.Pause();
            _timeline = null;
            _actors.Clear();
            _acting = null;
            _mode = null;
            Render();
            if (_session.Combat.IsOver) { EnterEnded(); return; }
            Banner(StringKeys.CombatYourTurn, null, Ui.Data.Tokens.Duration(TokenKeys.TurnBanner));
            FocusSoon(FirstPlayableCard());
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

        // ------------------------------------------------------------------ end state (victory → W-08 rewards; a death ends the run)

        private void EnterEnded()
        {
            _mode = UiValues.FocusEnded;
            Render();
            UiDom.Show(Root.Q(UiNames.PhaseBanner), false);
            var victory = _session.Combat.State.Result == CombatValues.Victory;
            var rewards = _session.HasPendingReward;
            Root.Q<LocLabel>(UiNames.EndTitle)?.SetResolved(Strings.Get(victory ? StringKeys.CombatEndVictory : StringKeys.CombatEndDefeat));
            Root.Q<LocLabel>(UiNames.EndBody)?.SetResolved(Strings.Format(_session.RunOver ? StringKeys.CombatEndDefeatBody : StringKeys.CombatEndBody,
                new StringArgs().Add(UiPlaceholders.Slot, _session.SlotIndex)));
            Root.Q<LocLabel>(UiNames.EndPlanned)?.SetResolved(Strings.Get(rewards ? StringKeys.CombatEndPlannedVictory : StringKeys.CombatEndPlanned));
            var rewardsButton = Root.Q<LocButton>(UiNames.EndRewards);
            var toTitle = Root.Q<LocButton>(UiNames.EndToTitle);
            var summary = Root.Q<LocButton>(UiNames.EndSummary);
            var summaryShown = _session.RunOver && Ui.Data.Screens.IsBuilt(ScreenIds.RunEnd);
            var nextBuilt = rewards ? Ui.Data.Screens.IsBuilt(ScreenIds.ActMap) : summaryShown;
            UiDom.Show(Root.Q(UiNames.EndPlanned), !nextBuilt);
            UiDom.Show(rewardsButton, rewards);
            UiDom.Show(summary, summaryShown);
            UiDom.Show(toTitle, !rewards && !summaryShown);
            rewardsButton?.EnableInClassList(UiClasses.ButtonPrimary, rewards);
            rewardsButton?.EnableInClassList(UiClasses.ButtonReady, rewards);
            summary?.EnableInClassList(UiClasses.ButtonPrimary, summaryShown);
            summary?.EnableInClassList(UiClasses.ButtonReady, summaryShown);
            toTitle?.EnableInClassList(UiClasses.ButtonPrimary, !rewards);
            toTitle?.EnableInClassList(UiClasses.ButtonReady, !rewards);
            UiDom.Show(Root.Q(UiNames.CombatEnd), true);
            FocusSoon(rewards && rewardsButton != null && rewardsButton.enabledSelf ? rewardsButton : summaryShown ? summary : (VisualElement)toTitle);
        }

        /// <summary>The spoils (W-08): the pending reward the fight's end rolled, on its own screen.</summary>
        private void OpenRewards()
        {
            if (!Ended || !_session.HasPendingReward) return;
            Nav.Go(ScreenIds.Rewards, new RewardsArgs { Session = _session }, true);
        }

        /// <summary>The run is over (a death, or the act-3 keeper felled): W-15 run end.</summary>
        private void OpenRunEnd()
        {
            if (!Ended || !_session.RunOver) return;
            Nav.Go(ScreenIds.RunEnd, new RunScreenArgs { Session = _session }, true);
        }

        // ------------------------------------------------------------------ pause and back

        private void OpenPause()
        {
            if (EnemyTurn) Skip();
            if (Ended) return;
            Nav.OpenModal(ScreenIds.Pause, new PauseArgs { Session = _session });
        }

        public override bool HandleBack()
        {
            switch (_mode)
            {
                case UiValues.FocusTargeting: Disarm(); return true;
                case UiValues.FocusFlasks: CloseFlasks(); return true;
                case UiValues.FocusDiscard: LeaveDiscard(); return true;
                case UiValues.FocusEnded: return true;
                case UiValues.FocusEnemyTurn: Skip(); return true;
            }
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
            if (_session?.Combat == null) return;
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

        // ------------------------------------------------------------------ test hooks (PlayMode smoke, captures)

        /// <summary>Arms a card for the review captures (targeting state) without playing it.</summary>
        public void ArmForReview(int handIndex)
        {
            if (handIndex < 0 || handIndex >= _view.Hand.Count) return;
            var card = _view.Hand[handIndex];
            if (card.NeedsTarget && card.Targets.Count > 0) Arm(card, null, _hand.Cards[handIndex]);
        }

        /// <summary>Opens the discard chooser for the review captures and selects the first cards.</summary>
        public void DiscardForReview(int chosen)
        {
            _mode = UiValues.FocusDiscard;
            _chosen.Clear();
            foreach (var c in _view.Hand.Take(chosen)) _chosen.Add(c.InstanceId);
            Render();
        }

        /// <summary>Shows a card's refusal at its CardView (review captures).</summary>
        public void RefuseForReview(int handIndex)
        {
            if (handIndex < 0 || handIndex >= _view.Hand.Count) return;
            var card = _view.Hand[handIndex];
            Refuse(_hand.Cards[handIndex], CombatText.Refusal(Strings, card.Refusal ?? new DomainRefusal(CombatStringKeys.CombatRefusalEnergy, card.Cost, _view.Player.Energy)));
        }

        /// <summary>Runs a command through the same path the controls use (the smoke test finishes a fight this way).</summary>
        public void Command(CombatCommand command) => Execute(command, _footer.EndTurnButton);

        /// <summary>Opens the flask panel (review captures).</summary>
        public void FlasksForReview() => ToggleFlasks();
    }
}
