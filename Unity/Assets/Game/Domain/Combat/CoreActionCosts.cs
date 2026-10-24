using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using CC = Ashen.Generated.Core2CostKeys;
using CCV = Ashen.Generated.Core2CostValues;
using CK = Ashen.Generated.Core2CombatKeys;
using K = Ashen.Generated.CombatKeys;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The price derivation of a player card (web model/actionCosts.js cardCostBudget, isCostOptOut, applyActionCosts, ownRestoreFunds).
    /// S_sp is the damage steps, the rank, the SP-side tag weights, the range, the casting effort, the SP-side statuses and the drawbacks;
    /// S_mp is the same on the Mana side; SP = floor(S_sp / spDivisor) and MP = floor(S_mp / mpDivisor), the maneuver floor, the minimum of one
    /// point, the break-even restores. The tables are <c>rules/actionCosts.json</c>. Every card of the shipped profile is <c>costRule: fixed</c>,
    /// so the derivation prices nothing in a Core 2 fight; the port exists so a profile that releases a card from that rule is priced as the web prices it.
    /// </summary>
    public static class CoreActionCosts
    {
        private static double Count(JToken value) => Js.IsFinite(value) ? Js.D(value) : 1;

        private static double WebRound(double value) => Math.Floor(value + Ashen.Generated.CombatMath.Half);

        private static List<string> Strings(JToken array) => Js.Items(array).Select(Js.Str).Where(s => s != null).ToList();

        /// <summary>cardCostBudget(def, { rank, upgradeDelta }): the cost receipt of a card.</summary>
        public static JObject Budget(CombatState c, JObject def, double rank = 0, double upgradeDelta = 0)
        {
            var budget = (JObject)c.CoreData.ActionCost(CC.CostBudget);
            var weights = (JObject)c.CoreData.ActionCost(CC.CostTags);
            var authored = def?[K.Tags] is JArray tagArray ? Strings(tagArray) : def?[CK.CardTags] is JArray cardTagArray ? Strings(cardTagArray) : new List<string>();
            var derived = new List<string>();
            void Derive(string tag) { if (tag != null && !derived.Contains(tag)) derived.Add(tag); }
            var keywordTags = (JObject)c.CoreData.ActionCost(CC.KeywordTags);
            foreach (var keyword in Js.Items(def?[K.Keywords])) Derive(Js.Str(keywordTags[Js.Str(keyword) ?? string.Empty]));

            var drawbackOps = (JObject)c.CoreData.ActionCost(CC.DrawbackOps);
            var restoreStaminaOps = c.CoreData.ActionCost(CC.RestoreStaminaOps);
            var restoreManaOps = c.CoreData.ActionCost(CC.RestoreManaOps);
            double damage = 0, restoreStamina = 0, restoreMana = 0, statusPoints = 0;
            var statusWeights = (JObject)c.CoreData.ActionCost(CC.StatusWeights);
            var statusWeight = c.CoreData.ActionCost(CC.StatusWeight);
            var stackCap = c.CoreData.ActionCost(CC.StatusStackCap);
            foreach (var e in Js.Items(def?[K.Effects]).OfType<JObject>())
            {
                var op = e.Str(K.Op);
                var drawback = op != null ? Js.Str(drawbackOps[op]) : null;
                if (drawback != null && (op != CCV.OpAddCard || e[CC.Pile] != null)) Derive(drawback);
                if (op == CCV.OpDamage && Js.IsNum(e[K.Amount])) damage += e.Num(K.Amount) * Count(e[CC.Hits]);
                if (Js.Includes(restoreStaminaOps, op) && Js.IsNum(e[K.Amount])) restoreStamina += e.Num(K.Amount);
                if (Js.Includes(restoreManaOps, op) && Js.IsNum(e[K.Amount])) restoreMana += e.Num(K.Amount);
                if ((op == CCV.OpApplyStatus || op == CCV.OpBuildup) && Js.Truthy(e[K.Status]))
                {
                    var stacks = op == CCV.OpBuildup ? 1 : Math.Max(0, Count(e[K.Stacks]));
                    var capped = Js.Nullish(stackCap) ? stacks : Math.Min(stacks, Js.D(stackCap));
                    var weight = statusWeights[e.Str(K.Status) ?? string.Empty];
                    statusPoints += (Js.Nullish(weight) ? Js.D(statusWeight) : Js.D(weight)) * capped;
                }
            }

            var allTags = authored.Concat(derived).Distinct().ToList();
            var magic = allTags.Any(t => (weights[t] as JObject)?.Str(CC.Side) == CCV.SideMp);
            var castTags = c.CoreData.ActionCost(CC.CastTags);
            var cast = allTags.Any(t => Js.Includes(castTags, t));
            double tagSp = 0, tagMp = 0;
            foreach (var tag in allTags)
            {
                if (!(weights[tag] is JObject row)) continue;
                if (row.Str(CC.Side) == CCV.SideMp) tagMp += row.Num(CC.Weight); else tagSp += row.Num(CC.Weight);
            }
            var ranges = (JObject)c.CoreData.ActionCost(CC.RangeWeights);
            var rangeTags = (JObject)c.CoreData.ActionCost(CC.RangeTags);
            double range = 0;
            foreach (var tag in allTags)
            {
                var family = Js.Str(rangeTags[tag]);
                if (family != null && Js.IsNum(ranges[family])) range = Math.Max(range, ranges.Num(family));
            }
            var steps = budget[CC.DamageSteps] as JArray;
            var damagePoints = budget.Num(CC.DamageWeight) * (steps != null && steps.Count > 0 ? steps.Count(t => damage >= Js.D(t)) : damage);
            var rankAbove = Math.Max(0, rank);
            var parts = new JObject
            {
                [CC.Damage] = Js.N(damagePoints),
                [CC.Rank] = Js.N(budget.Num(CC.RankWeight) * rankAbove),
                [CC.RankMp] = Js.N(magic ? budget.Num(CC.RankWeightMp) * rankAbove : 0),
                [CC.TagSp] = Js.N(tagSp),
                [CC.TagMp] = Js.N(tagMp),
                [CC.Range] = Js.N(range),
                [CC.CastingEffort] = Js.N(cast ? budget.Num(CC.CastingEffortWeight) : 0),
                [CC.Status] = Js.N(statusPoints),
                [CC.RestoreMana] = Js.N(WebRound(budget.Num(CC.RestoreWeight) * restoreMana)),
                [CC.RestoreStamina] = Js.N(WebRound(budget.Num(CC.RestoreWeight) * restoreStamina)),
            };
            var sSp = Math.Max(0, (magic ? 0 : damagePoints) + parts.Num(CC.Rank) + tagSp + range + parts.Num(CC.CastingEffort) + (magic ? 0 : statusPoints));
            var sMp = Math.Max(0, (magic ? damagePoints : 0) + parts.Num(CC.RankMp) + tagMp + (magic ? statusPoints : 0));
            var rawSp = Math.Floor(sSp / budget.Num(CC.SpDivisor));
            var rawMp = Math.Floor(sMp / budget.Num(CC.MpDivisor));
            double sp = rawSp, mp = rawMp;
            var floors = (JObject)c.CoreData.ActionCost(CC.ManeuverSpFloors);
            double floor = 0;
            if (rank == 0)
            {
                double top = 0;
                foreach (var tag in allTags) if (Js.IsNum(floors[tag])) top = Math.Max(top, floors.Num(tag));
                floor = top - Math.Max(0, upgradeDelta);
            }
            if (sp < floor) sp = floor;
            if (sp == 0 && mp == 0)
            {
                if (restoreMana > 0 || !(sMp > sSp)) sp = 1; else mp = 1;
            }
            var funded = new JObject
            {
                [CC.Sp] = Js.N(Math.Min(parts.Num(CC.RestoreStamina), restoreStamina)),
                [CC.Mp] = Js.N(Math.Min(parts.Num(CC.RestoreMana), restoreMana)),
            };
            sp += parts.Num(CC.RestoreStamina);
            mp += parts.Num(CC.RestoreMana);
            return new JObject
            {
                [CC.Sp] = Js.N(sp), [CC.Mp] = Js.N(mp), [CC.Funded] = funded, [CC.SSp] = Js.N(sSp), [CC.SMp] = Js.N(sMp),
                [CC.RawSp] = Js.N(rawSp), [CC.RawMp] = Js.N(rawMp), [CC.Magic] = magic, [CC.Cast] = cast, [K.Tags] = new JArray(allTags), [CC.Parts] = parts,
            };
        }

        /// <summary>isCostOptOut(def): a card keeps its authored cost with costRule fixed, as a curse or a status, or with an X (non-number) cost.</summary>
        public static bool IsCostOptOut(JObject def) =>
            def == null || def.Str(CC.CostRule) == CCV.CostRuleFixed || Js.Includes(new JArray(CCV.TypeStatus, CCV.TypeCurse), def.Str(K.Type)) || !Js.IsNum(def[K.Cost]);

        private static JObject ScaleEffect(CombatState c, JObject effect, double multiplier, List<string> unscaled)
        {
            var op = effect.Str(K.Op);
            if (!Js.Includes(c.CoreData.ActionCost(CC.ScaledEffectOps), op) || unscaled.Contains(op) || !Js.IsNum(effect[K.Amount])) return effect;
            var scaled = Js.Spread(effect);
            scaled[K.Amount] = Js.N(WebRound(effect.Num(K.Amount) * multiplier));
            return scaled;
        }

        private static List<JObject> TwoCostEffects(CombatState c, List<JObject> effects, List<string> tags)
        {
            var sweep = tags.Contains(CCV.TagManeuverSweep);
            var unscaled = sweep ? Strings(c.CoreData.ActionCost(CC.SweepUnscaledOps)) : new List<string>();
            var multiplier = ((JObject)c.CoreData.ActionCost(CC.CostBudget)).Num(CC.TwoCostMultiplier);
            return effects.Select(e => e != null ? ScaleEffect(c, e, multiplier, unscaled) : e).ToList();
        }

        private static List<JObject> WithSmashPoise(CombatState c, List<JObject> effects, List<string> tags)
        {
            var baseDamage = ((JObject)c.CoreData.ActionCost(CC.CostBudget)).Num(CC.SmashPoiseBase);
            if (!tags.Contains(CCV.TagManeuverSmash) || !(baseDamage > 0)) return effects;
            if (effects.Any(e => e?.Str(K.Op) == CCV.OpPoiseDamage)) return effects;
            var hit = effects.FirstOrDefault(e => e?.Str(K.Op) == CCV.OpDamage);
            var added = new JObject { [K.Op] = CCV.OpPoiseDamage, [K.Target] = Js.Truthy(hit?[K.Target]) ? hit[K.Target].DeepClone() : (JToken)CCV.TargetEnemy, [K.Amount] = Js.N(baseDamage) };
            return effects.Concat(new[] { added }).ToList();
        }

        private static List<JObject> WithFundedRestores(CombatState c, List<JObject> effects, double fundedSp, double fundedMp)
        {
            var left = new Dictionary<string, double> { [CC.Sp] = fundedSp, [CC.Mp] = fundedMp };
            var result = new List<JObject>();
            foreach (var e in effects)
            {
                var op = e?.Str(K.Op);
                var side = Js.Includes(c.CoreData.ActionCost(CC.RestoreStaminaOps), op) ? CC.Sp : Js.Includes(c.CoreData.ActionCost(CC.RestoreManaOps), op) ? CC.Mp : null;
                if (side == null || !Js.IsNum(e[K.Amount]) || left[side] <= 0) { result.Add(e); continue; }
                var part = Math.Min(left[side], e.Num(K.Amount));
                left[side] -= part;
                var marked = Js.Spread(e);
                marked[CC.Funded] = Js.N(part);
                result.Add(marked);
            }
            return result;
        }

        /// <summary>applyActionCosts(def, authored, { rank }): the card with its SP and Mana derived (and, at 2 SP or more, its effect amounts scaled); the card itself when it opts out or nothing changes.</summary>
        public static JObject Apply(CombatState c, JObject def, JObject authored = null, double rank = 0)
        {
            if (IsCostOptOut(def)) return def;
            authored ??= def;
            var upgradeDelta = !(authored[CC.GradeProfiles] is JArray) && Js.IsNum(authored[K.Cost]) && Js.IsNum(def[K.Cost]) ? authored.Num(K.Cost) - def.Num(K.Cost) : 0;
            var budget = Budget(c, def, rank, upgradeDelta);
            var sp = budget.Num(CC.Sp);
            var mp = budget.Num(CC.Mp);
            var funded = budget.Obj(CC.Funded);
            var tags = Strings(budget[K.Tags]);
            var scaled = sp - funded.Num(CC.Sp) >= c.CoreData.Section(CC.Costs).Num(CC.ScaleFromSp);
            var costs = (JObject)c.CoreData.ActionCost(CC.CostBudget);
            var smash = tags.Contains(CCV.TagManeuverSmash) && costs.Num(CC.SmashPoiseBase) > 0;
            var funds = funded.Num(CC.Sp) > 0 || funded.Num(CC.Mp) > 0;
            if (sp == def.Num(K.Cost) && mp == def.Or0(K.ManaCost) && !scaled && !smash && !funds) return def;
            List<JObject> effects = null;
            if (def[K.Effects] is JArray source)
            {
                effects = source.Select(e => e as JObject).ToList();
                if (scaled) effects = TwoCostEffects(c, effects, tags);
                effects = WithSmashPoise(c, effects, tags);
                if (funds) effects = WithFundedRestores(c, effects, funded.Num(CC.Sp), funded.Num(CC.Mp));
            }
            var template = def[CC.TextTemplate];
            var poiseLine = smash && Js.IsStr(template) && !Js.Str(template).Contains(CCV.PoiseToken) && effects != null && effects.Any(e => e?.Str(K.Op) == CCV.OpPoiseDamage);
            var result = Js.Spread(def);
            result[K.Cost] = Js.N(sp);
            result[K.ManaCost] = Js.N(mp);
            if (effects != null) result[K.Effects] = new JArray(effects.Select(e => e != null ? (JToken)e : Js.Null()));
            if (poiseLine) result[CC.TextTemplate] = Js.Str(template).Trim() + c.CoreData.Section(CC.Costs).Str(CC.PoiseLine);
            return result;
        }

        /// <summary>ownRestoreFunds(def): the part of a card's price its own restores pay: stamina and mana.</summary>
        public static (double Stamina, double Mana) OwnRestoreFunds(CombatState c, JObject def)
        {
            double stamina = 0, mana = 0;
            foreach (var e in Js.Items(def?[K.Effects]).OfType<JObject>())
            {
                var part = Js.IsFinite(e[CC.Funded]) ? Math.Max(0, e.Num(CC.Funded)) : 0;
                var op = e.Str(K.Op);
                if (Js.Includes(c.CoreData.ActionCost(CC.RestoreStaminaOps), op)) stamina += part;
                else if (Js.Includes(c.CoreData.ActionCost(CC.RestoreManaOps), op)) mana += part;
            }
            return (stamina, mana);
        }
    }
}
