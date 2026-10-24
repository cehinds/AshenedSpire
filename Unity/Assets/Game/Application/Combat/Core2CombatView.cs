using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using HK = Ashen.Generated.Core2HostKeys;
using HV = Ashen.Generated.Core2HostValues;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.Core2ReactionKeys;
using RV = Ashen.Generated.Core2ReactionValues;
using V = Ashen.Generated.CombatValues;

namespace Ashen.App.Combat
{
    /// <summary>A Poise or Ward pool: the value toward its maximum.</summary>
    public sealed class Core2MeterView
    {
        public double Value;
        public double Max;
    }

    /// <summary>A status on a fighter, display-ready (its row's name, icon and tint; stacks; the build-up meter and the duration when it has them).</summary>
    public sealed class Core2StatusView
    {
        public string Id;
        public string Name;
        public string Icon;
        public string Tint;
        public double Stacks;
        public double? Duration;
        public double? MeterValue;
        public double? MeterMax;
    }

    /// <summary>
    /// What the player knows of an enemy's next move (the observer's read, web <c>previewIntent</c> through the knowledge gate): <see cref="Known"/> when the read passed
    /// (every field below is the move's), otherwise the unknown mark with the label the player's own prediction gave it. Nothing of a hidden move leaks into a field.
    /// </summary>
    public sealed class Core2IntentView
    {
        public bool Known;

        /// <summary>The knowledge label: the action's category when read ('Attack', 'Defend', 'Staggered', ...), '?' while unread, the predicted category's clue after a prediction; null when the fight keeps no knowledge.</summary>
        public string Label;

        /// <summary>The numbered action the label belongs to (the serial a prediction names), or null.</summary>
        public double? ActionSerial;

        /// <summary>The player may predict this move now (it is unread, none was made yet, nothing waits on an answer): the maneuvers it may choose are <see cref="Core2CombatSession.PredictionChoices"/>.</summary>
        public bool Predictable;

        /// <summary>The maneuver the player predicted, and what became of it once the move resolved (null until then).</summary>
        public string Prediction;
        public bool? PredictionCorrect;

        public string Kind;
        public string MoveId;
        public string Camp;
        public string Maneuver;
        public string Range;
        public double? Damage;
        public double? Hits;
        public double? TotalDamage;
        public double? Block;
        public double? Barrier;
        public bool Pending;
        public bool Delayed;

        /// <summary>previewEnemyAction of the move (its effects and tags) when the move is known.</summary>
        public JObject Action;
    }

    /// <summary>One fighter as the HUD draws it: Evade, Poise, Ward, Block, HP, the stance (Staggered included) and the statuses with their stacks.</summary>
    public sealed class Core2FighterView
    {
        public string Id;
        public string DefId;
        public string Name;
        public double? Level;
        public bool IsPlayer;
        public bool Alive;
        public double Hp;
        public double MaxHp;
        public double Block;
        public double Barrier;
        public Core2MeterView Poise;
        public Core2MeterView Ward;

        /// <summary>The Evade and Precision stacks (the statuses of those ids).</summary>
        public double Evade;
        public double Precision;

        /// <summary>The combat stance: its maneuver ('attack', 'defend', 'smash', 'sweep', 'counter', 'evade', 'staggered'), camp and range; null when it stands in none.</summary>
        public string StanceManeuver;
        public string StanceName;
        public string StanceCamp;
        public string StanceRange;
        public bool Staggered;

        /// <summary>The armed Counter (its charges and the reply it holds), or null.</summary>
        public JObject Counter;

        public string FormationCell;
        public List<Core2StatusView> Statuses = new List<Core2StatusView>();

        /// <summary>The enemy's next move as the player may read it (null for the player and for the dead).</summary>
        public Core2IntentView Intent;

        // The player's pools (Actions is Stamina, bound to the turn).
        public double Stamina;
        public double MaxStamina;
        public double Mana;
        public double MaxMana;
        public bool ReactionsEnabled;
    }

    /// <summary>A range route (advance or throw) as the card face shows it: its price, whether it can be taken, and the odds against every target after it.</summary>
    public sealed class Core2RouteView
    {
        public string Id;
        public bool Available;
        public bool Affordable;
        public string Reason;
        public double AdvanceSp;
        public double PrecisionDelta;
        public double Energy;
        public double Mana;
        public List<Core2RouteTargetView> Targets = new List<Core2RouteTargetView>();
    }

    public sealed class Core2RouteTargetView
    {
        public string TargetId;
        public double Band;
        public bool InRange;
        public double? HitChance;
        public double? CurrentHitChance;
    }

    /// <summary>The hit a card would make on one target: the chance, the damage it would do, and the receipt behind that number.</summary>
    public sealed class Core2HitView
    {
        public string TargetId;
        public double? HitChance;
        public bool InRange = true;
        public double? Damage;
        public double? HitsCount;

        /// <summary>The damage receipt of the first damaging effect against this target (the sources, the mitigation and the HP loss), or null.</summary>
        public JObject Receipt;
    }

