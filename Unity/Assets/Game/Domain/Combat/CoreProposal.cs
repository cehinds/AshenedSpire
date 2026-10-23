using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using HK = Ashen.Generated.Core2HitKeys;
using DK = Ashen.Generated.Core2DamageKeys;
using DV = Ashen.Generated.Core2DamageValues;
using HV = Ashen.Generated.Core2HitValues;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;
using SV = Ashen.Generated.Core2StatusValues;
using SK = Ashen.Generated.Core2StatusKeys;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The part of the base-card status rules a hit reads (web engine/proposalStatusRuntime.js proposalTargetPlan, model/proposalStatusRules.js
    /// proposalTargetRules and its helpers): the accuracy delta an attacker's statuses add, the fire a Greased target cannot dodge, the
    /// Veil's perception check and the Evade chance a target's statuses carry. A read-only plan: it rolls nothing and changes nothing (the
    /// web's <c>resolve</c> mode, which draws on the <c>misc</c> stream, is not used by a hit and is not ported). The rules are the pinned
    /// profile's <c>baseCardStatusSource.rules</c>; a rule applies only when the profile's capabilities (its modules, elements and
    /// protections) hold every requirement, as <c>proposalRuleEnabled</c> reads them.
    /// </summary>
    public static class CoreProposal
    {
        /// <summary>The rules of the base-card statuses (<c>profile.content.baseCardStatusSource.rules</c>), by status id.</summary>
        public static JObject Rules(CombatState c) => (c.CoreData.Content(Ashen.Generated.Core2ProfileContentKeys.BaseCardStatusSource) as JObject)?.Obj(HK.Rules) ?? new JObject();

        /// <summary>
        /// proposalContext(ctx).capabilities: the profile's active modules and, outside a legacy foundation, no elements and the
        /// protections its defenses module includes. <paramref name="open"/> reads the capabilities of a legacy foundation (every element
        /// and protection allowed), the one switch the web's context has.
        /// </summary>
        public static JObject Capabilities(CombatState c, bool open = false)
        {
            var profile = c.CoreData.Profile;
            if (profile == null) return new JObject();
            var caps = new JObject();
            var modules = new JArray();
            if (profile.Obj(HK.Modules) is JObject all)
                foreach (var m in all.Properties())
                    if (!(m.Value.Type == JTokenType.Boolean && !m.Value.Value<bool>()) && !Js.Nullish(m.Value)) modules.Add(m.Name);
            caps[HK.Modules] = modules;
            var legacy = profile.Obj(HK.Foundation)?.Str(CK.Mode) == CV.ModeLegacy;
            if (!legacy && !open)
            {
                caps[HK.Elements] = new JArray();
                var defenses = profile.Obj(HK.Modules)?.Obj(CK.DefensesModule);
                caps[HK.Protections] = Js.Truthy(defenses?[CK.Include]) ? defenses[CK.Include].DeepClone() : new JArray();
            }
            return caps;
        }

        /// <summary>proposalRuleEnabled(rule, capabilities): every requirement the rule lists is held by the capabilities (a capability list that is absent holds all).</summary>
        public static bool Enabled(JObject rule, JObject capabilities)
        {
            if (rule == null) return false;
            var requires = rule.Obj(HK.Requires);
            if (requires == null) return true;
            foreach (var requirement in requires.Properties())
            {
                var held = capabilities?[requirement.Name];
                if (Js.Nullish(held)) continue;
                foreach (var value in Js.Items(requirement.Value))
                    if (!Js.Includes(held, Js.Str(value))) return false;
            }
            return true;
        }

        private sealed class Active
        {
            public string Status;
            public double Stacks;
            public JObject Behavior;
        }

        /// <summary>active(actor, rules, capabilities): each behavior of each enabled rule whose status the actor holds, in the actor's status order.</summary>
        private static IEnumerable<Active> ActiveBehaviors(JObject actor, JObject rules, JObject capabilities)
        {
            var statuses = actor?.Obj(K.Statuses);
            if (statuses == null) yield break;
            foreach (var entry in statuses.Properties().ToList())
            {
                var stacks = StackCount(entry.Value);
                var rule = rules.Obj(entry.Name);
                if (!(stacks > 0) || !Enabled(rule, capabilities)) continue;
                foreach (var behavior in Js.Items(rule[HK.Behaviors]).OfType<JObject>())
                    yield return new Active { Status = entry.Name, Stacks = StackCount(entry.Value), Behavior = behavior };
            }
        }

        private static double StackCount(JToken instance)
        {
            var value = Js.IsNum(instance) ? Js.D(instance) : Js.D((instance as JObject)?[K.Stacks]);
            return Math.Max(0, double.IsNaN(value) ? 0 : value);
        }

        private static bool Matches(JToken list, string value) => !(list is JArray a && a.Count > 0) || Js.Includes(list, value);

        private static JToken IdOf(JObject actor) => !Js.Nullish(actor?[K.Id]) ? actor[K.Id].DeepClone() : !Js.Nullish(actor?[HK.ActorId]) ? actor[HK.ActorId].DeepClone() : Js.S(HV.ActorDefaultId);

        private static double Perception(JObject actor, JObject rules, JObject capabilities)
        {
            var own = actor?[HK.Perception];
            var value = !Js.Nullish(own) ? Js.D(own) : Js.Or0(actor?.Obj(K.Attributes)?[CK.AttrWisdom]) + Js.Or0(actor?.Obj(K.Attributes)?[CK.AttrIntelligence]);
            if (double.IsNaN(value)) value = 0;
            foreach (var a in ActiveBehaviors(actor, rules, capabilities))
                if (a.Behavior.Str(K.Kind) == HV.KindPerception) value += a.Stacks * a.Behavior.Num(HK.AmountPerStack);
            return value;
        }

        private static List<string> TagsOf(JObject card)
        {
            var tags = !Js.Nullish(card?[CK.CardTags]) ? card[CK.CardTags] : card?[K.Tags];
            return Js.Items(tags).Select(Js.Str).Where(t => t != null).ToList();
        }

        /// <summary>
        /// proposalTargetPlan(ctx, actor, target, card) in its preview form. <paramref name="damageType"/> is proposalDamageType(card) (the
        /// damage-type derivation belongs to the damage receipt, WP-7); a null one falls through to the card's own damage fields and tags,
        /// as the rules read it.
        /// </summary>
        public static JObject TargetPlan(CombatState c, JObject actor, JObject target, JObject card, string damageType, bool openCapabilities = false)
        {
            var rules = Rules(c);
            var capabilities = Capabilities(c, openCapabilities);
            var data = Js.Spread(card);
            var profile = card?.Obj(CK.CombatProfile);
            var range = Js.Truthy(card?[CK.Range]) ? card[CK.Range] : profile?[CK.Range];
            if (range != null) data[CK.Range] = range.DeepClone(); else data.Remove(CK.Range);
            if (profile?[CK.Camp] != null) data[CK.Camp] = profile[CK.Camp].DeepClone(); else data.Remove(CK.Camp);
            data[CK.DamageType] = damageType != null ? Js.S(damageType) : Js.Null();
            return TargetRules(actor, target, data, rules, capabilities);
        }

        // ------------------------------------------------------------------ the plans a hit executes (web model/proposalStatusRules.js)

        private static double Rounded(JToken value) => Math.Max(0, Math.Floor(Js.IsFinite(value) ? Js.D(value) : 0));

        private static string TextOf(JToken id) => Js.IsStr(id) ? Js.Str(id) : id?.ToString();

        private static JObject Change(JObject actor, string status, double delta) => Js.Obj(HK.ActorId, IdOf(actor), K.Status, status, HK.Delta, delta);

        /// <summary>change(actor, status, delta) of the proposal rules: the status the actor loses (or gains) when the plan is applied.</summary>
        public static JObject StatusChange(JObject actor, string status, double delta) => Change(actor, status, delta);

        /// <summary>The extra blow an Extra Weapon status adds to an attack: the card's own first damage effect, else the status's fallback damage.</summary>
        public static JObject ExtraWeapon(JObject actor, JObject card, JObject behavior)
        {
            var hit = Js.Items(card?[K.Effects]).OfType<JObject>().FirstOrDefault(row => row.Str(K.Op) == CV.OpDamage);
            var result = Js.Spread(hit);
            result[K.Op] = CV.OpDamage;
            result[K.Amount] = !Js.Nullish(hit?[K.Amount]) ? hit[K.Amount].DeepClone() : behavior[SK.FallbackDamage]?.DeepClone();
            result[K.Target] = !Js.Nullish(hit?[K.Target]) ? hit[K.Target].DeepClone() : Js.S(V.Enemy);
            result[K.SourceId] = IdOf(actor);
            result[DK.ProposalReactive] = true;
            return result;
        }

        /// <summary>The extra protection an Extra Shield status adds to a defending card: the card's own block (or Barrier), else the status's fallback.</summary>
        public static JObject ExtraShield(JObject actor, JObject card, JObject behavior)
        {
            var protection = Js.Items(card?[K.Effects]).OfType<JObject>().FirstOrDefault(row => row.Str(K.Op) == Ashen.Generated.CombatOps.Block || row.Str(K.Op) == CV.OpGainBarrier);
            return Effect(actor, actor, Js.Obj(K.Op, protection != null ? protection[K.Op] : Js.S(Ashen.Generated.CombatOps.Block), K.Amount, !Js.Nullish(protection?[K.Amount]) ? protection[K.Amount] : behavior[SK.FallbackBlock], DK.ProposalReactive, true));
        }

        private static JObject Effect(JObject actor, JObject target, JObject data)
        {
            var result = Js.Spread(data);
            result[K.SourceId] = IdOf(actor);
            result[HK.TargetId] = IdOf(target);
            return result;
        }

        private static bool HasAnyTag(JObject card, JToken required)
        {
            if (!(required is JArray list) || list.Count == 0) return true;
            var tags = TagsOf(card);
            return list.Any(tag => tags.Contains(Js.Str(tag)));
        }

        /// <summary>proposalActor/proposalAllies: the living combatants of the actor's side.</summary>
        public static List<JObject> Allies(CombatState c, JObject actor) =>
            new[] { c.Player }.Concat(c.Enemies).Where(row => row != null && row.Str(K.Kind) == actor?.Str(K.Kind) && row.Is(K.Alive)).ToList();

        /// <summary>
        /// proposalHitPlan(ctx, source, target, amount, card): the amount a hit deals after the outgoing and incoming status rules (Weak, Mirrors, Intangible,
        /// a fire-greased target, necrotic healing, a guard), with the effects and status changes it asks for. <paramref name="roundDamage"/> false keeps full
        /// precision (a configured receipt floors once, where it is applied).
        /// </summary>
        public static JObject HitPlan(CombatState c, JObject actor, JObject target, double amount, JObject card, bool roundDamage = true)
        {
            card = card ?? new JObject();
            var rules = Rules(c);
            var capabilities = Capabilities(c);
            var reactive = card.Is(DK.ProposalReactive);
            var direct = !reactive;
            var damageType = CoreDamage.ProposalDamageType(c, card);
            var camp = Js.Truthy(card.Obj(CK.CombatProfile)?[CK.Camp]) ? card.Obj(CK.CombatProfile).Str(CK.Camp) : CV.CampPhysical;
            double Quantity(double value) => roundDamage ? Rounded(Js.N(value)) : Math.Max(0, double.IsNaN(value) ? 0 : value);
            var original = Quantity(amount);
            var result = new JObject { [DK.Amount] = Js.N(original), [DK.Healing] = 0, [HK.Unavoidable] = false, [CK.Effects] = new JArray(), [HK.StatusChanges] = new JArray(), [DK.Redirect] = Js.Null() };
            var effects = (JArray)result[CK.Effects];
            var changes = (JArray)result[HK.StatusChanges];
            double outgoing = 1;
            foreach (var a in ActiveBehaviors(actor, rules, capabilities))
            {
                var b = a.Behavior;
                var kind = b.Str(K.Kind);
                if (!reactive && kind == DV.KindOutgoingMultiplier && (!b.Is(DK.DirectOnly) || direct)) outgoing *= b.Num(CK.Multiplier);
                if (kind == DV.KindWeaponBonus && direct && !reactive && HasAnyTag(card, b[DK.RequiredTags]))
                    effects.Add(Effect(actor, target, Js.Obj(K.Op, CV.OpDamage, K.Amount, b[K.Amount], CK.DamageType, b[CK.DamageType], CK.Camp, b[CK.Camp], DK.ProposalReactive, true)));
            }
            var outAmount = Quantity(result.Num(DK.Amount) * outgoing);
            var incomingCap = double.PositiveInfinity;
            double? healingMultiplier = null;
            foreach (var a in ActiveBehaviors(target, rules, capabilities))
            {
                var b = a.Behavior;
                var kind = b.Str(K.Kind);
                if (kind == HV.KindGreaseFire && Matches(b[HK.DamageTypes], damageType))
                {
                    result[HK.Unavoidable] = true;
                    outAmount = Quantity(outAmount * b.Num(CK.Multiplier));
                    effects.Add(Effect(actor, target, Js.Obj(K.Op, Ashen.Generated.CombatOps.ApplyStatus, K.Status, b[DK.BurnStatus], K.Stacks, b[DK.BurnStacks])));
                    if (!reactive)
                        foreach (var ally in Allies(c, target).Where(row => TextOf(IdOf(row)) != TextOf(IdOf(target)) && row.Num(K.Hp) > 0))
                            effects.Add(Effect(actor, ally, Js.Obj(K.Op, CV.OpDamage, K.Amount, Rounded(Js.N(original * b.Num(DK.SplashMultiplier))), CK.DamageType, damageType, CK.Camp, camp, DK.ProposalReactive, true)));
                    changes.Add(Change(target, a.Status, -Math.Min(a.Stacks, b.Num(DK.ConsumeStacks))));
                }
                if (kind == DV.KindIncomingCap && (!b.Is(DK.DirectOnly) || direct)) incomingCap = Math.Min(incomingCap, b.Num(HK.Maximum));
                if (kind == DV.KindDamageToHealing && Matches(b[HK.DamageTypes], damageType)) healingMultiplier = b.Num(CK.Multiplier);
            }
            if (healingMultiplier != null)
            {
                result[DK.Healing] = Js.N(Rounded(Js.N(outAmount * healingMultiplier.Value)));
                outAmount = 0;
            }
            else outAmount = Math.Min(outAmount, incomingCap);
            if (direct && !reactive && outAmount > 0)
            {
                var guards = Allies(c, target).Where(row => TextOf(IdOf(row)) != TextOf(IdOf(target)) && row.Num(K.Hp) > 0).OrderBy(row => TextOf(IdOf(row)), StringComparer.Ordinal).ToList();
                foreach (var guard in guards)
                {
                    var found = false;
                    foreach (var a in ActiveBehaviors(guard, rules, capabilities))
                    {
                        var b = a.Behavior;
                        if (b.Str(K.Kind) != DV.KindGuard) continue;
                        var intercepted = Math.Min(outAmount, a.Stacks * b.Num(HK.AmountPerStack));
                        result[DK.Redirect] = Js.Obj(HK.TargetId, IdOf(guard), K.Amount, intercepted);
                        outAmount -= intercepted;
                        effects.Add(Effect(actor, guard, Js.Obj(K.Op, CV.OpDamage, K.Amount, intercepted, CK.DamageType, damageType, CK.Camp, camp, DK.ProposalReactive, true)));
                        changes.Add(Change(guard, a.Status, -Math.Min(a.Stacks, b.Num(DK.ConsumeStacks))));
                        found = true;
                        break;
                    }
                    if (found) break;
                }
            }
            result[DK.Amount] = Js.N(outAmount);
            return result;
        }

        /// <summary>
        /// proposalAfterHit(ctx, source, target, amount, hpDamage, card): what a hit that connected leaves behind: protection a persistent status spends, a
        /// reflect, an automatic reaction. The effects are queued by <see cref="ApplyPlan"/>.
        /// </summary>
        public static JObject AfterHitPlan(CombatState c, JObject actor, JObject target, double incomingDamage, double hpDamage, JObject card)
        {
            card = card ?? new JObject();
            var rules = Rules(c);
            var capabilities = Capabilities(c);
            var reactive = card.Is(DK.ProposalReactive);
            var direct = !reactive;
            var damageType = CoreDamage.ProposalDamageType(c, card);
            var camp = Js.Truthy(card.Obj(CK.CombatProfile)?[CK.Camp]) ? card.Obj(CK.CombatProfile).Str(CK.Camp) : CV.CampPhysical;
            var range = Js.Truthy(card[CK.Range]) ? card.Str(CK.Range) : card.Obj(CK.CombatProfile)?.Str(CK.Range);
            var result = new JObject { [CK.Effects] = new JArray(), [HK.StatusChanges] = new JArray() };
            var effects = (JArray)result[CK.Effects];
            var changes = (JArray)result[HK.StatusChanges];
            foreach (var a in ActiveBehaviors(target, rules, capabilities))
            {
                var b = a.Behavior;
                var kind = b.Str(K.Kind);
                if (kind == DV.KindPersistentProtection && direct && camp == b.Str(CK.Camp) && incomingDamage >= b.Num(DK.MinimumIncomingDamage))
                    changes.Add(Change(target, a.Status, -Math.Min(a.Stacks, 1)));
                if (!direct || reactive || TextOf(IdOf(actor)) == TextOf(IdOf(target))) continue;
                if (kind == DV.KindReflect && hpDamage > 0)
                    effects.Add(Effect(target, actor, Js.Obj(K.Op, CV.OpDamage, K.Amount, Rounded(Js.N(hpDamage * b.Num(CK.Multiplier))), CK.DamageType, damageType, CK.Camp, camp, DK.ProposalReactive, true)));
                if (kind == DV.KindAutoReaction && incomingDamage > 0 && Matches(b[CK.Ranges], range))
                {
                    var opponents = b.Str(CK.Mode) == DV.ModeSweepReaction ? Allies(c, actor).Where(row => row.Num(K.Hp) > 0).ToList() : new List<JObject> { actor };
                    foreach (var opponent in opponents)
                        effects.Add(Effect(target, opponent, Js.Obj(K.Op, CV.OpDamage, K.Amount, b[K.Amount], CK.DamageType, b[CK.DamageType], CK.Camp, b[CK.Camp], DK.ProposalReactive, true)));
                    changes.Add(Change(target, a.Status, -Math.Min(a.Stacks, b.Num(DK.ConsumeStacks))));
                }
            }
            return result;
        }

        /// <summary>
        /// applyProposalPlan(ctx, plan, source, target, card): the plan's status changes and effects become queued actions (a negative change removes
        /// stacks, a positive one applies them), owned by the entity they name.
        /// </summary>
        public static void ApplyPlan(CombatState c, JObject plan, JObject source, JObject target, JObject card, CoreImpact.Contact contact = null)
        {
            var actors = new[] { c.Player }.Concat(c.Enemies).Where(a => a != null).ToList();
            JObject Find(JToken id) => actors.FirstOrDefault(a => TextOf(a[K.Id]) == TextOf(id)) ?? target ?? source;
            foreach (var change in Js.Items(plan?[HK.StatusChanges]).OfType<JObject>())
            {
                var entity = Find(change[HK.ActorId]);
                var delta = change.Num(HK.Delta);
                var effect = delta < 0
                    ? Js.Obj(K.Op, Ashen.Generated.CombatOps.RemoveStatus, K.Status, change[K.Status], K.Amount, -delta)
                    : Js.Obj(K.Op, Ashen.Generated.CombatOps.ApplyStatus, K.Status, change[K.Status], K.Stacks, delta);
                c.Enqueue(new CombatAction { Effect = effect, Source = entity, Owner = entity, Target = entity, Meta = new ActionMeta { ProposalReactive = true } });
            }
            foreach (var job in Js.Items(plan?[CK.Effects]).OfType<JObject>())
            {
                var effect = Js.Spread(job);
                foreach (var key in new[] { K.SourceId, HK.TargetId, DK.ProposalReactive, CK.DamageType, CK.Camp, DK.Direct }) effect.Remove(key);
                var actor = Find(job[K.SourceId]);
                var recipient = Find(job[HK.TargetId]);
                var reactive = job.Is(DK.ProposalReactive);
                var carrier = Js.Spread(card);
                carrier[DK.ProposalReactive] = reactive;
                carrier[CK.DamageType] = job[CK.DamageType]?.DeepClone() ?? Js.Null();
                carrier[CK.DamageSchool] = Js.Truthy(job[CK.DamageType]) ? job[CK.DamageType].DeepClone() : card?[CK.DamageSchool]?.DeepClone() ?? Js.Null();
                var profile = Js.Spread(card?.Obj(CK.CombatProfile));
                profile[CK.Camp] = Js.Truthy(job[CK.Camp]) ? job[CK.Camp].DeepClone() : Js.Truthy(card?.Obj(CK.CombatProfile)?[CK.Camp]) ? card.Obj(CK.CombatProfile)[CK.Camp].DeepClone() : Js.S(CV.CampPhysical);
                carrier[CK.CombatProfile] = profile;
                carrier[DK.SkipRatingBonus] = reactive || card.Is(DK.SkipRatingBonus);
                c.Enqueue(new CombatAction { Effect = effect, Source = actor, Owner = actor, Target = recipient, Card = carrier, Meta = new ActionMeta { ProposalReactive = reactive, Direct = job[DK.Direct] == null || job.Is(DK.Direct), InheritedContact = contact } });
            }
        }

        /// <summary>
        /// proposalTargetPlan(ctx, actor, target, card, { resolve: true }): the preview, then the rolls it asks for (0..99 on the misc stream: the Veil's
        /// perception check, then the Evade chance), applied to the plan.
        /// </summary>
        public static JObject ResolveTargetPlan(CombatState c, JObject actor, JObject target, JObject card)
        {
            var preview = TargetPlan(c, actor, target, card, CoreDamage.ProposalDamageType(c, card));
            if (Js.Nullish(preview[HK.PerceptionChance]) && !(preview.Num(HK.EvadeChance) > 0)) return preview;
            double? perception = null, evade = null;
            if (!Js.Nullish(preview[HK.PerceptionChance])) perception = Math.Floor(c.Rng.Float(Ashen.Generated.RngStream.Misc) * Ashen.Generated.CombatMath.Percent);
            if (preview.Num(HK.EvadeChance) > 0) evade = Math.Floor(c.Rng.Float(Ashen.Generated.RngStream.Misc) * Ashen.Generated.CombatMath.Percent);
            var data = Js.Spread(card);
            var profile = card?.Obj(CK.CombatProfile);
            var range = Js.Truthy(card?[CK.Range]) ? card[CK.Range] : profile?[CK.Range];
            if (range != null) data[CK.Range] = range.DeepClone(); else data.Remove(CK.Range);
            if (profile?[CK.Camp] != null) data[CK.Camp] = profile[CK.Camp].DeepClone(); else data.Remove(CK.Camp);
            var damageType = CoreDamage.ProposalDamageType(c, card);
            data[CK.DamageType] = damageType != null ? Js.S(damageType) : Js.Null();
            return TargetRules(actor, target, data, Rules(c), Capabilities(c), perception, evade);
        }

        /// <summary>proposalThreshold(ctx, actor): a lethal-threshold status whose stacks reach the actor's HP loses it that much HP.</summary>
        public static JObject ThresholdPlan(CombatState c, JObject actor)
        {
            var effects = new JArray();
            foreach (var a in ActiveBehaviors(actor, Rules(c), Capabilities(c)))
                if (a.Behavior.Str(K.Kind) == DV.KindLethalThreshold && actor.Num(K.Hp) > 0 && a.Stacks >= actor.Num(K.Hp))
                    effects.Add(Effect(actor, actor, Js.Obj(K.Op, DV.OpLoseHp, K.Amount, actor[K.Hp], CK.DamageType, a.Behavior[CK.DamageType], DK.ProposalReactive, true)));
            return new JObject { [CK.Effects] = effects };
        }

        private static double RoundedAmount(JToken value) => Math.Max(0, Math.Floor(Js.IsFinite(value) ? Js.D(value) : 0));

        /// <summary>
        /// proposalTurnRules({ actor, phase, roll }): what the actor's base-card statuses do as its turn starts or ends: a persistent protection refilled,
        /// damage over time, a chance of a status, and the decay of a status that ends with the phase. <paramref name="roll"/> draws a 0..99 roll.
        /// </summary>
        public static JObject TurnPlan(CombatState c, JObject actor, string phase, Func<double> roll)
        {
            var effects = new JArray();
            var statusChanges = new JArray();
            var checks = new JArray();
            var rules = Rules(c);
            var capabilities = Capabilities(c);
            foreach (var a in ActiveBehaviors(actor, rules, capabilities))
            {
                var b = a.Behavior;
                var kind = b.Str(K.Kind);
                if (phase == SV.Start && kind == DV.KindPersistentProtection)
                {
                    var amount = Math.Max(0, a.Stacks - RoundedAmount(actor[b.Str(Ashen.Generated.Core2DeckKeys.Pool) ?? string.Empty]));
                    if (amount != 0) effects.Add(Effect(actor, actor, Js.Obj(K.Op, b.Str(Ashen.Generated.Core2DeckKeys.Pool) == CK.Barrier ? CV.OpGainBarrier : Ashen.Generated.CombatOps.Block, K.Amount, amount)));
                }
                if (kind == SV.TurnDamage && b.Str(K.Phase) == phase)
                    effects.Add(Effect(actor, actor, Js.Obj(K.Op, CV.OpDamage, K.Amount, RoundedAmount(Js.N(a.Stacks * b.Num(HK.AmountPerStack))), CK.DamageType, b[CK.DamageType], CK.Camp, b[CK.Camp], DK.Direct, false, DK.ProposalReactive, true)));
                if (kind == SV.TurnChance && b.Str(K.Phase) == phase)
                {
                    checks.Add(Js.Obj(HK.ActorId, IdOf(actor), K.Status, a.Status, HK.Chance, b[HK.Chance], K.Kind, K.Status));
                    if (roll != null && roll() < b.Num(HK.Chance)) effects.Add(Effect(actor, actor, Js.Obj(K.Op, Ashen.Generated.CombatOps.ApplyStatus, K.Status, b[K.Status], K.Stacks, b[K.Stacks])));
                }
            }
            foreach (var entry in (actor?.Obj(K.Statuses) ?? new JObject()).Properties())
            {
                var rule = rules.Obj(entry.Name);
                var decay = rule?.Obj(K.Decay);
                if (StackCount(entry.Value) > 0 && Enabled(rule, capabilities) && decay?.Str(K.Phase) == phase)
                    statusChanges.Add(Change(actor, entry.Name, -Math.Min(StackCount(entry.Value), decay.Num(K.Stacks))));
            }
            return new JObject { [CK.Effects] = effects, [HK.StatusChanges] = statusChanges, [HK.Checks] = checks };
        }

        /// <summary>proposalTurn(ctx, actor, phase): the turn plan, its chance rolls on the <c>misc</c> stream, queued as the actor's own reactive effects.</summary>
        public static void Turn(CombatState c, JObject actor, string phase)
        {
            var plan = TurnPlan(c, actor, phase, () => Math.Floor(c.Rng.Float(Ashen.Generated.RngStream.Misc) * Ashen.Generated.CombatMath.Percent));
            ApplyPlan(c, plan, actor, actor, new JObject { [DK.SkipRatingBonus] = true, [SK.ProposalProtectionFloor] = true });
        }

        /// <summary>proposalRetainProtection(ctx, actor, pool): the Block (or Barrier) a retaining status keeps through the owner's turn start.</summary>
        public static double Retained(CombatState c, JObject actor, string pool = null)
        {
            pool ??= Ashen.Generated.CombatOps.Block;
            double retain = 0;
            foreach (var a in ActiveBehaviors(actor, Rules(c), Capabilities(c)))
                if (a.Behavior.Str(K.Kind) == SV.RetainProtection && a.Behavior.Str(Ashen.Generated.Core2DeckKeys.Pool) == pool) retain += a.Stacks * a.Behavior.Num(HK.AmountPerStack);
            return Math.Min(RoundedAmount(actor?[pool]), retain);
        }

        /// <summary>proposalHealingRules({ target, amount, damageType }): a target a healing status turns into damage takes the converted amount instead of healing.</summary>
        public static JObject HealingPlan(CombatState c, JObject target, double amount, string damageType)
        {
            var result = Js.Obj(DK.Healing, RoundedAmount(Js.N(amount)), K.Damage, 0, CK.DamageType, damageType);
            foreach (var a in ActiveBehaviors(target, Rules(c), Capabilities(c)))
                if (a.Behavior.Str(K.Kind) == SV.HealingToDamage && Matches(a.Behavior[HK.DamageTypes], damageType))
                {
                    result[K.Damage] = Js.N(RoundedAmount(Js.N(result.Num(DK.Healing) * a.Behavior.Num(CK.Multiplier))));
                    result[DK.Healing] = 0;
                    result[CK.DamageType] = a.Behavior[CK.DamageType]?.DeepClone() ?? Js.Null();
                }
            return result;
        }

        /// <summary>proposalTargetRules({actor, target, card, rules, capabilities}) without the optional rolls: the rules a hit reads before anything is drawn.</summary>
        public static JObject TargetRules(JObject actor, JObject target, JObject card, JObject rules, JObject capabilities, double? perceptionRoll = null, double? evadeRoll = null)
        {
            var allowed = true;
            var unavoidable = false;
            double accuracyDelta = 0;
            JToken perceptionChance = Js.Null();
            double evadeChance = 0;
            var checks = new JArray();
            var tags = TagsOf(card);
            var range = !Js.Nullish(card?[CK.Range]) ? Js.Str(card[CK.Range]) : tags.FirstOrDefault(t => t.StartsWith(HV.TagRange, StringComparison.Ordinal))?.Substring(HV.TagRange.Length);
            var camp = !Js.Nullish(card?[CK.Camp]) ? Js.Str(card[CK.Camp]) : tags.Contains(HV.TagCampSpell) ? CV.CampSpell : CV.CampPhysical;
            var damageType = !Js.Nullish(card?[CK.DamageType]) ? Js.Str(card[CK.DamageType])
                : !Js.Nullish(card?.Obj(CK.Attack)?[CK.DamageType]) ? Js.Str(card.Obj(CK.Attack)[CK.DamageType])
                : Js.Items(card?[CK.Effects]).OfType<JObject>().FirstOrDefault(row => row.Str(K.Op) == CV.OpDamage && Js.Truthy(row[CK.DamageType])) is JObject typed ? Js.Str(typed[CK.DamageType])
                : tags.FirstOrDefault(t => t.StartsWith(HV.TagDamage, StringComparison.Ordinal))?.Substring(HV.TagDamage.Length);
            foreach (var a in ActiveBehaviors(actor, rules, capabilities))
                if (a.Behavior.Str(K.Kind) == HV.KindAccuracy && Matches(a.Behavior[CK.Ranges], range)) accuracyDelta += a.Behavior.Num(HK.Delta);
            foreach (var a in ActiveBehaviors(target, rules, capabilities))
            {
                var b = a.Behavior;
                if (b.Str(K.Kind) == HV.KindGreaseFire && Matches(b[HK.DamageTypes], damageType)) unavoidable = true;
                if (b.Str(K.Kind) == HV.KindVeil && !(b.Is(HK.AreaBypass) && (card?.Str(CK.Targeting) == CV.TargetingArea || tags.Contains(HV.TagTargetingArea))))
                {
                    var chance = Math.Max(b.Num(HK.Minimum), Math.Min(b.Num(HK.Maximum),
                        b.Num(HK.BaseChance) + (Perception(actor, rules, capabilities) - Perception(target, rules, capabilities)) * b.Num(HK.PerceptionScale) - a.Stacks * b.Num(HK.PenaltyPerStack)));
                    perceptionChance = Js.N(chance);
                    checks.Add(Js.Obj(K.Kind, HV.KindPerception, HK.Chance, chance, K.Status, a.Status, HK.ActorId, IdOf(actor), HK.TargetId, IdOf(target)));
                    if (perceptionRoll != null && !(perceptionRoll.Value < chance)) allowed = false;
                }
            }
            var avoided = false;
            var statusChanges = new JArray();
            if (allowed && !unavoidable)
                foreach (var a in ActiveBehaviors(target, rules, capabilities))
                {
                    var b = a.Behavior;
                    if (b.Str(K.Kind) == HV.KindEvade && Matches(b[HK.Camps], camp))
                    {
                        evadeChance = Math.Max(evadeChance, b.Num(HK.Chance));
                        checks.Add(Js.Obj(K.Kind, HV.KindEvade, HK.Chance, b.Num(HK.Chance), K.Status, a.Status, HK.ActorId, IdOf(target)));
                        if (evadeRoll != null)
                        {
                            avoided |= evadeRoll.Value < b.Num(HK.Chance);
                            statusChanges.Add(Js.Obj(HK.ActorId, IdOf(target), K.Status, a.Status, HK.Delta, -Math.Min(a.Stacks, b.Num(DK.ConsumeStacks))));
                        }
                    }
                }
            return new JObject
            {
                [HK.Allowed] = allowed,
                [HK.Avoided] = avoided,
                [HK.AccuracyDelta] = Js.N(accuracyDelta),
                [HK.Unavoidable] = unavoidable,
                [HK.PerceptionChance] = perceptionChance,
                [HK.EvadeChance] = Js.N(evadeChance),
                [HK.Checks] = checks,
                [HK.StatusChanges] = statusChanges,
            };
        }
    }
}
