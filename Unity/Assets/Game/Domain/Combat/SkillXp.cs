using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using E = Ashen.Generated.CombatEvents;
using K = Ashen.Generated.CombatKeys;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// The skill tracks' XP hooks (shipped engine/skillXp.js, plan phase 4a): one listener on the event bus that
    /// pays balance.skill.xp's rows into a receipt on the combat (combat.skillXp[ownerKey] = { xp, killGroup }).
    /// The run is paid once, when the fight is over, from <see cref="Receipt"/>. Track vocabulary (the focus item
    /// type, the armour-track prefix, the weight-class shares) is data in rules/combatEngine.json skillXp.
    /// </summary>
    public static class SkillXp
    {
        private static JObject Rules(CombatState c) => c.Data.Engine.Obj(K.SkillXp) ?? new JObject();

        private static string OwnerKeyOf(string entityId) => entityId == V.Player ? V.Player : null;

        private static JObject ReceiptFor(CombatState c, string ownerKey)
        {
            if (!(c.SkillXp[ownerKey] is JObject receipt))
            {
                receipt = new JObject { [K.Xp] = new JObject(), [K.KillGroup] = Js.Null() };
                c.SkillXp[ownerKey] = receipt;
                receipt = c.SkillXp.Obj(ownerKey);
            }
            return receipt;
        }

        /// <summary>favoredMult: the product of mounted skillXpMult passives whose carrier's own tags name the track.</summary>
        private static double FavoredMult(CombatState c, string ownerKey, string skillId)
        {
            double m = 1;
            if (c.PropertyMounts == null || !c.PropertyMounts.TryGetValue(ownerKey, out var mounts)) return m;
            foreach (var key in mounts.SortedKeys())
            {
                var mount = mounts[key];
                if (mount.ScopeTags == null || !mount.ScopeTags.Contains(skillId)) continue;
                foreach (var rule in mount.Rules)
                {
                    var v = rule.Obj(K.Passives)?[K.SkillXpMult];
                    if (Js.IsNum(v)) m *= Js.D(v);
                }
            }
            return m;
        }

        private static void Pay(JObject receipt, string skillId, double amount, double mult)
        {
            if (string.IsNullOrEmpty(skillId) || !(amount > 0)) return;
            var xp = receipt.Obj(K.Xp);
            xp.Put(skillId, xp.Or0(skillId) + amount * mult);
        }

        private static string GroupOfPiece(CombatState c, JObject piece)
        {
            if (piece == null) return null;
            var armor = Rules(c).Str(K.ArmorItemType);
            return Js.Items(piece[K.ItemTypeTags]).Select(Js.Str).FirstOrDefault(tag => tag != armor);
        }

        private static JObject PieceInHand(CombatState c, string hand) =>
            Equipment.PieceInHand(c.Data, c.Loadout, c.Player?.Str(K.ClassId), hand);

        /// <summary>The group a card belongs to: the piece that lent it (by hand, else by its grantedBy item ref).</summary>
        private static string GroupOfCard(CombatState c, JObject ev)
        {
            var hand = ev.Str(K.SourceHand);
            if (hand == V.Right || hand == V.Left) return GroupOfPiece(c, PieceInHand(c, hand));
            var by = ev.Str(K.GrantedBy);
            if (string.IsNullOrEmpty(by) || by.StartsWith(Rules(c).Str(K.UnarmedGrantPrefix) ?? V.Undefined, StringComparison.Ordinal)) return null;
            var itemRef = by.Contains(V.ItemRefSeparator) ? by : V.ArmamentRefPrefix + V.ItemRefSeparator + by;
            var prefix = V.ArmamentRefPrefix + V.ItemRefSeparator;
            if (!itemRef.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var id = itemRef.Substring(prefix.Length);
            return GroupOfPiece(c, Js.Items(c.Data.Equipment[K.Armaments]).OfType<JObject>().FirstOrDefault(a => a.Str(K.Id) == id));
        }

        private static bool IsDual(CombatState c) => c.Loadout != null && Equipment.GripMode(c.Data, c.Loadout, c.Player?.Str(K.ClassId)) == V.GripDual;

        private static string WeightClassId(CombatState c)
        {
            try
            {
                return Equipment.PlayerWeightClass(c).WeightClass?.Str(K.Id);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ArmourSkill(CombatState c, string weightClass) => Rules(c).Str(K.ArmourSkillPrefix) + weightClass;

        /// <summary>recordSkillXp(combat, event): called by the bus after every event the engine emits.</summary>
        public static void Record(CombatState c, JObject ev)
        {
            var rows = c.Data.Balance.Obj(K.Skill)?.Obj(K.Xp);
            if (rows == null || c.Player == null) return;
            var rules = Rules(c);
            var dual = rules.Str(K.DualWieldSkill);
            switch (ev.Str(K.Type))
            {
                case E.DamageDealt:
                {
                    var owner = OwnerKeyOf(ev.Str(K.SourceId));
                    if (owner == null || ev.Str(K.TargetId) == V.Player || !(ev.Num(K.Amount) > 0)) return;
                    var group = GroupOfCard(c, ev);
                    if (group == null) return;
                    var receipt = ReceiptFor(c, owner);
                    Pay(receipt, group, rows.Num(K.PerHit), FavoredMult(c, owner, group));
                    if (IsDual(c)) Pay(receipt, dual, rows.Num(K.PerHit), FavoredMult(c, owner, dual));
                    var target = c.Enemies.FirstOrDefault(e => e.Str(K.Id) == ev.Str(K.TargetId));
                    if (target != null && (target.Num(K.Hp) <= 0 || !target.Is(K.Alive))) receipt[K.KillGroup] = group;
                    return;
                }
                case E.BlockGained:
                {
                    if (OwnerKeyOf(ev.Str(K.TargetId)) == null || !(ev.Num(K.Amount) > 0)) return;
                    var owner = V.Player;
                    var group = GroupOfCard(c, ev);
                    if (group == null) return;
                    var receipt = ReceiptFor(c, owner);
                    Pay(receipt, group, rows.Num(K.PerHit), FavoredMult(c, owner, group));
                    if (IsDual(c)) Pay(receipt, dual, rows.Num(K.PerHit), FavoredMult(c, owner, dual));
                    return;
                }
                case E.ImpactDealt:
                {
                    var owner = OwnerKeyOf(ev.Str(K.TargetId));
                    if (owner == null || !(ev.Num(K.Amount) > 0)) return;
                    var wc = WeightClassId(c);
                    var share = wc == null ? null : rules.Obj(K.ImpactShare)?[wc];
                    if (Js.IsNum(share)) Pay(ReceiptFor(c, owner), ArmourSkill(c, wc), ev.Num(K.Amount) / rows.Num(K.ImpactPerXp) * Js.D(share), FavoredMult(c, owner, ArmourSkill(c, wc)));
                    return;
                }
                case E.AttackEvaded:
                {
                    var owner = OwnerKeyOf(ev.Str(K.TargetId));
                    if (owner == null) return;
                    var wc = WeightClassId(c);
                    var share = wc == null ? null : rules.Obj(K.EvadeShare)?[wc];
                    if (Js.IsNum(share)) Pay(ReceiptFor(c, owner), ArmourSkill(c, wc), rows.Num(K.EvadeXp) * Js.D(share), FavoredMult(c, owner, ArmourSkill(c, wc)));
                    return;
                }
                case E.ArcaneExposureChanged:
                {
                    var owner = OwnerKeyOf(ev.Str(K.SourceId));
                    if (owner == null || !(ev.Num(K.Amount) > 0)) return;
                    var focus = rules.Str(K.FocusItemType);
                    Pay(ReceiptFor(c, owner), focus, ev.Num(K.Amount) / rows.Num(K.BuildupPerXp), FavoredMult(c, owner, focus));
                    return;
                }
                case E.CombatEnd:
                {
                    if (!ev.Is(K.Victory)) return;
                    var owner = V.Player;
                    var receipt = ReceiptFor(c, owner);
                    var groups = new[] { V.Right, V.Left }.Select(hand => GroupOfPiece(c, PieceInHand(c, hand))).Where(g => g != null).Distinct().ToList();
                    foreach (var group in groups)
                        Pay(receipt, group, rows.Num(K.PerWinEquipped) * (receipt.Str(K.KillGroup) == group ? rows.Num(K.KillMult) : 1), FavoredMult(c, owner, group));
                    if (IsDual(c)) Pay(receipt, dual, rows.Num(K.PerWinEquipped), FavoredMult(c, owner, dual));
                    return;
                }
            }
        }

        /// <summary>skillXpReceipt(combat): whole XP per track (fractional accumulations floored once, here).</summary>
        public static JObject Receipt(CombatState c, string ownerKey = null)
        {
            var result = new JObject();
            var xp = c.SkillXp.Obj(ownerKey ?? V.Player)?.Obj(K.Xp);
            if (xp == null) return result;
            foreach (var p in xp.Properties())
            {
                var whole = Math.Floor(Js.D(p.Value));
                if (whole > 0) result.Put(p.Name, whole);
            }
            return result;
        }
    }
}