    /// <summary>A card in hand as the player sees it: costs, legality and the previews, all from the engine's own math.</summary>
    public sealed class Core2CardView
    {
        public string InstanceId;
        public string CardId;
        public string Name;
        public string Text;
        public string Type;
        public double? Rank;
        public bool Upgraded;
        public List<string> Tags = new List<string>();

        public double Stamina;
        public double Mana;
        public bool CostIsX;
        public JObject Costs;

        public bool NeedsTarget;

        /// <summary>The living enemies the card may be aimed at (empty when it needs no target).</summary>
        public List<string> Targets = new List<string>();

        /// <summary>The stance choices the card offers (empty when it has none).</summary>
        public List<string> Choices = new List<string>();

        /// <summary>Why the card cannot be played now (the rules' words), or null.</summary>
        public string Refusal;

        /// <summary>The rules would take at least one play of this card now (a target, a choice, a route).</summary>
        public bool Playable;

        /// <summary>Every play of the card the rules accept now, as the intent to send (the legal list's rows for this card).</summary>
        public List<JObject> Plays = new List<JObject>();

        public Dictionary<string, Core2HitView> Hits = new Dictionary<string, Core2HitView>();
        public List<Core2RouteView> Routes = new List<Core2RouteView>();
    }

    /// <summary>An option of a reaction offer: Play (the card and its price) for this option; the offer's Back is <see cref="Core2OfferView.Back"/>.</summary>
    public sealed class Core2OfferOptionView
    {
        public string Id;
        public string CardId;
        public string Name;
        public double Stamina;
        public double Mana;

        /// <summary>The rules accept this option now (it can be paid for).</summary>
        public bool Legal;

        public string Refusal;

        /// <summary>The intent that plays it.</summary>
        public JObject Intent;
    }

    /// <summary>A pending reaction offer: the fight is paused until the player plays one option or goes Back.</summary>
    public sealed class Core2OfferView
    {
        public string Id;
        public string SourceId;
        public string SourceName;
        public List<Core2OfferOptionView> Options = new List<Core2OfferOptionView>();

        /// <summary>The intent that declines the offer.</summary>
        public JObject Back;
        public JObject Raw;
    }

    /// <summary>A pending card choice (a card that asks for cards from a pile) or discard: the fight is paused until the player picks <see cref="Count"/> cards.</summary>
    public sealed class Core2ChoiceView
    {
        /// <summary>'chooseCards' or 'chooseDiscard' (the intent type that answers it).</summary>
        public string Intent;
        public double Count;
        public List<Core2PickView> Candidates = new List<Core2PickView>();

        /// <summary>The answers the rules take now (the first and the last selection the web offers), as intents.</summary>
        public List<JObject> Answers = new List<JObject>();

        /// <summary>The intent that answers with <paramref name="ids"/>.</summary>
        public JObject Select(IEnumerable<string> ids) => new JObject { [K.Type] = Intent, [RK.CardInstanceIds] = new JArray(ids) };
    }

    public sealed class Core2PickView
    {
        public string InstanceId;
        public string CardId;
        public string Name;
    }

    /// <summary>A flask the player can drink: a charge pool (by kind) or a carried flask (by slot).</summary>
    public sealed class Core2FlaskView
    {
        /// <summary>The carried flask's slot, or -1 for a charge pool.</summary>
        public int Slot = -1;
        public string ChargeKind;
        public string FlaskId;
        public string Name;
        public double Current;
        public double Max;
        public bool Targeted;
        public string Refusal;
        public bool Usable => Refusal == null;

        /// <summary>The intents that drink it (one per living enemy when it is aimed).</summary>
        public List<JObject> Uses = new List<JObject>();
    }

    /// <summary>One prepared set of a hand slot: what it holds, whether it is the active one, and what cycling to it costs now.</summary>
    public sealed class Core2SetView
    {
        public int SetIndex;
        public string ItemId;
        public string Name;
        public bool Active;

        /// <summary>The Stamina the swap would cost now (null where the rules refuse first).</summary>
        public double? Cost;
        public string Refusal;
        public bool Swappable => Refusal == null && !Active;
        public JObject Intent;
    }

    public sealed class Core2SlotView
    {
        public string SlotId;
        public string Label;
        public string Hand;
        public List<Core2SetView> Sets = new List<Core2SetView>();
    }

    /// <summary>A way to re-arm one position (a carried piece, or an empty hand when <see cref="PieceId"/> is null), priced and judged.</summary>
    public sealed class Core2ChangeView
    {
        public string SlotId;
        public int SetIndex;
        public string PieceId;
        public string Name;
        public double? Cost;
        public string Refusal;
        public bool Allowed => Refusal == null;
        public JObject Intent;
    }

    /// <summary>The mid-fight equipment swap as data: the hand slots with their sets and prices, and the carried pieces.</summary>
    public sealed class Core2EquipmentView
    {
        public List<Core2SlotView> Slots = new List<Core2SlotView>();
        public List<string> Carried = new List<string>();
    }

    /// <summary>A line of the combat log: the event's type, who and what it names, and its text when the string table has one.</summary>
    public sealed class Core2LogLine
    {
        public int Index;
        public string Type;
        public double Turn;
        public string SourceId;
        public string TargetId;
        public string SourceName;
        public string TargetName;
        public double? Amount;
        public string Detail;
        public JObject Event;

