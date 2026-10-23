using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CM = Ashen.Generated.Core2CombatMessages;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DV = Ashen.Generated.Core2DamageValues;
using EV = Ashen.Generated.Core2EnemyValues;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using V = Ashen.Generated.CombatValues;
using RK = Ashen.Generated.Core2ReactionKeys;
using RV = Ashen.Generated.Core2ReactionValues;
using SV = Ashen.Generated.Core2StatusValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The turn loop of a Core 2 fight with no card play (web engine/combat.js startPlayerTurn, endPlayerTurn, enemyTurnStart,
    /// enemyOwnerTurnStart, enemyPhase, finishReactionPhase and doEndTurn): the player's turn opens (Block lost, Stamina and the hand
    /// refilled, the owner's cycle begun), ends (the turn-end hooks, the Precision decay, the hand returned), and the enemies take
    /// their turn. An enemy that acts (a move played, a delayed move resolving, a planned follow-up) belongs to WP-6 to WP-11 and is
    /// refused by name; an enemy that is Staggered, held by a Control status or told to skip its turn takes its turn start and end and
    /// nothing between, which is exactly the end-turn loop WP-4 ports.
    /// </summary>
    public static class CoreTurns
    {
        private static bool Expanded(CombatState c) => Js.D(c.Fields[CK.CombatExpansionVersion]) == c.CoreData.ExpandedVersion;

        // ------------------------------------------------------------------ shared pieces

        /// <summary>passiveMax(registries, relicIds, key, mounts): the largest relic or mounted passive of that key, at least 0.</summary>
        private static double PassiveMax(CombatState c, JObject entity, string key)
        {
            double max = 0;
            foreach (var id in Js.Items(entity[K.RelicIds]).Select(Js.Str))
            {
                var value = c.Data.Relics.Get(id).Obj(K.Passives)?[key];
                if (Js.IsFinite(value) && Js.D(value) > max) max = Js.D(value);
            }
            var mounts = Properties.MountsOf(c, entity);
            if (mounts != null)
                foreach (var entry in mounts.Entries())
                    foreach (var rule in entry.Value.Rules)
                    {
                        var value = rule.Obj(K.Passives)?[key];
                        if (Js.IsFinite(value) && Js.D(value) > max) max = Js.D(value);
                    }
            return max;
        }

        /// <summary>startTacticalTurn(ctx, entity): the owner's cycle advances and what the last cycle prepared is cleared.</summary>
        private static void StartTacticalTurn(CombatState c, JObject entity)
        {
            if (!Expanded(c) || entity == null) return;
            entity.Put(CK.CombatOwnerCycle, entity.Or0(CK.CombatOwnerCycle) + 1);
            CoreCounters.Clear(entity, c);
            entity.Remove(CK.CombatEvade);
            entity.Remove(CK.CombatTraitGuard);
            var stance = entity.Obj(CK.CombatStance);
            if (stance?[CK.ExpiresOnOwnerCycle] != null && entity.Num(CK.CombatOwnerCycle) >= stance.Num(CK.ExpiresOnOwnerCycle)) entity.Remove(CK.CombatStance);
        }

        /// <summary>fireOwnerHooks: status and stance hooks of the owner's turn; a status that drains Ward every turn (flux) spends it first.</summary>
        private static void FireOwnerHooks(CombatState c, JObject entity, string hook) => CoreStatusHooks.FireOwner(c, entity, hook);

        // ------------------------------------------------------------------ the player's turn

        /// <summary>startPlayerTurn(combat): turn counter, Block, Stamina, the draw, the turn-start triggers.</summary>
        public static void StartPlayerTurn(CombatState c)
        {
            c.Turn += 1;
            CoreCardZones.ReturnHidden(c);
            c.Phase = V.PhasePlayer;
            c.Fields.Remove(CK.EnemyReactionRolls);
            var p = c.Player;
            CoreStatuses.DecayAtTurnStart(c, p);
            BeginAbilityTurn(p);
            p.Remove(CK.CombatRetainedCards);
            p.Obj(K.Counters).Put(K.CardsPlayedThisTurn, 0);
            var eq = c.Data.Balance.Obj(K.Equipment) ?? new JObject();
            c.SwapsLeft = eq.Str(K.SwapCostKind) == V.SwapAllowance ? eq.Or0(K.SwapAllowancePerTurn) : 0;

            if (!CoreStatuses.Flag(c, p, K.RetainBlock)) p.Put(K.Block, Math.Min(p.Num(K.Block), Math.Max(CoreProposal.Retained(c, p), PassiveMax(c, p, CK.RetainBlockUpTo))));
            else
            {
                var cap = CoreActionsCap(c, p);
                if (cap != null) p.Put(K.Block, Math.Min(p.Num(K.Block), cap.Value));
            }
            p.Remove(CK.PoiseGuard);
            p.Remove(CK.WardGuard);
            CoreCounters.Clear(p, c);
            if (Expanded(c))
            {
                p.Put(CK.Barrier, 0);
                StartTacticalTurn(c, p);
                BeginAshenBlightCycle(c, p, p.Num(CK.CombatOwnerCycle).ToString(CultureInfo.InvariantCulture));
            }

            p.Put(K.Stamina, Math.Max(0, p.Num(K.MaxStamina) - p.Or0(K.PendingActionLoss)));
            c.Fields[CK.GameProfileCommands] = 0;
            var resources = c.CoreData.Configured(CK.ResourcesModule);
            if (!Js.Includes(resources?[CK.Include], CV.PoolSpShort)) p.Put(K.Stamina, 0);
            if (resources != null && Js.Truthy(resources[CK.ManaPerTurn])) p.Put(K.Mana, Math.Min(p.Num(K.MaxMana), p.Num(K.Mana) + resources.Num(CK.ManaPerTurn)));
            c.EquipmentPoolDeficits.Put(K.Stamina, p.Num(K.MaxStamina) - p.Num(K.Stamina));
            p.Put(K.PendingActionLoss, 0);

            var penalty = Js.Items(p.Obj(CK.AshenBlight)?[CK.Milestones]).Any() ? throw new NotSupportedException(CM.AshenBlightNotPorted) : 0;
            CoreHand.Draw(c, Math.Max(1, CoreHand.TurnDrawCount(c) - (c.Turn == 1 ? penalty : 0)));

            c.Emit(E.PlayerTurnStart, Js.Obj(K.Turn, c.Turn));
            CoreProposal.Turn(c, p, SV.Start);
            FireOwnerHooks(c, p, E.OwnerTurnStart);
            CoreActions.Drain(c);
            if (Expanded(c) && c.Result == null)
            {
                CoreControl.RecoverAtOwnerStart(c, p, p.Num(CK.CombatOwnerCycle));
                CoreControl.SleepRestoration(c, p);
                p.Put(K.Stamina, Math.Max(0, p.Num(K.Stamina) - CoreControl.ConsumeActionLoss(c, p)));
            }
        }

        private static double? CoreActionsCap(CombatState c, JObject entity)
        {
            double? cap = null;
            foreach (var id in entity.Obj(K.Statuses).Properties().Select(p => p.Name).ToList())
            {
                var mods = CoreStatuses.Definition(c, id).Obj(K.Modifiers);
                if (Js.IsNum(mods?[K.BlockCap])) cap = cap == null ? mods.Num(K.BlockCap) : Math.Max(cap.Value, mods.Num(K.BlockCap));
            }
            return cap;
        }

        /// <summary>beginAbilityTurn(entity): the per-turn ability counters restart; the combat-long ones carry.</summary>
        private static void BeginAbilityTurn(JObject entity)
        {
            var old = entity.Obj(CK.AbilityRiders) ?? new JObject();
            entity[CK.AbilityRiders] = new JObject
            {
                [CK.CardsPlayed] = 0,
                [CK.TagPlays] = new JObject(),
                [CK.DistinctTagPlays] = new JObject(),
                [CK.ManaSpent] = 0,
                [CK.Discarded] = 0,
                [CK.HpLostSinceTurnStart] = Js.Or0(old[CK.HpLostThisRound]),
                [CK.HpLostThisRound] = 0,
                [CK.OfferingsPaid] = 0,
                [CK.PreviousSpell] = 0,
                [CK.TurnStartHpPct] = entity.Num(K.MaxHp) > 0 ? Js.N(Ashen.Generated.CombatMath.Percent * entity.Num(K.Hp) / entity.Num(K.MaxHp)) : Js.N(0),
                [CK.Charges] = new JObject(),
                [CK.Once] = new JObject(),
                [CK.InstanceUsesCombat] = old[CK.InstanceUsesCombat]?.DeepClone() ?? new JObject(),
                [CK.CardUsesCombat] = old[CK.CardUsesCombat]?.DeepClone() ?? new JObject(),
                [CK.TypePlays] = new JObject(),
                [CK.SameCardPlays] = new JObject(),
                [CK.AttacksPlayed] = 0,
                [CK.CardPlaysCombat] = Js.Or0(old[CK.CardPlaysCombat]),
            };
        }

        /// <summary>beginAshenBlightCycle(ctx, owner, cycleKey): the Ashen Blight procs of one owner cycle are unspent.</summary>
        private static void BeginAshenBlightCycle(CombatState c, JObject owner, string cycleKey)
        {
            var state = owner.Obj(CK.AshenBlightCombat) ?? new JObject { [CK.CycleKey] = Js.Null(), [CK.Used] = new JObject { [CK.Martial] = false, [CK.Spell] = false, [CK.Survivor] = false }, [CK.WardCarry] = 0 };
            if (state.Str(CK.CycleKey) != cycleKey)
            {
                state[CK.CycleKey] = cycleKey;
                state[CK.Used] = new JObject { [CK.Martial] = false, [CK.Spell] = false, [CK.Survivor] = false };
            }
            owner[CK.AshenBlightCombat] = state;
        }

        // ------------------------------------------------------------------ ending the turn

        /// <summary>endPlayerTurn(combat, discardIds): the turn-end hooks and decay; the hand is dealt with after the enemies (reactions are on).</summary>
        private static void EndPlayerTurn(CombatState c, IReadOnlyList<string> discardIds)
        {
            var p = c.Player;
            var preserveLockedHand = CoreControl.Locked(c, p);
            c.Emit(E.PlayerTurnEnd, Js.Obj(K.Turn, c.Turn));
            CoreProposal.Turn(c, p, SV.End);
            FireOwnerHooks(c, p, E.OwnerTurnEnd);
            CoreActions.Drain(c);
            if (c.Result != null) return;
            // Each card still in hand fires its authored turn-end effects (a Curse's Health loss) before the hand is dealt with.
            var firedInHand = false;
            foreach (var card in c.Piles.Hand.ToList())
            {
                var hook = CoreCosts.Resolve(c, card).Arr(CK.OnTurnEndInHand);
                if (hook == null || hook.Count == 0) continue;
                foreach (var eff in hook.OfType<JObject>())
                    c.Enqueue(new CombatAction { Effect = eff, Source = p, Owner = p, Target = p, Meta = new ActionMeta { CardInstanceId = card.Str(K.InstanceId), CardId = card.Str(K.CardId), Trigger = SV.TurnEndInHand } });
                firedInHand = true;
            }
            if (firedInHand)
            {
                CoreActions.Drain(c);
                if (c.Result != null) return;
            }
            CoreStatuses.DecayAtTurnEnd(c, p);
            p.Obj(K.Counters).Put(K.StaminaSpentThisTurn, 0);
            CoreHand.ApplyDiscardChoice(c, discardIds);
            c.Fields[CK.ReactionHandCleanup] = new JObject { [CK.PreserveLockedHand] = preserveLockedHand };
            if (Expanded(c)) CoreControl.EndTurn(c, p);
        }

        /// <summary>enemyTurnStart(combat): the start event, then each living enemy loses its Block and what it prepared.</summary>
        private static void EnemyTurnStart(CombatState c)
        {
            c.Emit(E.EnemyTurnStart, Js.Obj(K.Turn, c.Turn));
            foreach (var e in c.Enemies)
            {
                if (!e.Is(K.Alive)) continue;
                if (!CoreStatuses.Flag(c, e, K.RetainBlock)) e.Put(K.Block, 0);
                e.Remove(CK.PoiseGuard);
                e.Remove(CK.WardGuard);
                CoreCounters.Clear(e);
                if (Expanded(c))
                {
                    e.Put(CK.Barrier, 0);
                    StartTacticalTurn(c, e);
                }
            }
            CoreActions.Drain(c);
        }

        /// <summary>enemyOwnerTurnStart(combat, enemy): the owner's own turn begins; a held enemy is revealed to the player.</summary>
        private static void EnemyOwnerTurnStart(CombatState c, JObject enemy)
        {
            CoreStatuses.DecayAtTurnStart(c, enemy);
            if (CoreFixCore.Enabled(c))
            {
                var def = c.Data.Enemies.Get(enemy.Str(K.EnemyId));
                var pool = Js.Nullish(enemy[K.MaxStamina]) ? CoreIntents.Pool(c, def) : enemy.Num(K.MaxStamina);
                enemy.Put(CK.FixCoreStaminaRemaining, Math.Max(0, pool - enemy.Or0(CK.PendingStaminaLoss)));
                enemy.Put(CK.PendingStaminaLoss, 0);
            }
            CoreBurdens.Begin(c, enemy, CoreIntents.Pool(c, c.Data.Enemies.Get(enemy.Str(K.EnemyId))));
            enemy.Put(CK.ActorIntentRevealed, true);
            c.Emit(CV.EventEnemyActorTurnStarted, Js.Obj(K.SourceId, enemy[K.Id], K.EnemyId, enemy[K.EnemyId], K.MoveId, Js.Truthy(enemy.Obj(K.PendingMove)?[K.MoveId]) ? enemy.Obj(K.PendingMove)[K.MoveId] : enemy.Obj(K.Intent)?[K.MoveId]));
            CoreProposal.Turn(c, enemy, SV.Start);
            FireOwnerHooks(c, enemy, E.OwnerTurnStart);
            CoreActions.Drain(c);
            if (c.Result != null || !enemy.Is(K.Alive)) return;
            if (Expanded(c))
            {
                CoreControl.RecoverAtOwnerStart(c, enemy, enemy.Num(CK.CombatOwnerCycle));
                CoreControl.SleepRestoration(c, enemy);
            }
        }

        /// <summary>
        /// enemyPhase(combat): each enemy's turn. A reaction offer pauses the phase where it stands: the cursor (which enemy, which stage of its turn)
        /// is kept in the state, and the phase is entered again from it once the offer is answered.
        /// </summary>
        private static void EnemyPhase(CombatState c)
        {
            var cursor = c.Fields.Obj(DK.ReactionCursor);
            if (cursor == null)
            {
                c.Phase = V.PhaseEnemy;
                c.Fields.Remove(CK.FollowTrace);
                EnemyTurnStart(c);
                if (c.Result != null) return;
            }
            if (cursor == null)
            {
                cursor = new JObject { [RK.Index] = 0, [RK.Stage] = RV.StageStart };
                if (CoreReactions.Active(c)) c.Fields[DK.ReactionCursor] = cursor;
            }
            for (; cursor.Num(RK.Index) < c.Enemies.Count; cursor[RK.Index] = Js.N(cursor.Num(RK.Index) + 1), cursor[RK.Stage] = RV.StageStart)
            {
                var enemy = c.Enemies[(int)cursor.Num(RK.Index)];
                if (c.Result != null) return;
                if (!enemy.Is(K.Alive)) continue;
                if (cursor.Str(RK.Stage) == RV.StageStart)
                {
                    EnemyOwnerTurnStart(c, enemy);
                    if (c.Result != null) return;
                    if (!enemy.Is(K.Alive)) continue;
                    cursor[RK.Stage] = RV.StagePayload;
                }
                if (cursor.Str(RK.Stage) == RV.StagePayload)
                {
                    cursor[RK.Stage] = RV.StageDrain;
                    if (enemy.Is(K.SkipNextTurn) || CoreStatuses.Flag(c, enemy, K.SkipTurn) || CoreControl.Locked(c, enemy) || CoreControl.ConsumeActionLoss(c, enemy) > 0)
                    {
                        if (!Js.Truthy(enemy[K.PendingMove])) CoreKnowledge.Cancel(c, enemy);
                        enemy.Put(K.SkipNextTurn, false);
                        enemy.Put(CK.PlanDue, false);
                    }
                    else if (Js.Truthy(enemy[K.PendingMove]))
                    {
                        // A delayed move resolving: the committed attack lands regardless of newly rolled intents.
                        enemy.Put(CK.PlanDue, false);
                        if (c.Turn >= enemy.Obj(K.PendingMove).Num(K.ResolveOnTurn))
                        {
                            var def = c.Data.Enemies.Get(enemy.Str(K.EnemyId));
                            var moveId = enemy.Obj(K.PendingMove).Str(K.MoveId);
                            enemy[K.PendingMove] = Js.Null();
                            CoreEnemyMoves.Execute(c, enemy, def.Obj(K.Moves).Obj(moveId), moveId);
                        }
                        // else: still charging (delay.turns > 1): the enemy does nothing.
                    }
                    else if (Js.Truthy(enemy[K.Intent]) && Js.Truthy(enemy.Obj(K.Intent)[K.MoveId]))
                    {
                        var def = c.Data.Enemies.Get(enemy.Str(K.EnemyId));
                        var moveId = enemy.Obj(K.Intent).Str(K.MoveId);
                        var move = CoreIntents.Expanded(c, enemy, def.Obj(K.Moves).Obj(moveId), moveId);
                        if (Js.Truthy(move[K.Delay]))
                        {
                            // A charging move takes the enemy's turn: its planned follow-ups do not play this turn.
                            enemy.Put(CK.PlanDue, false);
                            enemy[CK.PlannedMoves] = new JArray();
                            CoreEnemyMoves.StartDelayed(c, enemy, move, moveId);
                        }
                        else CoreEnemyMoves.Execute(c, enemy, move, moveId);
                    }
                }
                CoreActions.Drain(c);
                if (CoreReactions.Paused(c)) return;
                if (c.Result != null) return;
                // Each move leaves the plan before it runs, so a reaction pause resumes at the next planned move.
                while (enemy.Is(K.Alive) && enemy.Is(CK.PlanDue) && !Js.Truthy(enemy[K.PendingMove]) && enemy.Arr(CK.PlannedMoves)?.Count > 0)
                {
                    if (CoreEnemyMoves.Halted(c, enemy)) { enemy.Put(CK.PlanDue, false); break; }
                    var plan = enemy.Arr(CK.PlannedMoves);
                    var next = (JObject)plan[0];
                    plan.RemoveAt(0);
                    var def = c.Data.Enemies.Get(enemy.Str(K.EnemyId));
                    CoreIntents.Present(c, enemy, def, next.Str(K.MoveId));
                    var traced = new JObject { [DK.Read] = CoreEnemyMoves.PerceptionRead(c, enemy), [DK.Outcome] = DV.OutcomePlayed };
                    var trace = c.Fields.Obj(CK.FollowTrace);
                    if (trace == null) c.Fields[CK.FollowTrace] = trace = new JObject();
                    var rows = trace.Arr(enemy.Str(K.Id));
                    if (rows == null) trace[enemy.Str(K.Id)] = rows = new JArray();
                    rows.Add(traced);
                    var move = CoreIntents.Expanded(c, enemy, def.Obj(K.Moves).Obj(next.Str(K.MoveId)), next.Str(K.MoveId));
                    if (Js.Truthy(move[K.Delay]))
                    {
                        enemy[CK.PlannedMoves] = new JArray();
                        traced[DK.Outcome] = EV.OutcomeCharging;
                        CoreEnemyMoves.StartDelayed(c, enemy, move, next.Str(K.MoveId));
                    }
                    else traced[DK.Outcome] = CoreEnemyMoves.Execute(c, enemy, move, next.Str(K.MoveId)) ?? DV.OutcomePlayed;
                    CoreActions.Drain(c);
                    if (CoreReactions.Paused(c)) return;
                    if (c.Result != null) return;
                }
                if (enemy.Arr(CK.PlannedMoves)?.Count == 0) enemy.Put(CK.PlanDue, false);
                if (enemy.Is(K.Alive))
                {
                    CoreBurdens.End(c, enemy);
                    CoreProposal.Turn(c, enemy, SV.End);
                    FireOwnerHooks(c, enemy, E.OwnerTurnEnd);
                    CoreActions.Drain(c);
                    if (c.Result != null) return;
                    if (enemy.Is(K.Alive)) CoreStatuses.DecayAtTurnEnd(c, enemy);
                    if (Expanded(c)) CoreControl.EndTurn(c, enemy);
                }
            }
            c.Emit(E.EnemyTurnEnd, Js.Obj(K.Turn, c.Turn));
            CoreActions.Drain(c);
            c.Fields.Remove(DK.ReactionCursor);
        }

        /// <summary>doEndTurn(combat, discardIds): the player's turn ends, the enemies take theirs, the next turn begins (unless a reaction offer pauses the enemy phase).</summary>
        public static void EndTurn(CombatState c, IReadOnlyList<string> discardIds)
        {
            if (c.Phase != V.PhasePlayer) throw new InvalidOperationException(M.NotPlayerTurn);
            c.RecordSkillXp = true;
            CoreHand.ValidateDiscardChoice(c, discardIds);
            EndPlayerTurn(c, discardIds);
            if (c.Result != null) return;
            EnemyPhase(c);
            if (CoreReactions.Paused(c)) return;
            if (c.Result != null) return;
            FinishReactionPhase(c);
        }

        /// <summary>
        /// resumeEnemyTurn(combat): the offer was answered (or a card choice made); the fight goes on from where it paused: the queue is drained, and
        /// the enemy phase resumed from its cursor, then the player's hand and turn.
        /// </summary>
        public static void Resume(CombatState c)
        {
            if (CoreCardZones.HasChoice(c) || c.Fields[DK.ReactionResume] != null) return;
            if (c.Fields[DK.ReactionCursor] == null)
            {
                CoreActions.Drain(c);
                CoreCardPlay.FinishPlay(c);
                return;
            }
            EnemyPhase(c);
            if (c.Result != null || CoreReactions.Paused(c)) return;
            FinishReactionPhase(c);
        }

        /// <summary>finishReactionPhase(combat): the player's hand is dealt with after the enemies, new intents are rolled and the next turn begins.</summary>
        private static void FinishReactionPhase(CombatState c)
        {
            var cleanup = c.Fields.Obj(CK.ReactionHandCleanup);
            if (cleanup != null)
            {
                CoreHand.FinishPlayerHand(c, cleanup.Is(CK.PreserveLockedHand));
                c.Fields.Remove(CK.ReactionHandCleanup);
                if (c.Result != null) return;
            }
            CoreIntents.Roll(c);
            StartPlayerTurn(c);
        }
    }
}
