using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RM = Ashen.Generated.RunMessages;
using RV = Ashen.Generated.RunValues;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Run
{
    /// <summary>
    /// Where an item's cards sit and what a smith has done to them (shipped model/cardMounts.js): the mount rules
    /// (balance.equipment.cardMounts, validated), the fallback card an emptied mount shows, the smith overrides and
    /// the filled extra mounts. A run without <c>itemMounts</c> composes exactly as authored.
    /// </summary>
    public static class CardMounts
    {
        private static readonly Regex ItemRefPattern = new Regex(Ashen.Generated.RunPatterns.ItemRef, RegexOptions.CultureInvariant);

        private static List<string> Kinds(RunData d) => d.RuleList(RK.Mounts, K.Kinds);

        /// <summary>ownerItemRef(inst): the namespaced item an instance rides with, or null (run-owned, empty hand).</summary>
        public static string OwnerItemRef(JObject inst)
        {
            var by = inst?.Str(K.GrantedBy);
            if (string.IsNullOrEmpty(by)) return null;
            if (by.StartsWith(RV.Unarmed + V.KeySeparator, StringComparison.Ordinal)) return null;
            return by.Contains(V.ItemRefSeparator) ? by : V.ArmamentRefPrefix + V.ItemRefSeparator + by;
        }

        private static JObject OwnObject(JToken value, string label)
        {
            if (!(value is JObject o)) throw new InvalidOperationException(RunJs.Fmt(RM.MustBeObjectLabel, label));
            return o;
        }

        private static string NonEmptyString(JToken value, string label)
        {
            if (!Js.IsStr(value) || Js.Str(value).Length == 0) throw new InvalidOperationException(RunJs.Fmt(RM.MustBeNonEmptyStringLabel, label));
            return Js.Str(value);
        }

        private static JArray StringList(JToken value, string label)
        {
            if (!(value is JArray a) || a.Any(e => !Js.IsStr(e) || Js.Str(e).Length == 0)) throw new InvalidOperationException(RunJs.Fmt(RM.MustBeStringListLabel, label));
            return a;
        }

        private static JObject NormalizeFallback(JToken raw, string label)
        {
            if (raw != null && raw.Type == JTokenType.Null) return null;
            var src = OwnObject(raw, label);
            var keys = src.Properties().Select(p => p.Name).ToList();
            if (keys.Count != 1 || !(keys[0] == K.CardId || keys[0] == RK.UnarmedProfile)) throw new InvalidOperationException(RunJs.Fmt(RM.FallbackExactlyOne, label));
            return new JObject { [keys[0]] = NonEmptyString(src[keys[0]], label + RV.PathDot + keys[0]) };
        }

        /// <summary>
        /// cardMountRules(registries): balance.equipment.cardMounts validated (normalizeCardMountRules), or the inert
        /// rules when none is authored. Shape: { extractableTag, kinds: { kind: { accepts, fallback } }, fallbackByItem,
        /// extraMounts: { enabled, perItem, kind } }.
        /// </summary>
        public static JObject Rules(RunData d)
        {
            var raw = d.EquipmentBalance[RK.CardMounts];
            var kinds = Kinds(d);
            if (Js.Nullish(raw))
            {
                var inertKinds = new JObject();
                foreach (var kind in kinds) inertKinds[kind] = Js.Obj(RK.Accepts, new JArray(), RK.Fallback, Js.Null());
                return Js.Obj(RK.ExtractableTag, Js.Null(), K.Kinds, inertKinds, RK.FallbackByItem, new JObject(),
                    RK.ExtraMounts, Js.Obj(K.Enabled, false, RK.PerItem, 0, K.Kind, RV.RoleGranted));
            }
            var root = K.Equipment + RV.PathDot + RK.CardMounts;
            var src = OwnObject(raw, root);
            var extractableTag = NonEmptyString(src[RK.ExtractableTag], root + RV.PathDot + RK.ExtractableTag);
            var kindsRaw = OwnObject(src[K.Kinds], root + RV.PathDot + K.Kinds);
            var normalized = new JObject();
            foreach (var kind in kinds)
            {
                var label = root + RV.PathDot + K.Kinds + RV.PathDot + kind;
                var row = OwnObject(kindsRaw[kind], label);
                normalized[kind] = new JObject
                {
                    [RK.Accepts] = StringList(row[RK.Accepts], label + RV.PathDot + RK.Accepts).DeepClone(),
                    [RK.Fallback] = (JToken)NormalizeFallback(row[RK.Fallback] ?? Js.Null(), label + RV.PathDot + RK.Fallback) ?? Js.Null(),
                };
            }
            foreach (var p in kindsRaw.Properties())
                if (!kinds.Contains(p.Name)) throw new InvalidOperationException(RunJs.Fmt(RM.UnknownMountKind, root + RV.PathDot + K.Kinds + RV.PathDot + p.Name, string.Join(RV.ListJoiner, kinds)));
            var byItemRaw = src[RK.FallbackByItem] == null ? new JObject() : OwnObject(src[RK.FallbackByItem], root + RV.PathDot + RK.FallbackByItem);
            var byItem = new JObject();
            foreach (var item in byItemRaw.Properties())
            {
                var label = root + RV.PathDot + RK.FallbackByItem + RV.PathDot + item.Name;
                if (!ItemRefPattern.IsMatch(item.Name)) throw new InvalidOperationException(RunJs.Fmt(RM.NotNamespacedItemRef, label));
                var rows = OwnObject(item.Value, label);
                var perKind = new JObject();
                foreach (var r in rows.Properties())
                {
                    if (!kinds.Contains(r.Name)) throw new InvalidOperationException(RunJs.Fmt(RM.UnknownMountKindShort, label + RV.PathDot + r.Name));
                    perKind[r.Name] = (JToken)NormalizeFallback(r.Value, label + RV.PathDot + r.Name) ?? Js.Null();
                }
                byItem[item.Name] = perKind;
            }
            var extraLabel = root + RV.PathDot + RK.ExtraMounts;
            var extra = OwnObject(src[RK.ExtraMounts], extraLabel);
            if (extra[K.Enabled]?.Type != JTokenType.Boolean) throw new InvalidOperationException(RunJs.Fmt(RM.MustBeBooleanLabel, extraLabel + RV.PathDot + K.Enabled));
            if (!Js.IsInt(extra[RK.PerItem]) || extra.Num(RK.PerItem) < 0) throw new InvalidOperationException(RunJs.Fmt(RM.MustBeNonNegativeIntegerLabel, extraLabel + RV.PathDot + RK.PerItem));
            if (!kinds.Contains(extra.Str(K.Kind))) throw new InvalidOperationException(RunJs.Fmt(RM.ExtraMountKind, extraLabel + RV.PathDot + K.Kind, string.Join(RV.ListJoiner, kinds)));
            return Js.Obj(RK.ExtractableTag, extractableTag, K.Kinds, normalized, RK.FallbackByItem, byItem,
                RK.ExtraMounts, Js.Obj(K.Enabled, extra[K.Enabled], RK.PerItem, extra[RK.PerItem], K.Kind, extra[K.Kind]));
        }

        /// <summary>resolveFallbackCard: the card an emptied mount shows (item override, else its kind's), or null.</summary>
        public static string ResolveFallbackCard(RunData d, JObject rules, string itemRef, string kind)
        {
            var perItem = rules.Obj(RK.FallbackByItem).Obj(itemRef);
            var fallback = perItem != null && perItem[kind] != null ? perItem[kind] : rules.Obj(K.Kinds).Obj(kind ?? V.Undefined)?[RK.Fallback];
            if (!(fallback is JObject f)) return null;
            if (Js.Truthy(f[K.CardId])) return f.Str(K.CardId);
            var profileId = d.EquipmentBalance.Obj(RK.UnarmedProfiles)?[f.Str(RK.UnarmedProfile) ?? V.Undefined];
            var profile = Loadout.ProfileById(d, profileId);
            return profile != null && Js.Truthy(profile[RK.BaseCardId]) ? profile.Str(RK.BaseCardId) : null;
        }

        /// <summary>itemMountEntries(run, itemRef): the smith's record for one item, or an empty object.</summary>
        public static JObject ItemMountEntries(JObject itemMounts, string itemRef) =>
            itemMounts?[itemRef ?? V.Undefined] as JObject ?? new JObject();

        /// <summary>isExtraMountKey(key): a mount key the extra-mount rule minted.</summary>
        public static bool IsExtraMountKey(string key) => key != null && key.StartsWith(RV.ExtraMountPrefix, StringComparison.Ordinal);

        /// <summary>openExtraMountKey(registries, run, itemRef): the next open extra mount of an item, or null when the flag or the cap says no.</summary>
        public static string OpenExtraMountKey(RunData d, JObject itemMounts, string itemRef)
        {
            var extra = Rules(d).Obj(RK.ExtraMounts);
            if (!extra.Is(K.Enabled)) return null;
            var entries = ItemMountEntries(itemMounts, itemRef);
            var used = entries.Properties().Count(p => IsExtraMountKey(p.Name) && Js.Truthy(p.Value) && Js.Truthy(Js.Get(p.Value, K.Card)));
            var perItem = extra.Num(RK.PerItem);
            if (used >= perItem) return null;
            for (var index = 0; index < perItem; index++)
            {
                var key = RV.ExtraMountPrefix + itemRef + V.KeySeparator + RunJs.NumStr(index);
                var entry = entries[key];
                if (!Js.Truthy(entry) || !Js.Truthy(Js.Get(entry, K.Card))) return key;
            }
            return null;
        }

        /// <summary>applyMountOverrides: emptied mounts show their fallback (or nothing), refilled ones the installed card.</summary>
        public static List<JObject> ApplyMountOverrides(RunData d, JObject itemMounts, List<JObject> desired)
        {
            var rules = Rules(d);
            var out_ = new List<JObject>();
            foreach (var inst in desired)
            {
                var itemRef = OwnerItemRef(inst);
                var entry = itemRef != null ? ItemMountEntries(itemMounts, itemRef)[inst.Str(K.InstanceId) ?? V.Undefined] : null;
                if (!Js.Truthy(entry))
                {
                    out_.Add(inst);
                    continue;
                }
                if (Js.Truthy(Js.Get(entry, K.Card)))
                {
                    var refilled = Js.Spread(inst);
                    refilled[K.CardId] = entry[K.Card].DeepClone();
                    refilled[K.Upgraded] = entry[K.Upgraded]?.Type == JTokenType.Boolean && entry.Value<bool>(K.Upgraded);
                    out_.Add(refilled);
                    continue;
                }
                var fallback = ResolveFallbackCard(d, rules, itemRef, inst.Str(K.EquipmentRole));
                if (fallback == null) continue;
                var shown = Js.Spread(inst);
                shown[K.CardId] = fallback;
                shown[K.Upgraded] = false;
                out_.Add(shown);
            }
            return out_;
        }

        /// <summary>extraMountInstances: one instance per recorded extra-mount entry naming a card.</summary>
        public static List<JObject> ExtraMountInstances(RunData d, JObject itemMounts, string itemRef, JToken grantSource)
        {
            var rules = Rules(d);
            var out_ = new List<JObject>();
            foreach (var p in ItemMountEntries(itemMounts, itemRef).Properties())
            {
                var entry = p.Value as JObject;
                if (!p.Name.StartsWith(RV.ExtraMountPrefix, StringComparison.Ordinal) || entry == null || !Js.Truthy(entry[K.Card])) continue;
                var inst = Js.Obj(K.InstanceId, p.Name, K.CardId, entry[K.Card], K.Upgraded, entry[K.Upgraded]?.Type == JTokenType.Boolean && entry.Value<bool>(K.Upgraded),
                    K.EquipmentRole, rules.Obj(RK.ExtraMounts)[K.Kind], K.GrantedBy, itemRef);
                if (Js.Truthy(grantSource)) inst[K.GrantSource] = grantSource.DeepClone();
                out_.Add(inst);
            }
            return out_;
        }
    }
}
