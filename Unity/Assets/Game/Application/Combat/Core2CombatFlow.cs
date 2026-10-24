using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using DV = Ashen.Generated.Core2DamageValues;
using EV = Ashen.Generated.Core2EnemyValues;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.Core2ReactionKeys;
using RV = Ashen.Generated.Core2ReactionValues;
using UV = Ashen.Generated.Core2UiValues;

namespace Ashen.App.Combat
{
    /// <summary>One choice of an open prompt: what it shows (the typed row it came from) and the prebuilt intent it sends, or no intent when it opens the next step.</summary>
    public sealed class Core2PromptOption
    {
        public string Id;

        /// <summary>The intent this option sends, built by the view (null when the option only opens the next step).</summary>
        public JObject Intent;

        /// <summary>Why the option cannot be taken (the rules' words, or the view's reason), or null.</summary>
        public string Refusal;

        public bool Enabled => Refusal == null;

        public string Label;
        public Core2RouteView Route;
        public Core2RouteTargetView Odds;
        public Core2OfferOptionView Offer;
        public Core2FlaskView Flask;
        public Core2SlotView Slot;
        public Core2SetView Set;
        public Core2ChangeView Change;
        public Core2PickView Pick;
        public bool Selected;
    }

    /// <summary>A step the player has to answer before the fight goes on: a stance or route for a card, a reaction offer, a card choice, a flask, an equipment swap, a prediction.</summary>
    public sealed class Core2Prompt
    {
        /// <summary><see cref="Core2UiValues"/> prompt kind.</summary>
        public string Kind;

        public Core2CardView Card;
        public Core2FighterView Enemy;
        public Core2OfferView Offer;
        public Core2ChoiceView Choice;

        /// <summary>The set being re-armed (a change prompt).</summary>
        public Core2SlotView Slot;
        public Core2SetView Set;
        public List<Core2PromptOption> Options = new List<Core2PromptOption>();

        /// <summary>The cards a card choice wants.</summary>
        public int Count;

        /// <summary>A prompt the player may not leave without answering (an offer is declined with its own Back; a card choice must be answered).</summary>
        public bool Required;
    }

    /// <summary>What one press did: refused (nothing changed), opened or closed a step, or ran an intent.</summary>
    public sealed class Core2StepResult
    {
        /// <summary>The refusal to show at the control that was pressed, or null.</summary>
        public string Refusal;

        /// <summary>An intent went through the session (accepted or refused by the rules, see <see cref="Refusal"/>).</summary>
        public bool Ran;

        public Core2Outcome Outcome;

        /// <summary>The exception an intent threw (the session or a save failed): the fight is as it was, <see cref="Refusal"/> says so and the screen logs this.</summary>
        public Exception Error;

        /// <summary>The new log lines the intent produced, oldest first.</summary>
        public List<Core2LogLine> Timeline = new List<Core2LogLine>();

        /// <summary>The enemies that acted, in the order the lines name them.</summary>
        public List<string> Actors = new List<string>();

        public bool Accepted => Ran && Refusal == null;
    }

    /// <summary>
    /// The W-07 screen's logic for a Core 2 fight (docs/design/11 WP-C1b, D-311): which card is being played and what it still asks (a stance, a target, a route), the prompts (reaction offer,
    /// card choice, flasks, equipment, prediction), the end of the turn, and the pacing of an enemy turn. It builds nothing of its own: every button resolves to an intent the view
    /// (<see cref="Core2CombatView"/>) prebuilt, the run session (<see cref="Core2RunSession.Execute"/>) runs it, and a refusal is the rules' own. Engine-free, so the sequences a player
    /// takes are tested without the editor; the screen only draws <see cref="View"/>, <see cref="Prompt"/> and <see cref="Armed"/> and forwards presses.
    /// </summary>
    public sealed class Core2CombatFlow
    {
        private readonly Core2RunSession _run;
        private readonly StringTable _strings;
        private readonly HashSet<string> _picked = new HashSet<string>(StringComparer.Ordinal);
        private string _choice;
        private string _target;
        private string _route;
        private bool _routeDecided;
        private bool _wantsTarget;

        public Core2CombatFlow(Core2RunSession run, StringTable strings)
        {
            _run = run ?? throw new ArgumentNullException(nameof(run));
            _strings = strings;
            Refresh();
        }

        public Core2RunSession Run => _run;
        public Core2CombatSession Session => _run.Combat;

        /// <summary>The pacing and play dials from the player's settings (read fresh each time the view is built).</summary>
        public CombatPacing Pacing { get; private set; }

