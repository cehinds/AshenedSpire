using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using Pr = Ashen.Generated.CombatPredicates;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>The evaluation context of a gated effect or trigger (shipped pctx).</summary>
    public sealed class PredicateContext
    {
        public JObject Owner;
        public JObject Source;
        public JObject Target;
        public JObject Card;
        public ActionMeta Meta;
        public JObject Event;
    }

    /// <summary>
    /// The event bus, declarative trigger wiring and predicates (shipped engine/triggers.js, SPEC §3.6, §3.9):
    /// relics (through their property mounts), stances, statuses and enemy phases react to events by enqueueing
    /// actions; they never mutate directly. Solo combats only (co-op seats arrive with the co-op feature).
    /// </summary>
    public static class Triggers
    {
        public static JObject EmitEvent(CombatState c, string type, JObject payload)
        {
            var ev = new JObject { [K.Type] = type };
            if (payload != null) foreach (var p in payload.Properties().ToList()) ev[p.Name] = p.Value;
            c.EventLog.Add(ev);
            c.Buffer?.Add(ev);
            c.EmitDepth += 1;
            if (c.EmitDepth > c.Data.Rule(K.Limits, K.MaxEmitDepth))
            {
                c.EmitDepth = 0;
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.TriggerRecursion, type));
            }
            try
            {
                ScanTriggers(c, ev);
            }
            finally
            {
                c.EmitDepth -= 1;
            }
            return ev;
        }

        /// <summary>The key an entity's trigger gates and property mounts live under (solo: the entity id).</summary>
        public static string OwnerKey(JObject entity) => entity != null ? entity.Str(K.Id) : V.NoOwner;

        private static string GateKey(params string[] parts) => string.Join(V.KeySeparator, parts);

        private static string Index(int i) => i.ToString(CultureInfo.InvariantCulture);

        private static void ScanTriggers(CombatState c, JObject ev)
        {
            var player = c.Player;
            if (player == null) return;
            var type = ev.Str(K.Type);
            var pKey = OwnerKey(player);

            if (Js.Truthy(player[K.StanceId]))
            {
                var stanceId = player.Str(K.StanceId);
                var hooks = c.Data.Stances.Get(stanceId).Arr(K.Hooks);
                for (var i = 0; hooks != null && i < hooks.Count; i++)
                {
                    var trig = (JObject)hooks[i];
                    if (trig.Str(K.On) != type) continue;
                    MaybeFire(c, GateKey(V.StanceGate, pKey, stanceId, Index(i)), trig, player, ev);
                }
            }

            if (c.PropertyMounts != null && c.PropertyMounts.TryGetValue(pKey, out var mounts))
            {
                foreach (var sourceKey in mounts.SortedKeys())
                {
                    if (!mounts.TryGetValue(sourceKey, out var mount)) continue;
                    var index = 0;
                    var relicId = mount.Kind == V.RelicKind ? mount.Id : null;
                    var announce = relicId != null && type != E.RelicTriggered;
                    foreach (var rule in mount.Rules)
                        foreach (var trig in Js.Items(rule[K.Triggers]).OfType<JObject>())
                        {
                            var i = index++;
                            if (trig.Str(K.On) != type) continue;
                            if (MaybeFire(c, GateKey(V.PropertyGate, pKey, sourceKey, Index(i)), trig, player, ev) && announce)
                                EmitEvent(c, E.RelicTriggered, Js.Obj(K.RelicId, relicId));
                        }
                }
            }

            foreach (var entity in new[] { c.Player }.Concat(c.Enemies).ToList())
            {
                if (!entity.Is(K.Alive)) continue;
                var statuses = entity.Obj(K.Statuses);
                foreach (var statusId in statuses.Properties().Select(p => p.Name).ToList())
                {
                    var hooks = c.Data.Statuses.Get(statusId).Arr(K.Hooks);
                    for (var i = 0; hooks != null && i < hooks.Count; i++)
                    {
                        var trig = (JObject)hooks[i];
                        if (trig.Str(K.On) != type) continue;
                        MaybeFire(c, GateKey(V.StatusGate, OwnerKey(entity), statusId, Index(i)), trig, entity, ev);
                    }
                }
            }

            foreach (var enemy in c.Enemies.ToList())
            {
                if (!enemy.Is(K.Alive)) continue;
                var phases = c.Data.Enemies.Get(enemy.Str(K.EnemyId)).Arr(K.Phases);
                for (var i = 0; phases != null && i < phases.Count; i++)
                {
                    var phase = (JObject)phases[i];
                    if (phase.Str(K.On) != type) continue;
                    FirePhase(c, enemy, phase, i, ev);
                }
            }
        }

        /// <summary>fireOwnerHooks: status (and, for the player, stance) hooks declared on ownerTurnStart/ownerTurnEnd.</summary>
        public static void FireOwnerHooks(CombatState c, JObject entity, string hookName)
        {
            if (!entity.Is(K.Alive)) return;
            var synthetic = Js.Obj(K.Type, hookName, K.OwnerId, entity[K.Id]);
            var oKey = OwnerKey(entity);
            var statuses = entity.Obj(K.Statuses);
            foreach (var statusId in statuses.Properties().Select(p => p.Name).ToList())
            {
                var hooks = c.Data.Statuses.Get(statusId).Arr(K.Hooks);
                for (var i = 0; hooks != null && i < hooks.Count; i++)
                {
                    var trig = (JObject)hooks[i];
                    if (trig.Str(K.On) != hookName) continue;
                    MaybeFire(c, GateKey(V.StatusGate, oKey, statusId, Index(i), hookName), trig, entity, synthetic);
                }
            }
            if (entity.Str(K.Kind) == V.Player && Js.Truthy(entity[K.StanceId]))
            {
                var stanceId = entity.Str(K.StanceId);
                var hooks = c.Data.Stances.Get(stanceId).Arr(K.Hooks);
                for (var i = 0; hooks != null && i < hooks.Count; i++)
                {
                    var trig = (JObject)hooks[i];
                    if (trig.Str(K.On) != hookName) continue;
                    MaybeFire(c, GateKey(V.StanceGate, oKey, stanceId, Index(i), hookName), trig, entity, synthetic);
                }
            }
        }

        private static TriggerGate Gate(CombatState c, string key)
        {
            if (!c.TriggerState.TryGetValue(key, out var st))
            {
                st = new TriggerGate();
                c.TriggerState[key] = st;
            }
            return st;
        }

        private static bool MaybeFire(CombatState c, string key, JObject trigger, JObject owner, JObject ev)
        {
            var st = Gate(c, key);
            if (trigger.Is(K.Once) && st.Fires > 0) return false;
            if (st.Turn != c.Turn)
            {
                st.Turn = c.Turn;
                st.TurnFires = 0;
            }
            if (!Js.Nullish(trigger[K.LimitPerTurn]) && st.TurnFires >= trigger.Num(K.LimitPerTurn)) return false;

            var target = ResolveEventEntity(c, ev);
            if (Js.Truthy(trigger[K.If]) && !EvalPredicate(c, trigger.Obj(K.If), new PredicateContext { Owner = owner, Target = target, Event = ev })) return false;
            st.Fires += 1;
            st.TurnFires += 1;
            JObject ratingCard = null;
            if (c.RatingsRules != null && key.StartsWith(V.StatusGate + V.KeySeparator, StringComparison.Ordinal))
            {
                var statuses = owner.Obj(K.Statuses);
                if (statuses != null)
                    foreach (var p in statuses.Properties())
                        if (key.Contains(V.KeySeparator + p.Name + V.KeySeparator))
                        {
                            ratingCard = (p.Value as JObject)?.Obj(K.RatingCard);
                            break;
                        }
            }
            foreach (var eff in Js.Items(trigger[K.Do]).OfType<JObject>())
                c.Enqueue(new CombatAction { Effect = eff, Source = owner, Owner = owner, Target = target, Card = ratingCard, Meta = new ActionMeta { Event = ev } });
            return true;
        }

        private static JObject ResolveEventEntity(CombatState c, JObject ev)
        {
            var id = Js.Truthy(ev[K.TargetId]) ? ev.Str(K.TargetId) : Js.Truthy(ev[K.EnemyId]) ? ev.Str(K.EnemyId) : null;
            return id == null ? null : FindEntity(c, id);
        }

        public static JObject FindEntity(CombatState c, string id)
        {
            if (c.Player != null && c.Player.Str(K.Id) == id) return c.Player;
            foreach (var e in c.Enemies) if (e.Str(K.Id) == id) return e;
            return null;
        }

        /// <summary>checkPhases: hpBelowPct enemy phases, after any HP change (default once).</summary>
        public static void CheckPhases(CombatState c)
        {
            foreach (var enemy in c.Enemies.ToList())
            {
                if (!enemy.Is(K.Alive)) continue;
                var phases = c.Data.Enemies.Get(enemy.Str(K.EnemyId)).Arr(K.Phases);
                for (var i = 0; phases != null && i < phases.Count; i++)
                {
                    var phase = (JObject)phases[i];
                    if (phase.Str(K.On) != E.HpBelowPct) continue;
                    if (enemy.Num(K.Hp) > enemy.Num(K.MaxHp) * phase.Num(K.Pct) / Ashen.Generated.CombatMath.Percent) continue;
                    FirePhase(c, enemy, phase, i, Js.Obj(K.Type, E.HpBelowPct, K.TargetId, enemy[K.Id]));
                }
            }
        }

        private static void FirePhase(CombatState c, JObject enemy, JObject phase, int index, JObject ev)
        {
            var key = GateKey(V.PhaseGate, enemy.Str(K.Id), Index(index));
            var once = !(phase[K.Once] != null && phase[K.Once].Type == JTokenType.Boolean && !phase[K.Once].Value<bool>());
            var st = Gate(c, key);
            if (once && st.Fires > 0) return;
            if (Js.Truthy(phase[K.If]) && !EvalPredicate(c, phase.Obj(K.If), new PredicateContext { Owner = enemy, Target = c.Player, Event = ev })) return;
            st.Fires += 1;
            foreach (var eff in Js.Items(phase[K.Do]).OfType<JObject>())
                c.Enqueue(new CombatAction { Effect = eff, Source = enemy, Owner = enemy, Target = c.Player, Meta = new ActionMeta { Event = ev } });
            var unlocked = enemy.Arr(K.UnlockedMoves);
            foreach (var moveId in Js.Items(phase[K.UnlockMoves]))
                if (!Js.Includes(unlocked, Js.Str(moveId))) unlocked.Add(moveId.DeepClone());
        }

        // ------------------------------------------------------------------ predicates

        public static bool EvalPredicate(CombatState c, JObject pred, PredicateContext pctx)
        {
            pctx ??= new PredicateContext();
            var meta = pctx.Meta ?? ActionMeta.Empty();
            var ev = pctx.Event;
            switch (pred.Str(K.P))
            {
                case Pr.InStance:
                    return c.Player.Str(K.StanceId) != null && c.Player.Str(K.StanceId) == pred.Str(K.Stance);
                case Pr.HasStatus:
                {
                    var ent = ResolveOf(c, pctx, pred.Str(K.Of));
                    var atLeast = Js.Coalesce(pred[K.AtLeast], 1);
                    return ent != null && Statuses.Stacks(ent, pred.Str(K.Status)) >= atLeast;
                }
                case Pr.HasBlock:
                {
                    var ent = ResolveOf(c, pctx, pred.Str(K.Of));
                    return ent != null && ent.Num(K.Block) > 0;
                }
                case Pr.HpBelowPct:
                {
                    var ent = ResolveOf(c, pctx, pred.Str(K.Of));
                    return ent != null && ent.Num(K.Hp) <= ent.Num(K.MaxHp) * pred.Num(K.Pct) / Ashen.Generated.CombatMath.Percent;
                }
                case Pr.FirstCardThisTurn:
                    if (meta.OrdinalThisTurn != null) return meta.OrdinalThisTurn == 1;
                    return c.Player.Obj(K.Counters).Num(K.CardsPlayedThisTurn) == 0;
                case Pr.FirstAttackThisCombat:
                    if (meta.AttackOrdinal != null) return meta.AttackOrdinal == 1;
                    return c.Player.Obj(K.Counters).Num(K.AttacksPlayedThisCombat) == 0;
                case Pr.CardTagIs:
                {
                    var tag = pred.Str(K.Tag);
                    if (pctx.Card != null)
                    {
                        var own = !Js.Nullish(pctx.Card[K.AuthoredTags]) ? pctx.Card[K.AuthoredTags] : pctx.Card[K.Tags];
                        return Js.Includes(own, tag) || Js.Includes(pctx.Card[K.DerivedTags], tag);
                    }
                    if (ev != null) return Js.Includes(ev[K.CardTags], tag) || Js.Includes(ev[K.DerivedTags], tag);
                    return false;
                }
                case Pr.CardTypeIs:
                    if (pctx.Card != null) return pctx.Card.Str(K.Type) == pred.Str(K.Type);
                    return ev != null && ev.Str(K.CardType) == pred.Str(K.Type);
                case Pr.EventIsAttack:
                    return ev != null && ev[K.IsAttack] != null && ev[K.IsAttack].Type == JTokenType.Boolean && ev[K.IsAttack].Value<bool>();
                case Pr.HpDamagePositive:
                    return ev != null && ev.Str(K.Type) == E.DamageDealt && ev.Num(K.Amount) > ev.Num(K.Blocked);
                case Pr.HealPositive:
                    return ev != null && ev.Str(K.Type) == E.Healed && ev.Num(K.Amount) > 0;
                case Pr.ManaPositive:
                    return ev != null && ev.Str(K.Type) == E.ManaRestored && ev.Num(K.Amount) > 0;
                case Pr.EventSourceIsOwner:
                    return ev != null && pctx.Owner != null && ev.Str(K.SourceId) != null && ev.Str(K.SourceId) == pctx.Owner.Str(K.Id);
                case Pr.EventTargetIsOwner:
                    return ev != null && pctx.Owner != null && ev.Str(K.TargetId) != null && ev.Str(K.TargetId) == pctx.Owner.Str(K.Id);
                case Pr.EventStatusIs:
                    return ev != null && ev.Str(K.Status) == pred.Str(K.Status) && ev[K.Status] != null;
                case Pr.EveryNthCardThisCombat:
                {
                    var ordinal = meta.OrdinalThisCombat ?? c.Player.Obj(K.Counters).Num(K.CardsPlayedThisCombat);
                    return ordinal > 0 && ordinal % pred.Num(K.N) == 0;
                }
                case Pr.Random:
                    return c.Rng.Float(Ashen.Generated.RngStream.Misc) * Ashen.Generated.CombatMath.Percent < pred.Num(K.Pct);
                case Pr.SkillLevelAtLeast:
                {
                    var row = c.Skills?.Obj(pred.Str(K.Skill) ?? string.Empty);
                    return (row != null && Js.IsInt(row[K.Level]) ? row.Num(K.Level) : 0) >= pred.Num(K.Level);
                }
                case Pr.ClassLevelAtLeast:
                {
                    var classId = pctx.Owner != null && Js.Truthy(pctx.Owner[K.ClassId]) ? pctx.Owner.Str(K.ClassId) : c.Player?.Str(K.ClassId);
                    var row = classId != null ? c.Skills?.Obj(V.ClassSkillPrefix + V.KeySeparator + classId) : null;
                    return (row != null && Js.IsInt(row[K.Level]) ? row.Num(K.Level) : 0) >= pred.Num(K.Level);
                }
                case Pr.All:
                    return Js.Items(pred[K.Preds]).OfType<JObject>().All(sub => EvalPredicate(c, sub, pctx));
                case Pr.Any:
                    return Js.Items(pred[K.Preds]).OfType<JObject>().Any(sub => EvalPredicate(c, sub, pctx));
                case Pr.Not:
                    return !EvalPredicate(c, pred.Obj(K.Pred), pctx);
                default:
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UnknownPredicate, pred.Str(K.P)));
            }
        }

        private static JObject ResolveOf(CombatState c, PredicateContext pctx, string of)
        {
            switch (of)
            {
                case V.RefPlayer:
                    return c.Player;
                case V.RefSelf:
                    return pctx.Source ?? pctx.Owner ?? c.Player;
                case V.RefOwner:
                    return pctx.Owner ?? pctx.Source;
                case V.RefEnemy:
                case V.RefTarget:
                    return pctx.Target;
                default:
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UnresolvableRef, of));
            }
        }
    }
}
