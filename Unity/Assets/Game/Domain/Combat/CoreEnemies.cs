using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using CK = Ashen.Generated.Core2CombatKeys;
using CM = Ashen.Generated.Core2CombatMessages;
using CV = Ashen.Generated.Core2CombatValues;
using EK = Ashen.Generated.Core2EnemyKeys;
using EV = Ashen.Generated.Core2EnemyValues;
using PC = Ashen.Generated.Core2ProfileContentKeys;
using RC = Ashen.Generated.Core2RunKeys;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// A Core 2 enemy as a character (web engine/kernel/gameConfig.js characterSettings, enemyLevel, characterRule,
    /// resolveEnemyAttributes, enemyItemBonuses, enemyPools; model/gameProfileEnemy.js gameProfileEnemyRatings): the pinned profile's
    /// enemy row, its tier and tags, a seeded attribute spread, the worn gear and relics, and from those the SP, MP, Attack and Defense
    /// the fight opens with. Attribute variance is a hash of the run seed and the enemy's instance id, never an RNG stream.
    /// </summary>
    public static class CoreEnemies
    {
        private static JObject EnemyRow(CombatState c, string enemyId) =>
            Js.Items(c.CoreData.Content(PC.Enemies)).OfType<JObject>().FirstOrDefault(row => row.Str(K.Id) == enemyId);

        /// <summary>characterSettings(modules): the enemies and characters modules over one another, with the variance module.</summary>
        public static JObject Settings(CombatState c)
        {
            var d = c.CoreData;
            var settings = Js.Spread(d.Module(CK.EnemiesModule), d.Module(CK.CharactersModule));
            var variance = d.Module(CK.VarianceModule);
            if (variance != null) settings[CK.Variance] = variance.DeepClone();
            return settings;
        }

        /// <summary>enemyLevel(row, settings): the row's own level, else the profile's minimum (or maximum) level, at least 1.</summary>
        public static double Level(JObject row, JObject settings)
        {
            if (Js.IsInt(row[K.Level])) return Math.Max(1, row.Num(K.Level));
            var profile = row.Obj(CK.LevelProfile) ?? new JObject();
            var max = settings.Str(CK.LevelFrom) == CV.LevelFromMax;
            var value = max ? (Js.Nullish(profile[CK.Max]) ? 1 : profile.Num(CK.Max)) : (Js.Nullish(profile[CK.Min]) ? 1 : profile.Num(CK.Min));
            return Math.Max(1, value);
        }

        /// <summary>characterTags(row): the row's tags, else its kind and tier tags.</summary>
        public static List<string> Tags(JObject row)
        {
            if (row[K.Tags] is JArray tags) return tags.Select(Js.Str).ToList();
            var list = new List<string> { CV.TagCharacterEnemy };
            if (Js.Truthy(row[CK.Tier])) list.Add(CV.TagTierPrefix + Js.Str(row[CK.Tier]));
            return list;
        }

        /// <summary>characterRule(row, settings): the pools and hand the row's tags add up to, and the last budget they set.</summary>
        public static JObject Rule(JObject row, JObject settings)
        {
            var pools = new JObject();
            foreach (var key in new[] { CV.PoolHp, CV.PoolSp, CV.PoolMp }) pools.Put(key, 0);
            var rule = new JObject { [CK.Pools] = pools, [CK.Hand] = 0 };
            foreach (var tag in Tags(row))
            {
                var r = settings.Obj(CK.TagRules)?.Obj(tag);
                if (r == null) continue;
                foreach (var key in new[] { CV.PoolHp, CV.PoolSp, CV.PoolMp }) pools.Put(key, pools.Num(key) + Js.Or0(r.Obj(CK.Pools)?[key]));
                rule.Put(CK.Hand, rule.Num(CK.Hand) + Js.Or0(r[CK.Hand]));
                foreach (var key in new[] { CK.AttributeBudget, CK.DeckSize, CK.SpRange, CK.DeckDefault }) if (Js.Truthy(r[key])) rule[key] = r[key].DeepClone();
                if (Js.Truthy(r[CK.Variance])) rule[CK.Variance] = true;
            }
            return rule;
        }

        private static Dictionary<string, double> Weights(JObject row, JObject settings)
        {
            var fill = settings.Obj(CK.AttributeFill);
            var order = (row[CK.AttributePriority] is JArray p && Js.Truthy(p) ? p : fill[CK.DefaultPriority] as JArray).Select(Js.Str).ToList();
            var ranks = (row[CK.PriorityWeights] is JArray r && Js.Truthy(r) ? r : fill[CK.PriorityWeights] as JArray);
            var weights = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var i = 0; i < order.Count; i++) weights[order[i]] = i < ranks.Count && !Js.Nullish(ranks[i]) ? Js.D(ranks[i]) : 0;
            return weights;
        }

        /// <summary>resolveEnemyAttributes(row, settings, seed): authored points kept, the rest of the tier budget split by priority within the caps, then seeded variance.</summary>
        public static JObject Attributes(CoreCombatData cd, JObject row, JObject settings, string seed)
        {
            var d = cd.Run;
            var enemyRules = cd.Section(CK.Enemies);
            var attributeIds = CoreGear.Attributes(d);
            var fill = settings.Obj(CK.AttributeFill);
            var tier = Rule(row, settings);
            var budget = Js.IsInt(row[CK.AttributePoints]) ? row.Num(CK.AttributePoints) : Js.D((tier[CK.AttributeBudget] as JArray)[1]);
            var set = new JObject();
            foreach (var id in attributeIds) set.Put(id, Js.Or0(row.Obj(K.Attributes)?[id]));
            var output = Js.Spread(set);
            var caps = Js.Spread(fill.Obj(CK.Caps), row.Obj(CK.AttributeCaps));
            var order = Weights(row, settings);
            var weights = new JObject();
            foreach (var kv in order) weights.Put(kv.Key, kv.Value);
            var left = budget - attributeIds.Sum(id => set.Num(id));
            var guardLimit = enemyRules.Num(CK.FillGuard);
            for (var guard = 0; left > 0 && guard < guardLimit; guard++)
            {
                var open = new JObject();
                foreach (var p in weights.Properties()) if (caps[p.Name] == null || output.Num(p.Name) < caps.Num(p.Name)) open[p.Name] = p.Value.DeepClone();
                if (!open.Properties().Any()) break;
                var share = CoreGear.Split(left, open);
                foreach (var p in share.Properties())
                {
                    var room = caps[p.Name] == null ? Js.D(p.Value) : Math.Min(Js.D(p.Value), caps.Num(p.Name) - output.Num(p.Name));
                    output.Put(p.Name, output.Num(p.Name) + room);
                    left -= room;
                }
                weights = open;
            }
            var v = Js.Spread(new JObject { [CK.Points] = 0, [CK.Among] = enemyRules.Num(CK.VarianceAmong) }, settings.Obj(CK.Variance), row.Obj(CK.Variance));
            if (seed != null && Js.Truthy(settings[CK.Variance]) && v.Num(CK.Points) > 0 && Js.Truthy(tier[CK.Variance]))
            {
                var priority = (row[CK.AttributePriority] is JArray p && Js.Truthy(p) ? p : fill[CK.DefaultPriority] as JArray).Select(Js.Str).Take((int)v.Num(CK.Among)).ToList();
                var hash = enemyRules.Obj(CK.Hash);
                uint h = (uint)hash.Num(CK.Seed);
                foreach (var ch in seed + row.Str(K.Id)) h = unchecked((uint)(ulong)(h * hash.Num(CK.Multiplier) + ch));
                for (var i = 0; i < v.Num(CK.Points); i++)
                {
                    var from = priority[(int)(h % (uint)priority.Count)];
                    var to = priority[(int)((h >> (int)hash.Num(CK.Shift)) % (uint)priority.Count)];
                    h = unchecked((uint)(ulong)(h * hash.Num(CK.LcgMultiplier) + hash.Num(CK.LcgIncrement)));
                    if (from != to && output.Num(from) > set.Num(from) && (caps[to] == null || output.Num(to) < caps.Num(to)))
                    {
                        output.Put(from, output.Num(from) - 1);
                        output.Put(to, output.Num(to) + 1);
                    }
                }
            }
            return output;
        }

        /// <summary>enemyItemBonuses(row, content, gearSettings): attribute and pool points from the worn gear and the relics' <c>self.x=+n</c> modifiers.</summary>
        public static JObject ItemBonuses(CoreCombatData cd, JObject row, JToken gearItems, JToken relics, JObject gearSettings)
        {
            var d = cd.Run;
            var enemyRules = cd.Section(CK.Enemies);
            var attributeIds = CoreGear.Attributes(d);
            var poolMods = Js.Items(enemyRules[CK.PoolMods]).Select(Js.Str).ToList();
            var bonus = new JObject();
            foreach (var id in attributeIds.Concat(poolMods).Concat(CoreGear.AllKeys(d)).Distinct()) bonus.Put(id, 0);
            var gear = Js.Items(gearItems).OfType<JObject>().ToDictionary(item => item.Str(K.Id), item => item, StringComparer.Ordinal);
            foreach (var slot in Js.Items(row[CK.DefaultGear]))
            {
                var id = Js.Str(Js.Get(slot, K.Id));
                if (id == null || !gear.TryGetValue(id, out var item) || gearSettings == null) continue;
                var resolved = CoreGear.ResolveItem(d, item, gearSettings, Js.Nullish(item[CK.Rank]) ? 1 : item.Num(CK.Rank));
                var budgeted = gearSettings.Obj(CK.ArmorBudget) != null;
                foreach (var key in budgeted ? CoreGear.AllKeys(d) : attributeIds)
                    bonus.Put(key, bonus.Num(key) + (budgeted ? Js.Or0(resolved.Obj(CK.Values)?[key]) : Js.Or0(resolved.Obj(CK.Points)?[key])));
            }
            var relicRows = Js.Items(relics).OfType<JObject>().ToDictionary(item => item.Str(K.Id), item => item, StringComparer.Ordinal);
            foreach (var slot in Js.Items(row[CK.DefaultRelics]))
            {
                var id = Js.Str(Js.Get(slot, K.Id));
                if (id == null || !relicRows.TryGetValue(id, out var relic)) continue;
                foreach (var modifier in Js.Items(relic[CK.Modifiers]).Select(Js.Str))
                {
                    if (!TryParseModifier(cd, modifier, attributeIds.Concat(poolMods).ToList(), out var stat, out var amount)) continue;
                    bonus.Put(stat, bonus.Num(stat) + amount);
                }
            }
            return bonus;
        }

        /// <summary>ITEM_MODIFIER: <c>self.&lt;attribute or pool&gt;=&lt;signed integer&gt;</c>.</summary>
        private static bool TryParseModifier(CoreCombatData cd, string text, List<string> allowed, out string stat, out double amount)
        {
            var d = cd.Run;
            var enemyRules = cd.Section(CK.Enemies);
            stat = null;
            amount = 0;
            var rules = enemyRules;
            var prefix = rules.Str(CK.ModifierPrefix);
            var equals = rules.Str(CK.ModifierEquals);
            if (text == null || !text.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var at = text.IndexOf(equals, StringComparison.Ordinal);
            if (at < 0) return false;
            var id = text.Substring(prefix.Length, at - prefix.Length);
            var number = text.Substring(at + equals.Length);
            if (!allowed.Contains(id) || number.Length <= 1 || !(enemyRules.Str(CK.ModifierSigns) ?? string.Empty).Contains(number[0])) return false;
            if (!number.Skip(1).All(char.IsDigit)) return false;
            stat = id;
            amount = double.Parse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>enemyPools(row, settings, level, bonus): HP, SP, MP, Attack and Defense from the base, the attributes and the level.</summary>
        public static JObject Pools(CoreCombatData cd, JObject row, JObject settings, double level, JObject bonus, JObject resolvedAttributes)
        {
            var d = cd.Run;
            var enemyRules = cd.Section(CK.Enemies);
            var attributeIds = CoreGear.Attributes(d);
            var poolMods = Js.Items(enemyRules[CK.PoolMods]).Select(Js.Str).ToList();
            var tier = Rule(row, settings);
            var rounding = settings.Str(CK.Rounding);
            Func<double, double> round = x => rounding == CV.RoundUp ? Math.Ceiling(x) : rounding == CV.RoundNearest ? Math.Floor(x + Ashen.Generated.CombatMath.Half) : Math.Floor(x);
            var baseValues = new JObject { [CV.PoolHp] = tier.Obj(CK.Pools).Num(CV.PoolHp), [CV.PoolSp] = tier.Obj(CK.Pools).Num(CV.PoolSp), [CV.PoolMp] = tier.Obj(CK.Pools).Num(CV.PoolMp),
                [CK.Hand] = tier.Num(CK.Hand), [CK.RatingAr] = 0, [CK.RatingDr] = 0 };
            var own = resolvedAttributes ?? Attributes(cd, row, settings, null);
            var attributes = new JObject();
            foreach (var id in attributeIds) attributes.Put(id, Math.Max(0, Js.Or0(own[id]) + Js.Or0(bonus?[id])));
            var pools = new JObject();
            foreach (var stat in settings.Obj(CK.StatRules).Properties())
            {
                var rule = (JObject)stat.Value;
                var value = baseValues[stat.Name] != null ? baseValues.Num(stat.Name) : 0;
                foreach (var id in attributeIds) if (Js.Truthy(rule[id])) value += round(attributes.Num(id) * rule.Num(id));
                if (Js.Truthy(rule[CK.PerLevel])) value += round((level - 1) * rule.Num(CK.PerLevel));
                var mod = poolMods.Contains(stat.Name) ? Js.Or0(bonus?[stat.Name]) : 0;
                var floor = Js.IsNum(settings.Obj(CK.PenaltyFloor)?[stat.Name]) ? settings.Obj(CK.PenaltyFloor).Num(stat.Name) : 0;
                pools.Put(stat.Name, mod < 0 ? Math.Max(value + mod, Math.Min(value, floor)) : value + mod);
            }
            return pools;
        }

        /// <summary>
        /// gameProfileEnemyRatings(ctx, enemy): the fields a fight assigns an enemy from its pinned profile row; null when FixCore is off or the row is
        /// missing. The seed of the attribute variance is the run RNG seed with the instance id.
        /// </summary>
        public static JObject Ratings(CombatState c, JObject enemy)
        {
            if (!CoreFixCore.Enabled(c)) return null;
            var d = c.CoreData;
            var row = EnemyRow(c, enemy.Str(K.EnemyId));
            if (row == null) return null;
            var settings = Settings(c);
            var level = Level(row, settings);
            var seed = c.Rng.Seed.ToString(CultureInfo.InvariantCulture) + CV.SeedJoin + enemy.Str(K.Id);
            var allocated = Attributes(d, row, settings, seed);
            var bonus = ItemBonuses(d, row, d.Content(PC.Gear), d.Content(PC.EnemyRelics), d.Module(CK.GearModule));
            var attributes = new JObject();
            foreach (var p in allocated.Properties()) attributes.Put(p.Name, Js.D(p.Value) + Js.Or0(bonus[p.Name]));
            var pools = Pools(d, row, settings, level, bonus, allocated);
            var current = enemy.Obj(CK.Ratings);
            return new JObject
            {
                [K.Attributes] = attributes,
                [K.Level] = Js.N(level),
                [K.MaxStamina] = Js.N(pools.Num(CV.PoolSp)),
                [K.Stamina] = Js.N(pools.Num(CV.PoolSp)),
                [K.MaxMana] = Js.N(pools.Num(CV.PoolMp)),
                [K.Mana] = Js.N(pools.Num(CV.PoolMp)),
                [CK.BaseEvasionBonus] = Js.N(Js.Or0(bonus[CK.Evasion])),
                [CK.BasePrecisionBonus] = Js.N(Js.Or0(bonus[CK.Precision])),
                [CK.Ratings] = new JObject
                {
                    [CK.RatingAr] = Js.N(Js.Or0(pools[CK.RatingAr]) + Js.Or0(bonus[CK.RatingAr])),
                    [CK.RatingDr] = Js.N(Js.Or0(pools[CK.RatingDr]) + Js.Or0(bonus[CK.RatingDr])),
                    [CK.RatingPr] = 0,
                    [CK.RatingPoise] = Js.N(Js.Or0(current?[CK.RatingPoise]) + Js.Or0(bonus[CK.RatingPoise])),
                    [CK.RatingWard] = Js.N(Js.Or0(current?[CK.RatingWard]) + Js.Or0(bonus[CK.RatingWard])),
                },
            };
        }

        /// <summary>syncConfiguredEnemyProperties(ctx): an enemy holds exactly its pinned gear and relic slots; those that attach mechanics mount them.</summary>
        public static void SyncConfiguredProperties(CombatState c)
        {
            if (!CoreFixCore.Enabled(c)) return;
            foreach (var enemy in c.Enemies)
            {
                var row = EnemyRow(c, enemy.Str(K.EnemyId));
                if (row == null) continue;
                var carriers = new List<(string Kind, string Id, string InstanceId, List<string> Tags, JArray MechanicTags, JArray MechanicBindings)>();
                foreach (var (field, catalog, relic) in new[] { (CK.DefaultGear, PC.Gear, false), (CK.DefaultRelics, PC.EnemyRelics, true) })
                    foreach (var slot in Js.Items(row[field]).OfType<JObject>())
                    {
                        var source = Js.Items(c.CoreData.Content(catalog)).OfType<JObject>().FirstOrDefault(r => r.Str(K.Id) == slot.Str(K.Id));
                        if (source == null || !(Js.Items(source[CK.MechanicTags]).Any() || Js.Items(source[CK.MechanicBindings]).Any())) continue;
                        carriers.Add((relic ? V.RelicKind : source.Str(K.Kind) == V.Armor ? V.ArmourKind : V.ArmamentRefPrefix, source.Str(K.Id), source.Str(K.Id) + EV.SlotSeparator + slot.Str(EK.SlotKey),
                            Js.Items(source[K.PropertyTags]).Select(Js.Str).ToList(), new JArray(Js.Items(source[CK.MechanicTags]).Select(t => t.DeepClone())), (JArray)(source[CK.MechanicBindings]?.DeepClone() ?? new JArray())));
                    }
                Properties.SyncConfiguredEnemy(c, enemy, carriers);
            }
        }
    }
}
