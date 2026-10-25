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

        /// <summary>The status's tooltip with its numbers filled (what the chip says when pointed at), and whether its payload is a percent (the web's instancePresentation).</summary>
        public string Tooltip;
        public bool Percent;
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

        /// <summary>
        /// The web's projectCombatIntent of the move once read (P-1): the intent priced against the player hit by hit (damage, hitDamages, hpLossBounds, ...) with the HP range it can take
        /// at the player's current defenses in <c>projectedDamage</c>; null while the move is unread or when it cannot be priced.
        /// </summary>
        public JObject Preview;

        /// <summary>The HP the move can take as a range ('0–16', or '9' when certain), from <see cref="Preview"/>; null without one.</summary>
        /// <summary>The player's stance lowers the HP range shown (the web's green damage tone).</summary>
        public bool Reduced => Known && Preview?.Obj(Ashen.Generated.IntentKeys.ProjectedDamage)?.Str(Ashen.Generated.IntentKeys.Tone) == Ashen.Generated.IntentKeys.ToneGreen;

        public string DamageRange => Preview?.Obj(Ashen.Generated.IntentKeys.ProjectedDamage)?.Str(Ashen.Generated.IntentKeys.Label);
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

        /// <summary>
        /// The share of a physical (Poise) and a magical (Ward) hit the pool takes off while it holds, in percent, as the web's fixCoreProtectionDescription reads it (a pool of 1000 so the
        /// share is uncapped); null when the fight has no FixCore rules. <see cref="WardOff"/> is the data toggle that switches Ward off (Poise alone is described then).
        /// </summary>
        public double? PoiseAbsorb;
        public double? WardAbsorb;
        public bool WardOff;

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

        /// <summary>What each hit of the first damaging effect takes, in turn.</summary>
        public List<double> HitDamages = new List<double>();

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

        /// <summary>The card instance (id, upgraded) the face is made from, the definition it is read as and, for a card in the hand, the preview it prints its numbers from (P-1: <see cref="CardWords"/> words the face from them).</summary>
        public JObject Instance;
        public JObject FaceDef;
        public JObject FacePreview;
        public FaceRules Words;
        public string Type;
        public double? Rank;
        public bool Upgraded;
        public List<string> Tags = new List<string>();

        /// <summary>Every effect row of the card (nested rows too): what the card face's art and tag rail read (VP-3).</summary>
        public List<CardEffectRef> Effects = new List<CardEffectRef>();

        /// <summary>The basic card a Spell mirrors (the card's own or its profile's), or null: the type band shows both marks.</summary>
        public string Mirrors;

        /// <summary>The Precision and the Evade the card's stance entry would grant now (the counted tags of the rail), or null.</summary>
        public double? EntryPrecision;
        public double? EntryEvade;

        /// <summary>The numbered traits the card authors (flux, impact).</summary>
        public Dictionary<string, double> TraitValues = new Dictionary<string, double>();

        /// <summary>What the player's card stage does for this card: its action, the rest it leaves the figure in and the stance that holds the figure while it is selected (P-3b); null for a card the stage cannot read.</summary>
        public CardStagePlan Stage;

        /// <summary>What the definition says about the card's stance and counter (the inspector's hints).</summary>
        public Core2CardRules Rules;

        /// <summary>+1 when a rule made the printed Stamina price dearer, -1 when cheaper, else 0 (the face marks the number).</summary>
        public int CostShift;

        /// <summary>What the card's face is drawn from (its art, type band and tag rail).</summary>
        public CardFaceQuery Face => new CardFaceQuery
        {
            CardId = CardId, Type = Type, Tags = Tags, Effects = Effects, Mirrors = Mirrors, EntryPrecision = EntryPrecision, EntryEvade = EntryEvade, TraitValues = TraitValues,
        };

        /// <summary>The figures of the text the preview moved off the printed number (up: more than printed, down: less), by place in <see cref="Text"/>.</summary>
        public List<TextShift> TextShifts = new List<TextShift>();

        public double Stamina;
        public double Mana;
        public bool CostIsX;
        public JObject Costs;

        public bool NeedsTarget;

        /// <summary>The living enemies the card may be aimed at (empty when it needs no target).</summary>
        public List<string> Targets = new List<string>();

        /// <summary>The stance choices the card offers (empty when it has none).</summary>
        public List<string> Choices = new List<string>();

        /// <summary>The name of each stance choice (its stance row's name, else the choice itself).</summary>
        public Dictionary<string, string> ChoiceNames = new Dictionary<string, string>();

        /// <summary>Why the card cannot be played now (the rules' words), or null.</summary>
        public string Refusal;

        /// <summary>The rules would take at least one play of this card now (a target, a choice, a route).</summary>
        public bool Playable;

        /// <summary>The figure each text token shows, by token name (web previewCard tokens).</summary>
        public Dictionary<string, double> Tokens = new Dictionary<string, double>();

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

        /// <summary>The flask's own words ("Heal 25% of your max HP."), from its definition's text template; empty when it has none.</summary>
        public string Text;

        /// <summary>The key of the flask's art ("flask-crimson"); the screen looks the picture up from it.</summary>
        public string ArtKey;
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
        /// <summary>An amount the preview can price: a number or a formula the engine's evaluator reads.</summary>
        /// <summary>The ops previewCard values by their <c>amount</c> alone (combat.js:2765-2784).</summary>
        private static bool Tokenized(string op) => PlainOps.Contains(op);

        private static readonly HashSet<string> PlainOps = new HashSet<string>
        {
            Ashen.Generated.Core2CombatValues.OpBuildup, Ashen.Generated.CombatOps.LoseHp, Ashen.Generated.CombatOps.Draw, Ashen.Generated.CombatOps.Discard, Ashen.Generated.CombatOps.RemoveStatus,
            Ashen.Generated.CombatOps.GainEnergy, Ashen.Generated.CombatOps.RestoreMana, Ashen.Generated.CombatOps.RestoreStamina, Ashen.Generated.CombatOps.PoiseDamage, Ashen.Generated.CombatOps.AddCinders,
            Ashen.Generated.Core2DamageValues.OpWardDamage, Ashen.Generated.CombatOps.LoseMaxHpPct,
        };

        private static bool Priceable(JToken amount) => Js.IsNum(amount) || Formulas.IsFormula(amount);

        private static T Try<T>(Func<T> read) where T : class
        {
            try { return read(); }
            catch (InvalidOperationException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        /// <summary>The view of the fight as it stands. <paramref name="strings"/> fills the log's sentences; <paramref name="logLines"/> keeps the newest lines.</summary>
        public static Core2CombatViewState Build(Core2CombatSession session, StringTable strings = null, int logLines = (int)Ashen.Generated.Core2HostNumbers.LogLines, JObject presentation = null, CardTextData text = null)
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
            foreach (var inst in c.Piles.Hand) view.Hand.Add(Worded(Card(session, inst, legal), c, text));
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
                    Tooltip = Core2Readings.StatusText(def),
                    Percent = def.Obj(Ashen.Generated.CardFaceKeys.InstancePresentation)?.Str(Ashen.Generated.CardFaceKeys.ValueToken) == K.Percent,
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

        /// <summary>fixCoreProtectionDescription's share: protectionReceipt(shadow, kind, 100).reduced, the shadow holding 1000 in each pool.</summary>
        private static double? Absorbed(CombatState c, JObject entity, string kind)
        {
            var shadow = Js.Spread(entity);
            shadow[CK.PersistentWard] = Js.Spread(entity.Obj(CK.PersistentWard), Js.Obj(K.Value, Ashen.Generated.P1Math.PoolDepth));
            shadow[CK.PersistentPoise] = Js.Spread(entity.Obj(CK.PersistentPoise), Js.Obj(K.Value, Ashen.Generated.P1Math.PoolDepth));
            try { return CoreFixCore.ProtectionReceipt(c, shadow, kind, Ashen.Generated.P1Math.Percent).Num(DK.Reduced); }
            catch (InvalidOperationException) { return null; }
            catch (NotSupportedException) { return null; }
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
            if (CoreFixCore.Enabled(c))
            {
                f.WardOff = CoreFixCore.WardDisabled(c);
                f.PoiseAbsorb = Absorbed(c, entity, Ashen.Generated.Core2DamageValues.KindPhysical);
                f.WardAbsorb = Absorbed(c, entity, Ashen.Generated.Core2DamageValues.KindMagical);
            }
            if (!player && f.Alive) f.Intent = ReadIntent(c, entity);
            // An enemy whose move the player has not read keeps its stance and armed Counter private: they would give the move away (the web shows only the broad family).
            if (f.Intent != null && !f.Intent.Known)
            {
                f.StanceManeuver = null;
                f.StanceName = null;
                f.StanceCamp = null;
                f.StanceRange = null;
                f.Counter = null;
            }
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
            view.Preview = Try(() => CoreIntentProjection.Project(c, enemy));
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

        // ------------------------------------------------------------------ the piles

        /// <summary>
        /// The cards of one pile (<see cref="Ashen.Generated.OverlayValues"/> PileDraw, PileDiscard, PileExhaust) as the pile viewer lists them, each priced like a hand card. The discard and exhaust piles
        /// keep the order the cards reached them (the web's spent-pile view); the draw pile is listed by name, rank then instance, which reveals nothing of its order (the web shuffles it for display with
        /// Math.random, piles.js:24-29; a sorted list is the same promise without randomness in the view).
        /// </summary>
        public static List<Core2CardView> Pile(Core2CombatSession session, string pile, CardTextData text = null)
        {
            var piles = session.State.Piles;
            var source = pile == Ashen.Generated.OverlayValues.PileDraw ? piles.Draw : pile == Ashen.Generated.OverlayValues.PileDiscard ? piles.Discard : pile == Ashen.Generated.OverlayValues.PileExhaust ? piles.Exhaust : new List<JObject>();
            var cards = source.Select(inst => Worded(PileCard(session, inst), session.State, text)).ToList();
            if (pile != Ashen.Generated.OverlayValues.PileDraw) return cards;
            return cards.OrderBy(card => card.Name, StringComparer.Ordinal).ThenBy(card => card.Rank ?? 0).ThenBy(card => card.InstanceId, StringComparer.Ordinal).ToList();
        }

        /// <summary>A card of a pile as its face prints it: name, type, rank, what it costs now and its text with the authored numbers (a pile card is not in play, so nothing is priced against a target).</summary>
        /// <summary>The card with the words of its face (P-1), when the caller has the card text.</summary>
        private static Core2CardView Worded(Core2CardView card, CombatState c, CardTextData text)
        {
            if (text != null) card.Words = new CardWords(c, text).For(card);
            return card;
        }

        private static Core2CardView PileCard(Core2CombatSession session, JObject inst)
        {
            var c = session.State;
            var id = inst.Str(K.InstanceId);
            var def = Try(() => CoreCosts.Resolve(c, inst)) ?? new JObject();
            var costs = Try(() => CoreCosts.PreviewCosts(c, id));
            var card = new Core2CardView
            {
                InstanceId = id,
                CardId = inst.Str(K.CardId),
                Name = def.Str(K.Name) ?? CardName(c, inst.Str(K.CardId)),
                Type = def.Str(K.Type),
                Rank = Js.IsNum(def[CK.Rank]) ? def.Num(CK.Rank) : (double?)null,
                Upgraded = inst.Is(K.Upgraded),
                Stamina = costs?.Num(CK.StaminaCost) ?? 0,
                Mana = costs?.Num(K.ManaCost) ?? 0,
                CostIsX = Js.IsStr(def[K.Cost]) && def.Str(K.Cost) == V.XCost,
            };
            foreach (var tag in Js.Items(def[CK.CardTags] ?? def[K.Tags])) card.Tags.Add(Js.IsStr(tag) ? Js.Str(tag) : Js.Str(Js.Get(tag, K.Id)));
            CardFaceQuery.CollectEffects(def[K.Effects], card.Effects);
            FaceExtras(c, def, card, costs);
            card.Instance = inst;
            card.FaceDef = def;
            card.Text = Text(c, def, card, new Dictionary<string, double>(), Js.Items(def[K.Effects]).OfType<JObject>().ToList(), false);
            return card;
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
            CardFaceQuery.CollectEffects(def[K.Effects], card.Effects);
            FaceExtras(c, def, card, Try(() => CoreCosts.PreviewCosts(c, id)));
            card.Instance = inst;
            card.Stage = Try(() => CardStagePlans.For(c, printed));
            card.FacePreview = Try(() => Core2FacePreview.Build(c, inst));
            card.FaceDef = card.FacePreview?.Obj(Ashen.Generated.CardTextKeys.ResolvedDefinition) ?? def;
            card.Choices = Try(() => CoreCardPlay.ChoiceOptions(def)) ?? new List<string>();
            foreach (var choice in card.Choices) card.ChoiceNames[choice] = c.Data.Stances.Has(choice) ? c.Data.Stances.Get(choice).Str(K.Name) ?? choice : choice;
            card.Plays = legal.Where(l => l.Str(RK.CardInstanceId) == id && l.Str(K.Type) == RV.IntentPlayCard).ToList();
            card.Playable = card.Plays.Count > 0;

            var authored = Js.Items(def[K.Effects]).OfType<JObject>().ToList();
            var bindings = CombatPreview.TokenBindings(c.Data, def[K.Effects]);
            // The web's previewCard prices a card on a detached copy of the fight as the player sees it (an unread enemy shows no stance, combat.js:2465-2476), that has already entered the card's stance (combat.js setCombatStance), as
            // CoreCardPlay.Enter does: the entry's grants and rating bonus count, and the matching-stance flag is read before the entry.
            var priced = authored.Count == 0 ? null : Try(() => CoreVisibility.Project(CoreTransaction.DetachPaused(c)));
            var carrier = priced == null ? null : Try(() => CoreCardPlay.Carrier(priced, inst, def));
            if (carrier != null) Try(() => { CoreStances.SetProfileStance(priced, priced.Player, carrier); return carrier; });
            // previewCard's action (combat.js:2511-2540): the formulas resolve as execution's do, against the card's aimed (or first living) enemy, with the resources the play pays (energySpent is the Stamina paid, combat.js:2540).
            var action = priced == null || carrier == null ? null : new CombatAction
            {
                Source = priced.Player,
                Owner = priced.Player,
                Target = first == null ? null : priced.Enemies.FirstOrDefault(e => e.Str(K.Id) == first),
                Card = carrier,
                Meta = new ActionMeta { EnergySpent = costs?.Num(K.Stamina) ?? 0, ManaSpent = costs?.Num(K.Mana) ?? 0, StaminaSpent = costs?.Num(K.Stamina) ?? 0 },
            };
            double? Price(JToken value, JObject on)
            {
                if (action == null) return null;
                if (Js.IsNum(value)) return Math.Floor(Js.D(value));
                var read = Try(() => (JToken)Js.N(CombatPreview.EvalPreview(priced, action, value, on)));
                return read?.Value<double>();
            }
            // A Counter's reply is priced as the reaction it is (combat.js previewCounter): no rating bonus on the receipt, the reply flags on the carrier.
            var counterCard = carrier?.Obj(CK.CombatProfile)?.Str(CK.Maneuver) == CK.Counter;
            var replyCarrier = carrier;
            if (counterCard)
            {
                replyCarrier = (JObject)carrier.DeepClone();
                replyCarrier[DK.CombatReaction] = true;
                replyCarrier[DK.CounterReturnAttack] = true;
                replyCarrier[DK.SkipRatingBonus] = true;
            }
            // The charges other cards left for this one, and the effects they add (combat.js:2547-2553); the first effect that can carry a charge carries it (attachAbilityCharges).
            var livingNow = priced?.Enemies.Any(e => e.Is(K.Alive)) ?? false;
            var charges = carrier == null ? null : Try(() => CoreRiders.Matching(priced.Player, carrier, livingNow));
            var played = charges == null ? authored : Try(() => CoreRiders.ChargedEffects(priced, authored, charges, livingNow)) ?? authored;
            // Every token-bound value, in the order previewCard computes them: the effects are priced in turn, each against what the one before it spent on the previewed target.
            var values = new Dictionary<string, double>();
            var chargeApplied = new HashSet<string>();
            var damageEntries = new List<(JObject effect, int index)>();
            var damageStates = new Dictionary<string, JObject>();
            for (var i = 0; action != null && i < played.Count; i++)
            {
                var effect = played[i];
                var authoredIndex = authored.IndexOf(effect);
                action.Effect = effect;
                action.Meta = new ActionMeta { EnergySpent = costs?.Num(K.Stamina) ?? 0, ManaSpent = costs?.Num(K.Mana) ?? 0, StaminaSpent = costs?.Num(K.Stamina) ?? 0 };
                if (charges != null) Try(() => { CoreRiders.Attach(priced, action, charges, chargeApplied); return carrier; });
                var charge = action.Meta.Charge;
                var primary = Try(() => CombatPreview.FirstResolvedTarget(priced, action, effect));
                var op = effect.Str(K.Op);
                double? value = null;
                var field = op == Ashen.Generated.CombatOps.ApplyStatus ? K.Stacks : op == Ashen.Generated.CombatOps.LoseMaxHpPct ? K.Pct : K.Amount;
                switch (op)
                {
                    case var _ when op == Ashen.Generated.CombatOps.Damage:
                    {
                        damageEntries.Add((effect, authoredIndex));
                        if (counterCard) break;
                        // The un-aimed face of a damage effect (combat.js:2792): the amount before any defense or matchup, whole, rounded down.
                        var printedAmount = Price(effect[K.Amount], null);
                        if (printedAmount == null) break;
                        var raised = printedAmount.Value + (charge?.Or0(K.Damage) ?? 0) + (charge?.Or0(Ashen.Generated.Core2StatusKeys.DamageEffect) ?? 0);
                        var raw = Try(() => (JToken)Js.N(CoreDamage.ProfileDamageReceipt(priced, priced.Player, null, raised, carrier).Num(DK.Raw)));
                        if (raw != null) value = Math.Floor(raw.Value<double>());
                        break;
                    }
                    case var _ when op == Ashen.Generated.CombatOps.Block || op == Ashen.Generated.Core2CombatValues.OpGainBarrier:
                    {
                        // previewCard's block entries: computeBlockGain on the same stance-entered copy, so the Defense contribution and the matching-stance effectiveness are in the number the text shows.
                        var amount = Price(effect[K.Amount], primary);
                        if (amount == null) break;
                        var gain = Try(() => (JToken)Js.N(CoreActions.ComputeBlockGain(priced, priced.Player, amount.Value + (charge?.Or0(K.Block) ?? 0), carrier)));
                        if (gain != null) value = gain.Value<double>();
                        break;
                    }
                    case var _ when op == Ashen.Generated.Core2DamageValues.OpGainPoise || op == Ashen.Generated.Core2StatusValues.GainWard:
                    {
                        var amount = Price(effect[K.Amount], primary);
                        var gain = amount == null ? null : Try(() => (JToken)Js.N(CoreActions.ComputeMeterGuardGain(priced, priced.Player, amount.Value, carrier)));
                        if (gain != null) value = gain.Value<double>();
                        break;
                    }
                    case var _ when op == Ashen.Generated.CombatOps.ApplyStatus:
                        value = Price(Js.Nullish(effect[K.Stacks]) ? Js.N(1) : effect[K.Stacks], primary) + (charge?.Or0(Ashen.Generated.Core2CombatValues.OpBuildup) ?? 0);
                        break;
                    case var _ when op == Ashen.Generated.CombatOps.Heal:
                    {
                        // previewCard's heal entry (combat.js:2751): the amount plus the card's rating bonus and the charge's, times the matching-stance effectiveness, floored.
                        var amount = Price(effect[K.Amount], primary);
                        var healed = amount == null ? null : Try(() => (JToken)Js.N(Math.Floor((amount.Value + Ratings.CardBonus(priced, priced.Player, carrier, V.BonusHeal, amount.Value) + (charge?.Or0(V.BonusHeal) ?? 0)) * CoreActions.StanceEffectiveness(priced, priced.Player, carrier))));
                        if (healed != null) value = healed.Value<double>();
                        break;
                    }
                    case var _ when op == Ashen.Generated.Core2DamageValues.OpGrantCardCharge:
                        foreach (var rider in new[] { K.Damage, Ashen.Generated.Core2StatusValues.ManaDiscount, K.Block, V.BonusHeal, Ashen.Generated.Core2StatusValues.Break, Ashen.Generated.Core2CombatValues.OpBuildup })
                            if (authoredIndex >= 0 && bindings.TryGetValue(authoredIndex.ToString(CultureInfo.InvariantCulture) + V.KeySeparator + rider, out var riderToken))
                                values[riderToken] = Math.Max(0, Price(effect[rider], primary) ?? 0);
                        break;
                    case var _ when Tokenized(op):
                        value = Price(op == Ashen.Generated.CombatOps.LoseMaxHpPct ? (Js.Nullish(effect[K.Pct]) ? Js.N(0) : effect[K.Pct]) : Js.Nullish(effect[K.Amount]) ? Js.N(1) : effect[K.Amount], primary);
                        break;
                }
                if (!value.HasValue || authoredIndex < 0) continue;
                if (bindings.TryGetValue(authoredIndex.ToString(CultureInfo.InvariantCulture) + V.KeySeparator + field, out var token)) values[token] = value.Value;
                if (op == Ashen.Generated.CombatOps.Damage && bindings.TryGetValue(authoredIndex.ToString(CultureInfo.InvariantCulture) + V.KeySeparator + K.Hits, out var hitsToken))
                {
                    var hitsRead = Price(Js.Nullish(effect[K.Hits]) ? Js.N(1) : effect[K.Hits], primary);
                    if (hitsRead.HasValue) values[hitsToken] = hitsRead.Value;
                }
            }
            // The damage the card's damaging effect would do to each target, and the odds, as the engine's own receipt and hit preview say: the first damaging effect whose condition holds now.
            // Each target reads the condition for itself: an Executioner preview picks the Staggered branch only against a Staggered enemy.
            JObject DamageAgainst(JObject against) => damageEntries.Select(x => x.effect).Where(e => Priceable(e[K.Amount]))
                .FirstOrDefault(e => !e.Is(K.If) || (action != null && Try(() => (object)Triggers.EvalPredicate(priced, e.Obj(K.If), new PredicateContext { Owner = priced.Player, Source = priced.Player, Target = against, Card = carrier, Meta = action.Meta })) is bool open && open))
                ?? damageEntries.Select(x => x.effect).FirstOrDefault(e => Priceable(e[K.Amount]));
            var damage = DamageAgainst(action?.Target);
            if (action != null) action.Effect = damage;
            var damageHits = damage == null ? null : Price(Js.Nullish(damage[K.Hits]) ? Js.N(1) : damage[K.Hits], Try(() => CombatPreview.FirstResolvedTarget(priced, action, damage)));
            var damageCharge = damage == null || charges == null ? 0 : charges.Or0(K.Damage) + charges.Or0(Ashen.Generated.Core2StatusKeys.DamageEffect);
            foreach (var target in living)
            {
                if (!needsTarget) break;
                var hitRange = Try(() => session.PreviewHit(id, target));
                var row = Js.Items(hitRange?[HK.Targets]).OfType<JObject>().FirstOrDefault(t => t.Str(K.TargetId) == target);
                var hv = new Core2HitView
                {
                    TargetId = target,
                    HitChance = Js.IsNum(row?[HK.CurrentHitChance]) ? row.Num(HK.CurrentHitChance) : Js.IsNum(row?[HK.HitChance]) ? row.Num(HK.HitChance) : (double?)null,
                    InRange = row == null || !(row[HK.CurrentInRange]?.Type == JTokenType.Boolean) || row.Is(HK.CurrentInRange),
                };
                var pricedEnemy = priced.Enemies.First(e => e.Str(K.Id) == target);
                var targetDamage = DamageAgainst(pricedEnemy);
                var targetHits = targetDamage == damage ? damageHits : targetDamage == null ? null : Price(Js.Nullish(targetDamage[K.Hits]) ? Js.N(1) : targetDamage[K.Hits], pricedEnemy);
                if (targetDamage != null && carrier != null && targetHits.HasValue)
                {
                    // previewDamageHits: every hit of the card in turn, each leaving its effect on the previewed target for the next; the amount is the formula as this target reads it (combat.js:2678).
                    var hitsCount = targetHits.Value;
                    var amount = Price(targetDamage[K.Amount], pricedEnemy);
                    if (amount != null) amount += damageCharge + (counterCard ? Ratings.CardBonus(priced, priced.Player, carrier, Ashen.Generated.CombatOps.Damage, amount.Value) : 0);
                    var receipts = amount == null ? null : Try(() => CoreDamage.PreviewHits(priced, priced.Player, pricedEnemy, amount.Value, replyCarrier, hitsCount));
                    if (receipts != null && receipts.Count > 0)
                    {
                        hv.Damage = receipts[0].Num(K.Amount);
                        hv.HitsCount = hitsCount;
                        hv.HitDamages = receipts.Select(r => r.Num(K.Amount)).ToList();
                        hv.Receipt = CoreDamage.CombinedReceipt(pricedEnemy, receipts);
                    }
                }
                card.Hits[target] = hv;
            }
            foreach (var route in Js.Items(range?[HK.Routes]).OfType<JObject>()) card.Routes.Add(Route(route));
            card.Tokens = values;
            // The live figure of each plain status grant, which the rail's count reads (web: preview.values[index]).
            foreach (var effect in card.Effects.Where(e => e.Index >= 0 && e.Status != null))
            {
                var token = bindings.TryGetValue(effect.Index.ToString(CultureInfo.InvariantCulture) + V.KeySeparator + K.Stacks, out var stackToken) ? stackToken : null;
                if (token != null && values.TryGetValue(token, out var stacks)) effect.Live = stacks;
            }
            card.Text = Text(c, def, card, values, authored, true);
            return card;
        }

        /// <summary>The card's text template with its numbers: each tokenizable value as authored, the damage token as the first target's receipt says and each Block token as the play grants it.</summary>
        /// <summary>What the card's face reads besides its text: the mirrored basic, the numbered traits, the entry grants (the stance entry's Precision as the preview quotes it, its Evade as the rules give it) and the price's shift.</summary>
        private static void FaceExtras(CombatState c, JObject def, Core2CardView card, JObject costs)
        {
            card.Mirrors = def.Obj(CK.CombatProfile)?.Str(CK.Mirrors) ?? def.Str(CK.Mirrors);
            card.Rules = new Core2CardRules
            {
                CounterCoverage = def.Obj(CK.CounterCoverage),
                DefenseRange = def.Str(Ashen.Generated.CardFaceKeys.DefenseRange),
                // The web's playingCardDefinition applies the combat expansion to every Core 2 card (defenseRulesVersion 2) unless the definition says otherwise.
                DefenseRulesVersion = Js.IsNum(def[Ashen.Generated.Core2RangeKeys.DefenseRulesVersion]) ? def.Num(Ashen.Generated.Core2RangeKeys.DefenseRulesVersion) : Ashen.Generated.Core2RangeNumbers.DefenseRulesVersion,
                DefensiveReaction = def.Is(Ashen.Generated.CardFaceKeys.DefensiveReaction),
            };
            foreach (var trait in def.Obj(DK.TraitValues)?.Properties() ?? Enumerable.Empty<JProperty>())
                if (Js.IsNum(trait.Value)) card.TraitValues[trait.Name] = Js.D(trait.Value);
            if (costs != null && Js.IsNum(costs[Ashen.Generated.Core2CostKeys.EntryPrecision])) card.EntryPrecision = costs.Num(Ashen.Generated.Core2CostKeys.EntryPrecision);
            if (costs != null && Js.IsNum(costs[Ashen.Generated.Core2CostKeys.CostShift])) card.CostShift = (int)costs.Num(Ashen.Generated.Core2CostKeys.CostShift);
            if (Try(() => (object)CoreFixCore.Enabled(c)) is bool core && core)
            {
                var evade = Try(() => (object)CoreStances.EntryBonuses(c, def).Num(CK.Evade));
                if (evade is double grant && grant > 0d) card.EntryEvade = grant;
            }
        }

        private static string Text(CombatState c, JObject def, Core2CardView card, Dictionary<string, double> values, List<JObject> authored, bool live)
        {
            var tokens = new JObject();
            var printed = new JObject();
            var shown = card.Hits.Values.FirstOrDefault(h => h.Damage.HasValue);
            string amountToken = null, hitsToken = null;
            foreach (var binding in CombatPreview.TokenBindings(c.Data, def[K.Effects]))
            {
                var split = binding.Key.Split(V.KeySeparator[0]);
                var index = int.Parse(split[0], CultureInfo.InvariantCulture);
                var effect = authored[index];
                var isDamage = effect.Str(K.Op) == Ashen.Generated.CombatOps.Damage;
                if (isDamage && split[1] == K.Hits && hitsToken == null) hitsToken = binding.Value;
                if (isDamage && split[1] == K.Amount) amountToken ??= binding.Value;
                if (values.TryGetValue(binding.Value, out var worked)) tokens[binding.Value] = Js.N(worked);
                // A Counter's reply is the hit the first target would take (previewCounter); its face keeps the printed amount when nothing is priced.
                if (isDamage && split[1] == K.Amount && shown != null && binding.Value == Ashen.Generated.CombatOps.Damage && !values.ContainsKey(binding.Value)) tokens[binding.Value] = Js.N(shown.Damage.Value);
                else if (tokens[binding.Value] == null && Js.IsNum(effect[split[1]])) tokens[binding.Value] = effect[split[1]].DeepClone();
                // The printed number (web staticCardTokens): the effect's own figure when it is a number.
                if (Js.IsNum(effect[split[1]])) printed[binding.Value] = effect[split[1]].DeepClone();
            }
            var template = def.Str(K.TextTemplate);
            // A card whose hits land unevenly (an earlier hit spends Block or Poise a later one meets) reads "{damage} ... {hits} times" as the hits in turn with their total (the card face's damageSequences).
            var hitDamages = shown?.HitDamages;
            if (amountToken != null && hitsToken != null && template != null && hitDamages != null && hitDamages.Count > 1 && hitDamages.Any(n => n != hitDamages[0]))
            {
                var done = false;
                template = System.Text.RegularExpressions.Regex.Replace(template, Ashen.Generated.CombatPatterns.DamageSequence, m =>
                {
                    if (done || m.Groups[V.GroupAmount].Value != amountToken || m.Groups[V.GroupHits].Value != hitsToken) return m.Value;
                    done = true;
                    return string.Format(CultureInfo.InvariantCulture, Ashen.Generated.CombatPatterns.TokenFormat, amountToken) + m.Groups[V.GroupBetween].Value
                        + string.Format(CultureInfo.InvariantCulture, Ashen.Generated.CombatPatterns.SequenceTotal, hitDamages.Sum(), hitDamages.Count);
                });
                template = template.Replace(string.Format(CultureInfo.InvariantCulture, Ashen.Generated.CombatPatterns.TokenFormat, amountToken), string.Join(Ashen.Generated.CombatPatterns.SequenceJoin, hitDamages));
            }
            card.TextShifts = new List<TextShift>();
            return CombatViewModel.ResolveText(template, tokens, live ? printed : null, card.TextShifts);
        }

        /// <summary>A route option as the card shows it (the engine's quote, display-ready); the flow reads it again for the picked target.</summary>
        internal static Core2RouteView Route(JObject route)
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
                    ChargeKind = kind, FlaskId = flaskId, Name = def.Str(K.Name), Text = def.Str(K.TextTemplate), ArtKey = def.Str(Ashen.Generated.OverlayKeys.FlaskArtKey), Current = charges.Num(kind + V.CurrentSuffix), Max = charges.Num(kind), Targeted = def.Is(K.Targeted),
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
                    Slot = i, FlaskId = slot.Str(K.FlaskId), Name = def.Str(K.Name), Text = def.Str(K.TextTemplate), ArtKey = def.Str(Ashen.Generated.OverlayKeys.FlaskArtKey), Current = 1, Max = 1, Targeted = def.Is(K.Targeted),
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