        public Core2CombatViewState View { get; private set; }

        /// <summary>The open prompt, or null.</summary>
        public Core2Prompt Prompt { get; private set; }

        /// <summary>The card being played (until it has everything it asks, or the player goes Back).</summary>
        public Core2CardView Armed { get; private set; }

        /// <summary>The flask being aimed.</summary>
        public Core2FlaskView ArmedFlask { get; private set; }

        /// <summary>The player is choosing an enemy for the armed card or flask (the battlefield takes the press).</summary>
        public bool Targeting => (Armed != null || ArmedFlask != null) && _wantsTarget && Prompt == null;

        public bool Ended => _run.FightFinished || View.IsOver;

        /// <summary>The enemies the armed card or flask may be aimed at now.</summary>
        public IReadOnlyCollection<string> LegalTargets
        {
            get
            {
                if (ArmedFlask != null) return ArmedFlask.Uses.Select(u => u.Str(RK.TargetId)).Where(t => t != null).ToList();
                if (Armed == null) return Array.Empty<string>();
                return PlaysNow().Select(p => p.Str(RK.TargetId)).Where(t => t != null).Distinct().ToList();
            }
        }

        /// <summary>The focus mode of the screens.json entry in force (null for plain play).</summary>
        public string Mode => Ended ? UiValues.FocusEnded : Prompt != null ? UV.FocusPrompt : Targeting ? UiValues.FocusTargeting : null;

        // ------------------------------------------------------------------ the view

        /// <summary>Rebuild the view from the committed fight and open the prompt the fight itself is waiting on (a reaction offer, a card choice).</summary>
        public void Refresh()
        {
            Pacing = CombatPacing.Read(_run.PlayerSettings);
            View = Core2CombatView.Build(_run.Combat, _strings);
            if (View.IsOver) { Reset(); return; }
            // The fight is paused: whatever the player had armed is moot, the offer or the choice comes first.
            if (View.Offer != null) { Reset(); Prompt = OfferPrompt(); return; }
            if (View.Choice != null)
            {
                if (Prompt?.Kind != UV.PromptCards) { _picked.Clear(); Armed = null; ArmedFlask = null; Prompt = CardsPrompt(); }
                else Prompt = CardsPrompt();
                return;
            }
            if (Prompt != null && (Prompt.Kind == UV.PromptOffer || Prompt.Kind == UV.PromptCards)) Prompt = null;
            // An armed card that is no longer in the hand is dropped; the rest of the prompt state is rebuilt from the new view.
            if (Armed != null)
            {
                Armed = View.Hand.FirstOrDefault(c => c.InstanceId == Armed.InstanceId);
                if (Armed == null) Reset();
            }
            if (Prompt != null) Prompt = Rebuild(Prompt);
        }

        private void Reset()
        {
            Armed = null;
            ArmedFlask = null;
            Prompt = null;
            _choice = null;
            _target = null;
            _route = null;
            _routeDecided = false;
            _wantsTarget = false;
            _picked.Clear();
        }

        private Core2Prompt Rebuild(Core2Prompt open)
        {
            switch (open.Kind)
            {
                case UV.PromptFlasks: return FlasksPrompt();
                case UV.PromptEquipment: return EquipmentPrompt();
                case UV.PromptPredict: return open.Enemy == null ? null : PredictPrompt(open.Enemy.Id);
                default: return open;
            }
        }

        // ------------------------------------------------------------------ playing a card

        private IEnumerable<JObject> PlaysNow() => Armed == null ? Enumerable.Empty<JObject>() : Armed.Plays.Where(Matches);

        private bool Matches(JObject play) =>
            (_choice == null || play.Str(RK.Choice) == _choice)
            && (_target == null || play.Str(RK.TargetId) == _target)
            && (!_routeDecided || play.Str(RK.RangeRoute) == _route);

        /// <summary>A card in the hand was pressed: an unplayable one refuses in the rules' words; a playable one asks what it still needs, or plays.</summary>
        public Core2StepResult SelectCard(int handIndex)
        {
            if (Ended || handIndex < 0 || handIndex >= View.Hand.Count) return new Core2StepResult();
            var card = View.Hand[handIndex];
            if (Prompt != null && Prompt.Kind != UV.PromptChoice && Prompt.Kind != UV.PromptRoute) return new Core2StepResult();
            if (!card.Playable) return new Core2StepResult { Refusal = card.Refusal ?? _strings?.Get(StringKeys.Core2RefusalUnplayable) };
            Reset();
            Armed = card;
            return Next();
        }

