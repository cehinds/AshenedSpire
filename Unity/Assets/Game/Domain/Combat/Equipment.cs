using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>The weight class a player stands in (framework computeWeightClass).</summary>
    public sealed class WeightClassReceipt
    {
        public double Capacity;
        public double Load;
        public double Percent;
        public JObject WeightClass;
    }

    /// <summary>
    /// The equipment reads combat needs (shipped model/loadout.js equippedIn/equippedPieces/gripOf/gripTags/
    /// pieceItemRef, model/itemUpgrades.js, model/statProjection.js playerLoadReceipt, model/combatWeight.js and the
    /// framework weight/dodge/stamina rules from rules/mechanics.json).
    /// </summary>
    public static class Equipment
    {
        // ------------------------------------------------------------------ item upgrades

        private static readonly Regex ArmamentRef = new Regex(Ashen.Generated.CombatPatterns.ArmamentRef, RegexOptions.CultureInvariant);
        private static readonly Regex ArmorRef = new Regex(Ashen.Generated.CombatPatterns.ArmorRef, RegexOptions.CultureInvariant);
        private static readonly Regex RelicRef = new Regex(Ashen.Generated.CombatPatterns.RelicRef, RegexOptions.CultureInvariant);

        private static IEnumerable<JObject> CumulativeRows(CombatData data, string itemRef, double level)
        {
            for (var tier = 1; tier <= level; tier += 1)
                foreach (var row in Js.Items(data.Equipment[K.ItemUpgradeChanges]).OfType<JObject>())
                    if (row.Str(K.ItemRef) == itemRef && row.Num(K.NextTier) == tier) yield return row;
        }

        /// <summary>resolveUpgradedEquipment: an armament or armour piece at a smithing tier.</summary>
        public static JObject ResolveUpgradedEquipment(CombatData data, string itemRef, double level)
        {
            JObject baseDef;
            var armament = ArmamentRef.Match(itemRef ?? string.Empty);
            var armor = ArmorRef.Match(itemRef ?? string.Empty);
            if (armament.Success)
                baseDef = Js.Items(data.Equipment[K.Armaments]).OfType<JObject>().FirstOrDefault(r => r.Str(K.Id) == armament.Groups[V.GroupId].Value);
            else if (armor.Success)
                baseDef = Js.Items(data.Equipment[K.Armour]).OfType<JObject>().FirstOrDefault(r => r.Str(K.ClassId) == armor.Groups[V.GroupClassId].Value && r.Str(K.Id) == armor.Groups[V.GroupId].Value);
            else throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.NotEquipmentRef, itemRef));
            if (baseDef == null) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UnknownUpgradeItem, itemRef));
            if (level <= 0) return baseDef;
            var poiseTag = data.Engine.Obj(K.ItemUpgrades)?.Str(K.EquipmentPoiseTag);
            var poise = baseDef[K.PoiseThreshold];
            foreach (var row in CumulativeRows(data, itemRef, level))
            {
                if (row.Str(K.Tag) != poiseTag) continue;
                if (!Js.IsInt(poise)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.PoiseThresholdNotInteger, itemRef));
                poise = Js.N(Js.D(poise) + row.Num(K.Value));
            }
            var result = Js.Spread(baseDef);
            Cards.SetOrRemove(result, K.PoiseThreshold, poise);
            return result;
        }

        /// <summary>resolveUpgradedRelic: a relic's passives at a smithing tier.</summary>
        public static JObject ResolveUpgradedRelic(CombatData data, string itemRef, double level)
        {
            var relic = RelicRef.Match(itemRef ?? string.Empty);
            if (!relic.Success) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.NotRelicRef, itemRef));
            var relicId = relic.Groups[V.GroupId].Value;
            if (!data.Relics.Has(relicId)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UnknownUpgradeItem, itemRef));
            var baseDef = data.Relics.Get(relicId);
            if (level <= 0) return baseDef;
            var tags = data.Engine.Obj(K.ItemUpgrades)?.Obj(K.RelicPassiveTags) ?? new JObject();
            var passives = Js.Spread(baseDef.Obj(K.Passives));
            foreach (var row in CumulativeRows(data, itemRef, level))
            {
                var passiveKey = tags.Str(row.Str(K.Tag) ?? string.Empty);
                if (passiveKey == null) continue;
                if (!Js.IsInt(passives[passiveKey])) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.PassiveNotInteger, itemRef, passiveKey));
                passives.Put(passiveKey, passives.Num(passiveKey) + row.Num(K.Value));
            }
            var result = Js.Spread(baseDef);
            result[K.Passives] = passives;
            return result;
        }

        // ------------------------------------------------------------------ loadout

        /// <summary>The piece in a slot's active set, or null. Armour resolves per class.</summary>
        public static JObject EquippedIn(CombatData data, JObject loadout, string classId, string slotId)
        {
            var ids = loadout?.Obj(K.Sets)?.Arr(slotId);
            if (ids == null) return null;
            var index = (int)Js.Or0(loadout.Obj(K.Active)?[slotId]);
            var id = index >= 0 && index < ids.Count ? Js.Str(ids[index]) : null;
            if (string.IsNullOrEmpty(id)) return null;
            var slot = Js.Items(data.Equipment[K.Slots]).OfType<JObject>().FirstOrDefault(s => s.Str(K.Id) == slotId);
            if (slot != null && Js.Includes(slot[K.Kinds], V.Armor))
                return Js.Items(data.Equipment[K.Armour]).OfType<JObject>().FirstOrDefault(o => o.Str(K.ClassId) == classId && o.Str(K.Id) == id);
            return Js.Items(data.Equipment[K.Armaments]).OfType<JObject>().FirstOrDefault(a => a.Str(K.Id) == id);
        }

        /// <summary>Every currently-worn piece, in slot order (armour at its smithing tier).</summary>
        public static List<JObject> EquippedPieces(CombatData data, JObject loadout, string classId, JObject itemUpgradeLevels)
        {
            var result = new List<JObject>();
            if (loadout == null) return result;
            foreach (var slot in Js.Items(data.Equipment[K.Slots]).OfType<JObject>())
            {
                var piece = EquippedIn(data, loadout, classId, slot.Str(K.Id));
                if (piece == null) continue;
                if (piece.Str(K.Kind) == V.Armor)
                {
                    var itemRef = PieceItemRef(piece);
                    result.Add(ResolveUpgradedEquipment(data, itemRef, Js.Or0(itemUpgradeLevels?[itemRef])));
                }
                else result.Add(piece);
            }
            return result;
        }

        /// <summary>The namespaced item ref of a piece: armament/&lt;id&gt; or armor/&lt;class&gt;/&lt;id&gt;.</summary>
        public static string PieceItemRef(JObject piece)
        {
            if (piece == null || string.IsNullOrEmpty(piece.Str(K.Id))) return null;
            return piece.Str(K.Kind) == V.Armor
                ? string.Join(V.ItemRefSeparator, V.ArmorRefPrefix, piece.Str(K.ClassId), piece.Str(K.Id))
                : string.Join(V.ItemRefSeparator, V.ArmamentRefPrefix, piece.Str(K.Id));
        }

        private static JObject HandPiece(CombatData data, JObject loadout, string classId, string hand, out bool twoHanded)
        {
            twoHanded = false;
            var slot = Js.Items(data.Equipment[K.Slots]).OfType<JObject>().FirstOrDefault(s => s.Str(K.Hand) == hand);
            if (slot == null || loadout == null) return null;
            var piece = EquippedIn(data, loadout, classId, slot.Str(K.Id));
            if (piece == null) return null;
            var package = piece.Obj(K.WeaponCardPackage);
            if (package == null && !piece.Is(K.AttackProfile)) return piece;
            var hands = !Js.Nullish(package?[K.HandsRequired]) ? package.Num(K.HandsRequired) : Js.Coalesce(piece[K.HandsRequired], 1);
            twoHanded = hands == Ashen.Generated.CombatMath.Two;
            return piece;
        }

        /// <summary>gripOf → its derived framework tags (equipment.twoHanded / equipment.dualWield), read at play time.</summary>
        public static JArray GripTags(CombatData data, JObject loadout, string classId)
        {
            var right = HandPiece(data, loadout, classId, V.Right, out var rightTwo);
            var left = HandPiece(data, loadout, classId, V.Left, out var leftTwo);
            var mode = V.GripOne;
            if (rightTwo || leftTwo) mode = V.GripTwo;
            else if (right != null && left != null)
            {
                var leftTags = left[K.ItemTypeTags];
                if (Js.Items(right[K.ItemTypeTags]).Any(t => Js.Includes(leftTags, Js.Str(t)))) mode = V.GripDual;
            }
            return (JArray)(data.Engine.Obj(K.Grip)?[mode] ?? new JArray()).DeepClone();
        }

        // ------------------------------------------------------------------ weight class (framework)

        public static WeightClassReceipt ComputeWeightClass(CombatData data, double constitution, double strength, double bonuses, double load)
        {
            var w = data.Mechanics.Obj(K.Weight);
            var capacity = w.Num(K.CapacityBase) + w.Num(K.CapacityPerConstitution) * constitution + w.Num(K.CapacityPerStrength) * strength + bonuses;
            var percent = Math.Floor(Ashen.Generated.CombatMath.Percent * load / Math.Max(1, capacity));
            var classes = w.Arr(K.Classes);
            JObject cls = null;
            foreach (var c in classes.OfType<JObject>())
                if (percent <= c.Num(K.MaxLoadPercent)) { cls = c; break; }
            cls ??= (JObject)classes[classes.Count - 1];
            return new WeightClassReceipt { Capacity = capacity, Load = load, Percent = percent, WeightClass = cls };
        }

        /// <summary>pieceWeight: armour weighs its poise threshold, an armament its authored integer weight.</summary>
        public static double PieceWeight(JObject piece)
        {
            if (piece == null) return 0;
            if (piece.Str(K.Kind) == V.Armor) return piece.Or0(K.PoiseThreshold);
            return Js.IsInt(piece[K.Weight]) ? piece.Num(K.Weight) : 0;
        }

        /// <summary>playerWeightClass(combat): the class the player's worn load puts them in.</summary>
        public static WeightClassReceipt PlayerWeightClass(CombatState c)
        {
            if (c.Loadout != null && c.Attributes != null)
            {
                double hands = 0, armour = 0;
                foreach (var piece in EquippedPieces(c.Data, c.Loadout, c.Player.Str(K.ClassId), c.ItemUpgradeLevels ?? new JObject()))
                {
                    if (piece.Str(K.Kind) == V.Armor && !Js.Nullish(piece[K.ClassId])) armour += PieceWeight(piece);
                    else hands += PieceWeight(piece);
                }
                return ComputeWeightClass(c.Data, c.Attributes.Num(K.Constitution), c.Attributes.Num(K.Strength), 0, hands + armour);
            }
            var fallback = c.Data.Rule(K.Defaults, K.WeightAttribute);
            var con = c.Attributes != null && c.Attributes[K.Constitution] != null ? c.Attributes.Num(K.Constitution) : fallback;
            var str = c.Attributes != null && c.Attributes[K.Strength] != null ? c.Attributes.Num(K.Strength) : fallback;
            return ComputeWeightClass(c.Data, con, str, 0, 0);
        }

        /// <summary>The framework attribute modifier floor((score - baseline) / divisor).</summary>
        public static double AttributeModifier(CombatData data, double score) =>
            Math.Floor((score - data.Rule(K.Dodge, K.AttributeBaseline)) / data.Rule(K.Dodge, K.AttributeDivisor));

        /// <summary>dodgeRollCheck: d20 + DEX modifier + weight-class evasion against the base difficulty.</summary>
        public static (double Check, double Difficulty, bool Success, double TemporaryGuard) DodgeRoll(CombatData data, double roll, double dexterity, JObject weightClass)
        {
            var d = data.Mechanics.Obj(K.DodgeRoll);
            if (Math.Floor(roll) != roll || roll < 1 || roll > d.Num(K.Die)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.BadDodgeRoll, roll));
            var mod = AttributeModifier(data, dexterity);
            var check = roll + mod + weightClass.Num(K.EvasionModifier);
            var difficulty = d.Num(K.BaseDifficulty);
            var success = check > difficulty;
            var guard = success ? d.Num(K.TemporaryGuardBase) + mod + weightClass.Num(K.TemporaryGuardModifier) : 0;
            return (check, difficulty, success, guard);
        }

        /// <summary>onTurnEndStamina: an idle turn recovers mechanics.stamina.idleRecoveryPerTurn, to the maximum.</summary>
        public static double StaminaTurnEnd(CombatData data, double current, double max, double spent)
        {
            var idle = spent == 0;
            return idle ? Math.Min(max, current + data.Mechanics.Obj(K.Stamina).Num(K.IdleRecoveryPerTurn)) : current;
        }
    }
}
