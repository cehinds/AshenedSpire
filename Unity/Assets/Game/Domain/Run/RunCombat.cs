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
        /// resolveHandRules(settings, attributes): the shipped defaults (rules/handRules.json). Settings-driven overrides
        /// (gameConfig.handRules.*) are not ported yet and throw by name.
        /// </summary>
        public static JObject ResolveHandRules(RunData d, JObject settings)
        {
            if (settings != null && settings.Properties().Any(p => p.Name.StartsWith(RV.HandRulesPrefix, StringComparison.Ordinal)))
                throw new NotSupportedException(RM.HandRuleSettingsDeferred);
            return RunJs.Clone(d.HandRules);
        }

        /// <summary>resolveSwapCostRule(registries, meta): the live swap-price rule row, or null.</summary>
        public static JToken ResolveSwapCostRule(RunData d, JObject settings)
        {
            var rows = Js.Items(d.EquipmentBalance[RK.SwapCostRules]).ToList();
            var want = settings?[K.SwapCostRule];
            var row = rows.FirstOrDefault(r => Js.Truthy(r) && want != null && JToken.DeepEquals(r[K.Id], want))
                      ?? rows.FirstOrDefault(r => Js.Truthy(r) && d.EquipmentBalance[K.SwapCostRule] != null && JToken.DeepEquals(r[K.Id], d.EquipmentBalance[K.SwapCostRule]));
            return row?.DeepClone() ?? Js.Null();
        }

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