        /// <summary>What the armed card asks next: a stance, a target (on the battlefield), a route, or nothing, and it plays.</summary>
        private Core2StepResult Next()
        {
            var plays = PlaysNow().ToList();
            if (plays.Count == 0)
            {
                var why = Armed.Refusal;
                Reset();
                return new Core2StepResult { Refusal = why };
            }
            if (_choice == null)
            {
                var choices = plays.Select(p => p.Str(RK.Choice)).Where(c => c != null).Distinct().ToList();
                if (choices.Count == 1) _choice = choices[0];
                else if (choices.Count > 1)
                {
                    Prompt = ChoicePrompt(choices);
                    _wantsTarget = false;
                    return new Core2StepResult();
                }
                plays = PlaysNow().ToList();
            }
            if (_target == null && Armed.NeedsTarget)
            {
                var targets = plays.Select(p => p.Str(RK.TargetId)).Where(t => t != null).Distinct().ToList();
                if (targets.Count == 1 && Pacing != null && Pacing.OneTapPlay) _target = targets[0];
                else if (targets.Count >= 1)
                {
                    Prompt = null;
                    _wantsTarget = true;
                    return new Core2StepResult();
                }
                plays = PlaysNow().ToList();
            }
            if (!_routeDecided)
            {
                var routes = plays.Select(p => p.Str(RK.RangeRoute)).Distinct().ToList();
                if (routes.Count > 1)
                {
                    // Only a card aimed at an enemy has a route to choose. The choice is asked when it matters: the plain advance costs Stamina, cannot be taken or does not reach the
                    // target; otherwise the advance is the play (a throw from the hand's own reach is never the better odds).
                    var advance = Armed.NeedsTarget ? Armed.Routes.FirstOrDefault(r => r.Id == DV.RouteAdvance) : null;
                    var reach = advance?.Targets.FirstOrDefault(t => t.TargetId == _target);
                    var matters = Armed.NeedsTarget && (advance == null || !advance.Available || !advance.Affordable || advance.AdvanceSp > 0 || (reach != null && !reach.InRange) || !routes.Contains(null));
                    var plain = plays.FirstOrDefault(p => p.Str(RK.RangeRoute) == null);
                    if (matters && !(plain != null && Pacing != null && Pacing.OneTapPlay))
                    {
                        Prompt = RoutePrompt(plays);
                        _wantsTarget = false;
                        return new Core2StepResult();
                    }
                    return Execute(plain ?? plays[0]);
                }
            }
            return Execute(plays[0]);
        }

        /// <summary>The player picked a stance for the armed card.</summary>
        public Core2StepResult PickChoice(string choice)
        {
            if (Armed == null) return new Core2StepResult();
            _choice = choice;
            Prompt = null;
            return Next();
        }

        /// <summary>The player pressed an enemy while a card or flask was waiting for its target.</summary>
        public Core2StepResult PickTarget(string enemyId)
        {
            if (!Targeting) return new Core2StepResult();
            if (!LegalTargets.Contains(enemyId)) return new Core2StepResult { Refusal = _strings?.Get(StringKeys.Core2RefusalTarget) };
            if (ArmedFlask != null)
            {
                var use = ArmedFlask.Uses.FirstOrDefault(u => u.Str(RK.TargetId) == enemyId);
                return use == null ? new Core2StepResult { Refusal = ArmedFlask.Refusal } : Execute(use);
            }
            _target = enemyId;
            _wantsTarget = false;
            return Next();
        }

        /// <summary>The player picked a route (advance or throw) for the armed card.</summary>
        public Core2StepResult PickRoute(string routeId)
        {
            if (Armed == null) return new Core2StepResult();
            _route = routeId == DV.RouteAdvance ? null : routeId;
            _routeDecided = true;
            Prompt = null;
            return Next();
        }

        private Core2Prompt ChoicePrompt(List<string> choices)
        {
            var prompt = new Core2Prompt { Kind = UV.PromptChoice, Card = Armed };
            foreach (var choice in Armed.Choices)
                prompt.Options.Add(new Core2PromptOption
                {
                    Id = choice,
                    Label = Armed.ChoiceNames.TryGetValue(choice, out var name) ? name : choice,
                    Refusal = choices.Contains(choice) ? null : Armed.Refusal ?? _strings?.Get(StringKeys.Core2RefusalUnavailable),
                });
            return prompt;
        }

