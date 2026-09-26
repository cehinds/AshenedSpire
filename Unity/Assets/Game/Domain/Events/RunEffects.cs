using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Ashen.Domain.Shop;
using Newtonsoft.Json.Linq;
using RngStream = Ashen.Generated.RngStream;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;
using WV = Ashen.Generated.RewardsValues;
using MK = Ashen.Generated.MapKeys;
using SK = Ashen.Generated.ShopKeys;
using SM = Ashen.Generated.ShopMessages;
using EK = Ashen.Generated.EventKeys;
using EV = Ashen.Generated.EventValues;
using EM = Ashen.Generated.EventMessages;
using CombatMath = Ashen.Generated.CombatMath;

namespace Ashen.Domain.Events
{
    /// <summary>
    /// The run-level door's context, the ONE port of shipped engine/actions.js createRunContext / syncRunContext /
    /// drainRunContext / runRunOpcode (D-100): a player FACADE over the run's pools, no enemies, empty piles, an action
    /// queue and trigger gates, so damage, heal, loseHp and restoreMana reach the run through the same opcode bodies a
    /// fight uses, and the run opcodes (rules/eventsEngine.json <c>runOpcodes</c>: cinders, cards in and out, card
    /// upgrades, relics, flasks, flask capacity, max HP, fights, the class swap and the grace refill) write the run
    /// itself. Three doors share it, as shipped: <see cref="RunEffects.Execute"/> (event choices), the rest visit (which
    /// mounts a place's property rules on <see cref="State"/> and emits <c>arrived</c>/<c>rested</c> through
    /// <see cref="EmitAndDrain"/>, with its heal scale) and whatever else runs an effect list outside a fight.
    /// </summary>
    public sealed class RunEffectContext
    {
        public EventsData Data { get; }
        public JObject Run { get; }
        public CombatState State { get; }

        /// <summary>ctx.receipts.refill: the refillFlasks receipt, or null.</summary>
        public JObject Refill;

        /// <summary>ctx.refillOpts: handed to the refillFlasks opcode.</summary>
        internal JObject RefillOpts { get; }

        /// <summary>
        /// createRunContext({ run, registries, rng }, { healMult, refillOpts }): <paramref name="healMult"/> scales every
        /// heal the context applies (floored once after it; a rest's custom mod × restHealMult), <paramref name="refillOpts"/>
        /// are handed to the refillFlasks opcode.
        /// </summary>
        public RunEffectContext(EventsData d, JObject run, Rng rng, double healMult = 1, JObject refillOpts = null)
        {
            Data = d ?? throw new ArgumentNullException(nameof(d));
            Run = run ?? throw new ArgumentNullException(nameof(run));
            RefillOpts = refillOpts ?? new JObject();
            var facade = Js.Obj(K.Id, V.Player, K.Kind, V.Player, K.Hp, run[K.Hp]?.DeepClone(), K.MaxHp, run[K.MaxHp]?.DeepClone(),
                K.Mana, run[K.Mana]?.DeepClone(), K.MaxMana, run[K.MaxMana]?.DeepClone(), K.Block, 0.0, K.Statuses, new JObject(),
                K.StanceId, Js.Null(), K.RelicIds, new JArray(),
                K.Counters, Js.Obj(K.CardsPlayedThisTurn, 0.0, K.CardsPlayedThisCombat, 0.0, K.AttacksPlayedThisCombat, 0.0),
                K.Alive, run.Num(K.Hp) > 0);
            State = new CombatState
            {
                Data = d.Combat,
                Rng = rng,
                Player = facade,
                HandMax = d.Combat.Balance.Num(K.HandMax),
                Turn = 0,
                HealMult = healMult,
                RunOpcodes = RunOpcodeHook,
            };
        }

        /// <summary>nextInstanceId: <c>run&lt;n&gt;</c> on the context's own counter.</summary>
        internal string NextInstanceId()
        {
            State.IdCounter += 1;
            return EV.RunInstancePrefix + RunJs.NumStr(State.IdCounter);
        }

        /// <summary>runOpcode's first branch: a run opcode goes to runRunOpcode (also for actions a combat opcode runs inline).</summary>
        private bool RunOpcodeHook(CombatState c, CombatAction action, JObject eff)
        {
            if (!Data.List(EK.RunOpcodes).Contains(eff.Str(K.Op))) return false;
            RunEffects.RunOpcode(this, action, eff);
            return true;
        }

