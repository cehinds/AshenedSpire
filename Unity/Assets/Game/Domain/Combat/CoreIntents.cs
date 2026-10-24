using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CM = Ashen.Generated.Core2CombatMessages;
using CV = Ashen.Generated.Core2CombatValues;
using EK = Ashen.Generated.Core2EnemyKeys;
using MI = Ashen.Generated.Core2ModuleIds;
using Core2RngStream = Ashen.Generated.Core2RngStream;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;
using RngStream = Ashen.Generated.RngStream;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// An enemy's turn plan and the intent it shows (web engine/combat.js rollIntents, presentIntent, weightedMovePick, buildIntent;
    /// engine/combatRoundsAdapter.js takePlannedMove and extendEnemyQueue; model/actionCosts.js enemyPoolOf and enemyMoveSpOf;
    /// engine/combatCardTactics.js expandedEnemyMove, enemyMoveCarrier and primeEnemyCounter). Each enemy plans its SP pool with the
    /// weighted pick on the <c>enemyAI</c> stream; the head of the plan is this turn's intent, rolled for visibility, and the rest plays
    /// in order during the enemy phase. Playing a move (WP-6 to WP-11) is not here: only the planning, the telegraph and the stance the
    /// telegraph puts the enemy in.
    /// </summary>
    public static class CoreIntents
    {
        // ------------------------------------------------------------------ SP: the pool and the price of a move

        /// <summary>enemyAttributesOf(def): the authored attributes, shifted when the enemy is blighted.</summary>
        public static JObject AttributesOf(CombatState c, JObject def)
        {
            var authored = def.Obj(K.Attributes) != null ? Js.Spread(def.Obj(K.Attributes)) : new JObject();
            if (!Js.Truthy(def[CK.Blighted])) return authored;
            var shift = c.CoreData.ActionCost(CK.EnemyBlightShift) as JObject ?? new JObject();
            foreach (var p in shift.Properties()) authored.Put(p.Name, Math.Max(0, Js.Or0(authored[p.Name]) + Js.D(p.Value)));
            return authored;
        }

        /// <summary>enemyPoolOf(def): the SP an enemy plans a turn with (a fixed spPool replaces the row), at least 0.</summary>
        public static double Pool(CombatState c, JObject def)
        {
            if (Js.IsInt(def[CK.SpPool]) && def.Num(CK.SpPool) >= 0) return def.Num(CK.SpPool);
            var attributes = AttributesOf(c, def);
            var row = c.CoreData.ActionCost(CK.EnemySpRow) as JObject;
            var sp = row.Num(CK.Base);
            foreach (var p in row.Properties()) if (p.Name != CK.Base) sp += Math.Floor(Js.Or0(attributes[p.Name]) * Js.D(p.Value));
            var equipment = Js.IsFinite(def[CK.EquipmentSp]) ? def.Num(CK.EquipmentSp) : 0;
            return Math.Max(0, Math.Floor(sp + equipment));
        }

        private static string MoveKind(JObject move)
        {
            if (!Js.Nullish(move[K.Damage]) || move.Str(K.Intent) == CV.IntentAttack) return CV.KindAttack;
            if (move.Str(K.Intent) == CV.IntentBlock || !Js.Nullish(move[K.Block])) return CV.KindGuard;
            if (move.Str(K.Intent) == CV.StanceCounter) return CV.StanceCounter;
            return CV.KindOther;
        }

        /// <summary>actionStance({kind, tags}): the row of the action-cost table a move prices as.</summary>
        private static string ActionStance(CombatState c, string kind, List<string> tags)
        {
            var map = c.CoreData.Section(CK.ActionStances);
            foreach (var rule in Js.Items(map[CK.ByTag]).OfType<JObject>())
                if (Js.Items(rule[CK.Tags]).Any(t => tags.Contains(Js.Str(t)))) return rule.Str(CK.Stance);
            if (kind == CV.KindAttack && (tags.Contains(CV.TagSmashBare) || tags.Contains(CV.TagManeuverSmash))) return CV.StanceSmash;
            if (kind == CV.KindAttack && (tags.Contains(CV.TagSweepBare) || tags.Contains(CV.TagManeuverSweep))) return CV.StanceSweep;
            return map.Obj(CK.ByKind)?.Str(kind);
        }

        /// <summary>enemyMoveSpOf(move, kind, fallback, {entity, enemyId, moveId, ctx}) with the paying enemy, as combat prices a move.</summary>
        public static double MoveSp(CombatState c, JObject move, JObject enemy, string moveId)
        {
            var effects = Js.Items(move[K.Effects]).ToList();
            if (CoreFixCore.Enabled(c)
                && (Js.D(move[CK.Heal]) > 0 || effects.Any(e => Js.Str(Js.Get(e, K.Op)) == Op.Heal))
                && (Js.D(move[K.Block]) > 0 || effects.Any(e => Js.Str(Js.Get(e, K.Op)) == Op.Block)))
                return CoreFixCore.Rules(c, enemy).Num(CK.EnemyHealBlockCost);
            double baseCost;
            if (!Js.Nullish(move[CK.Sp]) && Js.IsFinite(move[CK.Sp])) baseCost = Math.Max(0, move.Num(CK.Sp));
            else
            {
                var tags = CoreStances.TagIds(move) ?? new List<string>();
                var table = c.CoreData.ActionCost(CK.ActionCostTable) as JObject;
                var stance = ActionStance(c, MoveKind(move), tags);
                baseCost = stance != null && table[stance] != null ? table.Num(stance) : c.CoreData.Section(CK.Plan).Num(CK.MoveSpFallback);
            }
            var carrier = Js.Spread(move);
            carrier[K.EnemyId] = enemy.Str(K.EnemyId);
            carrier[K.MoveId] = moveId;
            return CoreStances.StaminaCost(c, baseCost, enemy, carrier) + SpellSurcharge(c, enemy, carrier);
        }

        /// <summary>spellCardCostSurcharge(entity, card): one SP per Silenced stack on a spell-camp action.</summary>
        private static double SpellSurcharge(CombatState c, JObject entity, JObject carrier)
        {
            var spell = carrier.Obj(CK.CombatProfile) != null && Js.Truthy(carrier[CK.CombatProfile])
                ? carrier.Obj(CK.CombatProfile).Str(CK.Camp) == CV.CampSpell
                : CoreStances.IsSpellCard(carrier) || CoreStances.ProfileFor(c, carrier).Str(CK.Camp) == CV.CampSpell;
            if (!spell) return 0;
            var stacks = entity.Obj(K.Statuses)?.Obj(CV.StatusSilenced)?.Num(K.Stacks) ?? 0;
            return Js.IsFinite(new JValue(stacks)) && stacks > 0 ? Math.Floor(stacks) : 0;
        }

        // ------------------------------------------------------------------ the move as the enemy plays it

        /// <summary>enemyMoveDamage(enemy, move): the authored damage scaled by the enemy's damage multiplier (rounded, at least 1).</summary>
        public static JToken MoveDamage(JObject enemy, JObject move)
        {
            if (move == null || Js.Nullish(move[K.Damage])) return Js.Null();
            var mult = enemy != null && Js.IsFinite(enemy[CK.DamageMult]) ? enemy.Num(CK.DamageMult) : 1;
            if (mult == 1 || !(move.Num(K.Damage) > 0)) return move[K.Damage].DeepClone();
            return Js.N(Math.Max(1, Math.Floor(move.Num(K.Damage) * mult + Ashen.Generated.CombatMath.Half)));
        }

        /// <summary>enemySmashFallbackPoise(move, profile, ctx): the Poise a Smash that authors none deals.</summary>
        private static JToken SmashFallbackPoise(CombatState c, JObject move, JObject profile)
        {
            var effects = Js.Items(move[K.Effects]).ToList();
            if (profile?.Str(CK.Maneuver) != CV.StanceSmash || !(!Js.Nullish(move[K.Damage]) || effects.Any(e => Js.Str(Js.Get(e, K.Op)) == Op.Damage))
                || effects.Any(e => Js.Str(Js.Get(e, K.Op)) == Op.PoiseDamage)) return null;
            return CoreStances.Rules(c)[CK.FallbackPoise].DeepClone();
        }

        /// <summary>expandedEnemyMove(enemy, move, moveId, ctx): the move with its range, targeting, break pressure and counter coverage stated.</summary>
        public static JObject Expanded(CombatState c, JObject enemy, JObject move, string moveId)
        {
            var probe = Js.Spread(move);
            probe[K.EnemyId] = enemy.Str(K.EnemyId);
            probe[K.MoveId] = moveId;
            var profile = CoreStances.ProfileFor(c, probe);
            var maneuver = profile.Str(CK.Maneuver);
            var range = Js.Truthy(profile[CK.Range]) ? profile.Str(CK.Range) : (maneuver == CV.StanceRanged || profile.Str(CK.Camp) == CV.CampSpell ? CV.RangeReach : CV.RangeMelee);
            var targeting = Js.Truthy(profile[CK.Targeting]) ? profile.Str(CK.Targeting) : (maneuver == CV.StanceSweep ? CV.TargetingArea : CV.TargetingSingle);
            var result = Js.Spread(move);
            result[CK.Range] = range;
            result[CK.Targeting] = targeting;
            if (maneuver == CV.StanceSmash) result[CK.BreakPoiseBonus] = !Js.Nullish(move[CK.BreakPoiseBonus]) ? move[CK.BreakPoiseBonus].DeepClone() : Js.N(c.CoreData.Section(CK.Plan).Num(CK.BreakPoiseBonus));
            if (!Js.Truthy(profile[CK.Maneuver])) result[CK.StanceTrigger] = false;
            var pressure = c.CoreData.Section(CK.EnemyPressure);
            var effects = new JArray();
            foreach (var effect in Js.Items(move[K.Effects]))
            {
                var row = effect as JObject;
                var rule = row != null && Js.IsStr(row[K.Status]) ? pressure.Obj(row.Str(K.Status)) : null;
                if (row != null && row.Str(K.Op) == Op.ApplyStatus && rule != null && row.Str(K.Target) != V.TargetSelf && row.Str(K.Target) != V.RefOwner)
                {
                    var rest = Js.Spread(row);
                    var stacks = rest[K.Stacks];
                    rest.Remove(K.Stacks);
                    rest.Remove(K.Status);
                    foreach (var p in rule.Properties()) rest[p.Name] = p.Value.DeepClone();
                    rest[K.Op] = CV.OpBuildup;
                    rest[CK.Camp] = Js.Truthy(profile[CK.Camp]) ? profile[CK.Camp].DeepClone() : Js.S(CV.CampPhysical);
                    var scale = c.CoreData.Section(CK.Plan).Num(CK.PressureScale);
                    rest[CK.Amount] = Js.IsNum(stacks) ? Js.N(Js.D(stacks) * scale) : Js.Obj(CK.F, CV.FormulaMul, CK.Args, new JArray(Js.Nullish(stacks) ? Js.N(1) : stacks.DeepClone(), Js.N(scale)));
                    effects.Add(rest);
                }
                else if (row != null && row.Str(K.Op) == Op.Block && profile.Str(CK.Camp) == CV.CampSpell && !CoreFixCore.Enabled(c))
                {
                    var barrier = Js.Spread(row);
                    barrier[K.Op] = CV.OpGainBarrier;
                    effects.Add(barrier);
                }
                else effects.Add(effect.DeepClone());
            }
            result[K.Effects] = effects;
            if (profile.Str(CK.Camp) == CV.CampSpell && !Js.Nullish(move[K.Block]) && !CoreFixCore.Enabled(c))
            {
                result.Remove(K.Block);
                result[CK.Barrier] = Js.N(Js.Or0(move[CK.Barrier]) + move.Num(K.Block));
            }
            var fallbackPoise = SmashFallbackPoise(c, result, profile);
            if (fallbackPoise != null) effects.Add(Js.Obj(K.Op, Op.PoiseDamage, K.Target, V.RefPlayer, CK.Amount, fallbackPoise));
            if (maneuver == CV.StanceRanged) result[CK.Projectile] = true;
            var delay = move.Obj(K.Delay);
            if (delay?.Obj(CK.WhileCharging) != null)
            {
                var charging = Js.Spread(delay.Obj(CK.WhileCharging));
                // { ...whileCharging, tags: move.tags, cardTags: move.cardTags }: an undefined value is no key.
                if (move[K.Tags] != null) charging[K.Tags] = move[K.Tags].DeepClone(); else charging.Remove(K.Tags);
                if (move[CK.CardTags] != null) charging[CK.CardTags] = move[CK.CardTags].DeepClone(); else charging.Remove(CK.CardTags);
                var next = Js.Spread(delay);
                next[CK.WhileCharging] = Expanded(c, enemy, charging, moveId);
                result[K.Delay] = next;
            }
            if (maneuver == CV.StanceCounter && !Js.Truthy(result[CK.CounterCoverage]))
            {
                var ranged = profile.Str(CK.CounterMode) == CV.RangeRanged;
                var spell = profile.Str(CK.Camp) == CV.CampSpell;
                var cfg = c.CoreData.Section(CK.CounterCoverage);
                var coverage = new JObject
                {
                    [CK.Camps] = new JArray(spell ? CV.CampSpell : CV.CampPhysical),
                    [CK.Ranges] = (JArray)cfg[spell ? CK.SpellRanges : ranged ? CK.RangedRanges : CK.MeleeRanges].DeepClone(),
                    [CK.Targeting] = (JArray)cfg[CK.Targeting].DeepClone(),
                    [CK.Effects] = (JArray)cfg[spell ? CK.SpellEffects : CK.PhysicalEffects].DeepClone(),
                };
                if (!ranged && !spell) coverage[CK.Maneuvers] = (JArray)cfg[CK.MeleeManeuvers].DeepClone();
                result[CK.CounterCoverage] = coverage;
            }
            return result;
        }

        /// <summary>tacticalCarrier + enemyMoveCarrier: the move as a carrier with its combat profile and traits.</summary>
        public static JObject Carrier(CombatState c, JObject enemy, JObject move, string moveId)
        {
            var def = Expanded(c, enemy, move, moveId);
            var carrier = new JObject();
            foreach (var key in Js.Items(c.CoreData.Section(CK.Plan)[CK.CarrierFields]).Select(Js.Str))
                if (def[key] != null) carrier[key] = def[key].DeepClone();
            carrier[K.EnemyId] = enemy.Str(K.EnemyId);
            carrier[K.MoveId] = moveId;
            carrier[K.Type] = CV.KindAttack;
            carrier[K.Tags] = (!Js.Nullish(def[CK.CardTags]) ? def[CK.CardTags] : def[K.Tags])?.DeepClone();
            if (def[K.Attack] != null) carrier[K.Attack] = def[K.Attack].DeepClone();
            if (def[CK.DamageSchool] != null) carrier[CK.DamageSchool] = def[CK.DamageSchool].DeepClone();
            if (Js.IsFinite(def[CK.MagicShare])) carrier[CK.MagicShare] = def[CK.MagicShare].DeepClone();
            if (Js.IsStr(def[CK.PhysicalDamageType])) carrier[CK.PhysicalDamageType] = def[CK.PhysicalDamageType].DeepClone();
            var applied = Js.Items(def[K.Effects]).OfType<JObject>().Where(e => e.Str(K.Op) == Op.ApplyStatus).ToList();
            carrier[CK.AppliedStatuses] = new JArray(applied.Select(e => e[K.Status]?.DeepClone()));
            carrier[CK.AppliedStatusEffects] = new JArray(applied.Select(e => e.DeepClone()));
            carrier[CK.AppliedBuildupStatuses] = new JArray(Js.Items(def[K.Effects]).OfType<JObject>().Where(e => e.Str(K.Op) == CV.OpBuildup).Select(e => e[K.Status]?.DeepClone()));
            carrier[CK.CombatRiderTargets] = new JArray();
            if (def[CK.RatingId] != null) carrier[CK.RatingId] = def[CK.RatingId].DeepClone();
            var profile = Js.Spread(CoreStances.ProfileFor(c, carrier), def.Obj(CK.CombatProfile));
            carrier[CK.CombatProfile] = profile;
            var evade = profile.Obj(CK.Evade);
            if (Js.Truthy(evade?[CK.WhileStatus])) profile[CK.EvadeEligible] = enemy.Obj(K.Statuses)?.Obj(evade.Str(CK.WhileStatus))?.Num(K.Stacks) > 0;
            // expansionCarrierTraits: the traits the attacker itself carries join the move's own.
            var grounded = Js.Truthy(enemy.Obj(CK.CombatTraits)?[CK.Grounded]) || enemy.Obj(K.Statuses)?.Obj(CV.StatusGrounded)?.Num(K.Stacks) > 0;
            var traits = new List<string>(Js.Items(profile[CK.Traits]).Select(Js.Str));
            foreach (var p in (enemy.Obj(CK.CombatTraits) ?? new JObject()).Properties())
                if (p.Value.Type == JTokenType.Boolean && p.Value.Value<bool>() && !(grounded && p.Name == CV.TraitConductive)) traits.Add(p.Name);
            if (grounded) traits.Add(CV.TraitGrounded);
            profile[CK.Traits] = new JArray(traits.Distinct());
            return carrier;
        }

        /// <summary>buildIntent(move, moveId, enemy, combat): the box an intent shows.</summary>
        public static JObject BuildIntent(CombatState c, JObject move, string moveId, JObject enemy)
        {
            var expanded = Expanded(c, enemy ?? new JObject(), move, moveId);
            var hasDamage = !Js.Nullish(expanded[K.Damage]);
            return new JObject
            {
                [CK.CombatProfile] = Carrier(c, enemy ?? new JObject(), expanded, moveId).Obj(CK.CombatProfile),
                [K.Kind] = expanded[K.Intent]?.DeepClone(),
                [K.MoveId] = moveId,
                [K.Damage] = MoveDamage(enemy, expanded),
                [K.Hits] = hasDamage ? (!Js.Nullish(expanded[K.Hits]) ? expanded[K.Hits].DeepClone() : Js.N(1)) : Js.Null(),
                [K.Block] = !Js.Nullish(expanded[K.Block]) ? expanded[K.Block].DeepClone() : Js.Null(),
                [CK.Barrier] = !Js.Nullish(expanded[CK.Barrier]) ? expanded[CK.Barrier].DeepClone() : Js.Null(),
                [K.Delayed] = Js.Truthy(expanded[K.Delay]),
                [K.Pending] = false,
            };
        }

        // ------------------------------------------------------------------ the weighted pick and the plan

        private static JObject Moves(CombatState c, JObject def) => def.Obj(K.Moves) ?? new JObject();

        /// <summary>weightedMovePick(combat, enemy, def, maxSp): reaction moves are never rolled; a move dearer than the SP left is not offered.</summary>
        private static string WeightedMovePick(CombatState c, JObject enemy, JObject pricing, JObject def, double maxSp)
        {
            var unlocked = enemy.Arr(K.UnlockedMoves);
            var entries = Moves(c, def).Properties()
                .Select(p => new KeyValuePair<string, JObject>(p.Name, (JObject)p.Value))
                .Where(kv => !Js.Truthy(kv.Value[CK.Reaction]) && (!kv.Value.Is(K.Locked) || Js.Includes(unlocked, kv.Key)) && MoveSp(c, kv.Value, pricing, kv.Key) <= maxSp)
                .ToList();
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

        /// <summary>extendEnemyQueue(enemy, def, queue, {isFirstTurn, pick}): tops the plan up to the SP pool, each pick recorded in the move history.</summary>
        private static void ExtendQueue(CombatState c, JObject enemy, JObject def, JArray queue, bool isFirstTurn)
        {
            var pool = Pool(c, def);
            var pricing = (JObject)enemy.DeepClone();
            foreach (var entry in queue.OfType<JObject>())
                SetPricingStance(c, pricing, enemy, Moves(c, def).Obj(entry.Str(K.MoveId)), entry.Str(K.MoveId));
            double spent = queue.OfType<JObject>().Sum(entry => entry.Num(CK.Sp));
            var queueMax = c.CoreData.Section(CK.Plan).Num(CK.QueueMax);
            while (queue.Count < queueMax)
            {
                if (queue.Count > 0 && spent >= pool) break;
                var opener = isFirstTurn && queue.Count == 0 && Js.Truthy(def[CK.FirstMove]) ? def.Str(CK.FirstMove) : null;
                var moveId = opener ?? WeightedMovePick(c, enemy, pricing, def, pool - spent);
                if (moveId == null && queue.Count == 0 && opener == null) moveId = WeightedMovePick(c, enemy, pricing, def, double.PositiveInfinity);
                if (moveId == null) break;
                var sp = MoveSp(c, Moves(c, def).Obj(moveId), pricing, moveId);
                if (spent + sp > pool && queue.Count > 0) break;
                spent += sp;
                enemy.Arr(K.MovesHistory).Add(moveId);
                queue.Add(Js.Obj(K.MoveId, moveId, CK.Sp, sp));
                SetPricingStance(c, pricing, enemy, Moves(c, def).Obj(moveId), moveId);
            }
        }

        private static void SetPricingStance(CombatState c, JObject pricing, JObject enemy, JObject move, string moveId)
        {
            var carrier = Js.Spread(move);
            carrier[K.EnemyId] = enemy.Str(K.EnemyId);
            carrier[K.MoveId] = moveId;
            var stance = CoreStances.StanceOf(c, carrier);
            if (stance != null) pricing[CK.CombatStance] = stance;
        }

        /// <summary>takePlannedMove(combat, enemy, def, {isFirstTurn, pick}): tops the plan up and takes its head.</summary>
        public static string TakePlannedMove(CombatState c, JObject enemy, JObject def, bool isFirstTurn)
        {
            if (!(enemy[CK.PlannedMoves] is JArray plan))
            {
                plan = new JArray();
                enemy[CK.PlannedMoves] = plan;
            }
            ExtendQueue(c, enemy, def, plan, isFirstTurn);
            if (plan.Count == 0) return null;
            var head = plan[0];
            plan.RemoveAt(0);
            return Js.Str(Js.Get(head, K.MoveId));
        }

        // ------------------------------------------------------------------ rolling

        /// <summary>rollIntents(combat, isFirstTurn): every living enemy plans its pool and shows the head of the plan.</summary>
        public static void Roll(CombatState c, bool isFirstTurn = false)
        {
            foreach (var enemy in c.Enemies)
            {
                enemy.Remove(CK.ActorIntentRevealed);
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
                if (enemy.Is(K.SkipNextTurn) || CoreStatuses.Flag(c, enemy, K.SkipTurn))
                {
                    enemy[K.Intent] = Js.Obj(K.Kind, V.IntentStaggered, K.MoveId, Js.Null());
                    continue;
                }
                var def = c.Data.Enemies.Get(enemy.Str(K.EnemyId));
                var moveId = TakePlannedMove(c, enemy, def, isFirstTurn);
                if (moveId == null)
                {
                    enemy[K.Intent] = Js.Obj(K.Kind, V.IntentUnknown, K.MoveId, Js.Null());
                    continue;
                }
                Present(c, enemy, def, moveId);
                enemy.Put(CK.PlanDue, true);
                CoreActions.Drain(c);
            }
        }

        /// <summary>presentIntent(combat, enemy, def, moveId): the box, the knowledge read, and the stance the telegraph puts the enemy in.</summary>
        public static void Present(CombatState c, JObject enemy, JObject def, string moveId)
        {
            CoreCounters.Clear(enemy);
            var move = Moves(c, def).Obj(moveId);
            enemy[K.Intent] = BuildIntent(c, move, moveId, enemy);
            if (c.Fields[CK.EnemyKnowledge] != null)
            {
                CoreKnowledge.Roll(c, enemy, Js.Truthy(move[K.Delay]));
                enemy.Remove(CK.IntentRevealed);
            }
            else if (Js.Truthy(enemy.Obj(K.Intent).Obj(CK.CombatProfile)?[CK.Camp]))
            {
                // A configured profile whose intent module reads `exact` shows every intent and skips the hidden-intent roll: enemyIntentVisibility is its own
                // stream, so not drawing it moves no other roll.
                enemy[CK.IntentRevealed] = c.CoreData.Configured(MI.Intent)?.Str(CK.Reads) == CV.VisibilityExact
                    || c.Rng.Float(Core2RngStream.EnemyIntentVisibility) >= HiddenIntentChance(c.Attributes ?? new JObject(), c.Fields.Obj(CK.CombatIntentRules) ?? new JObject());
            }
            else enemy.Remove(CK.IntentRevealed);
            Prime(c, enemy, move, moveId);
        }

        /// <summary>hiddenIntentChance(attributes, rules): the chance an intent stays hidden, lowered by the observer's Wisdom and Intelligence and held between the rules' bounds.</summary>
        public static double HiddenIntentChance(JObject attributes, JObject rules)
        {
            var chance = rules.Num(EK.BaseHiddenChance) - Js.Or0(attributes[CK.AttrWisdom]) * rules.Num(EK.WisdomReduction) - Js.Or0(attributes[CK.AttrIntelligence]) * rules.Num(EK.IntelligenceReduction);
            return Math.Max(rules.Num(EK.MinimumHiddenChance), Math.Min(rules.Num(EK.MaximumHiddenChance), chance));
        }

        /// <summary>primeEnemyCounter(ctx, enemy, move, moveId): the stance the telegraph puts the enemy in; a Counter move arms its reply and guards.</summary>
        private static void Prime(CombatState c, JObject enemy, JObject move, string moveId)
        {
            var expanded = Expanded(c, enemy, move, moveId);
            var carrier = Carrier(c, enemy, expanded, moveId);
            CoreStances.SetProfileStance(c, enemy, carrier);
            var profile = carrier.Obj(CK.CombatProfile);
            if (profile.Str(CK.Maneuver) != CV.StanceCounter) return;
            var payload = profile.Obj(CK.CounterPayload);
            var damage = !Js.Nullish(expanded[CK.CounterDamage]) ? expanded[CK.CounterDamage] : expanded[K.Damage];
            var poiseDamage = CounterPoiseDamage(c, expanded, profile);
            var withDamage = Js.Spread(expanded);
            if (damage != null) withDamage[K.Damage] = damage.DeepClone();
            var options = new JObject
            {
                [K.Damage] = damage == null ? Js.N(0) : MoveDamage(enemy, withDamage),
                [CK.PoiseDamage] = poiseDamage,
                [CK.WardDamage] = Js.N(Js.Or0(expanded[CK.CounterWardDamage])),
            };
            if (payload != null)
            {
                var merged = Js.Spread(payload);
                merged[CK.Poise] = poiseDamage;
                options[CK.Payload] = merged;
            }
            CoreCounters.Arm(c, enemy, carrier, options);
            if (expanded.Num(K.Block) > 0)
                c.Enqueue(new CombatAction { Effect = Js.Obj(K.Op, Op.Block, K.Target, V.TargetSelf, K.Amount, expanded[K.Block].DeepClone()), Source = enemy, Owner = enemy, Target = enemy, Card = carrier,
                    Meta = new ActionMeta { MoveId = moveId, Preparing = true } });
            if (expanded.Num(CK.Barrier) > 0)
                c.Enqueue(new CombatAction { Effect = Js.Obj(K.Op, CV.OpGainBarrier, K.Target, V.TargetSelf, K.Amount, expanded[CK.Barrier].DeepClone()), Source = enemy, Owner = enemy, Target = enemy, Card = carrier,
                    Meta = new ActionMeta { MoveId = moveId, Preparing = true } });
            if (enemy.Obj(K.Intent)?.Str(K.MoveId) == moveId) enemy.Obj(K.Intent)[CK.CounterDefensePrimed] = true;
        }

        private static JToken CounterPoiseDamage(CombatState c, JObject move, JObject profile)
        {
            var authoredToken = profile?.Obj(CK.CounterPayload)?[CK.Poise] ?? move[CK.CounterPoiseDamage];
            var authored = Js.Nullish(authoredToken) ? 0 : Js.D(authoredToken);
            var hasPoise = Js.Items(move[K.Effects]).Any(e => Js.Str(Js.Get(e, K.Op)) == Op.PoiseDamage);
            return authored > 0 || hasPoise ? Js.N(authored) : CoreStances.Rules(c)[CK.FallbackPoise].DeepClone();
        }
    }
}
