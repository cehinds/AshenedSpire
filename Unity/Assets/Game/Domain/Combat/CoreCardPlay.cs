using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CC = Ashen.Generated.Core2CostKeys;
using CCM = Ashen.Generated.Core2CostMessages;
using CCV = Ashen.Generated.Core2CostValues;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The front half of a Core 2 card play (web engine/combat.js doPlayCard up to the effects): the cost gates, the price paid, the card leaves the
    /// hand, the play counters move, and the card's stance is entered (engine/combatCardTactics.js tacticalCarrier, engine/gameProfileCombat.js
    /// setProfileStance). The card's effects, the hit roll and the damage are the later work packages (WP-6, WP-7); <see cref="Enter"/> is the
    /// part of a play a Core 2 turn needs before them, and it refuses a card the gates refuse before it changes anything.
    /// </summary>
    public static class CoreCardPlay
    {
        /// <summary>
        /// The carrier a played card hands the stance and the hit: its instance, its tags, the metadata its definition keeps for combat, the
        /// effects' statuses and the combat profile (tacticalCarrier). The damage type a wielded weapon would resolve into the profile is the
        /// damage work package's (WP-7); the stance reads none of it.
        /// </summary>
        public static JObject Carrier(CombatState c, JObject instance, JObject def)
        {
            var tags = Js.Items(def[K.Tags]).Any() ? def[K.Tags] : null;
            var authoredTags = !Js.Nullish(def[CK.CardTags]) ? def[CK.CardTags] : tags ?? new JArray();
            var cardTags = !Js.Nullish(def[CK.CardTags]) ? def[CK.CardTags] : tags;
            var spell = Js.Items(!Js.Nullish(def[CK.CardTags]) ? def[CK.CardTags] : def[K.Tags]).Select(Js.Str).Contains(CV.TagSourceSpell);
            var carrier = new JObject();
            foreach (var key in Js.Items(c.CoreData.Section(CK.Plan)[CK.CarrierFields]).Select(Js.Str))
                if (def[key] != null) carrier[key] = def[key].DeepClone();
            var extra = Js.Obj(
                CK.AbilityKind, Js.Truthy(def[CK.AbilityKind]) ? def[CK.AbilityKind] : Js.S(spell ? CV.TypeSpell : CCV.AbilityManeuver),
                CC.AbilityFamily, def[CC.AbilityFamily], CK.AbilityRank, def[CK.AbilityRank],
                K.InstanceId, instance[K.InstanceId], K.CardId, instance[K.CardId], K.Upgraded, instance[K.Upgraded],
                K.Type, Js.S(Cards.Kind(c.Data, def)), K.Tags, cardTags, K.Attack, def[K.Attack], CC.AuthoredTags, authoredTags,
                CK.DamageSchool, instance[CK.DamageSchool] ?? def[CK.DamageSchool], CC.ExposureBuildupPerHit, instance[CC.ExposureBuildupPerHit] ?? def[CC.ExposureBuildupPerHit]);
            foreach (var p in extra.Properties()) carrier[p.Name] = p.Value.DeepClone();
            carrier[K.Tags] = (!Js.Nullish(def[CK.CardTags]) ? def[CK.CardTags] : !Js.Nullish(def[K.Tags]) ? def[K.Tags] : carrier[K.Tags])?.DeepClone();
            if (extra[K.Attack] == null && def[K.Attack] != null) carrier[K.Attack] = def[K.Attack].DeepClone();
            if (Js.IsFinite(def[CK.MagicShare])) carrier[CK.MagicShare] = def[CK.MagicShare].DeepClone();
            if (Js.IsStr(def[CK.PhysicalDamageType])) carrier[CK.PhysicalDamageType] = def[CK.PhysicalDamageType].DeepClone();
            var effects = Js.Items(def[K.Effects]).OfType<JObject>().ToList();
            var applied = effects.Where(e => e.Str(K.Op) == Op.ApplyStatus).ToList();
            carrier[CK.AppliedStatuses] = new JArray(applied.Select(e => e[K.Status]?.DeepClone()));
            carrier[CK.AppliedStatusEffects] = new JArray(applied.Select(e => e.DeepClone()));
            carrier[CK.AppliedBuildupStatuses] = new JArray(effects.Where(e => e.Str(K.Op) == CV.OpBuildup).Select(e => e[K.Status]?.DeepClone()));
            carrier[CK.CombatRiderTargets] = new JArray();
            if (carrier[CK.RatingId] == null && def[CK.RatingId] != null) carrier[CK.RatingId] = def[CK.RatingId].DeepClone();
            var profile = Js.Spread(CoreStances.ProfileFor(c, carrier), def.Obj(CK.CombatProfile));
            carrier[CK.CombatProfile] = profile;
            var player = c.Player;
            var evade = profile.Obj(CK.Evade);
            if (Js.Truthy(evade?[CK.WhileStatus])) profile[CK.EvadeEligible] = player.Obj(K.Statuses)?.Obj(evade.Str(CK.WhileStatus))?.Num(K.Stacks) > 0;
            // expansionCarrierTraits: the traits the player itself carries join the card's own.
            var grounded = Js.Truthy(player.Obj(CK.CombatTraits)?[CK.Grounded]) || player.Obj(K.Statuses)?.Obj(CV.StatusGrounded)?.Num(K.Stacks) > 0;
            var traits = new List<string>(Js.Items(profile[CK.Traits]).Select(Js.Str));
            foreach (var p in (player.Obj(CK.CombatTraits) ?? new JObject()).Properties())
                if (p.Value.Type == JTokenType.Boolean && p.Value.Value<bool>() && !(grounded && p.Name == CV.TraitConductive)) traits.Add(p.Name);
            if (grounded) traits.Add(CV.TraitGrounded);
            profile[CK.Traits] = new JArray(traits.Distinct());
            return carrier;
        }

        /// <summary>What <see cref="Enter"/> did: the price paid and the carrier the stance was entered with.</summary>
        public sealed class Entry
        {
            public JObject Paid;
            public JObject Carrier;
            public string CardId;
        }

        /// <summary>
        /// Plays the front half of a card: refuses (before any change) a card whose gates refuse, pays the price, takes it out of the hand, counts it
        /// and enters its stance with the entry grants and the matching-stance multiplier. The effects stay unplayed (WP-6, WP-7).
        /// </summary>
        public static Entry Enter(CombatState c, string instanceId)
        {
            var refusal = CoreCosts.PlayRefusal(c, instanceId);
            if (refusal != null) throw new InvalidOperationException(refusal);
            var player = c.Player;
            var index = c.Piles.Hand.FindIndex(card => card.Str(K.InstanceId) == instanceId);
            var instance = c.Piles.Hand[index];
            var def = CoreCosts.Resolve(c, instance);
            var paid = CoreCosts.CardPlayCosts(c, instanceId);
            var isX = def.Str(K.Cost) == CCV.CostX;
            // Stamina is the Actions pool; the receipts follow the payment as the web emits them.
            player.Put(K.Stamina, player.Num(K.Stamina) - paid.Num(K.Energy));
            if (paid.Num(K.Energy) > 0 || isX) c.Emit(E.EnergySpent, Js.Obj(K.Amount, paid[K.Energy]));
            player.Put(K.Mana, player.Num(K.Mana) - paid.Num(K.Mana));
            if (paid.Num(K.Mana) > 0) c.Emit(E.ManaSpent, Js.Obj(K.Amount, paid[K.Mana]));
            var counters = player.Obj(K.Counters);
            if (paid.Num(K.Stamina) > 0)
            {
                counters.Put(CC.StaminaSpentThisTurn, counters.Or0(CC.StaminaSpentThisTurn) + paid.Num(K.Stamina));
                c.Emit(E.StaminaSpent, Js.Obj(K.Amount, paid[K.Stamina]));
            }
            c.Piles.Hand.RemoveAt(index);
            counters.Put(CC.CardsPlayedThisTurn, counters.Num(CC.CardsPlayedThisTurn) + 1);
            counters.Put(CC.CardsPlayedThisCombat, counters.Num(CC.CardsPlayedThisCombat) + 1);
            var carrier = Carrier(c, instance, def);
            if (carrier.Str(K.Type) == CV.TypeAttack) counters.Put(CC.AttacksPlayedThisCombat, counters.Num(CC.AttacksPlayedThisCombat) + 1);
            CoreStances.SetProfileStance(c, player, carrier);
            return new Entry { Paid = paid, Carrier = carrier, CardId = instance.Str(K.CardId) };
        }
    }
}
