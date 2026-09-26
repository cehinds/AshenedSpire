using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using LM = Ashen.Generated.LoopMessages;
using LV = Ashen.Generated.LoopValues;
using RK = Ashen.Generated.RunKeys;
using RV = Ashen.Generated.RunValues;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;

namespace Ashen.Domain.Loop
{
    /// <summary>One Smithing candidate: its item and whether the run can pay for it, with the shipped candidate document.</summary>
    public sealed class SmithCandidate
    {
        public string ItemRef;
        public bool Affordable;
        public JObject Doc;
    }

    /// <summary>smithingPlan(registries, run): the stone balance and every owned item a tier can promote, with exact receipts.</summary>
    public sealed class SmithPlan
    {
        public double Stones;
        public List<SmithCandidate> Candidates = new List<SmithCandidate>();
    }

    /// <summary>A namespaced item ref's identity (shipped itemRefIdentity): armament/&lt;id&gt;, armor/&lt;class&gt;/&lt;id&gt; or relic/&lt;id&gt;.</summary>
    public sealed class ItemIdentity
    {
        public string ItemRef;
        public string ItemKind;
        public string ItemId;
        public string ClassId;
    }

    /// <summary>
    /// Smithing as a run-owned transaction over a namespaced item (shipped model/smithing.js smithingPlan, commitSmithing
    /// and restampSmithingCards, over model/itemUpgrades.js): the plan lists every owned item — the deck's sources, the
    /// carried armaments, the worn armour, the relics — whose next tier authors an effective change, priced in Smithing
    /// Stones; the commit revalidates through the plan, pays, records the tier, restamps the lent cards and keeps the
    /// receipt on the run (<c>lastSmithingReceipt</c>).
    /// </summary>
    public static class ItemSmithing
    {
        private static readonly Regex ArmamentRef = new Regex(Ashen.Generated.CombatPatterns.ArmamentRef, RegexOptions.CultureInvariant);
        private static readonly Regex ArmorRef = new Regex(Ashen.Generated.CombatPatterns.ArmorRef, RegexOptions.CultureInvariant);
        private static readonly Regex RelicRef = new Regex(Ashen.Generated.CombatPatterns.RelicRef, RegexOptions.CultureInvariant);

        private static string Ref(params string[] parts) => string.Join(V.ItemRefSeparator, parts);

        private static string ArmamentItemRef(string id) => Ref(V.ArmamentRefPrefix, id);

        /// <summary>itemRefIdentity(itemRef), or null for a ref that names no item kind.</summary>
        public static ItemIdentity Identity(string itemRef)
        {
            if (itemRef == null) return null;
            var m = ArmamentRef.Match(itemRef);
            if (m.Success) return new ItemIdentity { ItemRef = itemRef, ItemKind = V.ArmamentRefPrefix, ItemId = m.Groups[V.GroupId].Value };
            m = ArmorRef.Match(itemRef);
            if (m.Success) return new ItemIdentity { ItemRef = itemRef, ItemKind = V.Armor, ItemId = m.Groups[V.GroupId].Value, ClassId = m.Groups[V.GroupClassId].Value };
            m = RelicRef.Match(itemRef);
            if (m.Success) return new ItemIdentity { ItemRef = itemRef, ItemKind = V.RelicRefPrefix, ItemId = m.Groups[V.GroupId].Value };
            return null;
        }

        /// <summary>resolveUpgradedItem(registries, itemRef, level): the piece or relic at a tier.</summary>
        public static JObject ResolveItem(LoopData d, string itemRef, double level)
        {
            var identity = Identity(itemRef) ?? throw new InvalidOperationException(RunJs.Fmt(LM.UnknownItemRef, itemRef));
            return identity.ItemKind == V.RelicRefPrefix
                ? Combat.Equipment.ResolveUpgradedRelic(d.Combat, itemRef, level)
                : Combat.Equipment.ResolveUpgradedEquipment(d.Combat, itemRef, level);
        }

