using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Random;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using HK = Ashen.Generated.Core2HitKeys;
using HM = Ashen.Generated.Core2HitMessages;
using HV = Ashen.Generated.Core2HitValues;
using K = Ashen.Generated.CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// Chance dice and the avoidance receipt (web engine/combatAvoidance.js chanceMode, effectiveChance, rollChanceDie, previewAvoidance,
    /// resolveAvoidance). In an expanded (version 2) fight the distance and Evade rolls of the 405 rules do not exist: the d20 hit
    /// (<see cref="CoreHitRoll"/>) is the one decision, and avoidance only spends an Invulnerability stack or a Decoy that covers the
    /// hit. The 405 roll is refused by name.
    /// </summary>
    public static class CoreAvoidance
    {
        /// <summary>chanceMode(flags): advantage and disadvantage cancel; either alone decides.</summary>
        public static string ChanceMode(JObject flags)
        {
            var advantage = Js.Truthy(flags?[HK.Advantage]);
            var disadvantage = Js.Truthy(flags?[HK.Disadvantage]);
            return advantage == disadvantage ? HV.ModeNormal : advantage ? HV.ModeAdvantage : HV.ModeDisadvantage;
        }

        /// <summary>effectiveChance(basePercent, flags): the percent after advantage (one minus the miss squared) or disadvantage (the hit squared).</summary>
        public static double EffectiveChance(CombatState c, double basePercent, JObject flags)
        {
            var scale = c.CoreData.Section(HK.SectionHit).Num(HK.Percent);
            var finite = double.IsNaN(basePercent) || double.IsInfinity(basePercent) ? 0 : basePercent;
            var p = Math.Max(0, Math.Min(scale, finite)) / scale;
            var miss = 1 - p;
            var mode = ChanceMode(flags);
            return scale * (mode == HV.ModeAdvantage ? 1 - miss * miss : mode == HV.ModeDisadvantage ? p * p : p);
        }

        /// <summary>rollChanceDie(rng, die, flags): the dice on the caller's stream (two with advantage or disadvantage) and the one kept; the percent die keeps its low roll for advantage.</summary>
        public static JObject RollChanceDie(CombatState c, Func<int, int, int> draw, int die, JObject flags)
        {
            var mode = ChanceMode(flags);
            var dice = new List<int> { draw(1, die) };
            if (mode != HV.ModeNormal) dice.Add(draw(1, die));
            var favorableHigh = die != (int)c.CoreData.Section(HK.SectionHit).Num(HK.Percent);
            var keepHigh = mode == HV.ModeNormal || ((mode == HV.ModeAdvantage) == favorableHigh);
            return new JObject
            {
                [HK.Dice] = new JArray(dice.Select(d => Js.N(d))),
                [K.Roll] = Js.N(keepHigh ? dice.Max() : dice.Min()),
                [CK.Mode] = mode,
            };
        }

        /// <summary>previewAvoidance(ctx, entity, profile, incomingDamage) for an expanded fight: the prevention that would absorb the hit, and the chance (all or nothing).</summary>
        public static JObject PreviewAvoidance(CombatState c, JObject entity, JObject profile, double incomingDamage)
        {
            if (Js.D(c.Fields[CK.CombatExpansionVersion]) != c.CoreData.ExpandedVersion) throw new NotSupportedException(HM.LegacyAvoidanceNotPorted);
            string prevention = null;
            if (incomingDamage > 0)
            {
                if (CoreHitRoll.Stacks(entity, HV.StatusInvulnerability) > 0) prevention = HV.StatusInvulnerability;
                else if (CoreHitRoll.Stacks(entity, HV.StatusDecoy) > 0 && profile?.Str(CK.Targeting) == CV.TargetingSingle
                    && Js.Includes(c.CoreData.Section(HK.SectionHit)[HK.DecoyRanges], profile.Str(CK.Range))) prevention = HV.StatusDecoy;
            }
            return new JObject { [HK.Prevention] = prevention != null ? (JToken)prevention : Js.Null(), [HK.Chance] = prevention != null ? c.CoreData.Section(HK.SectionHit).Num(HK.Percent) : 0 };
        }

        /// <summary>
        /// resolveAvoidance(ctx, source, target, profile, incomingDamage, options, saved): spends the prevention that absorbs the hit and says so
        /// in a combatAvoidanceResolved event; a receipt already resolved is returned as it was.
        /// </summary>
        public static JObject Resolve(CombatState c, JObject source, JObject target, JObject profile, double incomingDamage, bool hitCheck = false, JObject saved = null)
        {
            if (Js.Truthy(saved?[CK.Resolved])) return saved;
            var preview = PreviewAvoidance(c, target, profile, incomingDamage);
            var outcome = new JObject
            {
                [CK.Resolved] = true, [HK.Avoided] = false, [HK.Distance] = Js.Null(), [CK.Evade] = Js.Null(), [HK.Chance] = preview[HK.Chance].DeepClone(),
            };
            if (hitCheck) outcome[HK.HitCheck] = true;
            if (!Js.Nullish(preview[HK.Prevention]))
            {
                CoreStatuses.Remove(c, target, preview.Str(HK.Prevention), CV.ReasonConsumed, 1);
                outcome[HK.Avoided] = true;
                outcome[HK.Prevention] = preview[HK.Prevention].DeepClone();
            }
            if (outcome[HK.Prevention] != null)
            {
                var payload = new JObject();
                if (!Js.Nullish(source?[K.Id])) payload[K.SourceId] = source[K.Id].DeepClone();
                if (!Js.Nullish(target?[K.Id])) payload[K.TargetId] = target[K.Id].DeepClone();
                if (Js.Truthy(source?[CK.KnowledgeAction]) && source.Obj(CK.KnowledgeAction)?[CK.Serial] != null) payload[HK.EnemyActionSerial] = source.Obj(CK.KnowledgeAction)[CK.Serial].DeepClone();
                foreach (var p in outcome.Properties()) payload[p.Name] = p.Value.DeepClone();
                c.Emit(HV.EventCombatAvoidanceResolved, payload);
            }
            return outcome;
        }
    }
}
