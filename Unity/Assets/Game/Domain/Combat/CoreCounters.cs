using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// An armed Counter (web engine/combatMatchups.js armCombatCounter, armExpansionCounter, clearCombatCounter): a prepared reply that
    /// waits on its owner, with the coverage it answers and the payload it returns. An enemy arms one when it telegraphs a Counter move;
    /// the reply itself (<see cref="CoreCounterReturn"/>) reads the structure laid down here.
    /// </summary>
    public static class CoreCounters
    {
        private static double Number(JToken value) => Js.IsFinite(value) ? Math.Max(0, Js.D(value)) : 0;

        /// <summary>clearCombatCounter(entity): drop the prepared reply; a queued reply already triggered is marked interrupted.</summary>
        public static void Clear(JObject entity, CombatState c = null)
        {
            if (entity == null) return;
            entity.Remove(CK.CombatCounter);
            if (c == null) return;
            foreach (var action in c.Queue)
                if (action.Source == entity && action.Meta?.CombatCounterReaction == true) action.Meta.CombatCounterInterrupted = true;
        }

        private static bool IsSpell(CombatState c, JObject profile) => Js.Includes(c.CoreData.Section(CK.Vocabulary)[CK.SpellCamps], profile.Str(CK.Camp));

        /// <summary>armCombatCounter(ctx, entity, carrier, options): the expanded (version 2) prepared Counter.</summary>
        public static JObject Arm(CombatState c, JObject entity, JObject carrier, JObject options)
        {
            var baseProfile = carrier.Obj(CK.CombatProfile);
            var combatProfile = baseProfile != null && Js.Includes(c.CoreData.Section(CK.Vocabulary)[CK.SpellCamps].DeepClone(), baseProfile.Str(CK.Camp)) || baseProfile?.Str(CK.Camp) == CV.CampPhysical ? baseProfile : null;
            if (entity == null || combatProfile == null) return null;
            var profile = Js.Spread(combatProfile);
            if (IsSpell(c, profile)) profile[CK.Camp] = CV.CampSpell;
            var mode = Js.Truthy(options[CK.Mode]) ? options.Str(CK.Mode) : Js.Truthy(profile[CK.CounterMode]) ? profile.Str(CK.CounterMode) : IsSpell(c, profile) ? CV.CampSpell : CV.RangeMelee;
            entity.Put(CK.CombatPreparationSerial, entity.Or0(CK.CombatPreparationSerial) + 1);
            var authored = options.Obj(CK.Payload) ?? profile.Obj(CK.CounterPayload);
            var coverage = options[CK.Coverage] ?? profile[CK.CounterCoverage]
                ?? c.Fields.Obj(CK.CombatExpansionRules)?.Obj(CK.Matchups)?.Obj(CK.Counter)?.Obj(CK.DefaultCoverage)?[mode];
            JObject payload;
            if (authored != null)
            {
                payload = new JObject
                {
                    [K.Hp] = Math.Floor(Number(authored[K.Hp])),
                    [CK.Poise] = Math.Floor(Number(authored[CK.Poise])),
                    [CK.Ward] = Math.Floor(Number(authored[CK.Ward])),
                };
                if (authored[CK.SmashPoiseBonus] != null) payload[CK.SmashPoiseBonus] = Math.Floor(Number(authored[CK.SmashPoiseBonus]));
                if (authored[CK.HpAgainstNonCaster] != null) payload[CK.HpAgainstNonCaster] = authored[CK.HpAgainstNonCaster].DeepClone();
                if (authored[CK.HpBase] != null) { payload[CK.HpBase] = Math.Floor(Number(authored[CK.HpBase])); payload[CK.HpRating] = Math.Floor(Number(authored[CK.HpRating])); }
                if (authored[CK.PoiseBase] != null) payload[CK.PoiseBase] = Math.Floor(Number(authored[CK.PoiseBase]));
            }
            else payload = new JObject
            {
                [K.Hp] = Number(options[K.Damage]) + Number(options[CK.Bonus]),
                [CK.Poise] = Number(options[CK.PoiseDamage]),
                [CK.Ward] = Number(options[CK.WardDamage]),
            };
            var counter = new JObject
            {
                [CK.Version] = c.CoreData.Section(CK.Counter).Num(CK.Version),
                [CK.Charges] = 1,
                [CK.Serial] = entity[CK.CombatPreparationSerial].DeepClone(),
                [CK.Mode] = mode,
                [CK.Coverage] = coverage?.DeepClone(),
                [CK.Payload] = payload,
                [CK.ExpiresOnOwnerCycle] = entity.Or0(CK.CombatOwnerCycle) + 1,
                [CK.ArmedAtCardCount] = Js.Nullish(entity.Obj(K.Counters)?[K.CardsPlayedThisCombat]) ? 0 : entity.Obj(K.Counters).Num(K.CardsPlayedThisCombat),
            };
            if (Js.Truthy(profile[CK.SchoolEffect])) counter[CK.SchoolEffect] = profile[CK.SchoolEffect].DeepClone();
            counter[CK.Carrier] = carrier.DeepClone();
            entity[CK.CombatCounter] = counter;
            var body = CoreMatchups.EventCarrier(entity, carrier);
            body[K.SourceId] = entity[K.Id].DeepClone();
            body[CK.Mode] = mode;
            c.Emit(CV.EventCombatCounterArmed, body);
            return counter;
        }
    }
}
