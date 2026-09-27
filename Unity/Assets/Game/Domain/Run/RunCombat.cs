using System;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RM = Ashen.Generated.RunMessages;
using RV = Ashen.Generated.RunValues;

namespace Ashen.Domain.Run
{
    /// <summary>
    /// Starting a fight from a run (shipped main.js enterCombat's createCombat arguments, with combatMods,
    /// model/handRules.js resolveHandRules and model/loadout.js resolveSwapCostRule): the JObject
    /// <see cref="CombatStart.Create"/> takes, built from the run and content alone.
    /// </summary>
    public static class RunCombat
    {
        /// <summary>
        /// resolveHandRules(settings, attributes): the shipped defaults (rules/handRules.json) with the Settings overrides
        /// (gameConfig.handRules.*) applied row by row, as the Advanced rows read them: a choice must be one of its
        /// choices (the scaling stat: an attribute id), a switch must be a boolean, a number is floored and clamped to the
        /// row's range (rules/runEngine.json handRuleSettings); a group whose minimum exceeds its maximum falls back to
        /// its defaults. An unreadable value is ignored.
        /// </summary>
        public static JObject ResolveHandRules(RunData d, JObject settings)
        {
            var rules = RunJs.Clone(d.HandRules);
            if (settings == null) return rules;
            var cfg = d.Engine.Obj(RK.HandRuleSettings) ?? new JObject();
            var attributeIds = d.Attributes.Ids.ToList();
            void Apply(JObject target, string path, string field, JToken def)
            {
                var raw = settings[RV.HandRulesPrefix + path];
                if (raw == null) return;
                JToken value;
                if (Js.IsStr(def))
                {
                    var choices = Js.Includes(cfg[RK.AttributeChoiceFields], field) ? attributeIds : RunJs.Strs(cfg.Obj(RK.Choices)?[field]);
                    if (!Js.IsStr(raw) || !choices.Contains(Js.Str(raw))) return;
                    value = raw.DeepClone();
                }
                else if (def.Type == JTokenType.Boolean)
                {
                    if (raw.Type != JTokenType.Boolean) return;
                    value = raw.DeepClone();
                }
                else
                {
                    var n = RunJs.Number(raw);
                    if (double.IsNaN(n) || double.IsInfinity(n)) return;
                    var minimums = cfg.Obj(RK.NumberMinimums) ?? new JObject();
                    var min = Js.IsNum(minimums[path]) ? minimums.Num(path) : Js.IsNum(minimums[field]) ? minimums.Num(field) : cfg.Num(RK.NumberMinimum);
                    value = Js.N(Math.Min(cfg.Num(RK.NumberMaximum), Math.Max(min, Math.Floor(n))));
                }
                target[field] = value;
            }
            var groups = RunJs.Strs(cfg[RK.Groups]);
            foreach (var p in d.HandRules.Properties())
            {
                if (groups.Contains(p.Name) && p.Value is JObject group)
                    foreach (var f in group.Properties()) Apply(rules.Obj(p.Name), p.Name + RV.PathDot + f.Name, f.Name, f.Value);
                else Apply(rules, p.Name, p.Name, p.Value);
            }
            foreach (var group in groups)
                if (rules.Obj(group) != null && rules.Obj(group).Num(K.Minimum) > rules.Obj(group).Num(K.Maximum)) rules[group] = RunJs.Clone(d.HandRules.Obj(group));
            return rules;
        }

        /// <summary>resolveSwapCostRule(registries, meta): the live swap-price rule row, or null.</summary>
        public static JToken ResolveSwapCostRule(RunData d, JObject settings) => Equipment.ResolveSwapCostRule(d.Combat, settings?[K.SwapCostRule]);

        /// <summary>
        /// The createCombat arguments main.js builds to enter <paramref name="encounterId"/> from <paramref name="run"/>
        /// (without registries/rng, JSON-plain). Journeys, legacy dungeons and Custom Climb modifiers are not ported
        /// yet and throw by name; for an ordinary run the only combat modifier is the seat-tier HP ratio.
        /// </summary>
        public static JObject CreateArgs(JObject run, string encounterId, RunData d, JObject settings = null)
        {
            if (Js.Truthy(run[RK.Journey]) || Js.Truthy(run[RK.LegacyDungeon])) throw new NotSupportedException(RM.JourneyCombatDeferred);
            if (Js.Truthy(run[RK.Custom])) throw new NotSupportedException(RM.CustomClimbDeferred);
            settings ??= new JObject();
            var encounter = d.Encounters.Get(encounterId);
            double hpMult = 1;
            if (run[RK.SeatOrder] is JArray seatOrder)
            {
                var act = run.Num(RK.ActNumber);
                hpMult *= Creation.SeatTierHpMult(d, Creation.SeatAtTier(seatOrder, act), act);
            }
            return Arguments(run, encounter, d, settings, hpMult, new JArray(), new JArray());
        }

        /// <summary>
        /// The createCombat arguments main.js enterCombat builds (without registries/rng, JSON-plain), for the combat
        /// modifiers the caller resolved (main.js combatMods: the HP ratio, the enemies' and the player's opening
        /// statuses); the run's own <c>self.*</c> start statuses follow the player's.
        /// </summary>
        public static JObject Arguments(JObject run, JObject encounter, RunData d, JObject settings, double hpMult, JArray enemyStatuses, JArray playerStatuses)
        {
            settings ??= new JObject();
            var loadout = run.Obj(K.Loadout);
            var player = new JObject();
            void Put(string key, JToken value)
            {
                if (value != null) player[key] = value.DeepClone();
            }
            Put(K.ClassId, run[RK.Class]);
            foreach (var key in new[]
                     {
                         K.Attributes, K.DerivedStatRuleSnapshot, K.Skills, K.CoreTags, K.MaxHp, K.Hp, K.MaxMana, K.Mana, K.MaxStamina, K.Stamina,
                         K.EnergyMax, K.DrawPerTurn, K.DamageBySchoolAdd, K.EquipmentProfileRuleSnapshot, K.EquipmentAttackSlotCount,
                         K.RemovedAttackSlotIds, K.EquipmentPoolDeficits, K.ItemUpgradeLevels, K.ItemMounts, RK.ArmamentLevels, K.Deck,
                     })
                Put(key, run[key]);
            Put(K.RelicIds, run[K.Relics]);
            Put(K.Flasks, run[K.Flasks]);
            Put(K.FlaskCharges, run[K.FlaskCharges]);
            Put(K.Loadout, loadout);
            var ratings = d.Balance[K.CombatRatings];
            var starts = new JArray(Js.Items(playerStatuses).Select(t => t.DeepClone()));
            foreach (var status in Loadout.RunMods(d, loadout, run.Str(RK.Class)).StartStatuses) starts.Add(status.DeepClone());
            return new JObject
            {
                [K.RatingsRules] = Js.Truthy(ratings) ? ratings.DeepClone() : Js.Null(),
                [K.HandRules] = ResolveHandRules(d, settings),
                [K.Player] = player,
                [K.EnemyIds] = encounter[K.Enemies]?.DeepClone(),
                [K.HpMult] = Js.N(hpMult),
                [K.EnemyStatuses] = new JArray(Js.Items(enemyStatuses).Select(t => t.DeepClone())),
                [K.SwapCostRule] = ResolveSwapCostRule(d, settings),
                [K.PlayerStatuses] = starts,
            };
        }
    }
}
