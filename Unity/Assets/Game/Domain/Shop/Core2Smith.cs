using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Rewards;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2RunKeys;
using K = Ashen.Generated.CombatKeys;
using MI = Ashen.Generated.Core2ModuleIds;
using PC = Ashen.Generated.Core2ProfileContentKeys;
using RK = Ashen.Generated.RunKeys;
using RR = Ashen.Generated.Core2RestKeys;
using RS = Ashen.Generated.Core2RestValues;
using SK = Ashen.Generated.ShopKeys;
using SM = Ashen.Generated.ShopMessages;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;

namespace Ashen.Domain.Shop
{
    /// <summary>
    /// The Smith of a Core 2 run (web model/smithing.js smithingPlan and commitItemUpgrade over model/smithingCore.js, WP-16): every item the run owns that the content upgrades
    /// one tier, with the stat rows a tier step changes (a weapon's AR, an armour's budget and ratings), the attribute requirements it moves, the equipment Poise threshold or
    /// relic passive it raises, and, for an item that is worn, the live damage and Block of every card in the deck read off two real fights (the card rows). A commit promotes the
    /// item one tier and writes the durable receipt to <c>lastSmithingReceipt</c>. The 405 smith (<see cref="ItemSmithing"/>) rewrites cards by tier rows; a Core card is never
    /// rewritten, so this is its own port. Engine-free.
    /// </summary>
    public static class Core2Smith
    {
        /// <summary>coreSmithingActive(run): the gear module has an armour budget and the stats module is configured.</summary>
        public static bool Active(Core2RewardsData d, JObject run)
        {
            var gear = d.Module(run, MI.Gear);
            return gear?.Str(RR.Mode) == Ashen.Generated.Core2RunValues.Configured && Js.Truthy(gear[CK.ArmorBudget]) && d.Module(run, MI.Stats)?.Str(RR.Mode) == Ashen.Generated.Core2RunValues.Configured;
        }

        // ------------------------------------------------------------------ the item's own rows

        private static JObject GearItemFor(Core2RewardsData d, JObject run, string itemRef)
        {
            var identity = ItemUpgrades.Identity(itemRef);
            if (identity == null || identity.ItemKind == V.RelicKind) return null;
            var armor = identity.ItemKind == V.Armor;
            var gear = d.Deck.Run.RuleSection(CK.Gear);
            var kindArmor = gear.Str(CK.ArmorKind);
            return (d.Content(run, PC.Gear) as JArray ?? new JArray()).OfType<JObject>().FirstOrDefault(item =>
                (Js.Truthy(item.Obj(RR.Source)?[RR.Id]) ? item.Obj(RR.Source).Str(RR.Id) : item.Str(RR.Id)) == identity.ItemId
                && (item.Str(RR.Kind) == kindArmor) == armor
                && (!Js.Truthy(item[RR.Classes]) || !Js.Truthy(run[RK.Class]) || Js.Includes(item[RR.Classes], run.Str(RK.Class))));
        }

        private sealed class Tier
        {
            public JObject Values;
            public double Points;
            public double? Budget;
        }

        /// <summary>itemAtTier(run, item, level): the item's own stat points and values at a tier, as the equipped-gear receipt resolves them.</summary>
        private static Tier ItemAtTier(Core2RewardsData d, JObject run, JObject item, double level)
        {
            var settings = d.Module(run, MI.Gear);
            var resolved = CoreGear.ResolveItem(d.Deck.Run, item, settings, (Js.Nullish(item[RR.Rank]) ? 1 : item.Num(RR.Rank)) + level);
            var values = (JObject)resolved.Obj(RR.Values).DeepClone();
            var gear = d.Deck.Run.RuleSection(CK.Gear);
            if (item.Str(RR.Kind) != gear.Str(CK.ArmorKind) && item.Str(RR.Kind) != gear.Str(CK.ShieldKind))
                values.Put(RR.Ar, values.Num(RR.Ar) + level * Js.Or0(d.Module(run, MI.Equipment)?.Obj(RR.UpgradeBonuses)?[RR.WeaponArPerTier]));
            var points = resolved.Obj(RR.Points).Properties().Sum(p => Js.D(p.Value)) + resolved.Num(RR.TagCost);
            return new Tier { Values = values, Points = points, Budget = Js.Nullish(resolved[RR.Budget]) ? (double?)null : resolved.Num(RR.Budget) };
        }