        private Core2Prompt RoutePrompt(List<JObject> plays)
        {
            var prompt = new Core2Prompt { Kind = UV.PromptRoute, Card = Armed, Enemy = View.Enemies.FirstOrDefault(e => e.Id == _target) };
            var offered = plays.Select(p => p.Str(RK.RangeRoute) ?? DV.RouteAdvance).Distinct().ToList();
            var ids = Armed.Routes.Select(r => r.Id).Concat(offered).Distinct().ToList();
            foreach (var id in ids)
            {
                var route = Armed.Routes.FirstOrDefault(r => r.Id == id);
                var play = plays.FirstOrDefault(p => (p.Str(RK.RangeRoute) ?? DV.RouteAdvance) == id);
                prompt.Options.Add(new Core2PromptOption
                {
                    Id = id,
                    Route = route,
                    Odds = route?.Targets.FirstOrDefault(t => t.TargetId == _target),
                    Intent = play,
                    Refusal = play != null ? null : route?.Reason ?? _strings?.Get(StringKeys.Core2RefusalUnavailable),
                });
            }
            return prompt;
        }

        // ------------------------------------------------------------------ prompts: offers, card choices, flasks, equipment, predictions

        private Core2Prompt OfferPrompt()
        {
            var prompt = new Core2Prompt { Kind = UV.PromptOffer, Offer = View.Offer, Required = true };
            foreach (var option in View.Offer.Options)
                prompt.Options.Add(new Core2PromptOption { Id = option.Id, Offer = option, Intent = option.Intent, Refusal = option.Legal ? null : option.Refusal ?? _strings?.Get(StringKeys.Core2RefusalUnavailable) });
            return prompt;
        }

        private Core2Prompt CardsPrompt()
        {
            var choice = View.Choice;
            _picked.IntersectWith(choice.Candidates.Select(c => c.InstanceId));
            var prompt = new Core2Prompt { Kind = UV.PromptCards, Choice = choice, Count = (int)choice.Count, Required = true };
            foreach (var pick in choice.Candidates)
                prompt.Options.Add(new Core2PromptOption { Id = pick.InstanceId, Pick = pick, Label = pick.Name, Selected = _picked.Contains(pick.InstanceId) });
            return prompt;
        }

        /// <summary>Toggle a candidate of the open card choice.</summary>
        public void TogglePick(string instanceId)
        {
            if (Prompt?.Kind != UV.PromptCards) return;
            if (!_picked.Remove(instanceId)) _picked.Add(instanceId);
            Prompt = CardsPrompt();
        }

        /// <summary>The cards picked so far, in the choice's order.</summary>
        public IReadOnlyList<string> PickedCards => Prompt?.Kind == UV.PromptCards ? Prompt.Options.Where(o => o.Selected).Select(o => o.Id).ToList() : new List<string>();

        /// <summary>Answer the card choice with the cards picked (the intent is the view's, built for exactly these cards; the rules refuse a wrong count).</summary>
        public Core2StepResult ConfirmPicks()
        {
            if (Prompt?.Kind != UV.PromptCards) return new Core2StepResult();
            return Execute(View.Choice.Select(PickedCards));
        }

        /// <summary>Open the flask panel: every charge pool and carried flask with the rules' refusal.</summary>
        public Core2StepResult OpenFlasks()
        {
            if (Ended || Prompt != null) return new Core2StepResult();
            Armed = null;
            _wantsTarget = false;
            Prompt = FlasksPrompt();
            return new Core2StepResult();
        }

        private Core2Prompt FlasksPrompt()
        {
            var prompt = new Core2Prompt { Kind = UV.PromptFlasks };
            foreach (var flask in View.Flasks)
                prompt.Options.Add(new Core2PromptOption { Id = flask.ChargeKind ?? flask.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture), Flask = flask, Label = flask.Name, Refusal = flask.Refusal });
            return prompt;
        }

        /// <summary>A flask was pressed in the panel: a plain one is drunk, an aimed one waits for its target on the battlefield.</summary>
        public Core2StepResult ChooseFlask(Core2PromptOption option)
        {
            var flask = option?.Flask;
            if (flask == null || Prompt?.Kind != UV.PromptFlasks) return new Core2StepResult();
            if (!flask.Usable) return new Core2StepResult { Refusal = flask.Refusal };
            if (flask.Targeted)
            {
                Prompt = null;
                ArmedFlask = flask;
                _wantsTarget = true;
                return new Core2StepResult();
            }
            return Execute(flask.Uses[0]);
        }

