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
using SV = Ashen.Generated.ShopValues;
using SM = Ashen.Generated.ShopMessages;
using EK = Ashen.Generated.EventKeys;
using EV = Ashen.Generated.EventValues;
using EM = Ashen.Generated.EventMessages;
using CombatMath = Ashen.Generated.CombatMath;

namespace Ashen.Domain.Events
{
    /// <summary>
    /// The run-level effect door (shipped engine/actions.js executeRunEffects, createRunContext, drainRunContext and
    /// runRunOpcode): an effect list runs outside combat through the same interpreter a fight uses, against a player
    /// FACADE over the run's pools (so damage, heal and loseHp apply to run.hp) with no enemies; the run opcodes
    /// (cinders, cards in and out, card upgrades, relics, flasks, flask capacity, max HP, fights, the class swap) write
    /// the run itself. After the queue drains, the facade's HP and Mana are written back. Returns the event log.
    /// </summary>
    public static class RunEffects
    {
        private sealed class RunContext
        {
            public EventsData Data;
            public JObject Run;
            public CombatState Combat;
            public double IdCounter;

            public string NextInstanceId()
            {
                IdCounter += 1;
                return EV.RunInstancePrefix + RunJs.NumStr(IdCounter);
            }
        }

        /// <summary>createRunContext({ run, registries, rng }): the facade, an empty board and an action queue.</summary>
        private static RunContext Create(EventsData d, JObject run, Rng rng)
        {
            var facade = Js.Obj(K.Id, V.Player, K.Kind, V.Player, K.Hp, run[K.Hp]?.DeepClone(), K.MaxHp, run[K.MaxHp]?.DeepClone(),
                K.Mana, run[K.Mana]?.DeepClone(), K.MaxMana, run[K.MaxMana]?.DeepClone(), K.Block, 0.0, K.Statuses, new JObject(), K.StanceId, Js.Null(),
                K.RelicIds, new JArray(), K.Counters, Js.Obj(K.CardsPlayedThisTurn, 0.0, K.CardsPlayedThisCombat, 0.0, K.AttacksPlayedThisCombat, 0.0),
                K.Alive, run.Num(K.Hp) > 0);
            var combat = new CombatState { Data = d.Combat, Rng = rng, Player = facade, HandMax = d.Combat.Balance.Num(K.HandMax) };
            return new RunContext { Data = d, Run = run, Combat = combat };
        }

        /// <summary>
        /// executeRunEffects({ run, registries, rng }, effects) → events: every effect queued against the facade (source,
        /// owner and target), the queue drained, the facade's pools written back.
        /// </summary>
        public static JArray Execute(EventsData d, JObject run, Rng rng, JArray effects)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            if (run == null) throw new ArgumentNullException(nameof(run));
            var ctx = Create(d, run, rng);
            var c = ctx.Combat;
            foreach (var eff in Js.Items(effects).OfType<JObject>())
                c.Enqueue(new CombatAction { Effect = eff, Source = c.Player, Owner = c.Player, Target = c.Player, Meta = ActionMeta.Empty() });
            double guard = 0;
            var limit = Js.D(d.Rule(K.Effects, K.QueueGuard));
            while (c.Queue.Count > 0)
            {
                if (++guard > limit) throw new InvalidOperationException(EM.QueueDidNotDrain);
                var action = c.Queue.First.Value;
                c.Queue.RemoveFirst();
                ExecuteAction(ctx, action);
            }
            run.Put(K.Hp, Math.Min(c.Player.Num(K.Hp), run.Num(K.MaxHp)));
            run.Put(K.Mana, Math.Min(c.Player.Num(K.Mana), run.Num(K.MaxMana)));
            return new JArray(c.EventLog.Select(e => e.DeepClone()));
        }

        /// <summary>executeAction: the `if` gate and `repeat`, then one opcode per repetition.</summary>
        private static void ExecuteAction(RunContext ctx, CombatAction action)
        {
            var c = ctx.Combat;
            if (c.Result != null) return;
            var eff = action.Effect;
            if (Js.IsStr(eff[EK.Script])) throw new NotSupportedException(EM.ScriptsDeferred);
            if (Js.Truthy(eff[K.If]))
            {
                var pctx = new PredicateContext { Owner = action.Owner ?? action.Source, Source = action.Source, Target = action.Target, Card = action.Card, Meta = action.Meta };
                if (!Triggers.EvalPredicate(c, eff.Obj(K.If), pctx)) return;
            }
            var repeat = Actions.EvalNum(c, action, eff[K.Repeat], 1);
            for (var r = 0; r < repeat; r++)
            {
                if (ctx.Data.List(EK.RunOpcodes).Contains(eff.Str(K.Op))) RunOpcode(ctx, action, eff);
                else Actions.RunOpcode(c, action, eff);
                if (c.Result != null) return;
            }
        }

        private static double FlaskSlotCap(EventsData d)
        {
            var n = d.Combat.Balance[SK.FlaskSlots];
            if (!Js.IsInt(n) || Js.D(n) <= 0) throw new InvalidOperationException(SM.FlaskSlotsNotPositive);
            return Js.D(n);
        }

        /// <summary>runRunOpcode: the run-level opcodes (events, shops and rewards share the same DSL).</summary>
        private static void RunOpcode(RunContext ctx, CombatAction action, JObject eff)
        {
            var d = ctx.Data;
            var run = ctx.Run;
            var c = ctx.Combat;
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
            else if (op == EV.RefillFlasks) throw new NotSupportedException(EM.RefillFlasksDeferred);
            else throw new InvalidOperationException(RunJs.Fmt(EM.RunOpcodeUnimplemented, op));
        }
    }
}