        private static JObject Row(string kind, string tag, string label, JToken before, JToken after) =>
            new JObject { [RR.Kind] = kind, [RR.Tag] = tag, [RR.Label] = label, [RR.Before] = before?.DeepClone(), [RR.After] = after?.DeepClone() };

        /// <summary>coreSmithingStats(run, itemRef, currentLevel, nextLevel): the item's own rows for one tier step (budget and stat points, ratings). Cheap: no combat.</summary>
        public static JArray Stats(Core2RewardsData d, JObject run, string itemRef, double currentLevel, double nextLevel)
        {
            var rows = new JArray();
            var item = GearItemFor(d, run, itemRef);
            if (item == null) return rows;
            var before = ItemAtTier(d, run, item, currentLevel);
            var after = ItemAtTier(d, run, item, nextLevel);
            var gear = d.Deck.Run.RuleSection(CK.Gear);
            var kind = item.Str(RR.Kind);
            if (kind == gear.Str(CK.ArmorKind) || kind == gear.Str(CK.ShieldKind))
                rows.Add(Row(RS.RowBudget, RunJs.Fmt(RS.BudgetTagFormat, itemRef), d.RestRules.Obj(RR.BudgetLabels).Str(kind == gear.Str(CK.ArmorKind) ? RR.Armor : RR.Shield),
                    Js.N(before.Budget ?? before.Points), Js.N(after.Budget ?? after.Points)));
            var labels = d.RestRules.Obj(RR.StatLabels);
            foreach (var id in Js.Items(d.RestRules[RR.StatOrder]).Select(Js.Str))
                if (!Same(before.Values[id], after.Values[id]))
                    rows.Add(Row(RS.RowStat, RunJs.Fmt(RS.StatTagFormat, id, itemRef), labels.Str(id), before.Values[id], after.Values[id]));
            return rows;
        }

        /// <summary>coreSmithingPreviewRows(itemName, stats, cards): card-shaped rows for the Smith modal's "affected" list: the item's stats first (cards are asked for apart).</summary>
        public static JArray PreviewRows(string itemName, JArray stats)
        {
            var rows = new JArray();
            if (stats.Count == 0) return rows;
            rows.Add(new JObject
            {
                [RR.InstanceId] = Js.Null(), [RR.CardId] = Js.Null(), [RR.Role] = RS.RoleItem, [RR.Name] = itemName, [RR.Rating] = Js.Null(), [RR.Reference] = Js.Null(), [SK.Used] = true, [RR.ActiveCopies] = 1,
                [RR.Changes] = new JArray(stats.OfType<JObject>().Select(row => new JObject
                {
                    [RR.Kind] = row[RR.Kind].DeepClone(), [RR.Tag] = row[RR.Tag].DeepClone(), [RR.Op] = row[RR.Tag].DeepClone(), [RR.Label] = row[RR.Label].DeepClone(),
                    [RR.Before] = row[RR.Before].DeepClone(), [RR.After] = row[RR.After].DeepClone(),
                })),
            });
            return rows;
        }

        /// <summary>JS <c>===</c> on two receipt values.</summary>
        private static bool Same(JToken a, JToken b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (Js.IsNum(a) && Js.IsNum(b)) return Js.D(a) == Js.D(b);
            if (a.Type != b.Type) return false;
            return a is JValue && JToken.DeepEquals(a, b);
        }

        // ------------------------------------------------------------------ the plan