        /// <summary>executeAction: the result check, then the (deferred) script escape hatch, then the combat interpreter's body.</summary>
        internal void ExecuteAction(CombatAction action)
        {
            if (State.Result != null) return;
            if (Js.IsStr(action.Effect[EK.Script])) throw new NotSupportedException(EM.ScriptsDeferred);
            Actions.ExecuteAction(State, action);
        }

        /// <summary>syncRunContext: re-read the run's pools into the facade.</summary>
        public void Sync()
        {
            var p = State.Player;
            p[K.Hp] = Run[K.Hp]?.DeepClone();
            p[K.MaxHp] = Run[K.MaxHp]?.DeepClone();
            p[K.Mana] = Run[K.Mana]?.DeepClone();
            p[K.MaxMana] = Run[K.MaxMana]?.DeepClone();
            p.Put(K.Alive, Run.Num(K.Hp) > 0);
        }

        /// <summary>drainRunContext: run the queue to empty (bounded), then write the facade's pools back.</summary>
        public void Drain()
        {
            double guard = 0;
            var limit = Js.D(Data.Rule(K.Effects, K.QueueGuard));
            while (State.Queue.Count > 0)
            {
                if (++guard > limit) throw new InvalidOperationException(EM.QueueDidNotDrain);
                var action = State.Queue.First.Value;
                State.Queue.RemoveFirst();
                ExecuteAction(action);
            }
            Run.Put(K.Hp, Math.Min(State.Player.Num(K.Hp), Run.Num(K.MaxHp)));
            Run.Put(K.Mana, Math.Min(State.Player.Num(K.Mana), Run.Num(K.MaxMana)));
        }

        /// <summary>emitAndDrain (engine/locations.js): sync, emit the event, drain; returns the events it logged.</summary>
        public JArray EmitAndDrain(string type, JObject payload)
        {
            Sync();
            var from = State.EventLog.Count;
            Triggers.EmitEvent(State, type, payload);
            Drain();
            return new JArray(State.EventLog.Skip(from).Select(e => e.DeepClone()));
        }
    }

    /// <summary>
    /// The run-level effect door (shipped engine/actions.js executeRunEffects and runRunOpcode, engine/encounters.js
    /// applyGraceRefill): an effect list runs outside combat on a <see cref="RunEffectContext"/>; after the queue drains,
    /// the facade's HP and Mana are written back. Returns the event log.
    /// </summary>
    public static class RunEffects
    {
        /// <summary>
        /// executeRunEffects({ run, registries, rng }, effects) → events: every effect queued against the facade (source,
        /// owner and target), the queue drained, the facade's pools written back.
        /// </summary>
        public static JArray Execute(EventsData d, JObject run, Rng rng, JArray effects)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            if (run == null) throw new ArgumentNullException(nameof(run));
            var ctx = new RunEffectContext(d, run, rng);
            var c = ctx.State;
            foreach (var eff in Js.Items(effects).OfType<JObject>())
                c.Enqueue(new CombatAction { Effect = eff, Source = c.Player, Owner = c.Player, Target = c.Player, Meta = ActionMeta.Empty() });
            ctx.Drain();
            return new JArray(c.EventLog.Select(e => e.DeepClone()));
        }

        /// <summary>The flask charge kinds (rules/combatEngine.json flasks.chargeKinds), in order.</summary>
        public static List<string> ChargeKinds(CombatData d) => Js.Items(d.Engine.Obj(K.Flasks)?[K.ChargeKinds]).Select(Js.Str).ToList();

        /// <summary>
        /// applyGraceRefill(registries, run, opts): the Crimson and Azure charge pools refilled to their split; the receipt
        /// the rest screen's refill line reads. A run without charge pools (a pre-authority save) takes the legacy
        /// flask-slot refill, deferred (D-094).
        /// </summary>
        public static JObject GraceRefill(CombatData d, JObject run, JObject opts)
        {
            if (!(run[K.FlaskCharges] is JObject charges) || !Js.Truthy(run[K.FlaskCharges])) throw new NotSupportedException(EM.LegacyGraceRefillDeferred);
            foreach (var kind in ChargeKinds(d)) charges[kind + V.CurrentSuffix] = charges[kind]?.DeepClone();
            return Js.Obj(EK.ChargePools, charges.DeepClone(), RK.Grants, new JArray(), K.Total, 0.0, EK.Shortfalls, new JArray());
        }

