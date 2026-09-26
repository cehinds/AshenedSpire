using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>A player intent (shipped dispatch intents: playCard / endTurn / useFlask).</summary>
    public sealed class CombatCommand
    {
        public string Type;
        public string CardInstanceId;
        public string TargetId;
        public List<string> DiscardIds = new List<string>();
        public int Slot;
        public string ChargeKind;

        public static CombatCommand PlayCard(string cardInstanceId, string targetId = null) =>
            new CombatCommand { Type = V.CommandPlayCard, CardInstanceId = cardInstanceId, TargetId = targetId };

        public static CombatCommand EndTurn(IEnumerable<string> discardIds = null) =>
            new CombatCommand { Type = V.CommandEndTurn, DiscardIds = discardIds?.ToList() ?? new List<string>() };

        public static CombatCommand UseFlask(int slot, string chargeKind = null, string targetId = null) =>
            new CombatCommand { Type = V.CommandUseFlask, Slot = slot, ChargeKind = chargeKind, TargetId = targetId };

        /// <summary>The wire form ({ type, cardInstanceId?, targetId?, discardIds?, slot?, chargeKind? }).</summary>
        public static CombatCommand FromJson(JObject o) => new CombatCommand
        {
            Type = o.Str(K.Type),
            CardInstanceId = o.Str(K.CardInstanceId),
            TargetId = o.Str(K.TargetId),
            DiscardIds = Js.Items(o[K.DiscardIds]).Select(Js.Str).ToList(),
            Slot = (int)Js.Or0(o[K.Slot]),
            ChargeKind = o.Str(K.ChargeKind),
        };
    }

    /// <summary>
    /// The action queue and turn loop (shipped engine/combat.js, SPEC §3.9, §4.1–§4.3, §4.6): the generic
    /// interpreter — no entity-specific code; all behaviour is content composed of the closed primitive sets.
    /// </summary>
    public static class CombatEngine
    {
        /// <summary>dispatch(combat, intent) → the events this intent emitted. Throws on an illegal intent.</summary>
        public static List<JObject> Dispatch(CombatState c, CombatCommand command)
        {
            if (c.Result != null) throw new InvalidOperationException(M.CombatOver);
            c.Buffer = new List<JObject>();
            try
            {
                switch (command.Type)
                {
                    case V.CommandPlayCard:
                        PlayCard(c, command);
                        break;
                    case V.CommandEndTurn:
                        EndTurn(c, command.DiscardIds ?? new List<string>());
                        break;
                    case V.CommandUseFlask:
                        UseFlask(c, command);
                        break;
                    default:
                        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UnknownIntent, command.Type));
                }
                return c.Buffer;
            }
            finally
            {
                c.Buffer = null;
            }
        }

        // ------------------------------------------------------------------ queue

        internal static void DrainQueue(CombatState c)
        {
            var guard = 0;
            while (c.Queue.Count > 0)
            {
                if (++guard > c.Data.Rule(K.Limits, K.QueueGuard)) throw new InvalidOperationException(M.QueueDidNotDrain);
                var action = c.Queue.First.Value;
                c.Queue.RemoveFirst();
                Actions.ExecuteAction(c, action);
                EndCheck(c);
                if (c.Result != null)
                {
                    c.Queue.Clear();
                    return;
                }
            }
            EndCheck(c);
        }

        private static void EndCheck(CombatState c)
        {
            if (c.Result != null) return;
            if (!c.Player.Is(K.Alive) || c.Player.Num(K.Hp) <= 0) Finish(c, V.Defeat);
            else if (c.Enemies.Count > 0 && c.Enemies.All(e => !e.Is(K.Alive))) Finish(c, V.Victory);
        }

        private static void Finish(CombatState c, string result)
        {
            c.Result = result;
            c.Phase = V.PhaseEnded;
            c.Queue.Clear();
            c.Emit(E.CombatEnd, Js.Obj(K.Victory, result == V.Victory));
            c.Queue.Clear();
        }

        // ------------------------------------------------------------------ turn loop

        internal static void StartPlayerTurn(CombatState c)
        {
            c.Turn += 1;
            c.Phase = V.PhasePlayer;
            var p = c.Player;
            p.Obj(K.Counters).Put(K.CardsPlayedThisTurn, 0);
            var eq = c.Data.Balance.Obj(K.Equipment) ?? new JObject();
            c.SwapsLeft = eq.Str(K.SwapCostKind) == V.SwapAllowance ? eq.Or0(K.SwapAllowancePerTurn) : 0;

            if (!Statuses.GetFlag(c, p, K.RetainBlock)) p.Put(K.Block, 0);
            else
            {
                var cap = Statuses.GetCap(c, p, K.BlockCap);
                if (cap != null) p.Put(K.Block, Math.Min(p.Num(K.Block), cap.Value));
            }

            p.Put(K.Energy, Math.Max(0, p.Num(K.EnergyMax) - p.Or0(K.PendingActionLoss)));
            p.Put(K.PendingActionLoss, 0);
            Ratings.RecoverMeters(c, p);

            Actions.DrawCards(c, HandRules.TurnDrawCount(c));

            c.Emit(E.PlayerTurnStart, Js.Obj(K.Turn, c.Turn));
            Triggers.FireOwnerHooks(c, p, E.OwnerTurnStart);
            DrainQueue(c);
        }

        private static void EndPlayerTurn(CombatState c, IReadOnlyList<string> discardIds)
        {
            var p = c.Player;
            c.Emit(E.PlayerTurnEnd, Js.Obj(K.Turn, c.Turn));
            Triggers.FireOwnerHooks(c, p, E.OwnerTurnEnd);
            DrainQueue(c);
            if (c.Result != null) return;

            Statuses.DecayAtTurnEnd(c, p);

            var counters = p.Obj(K.Counters);
            if (Js.IsFinite(p[K.MaxStamina]) && p.Num(K.MaxStamina) > 0)
            {
                var next = Equipment.StaminaTurnEnd(c.Data, p.Num(K.Stamina), p.Num(K.MaxStamina), counters.Or0(K.StaminaSpentThisTurn));
                if (next != p.Num(K.Stamina))
                {
                    var amount = next - p.Num(K.Stamina);
                    p.Put(K.Stamina, next);
                    c.Emit(E.StaminaRecovered, Js.Obj(K.Amount, amount, K.Reason, V.ReasonIdle));
                }
            }
            counters.Put(K.StaminaSpentThisTurn, 0);

            HandRules.ApplyDiscardChoice(c, discardIds);
            var keep = new List<JObject>();
            var toDiscard = new List<JObject>();
            var toExhaust = new List<JObject>();
            foreach (var card in c.Piles.Hand)
            {
                var fate = HandRules.EndTurnCardFate(c, card);
                if (fate == V.Keep) keep.Add(card);
                else if (fate == V.Exhaust) toExhaust.Add(card);
                else toDiscard.Add(card);
            }
            c.Piles.Hand = keep;
            foreach (var card in toExhaust)
            {
                c.Piles.Exhaust.Add(card);
                c.Emit(E.CardExhausted, Js.Obj(K.CardInstanceId, card[K.InstanceId], K.CardId, card[K.CardId], K.Reason, V.ReasonEthereal));
            }
            foreach (var card in toDiscard)
            {
                c.Piles.Discard.Add(card);
                c.Emit(E.CardDiscarded, Js.Obj(K.CardInstanceId, card[K.InstanceId], K.CardId, card[K.CardId], K.Reason, V.ReasonTurnEnd));
            }
            p.Put(K.Energy, 0);
            DrainQueue(c);
        }

        private static void EnemyPhase(CombatState c)
        {
            c.Phase = V.PhaseEnemy;
            c.Emit(E.EnemyTurnStart, Js.Obj(K.Turn, c.Turn));
            foreach (var e in c.Enemies)
            {
                if (!e.Is(K.Alive)) continue;
                if (!Statuses.GetFlag(c, e, K.RetainBlock)) e.Put(K.Block, 0);
            }
            DrainQueue(c);
            if (c.Result != null) return;

            foreach (var enemy in c.Enemies.ToList())
            {
                if (c.Result != null) return;
                if (!enemy.Is(K.Alive)) continue;
                Triggers.FireOwnerHooks(c, enemy, E.OwnerTurnStart);
                DrainQueue(c);
                if (c.Result != null) return;
                if (!enemy.Is(K.Alive)) continue;

                if (enemy.Is(K.SkipNextTurn) || Statuses.GetFlag(c, enemy, K.SkipTurn))
                {
                    enemy.Put(K.SkipNextTurn, false);
                }
                else if (Js.Truthy(enemy[K.PendingMove]))
                {
                    var pending = enemy.Obj(K.PendingMove);
                    if (c.Turn >= pending.Num(K.ResolveOnTurn))
                    {
                        var def = c.Data.Enemies.Get(enemy.Str(K.EnemyId));
                        var moveId = pending.Str(K.MoveId);
                        var move = def.Obj(K.Moves).Obj(moveId);
                        enemy[K.PendingMove] = Js.Null();
                        ExecuteMovePayload(c, enemy, move, moveId);
                    }
                }
                else if (Js.Truthy(enemy[K.Intent]) && Js.Truthy(enemy.Obj(K.Intent)[K.MoveId]))
                {
                    var def = c.Data.Enemies.Get(enemy.Str(K.EnemyId));
                    var moveId = enemy.Obj(K.Intent).Str(K.MoveId);
                    var move = def.Obj(K.Moves).Obj(moveId);
                    if (Js.Truthy(move[K.Delay]))
                    {
                        var delay = move.Obj(K.Delay);
                        var wc = delay?.Obj(K.WhileCharging) ?? new JObject();
                        if (!Js.Nullish(wc[K.Block]))
                            c.Enqueue(new CombatAction { Effect = Js.Obj(K.Op, Op.Block, K.Target, V.TargetSelf, K.Amount, wc[K.Block].DeepClone()), Source = enemy, Owner = enemy, Target = c.Player, Meta = ActionMeta.Empty() });
                        foreach (var eff in Js.Items(wc[K.Effects]).OfType<JObject>())
                            c.Enqueue(new CombatAction { Effect = eff, Source = enemy, Owner = enemy, Target = c.Player, Meta = ActionMeta.Empty() });
                        enemy[K.PendingMove] = Js.Obj(K.MoveId, moveId, K.ResolveOnTurn, c.Turn + Js.Coalesce(delay?[K.Turns], 1));
                        var intent = Js.Spread(enemy.Obj(K.Intent));
                        intent.Put(K.Pending, true);
                        enemy[K.Intent] = intent;
                    }
                    else ExecuteMovePayload(c, enemy, move, moveId);
                }
                DrainQueue(c);
                if (c.Result != null) return;

                if (enemy.Is(K.Alive))
                {
                    Triggers.FireOwnerHooks(c, enemy, E.OwnerTurnEnd);
                    DrainQueue(c);
                    if (c.Result != null) return;
                    if (enemy.Is(K.Alive)) Statuses.DecayAtTurnEnd(c, enemy);
                }
            }

            c.Emit(E.EnemyTurnEnd, Js.Obj(K.Turn, c.Turn));
            DrainQueue(c);
        }

        private static void ExecuteMovePayload(CombatState c, JObject enemy, JObject move, string moveId)
        {
            if (!Js.Truthy(enemy[K.PerformedMoves])) enemy[K.PerformedMoves] = new JArray();
            enemy.Arr(K.PerformedMoves).Add(moveId);
            c.Emit(E.EnemyMoveStarted, Js.Obj(K.SourceId, enemy[K.Id], K.EnemyId, enemy[K.EnemyId], K.MoveId, moveId, K.Kind, move[K.Intent]));
            if (!Js.Nullish(move[K.Damage]))
            {
                var effect = Js.Obj(K.Op, Op.Damage, K.Target, V.RefPlayer, K.Amount, move[K.Damage].DeepClone(),
                    K.Hits, Js.Nullish(move[K.Hits]) ? Js.N(1) : move[K.Hits].DeepClone());
                if (Js.Truthy(move[K.DamageSchool])) effect[K.DamageSchool] = move[K.DamageSchool].DeepClone();
                c.Enqueue(new CombatAction { Effect = effect, Source = enemy, Owner = enemy, Target = c.Player, Meta = new ActionMeta { MoveId = moveId } });
            }
            if (!Js.Nullish(move[K.Block]))
                c.Enqueue(new CombatAction { Effect = Js.Obj(K.Op, Op.Block, K.Target, V.TargetSelf, K.Amount, move[K.Block].DeepClone()), Source = enemy, Owner = enemy, Target = c.Player, Meta = new ActionMeta { MoveId = moveId } });
            foreach (var eff in Js.Items(move[K.Effects]).OfType<JObject>())
                c.Enqueue(new CombatAction { Effect = eff, Source = enemy, Owner = enemy, Target = c.Player, Meta = new ActionMeta { MoveId = moveId } });
        }

        // ------------------------------------------------------------------ enemy AI

        internal static void RollIntents(CombatState c, bool isFirstTurn = false)
        {
            foreach (var enemy in c.Enemies)
            {
                if (!enemy.Is(K.Alive)) continue;
                if (Js.Truthy(enemy[K.PendingMove]))
                {
                    if (Js.Truthy(enemy[K.Intent]))
                    {
                        var intent = Js.Spread(enemy.Obj(K.Intent));
                        intent.Put(K.Pending, true);
                        enemy[K.Intent] = intent;
                    }
                    continue;
                }
                if (enemy.Is(K.SkipNextTurn) || Statuses.GetFlag(c, enemy, K.SkipTurn))
                {
                    enemy[K.Intent] = Js.Obj(K.Kind, V.IntentStaggered, K.MoveId, Js.Null());
                    continue;
                }
                var def = c.Data.Enemies.Get(enemy.Str(K.EnemyId));
                var moveId = isFirstTurn && Js.Truthy(def[K.FirstMove]) ? def.Str(K.FirstMove) : WeightedMovePick(c, enemy, def);
                if (moveId == null)
                {
                    enemy[K.Intent] = Js.Obj(K.Kind, V.IntentUnknown, K.MoveId, Js.Null());
                    continue;
                }
                enemy.Arr(K.MovesHistory).Add(moveId);
                enemy[K.Intent] = BuildIntent(def.Obj(K.Moves).Obj(moveId), moveId);
            }
        }

        private static string WeightedMovePick(CombatState c, JObject enemy, JObject def)
        {
            var unlocked = enemy.Arr(K.UnlockedMoves);
            var entries = def.Obj(K.Moves).Properties()
                .Where(p => !((JObject)p.Value).Is(K.Locked) || Js.Includes(unlocked, p.Name))
                .Select(p => new KeyValuePair<string, JObject>(p.Name, (JObject)p.Value)).ToList();
            if (entries.Count == 0) return null;
            var history = enemy.Arr(K.MovesHistory);
            var eligible = entries.Where(kv =>
            {
                if (Js.Nullish(kv.Value[K.MaxConsecutive])) return true;
                var run = 0;
                for (var i = history.Count - 1; i >= 0; i--)
                {
                    if (Js.Str(history[i]) == kv.Key) run++;
                    else break;
                }
                return run < kv.Value.Num(K.MaxConsecutive);
            }).ToList();
            var pool = eligible.Count > 0 ? eligible : entries;
            double total = 0;
            foreach (var kv in pool) total += kv.Value.Num(K.Weight);
            if (total <= 0) return pool[0].Key;
            var r = c.Rng.Float(RngStream.EnemyAI) * total;
            foreach (var kv in pool)
            {
                r -= kv.Value.Num(K.Weight);
                if (r < 0) return kv.Key;
            }
            return pool[pool.Count - 1].Key;
        }

        private static JObject BuildIntent(JObject move, string moveId)
        {
            var hasDamage = !Js.Nullish(move[K.Damage]);
            return new JObject
            {
                [K.Kind] = move[K.Intent]?.DeepClone(),
                [K.MoveId] = moveId,
                [K.Damage] = hasDamage ? move[K.Damage].DeepClone() : Js.Null(),
                [K.Hits] = hasDamage ? (Js.Nullish(move[K.Hits]) ? Js.N(1) : move[K.Hits].DeepClone()) : Js.Null(),
                [K.Block] = !Js.Nullish(move[K.Block]) ? move[K.Block].DeepClone() : Js.Null(),
                [K.Delayed] = Js.Truthy(move[K.Delay]),
                [K.Pending] = false,
            };
        }

        // ------------------------------------------------------------------ player intents

        private static bool NeedsEnemyTarget(JObject def) => Js.Items(def[K.Effects]).OfType<JObject>().Any(e => e.Str(K.Target) == V.RefEnemy);

        /// <summary>The numeric energy cost after relic passives (X-cost excluded: it spends everything).</summary>
        public static double EffectiveCost(CombatState c, JObject def)
        {
            var reduction = Cards.PassiveSum(c.Data, c.Player.Arr(K.RelicIds), K.PowerCostReduction, c.ItemUpgradeLevels ?? new JObject(), Properties.MountsOf(c, c.Player));
            return Framework.Costs(c.Data, def, reduction, Equipment.PlayerWeightClass(c).WeightClass).Action;
        }

        private static void PlayCard(CombatState c, CombatCommand command)
        {
            if (c.Phase != V.PhasePlayer) throw new InvalidOperationException(M.NotPlayerTurnForCards);
            var p = c.Player;
            var idx = c.Piles.Hand.FindIndex(card => card.Str(K.InstanceId) == command.CardInstanceId);
            if (idx < 0) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.CardNotInHand, command.CardInstanceId));
            var inst = c.Piles.Hand[idx];
            var def = Cards.Resolve(c.Data, inst);
            if (Framework.IsUnplayable(c.Data, def)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.CardUnplayable, def.Str(K.Name)));

            var isX = def.Str(K.Cost) == V.XCost;
            var cost = isX ? p.Num(K.Energy) : EffectiveCost(c, def);
            var pools = Framework.Costs(c.Data, def, 0, Equipment.PlayerWeightClass(c).WeightClass);
            var manaCost = pools.Mana;
            var staminaCost = pools.Stamina;
            if (p.Num(K.Energy) < cost) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.NotEnoughEnergy, cost, p.Num(K.Energy)));
            if (p.Num(K.Mana) < manaCost) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.NotEnoughMana, manaCost, p.Num(K.Mana)));
            if (p.Num(K.Stamina) < staminaCost) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.NotEnoughStamina, staminaCost, p.Num(K.Stamina)));

            JObject target = null;
            if (command.TargetId != null)
            {
                target = Triggers.FindEntity(c, command.TargetId);
                if (target == null || !target.Is(K.Alive)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.InvalidTarget, command.TargetId));
            }
            else if (NeedsEnemyTarget(def))
            {
                target = c.Enemies.FirstOrDefault(e => e.Is(K.Alive)) ?? throw new InvalidOperationException(M.NoLivingEnemy);
            }

            var kind = Cards.Kind(c.Data, def);
            var derivedTags = Equipment.GripTags(c.Data, c.Loadout, p.Str(K.ClassId));
            var ownTags = !Js.Nullish(def[K.CardTags]) ? def[K.CardTags] : (def[K.Tags] is JArray t && t.Count > 0 ? def[K.Tags] : null);
            var cardRef = Js.Obj(
                K.SourceArmamentId, Js.Truthy(inst[K.SourceArmamentId]) ? inst[K.SourceArmamentId] : inst[K.WeaponId],
                K.RatingId, inst[K.RatingId],
                K.RatingValue, inst[K.RatingValue],
                K.RatingCap, inst[K.RatingCap],
                K.EquipmentRole, inst[K.EquipmentRole],
                K.InstanceId, inst[K.InstanceId],
                K.CardId, inst[K.CardId],
                K.Upgraded, inst[K.Upgraded],
                K.Type, kind,
                K.Tags, ownTags,
                K.Attack, def[K.Attack],
                K.SourceHand, inst[K.SourceHand],
                K.DerivedTags, derivedTags,
                K.AuthoredTags, ownTags ?? new JArray(),
                K.GrantedBy, Js.Truthy(inst[K.GrantedBy]) ? inst[K.GrantedBy] : null,
                K.DamageSchool, !Js.Nullish(inst[K.DamageSchool]) ? inst[K.DamageSchool] : def[K.DamageSchool],
                K.ExposureBuildupPerHit, !Js.Nullish(inst[K.ExposureBuildupPerHit]) ? inst[K.ExposureBuildupPerHit] : def[K.ExposureBuildupPerHit],
                K.CardRatingValues, Js.Truthy(def[K.CardRatingValues]) ? def[K.CardRatingValues] : null);

            p.Put(K.Energy, p.Num(K.Energy) - cost);
            if (cost > 0 || isX) c.Emit(E.EnergySpent, Js.Obj(K.Amount, cost));
            p.Put(K.Mana, p.Num(K.Mana) - manaCost);
            if (manaCost > 0) c.Emit(E.ManaSpent, Js.Obj(K.Amount, manaCost));
            p.Put(K.Stamina, p.Num(K.Stamina) - staminaCost);
            var counters = p.Obj(K.Counters);
            if (staminaCost > 0)
            {
                counters.Put(K.StaminaSpentThisTurn, counters.Or0(K.StaminaSpentThisTurn) + staminaCost);
                c.Emit(E.StaminaSpent, Js.Obj(K.Amount, staminaCost));
            }

            c.Piles.Hand.Remove(inst);
            counters.Put(K.CardsPlayedThisTurn, counters.Num(K.CardsPlayedThisTurn) + 1);
            counters.Put(K.CardsPlayedThisCombat, counters.Num(K.CardsPlayedThisCombat) + 1);
            var meta = new ActionMeta
            {
                EnergySpent = cost,
                ManaSpent = manaCost,
                StaminaSpent = staminaCost,
                OrdinalThisTurn = counters.Num(K.CardsPlayedThisTurn),
                OrdinalThisCombat = counters.Num(K.CardsPlayedThisCombat),
            };
            if (kind == V.KindAttack)
            {
                counters.Put(K.AttacksPlayedThisCombat, counters.Num(K.AttacksPlayedThisCombat) + 1);
                meta.AttackOrdinal = counters.Num(K.AttacksPlayedThisCombat);
            }
            foreach (var effect in Js.Items(def[K.Effects]).OfType<JObject>())
                c.Enqueue(new CombatAction { Effect = effect, Source = p, Owner = p, Target = target, Card = cardRef, Meta = meta });
            c.Emit(E.CardPlayed, Js.Obj(
                K.CardInstanceId, inst[K.InstanceId], K.CardId, inst[K.CardId], K.CardType, kind,
                K.CardTags, (cardRef[K.Tags] as JArray)?.DeepClone() ?? new JArray(), K.DerivedTags, derivedTags.DeepClone(),
                K.TargetId, target != null ? target[K.Id] : Js.Null(),
                K.OrdinalThisTurn, meta.OrdinalThisTurn.Value, K.OrdinalThisCombat, meta.OrdinalThisCombat.Value,
                K.EnergySpent, cost, K.ManaSpent, manaCost, K.StaminaSpent, staminaCost));
            DrainQueue(c);

            if (c.Result == null)
            {
                var destination = Framework.AfterPlayDestination(c.Data, def);
                if (destination == V.ExhaustPile)
                {
                    c.Piles.Exhaust.Add(inst);
                    c.Emit(E.CardExhausted, Js.Obj(K.CardInstanceId, inst[K.InstanceId], K.CardId, inst[K.CardId], K.Reason, V.ReasonPlayed));
                }
                else if (destination == V.HandZone) c.Piles.Hand.Add(inst);
                else if (destination != V.RemovedFromPlay) c.Piles.Discard.Add(inst);
                DrainQueue(c);
            }
        }

        private static void EndTurn(CombatState c, IReadOnlyList<string> discardIds)
        {
            if (c.Phase != V.PhasePlayer) throw new InvalidOperationException(M.NotPlayerTurn);
            HandRules.ValidateDiscardChoice(c, discardIds);
            EndPlayerTurn(c, discardIds);
            if (c.Result != null) return;
            EnemyPhase(c);
            if (c.Result != null) return;
            RollIntents(c);
            StartPlayerTurn(c);
        }

        /// <summary>The flask backing a charge-pool kind: the first authored flask of that kind (rules/combatEngine.json flasks).</summary>
        private static string ChargeFlaskId(CombatState c, string kind)
        {
            var cfg = c.Data.Engine.Obj(K.Flasks);
            if (kind == null || !Js.Includes(cfg?[K.ChargeKinds], kind)) return null;
            foreach (var def in c.Data.Flasks.All)
            {
                string defKind = null;
                if (Js.IsStr(def[K.Kind])) defKind = def.Str(K.Kind);
                else
                    foreach (var rule in Js.Items(cfg[K.KindByOp]).OfType<JObject>())
                        if (Js.Items(def[K.Effects]).OfType<JObject>().Any(e => e.Str(K.Op) == rule.Str(K.Op))) { defKind = rule.Str(K.Kind); break; }
                if (defKind == kind) return def.Str(K.Id);
            }
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.MissingChargeFlask, kind));
        }

        private static void UseFlask(CombatState c, CombatCommand command)
        {
            if (c.Phase != V.PhasePlayer) throw new InvalidOperationException(M.NotPlayerTurnForFlasks);
            var p = c.Player;
            var chargeId = ChargeFlaskId(c, command.ChargeKind);
            var currentKey = command.ChargeKind + V.CurrentSuffix;
            var charges = p.Obj(K.FlaskCharges);
            if (chargeId != null && (charges == null || charges.Num(currentKey) <= 0))
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.NoFlaskCharges, command.ChargeKind));
            var flasks = p.Arr(K.Flasks);
            string flaskId;
            if (chargeId != null) flaskId = chargeId;
            else
            {
                var slot = flasks != null && command.Slot >= 0 && command.Slot < flasks.Count ? flasks[command.Slot] as JObject : null;
                flaskId = slot?.Str(K.FlaskId) ?? throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.NoFlaskInSlot, command.Slot));
            }
            var def = c.Data.Flasks.Get(flaskId);
            JObject target = null;
            if (command.TargetId != null)
            {
                target = Triggers.FindEntity(c, command.TargetId);
                if (target == null || !target.Is(K.Alive)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.InvalidTarget, command.TargetId));
            }
            else if (def.Is(K.Targeted)) target = c.Enemies.FirstOrDefault(e => e.Is(K.Alive));
            if (chargeId != null) charges.Put(currentKey, charges.Num(currentKey) - 1);
            else flasks.RemoveAt(command.Slot);
            c.Emit(E.FlaskUsed, Js.Obj(K.FlaskId, flaskId, K.Slot, chargeId == null ? (object)(double)command.Slot : null, K.TargetId, target != null ? target[K.Id] : Js.Null()));
            var amountMult = Cards.PassiveMult(c.Data, p.Arr(K.RelicIds), K.FlaskPowerMult, Properties.MountsOf(c, p));
            foreach (var eff in Js.Items(def[K.Effects]).OfType<JObject>())
                c.Enqueue(new CombatAction { Effect = eff, Source = p, Owner = p, Target = target, Meta = amountMult != 1 ? new ActionMeta { AmountMult = amountMult } : ActionMeta.Empty() });
            DrainQueue(c);
        }
    }
}