        /// <summary>
        /// smithingPlan(registries, run, undefined, { free }) → { schemaVersion, stones, candidates }: stable, distinct owned items with exact, non-noop change receipts. A candidate
        /// carries its stat rows (<c>changes</c>), the requirements it moves and the authored tier rows; the live card rows run combats and are asked for apart (<see cref="CardRows"/>).
        /// </summary>
        public static JObject Plan(Core2RewardsData d, JObject run, bool free)
        {
            var shop = d.Smith;
            SmithServices.Rules(shop);
            var levelMap = ItemSmithing.Levels(run);
            foreach (var p in levelMap.Properties())
            {
                if (ItemSmithing.ItemByRef(shop, p.Name) == null) throw new InvalidOperationException(RunJs.Fmt(SM.UnknownLevelItem, p.Name));
                var tiers = ItemUpgrades.Tiers(shop.Combat, p.Name);
                var top = tiers.Count > 0 ? tiers[tiers.Count - 1] : 0;
                if (Js.D(p.Value) > top) throw new InvalidOperationException(RunJs.Fmt(SM.LevelExceedsTier, p.Name, RunJs.NumStr(top)));
            }
            var stones = Ashen.Domain.Rewards.Smithing.StoneBalance(run);
            var inventory = RewardRolls.CarriedIds(run.Obj(K.Loadout));
            var candidates = new JArray();
            var coreActive = Active(d, run);
            foreach (var itemRef in ItemSmithing.OwnedItemRefs(shop, run))
            {
                var identity = ItemUpgrades.Identity(itemRef);
                var piece = ItemSmithing.ItemByRef(shop, itemRef);
                var armament = identity.ItemKind == V.ArmamentRefPrefix;
                var currentLevel = Js.Or0(levelMap[itemRef]);
                var nextLevel = currentLevel + 1;
                var upgradeRows = ItemUpgrades.Rows(shop.Combat, itemRef, nextLevel);
                if (upgradeRows.Count == 0) continue;
                var cost = free ? 0 : ItemSmithing.UpgradeCost(shop, upgradeRows);
                // Core 2: cards are not tier-rewritten, so the receipt comes from the Core pipeline and the legacy rows only supply the item's tier ladder and cost.
                var core = identity.ItemKind != V.RelicKind && coreActive ? Stats(d, run, itemRef, currentLevel, nextLevel) : null;
                var previews = core != null ? PreviewRows(piece.Str(K.Name), core) : new JArray();
                var requirements = armament ? ItemSmithing.RequirementPreview(shop, run, piece, currentLevel, nextLevel) : new JArray();
                var costTag = ItemUpgrades.CostTag(shop.Combat);
                var authoredChanges = upgradeRows.Where(r => r.Str(K.Tag) != costTag).ToList();
                JArray changes;
                if (core != null)
                {
                    var extras = new List<JToken>();
                    if (core.Count > 0)
                    {
                        foreach (var row in requirements.OfType<JObject>().Where(r => r.Num(SK.CurrentRequired) != r.Num(SK.NextRequired)))
                            extras.Add(Row(RS.RowRequirement, RunJs.Fmt(RS.RequirementTagFormat, row.Str(RK.AttributeId)), RunJs.Fmt(RS.RequirementLabelFormat, RunJs.Key(row[K.Label])), row[SK.CurrentRequired], row[SK.NextRequired]));
                        if (identity.ItemKind == V.Armor) extras.AddRange(ItemSmithing.ValueReceipts(shop, itemRef, currentLevel, nextLevel));
                    }
                    changes = new JArray(core.Concat(extras).Select(row => row.DeepClone()));
                }
                else changes = ItemSmithing.ValueReceipts(shop, itemRef, currentLevel, nextLevel);
                if (authoredChanges.Count == 0 || changes.Count == 0 || changes.OfType<JObject>().All(r => Same(r[RR.Before], r[RR.After]))) continue;
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
                c[SK.AffectedCards] = new JArray();
                c[SK.PreviewCards] = previews;
                c[SK.Changes] = changes;
                if (core != null) c[RR.Core] = true;
                candidates.Add(c);
            }
            return Js.Obj(RK.SchemaVersion, shop.RuleNum(SK.Smith, SK.SmithingSchemaVersion), SK.Stones, stones, SK.Candidates, candidates);
        }

        // ------------------------------------------------------------------ the card rows (two real fights)

        private static bool IsEquipped(JObject run, string itemRef)
        {
            var identity = ItemUpgrades.Identity(itemRef);
            var loadout = run.Obj(K.Loadout);
            foreach (var slot in (loadout?.Obj(K.Sets) ?? new JObject()).Properties())
            {
                var ids = slot.Value as JArray;
                var index = Js.Truthy(loadout.Obj(K.Active)?[slot.Name]) ? (int)loadout.Obj(K.Active).Num(slot.Name) : 0;
                var id = ids != null && index >= 0 && index < ids.Count ? ids[index] : null;
                if (id != null && Js.Str(id) == identity.ItemId && (slot.Name == RK.Armor) == (identity.ItemKind == V.Armor)) return true;
            }
            return false;
        }

        private sealed class FaceRow
        {
            public string CardId;
            public string Name;
            public int Copies = 1;
            public Dictionary<string, double?> Values = new Dictionary<string, double?>(StringComparer.Ordinal);
        }