        /// <summary>Open the equipment panel: the hand slots with their sets, what a swap costs now and why it is refused.</summary>
        public Core2StepResult OpenEquipment()
        {
            if (Ended || Prompt != null) return new Core2StepResult();
            if (View.Equipment == null || View.Equipment.Slots.Count == 0) return new Core2StepResult { Refusal = _strings?.Get(StringKeys.Core2RefusalNoEquipment) };
            Armed = null;
            _wantsTarget = false;
            Prompt = EquipmentPrompt();
            return new Core2StepResult();
        }

        private Core2Prompt EquipmentPrompt()
        {
            var prompt = new Core2Prompt { Kind = UV.PromptEquipment };
            foreach (var slot in View.Equipment?.Slots ?? new List<Core2SlotView>())
                foreach (var set in slot.Sets)
                {
                    prompt.Options.Add(new Core2PromptOption { Id = slot.SlotId + set.SetIndex, Slot = slot, Set = set, Intent = set.Intent, Label = set.Name, Refusal = set.Active ? _strings?.Get(StringKeys.Core2RefusalActiveSet) : set.Refusal });
                    prompt.Options.Add(new Core2PromptOption { Id = UV.OptionChange, Slot = slot, Set = set, Label = set.Name });
                }
            return prompt;
        }

        /// <summary>The "change" control of a set: open the ways to re-arm it (an empty hand, each carried piece, each piece held in another cell) with price and refusal.</summary>
        public Core2StepResult OpenChanges(Core2PromptOption option)
        {
            if (option?.Slot == null || option.Set == null || Prompt?.Kind != UV.PromptEquipment) return new Core2StepResult();
            var prompt = new Core2Prompt { Kind = UV.PromptChange, Slot = option.Slot, Set = option.Set };
            foreach (var change in Core2CombatView.EquipmentChanges(Session, option.Slot.SlotId, option.Set.SetIndex))
                prompt.Options.Add(new Core2PromptOption { Id = change.PieceId ?? UV.OptionEmptyHand, Change = change, Intent = change.Intent, Label = change.Name, Refusal = change.Refusal });
            Prompt = prompt;
            return new Core2StepResult();
        }

        /// <summary>The player holds a control status the rules would let them shake off now (a recoverControl intent is legal).</summary>
        public bool CanRecover => !Ended && View.Legal.Any(l => l.Str(K.Type) == RV.IntentRecoverControl);

        /// <summary>Open the ways to recover from a held control status (one option per recoverControl intent the rules take).</summary>
        public Core2StepResult OpenRecover()
        {
            if (Ended || Prompt != null) return new Core2StepResult();
            var prompt = new Core2Prompt { Kind = UV.PromptRecover };
            foreach (var intent in View.Legal.Where(l => l.Str(K.Type) == RV.IntentRecoverControl))
            {
                var id = Js.Items(intent[RK.Selections]).OfType<JObject>().Select(x => x.Str(K.Status)).FirstOrDefault();
                var status = View.Player.Statuses.FirstOrDefault(x => x.Id == id);
                prompt.Options.Add(new Core2PromptOption { Id = id, Label = status?.Name ?? id, Intent = intent });
            }
            if (prompt.Options.Count == 0) return new Core2StepResult { Refusal = _strings?.Get(StringKeys.Core2RefusalRecover) };
            Armed = null;
            _wantsTarget = false;
            Prompt = prompt;
            return new Core2StepResult();
        }

        /// <summary>Open the prediction choices of an enemy whose move the player has not read (Perception).</summary>
        public Core2StepResult OpenPredict(string enemyId)
        {
            if (Ended || Prompt != null) return new Core2StepResult();
            var prompt = PredictPrompt(enemyId);
            if (prompt == null) return new Core2StepResult { Refusal = _strings?.Get(StringKeys.Core2RefusalPredict) };
            Armed = null;
            _wantsTarget = false;
            Prompt = prompt;
            return new Core2StepResult();
        }

        private Core2Prompt PredictPrompt(string enemyId)
        {
            var enemy = View.Enemies.FirstOrDefault(e => e.Id == enemyId);
            var options = View.Legal.Where(l => l.Str(K.Type) == EV.IntentPredictIntent && l.Str(Core2DamageKeys.EnemyInstanceId) == enemyId).ToList();
            if (enemy == null || options.Count == 0) return null;
            var prompt = new Core2Prompt { Kind = UV.PromptPredict, Enemy = enemy };
            foreach (var intent in options)
                prompt.Options.Add(new Core2PromptOption { Id = intent.Str(Core2CombatKeys.Maneuver), Label = intent.Str(Core2CombatKeys.Maneuver), Intent = intent });
            return prompt;
        }

