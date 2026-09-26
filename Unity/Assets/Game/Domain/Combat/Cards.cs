using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// Card definitions vs instances (shipped model/registries.js resolveCard and its helpers, model/tree.js
    /// cardKind, model/attackCardDamage.js cardIsMagical/cardForSchool, model/loadout.js applyCardMods): what a card
    /// instance resolves to, and the relic/property passives the engine consults.
    /// </summary>
    public static class Cards
    {
        private static readonly Regex ModPattern = new Regex(Ashen.Generated.CombatPatterns.CardMod, RegexOptions.CultureInvariant);
        private static readonly Regex TokenPattern = new Regex(Ashen.Generated.CombatPatterns.TextToken, RegexOptions.CultureInvariant);

        /// <summary>resolveCard(registries, instance): the effective card def (the registry row when nothing changes it).</summary>
        public static JObject Resolve(CombatData data, JObject inst)
        {
            var cardId = inst.Str(K.CardId);
            var baseDef = data.Cards.Get(cardId);
            var mods = inst.Arr(K.Mods);
            var profileId = inst.Str(K.ProfileId);
            var smithingLevel = Js.IsInt(inst[K.SmithingLevel]) ? inst.Num(K.SmithingLevel) : 0;
            var sourceArmamentId = inst.Str(K.SourceArmamentId) ?? string.Empty;
            if (smithingLevel < 0) throw new InvalidOperationException(M.NegativeSmithingLevel);
            var hasCarrier = Js.IsStr(inst[K.DamageSchool]) || Js.IsInt(inst[K.ExposureBuildupPerHit]);
            var upgraded = inst.Is(K.Upgraded);
            var hasMods = mods != null && mods.Count > 0;
            if (!upgraded && !hasMods && string.IsNullOrEmpty(profileId) && !hasCarrier && smithingLevel == 0) return baseDef;

            var key = string.Join(V.CacheKeySeparator, cardId, upgraded ? V.One : V.Zero, profileId ?? string.Empty,
                hasMods ? string.Join(V.ListSeparator, mods.Select(m => Js.Str(m))) : string.Empty,
                inst.Str(K.DamageSchool) ?? string.Empty,
                Js.Nullish(inst[K.ExposureBuildupPerHit]) ? string.Empty : Js.D(inst[K.ExposureBuildupPerHit]).ToString(CultureInfo.InvariantCulture),
                sourceArmamentId, smithingLevel.ToString(CultureInfo.InvariantCulture));
            if (data.ResolveCache.TryGetValue(key, out var hit)) return hit;

            var profile = !string.IsNullOrEmpty(profileId)
                ? Js.Items(data.Equipment[K.BasicCardProfiles]).OfType<JObject>().FirstOrDefault(p => p.Str(K.Id) == profileId)
                : null;
            var school = Js.IsStr(inst[K.DamageSchool]) ? inst.Str(K.DamageSchool) : profile?.Str(K.DamageSchool);
            var result = CardForSchool(data, baseDef, school);
            if (upgraded) result = MergeUpgrade(result);
            if (!string.IsNullOrEmpty(profileId)) result = ApplyBasicCardProfile(result, profile);
            if (hasMods)
                result = ApplyCardMods(result, mods, data.Equipment.Obj(K.ModFields) ?? new JObject(), data.Balance.Obj(K.Equipment)?.Obj(K.Limits) ?? new JObject(), data);
            if (hasCarrier)
            {
                result = Js.Spread(result);
                if (Js.IsStr(inst[K.DamageSchool])) result[K.DamageSchool] = inst[K.DamageSchool].DeepClone();
                if (Js.IsInt(inst[K.ExposureBuildupPerHit])) result[K.ExposureBuildupPerHit] = inst[K.ExposureBuildupPerHit].DeepClone();
            }
            if (smithingLevel > 0) throw new NotSupportedException(M.SmithedCardsDeferred);
            data.ResolveCache[key] = result;
            return result;
        }

        private static JObject MergeUpgrade(JObject baseDef)
        {
            var up = baseDef.Obj(K.Upgrade) ?? new JObject();
            var result = Js.Spread(baseDef, up);
            result[K.Name] = !Js.Nullish(up[K.Name]) ? up[K.Name].DeepClone() : Js.S(baseDef.Str(K.Name) + V.UpgradeSuffix);
            result[K.Keywords] = !Js.Nullish(up[K.Keywords]) ? up[K.Keywords].DeepClone() : (Js.Truthy(baseDef[K.Keywords]) ? baseDef[K.Keywords].DeepClone() : new JArray());
            SetOrRemove(result, K.Effects, !Js.Nullish(up[K.Effects]) ? up[K.Effects] : baseDef[K.Effects]);
            SetOrRemove(result, K.TextTemplate, !Js.Nullish(up[K.TextTemplate]) ? up[K.TextTemplate] : baseDef[K.TextTemplate]);
            result[K.Upgraded] = true;
            return result;
        }

        private static JObject ApplyBasicCardProfile(JObject def, JObject profile)
        {
            if (profile == null) return def;
            var tags = profile[K.Tags] is JArray t ? (JArray)t.DeepClone() : new JArray();
            var effects = new JArray();
            foreach (var e in Js.Items(def[K.Effects]).OfType<JObject>())
            {
                var copy = Js.Spread(e);
                if (e.Str(K.Op) == Op.Damage) copy[K.Tags] = tags.DeepClone();
                effects.Add(copy);
            }
            var result = Js.Spread(def);
            SetOrRemove(result, K.Name, profile[K.DisplayName]);
            SetOrRemove(result, K.Icon, profile[K.Icon]);
            SetOrRemove(result, K.Flavor, Js.Truthy(profile[K.Flavor]) ? profile[K.Flavor] : def[K.Flavor]);
            SetOrRemove(result, K.DamageSchool, profile[K.DamageSchool]);
            SetOrRemove(result, K.ExposureBuildupPerHit, profile[K.ExposureBuildupPerHit]);
            result[K.CardTags] = tags;
            result[K.Effects] = effects;
            SetOrRemove(result, K.EquipmentProfileId, profile[K.Id]);
            SetOrRemove(result, K.EquipmentRole, profile[K.Role]);
            return result;
        }

        /// <summary><c>{ ...o, key: value }</c> where value may be undefined (JS keeps the key; JSON drops it).</summary>
        internal static void SetOrRemove(JObject o, string key, JToken value)
        {
            if (value == null || value.Type == JTokenType.Undefined) o.Remove(key);
            else o[key] = value.DeepClone();
        }

        /// <summary>The card as it resolves in a damage school (the projected face for that side of the line).</summary>
        public static JObject CardForSchool(CombatData data, JObject def, string school)
        {
            var variant = def?.Obj(K.SchoolVariant);
            if (variant == null || school == null) return def;
            var probe = Js.Spread(def);
            probe[K.DamageSchool] = school;
            if (IsMagical(data, probe) != variant.Is(K.Magical)) return def;
            var resolved = Js.Spread(def);
            resolved.Remove(K.SchoolVariant);
            resolved.Remove(K.CardRatingValues);
            SetOrRemove(resolved, K.Effects, variant[K.Effects]);
            if (variant[K.CardRatingValues] != null && Js.Truthy(variant[K.CardRatingValues])) resolved[K.CardRatingValues] = variant[K.CardRatingValues].DeepClone();
            if (def[K.Upgrade] is JObject defUp)
            {
                var up = Js.Spread(defUp);
                up.Remove(K.CardRatingValues);
                var vUp = variant.Obj(K.Upgrade);
                SetOrRemove(up, K.Effects, vUp?[K.Effects]);
                if (Js.Truthy(vUp?[K.CardRatingValues])) up[K.CardRatingValues] = vUp[K.CardRatingValues].DeepClone();
                resolved[K.Upgrade] = up;
            }
            return resolved;
        }

        /// <summary>cardIsMagical(face): an explicit school decides; else a magical tag; else a mana cost.</summary>
        public static bool IsMagical(CombatData data, JObject face)
        {
            if (face == null) return false;
            if (Js.Truthy(face[K.DamageSchool])) return face.Str(K.DamageSchool) != V.Physical;
            var tags = Js.Truthy(face[K.CardTags]) ? face[K.CardTags] : face[K.Tags];
            var magical = data.Engine[K.MagicalTags] as JArray;
            foreach (var tag in Js.Items(Js.Truthy(tags) ? tags : null))
            {
                var text = Js.IsStr(tag) ? Js.Str(tag) : Js.Str(Js.Get(tag, K.Id)) ?? V.Undefined;
                var parts = text.Split(V.TagSeparator[0]);
                if (Js.Includes(magical, parts[parts.Length - 1])) return true;
            }
            return face.Or0(K.ManaCost) > 0;
        }

        /// <summary>cardKind(def): the card's kind read off its classification tag (null when it carries none).</summary>
        public static string Kind(CombatData data, JObject def)
        {
            var map = data.Map(K.CardProperties, K.Kind);
            foreach (var id in Js.Items(def?[K.KindIds]))
            {
                var kind = map.Str(Js.Str(id) ?? string.Empty);
                if (kind != null) return kind;
            }
            return null;
        }

        /// <summary>applyCardMods(def, mods, { modFields, limits }) — equipment mods layered onto a card, in order.</summary>
        public static JObject ApplyCardMods(JObject def, JArray mods, JObject fields, JObject limits, CombatData data)
        {
            if (mods == null || mods.Count == 0) return def;
            var effects = new JArray(Js.Items(def[K.Effects]).OfType<JObject>().Select(e => (JToken)Js.Spread(e)));
            var cost = def[K.Cost]?.DeepClone();
            var touched = new List<JObject>();
            foreach (var raw in mods)
            {
                var mod = ParseMod(V.ModPrefix + Js.Str(raw));
                var spec = mod == null ? null : fields.Obj(mod.Field);
                if (spec == null) continue;
                touched.Add(spec);
                var apply = spec.Str(K.Apply);
                if (apply == V.ApplyCost)
                {
                    cost = Js.N(Adjust(cost, mod, Js.Coalesce(limits[K.MinCost], 0)));
                    continue;
                }
                if (apply == V.ApplyScale)
                {
                    foreach (var e in effects.OfType<JObject>())
                    {
                        if (Js.IsNum(e[K.Amount])) e.Put(K.Amount, Math.Max(0, e.Num(K.Amount) + mod.Value));
                        if (Js.IsNum(e[K.Stacks])) e.Put(K.Stacks, Math.Max(0, e.Num(K.Stacks) + mod.Value));
                    }
                    continue;
                }
                var specOp = spec.Str(K.Op);
                if (apply == V.ApplyAmount)
                {
                    var i = FirstIndexOfOp(effects, specOp);
                    var floorToken = specOp == Op.Damage ? limits[K.MinDamage] : specOp == Op.Block ? limits[K.MinBlock] : Js.N(0);
                    if (i == -1)
                    {
                        if (mod.Value > 0) effects.Add(Js.Obj(K.Op, specOp, K.Target, spec[K.EffTarget], K.Amount, mod.Value));
                    }
                    else if (Js.IsNum(effects[i][K.Amount]))
                    {
                        ((JObject)effects[i]).Put(K.Amount, Adjust(effects[i][K.Amount], mod, Js.Coalesce(floorToken, 0)));
                    }
                    continue;
                }
                if (apply == V.ApplyHits)
                {
                    var i = FirstIndexOfOp(effects, specOp);
                    if (i == -1) continue;
                    var max = Js.Coalesce(limits[K.MaxHits], data.Rule(K.Defaults, K.MaxHits));
                    var hitsToken = Js.Nullish(effects[i][K.Hits]) ? Js.N(1) : effects[i][K.Hits];
                    var n = Adjust(hitsToken, mod, 1);
                    ((JObject)effects[i]).Put(K.Hits, Math.Min(max, n));
                    continue;
                }
                if (apply == V.ApplyStatus)
                {
                    var statusId = spec.Str(K.Status);
                    var i = -1;
                    for (var j = 0; j < effects.Count; j++)
                        if (((JObject)effects[j]).Str(K.Op) == Op.ApplyStatus && ((JObject)effects[j]).Str(K.Status) == statusId) { i = j; break; }
                    if (i == -1)
                    {
                        if (mod.Value > 0) effects.Add(Js.Obj(K.Op, Op.ApplyStatus, K.Target, spec[K.EffTarget], K.Status, statusId, K.Stacks, mod.Value));
                    }
                    else if (Js.IsNum(effects[i][K.Stacks]))
                    {
                        ((JObject)effects[i]).Put(K.Stacks, Adjust(effects[i][K.Stacks], mod, 0));
                    }
                }
            }

            var textTemplate = def.Str(K.TextTemplate);
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in TokenPattern.Matches(textTemplate ?? string.Empty)) present.Add(m.Groups[V.GroupToken].Value);
            foreach (var spec in touched)
            {
                var clause = spec.Str(K.Clause);
                if (string.IsNullOrEmpty(clause)) continue;
                var tok = TokenPattern.Match(clause);
                if (!tok.Success || present.Contains(tok.Groups[V.GroupToken].Value)) continue;
                present.Add(tok.Groups[V.GroupToken].Value);
                textTemplate = (textTemplate + V.Space + clause).Trim();
            }

            var result = Js.Spread(def);
            SetOrRemove(result, K.Cost, cost);
            result[K.Effects] = effects;
            SetOrRemove(result, K.TextTemplate, textTemplate == null ? null : Js.S(textTemplate));
            result[K.EquipMods] = mods.DeepClone();
            return result;
        }

        private static int FirstIndexOfOp(JArray effects, string op)
        {
            for (var i = 0; i < effects.Count; i++) if (((JObject)effects[i]).Str(K.Op) == op) return i;
            return -1;
        }

        private static double Adjust(JToken current, CardMod mod, double floor)
        {
            var next = mod.Add ? (Js.IsNum(current) ? Js.D(current) : 0) + mod.Value : mod.Value;
            return Math.Max(floor, next);
        }

        private sealed class CardMod
        {
            public string Field;
            public bool Add;
            public double Value;
        }

        private static CardMod ParseMod(string text)
        {
            var m = ModPattern.Match(text.Trim());
            if (!m.Success) return null;
            var sign = m.Groups[V.GroupSign].Value;
            var num = double.Parse(m.Groups[V.GroupNumber].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            return new CardMod { Field = m.Groups[V.GroupField].Value, Add = sign.Length > 0, Value = sign == V.Minus ? -num : num };
        }

        // ------------------------------------------------------------------ passives

        private static IEnumerable<JToken> MountedPassiveValues(OrderedMap<PropertyMount> mounts, string key)
        {
            if (mounts == null) yield break;
            foreach (var sourceKey in mounts.SortedKeys())
                foreach (var rule in mounts[sourceKey].Rules)
                {
                    var p = rule.Obj(K.Passives);
                    if (p != null && p[key] != null) yield return p[key];
                }
        }

        /// <summary>Product of a multiplicative passive across owned relics and mounted properties (default 1).</summary>
        public static double PassiveMult(CombatData data, JArray relicIds, string key, OrderedMap<PropertyMount> mounts)
        {
            double m = 1;
            foreach (var id in Js.Items(relicIds))
            {
                var p = data.Relics.Get(Js.Str(id)).Obj(K.Passives);
                if (p != null && Js.IsNum(p[key])) m *= p.Num(key);
            }
            foreach (var v in MountedPassiveValues(mounts, key)) if (Js.IsNum(v)) m *= Js.D(v);
            return m;
        }

        /// <summary>Sum of an additive passive across owned (upgrade-resolved) relics and mounted properties (default 0).</summary>
        public static double PassiveSum(CombatData data, JArray relicIds, string key, JObject itemUpgradeLevels, OrderedMap<PropertyMount> mounts)
        {
            double s = 0;
            foreach (var id in Js.Items(relicIds))
            {
                var itemRef = V.RelicRefPrefix + V.ItemRefSeparator + Js.Str(id);
                var p = Equipment.ResolveUpgradedRelic(data, itemRef, Js.Or0(itemUpgradeLevels?[itemRef])).Obj(K.Passives);
                if (p != null && Js.IsNum(p[key])) s += p.Num(key);
            }
            foreach (var v in MountedPassiveValues(mounts, key)) if (Js.IsNum(v)) s += Js.D(v);
            return s;
        }
    }
}
