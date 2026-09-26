using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using Op = Ashen.Generated.CombatOps;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>The end-of-turn discard choice a hand-rules fight may ask for (shipped discardChoicePlan).</summary>
    public sealed class DiscardPlan
    {
        public List<JObject> Cards = new List<JObject>();
        public double Minimum;
        public double Maximum;
        public bool Prompt;
    }

    /// <summary>
    /// The configurable hand rules (shipped engine/handRules.js and model/handRules.js scaledCards): opening and
    /// turn draws, hand capacity, retention and the optional end-of-turn discard choice.
    /// </summary>
    public static class HandRules
    {
        /// <summary>scaledCards(rule, attributes): base + floor(max(0, stat − baseline) / pointsPerCard), clamped.</summary>
        public static double ScaledCards(JObject rule, JObject attributes)
        {
            var bonus = rule.Is(K.StatEnabled)
                ? Math.Floor(Math.Max(0, Js.Or0(attributes?[rule.Str(K.Stat) ?? string.Empty]) - rule.Num(K.Baseline)) / rule.Num(K.PointsPerCard))
                : 0;
            return Math.Min(rule.Num(K.Maximum), Math.Max(rule.Num(K.Minimum), rule.Num(K.Base) + bonus));
        }

        public static double TurnDrawCount(CombatState c)
        {
            var opening = c.Turn == 1;
            var rules = c.HandRules;
            if (rules == null) return !Js.Nullish(c.DrawPerTurn) ? Js.D(c.DrawPerTurn) : c.Player.Num(K.DrawPerTurn);
            c.HandMax = ScaledCards(rules.Obj(K.Capacity), c.Attributes);
            var room = Math.Max(0, c.HandMax - c.Piles.Hand.Count);
            var wanted = opening ? ScaledCards(rules.Obj(K.Starting), c.Attributes)
                : rules.Str(K.DrawMode) == V.DrawFill ? room : ScaledCards(rules.Obj(K.Turn), c.Attributes) + c.PendingDiscardDraw;
            c.PendingDiscardDraw = 0;
            return Math.Min(room, wanted);
        }

        public static string EndTurnCardFate(CombatState c, JObject card)
        {
            var def = Cards.Resolve(c.Data, card);
            var fate = Framework.EndTurnFate(c.Data, def);
            return fate == V.Discard && c.HandRules != null && c.HandRules.Is(K.Retain) ? V.Keep : fate;
        }

        public static DiscardPlan Plan(CombatState c)
        {
            var plan = new DiscardPlan();
            if (c.HandRules == null) return plan;
            plan.Cards = c.Piles.Hand.Where(card => EndTurnCardFate(c, card) == V.Keep).ToList();
            var capacity = ScaledCards(c.HandRules.Obj(K.Capacity), c.Attributes);
            plan.Minimum = c.HandRules.Str(K.Overflow) == V.Discard ? Math.Max(0, plan.Cards.Count - capacity) : 0;
            var optional = c.HandRules.Is(K.Retain) && c.HandRules.Is(K.PromptDiscard);
            plan.Maximum = Math.Min(plan.Cards.Count, Math.Max(plan.Minimum, optional ? c.HandRules.Num(K.DiscardLimit) : 0));
            plan.Prompt = plan.Minimum > 0 || (optional && plan.Maximum > 0);
            return plan;
        }

        public static void ValidateDiscardChoice(CombatState c, IReadOnlyList<string> ids)
        {
            var plan = Plan(c);
            var distinct = new HashSet<string>(ids, StringComparer.Ordinal).Count == ids.Count;
            if (!distinct || ids.Any(id => !plan.Cards.Any(card => card.Str(K.InstanceId) == id)) || ids.Count > plan.Maximum || ids.Count < plan.Minimum)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.DiscardSelection, plan.Minimum, plan.Maximum));
        }

        public static void ApplyDiscardChoice(CombatState c, IReadOnlyList<string> ids)
        {
            double discarded = 0;
            foreach (var id in ids)
            {
                var index = c.Piles.Hand.FindIndex(card => card.Str(K.InstanceId) == id);
                if (index < 0) continue;
                var card = c.Piles.Hand[index];
                if (EndTurnCardFate(c, card) != V.Keep) continue;
                c.Piles.Hand.RemoveAt(index);
                c.Piles.Discard.Add(card);
                c.Emit(E.CardDiscarded, Js.Obj(K.CardInstanceId, card[K.InstanceId], K.CardId, card[K.CardId], K.Reason, V.ReasonChoice));
                discarded++;
            }
            c.PendingDiscardDraw = c.HandRules != null && c.HandRules.Is(K.ReplaceDiscards) ? discarded : 0;
        }
    }

    /// <summary>
    /// Property carriers (shipped engine/properties.js): worn equipment, held relics and the class card mount the
    /// property rules their tags confer; the mounts react like relics and confer passives. Mounts are never saved —
    /// they are re-derived on restore, and fire in sorted source order either way.
    /// </summary>
    public static class Properties
    {
        private static string SourceKey(string kind, string instanceId) => kind + V.KeySeparator + instanceId;

        /// <summary>carrierRules: each tag's rule, kept only if its requires are held and its excludes are not.</summary>
        public static List<JObject> CarrierRules(CombatData data, IReadOnlyList<string> tagIds)
        {
            var held = new HashSet<string>(tagIds, StringComparer.Ordinal);
            var rules = new List<JObject>();
            foreach (var tag in tagIds)
            {
                var rule = data.PropertyRules.Get(tag);
                if (Js.Items(rule[K.Requires]).Any(t => !held.Contains(Js.Str(t)))) continue;
                if (Js.Items(rule[K.Excludes]).Any(t => held.Contains(Js.Str(t)))) continue;
                rules.Add(rule);
            }
            return rules;
        }

        public static OrderedMap<PropertyMount> MountsOf(CombatState c, JObject entity)
        {
            if (entity == null || c.PropertyMounts == null) return null;
            return c.PropertyMounts.TryGetValue(Triggers.OwnerKey(entity), out var m) ? m : null;
        }

        private static void Mount(CombatState c, string ownerKey, string kind, string id, string instanceId, IReadOnlyList<string> tagIds, IEnumerable<string> scopeTags)
        {
            var rules = CarrierRules(c.Data, tagIds);
            if (rules.Count == 0) return;
            var sourceKey = SourceKey(kind, instanceId ?? id);
            c.PropertyMounts ??= new OrderedMap<OrderedMap<PropertyMount>>();
            if (!c.PropertyMounts.TryGetValue(ownerKey, out var owned))
            {
                owned = new OrderedMap<PropertyMount>();
                c.PropertyMounts[ownerKey] = owned;
            }
            if (owned.ContainsKey(sourceKey)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.AlreadyMounted, sourceKey, ownerKey));
            owned[sourceKey] = new PropertyMount { Kind = kind, Id = id, InstanceId = instanceId ?? id, Rules = rules, ScopeTags = (scopeTags ?? Enumerable.Empty<string>()).ToList() };
        }

        private static bool IsMounted(CombatState c, string ownerKey, string kind, string instanceId) =>
            c.PropertyMounts != null && c.PropertyMounts.TryGetValue(ownerKey, out var owned) && owned.ContainsKey(SourceKey(kind, instanceId));

        private static List<string> Strings(JToken array) => Js.Items(array).Select(Js.Str).ToList();

        public static void SyncLoadout(CombatState c)
        {
            var owner = c.Player;
            if (owner == null) return;
            var ownerKey = Triggers.OwnerKey(owner);
            var wanted = new List<(string Kind, string Id, string InstanceId, List<string> Tags)>();
            if (c.Loadout != null)
                foreach (var piece in Equipment.EquippedPieces(c.Data, c.Loadout, owner.Str(K.ClassId), c.ItemUpgradeLevels ?? new JObject()))
                {
                    var tags = Strings(piece[K.PropertyTags]);
                    if (tags.Count == 0) continue;
                    wanted.Add((piece.Str(K.Kind) == V.Armor ? V.ArmourKind : V.ArmamentRefPrefix, piece.Str(K.Id), Equipment.PieceItemRef(piece), tags));
                }
            var wantedKeys = new HashSet<string>(wanted.Select(w => SourceKey(w.Kind, w.InstanceId ?? w.Id)), StringComparer.Ordinal);
            if (c.PropertyMounts != null && c.PropertyMounts.TryGetValue(ownerKey, out var current))
            {
                foreach (var entry in current.Entries())
                {
                    var loadoutKind = entry.Value.Kind == V.ArmourKind || entry.Value.Kind == V.ArmamentRefPrefix;
                    if (loadoutKind && !wantedKeys.Contains(entry.Key)) current.Remove(entry.Key);
                }
                if (current.Count == 0) c.PropertyMounts.Remove(ownerKey);
            }
            foreach (var w in wanted)
                if (!IsMounted(c, ownerKey, w.Kind, w.InstanceId ?? w.Id)) Mount(c, ownerKey, w.Kind, w.Id, w.InstanceId, w.Tags, null);
        }

        public static void SyncRelics(CombatState c)
        {
            var owner = c.Player;
            if (owner == null) return;
            var ownerKey = Triggers.OwnerKey(owner);
            foreach (var relicToken in Js.Items(owner[K.RelicIds]))
            {
                var relicId = Js.Str(relicToken);
                var tags = Strings(c.Data.Relics.Get(relicId)[K.PropertyTags]);
                if (tags.Count == 0) continue;
                if (!IsMounted(c, ownerKey, V.RelicKind, relicId)) Mount(c, ownerKey, V.RelicKind, relicId, relicId, tags, null);
            }
        }

        public static void SyncClass(CombatState c)
        {
            var owner = c.Player;
            var classId = owner?.Str(K.ClassId);
            if (string.IsNullOrEmpty(classId)) return;
            var ownerKey = Triggers.OwnerKey(owner);
            var def = c.Data.Classes.Has(classId) ? c.Data.Classes.Get(classId) : null;
            var own = Strings(def?[K.PropertyTags]);
            var tree = new HashSet<string>(c.Data.ClassTree.OfType<JObject>().Where(r => r.Str(K.ClassId) == classId).Select(r => r.Str(K.NodeId)), StringComparer.Ordinal);
            var picked = Strings(c.CoreTags).Where(id => tree.Contains(id) && c.Data.PropertyRules.Has(id) && !own.Contains(id));
            var tagIds = own.Concat(picked).ToList();
            if (tagIds.Count == 0 || def == null) return;
            if (!IsMounted(c, ownerKey, V.ClassKind, classId)) Mount(c, ownerKey, V.ClassKind, classId, classId, tagIds, Strings(def[K.Tags]));
        }
    }
}