        /// <summary>
        /// liveCardValues(registries, run, itemRef, level): the run's fight with the item at a tier (a throwaway fight on a fresh generator against the first enemy of the
        /// registry), and for every distinct card face in the hand, the draw pile and the discard pile its damage and Block as the card face prints them. Faces keep the
        /// order the piles list them.
        /// </summary>
        private static List<KeyValuePair<string, FaceRow>> LiveCardValues(Core2RewardsData d, JObject run, string itemRef, double level)
        {
            var current = run.Obj(K.ItemUpgradeLevels);
            var levels = current != null ? Js.Spread(current) : new JObject();
            if (Js.Or0(current?[itemRef]) != level) levels[itemRef] = Js.N(level);
            var enemies = d.Combat.Combat.Enemies.Ids.ToList();
            var rows = new List<KeyValuePair<string, FaceRow>>();
            if (enemies.Count == 0) return rows;
            // The web stages {...run, itemUpgradeLevels}; here the run's own field stands in for the call and is put back.
            var was = run[K.ItemUpgradeLevels];
            run[K.ItemUpgradeLevels] = levels;
            CombatState combat;
            try { combat = CoreRunCombat.Create(d.Combat, new Rng((uint)d.RestRules.Num(RR.SimulationSeed)), run, null, null, new[] { enemies[0] }); }
            finally
            {
                if (was != null) run[K.ItemUpgradeLevels] = was;
                else run.Remove(K.ItemUpgradeLevels);
            }
            var ops = Js.Items(d.RestRules[RR.CardValues]).OfType<JObject>().ToList();
            var byKey = new Dictionary<string, FaceRow>(StringComparer.Ordinal);
            foreach (var pile in new[] { combat.Piles.Hand, combat.Piles.Draw, combat.Piles.Discard })
                foreach (var instance in pile)
                {
                    // Copies of one card can differ (mods, upgraded, rank...): each distinct face is its own row.
                    var face = new JObject(instance.Properties().Where(p => p.Name != K.InstanceId).Select(p => new JProperty(p.Name, p.Value.DeepClone())));
                    var key = instance.Str(K.CardId) + RS.FaceSeparator + RunJs.Json(face);
                    if (byKey.TryGetValue(key, out var seen))
                    {
                        seen.Copies++;
                        continue;
                    }
                    var printed = CoreCosts.Resolve(combat, instance);
                    var row = new FaceRow { CardId = instance.Str(K.CardId), Name = printed.Str(K.Name) };
                    // A card with no damage or Block effect cannot change; skip its (costly) detached preview.
                    if (Js.Items(printed[K.Effects]).OfType<JObject>().Any(e => ops.Any(o => o.Str(RR.Op) == e.Str(K.Op))))
                        Core2CardFace.Read(combat, instance, printed, ops.Select(o => o.Str(RR.Op)).ToList(), row.Values, name => row.Name = name);
                    byKey[key] = row;
                    rows.Add(new KeyValuePair<string, FaceRow>(key, row));
                }
            return rows;
        }

        /// <summary>
        /// coreSmithingCards(registries, run, itemRef, currentLevel, nextLevel): each deck card whose live damage or Block changes with this tier step (a worn item only; a stored item
        /// changes no card until it is worn). Runs two real fights, so callers ask for it only for the item being reviewed or committed.
        /// </summary>
        public static JArray CardRows(Core2RewardsData d, JObject run, string itemRef, double currentLevel, double nextLevel)
        {
            var cards = new JArray();
            if (GearItemFor(d, run, itemRef) == null || !IsEquipped(run, itemRef)) return cards;
            var was = LiveCardValues(d, run, itemRef, currentLevel).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            var now = LiveCardValues(d, run, itemRef, nextLevel);
            foreach (var pair in now)
                foreach (var spec in Js.Items(d.RestRules[RR.CardValues]).OfType<JObject>())
                {
                    var op = spec.Str(RR.Op);
                    double? b = was.TryGetValue(pair.Key, out var earlier) && earlier.Values.TryGetValue(op, out var bv) ? bv : null;
                    double? a = pair.Value.Values.TryGetValue(op, out var av) ? av : null;
                    if (b == null || a == null || b == a) continue;
                    cards.Add(new JObject
                    {
                        [RR.Kind] = RS.RowCard, [RR.Tag] = RunJs.Fmt(RS.CardTagFormat, pair.Key, op), [RR.Label] = RunJs.Fmt(RS.CardLabelFormat, pair.Value.Name, spec.Str(RR.Label).ToLowerInvariant()),
                        [RR.CardId] = pair.Value.CardId, [RR.Name] = pair.Value.Name, [RR.Op] = op, [RR.OpLabel] = spec.Str(RR.Label), [RR.Rating] = spec.Str(RR.Rating),
                        [RR.Copies] = pair.Value.Copies, [RR.Before] = Js.N(b.Value), [RR.After] = Js.N(a.Value),
                    });
                }
            return cards;
        }

