using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using HK = Ashen.Generated.Core2HitKeys;
using HV = Ashen.Generated.Core2HitValues;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

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

        /// <summary>proposalTargetRules({actor, target, card, rules, capabilities}) without the optional rolls: the rules a hit reads before anything is drawn.</summary>
        public static JObject TargetRules(JObject actor, JObject target, JObject card, JObject rules, JObject capabilities)
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
                }
            }
            if (allowed && !unavoidable)
                foreach (var a in ActiveBehaviors(target, rules, capabilities))
                {
                    var b = a.Behavior;
                    if (b.Str(K.Kind) == HV.KindEvade && Matches(b[HK.Camps], camp))
                    {
                        evadeChance = Math.Max(evadeChance, b.Num(HK.Chance));
                        checks.Add(Js.Obj(K.Kind, HV.KindEvade, HK.Chance, b.Num(HK.Chance), K.Status, a.Status, HK.ActorId, IdOf(target)));
                    }
                }
            return new JObject
            {
                [HK.Allowed] = allowed,
                [HK.Avoided] = false,
                [HK.AccuracyDelta] = Js.N(accuracyDelta),
                [HK.Unavoidable] = unavoidable,
                [HK.PerceptionChance] = perceptionChance,
                [HK.EvadeChance] = Js.N(evadeChance),
                [HK.Checks] = checks,
                [HK.StatusChanges] = new JArray(),
            };
        }
    }
}