        private static double FlaskSlotCap(EventsData d)
        {
            var n = d.Combat.Balance[SK.FlaskSlots];
            if (!Js.IsInt(n) || Js.D(n) <= 0) throw new InvalidOperationException(SM.FlaskSlotsNotPositive);
            return Js.D(n);
        }

        /// <summary>runRunOpcode: the run-level opcodes (events, shops, rewards and rest places share the same DSL).</summary>
        internal static void RunOpcode(RunEffectContext ctx, CombatAction action, JObject eff)
        {
            var d = ctx.Data;
            var run = ctx.Run;
            var c = ctx.State;
            var rng = c.Rng;
            var op = eff.Str(K.Op);
            if (op == EV.AddCinders)
            {
                var n = Actions.EvalNum(c, action, eff[K.Amount], 0);
                run.Put(RK.Cinders, Math.Max(0, run.Num(RK.Cinders) + n));
                c.Emit(EV.CindersChanged, Js.Obj(K.Amount, n, K.Total, run[RK.Cinders]));
            }
            else if (op == EV.AddCardToDeck)
            {
                d.Run.Cards.Get(eff.Str(K.Card));
                run.Arr(K.Deck).Add(Js.Obj(K.InstanceId, ctx.NextInstanceId(), K.CardId, eff[K.Card], K.Upgraded, false));
            }
            else if (op == EV.RemoveCardFromDeck)
            {
                var deck = run.Arr(K.Deck);
                var idx = -1;
                if (Js.Truthy(eff[K.Card])) idx = deck.OfType<JObject>().ToList().FindIndex(card => card.Str(K.CardId) == eff.Str(K.Card) && CardRemoval.CanRemove(card));
                else if (Js.Truthy(eff[K.Random]))
                {
                    var candidates = Enumerable.Range(0, deck.Count).Where(i => CardRemoval.CanRemove(deck[i] as JObject)).ToList();
                    idx = candidates.Count > 0 ? candidates[rng.Below(RngStream.Misc, candidates.Count)] : -1;
                }
                if (idx >= 0) CardRemoval.RemoveDeckCard(d.Shop, run, deck[idx].Value<string>(K.InstanceId));
            }
            else if (op == EV.UpgradeCard)
            {
                var named = Js.Truthy(eff[K.Card]) ? eff.Str(K.Card) : null;
                var plan = ItemSmithing.Plan(d.Shop, run);
                // (armament id, or the deck instance itself for an ordinary card — held by reference, never copied)
                var candidates = new List<KeyValuePair<string, JObject>>();
                foreach (var candidate in plan.Arr(SK.Candidates).OfType<JObject>())
                {
                    if (candidate.Str(SK.ItemKind) != V.ArmamentRefPrefix) continue;
                    var affected = candidate.Arr(SK.AffectedCards);
                    if (affected.Count == 0) continue;
                    if (named != null && !affected.OfType<JObject>().Any(card => card.Str(K.CardId) == named)) continue;
                    candidates.Add(new KeyValuePair<string, JObject>(candidate.Str(WK.ArmamentId), null));
                }
                foreach (var card in run.Arr(K.Deck).OfType<JObject>())
                {
                    if (Js.Truthy(card[K.SourceArmamentId]) || Js.Truthy(card[K.GrantedBy]) || Js.Truthy(card[K.Upgraded])) continue;
                    if (named != null && card.Str(K.CardId) != named) continue;
                    if (!d.Run.Cards.Has(card.Str(K.CardId)) || !Js.Truthy(d.Run.Cards.Get(card.Str(K.CardId))[K.Upgrade])) continue;
                    candidates.Add(new KeyValuePair<string, JObject>(null, card));
                }
                if (candidates.Count == 0) return;
                var chosen = Js.Truthy(eff[K.Random]) ? rng.Pick(RngStream.Misc, candidates) : candidates[0];
                if (chosen.Value == null)
                {
                    var receipt = ItemSmithing.Commit(d.Shop, run, chosen.Key, true);
                    c.Emit(EV.ArmamentSmithed, receipt);
                }
                else chosen.Value.Put(K.Upgraded, true);
            }
            else if (op == EV.AddRelic)
            {
                var relicId = Js.Truthy(eff[K.Id]) ? eff.Str(K.Id) : null;
                if (relicId == null && Js.Truthy(eff[K.Random]))
                {
                    var pool = d.Run.Relics.Ids.Where(id => !Js.Includes(run[K.Relics], id)
                        && (Js.Truthy(d.Run.Relics.Get(id)[WK.Pool]) ? RunJs.Key(d.Run.Relics.Get(id)[WK.Pool]) : WV.RewardPool) == WV.RewardPool).ToList();
                    if (pool.Count == 0) return;
                    relicId = rng.Pick(RngStream.RelicRewards, pool);
                }
                if (relicId != null && !Js.Includes(run[K.Relics], relicId))
                {
                    d.Run.Relics.Get(relicId);
                    run.Arr(K.Relics).Add(relicId);
                    Creation.SyncFlaskGrowth(d.Run, run);
                }
            }
            else if (op == EV.AddFlask)
            {
                if (run.Arr(K.Flasks).Count >= FlaskSlotCap(d)) return;
                var flaskId = Js.Truthy(eff[K.Id]) ? eff.Str(K.Id) : null;
                if (flaskId == null && Js.Truthy(eff[K.Random]))
                {
                    var pool = d.Combat.Flasks.Ids.ToList();
                    if (pool.Count == 0) return;
                    flaskId = rng.Pick(RngStream.FlaskRewards, pool);
                }
                if (flaskId != null)
                {
                    d.Combat.Flasks.Get(flaskId);
                    run.Arr(K.Flasks).Add(Js.Obj(K.FlaskId, flaskId));
                }
            }
            else if (op == EV.AddFlaskCapacity)
            {
                var kind = eff.Str(K.Kind);
                var charges = run.Obj(K.FlaskCharges);
                if (!Js.Truthy(run[K.FlaskCharges]) || kind == null || !d.RuleList(K.Effects, EK.CapacityKinds).Contains(kind) || !Js.IsInt(eff[K.Amount]) || eff.Num(K.Amount) <= 0) return;
                var amount = eff.Num(K.Amount);
                charges.Put(K.Capacity, charges.Num(K.Capacity) + amount);
                charges.Put(kind, charges.Num(kind) + amount);
                charges.Put(kind + V.CurrentSuffix, charges.Num(kind + V.CurrentSuffix) + amount);
                charges.Put(RK.Granted, charges.Num(RK.Granted) + amount);
            }
            else if (op == EV.LoseMaxHpPct)
            {
                var pct = Actions.EvalNum(c, action, eff[K.Pct], 0);
                if (!Js.IsInt(run[RK.MaxHpAdjustment])) throw new InvalidOperationException(EM.MaxHpLedgerMissing);
                var before = run.Num(K.MaxHp);
                run.Put(K.MaxHp, Math.Max(1, Math.Floor(run.Num(K.MaxHp) * (1 - pct / CombatMath.Percent))));
                run.Put(RK.MaxHpAdjustment, run.Num(RK.MaxHpAdjustment) + run.Num(K.MaxHp) - before);
                run.Put(K.Hp, Math.Min(run.Num(K.Hp), run.Num(K.MaxHp)));
            }
            else if (op == EV.StartCombat)
            {
                d.Run.Encounters.Get(eff.Str(MK.EncounterId));
                run[RK.CombatEntered] = eff[MK.EncounterId]?.DeepClone();
            }
            else if (op == EV.SwapClass)
            {
                var others = d.Run.Classes.Ids.Where(id => id != run.Str(RK.Class)).ToList();
                var classId = Js.Truthy(eff[K.Random]) ? (others.Count > 0 ? rng.Pick(RngStream.Misc, others) : run.Str(RK.Class)) : eff.Str(K.ClassId);
                ClassSwap.Swap(d, run, classId);
            }
            else if (op == EV.RefillFlasks) ctx.Refill = GraceRefill(d.Combat, run, ctx.RefillOpts);
            else throw new InvalidOperationException(RunJs.Fmt(EM.RunOpcodeUnimplemented, op));
        }
    }
}