        /// <summary>The string key of the event's sentence (combat.log.&lt;type&gt;) and the filled sentence, null when the table has no such line.</summary>
        public string StringKey;
        public string Text;
    }

    /// <summary>Everything the Core 2 combat screen (W-07) draws, derived from the committed fight (views never call Domain directly).</summary>
    public sealed class Core2CombatViewState
    {
        public double Turn;
        public string Phase;
        public string Result;
        public bool IsOver;
        public Core2FighterView Player;
        public List<Core2FighterView> Enemies = new List<Core2FighterView>();
        public List<Core2CardView> Hand = new List<Core2CardView>();
        public List<Core2FlaskView> Flasks = new List<Core2FlaskView>();
        public int DrawCount;
        public int DiscardCount;
        public int ExhaustCount;

        /// <summary>Why the turn cannot end now (the rules' words), or null.</summary>
        public string EndTurnRefusal;
        public JObject EndTurn;

        /// <summary>The reaction switch: whether the player's reactions are on, and the intent that flips it.</summary>
        public bool ReactionsEnabled;
        public JObject ReactionsToggle;

        public Core2OfferView Offer;
        public Core2ChoiceView Choice;
        public Core2EquipmentView Equipment;
        public List<Core2LogLine> Log = new List<Core2LogLine>();

        /// <summary>Every intent that is legal now, in the web's order.</summary>
        public IReadOnlyList<JObject> Legal = new JObject[0];
    }

