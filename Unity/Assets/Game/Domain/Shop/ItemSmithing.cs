using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RV = Ashen.Generated.RunValues;
using WK = Ashen.Generated.RewardsKeys;
using V = Ashen.Generated.CombatValues;
using SK = Ashen.Generated.ShopKeys;
using SV = Ashen.Generated.ShopValues;
using SM = Ashen.Generated.ShopMessages;
using SS = Ashen.Generated.ShopStringKeys;

namespace Ashen.Domain.Shop
{
    /// <summary>
    /// Smithing as a run-owned transaction over a namespaced item (shipped model/smithing.js smithingPlan,
    /// smithingCardReceipt, commitItemUpgrade/commitSmithing and restampSmithingCards, with model/itemUpgrades.js
    /// itemUpgradeValueReceipts over <see cref="Combat.Equipment.ResolveUpgradedItem"/>): every owned item with an
    /// authored next tier and an effective change, priced in Smithing Stones with exact before/after receipts; a commit
    /// revalidates through the plan, promotes the item and restamps the cards it supplies. The one port: the merchant,
    /// the events (free grants) and the rest stop's smith all call it (D-085, D-112u).
    /// </summary>
    public static class ItemSmithing
    {
        private static JObject OwnObject(JToken value, string label) =>
            value as JObject ?? throw new InvalidOperationException(RunJs.Fmt(SM.MustBeObject, label));

        private static string Label(params string[] parts) => string.Join(RV.PathDot, parts);

        private static string ArmamentRef(string id) => string.Join(V.ItemRefSeparator, V.ArmamentRefPrefix, id);