        /// <summary>coreAffectedCards(cards): one row per changed card copy (the distinct faces expanded by their copies).</summary>
        public static JArray AffectedCards(JArray cards)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var rows = new JArray();
            foreach (var row in cards.OfType<JObject>())
            {
                var tag = row.Str(RR.Tag);
                var face = tag.Substring(0, tag.LastIndexOf(RS.TagSeparator[0]));
                if (!seen.Add(face)) continue;
                for (var i = 0; i < row.Num(RR.Copies); i++) rows.Add(row.DeepClone());
            }
            return rows;
        }

        // ------------------------------------------------------------------ commit

        /// <summary>
        /// commitItemUpgrade(registries, run, itemRef, undefined, { free }) for a Core 2 run: revalidates through the plan (priced as the content prices it), promotes the item one tier,
        /// restamps the cards it supplies and writes <c>lastSmithingReceipt</c>. The receipt's changes are the item's stat rows; the live card rows (<paramref name="simulate"/>) come with
        /// them when the caller asks (the rest site adds them itself after the whole service has committed). Throws the web's sentence when the item is no candidate.
        /// </summary>
        public static JObject Commit(Core2RewardsData d, JObject run, string requestedItemRef, bool free, bool simulate)
        {
            var shop = d.Smith;
            var itemRef = requestedItemRef != null && requestedItemRef.Contains(V.ItemRefSeparator) ? requestedItemRef : string.Join(V.ItemRefSeparator, V.ArmamentRefPrefix, requestedItemRef ?? V.Undefined);
            var plan = Plan(d, run, false);
            var candidate = Js.Items(plan[SK.Candidates]).OfType<JObject>().FirstOrDefault(c => c.Str(K.ItemRef) == itemRef)
                            ?? throw new InvalidOperationException(RunJs.Fmt(Ashen.Generated.Core2RestMessages.NotCandidate, itemRef));
            if (!free && !candidate.Is(SK.Affordable)) throw new InvalidOperationException(RunJs.Fmt(Ashen.Generated.Core2RestMessages.InsufficientStones, RunJs.NumStr(candidate.Num(SK.Shortfall))));
            var beforeStones = plan.Num(SK.Stones);
            run.Put(RK.SmithingStones, free ? beforeStones : beforeStones - candidate.Num(K.Cost));
            var levels = ItemSmithing.Levels(run);
            levels[itemRef] = candidate[SK.NextLevel].DeepClone();
            run[K.ItemUpgradeLevels] = levels;
            run.Remove(RK.ArmamentLevels);
            ItemSmithing.RestampCards(shop, run, Js.Items(run[K.Deck]).OfType<JObject>());
            var armament = candidate.Str(SK.ItemKind) == V.ArmamentRefPrefix;
            var spent = free ? 0 : candidate.Num(K.Cost);
            var receipt = Js.Obj(RK.SchemaVersion, shop.RuleNum(SK.Smith, SK.SmithingSchemaVersion), K.ItemRef, itemRef, SK.ItemKind, candidate[SK.ItemKind],
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
            receipt[RR.Purse] = RS.PurseStones;
            var changes = (JArray)candidate[SK.Changes].DeepClone();
            var affected = new JArray();
            if (candidate.Is(RR.Core) && simulate)
            {
                var cards = CardRows(d, run, itemRef, candidate.Num(SK.CurrentLevel), candidate.Num(SK.NextLevel));
                foreach (var row in cards) changes.Add(row.DeepClone());
                affected = AffectedCards(cards);
            }
            receipt[SK.Changes] = changes;
            receipt[SK.AffectedCards] = affected;
            run[SK.LastSmithingReceipt] = receipt.DeepClone();
            return receipt;
        }
    }
}