    /// <summary>
    /// Builds the display-ready Core 2 combat view (the 405 <see cref="CombatViewModel"/> pattern, docs/design/11 WP-C1a, D-303): strings come from the content rows (names, text templates,
    /// the string table's log lines), numbers and legality from the session (the previews and the rules' own refusals), never recomputed here.
    /// </summary>
    public static class Core2CombatView
    {
        private static T Try<T>(Func<T> read) where T : class
        {
            try { return read(); }
            catch (InvalidOperationException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        /// <summary>The view of the fight as it stands. <paramref name="strings"/> fills the log's sentences; <paramref name="logLines"/> keeps the newest lines.</summary>
        public static Core2CombatViewState Build(Core2CombatSession session, StringTable strings = null, int logLines = (int)Ashen.Generated.Core2HostNumbers.LogLines, JObject presentation = null)
        {
            var c = session.State;
            var legal = session.IsOver ? new List<JObject>() : session.LegalIntents(presentation).ToList();
            var view = new Core2CombatViewState
            {
                Turn = c.Turn,
                Phase = c.Phase,
                Result = c.Result,
                IsOver = session.IsOver,
                DrawCount = c.Piles.Draw.Count,
                DiscardCount = c.Piles.Discard.Count,
                ExhaustCount = c.Piles.Exhaust.Count,
                Legal = legal,
                Player = Fighter(c, c.Player, true),
            };
            foreach (var enemy in c.Enemies) view.Enemies.Add(Fighter(c, enemy, false));
            // A prediction is offered exactly when the rules would take one now (an unread move, none made yet, nothing waiting on an answer).
            foreach (var enemy in view.Enemies.Where(e => e.Intent != null))
                enemy.Intent.Predictable = legal.Any(l => l.Str(K.Type) == Ashen.Generated.Core2EnemyValues.IntentPredictIntent && l.Str(Ashen.Generated.Core2DamageKeys.EnemyInstanceId) == enemy.Id);
            view.ReactionsEnabled = view.Player.ReactionsEnabled;
            view.ReactionsToggle = Intent(Ashen.Generated.Core2ReactionValues.IntentSetReactions, RK.Enabled, !view.ReactionsEnabled);
            view.EndTurn = Intent(RV.IntentEndTurn, RK.DiscardIds, new JArray());
            view.EndTurnRefusal = session.Why(view.EndTurn);
            view.Offer = Offer(session, legal);
            view.Choice = Choice(session, legal);
            foreach (var inst in c.Piles.Hand) view.Hand.Add(Card(session, inst, legal));
            view.Flasks = Flasks(session, legal);
            view.Equipment = Equipment(session);
            view.Log = Log(session, strings, logLines);
            return view;
        }

        private static JObject Intent(string type, params object[] pairs)
        {
            var o = new JObject { [K.Type] = type };
            for (var i = 0; i + 1 < pairs.Length; i += (int)Ashen.Generated.Core2SnapshotNumbers.PairLength) o[(string)pairs[i]] = pairs[i + 1] is JToken token ? token.DeepClone() : Js.Token(pairs[i + 1]);
            return o;
        }

        // ------------------------------------------------------------------ the fighters

        private static List<Core2StatusView> Statuses(CombatState c, JObject entity)
        {
            var list = new List<Core2StatusView>();
            foreach (var p in (entity.Obj(K.Statuses) ?? new JObject()).Properties())
            {
                if (!(p.Value is JObject inst) || Ashen.Domain.Combat.Statuses.Stacks(entity, p.Name) <= 0 && inst.Obj(K.Meter) == null) continue;
                var def = c.Data.Statuses.Has(p.Name) ? c.Data.Statuses.Get(p.Name) : new JObject();
                var meter = inst.Obj(K.Meter);
                list.Add(new Core2StatusView
                {
                    Id = p.Name,
                    Name = def.Str(K.Name) ?? p.Name,
                    Icon = def.Str(K.Icon),
                    Tint = def.Str(K.Tint),
                    Stacks = Ashen.Domain.Combat.Statuses.Stacks(entity, p.Name),
                    Duration = Js.IsNum(inst[K.Duration]) ? inst.Num(K.Duration) : (double?)null,
                    MeterValue = meter != null ? meter.Num(K.Value) : (double?)null,
                    MeterMax = meter != null ? meter.Num(K.Max) : (double?)null,
                });
            }
            return list;
        }

        private static Core2MeterView Meter(JObject entity, string key)
        {
            var m = entity.Obj(key);
            return m == null ? null : new Core2MeterView { Value = m.Num(K.Value), Max = m.Num(K.Max) };
        }

        private static Core2FighterView Fighter(CombatState c, JObject entity, bool player)
        {
            var defId = player ? entity.Str(K.ClassId) : entity.Str(K.EnemyId);
            var registry = player ? c.Data.Classes : c.Data.Enemies;
            var stance = entity.Obj(CK.CombatStance);
            var maneuver = stance?.Str(CK.Maneuver);
            var f = new Core2FighterView
            {
                Id = entity.Str(K.Id),
                DefId = defId,
                IsPlayer = player,
                Name = defId != null && registry.Has(defId) ? registry.Get(defId).Str(K.Name) : defId,
                Level = Js.IsNum(entity[K.Level]) ? entity.Num(K.Level) : (double?)null,
                Alive = entity.Is(K.Alive),
                Hp = entity.Num(K.Hp),
                MaxHp = entity.Num(K.MaxHp),
                Block = entity.Num(K.Block),
                Barrier = entity.Num(CK.Barrier),
                Poise = Meter(entity, CK.PersistentPoise),
                Ward = Meter(entity, CK.PersistentWard),
                Evade = Ashen.Domain.Combat.Statuses.Stacks(entity, CV.StatusEvade),
                Precision = Ashen.Domain.Combat.Statuses.Stacks(entity, CV.StatusPrecision),
                StanceManeuver = maneuver,
                StanceName = maneuver != null && c.Data.Stances.Has(maneuver) ? c.Data.Stances.Get(maneuver).Str(K.Name) : maneuver,
                StanceCamp = stance?.Str(CK.Camp),
                StanceRange = stance?.Str(CK.Range),
                Staggered = stance != null && stance.Is(DK.Staggered),
                Counter = entity.Obj(CK.CombatCounter)?.DeepClone() as JObject,
                FormationCell = entity.Str(CK.FormationCell),
                Statuses = Statuses(c, entity),
                Stamina = entity.Num(K.Stamina),
                MaxStamina = entity.Num(K.MaxStamina),
                Mana = entity.Num(K.Mana),
                MaxMana = entity.Num(K.MaxMana),
                // An entity that never switched its reactions has none recorded: they are on (the web reads `reactionsEnabled !== false`).
                ReactionsEnabled = player && !(entity[Ashen.Generated.Core2SnapshotKeys.ReactionsEnabled] != null && !entity.Is(Ashen.Generated.Core2SnapshotKeys.ReactionsEnabled)),
            };
            if (!player && f.Alive) f.Intent = ReadIntent(c, entity);
            return f;
        }

        /// <summary>The observer's read of an enemy's move: the move itself once the read passed, otherwise only what the knowledge ledger shows (the label and the prediction).</summary>
        private static Core2IntentView ReadIntent(CombatState c, JObject enemy)
        {
            var action = enemy.Obj(CK.KnowledgeAction);
            var read = action?.Obj(CK.Reads)?.Obj(V.Player);
            var tracked = c.Fields.Obj(CK.EnemyKnowledge) != null;
            var exact = !tracked || CoreVisibility.IntentExact(c, enemy);
            var view = new Core2IntentView
            {
                Known = exact,
                Label = Js.Truthy(read?[CK.Label]) ? read.Str(CK.Label) : exact && tracked ? action?.Str(CK.Category) : null,
                ActionSerial = Js.IsNum(action?[CK.Serial]) ? action.Num(CK.Serial) : (double?)null,
                Predictable = read != null && read.Str(CK.Visibility) == CV.VisibilityUnknown && read[CK.Prediction]?.Type == JTokenType.Null,
                Prediction = Js.Truthy(read?[CK.Prediction]) ? read.Str(CK.Prediction) : null,
                PredictionCorrect = read != null && read[CK.Correct]?.Type == JTokenType.Boolean ? read.Is(CK.Correct) : (bool?)null,
            };
            if (!exact) return view;
            var intent = enemy.Obj(K.Intent);
            if (intent == null) return view;
            var profile = intent.Obj(CK.CombatProfile);
            view.Kind = intent.Str(K.Kind);
            view.MoveId = intent.Str(K.MoveId);
            view.Camp = profile?.Str(CK.Camp);
            view.Maneuver = profile?.Str(CK.Maneuver);
            view.Range = profile?.Str(CK.Range);
            view.Damage = Js.IsNum(intent[K.Damage]) ? intent.Num(K.Damage) : (double?)null;
            view.Hits = Js.IsNum(intent[K.Hits]) ? intent.Num(K.Hits) : (double?)null;
            view.TotalDamage = view.Damage.HasValue ? view.Damage.Value * (view.Hits ?? 1) : (double?)null;
            view.Block = Js.IsNum(intent[K.Block]) ? intent.Num(K.Block) : (double?)null;
            view.Barrier = Js.IsNum(intent[CK.Barrier]) ? intent.Num(CK.Barrier) : (double?)null;
            view.Pending = enemy[K.PendingMove] != null && enemy[K.PendingMove].Type != JTokenType.Null;
            view.Delayed = intent.Is(K.Delayed);
            if (view.MoveId != null) view.Action = Try(() => CoreEnemyPreview.Preview(c, enemy, view.MoveId));
            return view;
        }

        // ------------------------------------------------------------------ the offer and the choice

        private static string CardName(CombatState c, string cardId) => cardId != null && c.Data.Cards.Has(cardId) ? c.Data.Cards.Get(cardId).Str(K.Name) : cardId;

        private static Core2OfferView Offer(Core2CombatSession session, IReadOnlyList<JObject> legal)
        {
            var offer = session.PendingOffer();
            if (offer == null) return null;
            var c = session.State;
            var source = c.Enemies.FirstOrDefault(e => e.Str(K.Id) == offer.Str(K.SourceId));
            var view = new Core2OfferView
            {
                Id = offer.Str(K.Id),
                SourceId = offer.Str(K.SourceId),
                SourceName = source != null && c.Data.Enemies.Has(source.Str(K.EnemyId)) ? c.Data.Enemies.Get(source.Str(K.EnemyId)).Str(K.Name) : offer.Str(K.SourceId),
                Back = Intent(RV.IntentChooseReaction, RK.OfferId, offer[K.Id], RK.OptionId, Js.Null()),
                Raw = offer,
            };
            foreach (var option in Js.Items(offer[RK.Options]).OfType<JObject>())
            {
                var intent = Intent(RV.IntentChooseReaction, RK.OfferId, offer[K.Id], RK.OptionId, option[K.Id]);
                view.Options.Add(new Core2OfferOptionView
                {
                    Id = option.Str(K.Id),
                    CardId = option.Str(K.CardId),
                    Name = CardName(c, option.Str(K.CardId)),
                    Stamina = option.Num(CK.StaminaCost),
                    Mana = option.Num(CK.ManaCost),
                    Legal = legal.Any(l => JToken.DeepEquals(l, intent)),
                    Refusal = session.Why(intent),
                    Intent = intent,
                });
            }
            return view;
        }

        private static Core2ChoiceView Choice(Core2CombatSession session, IReadOnlyList<JObject> legal)
        {
            var pending = session.PendingChoice();
            if (pending == null) return null;
            var c = session.State;
            var cards = c.Fields.Obj(Ashen.Generated.Core2StatusKeys.PendingCardChoice) != null;
            var view = new Core2ChoiceView { Intent = cards ? RV.IntentChooseCards : RV.IntentChooseDiscard, Count = pending.Num(K.Count) };
            var ids = cards ? Js.Items(pending[Ashen.Generated.Core2StatusKeys.CandidateIds]).Select(Js.Str).ToList() : c.Piles.Hand.Select(h => h.Str(K.InstanceId)).ToList();
            var pool = c.Piles.Hand.Concat(c.Piles.Draw).Concat(c.Piles.Discard).Concat(c.Piles.Exhaust).ToList();
            foreach (var id in ids)
            {
                var inst = pool.FirstOrDefault(p => p.Str(K.InstanceId) == id);
                view.Candidates.Add(new Core2PickView { InstanceId = id, CardId = inst?.Str(K.CardId), Name = inst != null ? Try(() => CoreCosts.Resolve(c, inst))?.Str(K.Name) ?? CardName(c, inst.Str(K.CardId)) : id });
            }
            view.Answers = legal.Where(l => l.Str(K.Type) == view.Intent).ToList();
            return view;
        }

        // ------------------------------------------------------------------ the hand

        private static Core2CardView Card(Core2CombatSession session, JObject inst, IReadOnlyList<JObject> legal)
        {
            var c = session.State;
            var id = inst.Str(K.InstanceId);
            var printed = Try(() => CoreCosts.Resolve(c, inst)) ?? new JObject();
            var living = c.Enemies.Where(e => e.Is(K.Alive)).Select(e => e.Str(K.Id)).ToList();
            var needsTarget = Try(() => (object)CoreTargets.CardNeedsEnemyTargetNow(c, id)) is bool b && b;
            var first = needsTarget ? living.FirstOrDefault() : null;
            var costs = Try(() => session.CardCosts(id));
            var def = Try(() => CoreVariants.Select(c, printed, costs ?? new JObject(), first != null ? c.Enemies.FirstOrDefault(e => e.Str(K.Id) == first) : null)) ?? printed;
            var range = Try(() => session.PreviewCard(id, first));
            var card = new Core2CardView
            {
                InstanceId = id,
                CardId = inst.Str(K.CardId),
                Name = def.Str(K.Name) ?? CardName(c, inst.Str(K.CardId)),
                Type = def.Str(K.Type),
                Rank = Js.IsNum(def[CK.Rank]) ? def.Num(CK.Rank) : (double?)null,
                Upgraded = inst.Is(K.Upgraded),
                Stamina = costs?.Num(K.Stamina) ?? 0,
                Mana = costs?.Num(K.Mana) ?? 0,
                CostIsX = Js.IsStr(def[K.Cost]) && def.Str(K.Cost) == V.XCost,
                Costs = costs,
                NeedsTarget = needsTarget,
                Targets = needsTarget ? living : new List<string>(),
                Refusal = Try(() => session.PlayRefusal(id)),
            };
            foreach (var tag in Js.Items(def[CK.CardTags] ?? def[K.Tags])) card.Tags.Add(Js.IsStr(tag) ? Js.Str(tag) : Js.Str(Js.Get(tag, K.Id)));
            card.Choices = Try(() => CoreCardPlay.ChoiceOptions(def)) ?? new List<string>();
            card.Plays = legal.Where(l => l.Str(RK.CardInstanceId) == id && l.Str(K.Type) == RV.IntentPlayCard).ToList();
            card.Playable = card.Plays.Count > 0;

            // The damage the card's first damaging effect would do to each target, and the odds, as the engine's own receipt and hit preview say.
            var damage = Js.Items(def[K.Effects]).OfType<JObject>().FirstOrDefault(e => e.Str(K.Op) == Ashen.Generated.CombatOps.Damage && Js.IsNum(e[K.Amount]));
            var carrier = damage == null ? null : Try(() => CoreCardPlay.Carrier(c, inst, def));
            foreach (var target in living)
            {
                if (!needsTarget) break;
                var enemy = c.Enemies.First(e => e.Str(K.Id) == target);
                var hitRange = Try(() => session.PreviewHit(id, target));
                var row = Js.Items(hitRange?[HK.Targets]).OfType<JObject>().FirstOrDefault(t => t.Str(K.TargetId) == target);
                var hv = new Core2HitView
                {
                    TargetId = target,
                    HitChance = Js.IsNum(row?[HK.CurrentHitChance]) ? row.Num(HK.CurrentHitChance) : Js.IsNum(row?[HK.HitChance]) ? row.Num(HK.HitChance) : (double?)null,
                    InRange = row == null || !(row[HK.CurrentInRange]?.Type == JTokenType.Boolean) || row.Is(HK.CurrentInRange),
                };
                if (damage != null && carrier != null)
                {
                    var receipt = Try(() => CoreDamage.ProfileDamageReceipt(c, c.Player, enemy, damage.Num(K.Amount), carrier));
                    if (receipt != null)
                    {
                        hv.Damage = receipt.Num(K.Amount);
                        hv.HitsCount = Js.IsNum(damage[K.Hits]) ? damage.Num(K.Hits) : 1;
                        hv.Receipt = receipt.Obj(DK.DamageReceipt)?.DeepClone() as JObject;
                    }
                }
                card.Hits[target] = hv;
            }
            foreach (var route in Js.Items(range?[HK.Routes]).OfType<JObject>()) card.Routes.Add(Route(route));
            card.Text = Text(c, def, card);
            return card;
        }

        /// <summary>The card's text template with its numbers: each tokenizable value as authored, the damage token as the first target's receipt says.</summary>
        private static string Text(CombatState c, JObject def, Core2CardView card)
        {
            var tokens = new JObject();
            var effects = Js.Items(def[K.Effects]).ToList();
            foreach (var binding in CombatPreview.TokenBindings(c.Data, def[K.Effects]))
            {
                var split = binding.Key.Split(V.KeySeparator[0]);
                var index = int.Parse(split[0], CultureInfo.InvariantCulture);
                var value = (effects[index] as JObject)?[split[1]];
                if (Js.IsNum(value)) tokens[binding.Value] = value.DeepClone();
            }
            var shown = card.Hits.Values.FirstOrDefault(h => h.Damage.HasValue);
            if (shown != null && tokens[Ashen.Generated.CombatOps.Damage] != null) tokens[Ashen.Generated.CombatOps.Damage] = Js.N(shown.Damage.Value);
            return CombatViewModel.ResolveText(def.Str(K.TextTemplate), tokens);
        }

        private static Core2RouteView Route(JObject route)
        {
            var view = new Core2RouteView
            {
                Id = route.Str(Ashen.Generated.Core2RangeKeys.Id),
                Available = route.Is(HK.Available),
                Affordable = route.Is(HK.Affordable),
                Reason = Js.Truthy(route[K.Reason]) ? route.Str(K.Reason) : null,
                AdvanceSp = route.Num(HK.AdvanceSp),
                PrecisionDelta = route.Num(HK.PrecisionDelta),
                Energy = route.Obj(HK.Costs)?.Num(K.Energy) ?? 0,
                Mana = route.Obj(HK.Costs)?.Num(K.Mana) ?? 0,
            };
            foreach (var t in Js.Items(route[HK.Targets]).OfType<JObject>())
                view.Targets.Add(new Core2RouteTargetView
                {
                    TargetId = t.Str(K.TargetId),
                    Band = t.Num(HK.Band),
                    InRange = t.Is(HK.InRange),
                    HitChance = Js.IsNum(t[HK.HitChance]) ? t.Num(HK.HitChance) : (double?)null,
                    CurrentHitChance = Js.IsNum(t[HK.CurrentHitChance]) ? t.Num(HK.CurrentHitChance) : (double?)null,
                });
            return view;
        }

        // ------------------------------------------------------------------ flasks and equipment

        private static List<Core2FlaskView> Flasks(Core2CombatSession session, IReadOnlyList<JObject> legal)
        {
            var c = session.State;
            var list = new List<Core2FlaskView>();
            var living = c.Enemies.Where(e => e.Is(K.Alive)).Select(e => e.Str(K.Id)).ToList();
            List<JObject> Uses(JObject proto, bool targeted)
            {
                var uses = new List<JObject>();
                foreach (var target in targeted ? living : new List<string> { null })
                {
                    var use = (JObject)proto.DeepClone();
                    if (target != null) use[RK.TargetId] = target;
                    uses.Add(use);
                }
                return uses;
            }
            var charges = c.Player.Obj(K.FlaskCharges);
            foreach (var kind in Js.Items(c.Data.Engine.Obj(K.Flasks)?[K.ChargeKinds]).Select(Js.Str).Where(k => k != null))
            {
                var flaskId = CombatEngine.ChargeFlaskId(c, kind);
                if (flaskId == null || charges == null) continue;
                var def = c.Data.Flasks.Get(flaskId);
                var use = Intent(V.CommandUseFlask, K.ChargeKind, kind);
                list.Add(new Core2FlaskView
                {
                    ChargeKind = kind, FlaskId = flaskId, Name = def.Str(K.Name), Current = charges.Num(kind + V.CurrentSuffix), Max = charges.Num(kind), Targeted = def.Is(K.Targeted),
                    Refusal = session.Why(Uses(use, def.Is(K.Targeted)).First()), Uses = Uses(use, def.Is(K.Targeted)),
                });
            }
            var carried = c.Player.Arr(K.Flasks) ?? new JArray();
            for (var i = 0; i < carried.Count; i++)
            {
                if (!(carried[i] is JObject slot) || slot.Str(K.FlaskId) == null) continue;
                var def = c.Data.Flasks.Get(slot.Str(K.FlaskId));
                var use = Intent(V.CommandUseFlask, K.Slot, i);
                var uses = Uses(use, def.Is(K.Targeted));
                list.Add(new Core2FlaskView
                {
                    Slot = i, FlaskId = slot.Str(K.FlaskId), Name = def.Str(K.Name), Current = 1, Max = 1, Targeted = def.Is(K.Targeted),
                    Refusal = uses.Count == 0 ? Ashen.Generated.CombatMessages.CombatOver : session.Why(uses[0]), Uses = uses,
                });
            }
            return list;
        }

        private static string PieceName(CombatState c, string itemId) =>
            itemId == null ? null : Js.Items(c.Data.Equipment[K.Armaments]).OfType<JObject>().FirstOrDefault(a => a.Str(K.Id) == itemId)?.Str(K.Name) ?? itemId;

        private static IEnumerable<JObject> HandSlots(CombatState c) =>
            Js.Items(c.Data.Equipment[K.Slots]).OfType<JObject>().Where(s => s.Str(K.Hand) == V.Right || s.Str(K.Hand) == V.Left);

        /// <summary>What an intent would cost in Stamina, read off a private copy of the fight (null when the rules refuse it).</summary>
        private static double? PriceOf(Core2CombatSession session, JObject intent)
        {
            var copy = CoreTransaction.DetachPaused(session.State);
            var before = copy.Player.Num(K.Stamina);
            try { CoreDispatch.Dispatch(copy, (JObject)intent.DeepClone()); }
            catch (InvalidOperationException) { return null; }
            catch (NotSupportedException) { return null; }
            return before - copy.Player.Num(K.Stamina);
        }

        private static Core2EquipmentView Equipment(Core2CombatSession session)
        {
            var c = session.State;
            if (c.Loadout == null) return null;
            var view = new Core2EquipmentView();
            foreach (var slot in HandSlots(c))
            {
                var slotId = slot.Str(K.Id);
                var cells = c.Loadout.Obj(K.Sets)?.Arr(slotId) ?? new JArray();
                var active = (int)Js.Or0(c.Loadout.Obj(K.Active)?[slotId]);
                var sv = new Core2SlotView { SlotId = slotId, Label = slot.Str(K.Label), Hand = slot.Str(K.Hand) };
                for (var i = 0; i < cells.Count; i++)
                {
                    var intent = Intent(V.CommandSwapArmament, K.SlotId, slotId, K.SetIndex, i);
                    var why = session.Why(intent);
                    sv.Sets.Add(new Core2SetView { SetIndex = i, ItemId = Js.Str(cells[i]), Name = PieceName(c, Js.Str(cells[i])), Active = i == active, Cost = why == null ? PriceOf(session, intent) : null, Refusal = why, Intent = intent });
                }
                view.Slots.Add(sv);
            }
            foreach (var id in Js.Items(c.Loadout[Ashen.Generated.RunKeys.Storage]).Select(Js.Str).Where(id => id != null)) view.Carried.Add(id);
            return view;
        }

        /// <summary>Every way to re-arm one position now: an empty hand, each carried piece and each piece held in another hand cell, with the rules' refusal and the price.</summary>
        public static List<Core2ChangeView> EquipmentChanges(Core2CombatSession session, string slotId, int setIndex)
        {
            var c = session.State;
            var list = new List<Core2ChangeView>();
            if (c.Loadout == null) return list;
            var pieces = new List<string> { null };
            foreach (var id in Js.Items(c.Loadout[Ashen.Generated.RunKeys.Storage]).Select(Js.Str)) if (id != null && !pieces.Contains(id)) pieces.Add(id);
            foreach (var slot in HandSlots(c))
                foreach (var id in Js.Items(c.Loadout.Obj(K.Sets)?[slot.Str(K.Id)]).Select(Js.Str))
                    if (!string.IsNullOrEmpty(id) && !pieces.Contains(id)) pieces.Add(id);
            foreach (var pieceId in pieces)
            {
                var intent = Intent(V.CommandChangeEquipment, K.SlotId, slotId, K.SetIndex, setIndex, K.PieceId, pieceId == null ? Js.Null() : Js.S(pieceId));
                var why = session.Why(intent);
                list.Add(new Core2ChangeView { SlotId = slotId, SetIndex = setIndex, PieceId = pieceId, Name = PieceName(c, pieceId), Cost = why == null ? PriceOf(session, intent) : null, Refusal = why, Intent = intent });
            }
            return list;
        }

        // ------------------------------------------------------------------ the log

        private static string NameOf(CombatState c, string id)
        {
            if (id == null) return null;
            if (id == c.Player.Str(K.Id)) return c.Player.Str(K.ClassId) is string cls && c.Data.Classes.Has(cls) ? c.Data.Classes.Get(cls).Str(K.Name) : id;
            var enemy = c.Enemies.FirstOrDefault(e => e.Str(K.Id) == id);
            return enemy != null && c.Data.Enemies.Has(enemy.Str(K.EnemyId)) ? c.Data.Enemies.Get(enemy.Str(K.EnemyId)).Str(K.Name) : id;
        }

        /// <summary>The newest <paramref name="count"/> events as log lines: who and what each names, and its sentence when the table has a line for the type.</summary>
        public static List<Core2LogLine> Log(Core2CombatSession session, StringTable strings, int count)
        {
            var c = session.State;
            var lines = new List<Core2LogLine>();
            var from = Math.Max(0, c.EventLog.Count - count);
            for (var i = from; i < c.EventLog.Count; i++)
            {
                var e = c.EventLog[i];
                var type = e.Str(K.Type);
                var key = HV.LogKeyPrefix + type;
                var line = new Core2LogLine
                {
                    Index = i,
                    Type = type,
                    Turn = c.Turn,
                    SourceId = e.Str(K.SourceId),
                    TargetId = e.Str(K.TargetId),
                    SourceName = NameOf(c, e.Str(K.SourceId)),
                    TargetName = NameOf(c, e.Str(K.TargetId)),
                    Amount = Js.IsNum(e[K.Amount]) ? e.Num(K.Amount) : Js.IsNum(e[K.Turn]) ? e.Num(K.Turn) : (double?)null,
                    Detail = DetailOf(c, e),
                    Event = (JObject)e.DeepClone(),
                };
                if (strings != null && strings.Has(key))
                {
                    line.StringKey = key;
                    var args = new StringArgs()
                        .Add(HV.ArgSource, line.SourceName ?? string.Empty).Add(HV.ArgTarget, line.TargetName ?? string.Empty)
                        .Add(HV.ArgAmount, line.Amount.HasValue ? Number(line.Amount.Value) : string.Empty).Add(HV.ArgDetail, line.Detail ?? string.Empty);
                    line.Text = strings.Format(key, args);
                }
                lines.Add(line);
            }
            return lines;
        }

        /// <summary>What an event names besides who it happened to: the status, flask, card, stance or move, by its row's name.</summary>
        private static string DetailOf(CombatState c, JObject e)
        {
            string Named(Registry registry, string id) => id == null ? null : registry.Has(id) ? registry.Get(id).Str(K.Name) ?? id : id;
            return Named(c.Data.Statuses, e.Str(K.Status)) ?? Named(c.Data.Flasks, e.Str(K.FlaskId)) ?? Named(c.Data.Cards, e.Str(K.CardId)) ?? e.Str(K.MoveId) ?? Named(c.Data.Stances, e.Str(CK.Maneuver));
        }

        private static string Number(double d) => CombatText.Number(d);
    }
}
