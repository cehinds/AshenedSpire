using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using CombatMath = Ashen.Generated.CombatMath;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;
using RM = Ashen.Generated.RunMessages;
using RV = Ashen.Generated.RunValues;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Run
{
    /// <summary>One equipment role's source (shipped equipmentRoleSource) and, once priced, its receipt.</summary>
    public sealed class KitRow
    {
        public string Role;
        public string SlotId;
        public JObject Piece;
        public JObject Profile;
        public JObject Receipt;
    }

    /// <summary>A parsed equipment mod (shipped parseMod).</summary>
    public sealed class ParsedMod
    {
        public string Prefix;
        public string Field;
        public bool Add;
        public double Value;
    }

    /// <summary>runMods: the self.* half of the mod vocabulary.</summary>
    public sealed class RunModsResult
    {
        public readonly List<KeyValuePair<string, double>> Pools = new List<KeyValuePair<string, double>>();
        public double SwapCostDelta;
        public JArray StartStatuses = new JArray();

        public double Pool(string field) => Pools.First(p => p.Key == field).Value;
    }

    /// <summary>
    /// What you carry and what it does to your numbers (shipped model/loadout.js: createLoadout, the equipment
    /// profile rule snapshot, the kit plan and its receipts, card and run mods, the pool reconcile, the requirement
    /// receipt; with model/ratingFormula.js effectiveEquipmentRating and model/itemUpgrades.js requirement deltas).
    /// </summary>
    public static class Loadout
    {
        private static readonly Regex ModPattern = new Regex(Ashen.Generated.CombatPatterns.CardMod, RegexOptions.CultureInvariant);

        public static List<string> PoolFields(RunData d) => d.RuleList(K.Loadout, RK.PoolFields);

        public static List<string> Roles(RunData d) => d.RuleList(K.Loadout, RK.Roles);

        /// <summary>parseMod('strike.damage=+4') → { prefix, field, mode, value }, or null when malformed.</summary>
        public static ParsedMod ParseMod(JToken raw)
        {
            var text = raw == null ? V.Undefined : Js.IsStr(raw) ? Js.Str(raw) : raw.ToString();
            var m = ModPattern.Match(text.Trim());
            if (!m.Success) return null;
            var sign = m.Groups[V.GroupSign].Value;
            var num = double.Parse(m.Groups[V.GroupNumber].Value, System.Globalization.CultureInfo.InvariantCulture);
            return new ParsedMod { Prefix = m.Groups[RV.GroupPrefix].Value, Field = m.Groups[V.GroupField].Value, Add = sign.Length > 0, Value = sign == V.Minus ? -num : num };
        }

        /// <summary>moveEquipmentPool: resize one maximum while retaining a deficit that may exceed the smaller vessel.</summary>
        public static double MoveEquipmentPool(RunData d, JObject holder, string maxField, double nextMax, JToken carriedDeficit)
        {
            var currentField = d.RuleObj(K.Loadout, RK.PoolCurrent).Str(maxField);
            if (currentField == null || !Js.IsFinite(holder[maxField]) || !Js.IsFinite(holder[currentField]))
                throw new InvalidOperationException(RunJs.Fmt(RM.MovePoolNeedsFinite, maxField, currentField ?? V.Undefined));
            var oldMax = holder.Num(maxField);
            var observed = Math.Max(0, oldMax - holder.Num(currentField));
            var prior = Js.IsInt(carriedDeficit) && Js.D(carriedDeficit) >= 0 ? Js.D(carriedDeficit) : observed;
            var represented = Math.Min(prior, oldMax);
            var deficit = Math.Max(0, prior + observed - represented);
            holder.Put(maxField, nextMax);
            holder.Put(currentField, Math.Max(0, nextMax - deficit));
            return deficit;
        }

        /// <summary>slotHand(slot): 'left' | 'right' | null — where a slot is.</summary>
        public static string SlotHand(RunData d, JObject slot)
        {
            var h = slot?.Str(K.Hand);
            return h != null && d.RuleList(K.Loadout, RK.Hands).Contains(h) ? h : null;
        }

        /// <summary>createLoadout(registries, classId, startingKit, startingArmour).</summary>
        public static JObject CreateLoadout(RunData d, string classId, JObject startingKit, JObject startingArmour)
        {
            var sets = new JObject();
            var active = new JObject();
            foreach (var slot in d.EquipmentRows(K.Slots))
            {
                var size = Math.Max(1, slot.Num(K.Sets));
                if (double.IsNaN(size)) throw new InvalidOperationException(RunJs.Fmt(RM.SlotSetsInvalid, slot.Str(K.Id)));
                var cells = new JArray();
                for (var i = 0; i < size; i++) cells.Add(Js.Null());
                sets[slot.Str(K.Id)] = cells;
                active.Put(slot.Str(K.Id), 0);
            }
            var starting = startingArmour ?? d.EquipmentRows(K.Armour).FirstOrDefault(o => o.Str(K.ClassId) == classId && o.Str(RK.Unlock) == string.Empty && !o.Is(RK.SharedSet));
            if (starting != null && sets[RK.Armor] is JArray armor) armor[0] = starting[K.Id].DeepClone();
            var kit = startingKit ?? d.EquipmentRows(RK.StartingKits).FirstOrDefault(r => r.Str(K.ClassId) == classId && r[K.Baseline]?.Type == JTokenType.Boolean && r.Is(K.Baseline));
            foreach (var slotId in new[] { RK.RightHand, RK.LeftHand })
            {
                var pieceId = kit?[slotId];
                if (!Js.Truthy(pieceId)) continue;
                if (sets[slotId] is JArray cells) cells[0] = pieceId.DeepClone();
            }
            return new JObject
            {
                [K.Sets] = sets,
                [K.Active] = active,
                [RK.Storage] = new JArray(),
                [RK.CreationArmourGrant] = starting != null ? Js.Obj(K.ClassId, classId, K.Id, starting[K.Id].DeepClone()) : Js.Null(),
            };
        }

        public static JObject ProfileById(RunData d, JToken id) =>
            d.EquipmentRows(K.BasicCardProfiles).FirstOrDefault(p => JToken.DeepEquals(p[K.Id], id) && id != null);

        private static JObject ProfileRule(RunData d, JObject profile)
        {
            var rule = new JObject();
            foreach (var key in d.RuleList(K.Loadout, RK.ProfilePatchFields))
            {
                var v = profile[key];
                if (v == null) continue;
                rule[key] = Js.Str(v) == string.Empty ? Js.Null() : v.DeepClone();
            }
            foreach (var key in d.RuleList(K.Loadout, RK.ProfileCarrierFields)) if (profile[key] != null) rule[key] = profile[key].DeepClone();
            if (profile[RK.Compatibility] != null) rule[RK.Compatibility] = profile[RK.Compatibility].DeepClone();
            return rule;
        }

        private static IEnumerable<JToken> ProfileLayers(DerivedOptions options)
        {
            yield return options?.ModeModifiers;
            var run = options?.RunModifiers;
            if (run is JArray a) foreach (var x in a) yield return x;
            else if (Js.Truthy(run)) yield return run;
            yield return options?.ExplicitOverride;
        }

        /// <summary>createEquipmentProfileRuleSnapshot: the host-owned equipment scaling rows, resolved once per run.</summary>
        public static JObject CreateProfileSnapshot(RunData d, DerivedOptions options)
        {
            var profiles = new JObject();
            foreach (var profile in d.EquipmentRows(K.BasicCardProfiles)) profiles[profile.Str(K.Id)] = ProfileRule(d, profile);
            var patchFields = d.RuleList(K.Loadout, RK.ProfilePatchFields);
            foreach (var layer in ProfileLayers(options))
            {
                if (!Js.Truthy(layer) || Js.Nullish(Js.Get(layer, RK.EquipmentProfiles))) continue;
                if (!(layer[RK.EquipmentProfiles] is JObject overrides)) throw new InvalidOperationException(RM.ProfilesOverrideNotObject);
                foreach (var p in overrides.Properties())
                {
                    if (!(profiles[p.Name] is JObject target)) throw new InvalidOperationException(RunJs.Fmt(RM.ProfilesOverrideUnknown, p.Name));
                    if (!(p.Value is JObject patch)) throw new InvalidOperationException(RunJs.Fmt(RM.ProfilesPatchNotObject, p.Name));
                    foreach (var key in patch.Properties()) if (!patchFields.Contains(key.Name)) throw new InvalidOperationException(RunJs.Fmt(RM.ProfilesPatchUnknownField, p.Name, key.Name));
                    foreach (var key in patch.Properties()) target[key.Name] = key.Value.DeepClone();
                }
            }
            var rarity = RunJs.Clone(d.EquipmentBalance.Obj(RK.RarityBonuses)) ?? new JObject();
            return RestoreProfileSnapshot(d, new JObject
            {
                [K.SnapshotVersion] = Js.N(d.RuleNum(K.Loadout, RK.ProfileSnapshotVersion)),
                [RK.Profiles] = profiles,
                [RK.RarityBonuses] = rarity,
            });
        }

        /// <summary>restoreEquipmentProfileRuleSnapshot: validate and clone a saved scaling snapshot, no live-data repair.</summary>
        public static JObject RestoreProfileSnapshot(RunData d, JToken input)
        {
            if (!(input is JObject snapshot)) throw new InvalidOperationException(RM.ProfileSnapshotNotObject);
            var version = snapshot[K.SnapshotVersion];
            var current = d.RuleNum(K.Loadout, RK.ProfileSnapshotVersion);
            var legacy = d.RuleNum(K.Loadout, RK.LegacyProfileSnapshotVersion);
            if (!Js.IsNum(version) || (Js.D(version) != legacy && Js.D(version) != current))
                throw new InvalidOperationException(RunJs.Fmt(RM.ProfileSnapshotVersionUnknown, RunJs.Show(version)));
            if (!(snapshot[RK.Profiles] is JObject)) throw new InvalidOperationException(RM.ProfileSnapshotProfilesNotObject);
            snapshot = RunJs.Clone(snapshot);
            var patch = d.RuleList(K.Loadout, RK.ProfilePatchFields);
            var carrier = d.RuleList(K.Loadout, RK.ProfileCarrierFields);
            var legal = patch.Concat(carrier).Concat(new[] { RK.Compatibility }).ToList();
            var live = d.EquipmentRows(K.BasicCardProfiles).ToList();
            if (snapshot.Num(K.SnapshotVersion) == legacy)
            {
                var migrated = new JObject();
                foreach (var profile in live)
                {
                    var rule = ProfileRule(d, profile);
                    var saved = snapshot.Obj(RK.Profiles).Obj(profile.Str(K.Id)) ?? new JObject();
                    foreach (var key in legal)
                        if (saved[key] != null && key != K.RatingId) rule[key] = saved[key].DeepClone();
                    migrated[profile.Str(K.Id)] = rule;
                }
                snapshot = new JObject
                {
                    [K.SnapshotVersion] = Js.N(current),
                    [RK.Profiles] = migrated,
                    [RK.RarityBonuses] = snapshot[RK.RarityBonuses]?.DeepClone(),
                };
                if (snapshot[RK.RarityBonuses] == null) snapshot.Remove(RK.RarityBonuses);
            }
            var liveIds = new HashSet<string>(live.Select(p => p.Str(K.Id)), StringComparer.Ordinal);
            foreach (var p in snapshot.Obj(RK.Profiles).Properties())
                if (!liveIds.Contains(p.Name)) throw new InvalidOperationException(RunJs.Fmt(RM.ProfileSnapshotUnknown, p.Name));
            var ratingIds = RunJs.Strs(d.Combat.Engine.Obj(K.Ratings)?[K.Ids]);
            var schools = RunJs.Strs(d.Combat.Engine[K.DamageSchools]);
            foreach (var profile in live)
            {
                var id = profile.Str(K.Id);
                if (!(snapshot.Obj(RK.Profiles)[id] is JObject rule) || !Js.Truthy(rule)) throw new InvalidOperationException(RunJs.Fmt(RM.ProfileSnapshotMissing, id));
                if (!Js.IsFinite(rule[RK.BaseValue])) throw new InvalidOperationException(RunJs.Fmt(RM.ProfileBaseValueNotFinite, id));
                if (!ratingIds.Contains(rule.Str(K.RatingId))) throw new InvalidOperationException(RunJs.Fmt(RM.ProfileRatingUnknown, id, RunJs.Show(rule[K.RatingId])));
                if (!Js.Nullish(rule[RK.Cap]) && (!Js.IsFinite(rule[RK.Cap]) || rule.Num(RK.Cap) < 0)) throw new InvalidOperationException(RunJs.Fmt(RM.ProfileCapInvalid, id));
                var compatibility = profile.Str(K.Role) + d.RuleStr(K.Loadout, RK.CompatibilitySuffix);
                if (rule.Str(RK.Compatibility) != compatibility) throw new InvalidOperationException(RunJs.Fmt(RM.ProfileCompatibilityMismatch, id, RunJs.Show(rule[RK.Compatibility]), compatibility));
                if (rule[K.DamageSchool] == null) Cards.SetOrRemove(rule, K.DamageSchool, profile[K.DamageSchool]);
                if (rule[K.ExposureBuildupPerHit] == null) Cards.SetOrRemove(rule, K.ExposureBuildupPerHit, profile[K.ExposureBuildupPerHit]);
                if (!schools.Contains(rule.Str(K.DamageSchool))) throw new InvalidOperationException(RunJs.Fmt(RM.ProfileSchoolUnknown, id, RunJs.Show(rule[K.DamageSchool])));
                if (!Js.IsInt(rule[K.ExposureBuildupPerHit]) || rule.Num(K.ExposureBuildupPerHit) < 0) throw new InvalidOperationException(RunJs.Fmt(RM.ProfileBuildupInvalid, id));
                foreach (var key in rule.Properties()) if (!legal.Contains(key.Name)) throw new InvalidOperationException(RunJs.Fmt(RM.ProfileFieldUnknown, id, key.Name));
            }
            if (!(snapshot[RK.RarityBonuses] is JObject rarities)) throw new InvalidOperationException(RM.RarityBonusesNotObject);
            var roles = Roles(d);
            foreach (var r in rarities.Properties())
            {
                if (!(r.Value is JObject bonuses)) throw new InvalidOperationException(RunJs.Fmt(RM.RarityRowNotObject, r.Name));
                foreach (var b in bonuses.Properties())
                {
                    if (!roles.Contains(b.Name)) throw new InvalidOperationException(RunJs.Fmt(RM.RarityRoleUnknown, r.Name, b.Name));
                    if (!Js.IsFinite(b.Value)) throw new InvalidOperationException(RunJs.Fmt(RM.RarityValueNotFinite, r.Name, b.Name));
                }
            }
            return snapshot;
        }

        /// <summary>equipmentRoleSource: the first worn piece authoring a profile for the role, else the unarmed one.</summary>
        public static KitRow RoleSource(RunData d, JObject loadout, string classId, string role)
        {
            var eq = d.EquipmentBalance;
            foreach (var source in Js.Items(eq.Obj(RK.RoleSources)?[role]).OfType<JObject>())
            {
                var piece = Combat.Equipment.EquippedIn(d.Combat, loadout, classId, source.Str(K.Slot));
                if (piece == null) continue;
                if (Js.Truthy(source[K.Kinds]) && !Js.Includes(source[K.Kinds], piece.Str(K.Kind))) continue;
                var profileId = piece[role + RV.ProfileSuffix];
                if (Js.Truthy(profileId)) return new KitRow { Role = role, SlotId = source.Str(K.Slot), Piece = piece, Profile = ProfileById(d, profileId) };
            }
            return new KitRow { Role = role, SlotId = null, Piece = null, Profile = ProfileById(d, eq.Obj(RK.UnarmedProfiles)?[role]) };
        }

        /// <summary>equipmentKitPlan: one role source per equipment role.</summary>
        public static List<KitRow> KitPlan(RunData d, JObject loadout, string classId) =>
            Roles(d).Select(role => RoleSource(d, loadout, classId, role)).ToList();

        /// <summary>attributeRatingReceipt(config, attributes, id).value.</summary>
        private static JObject AttributeRatingReceipt(RunData d, JObject config, JObject attributes, string id)
        {
            var rule = config?.Obj(K.Ratings)?.Obj(id);
            if (rule == null) throw new InvalidOperationException(RunJs.Fmt(RM.MissingRatingFormula, id));
            double weighted = 0;
            foreach (var attr in RunJs.Strs(d.Combat.Engine.Obj(K.Ratings)?[K.Attributes]))
            {
                var value = Js.Or0(attributes?[attr]);
                weighted += Math.Floor(value * Js.D(rule[attr]) + CombatMath.Epsilon);
            }
            var multiplier = Js.Nullish(config[K.Multiplier]) ? 1 : config.Num(K.Multiplier);
            var attribute = Math.Floor(weighted * multiplier + CombatMath.Epsilon);
            return Js.Obj(K.Base, rule[K.Base]?.DeepClone(), K.Value, Js.D(rule[K.Base]) + attribute);
        }

        private static double EquipmentRatingBase(JObject piece, string id, JObject profile)
        {
            if (piece == null) return 0;
            if (id == K.Ar || id == K.Pr) return piece.Or0(K.AttackRating);
            if (id == K.Dr) return piece.Or0(K.DefenseRating);
            if (id == K.Poise && piece.Str(K.Kind) == V.Armor) return piece.Or0(K.PoiseThreshold);
            if (id == K.Ward && profile?.Str(K.RatingId) == K.Ward) return piece.Or0(K.DefenseRating);
            return 0;
        }

        /// <summary>effectiveEquipmentRating(config, attributes, piece, profile, id): the attribute part plus the item's own rating.</summary>
        public static JObject EffectiveEquipmentRating(RunData d, JObject config, JObject attributes, JObject piece, JObject profile, string id)
        {
            if (!RunJs.Strs(d.Combat.Engine.Obj(K.Ratings)?[K.Ids]).Contains(id)) throw new InvalidOperationException(RunJs.Fmt(RM.UnknownEquipmentRating, id));
            var attribute = AttributeRatingReceipt(d, config, attributes, id);
            string itemKey = null;
            if (piece != null)
                itemKey = piece.Str(K.Kind) == V.Armor
                    ? string.Join(V.KeySeparator, V.Armor, piece.Str(K.ClassId), piece.Str(K.Id))
                    : string.Join(V.KeySeparator, V.ArmamentRefPrefix, piece.Str(K.Id));
            var configured = itemKey != null ? config?.Obj(K.ItemRatings)?.Obj(itemKey)?[id] : null;
            var equipmentBase = Js.IsFinite(configured) ? Js.D(configured) : EquipmentRatingBase(piece, id, profile);
            return Js.Obj(K.Id, id, RK.AttributeBase, attribute[K.Base], RK.AttributeValue, attribute[K.Value], RK.EquipmentBase, equipmentBase,
                K.Value, attribute.Num(K.Value) + equipmentBase);
        }

        /// <summary>roleAmountReceipt: card base + source-equipment rating + rarity, capped by the profile rule.</summary>
        public static JObject RoleAmountReceipt(RunData d, KitRow row, JObject attributes, JObject snapshot)
        {
            var profile = row.Profile;
            var rule = snapshot?.Obj(RK.Profiles)?.Obj(profile?.Str(K.Id) ?? V.Undefined);
            if (rule == null) throw new InvalidOperationException(RunJs.Fmt(RM.ProfileSnapshotMissing, profile?.Str(K.Id)));
            var rarity = row.Piece == null ? Js.Null() : row.Piece[RK.Rarity];
            var rarityBonus = Js.Or0((snapshot.Obj(RK.RarityBonuses)?.Obj(RunJs.Key(rarity)))?[row.Role]);
            var ratingConfig = d.Balance.Obj(K.CombatRatings) is JObject cr && Js.Truthy(cr) ? cr : d.RuleObj(K.Ratings, RK.DefaultFormula);
            var rating = EffectiveEquipmentRating(d, ratingConfig, attributes, row.Piece, rule, rule.Str(K.RatingId));
            rating[RK.SourceLabel] = row.Piece != null ? (row.Piece.Str(K.Kind) == RV.ShieldKind ? RV.ShieldKind : RV.WeaponLabel) : V.AttributeSource;
            var uncapped = rule.Num(RK.BaseValue) + rarityBonus;
            var capped = Js.IsFinite(rule[RK.Cap]);
            var effectBase = capped ? Math.Min(rule.Num(RK.Cap), uncapped) : uncapped;
            var raw = uncapped + rating.Num(K.Value);
            var value = capped ? Math.Min(rule.Num(RK.Cap), raw) : raw;
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) throw new InvalidOperationException(RunJs.Fmt(RM.ProfileValueInvalid, profile.Str(K.Id), RunJs.NumStr(value)));
            return Js.Obj(K.Role, row.Role, K.ProfileId, profile[K.Id], K.PieceId, row.Piece == null ? Js.Null() : row.Piece[K.Id],
                K.Base, rule[RK.BaseValue], RK.Rarity, rarity, RK.RarityBonus, rarityBonus, RK.Rating, rating, RK.EffectBase, effectBase,
                RK.Raw, raw, RK.Cap, rule[RK.Cap], K.Value, value);
        }

        /// <summary>equipmentKitReceipt: the kit plan, each row priced from the (validated) run snapshot.</summary>
        public static List<KitRow> KitReceipt(RunData d, JObject loadout, string classId, JObject attributes, JObject profileSnapshot)
        {
            var snapshot = RestoreProfileSnapshot(d, profileSnapshot);
            var rows = KitPlan(d, loadout, classId);
            foreach (var row in rows) row.Receipt = RoleAmountReceipt(d, row, attributes, snapshot);
            return rows;
        }

        private static string CardForTarget(RunData d, string target, string classId)
        {
            var rows = d.EquipmentRows(RK.Targets).ToList();
            var exact = rows.FirstOrDefault(t => t.Str(K.Target) == target && t.Str(K.ClassId) == classId);
            if (exact != null) return exact.Str(K.CardId);
            var any = rows.FirstOrDefault(t => t.Str(K.Target) == target && t.Str(K.ClassId) == RV.AnyClass);
            return any?.Str(K.CardId);
        }

        /// <summary>
        /// cardMods(registries, loadout, classId, { attackSourceWeaponId }): card id → mod strings, in slot order.
        /// <paramref name="hasAttackSource"/> false is the shipped <c>undefined</c> (no weapon filter).
        /// </summary>
        public static OrderedMap<List<string>> CardMods(RunData d, JObject loadout, string classId, bool hasAttackSource = false, string attackSourceWeaponId = null)
        {
            var fields = d.Equipment.Obj(K.ModFields) ?? new JObject();
            var result = new OrderedMap<List<string>>();
            foreach (var piece in Combat.Equipment.EquippedPieces(d.Combat, loadout, classId, new JObject()))
            {
                var package = WeaponCards.FromPiece(d, piece);
                if (hasAttackSource && package != null && piece.Str(K.Id) != attackSourceWeaponId) continue;
                foreach (var raw in Js.Items(piece[K.Mods]))
                {
                    var mod = ParseMod(raw);
                    var spec = mod != null ? fields.Obj(mod.Field) : null;
                    if (spec == null || spec.Str(RK.Scope) != RV.CardScope) continue;
                    var cardId = CardForTarget(d, mod.Prefix, classId);
                    if (string.IsNullOrEmpty(cardId)) continue;
                    if (!result.TryGetValue(cardId, out var list)) list = new List<string>();
                    list.Add(mod.Field + RV.ModAssign + (mod.Add ? (mod.Value >= 0 ? RV.Plus : string.Empty) : string.Empty) + RunJs.NumStr(mod.Value));
                    result[cardId] = list;
                }
            }
            return result;
        }

        /// <summary>runMods(registries, loadout, classId): pool bonuses, the swap-cost delta and start statuses.</summary>
        public static RunModsResult RunMods(RunData d, JObject loadout, string classId)
        {
            var fields = d.Equipment.Obj(K.ModFields) ?? new JObject();
            var poolFields = PoolFields(d);
            var pools = poolFields.ToDictionary(f => f, f => 0d);
            var stacks = new OrderedMap<double>();
            double swapCostDelta = 0;
            foreach (var piece in Combat.Equipment.EquippedPieces(d.Combat, loadout, classId, new JObject()))
                foreach (var raw in Js.Items(piece[K.Mods]))
                {
                    var mod = ParseMod(raw);
                    var spec = mod != null ? fields.Obj(mod.Field) : null;
                    if (spec == null || spec.Str(RK.Scope) != RV.RunScope) continue;
                    var apply = spec.Str(K.Apply);
                    if (apply != null && poolFields.Contains(apply)) pools[apply] = mod.Add ? pools[apply] + mod.Value : mod.Value;
                    else if (apply == RV.SwapCostApply) swapCostDelta = mod.Add ? swapCostDelta + mod.Value : mod.Value;
                    else if (apply == RV.StartStatusApply)
                    {
                        var status = spec.Str(K.Status) ?? V.Undefined;
                        var prev = stacks.TryGetValue(status, out var s) ? s : 0;
                        stacks[status] = mod.Add ? prev + mod.Value : mod.Value;
                    }
                }
            var result = new RunModsResult { SwapCostDelta = swapCostDelta };
            foreach (var f in poolFields) result.Pools.Add(new KeyValuePair<string, double>(f, pools[f]));
            foreach (var e in stacks.Entries())
                if (e.Value > 0) result.StartStatuses.Add(Js.Obj(K.Status, e.Key, K.Stacks, e.Value));
            return result;
        }

        /// <summary>
        /// reconcileRunLoadoutHp: re-derive every pool from the run's snapshot, persisted equipment contribution and HP
        /// adjustment, carrying each absolute deficit. Returns false when the run lacks what it needs (the shipped null).
        /// </summary>
        public static bool ReconcileRunLoadoutHp(RunData d, JObject run, bool adoptEquipmentBonuses)
        {
            var poolFields = PoolFields(d);
            var current = d.RuleObj(K.Loadout, RK.PoolCurrent);
            if (run == null || !Js.Truthy(run[K.DerivedStatRuleSnapshot]) || !Js.Truthy(run.Obj(K.DerivedStatRuleSnapshot)?[K.Rules])
                || !poolFields.All(f => Js.IsFinite(run[f]) && Js.IsFinite(run[current.Str(f) ?? V.Undefined]))) return false;
            if (!Js.IsInt(run[RK.MaxHpAdjustment])) throw new InvalidOperationException(RM.ReconcileNeedsAdjustment);
            var classDef = d.Classes.Get(run.Str(RK.Class));
            var live = RunMods(d, run.Obj(K.Loadout), run.Str(RK.Class));
            var liveBonuses = new JObject();
            foreach (var f in poolFields) liveBonuses.Put(f, live.Pool(f));
            var prior = run.Obj(RK.EquipmentPoolBonuses) ?? liveBonuses;
            var bonuses = adoptEquipmentBonuses ? liveBonuses : prior;
            var deficits = new JObject();
            var level = RunState.CharacterLevelOf(run);
            var rules = run.Obj(K.DerivedStatRuleSnapshot).Obj(K.Rules);
            var attributes = run.Obj(K.Attributes);
            var hpField = poolFields[0];
            var derived = DerivedStats.Derive(rules, RV.StatHp, attributes, classDef, level);
            var nextMax = Math.Max(1, derived.Value + bonuses.Num(hpField) + run.Num(RK.MaxHpAdjustment));
            deficits.Put(current.Str(hpField), MoveEquipmentPool(d, run, hpField, nextMax, run.Obj(K.EquipmentPoolDeficits)?[current.Str(hpField)]));
            foreach (var f in poolFields.Skip(1))
            {
                var statId = current.Str(f);
                var poolDerived = DerivedStats.Derive(rules, statId, attributes, classDef, level);
                var poolMax = Math.Max(0, poolDerived.Value + bonuses.Num(f));
                deficits.Put(statId, MoveEquipmentPool(d, run, f, poolMax, run.Obj(K.EquipmentPoolDeficits)?[statId]));
            }
            run[RK.EquipmentPoolBonuses] = Js.Spread(bonuses);
            var ordered = new JObject();
            foreach (var f in poolFields) ordered[current.Str(f)] = deficits[current.Str(f)].DeepClone();
            run[K.EquipmentPoolDeficits] = ordered;
            return true;
        }

        /// <summary>cumulativeRequirementDelta: the requirement change an armament's smithing tiers add up to.</summary>
        private static double RequirementDelta(RunData d, string itemRef, string attributeId, double level)
        {
            double delta = 0;
            var tag = RV.RequirementTagPrefix + V.TagSeparator + attributeId;
            for (var tier = 1; tier <= level; tier += 1)
                foreach (var row in d.EquipmentRows(K.ItemUpgradeChanges))
                    if (row.Str(K.ItemRef) == itemRef && row.Num(K.NextTier) == tier && row.Str(K.Tag) == tag) delta += row.Num(K.Value);
            return delta;
        }

        /// <summary>equipmentRequirementReceipt(...).failures: the attribute minimums a piece asks for that are unmet.</summary>
        public static List<JObject> RequirementFailures(RunData d, JObject piece, JObject attributes, JObject itemUpgradeLevels = null)
        {
            if (piece == null) throw new InvalidOperationException(RM.RequirementNeedsPiece);
            var authored = piece.Obj(RK.Requirements)?.Obj(K.Attributes) ?? new JObject();
            var failures = new List<JObject>();
            var itemRef = Combat.Equipment.PieceItemRef(piece);
            var namespaced = itemUpgradeLevels?[itemRef ?? V.Undefined];
            var level = Js.IsInt(namespaced) ? Js.D(namespaced) : 0;
            foreach (var p in authored.Properties())
            {
                if (!d.Attributes.Has(p.Name)) throw new InvalidOperationException(RunJs.Fmt(RM.RequirementUnknownAttribute, piece.Str(K.Id), p.Name));
                if (!Js.IsInt(p.Value) || Js.D(p.Value) < 0) throw new InvalidOperationException(RunJs.Fmt(RM.RequirementNotInteger, piece.Str(K.Id), p.Name));
                var delta = RequirementDelta(d, itemRef, p.Name, level);
                var required = Math.Max(0, Js.D(p.Value) + delta);
                var actual = attributes?[p.Name];
                var row = Js.Obj(RK.AttributeId, p.Name, RK.BaseRequired, p.Value, RK.Reduction, -delta, RK.Required, required,
                    RK.Actual, Js.IsFinite(actual) ? actual : Js.Null());
                if (!Js.IsFinite(actual) || Js.D(actual) < required) failures.Add(row);
            }
            return failures;
        }
    }
}