        /// <summary>An option of an open prompt was pressed: one that carries an intent sends it; the stance and route steps go on with the card.</summary>
        public Core2StepResult Choose(Core2PromptOption option)
        {
            if (option == null || Prompt == null) return new Core2StepResult();
            if (!option.Enabled) return new Core2StepResult { Refusal = option.Refusal };
            switch (Prompt.Kind)
            {
                case UV.PromptChoice: return PickChoice(option.Id);
                case UV.PromptRoute: return option.Intent != null ? PickRoute(option.Id) : new Core2StepResult { Refusal = option.Refusal };
                case UV.PromptFlasks: return ChooseFlask(option);
                case UV.PromptCards: TogglePick(option.Id); return new Core2StepResult();
                case UV.PromptEquipment: return option.Id == UV.OptionChange ? OpenChanges(option) : Execute(option.Intent);
                default: return option.Intent != null ? Execute(option.Intent) : new Core2StepResult();
            }
        }

        // ------------------------------------------------------------------ the turn and the switches

        /// <summary>End the turn; the rules' refusal (a pending choice, no turn) shows at the End Turn control.</summary>
        public Core2StepResult EndTurn()
        {
            if (Ended || Prompt != null) return new Core2StepResult();
            if (View.EndTurnRefusal != null) return new Core2StepResult { Refusal = View.EndTurnRefusal };
            Reset();
            return Execute(View.EndTurn);
        }

        /// <summary>Flip the reaction switch (the next incoming attack asks, or does not).</summary>
        public Core2StepResult ToggleReactions()
        {
            if (Ended || Prompt != null) return new Core2StepResult();
            return Execute(View.ReactionsToggle);
        }

        /// <summary>Decline the open reaction offer.</summary>
        public Core2StepResult DeclineOffer() => Prompt?.Kind == UV.PromptOffer ? Execute(Prompt.Offer.Back) : new Core2StepResult();

        /// <summary>Go back one step: an aimed card or flask is put down, a prompt closes. A reaction offer is declined with its own Back and a card choice must be answered. True when something closed.</summary>
        public bool Back()
        {
            if (Prompt != null && Prompt.Required) return false;
            if (Prompt?.Kind == UV.PromptChange) { Prompt = EquipmentPrompt(); return true; }
            if (Prompt?.Kind == UV.PromptRoute && Armed != null && Armed.NeedsTarget)
            {
                // Route → target: the card keeps its stance and asks for the enemy again.
                _target = null;
                _routeDecided = false;
                _wantsTarget = true;
                Prompt = null;
                return true;
            }
            if (Prompt != null || Armed != null || ArmedFlask != null)
            {
                Reset();
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ running an intent

        /// <summary>Run an intent the view built through the run session (legality, autosave, the fight's end written back to the run), then rebuild the view.</summary>
        public Core2StepResult Execute(JObject intent)
        {
            Core2Outcome outcome;
            try { outcome = _run.Execute(intent); }
            catch (Exception e)
            {
                // A throwing dispatch must not leave the screen stale or silent: say so and rebuild from the fight as it stands.
                var failed = new Core2StepResult { Ran = true, Error = e, Refusal = _strings?.Get(StringKeys.Core2ErrorUnexpected) ?? e.Message };
                try { Reset(); Refresh(); } catch (Exception) { /* the view is rebuilt on the next press */ }
                return failed;
            }
            var result = new Core2StepResult { Ran = true, Outcome = outcome };
            if (!outcome.Accepted)
            {
                result.Refusal = outcome.Refusal?.Message;
                // A refused answer keeps the question open (an offer, a card choice with its picks); anything else the player had armed is put down.
                if (Prompt?.Kind != UV.PromptCards && Prompt?.Kind != UV.PromptOffer) Reset();
                Refresh();
                return result;
            }
            Reset();
            Refresh();
            if (outcome.Events.Count > 0)
            {
                result.Timeline = Core2CombatView.Log(Session, _strings, outcome.Events.Count);
                var enemies = new HashSet<string>(Session.State.Enemies.Select(e => e.Str(K.Id)), StringComparer.Ordinal);
                foreach (var line in result.Timeline)
                    if (line.SourceId != null && enemies.Contains(line.SourceId) && !result.Actors.Contains(line.SourceId)) result.Actors.Add(line.SourceId);
            }
            return result;
        }
    }
}