        /// <summary>
        /// itemByRef(registries, itemRef) (smithing.js): the item a namespaced ref names (armament, armour or relic) at tier
        /// 0, or null when the ref names no item kind or resolves to nothing.
        /// </summary>
        public static JObject ItemByRef(ShopData d, string itemRef)
        {
            if (ItemUpgrades.Identity(itemRef) == null) return null;
            try
            {
                return Combat.Equipment.ResolveUpgradedItem(d.Combat, itemRef, 0);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static JObject ResolveUpgradedItem(ShopData d, string itemRef, double level) => Combat.Equipment.ResolveUpgradedItem(d.Combat, itemRef, level);

        /// <summary>itemUpgradeCost(rows): the tier's Smithing Stone cost row.</summary>
        private static double UpgradeCost(ShopData d, List<JObject> rows)
        {
            var row = rows.FirstOrDefault(r => r.Str(K.Tag) == ItemUpgrades.CostTag(d.Combat)) ?? throw new InvalidOperationException(SM.TierMissingCost);
            return row.Num(K.Value);
        }

        /// <summary>cumulativeRequirementDelta(registries, itemRef, attributeId, level).</summary>
        private static double RequirementDelta(ShopData d, string itemRef, string attributeId, double level)
        {
            double delta = 0;
            var tag = string.Join(V.TagSeparator, V.RequirementKind, attributeId);
            for (var tier = 1; tier <= level; tier += 1)
                foreach (var row in ItemUpgrades.Rows(d.Combat, itemRef, tier))
                    if (row.Str(K.Tag) == tag) delta += row.Num(K.Value);
            return delta;
        }

        /// <summary>levels(run): run.itemUpgradeLevels with the legacy run.armamentLevels folded in, validated.</summary>
        internal static JObject Levels(JObject run)
        {
            var currentRaw = run[K.ItemUpgradeLevels];
            var current = Js.Nullish(currentRaw) ? null : OwnObject(currentRaw, Label(RK.Run, K.ItemUpgradeLevels));
            var legacyRaw = run[RK.ArmamentLevels];
            var legacy = Js.Nullish(legacyRaw) ? null : OwnObject(legacyRaw, Label(RK.Run, RK.ArmamentLevels));
            var result = current != null ? Js.Spread(current) : new JObject();
            foreach (var p in result.Properties())
            {
                if (ItemUpgrades.Identity(p.Name) == null) throw new InvalidOperationException(RunJs.Fmt(SM.LevelKeyNotNamespaced, p.Name));
                SmithServices.Integer(p.Value, Label(RK.Run, K.ItemUpgradeLevels, p.Name));
            }
            foreach (var p in (legacy ?? new JObject()).Properties())
            {
                SmithServices.Integer(p.Value, Label(RK.Run, RK.ArmamentLevels, p.Name));
                var itemRef = ArmamentRef(p.Name);
                if (result[itemRef] != null && Js.D(result[itemRef]) != Js.D(p.Value))
                    throw new InvalidOperationException(RunJs.Fmt(SM.LevelConflict, p.Name, RunJs.Key(p.Value), itemRef, RunJs.Key(result[itemRef])));
                result[itemRef] = p.Value.DeepClone();
            }
            return result;
        }

        private static string InstanceSourceId(ShopData d, JObject run, JObject instance)
        {
            foreach (var key in d.RuleList(SK.Smith, SK.SourceKeys))
            {
                if (instance == null || Js.Nullish(instance[key])) continue;
                var id = RunJs.Key(instance[key]);
                if (d.Armament(id) == null) throw new InvalidOperationException(RunJs.Fmt(SM.UnknownSourceArmament, id));
                return id;
            }
            if (instance == null || !Loadout.Roles(d.Run).Contains(instance.Str(K.EquipmentRole))) return null;
            var row = Loadout.RoleSource(d.Run, run.Obj(K.Loadout), run.Str(RK.Class), instance.Str(K.EquipmentRole));
            return row.Piece?.Str(K.Id);
        }

        /// <summary>sourceArmamentId(registries, run, instance): the stable armament that owns an equipment-bound basic instance.</summary>
        public static string SourceArmamentId(ShopData d, JObject run, JObject instance)
        {
            var id = InstanceSourceId(d, run, instance);
            return id != null && d.Armament(id) != null ? id : null;
        }

        private static List<JObject> SourceCards(ShopData d, JObject run, string pieceId) =>
            Js.Items(run[K.Deck]).OfType<JObject>().Where(inst => SourceArmamentId(d, run, inst) == pieceId).ToList();

        private static JObject RolePreviewInstance(ShopData d, JObject piece, string role)
        {
            var profileId = piece?[role + RV.ProfileSuffix];
            if (!Js.Truthy(profileId)) return null;
            var profile = Loadout.ProfileById(d.Run, profileId) ?? throw new InvalidOperationException(RunJs.Fmt(SM.UnknownRoleProfile, piece.Str(K.Id), role, RunJs.Key(profileId)));
            return Js.Obj(K.InstanceId, RunJs.Fmt(SV.PreviewInstanceFormat, piece.Str(K.Id), role), K.CardId, profile[RK.BaseCardId], K.EquipmentRole, role,
                K.ProfileId, profileId, K.SourceArmamentId, piece[K.Id], K.Upgraded, false);
        }

        private static JArray EffectsReceipt(JObject def) => new JArray(Js.Items(def[K.Effects]).Select(e => e is JObject o ? (JToken)Js.Spread(o) : e.DeepClone()));

        private static JToken NumericEffect(JObject def, string op)
        {
            var effect = Js.Items(def[K.Effects]).OfType<JObject>().FirstOrDefault(e => e.Str(K.Op) == op);
            if (effect == null) return null;
            return RunJs.Coalesce(effect[K.Amount], effect[K.Stacks], effect[K.Hits]) ?? Js.Null();
        }

        /// <summary>JS <c>===</c> on two receipt values: numbers by value, absent and null distinct, objects and arrays never equal.</summary>
        private static bool Same(JToken a, JToken b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (Js.IsNum(a) && Js.IsNum(b)) return Js.D(a) == Js.D(b);
            if (a.Type != b.Type) return false;
            return a is JValue && JToken.DeepEquals(a, b);
        }

        private static JArray CardChangesForTier(ShopData d, JObject instance, JObject before, JObject after, string pieceId, double nextLevel)
        {
            var changes = new JArray();
            foreach (var row in ItemUpgrades.Rows(d.Combat, ArmamentRef(pieceId), nextLevel))
            {
                var t = ItemUpgrades.ParseTag(d.Combat, row.Str(K.Tag));
                if (t == null || t.Role != instance.Str(K.EquipmentRole)) continue;
                if (t.Kind == V.CardEffectKind)
                {
                    var b = NumericEffect(before, t.Op);
                    var a = NumericEffect(after, t.Op);
                    if (Js.Nullish(b) || Js.Nullish(a) || Same(b, a)) continue;
                    changes.Add(Js.Obj(K.Kind, SV.ChangeEffect, K.Tag, row[K.Tag], K.Op, t.Op, WK.Before, b.DeepClone(), WK.After, a.DeepClone()));
                }
                else if (t.Kind == V.CardCostKind)
                {
                    var field = ItemUpgrades.CostField(d.Combat, t.Resource);
                    var b = Js.IsNum(before[field]) ? Js.D(before[field]) : 0;
                    var a = Js.IsNum(after[field]) ? Js.D(after[field]) : 0;
                    if (b == a) continue;
                    changes.Add(Js.Obj(K.Kind, SV.ChangeCost, K.Tag, row[K.Tag], K.Op, string.Join(V.TagSeparator, V.UpgradeCostPart, t.Resource), RK.Resource, t.Resource, WK.Before, b, WK.After, a));
                }
            }
            return changes;
        }

        private static bool Changes(JObject receipt) => Js.Items(receipt[SK.Changes]).OfType<JObject>().Any(c => !Same(c[WK.Before], c[WK.After]));

        /// <summary>
        /// smithingCardReceipt(registries, run, instance, nextLevel): the actual before/after faces of one sourced basic
        /// card at the next tier, with the change rows the tier's tags produce; null when no armament owns the card.
        /// </summary>
        public static JObject CardReceipt(ShopData d, JObject run, JObject instance, double nextLevel)
        {
            var pieceId = SourceArmamentId(d, run, instance);
            if (pieceId == null) return null;
            var currentLevel = Math.Max(0, nextLevel - 1);
            var beforeRef = Js.Spread(instance);
            beforeRef.Put(K.Upgraded, false);
            beforeRef.Put(K.SmithingLevel, currentLevel);
            var afterRef = Js.Spread(instance);
            afterRef.Put(K.Upgraded, false);
            afterRef.Put(K.SmithingLevel, nextLevel);
            var before = Cards.Resolve(d.Combat, beforeRef);
            var after = Cards.Resolve(d.Combat, afterRef);
            var profileId = instance.Str(K.ProfileId) ?? V.Undefined;
            var liveProfile = Loadout.ProfileById(d.Run, instance[K.ProfileId]);
            var snapshotProfile = run.Obj(K.EquipmentProfileRuleSnapshot)?.Obj(RK.Profiles)?[profileId];
            var profile = Js.Truthy(snapshotProfile) ? (JObject)snapshotProfile : liveProfile;
            JToken rating = profile != null
                ? Js.Obj(K.Id, profile[K.RatingId], K.Label, (profile.Str(K.RatingId) ?? string.Empty).ToUpperInvariant())
                : Js.Null();
            var reference = Js.Spread(instance);
            if (instance[K.Mods] is JArray mods) reference[K.Mods] = mods.DeepClone();
            reference.Put(K.Upgraded, false);
            reference.Put(K.SmithingLevel, currentLevel);
            return Js.Obj(K.InstanceId, instance[K.InstanceId], K.CardId, instance[K.CardId], K.Role, instance[K.EquipmentRole], K.SourceArmamentId, pieceId,
                K.Name, after[K.Name], RK.Rating, rating, SK.Reference, reference, WK.Before, EffectsReceipt(before), WK.After, EffectsReceipt(after),
                SK.Changes, CardChangesForTier(d, instance, before, after, pieceId, nextLevel));
        }

        private static JArray RolePreviews(ShopData d, JObject run, JObject piece, double nextLevel)
        {
            var live = SourceCards(d, run, piece.Str(K.Id));
            var rows = new JArray();
            foreach (var role in Loadout.Roles(d.Run))
            {
                var active = live.Where(inst => inst.Str(K.EquipmentRole) == role).ToList();
                var carriers = active.Count > 0 ? active : new[] { RolePreviewInstance(d, piece, role) }.Where(x => x != null).ToList();
                foreach (var carrier in carriers)
                {
                    var receipt = CardReceipt(d, run, carrier, nextLevel);
                    if (receipt == null || !Changes(receipt)) continue;
                    receipt.Put(SK.Used, active.Count > 0);
                    receipt.Put(SK.ActiveCopies, active.Count);
                    rows.Add(receipt);
                }
            }
            return rows;
        }

        private static JArray RequirementPreview(ShopData d, JObject run, JObject piece, double currentLevel, double nextLevel)
        {
            var authored = piece.Obj(RK.Requirements)?.Obj(K.Attributes) ?? new JObject();
            var itemRef = ArmamentRef(piece.Str(K.Id));
            var rows = new JArray();
            foreach (var p in authored.Properties())
            {
                var baseRequired = Js.D(p.Value);
                var currentDelta = RequirementDelta(d, itemRef, p.Name, currentLevel);
                var nextDelta = RequirementDelta(d, itemRef, p.Name, nextLevel);
                var currentRequired = Math.Max(0, baseRequired + currentDelta);
                var nextRequired = Math.Max(0, baseRequired + nextDelta);
                var actualToken = run.Obj(K.Attributes)?[p.Name];
                var actual = Js.IsFinite(actualToken) ? actualToken.DeepClone() : Js.Null();
                rows.Add(Js.Obj(RK.AttributeId, p.Name, K.Label, d.Run.Attributes.Get(p.Name)[SK.ShortLabel], RK.Actual, actual, RK.BaseRequired, p.Value.DeepClone(),
                    SK.CurrentRequired, currentRequired, SK.NextRequired, nextRequired, SK.Change, nextDelta - currentDelta,
                    SK.MetAfter, Js.IsFinite(actualToken) && Js.D(actualToken) >= nextRequired));
            }
            return rows;
        }

        private static JArray GenericCardChanges(ShopData d, JArray affected, JArray requirements)
        {
            var rows = new JArray();
            foreach (var card in affected.OfType<JObject>())
                foreach (var change in Js.Items(card[SK.Changes]).OfType<JObject>())
                    rows.Add(Js.Obj(K.Kind, change[K.Kind], K.Tag, change[K.Tag], K.Label, RunJs.Fmt(d.RuleStr(SK.Labels, SK.CardChange), RunJs.Key(card[K.Name]), RunJs.Key(change[K.Op])),
                        WK.Before, change[WK.Before], WK.After, change[WK.After], K.CardId, card[K.CardId], K.Role, card[K.Role]));
            foreach (var row in requirements.OfType<JObject>())
                if (row.Num(SK.CurrentRequired) != row.Num(SK.NextRequired))
                    rows.Add(Js.Obj(K.Kind, V.RequirementKind, K.Tag, string.Join(V.TagSeparator, V.RequirementKind, row.Str(RK.AttributeId)),
                        K.Label, RunJs.Fmt(d.RuleStr(SK.Labels, V.RequirementKind), RunJs.Key(row[K.Label])), WK.Before, row[SK.CurrentRequired], WK.After, row[SK.NextRequired]));
            return rows;
        }

        /// <summary>itemUpgradeValueReceipts(registries, itemRef, currentLevel, nextLevel): the typed non-card changes of one tier.</summary>
        private static JArray ValueReceipts(ShopData d, string itemRef, double currentLevel, double nextLevel)
        {
            var identity = ItemUpgrades.Identity(itemRef) ?? throw new InvalidOperationException(RunJs.Fmt(SM.UnknownUpgradeItem, itemRef));
            if (identity.ItemKind == V.ArmamentRefPrefix) return new JArray();
            var before = ResolveUpgradedItem(d, itemRef, currentLevel);
            var after = ResolveUpgradedItem(d, itemRef, nextLevel);
            var receipts = new JArray();
            foreach (var row in ItemUpgrades.Rows(d.Combat, itemRef, nextLevel))
            {
                var t = ItemUpgrades.ParseTag(d.Combat, row.Str(K.Tag));
                if (t?.Kind == V.EquipmentPoiseKind)
                    receipts.Add(Js.Obj(K.Kind, t.Kind, K.Tag, row[K.Tag], K.Label, d.RuleStr(SK.Labels, K.PoiseThreshold), WK.Before, before[K.PoiseThreshold], WK.After, after[K.PoiseThreshold]));
                else if (t?.Kind == V.RelicPassiveKind)
                {
                    var label = d.RuleObj(SK.Labels, K.Passives).Str(t.PassiveKey) ?? d.RuleStr(SK.Labels, SK.OtherPassive);
                    receipts.Add(Js.Obj(K.Kind, t.Kind, K.Tag, row[K.Tag], SK.PassiveKey, t.PassiveKey, K.Label, label,
                        WK.Before, before.Obj(K.Passives)?[t.PassiveKey], WK.After, after.Obj(K.Passives)?[t.PassiveKey]));
                }
            }
            if (receipts.Count == 0 || receipts.OfType<JObject>().All(r => Same(r[WK.Before], r[WK.After])))
                throw new InvalidOperationException(RunJs.Fmt(SM.TierNoEffectiveChange, itemRef, RunJs.NumStr(nextLevel)));
            return receipts;
        }

        private static string ActiveArmourRef(ShopData d, JObject run)
        {
            var slot = d.Run.EquipmentRows(K.Slots).FirstOrDefault(row => Js.Includes(row[K.Kinds], V.Armor));
            if (slot == null) return null;
            var ids = run.Obj(K.Loadout)?.Obj(K.Sets)?.Arr(slot.Str(K.Id));
            var active = (int)Js.Or0(run.Obj(K.Loadout)?.Obj(K.Active)?[slot.Str(K.Id)]);
            var id = ids != null && active >= 0 && active < ids.Count ? ids[active] : null;
            if (!Js.Truthy(id) || !Js.Truthy(run[RK.Class])) return null;
            var itemRef = string.Join(V.ItemRefSeparator, V.ArmorRefPrefix, run.Str(RK.Class), RunJs.Key(id));
            return ItemByRef(d, itemRef) != null ? itemRef : null;
        }

        private static List<string> OwnedItemRefs(ShopData d, JObject run)
        {
            var refs = new List<string>();
            void Add(string itemRef)
            {
                if (itemRef != null && !refs.Contains(itemRef)) refs.Add(itemRef);
            }
            foreach (var instance in Js.Items(run[K.Deck]).OfType<JObject>())
            {
                var id = SourceArmamentId(d, run, instance);
                Add(id != null ? ArmamentRef(id) : null);
            }
            foreach (var id in RewardRolls.CarriedIds(run.Obj(K.Loadout)))
                Add(d.Armament(id) != null ? ArmamentRef(id) : null);
            Add(ActiveArmourRef(d, run));
            foreach (var id in RunJs.Strs(run[K.Relics]))
            {
                var itemRef = string.Join(V.ItemRefSeparator, V.RelicRefPrefix, id);
                if (ItemByRef(d, itemRef) != null) Add(itemRef);
            }
            return refs;
        }

        /// <summary>
        /// smithingPlan(registries, run) → { schemaVersion, stones, candidates }: stable, distinct owned items with exact,
        /// non-noop change receipts, each priced against the run's Smithing Stones.
        /// </summary>
        public static JObject Plan(ShopData d, JObject run)
        {
            SmithServices.Rules(d);
            var levelMap = Levels(run);
            foreach (var p in levelMap.Properties())
            {
                if (ItemByRef(d, p.Name) == null) throw new InvalidOperationException(RunJs.Fmt(SM.UnknownLevelItem, p.Name));
                var tiers = ItemUpgrades.Tiers(d.Combat, p.Name);
                var top = tiers.Count > 0 ? tiers[tiers.Count - 1] : 0;
                if (Js.D(p.Value) > top) throw new InvalidOperationException(RunJs.Fmt(SM.LevelExceedsTier, p.Name, RunJs.NumStr(top)));
            }
            var stones = Rewards.Smithing.StoneBalance(run);
            var inventory = RewardRolls.CarriedIds(run.Obj(K.Loadout));
            var candidates = new JArray();
            foreach (var itemRef in OwnedItemRefs(d, run))
            {
                var identity = ItemUpgrades.Identity(itemRef);
                var piece = ItemByRef(d, itemRef);
                var armament = identity.ItemKind == V.ArmamentRefPrefix;
                var currentLevel = Js.Or0(levelMap[itemRef]);
                var nextLevel = currentLevel + 1;
                var upgradeRows = ItemUpgrades.Rows(d.Combat, itemRef, nextLevel);
                if (upgradeRows.Count == 0) continue;
                var cost = UpgradeCost(d, upgradeRows);
                var affected = armament
                    ? new JArray(SourceCards(d, run, identity.ItemId).Select(card => CardReceipt(d, run, card, nextLevel)).Where(Changes))
                    : new JArray();
                var previews = armament ? RolePreviews(d, run, piece, nextLevel) : new JArray();
                var requirements = armament ? RequirementPreview(d, run, piece, currentLevel, nextLevel) : new JArray();
                var costTag = ItemUpgrades.CostTag(d.Combat);
                var authoredChanges = upgradeRows.Where(r => r.Str(K.Tag) != costTag).ToList();
                var changes = armament ? GenericCardChanges(d, affected.Count > 0 ? affected : previews, requirements) : ValueReceipts(d, itemRef, currentLevel, nextLevel);
                if (authoredChanges.Count == 0 || changes.Count == 0 || changes.OfType<JObject>().All(r => Same(r[WK.Before], r[WK.After]))) continue;
                var shortfall = Math.Max(0, cost - stones);
                var c = Js.Obj(K.ItemRef, itemRef, SK.ItemKind, identity.ItemKind, SK.ItemId, identity.ItemId, SK.ItemName, piece[K.Name]);
                if (!string.IsNullOrEmpty(identity.ClassId)) c[K.ClassId] = identity.ClassId;
                if (armament)
                {
                    c[WK.ArmamentId] = identity.ItemId;
                    if (piece[K.Name] != null) c[SK.ArmamentName] = piece[K.Name].DeepClone();
                }
                c.Put(SK.CurrentLevel, currentLevel);
                c.Put(SK.NextLevel, nextLevel);
                c.Put(K.Cost, cost);
                c.Put(SK.Stones, stones);
                c.Put(SK.Shortfall, shortfall);
                c.Put(SK.Affordable, shortfall == 0);
                c.Put(SK.InventoryCount, armament ? inventory.Count(x => x == identity.ItemId) : 1);
                c[RK.Requirements] = requirements;
                c[SK.AuthoredChanges] = new JArray(authoredChanges.Select(r => Js.Spread(r)));
                c[SK.AffectedCards] = affected;
                c[SK.PreviewCards] = previews;
                c[SK.Changes] = changes;
                candidates.Add(c);
            }
            return Js.Obj(RK.SchemaVersion, d.RuleNum(SK.Smith, SK.SmithingSchemaVersion), SK.Stones, stones, SK.Candidates, candidates);
        }

        /// <summary>restampSmithingCards(registries, run, cards): stable source and tier carriers on every sourced instance.</summary>
        public static void RestampCards(ShopData d, JObject run, IEnumerable<JObject> cards)
        {
            foreach (var instance in cards.ToList())
            {
                var pieceId = SourceArmamentId(d, run, instance);
                if (pieceId == null) continue;
                instance[K.SourceArmamentId] = pieceId;
                instance[K.SmithingLevel] = Js.Truthy(Levels(run)[ArmamentRef(pieceId)]) ? Levels(run)[ArmamentRef(pieceId)].DeepClone() : Js.N(0);
                instance.Put(K.Upgraded, false);
            }
        }

        /// <summary>
        /// commitItemUpgrade(registries, run, itemRef, rules, { free }) (commitSmithing: a bare id means armament/&lt;id&gt;):
        /// revalidates through the plan, spends the stones (none when free), promotes the item one tier, restamps its
        /// cards and records the durable receipt on run.lastSmithingReceipt.
        /// </summary>
        public static JObject Commit(ShopData d, JObject run, string requestedItemRef, bool free = false)
        {
            var itemRef = requestedItemRef != null && requestedItemRef.Contains(V.ItemRefSeparator) ? requestedItemRef : ArmamentRef(requestedItemRef ?? V.Undefined);
            var plan = Plan(d, run);
            var candidate = Js.Items(plan[SK.Candidates]).OfType<JObject>().FirstOrDefault(c => c.Str(K.ItemRef) == itemRef)
                            ?? throw new RefusalException(SS.SmithRefusalNotCandidate, itemRef);
            if (!free && !candidate.Is(SK.Affordable)) throw new RefusalException(SS.SmithRefusalInsufficientStones, RunJs.NumStr(candidate.Num(SK.Shortfall)));
            var beforeStones = plan.Num(SK.Stones);
            run.Put(RK.SmithingStones, free ? beforeStones : beforeStones - candidate.Num(K.Cost));
            var levels = Levels(run);
            levels[itemRef] = candidate[SK.NextLevel].DeepClone();
            run[K.ItemUpgradeLevels] = levels;
            run.Remove(RK.ArmamentLevels);
            RestampCards(d, run, Js.Items(run[K.Deck]).OfType<JObject>());
            var armament = candidate.Str(SK.ItemKind) == V.ArmamentRefPrefix;
            var spent = free ? 0 : candidate.Num(K.Cost);
            var receipt = Js.Obj(RK.SchemaVersion, d.RuleNum(SK.Smith, SK.SmithingSchemaVersion), K.ItemRef, itemRef, SK.ItemKind, candidate[SK.ItemKind],
                SK.ItemId, candidate[SK.ItemId], SK.ItemName, candidate[SK.ItemName]);
            if (armament)
            {
                receipt[WK.ArmamentId] = candidate[SK.ItemId].DeepClone();
                if (candidate[SK.ItemName] != null) receipt[SK.ArmamentName] = candidate[SK.ItemName].DeepClone();
            }
            receipt[SK.BeforeLevel] = candidate[SK.CurrentLevel].DeepClone();
            receipt[SK.AfterLevel] = candidate[SK.NextLevel].DeepClone();
            receipt[SK.AuthoredCost] = candidate[K.Cost].DeepClone();
            receipt.Put(SK.Spent, spent);
            receipt.Put(K.Cost, spent);
            receipt.Put(SK.StoneBalanceBefore, beforeStones);
            receipt[WK.StoneBalanceAfter] = run[RK.SmithingStones].DeepClone();
            receipt.Put(SK.Free, free);
            receipt[SK.Changes] = candidate[SK.Changes].DeepClone();
            receipt[SK.AffectedCards] = candidate[SK.AffectedCards].DeepClone();
            run[SK.LastSmithingReceipt] = receipt.DeepClone();
            return receipt;
        }
    }
}