        /// <summary>itemByRef: the item at tier 0, or null when the ref resolves to nothing.</summary>
        public static JObject ItemByRef(LoopData d, string itemRef)
        {
            if (Identity(itemRef) == null) return null;
            try
            {
                return ResolveItem(d, itemRef, 0);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>itemUpgradeTiers(registries, itemRef): the authored tiers, ascending.</summary>
        public static List<double> Tiers(LoopData d, string itemRef) =>
            Js.Items(d.Combat.Equipment[K.ItemUpgradeChanges]).OfType<JObject>().Where(r => r.Str(K.ItemRef) == itemRef)
                .Select(r => r.Num(K.NextTier)).Distinct().OrderBy(t => t).ToList();

        /// <summary>itemUpgradeCost(rows): the tier's Smithing Stone price.</summary>
        public static double Cost(LoopData d, List<JObject> rows)
        {
            var costTag = d.Combat.Engine.Obj(K.ItemUpgrades).Str(K.CostTag);
            var row = rows.FirstOrDefault(r => r.Str(K.Tag) == costTag) ?? throw new InvalidOperationException(LM.TierMissingCost);
            return row.Num(K.Value);
        }

        private static double CumulativeRequirementDelta(LoopData d, string itemRef, string attributeId, double level)
        {
            double delta = 0;
            var tag = RunJs.Fmt(LV.RequirementTagFormat, attributeId);
            for (var tier = 1; tier <= level; tier += 1)
                foreach (var row in ItemUpgrades.Rows(d.Combat, itemRef, tier))
                    if (row.Str(K.Tag) == tag) delta += row.Num(K.Value);
            return delta;
        }

        private static JObject ArmamentById(LoopData d, string id) =>
            d.Run.EquipmentRows(K.Armaments).FirstOrDefault(p => p.Str(K.Id) == id);

        private static double StoneBalance(JObject run)
        {
            var value = run[RK.SmithingStones];
            if (Js.Nullish(value)) return 0;
            if (!Js.IsInt(value) || Js.D(value) < 0) throw new InvalidOperationException(LM.SmithingStonesNotInteger);
            return Js.D(value);
        }

        /// <summary>levels(run): run.itemUpgradeLevels, validated, with the legacy armamentLevels folded in.</summary>
        public static JObject Levels(JObject run)
        {
            var current = run[K.ItemUpgradeLevels];
            var legacy = run[RK.ArmamentLevels];
            if (!Js.Nullish(current) && !(current is JObject)) throw new InvalidOperationException(LM.ItemUpgradeLevelsNotObject);
            if (!Js.Nullish(legacy) && !(legacy is JObject)) throw new InvalidOperationException(LM.ArmamentLevelsNotObject);
            var result = Js.Spread(current as JObject);
            foreach (var p in result.Properties())
            {
                if (Identity(p.Name) == null) throw new InvalidOperationException(RunJs.Fmt(LM.LevelKeyNotItemRef, p.Name));
                if (!Js.IsInt(p.Value) || Js.D(p.Value) < 0) throw new InvalidOperationException(RunJs.Fmt(LM.LevelNotInteger, p.Name));
            }
            foreach (var p in (legacy as JObject ?? new JObject()).Properties())
            {
                if (!Js.IsInt(p.Value) || Js.D(p.Value) < 0) throw new InvalidOperationException(RunJs.Fmt(LM.LevelNotInteger, p.Name));
                var itemRef = ArmamentItemRef(p.Name);
                if (result[itemRef] != null && Js.D(result[itemRef]) != Js.D(p.Value)) throw new InvalidOperationException(RunJs.Fmt(LM.LevelConflict, p.Name, itemRef));
                result[itemRef] = p.Value.DeepClone();
            }
            return result;
        }

        /// <summary>sourceArmamentId(registries, run, instance): the armament that owns an equipment-bound instance, or null.</summary>
        public static string SourceArmamentId(LoopData d, JObject run, JObject instance)
        {
            string id = null;
            foreach (var key in new[] { K.SourceArmamentId, WK.ArmamentId, K.WeaponId })
            {
                if (instance == null || Js.Nullish(instance[key])) continue;
                id = RunJs.Key(instance[key]);
                if (ArmamentById(d, id) == null) throw new InvalidOperationException(RunJs.Fmt(LM.UnknownSourceArmament, id));
                return id;
            }
            if (instance == null || !Loadout.Roles(d.Run).Contains(instance.Str(K.EquipmentRole))) return null;
            var row = Loadout.RoleSource(d.Run, run.Obj(K.Loadout), run.Str(RK.Class), instance.Str(K.EquipmentRole));
            id = row.Piece?.Str(K.Id);
            return id != null && ArmamentById(d, id) != null ? id : null;
        }

        private static List<JObject> SourceCards(LoopData d, JObject run, string pieceId) =>
            Js.Items(run[K.Deck]).OfType<JObject>().Where(inst => SourceArmamentId(d, run, inst) == pieceId).ToList();

        private static JToken NumericEffect(JObject def, string op)
        {
            var effect = Js.Items(def[K.Effects]).OfType<JObject>().FirstOrDefault(e => e.Str(K.Op) == op);
            if (effect == null) return null;
            return RunJs.Coalesce(effect[K.Amount], effect[K.Stacks], effect[K.Hits]) ?? Js.Null();
        }

        private static JArray EffectsReceipt(JObject def) => new JArray(Js.Items(def[K.Effects]).OfType<JObject>().Select(e => (JToken)Js.Spread(e)));

        /// <summary>JS <c>===</c> on two receipt values (numbers as doubles, absent and null distinct).</summary>
        internal static bool StrictEquals(JToken a, JToken b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (Js.IsNum(a) && Js.IsNum(b)) return Js.D(a) == Js.D(b);
            if (a.Type != b.Type) return false;
            return (a is JValue) && JToken.DeepEquals(a, b);
        }

        private static JArray CardChangesForTier(LoopData d, JObject instance, JObject before, JObject after, string pieceId, double nextLevel)
        {
            var changes = new JArray();
            foreach (var row in ItemUpgrades.Rows(d.Combat, ArmamentItemRef(pieceId), nextLevel))
            {
                var descriptor = ItemUpgrades.Parse(d.Combat, row.Str(K.Tag));
                if (descriptor == null || descriptor.Role != instance.Str(K.EquipmentRole)) continue;
                if (descriptor.Kind == V.CardEffectKind)
                {
                    var b = NumericEffect(before, descriptor.Op);
                    var a = NumericEffect(after, descriptor.Op);
                    if (Js.Nullish(b) || Js.Nullish(a) || StrictEquals(b, a)) continue;
                    changes.Add(Js.Obj(K.Kind, LV.EffectChange, K.Tag, row[K.Tag], K.Op, descriptor.Op, WK.Before, b.DeepClone(), WK.After, a.DeepClone()));
                }
                else if (descriptor.Kind == V.CardCostKind)
                {
                    var field = ItemUpgrades.CostField(d.Combat, descriptor.Resource);
                    var b = Js.IsNum(before[field]) ? before.Num(field) : 0;
                    var a = Js.IsNum(after[field]) ? after.Num(field) : 0;
                    if (b == a) continue;
                    changes.Add(Js.Obj(K.Kind, LV.CostChange, K.Tag, row[K.Tag], K.Op, RunJs.Fmt(LV.CostOpFormat, descriptor.Resource), RK.Resource, descriptor.Resource,
                        WK.Before, b, WK.After, a));
                }
            }
            return changes;
        }

        /// <summary>smithingCardReceipt(registries, run, instance, nextLevel): one sourced card's actual before/after at the next tier.</summary>
        public static JObject CardReceipt(LoopData d, JObject run, JObject instance, double nextLevel)
        {
            var pieceId = SourceArmamentId(d, run, instance);
            if (pieceId == null) return null;
            var currentLevel = Math.Max(0, nextLevel - 1);
            JObject At(double level)
            {
                var inst = Js.Spread(instance);
                inst[K.Upgraded] = false;
                inst.Put(K.SmithingLevel, level);
                return Cards.Resolve(d.Combat, inst);
            }
            var before = At(currentLevel);
            var after = At(nextLevel);
            var profileId = instance.Str(K.ProfileId);
            var live = d.Run.EquipmentRows(K.BasicCardProfiles).FirstOrDefault(r => r.Str(K.Id) == profileId && profileId != null);
            var snapshot = run.Obj(K.EquipmentProfileRuleSnapshot)?.Obj(RK.Profiles)?[RunJs.Key(instance[K.ProfileId])] as JObject;
            var profile = Js.Truthy(snapshot) ? snapshot : live;
            var reference = Js.Spread(instance);
            if (instance[K.Mods] is JArray mods) reference[K.Mods] = mods.DeepClone();
            reference[K.Upgraded] = false;
            reference.Put(K.SmithingLevel, currentLevel);
            return Js.Obj(K.InstanceId, instance[K.InstanceId]?.DeepClone(), K.CardId, instance[K.CardId]?.DeepClone(), K.Role, instance[K.EquipmentRole]?.DeepClone(),
                K.SourceArmamentId, pieceId, K.Name, after[K.Name]?.DeepClone(),
                RK.Rating, profile != null ? Js.Obj(K.Id, profile[K.RatingId]?.DeepClone(), K.Label, profile.Str(K.RatingId).ToUpperInvariant()) : Js.Null(),
                LK.Reference, reference, WK.Before, EffectsReceipt(before), WK.After, EffectsReceipt(after),
                LK.Changes, CardChangesForTier(d, instance, before, after, pieceId, nextLevel));
        }

        private static bool Moves(JObject receipt) => Js.Items(receipt[LK.Changes]).OfType<JObject>().Any(c => !StrictEquals(c[WK.Before], c[WK.After]));

        private static JObject RolePreviewInstance(LoopData d, JObject piece, string role)
        {
            var profileId = piece[role + RV.ProfileSuffix];
            if (!Js.Truthy(profileId)) return null;
            var profile = d.Run.EquipmentRows(K.BasicCardProfiles).FirstOrDefault(r => JToken.DeepEquals(r[K.Id], profileId))
                          ?? throw new InvalidOperationException(RunJs.Fmt(LM.UnknownRoleProfile, piece.Str(K.Id), role, RunJs.Key(profileId)));
            return Js.Obj(K.InstanceId, string.Join(V.KeySeparator, LV.SmithPreviewPrefix, piece.Str(K.Id), role), K.CardId, profile[RK.BaseCardId],
                K.EquipmentRole, role, K.ProfileId, profileId, K.SourceArmamentId, piece[K.Id], K.Upgraded, false);
        }

        private static JArray RolePreviews(LoopData d, JObject run, JObject piece, double nextLevel)
        {
            var live = SourceCards(d, run, piece.Str(K.Id));
            var rows = new JArray();
            foreach (var role in Loadout.Roles(d.Run))
            {
                var active = live.Where(i => i.Str(K.EquipmentRole) == role).ToList();
                var carriers = active.Count > 0 ? active : new[] { RolePreviewInstance(d, piece, role) }.Where(x => x != null).ToList();
                foreach (var carrier in carriers)
                {
                    var receipt = CardReceipt(d, run, carrier, nextLevel);
                    if (receipt == null || !Moves(receipt)) continue;
                    var row = Js.Spread(receipt);
                    row[LK.Used] = active.Count > 0;
                    row.Put(LK.ActiveCopies, active.Count);
                    rows.Add(row);
                }
            }
            return rows;
        }

        private static JArray RequirementPreview(LoopData d, JObject run, JObject piece, double currentLevel, double nextLevel)
        {
            var itemRef = ArmamentItemRef(piece.Str(K.Id));
            var rows = new JArray();
            foreach (var p in (piece.Obj(RK.Requirements)?.Obj(K.Attributes) ?? new JObject()).Properties())
            {
                var baseRequired = Js.D(p.Value);
                var currentDelta = CumulativeRequirementDelta(d, itemRef, p.Name, currentLevel);
                var nextDelta = CumulativeRequirementDelta(d, itemRef, p.Name, nextLevel);
                var nextRequired = Math.Max(0, baseRequired + nextDelta);
                var actualToken = run.Obj(K.Attributes)?[p.Name];
                var actual = Js.IsFinite(actualToken) ? (JToken)Js.N(Js.D(actualToken)) : Js.Null();
                rows.Add(Js.Obj(RK.AttributeId, p.Name, K.Label, d.Combat.Attributes.Get(p.Name)[LK.ShortLabel]?.DeepClone(), RK.Actual, actual,
                    RK.BaseRequired, p.Value.DeepClone(), LK.CurrentRequired, Math.Max(0, baseRequired + currentDelta), LK.NextRequired, nextRequired,
                    LK.Change, nextDelta - currentDelta, LK.MetAfter, Js.IsFinite(actualToken) && Js.D(actualToken) >= nextRequired));
            }
            return rows;
        }

        private static JArray GenericCardChanges(LoopData d, JArray cards, JArray requirements)
        {
            var rows = new JArray();
            foreach (var card in cards.OfType<JObject>())
                foreach (var change in Js.Items(card[LK.Changes]).OfType<JObject>())
                    rows.Add(Js.Obj(K.Kind, change[K.Kind]?.DeepClone(), K.Tag, change[K.Tag]?.DeepClone(),
                        K.Label, RunJs.Key(card[K.Name]) + LV.LabelSpace + RunJs.Key(change[K.Op]),
                        WK.Before, change[WK.Before]?.DeepClone(), WK.After, change[WK.After]?.DeepClone(), K.CardId, card[K.CardId]?.DeepClone(), K.Role, card[K.Role]?.DeepClone()));
            foreach (var row in requirements.OfType<JObject>())
                if (row.Num(LK.CurrentRequired) != row.Num(LK.NextRequired))
                    rows.Add(Js.Obj(K.Kind, LV.RequirementChange, K.Tag, RunJs.Fmt(LV.RequirementTagFormat, row.Str(RK.AttributeId)),
                        K.Label, RunJs.Fmt(d.RuleStr(WK.Smithing, LK.RequirementLabel), RunJs.Key(row[K.Label])),
                        WK.Before, row[LK.CurrentRequired].DeepClone(), WK.After, row[LK.NextRequired].DeepClone()));
            return rows;
        }

        /// <summary>itemUpgradeValueReceipts(registries, itemRef, currentLevel, nextLevel): the armour's poise or the relic's passives, before and after.</summary>
        public static JArray ValueReceipts(LoopData d, string itemRef, double currentLevel, double nextLevel)
        {
            var identity = Identity(itemRef) ?? throw new InvalidOperationException(RunJs.Fmt(LM.UnknownItemRef, itemRef));
            if (identity.ItemKind == V.ArmamentRefPrefix) return new JArray();
            var before = ResolveItem(d, itemRef, currentLevel);
            var after = ResolveItem(d, itemRef, nextLevel);
            var labels = d.RuleObj(WK.Smithing, LK.ValueLabels);
            var receipts = new JArray();
            foreach (var row in ItemUpgrades.Rows(d.Combat, itemRef, nextLevel))
            {
                var descriptor = ItemUpgrades.Parse(d.Combat, row.Str(K.Tag));
                if (descriptor?.Kind == V.EquipmentPoiseKind)
                    receipts.Add(Js.Obj(K.Kind, descriptor.Kind, K.Tag, row[K.Tag], K.Label, labels[V.EquipmentPoiseKind],
                        WK.Before, before[K.PoiseThreshold]?.DeepClone(), WK.After, after[K.PoiseThreshold]?.DeepClone()));
                else if (descriptor?.Kind == V.RelicPassiveKind)
                    receipts.Add(Js.Obj(K.Kind, descriptor.Kind, K.Tag, row[K.Tag], LK.PassiveKey, descriptor.PassiveKey,
                        K.Label, labels[labels[descriptor.PassiveKey] != null ? descriptor.PassiveKey : LK.OtherPassive],
                        WK.Before, before.Obj(K.Passives)?[descriptor.PassiveKey]?.DeepClone(), WK.After, after.Obj(K.Passives)?[descriptor.PassiveKey]?.DeepClone()));
            }
            if (receipts.Count == 0 || receipts.OfType<JObject>().All(r => StrictEquals(r[WK.Before], r[WK.After])))
                throw new InvalidOperationException(RunJs.Fmt(LM.TierHasNoChange, itemRef, RunJs.NumStr(nextLevel)));
            return receipts;
        }

        private static string ActiveArmourRef(LoopData d, JObject run)
        {
            var slot = d.Run.EquipmentRows(K.Slots).FirstOrDefault(r => Js.Includes(r[K.Kinds], V.Armor));
            if (slot == null) return null;
            var ids = run.Obj(K.Loadout)?.Obj(K.Sets)?.Arr(slot.Str(K.Id)) ?? new JArray();
            var active = (int)Js.Or0(run.Obj(K.Loadout)?.Obj(K.Active)?[slot.Str(K.Id)]);
            var id = active >= 0 && active < ids.Count ? Js.Str(ids[active]) : null;
            if (string.IsNullOrEmpty(id) || !run.Is(RK.Class)) return null;
            var itemRef = Ref(V.ArmorRefPrefix, run.Str(RK.Class), id);
            return ItemByRef(d, itemRef) != null ? itemRef : null;
        }

        private static List<string> OwnedItemRefs(LoopData d, JObject run)
        {
            var refs = new List<string>();
            foreach (var instance in Js.Items(run[K.Deck]).OfType<JObject>())
            {
                var id = SourceArmamentId(d, run, instance);
                var itemRef = id != null ? ArmamentItemRef(id) : null;
                if (itemRef != null && !refs.Contains(itemRef)) refs.Add(itemRef);
            }
            foreach (var id in RewardRolls.CarriedIds(run.Obj(K.Loadout)))
            {
                var itemRef = ArmamentById(d, id) != null ? ArmamentItemRef(id) : null;
                if (itemRef != null && !refs.Contains(itemRef)) refs.Add(itemRef);
            }
            var armourRef = ActiveArmourRef(d, run);
            if (armourRef != null && !refs.Contains(armourRef)) refs.Add(armourRef);
            foreach (var id in Js.Items(run[K.Relics]).Select(RunJs.Key))
            {
                var itemRef = Ref(V.RelicRefPrefix, id);
                if (ItemByRef(d, itemRef) != null && !refs.Contains(itemRef)) refs.Add(itemRef);
            }
            return refs;
        }

        /// <summary>smithingPlan(registries, run): every owned item with an effective next tier, priced against the run's stones.</summary>
        public static SmithPlan Plan(LoopData d, JObject run)
        {
            Smithing.RewardByPool(d.Rewards);
            SmithServices.Rules(d);
            var levels = Levels(run);
            foreach (var p in levels.Properties())
            {
                if (ItemByRef(d, p.Name) == null) throw new InvalidOperationException(RunJs.Fmt(LM.UnknownLevelItem, p.Name));
                var tiers = Tiers(d, p.Name);
                if (Js.D(p.Value) > (tiers.Count > 0 ? tiers[tiers.Count - 1] : 0)) throw new InvalidOperationException(RunJs.Fmt(LM.LevelAboveTiers, p.Name));
            }
            var plan = new SmithPlan { Stones = StoneBalance(run) };
            var inventory = RewardRolls.CarriedIds(run.Obj(K.Loadout));
            var costTag = d.Combat.Engine.Obj(K.ItemUpgrades).Str(K.CostTag);
            foreach (var itemRef in OwnedItemRefs(d, run))
            {
                var identity = Identity(itemRef);
                var piece = ItemByRef(d, itemRef);
                var currentLevel = Js.Or0(levels[itemRef]);
                var nextLevel = currentLevel + 1;
                var upgradeRows = ItemUpgrades.Rows(d.Combat, itemRef, nextLevel);
                if (upgradeRows.Count == 0) continue;
                var cost = Cost(d, upgradeRows);
                var armament = identity.ItemKind == V.ArmamentRefPrefix;
                var affected = new JArray(armament
                    ? SourceCards(d, run, identity.ItemId).Select(card => CardReceipt(d, run, card, nextLevel)).Where(Moves).Select(r => (JToken)r)
                    : Enumerable.Empty<JToken>());
                var previews = armament ? RolePreviews(d, run, piece, nextLevel) : new JArray();
                var requirements = armament ? RequirementPreview(d, run, piece, currentLevel, nextLevel) : new JArray();
                var authored = upgradeRows.Where(r => r.Str(K.Tag) != costTag).ToList();
                var changes = armament ? GenericCardChanges(d, affected.Count > 0 ? affected : previews, requirements) : ValueReceipts(d, itemRef, currentLevel, nextLevel);
                if (authored.Count == 0 || changes.Count == 0 || changes.OfType<JObject>().All(r => StrictEquals(r[WK.Before], r[WK.After]))) continue;
                var shortfall = Math.Max(0, cost - plan.Stones);
                var doc = Js.Obj(K.ItemRef, itemRef, LK.ItemKind, identity.ItemKind, LK.ItemId, identity.ItemId, LK.ItemName, piece[K.Name]?.DeepClone());
                if (identity.ClassId != null) doc[K.ClassId] = identity.ClassId;
                if (armament)
                {
                    doc[WK.ArmamentId] = identity.ItemId;
                    doc[LK.ArmamentName] = piece[K.Name]?.DeepClone();
                }
                foreach (var pair in new (string, JToken)[]
                         {
                             (LK.CurrentLevel, Js.N(currentLevel)), (LK.NextLevel, Js.N(nextLevel)), (K.Cost, Js.N(cost)), (LK.Stones, Js.N(plan.Stones)),
                             (LK.Shortfall, Js.N(shortfall)), (LK.Affordable, new JValue(shortfall == 0)),
                             (LK.InventoryCount, Js.N(armament ? inventory.Count(id => id == identity.ItemId) : 1)), (RK.Requirements, requirements),
                             (LK.AuthoredChanges, new JArray(authored.Select(r => (JToken)Js.Spread(r)))), (LK.AffectedCards, affected), (LK.PreviewCards, previews),
                             (LK.Changes, changes),
                         })
                    doc[pair.Item1] = pair.Item2;
                plan.Candidates.Add(new SmithCandidate { ItemRef = itemRef, Affordable = shortfall == 0, Doc = doc });
            }
            return plan;
        }

        /// <summary>restampSmithingCards(registries, run, cards): every sourced instance carries its armament and tier.</summary>
        public static void Restamp(LoopData d, JObject run, IEnumerable<JObject> cards)
        {
            foreach (var instance in cards)
            {
                var pieceId = SourceArmamentId(d, run, instance);
                if (pieceId == null) continue;
                instance[K.SourceArmamentId] = pieceId;
                instance.Put(K.SmithingLevel, Js.Or0(Levels(run)[ArmamentItemRef(pieceId)]));
                instance[K.Upgraded] = false;
            }
        }

        /// <summary>
        /// commitSmithing(registries, run, itemRef, rules, { free }): revalidated through the plan, paid (unless free), the
        /// tier recorded, the lent cards restamped, and the receipt kept on the run.
        /// </summary>
        public static JObject Commit(LoopData d, JObject run, string requestedItemRef, bool free = false)
        {
            var itemRef = requestedItemRef != null && requestedItemRef.Contains(V.ItemRefSeparator) ? requestedItemRef : ArmamentItemRef(requestedItemRef);
            var plan = Plan(d, run);
            var candidate = plan.Candidates.FirstOrDefault(c => c.ItemRef == itemRef) ?? throw new InvalidOperationException(RunJs.Fmt(LM.NotASmithingCandidate, itemRef));
            var doc = candidate.Doc;
            if (!free && !candidate.Affordable) throw new InvalidOperationException(RunJs.Fmt(LM.InsufficientStones, RunJs.Key(doc[LK.Shortfall])));
            var beforeStones = plan.Stones;
            run.Put(RK.SmithingStones, free ? beforeStones : beforeStones - doc.Num(K.Cost));
            var levels = Levels(run);
            levels[itemRef] = doc[LK.NextLevel].DeepClone();
            run[K.ItemUpgradeLevels] = levels;
            run.Remove(RK.ArmamentLevels);
            Restamp(d, run, Js.Items(run[K.Deck]).OfType<JObject>().ToList());
            var receipt = Js.Obj(RK.SchemaVersion, d.RuleNum(WK.Smithing, RK.SchemaVersion), K.ItemRef, itemRef, LK.ItemKind, doc[LK.ItemKind], LK.ItemId, doc[LK.ItemId],
                LK.ItemName, doc[LK.ItemName]);
            if (doc.Str(LK.ItemKind) == V.ArmamentRefPrefix)
            {
                receipt[WK.ArmamentId] = doc[LK.ItemId].DeepClone();
                receipt[LK.ArmamentName] = doc[LK.ItemName]?.DeepClone();
            }
            var spent = free ? 0 : doc.Num(K.Cost);
            foreach (var pair in new (string, JToken)[]
                     {
                         (LK.BeforeLevel, doc[LK.CurrentLevel]), (LK.AfterLevel, doc[LK.NextLevel]), (LK.AuthoredCost, doc[K.Cost]), (LK.Spent, Js.N(spent)),
                         (K.Cost, Js.N(spent)), (LK.StoneBalanceBefore, Js.N(beforeStones)), (WK.StoneBalanceAfter, run[RK.SmithingStones]), (LK.Free, new JValue(free)),
                         (LK.Changes, doc[LK.Changes]), (LK.AffectedCards, doc[LK.AffectedCards]),
                     })
                receipt[pair.Item1] = pair.Item2.DeepClone();
            run[LK.LastSmithingReceipt] = receipt.DeepClone();
            return receipt;
        }
    }
}
