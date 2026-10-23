using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CV = Ashen.Generated.Core2CombatValues;
using DK = Ashen.Generated.Core2DamageKeys;
using DM = Ashen.Generated.Core2DamageMessages;
using DV = Ashen.Generated.Core2DamageValues;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// What a card or a piece says about the attack it makes (web model/attackTags.js attackDescriptor, weaponDamageType, weaponDamageComponents,
    /// dominantDamageType; engine/combatCardTactics.js wieldedArmament): the source (weapon, spell, unarmed), the damage types and the weapon a
    /// card is dealt with. A tag names the attack's identity; it never invents an effect.
    /// </summary>
    public static class CoreAttack
    {
        /// <summary>attackDescriptor(carrier): the carrier's attack with its source and damage type read from the tags it carries.</summary>
        public static JObject Descriptor(JObject carrier)
        {
            var tagsToken = !Js.Nullish(carrier?[K.Tags]) ? carrier[K.Tags] : carrier?[CK.CardTags];
            var tags = Js.Items(tagsToken).Select(Js.Str).Where(t => t != null).ToList();
            var sourceTags = tags.Where(t => t.StartsWith(CV.TagSource, StringComparison.Ordinal)).ToList();
            if (sourceTags.Count > 1) throw new InvalidOperationException(DM.AttackSourceTags);
            var taggedSource = sourceTags.Count > 0 ? sourceTags[0].Substring(CV.TagSource.Length) : null;
            var attack = Js.Spread(carrier?.Obj(K.Attack));
            if (taggedSource != null && attack.Is(CK.Source) && taggedSource != attack.Str(CK.Source)) throw new InvalidOperationException(DM.AttackSourceContradicts);
            if (!attack.Is(CK.Source)) attack[CK.Source] = Js.S(taggedSource);
            var types = tags.Where(t => t.StartsWith(CV.TagDamage, StringComparison.Ordinal)).Select(t => t.Substring(CV.TagDamage.Length)).ToList();
            if (!attack.Has(CK.Components) && !attack.Is(CK.DamageType) && types.Count == 1) attack[CK.DamageType] = types[0];
            if (!attack.Has(CK.Components) && !attack.Is(CK.DamageType) && types.Count > 1) throw new InvalidOperationException(DM.AttackMixedTags);
            var declared = attack.Arr(CK.Components) != null ? attack.Arr(CK.Components).Select(x => Js.Str(Js.Get(x, K.Type))).ToList()
                : attack.Is(CK.DamageType) ? new List<string> { attack.Str(CK.DamageType) } : new List<string>();
            if (types.Any(t => !declared.Contains(t))) throw new InvalidOperationException(DM.AttackTagsContradict);
            return attack;
        }

        /// <summary>dominantDamageType(components): the heaviest component's type, the first listed on a tie.</summary>
        public static string Dominant(JArray components)
        {
            JToken best = null;
            foreach (var x in Js.Items(components)) if (best == null || Js.D(Js.Get(x, CK.Weight)) > Js.D(Js.Get(best, CK.Weight))) best = x;
            return Js.Str(Js.Get(best, K.Type));
        }

        /// <summary>weaponDamageType(attack, family, damageType): the piece's type when the card's rule allows its family and type, else the rule's first type.</summary>
        public static string WeaponDamageType(JObject attack, string family, string damageType)
        {
            var rule = attack?.Obj(DK.WeaponDamage);
            if (rule == null || damageType == null) return damageType;
            return Js.Includes(rule[DK.Families], family) && Js.Includes(rule[DK.Types], damageType) ? damageType : Js.Str(Js.Items(rule[DK.Types]).FirstOrDefault());
        }

        /// <summary>weaponDamageComponents(attack, family, damageType): the mixed card's components with its weapon part typed as the piece resolves it.</summary>
        public static JArray WeaponDamageComponents(JObject attack, string family, string damageType)
        {
            var components = attack?.Arr(CK.Components);
            if (components == null || attack.Obj(DK.WeaponDamage) == null) return components;
            var type = WeaponDamageType(attack, family, damageType);
            if (type == null) return components;
            var weaponTypes = attack.Obj(DK.WeaponDamage)[DK.Types];
            return new JArray(components.Select(x => Js.Includes(weaponTypes, Js.Str(Js.Get(x, K.Type))) ? Js.Spread((JObject)x, new JObject { [K.Type] = type }) : (JObject)x.DeepClone()));
        }

        /// <summary>wieldedArmament(ctx, carrier, source, kind): the piece lending the card, else the first piece of that kind in its hand.</summary>
        public static JObject WieldedArmament(CombatState c, JObject carrier, JObject source, string kind)
        {
            var pieces = Js.Items(c.Data.Equipment[K.Armaments]).OfType<JObject>();
            var id = carrier?.Str(K.SourceArmamentId);
            JObject piece = Js.Truthy(carrier?[K.SourceArmamentId]) ? pieces.FirstOrDefault(row => row.Str(K.Id) == id) : null;
            if (piece == null && c.Loadout != null)
            {
                var hand = carrier?.Str(K.SourceHand);
                var want = hand == DV.HandOff || hand == V.Left ? V.Left : V.Right;
                foreach (var slot in Js.Items(c.Data.Equipment[K.Slots]).OfType<JObject>())
                {
                    if (slot.Str(K.Hand) != want) continue;
                    var equipped = Equipment.EquippedIn(c.Data, c.Loadout, source?.Str(K.ClassId), slot.Str(K.Id));
                    if (equipped != null && Descriptor(equipped).Str(CK.Source) == kind) { piece = equipped; break; }
                }
            }
            return piece;
        }
    }
}
